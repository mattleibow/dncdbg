// Experimental private DBI startup. MIT adaptation attribution: THIRD-PARTY-NOTICES.md.
#include "CorDebugAbi.h"
#include "Wire.h"
#include <atomic>
#include <cerrno>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <cstring>
#include <deque>
#include <dlfcn.h>
#include <fcntl.h>
#include <limits.h>
#include <mutex>
#include <pthread.h>
#include <string>
#include <unordered_map>
#include <unistd.h>
#ifdef __APPLE__
#include <TargetConditionals.h>
#elif defined(__ANDROID__)
#include <sys/system_properties.h>
#endif

#define EXPORT extern "C" __attribute__((visibility("default")))
using namespace dncdbg;
namespace {
using DllMainFunction = Bool (*)(void*, uint32_t, void*);
using FactoryFunction = HResult (*)(int, uint32_t, const char16_t*, const char16_t*, void*, CorDebug**);
constexpr HResult Fail = static_cast<HResult>(0x80004005);
std::atomic<int> state{0};
std::atomic<bool> ownerFinished{false};
std::atomic<bool> cancel{false};
std::mutex queueMutex, reportMutex;
std::condition_variable queueReady;
enum class CallbackKind { Other, Breakpoint, Module, Domain };
struct Callback { Controller* controller; Unknown* object; CallbackKind kind; };
std::deque<Callback> callbacks;
Controller* callbackProcess = nullptr;
std::atomic<bool> exited{false};
std::atomic<int> callbackCount{0};
int reportFd = -1;
void* dbi = nullptr;
void* runtimeBase = nullptr;
DllMainFunction dllMain = nullptr;
FactoryFunction factory = nullptr;
std::string runtimePath, dbiPath, dacPath;
int listener = -1;
std::string sessionToken, capabilities;
std::atomic<int64_t> heartbeat{0};
CorDebug* debug = nullptr;
Controller* process = nullptr;
bool ownsInitialize = false;
Json location, breakpoints;
Breakpoint* functionBreakpoint = nullptr;
Controller* stoppedController = nullptr;
Unknown* stoppedThread = nullptr;
int64_t stopId = 0, nextId = 0;
bool stopReported = false;
bool cleaning = false;
std::unordered_map<int64_t, Unknown*> frames;
constexpr Guid ILFrameId{0x03e26311, 0x4f76, 0x11d3, {0x88, 0xc6, 0, 0x60, 0x97, 0x94, 0x54, 0x18}};
constexpr Guid GenericValueId{0xcc7bcaf8, 0x8a68, 0x11d2, {0x98, 0x3c, 0, 0, 0xf8, 8, 0x34, 0x2d}};
constexpr Guid MetadataId{0x7dac8207, 0xd3ae, 0x4c75, {0x9b, 0x67, 0x92, 0x80, 0x1a, 0x49, 0x7d, 0x44}};
int64_t Milliseconds() { return std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now().time_since_epoch()).count(); }
int64_t NewId() { return (int64_t{1} << 32) | ++nextId; }

void Report(const char* phase, HResult result = 0) {
    std::lock_guard<std::mutex> guard(reportMutex);
    if (reportFd < 0) return;
    char text[256];
    int size = std::snprintf(text, sizeof(text), "{\"Phase\":\"%s\",\"HResult\":\"0x%08x\",\"Callbacks\":%d}\n",
        phase, static_cast<uint32_t>(result), callbackCount.load());
    if (size > 0 && size < static_cast<int>(sizeof(text))) {
        ssize_t written = write(reportFd, text, static_cast<size_t>(size));
        if (written != size) state.store(-1);
    }
}

bool PlatformAllowed() {
#if defined(__aarch64__) && defined(__APPLE__) && TARGET_OS_SIMULATOR && TARGET_OS_IOS
    return true;
#elif defined(__aarch64__) && defined(__ANDROID__)
    char value[PROP_VALUE_MAX]{};
    __system_property_get("ro.kernel.qemu", value);
    if (std::strcmp(value, "1") == 0) return true;
    __system_property_get("ro.boot.qemu", value);
    return std::strcmp(value, "1") == 0;
#else
    return false;
#endif
}

HResult Enqueue(Controller* controller, Unknown* object = nullptr, CallbackKind kind = CallbackKind::Other) {
    std::lock_guard<std::mutex> guard(queueMutex);
    if (!controller) controller = callbackProcess;
    if (!controller || callbacks.size() >= 256) {
        Report("callback-queue-invalid", Fail);
        state.store(-1);
        return Fail;
    }
    controller->AddRef();
    if (object) object->AddRef();
    callbacks.push_back({controller, object, kind});
    callbackCount.fetch_add(1);
    queueReady.notify_one();
    return 0;
}

// Callback delivery only transfers a reference/stop. The native owner is the only
// thread that invokes Continue, Detach, or other execution-control operations.
class Callbacks final : public ManagedCallback, public ManagedCallback2 {
    std::atomic<uint32_t> references{1};
public:
    HResult QueryInterface(const Guid& iid, void** result) override {
        if (!result) return static_cast<HResult>(0x80004003);
        *result = nullptr;
        if (!std::memcmp(&iid, &UnknownId, sizeof(Guid)) || !std::memcmp(&iid, &CallbackId, sizeof(Guid)))
            *result = static_cast<ManagedCallback*>(this);
        else if (!std::memcmp(&iid, &Callback2Id, sizeof(Guid)))
            *result = static_cast<ManagedCallback2*>(this);
        if (!*result) return static_cast<HResult>(0x80004002);
        AddRef();
        return 0;
    }
    uint32_t AddRef() override { return references.fetch_add(1) + 1; }
    uint32_t Release() override { return references.fetch_sub(1) - 1; }
    HResult Breakpoint(Controller* c, Unknown* thread, Unknown*) override { return Enqueue(c, thread, CallbackKind::Breakpoint); }
    HResult StepComplete(Controller* c, Unknown*, Unknown*, uint32_t) override { return Enqueue(c); }
    HResult Break(Controller* c, Unknown*) override { return Enqueue(c); }
    HResult Exception(Controller* c, Unknown*, Bool) override { return Enqueue(c); }
    HResult EvalComplete(Controller* c, Unknown*, Unknown*) override { return Enqueue(c); }
    HResult EvalException(Controller* c, Unknown*, Unknown*) override { return Enqueue(c); }
    HResult CreateProcess(Controller* c) override {
        {
            std::lock_guard<std::mutex> guard(queueMutex);
            if (!callbackProcess) { c->AddRef(); callbackProcess = c; }
        }
        Report("create-process-callback");
        return Enqueue(c);
    }
    HResult ExitProcess(Controller*) override {
        exited.store(true);
        Report("exit-process-callback");
        queueReady.notify_one();
        return 0;
    }
    HResult CreateThread(Controller* c, Unknown*) override { return Enqueue(c); }
    HResult ExitThread(Controller* c, Unknown*) override { return Enqueue(c); }
    HResult LoadModule(Controller* c, Unknown* module) override { return Enqueue(c, module, CallbackKind::Module); }
    HResult UnloadModule(Controller* c, Unknown*) override { return Enqueue(c); }
    HResult LoadClass(Controller* c, Unknown*) override { return Enqueue(c); }
    HResult UnloadClass(Controller* c, Unknown*) override { return Enqueue(c); }
    HResult DebuggerError(Controller*, HResult error, uint32_t) override {
        Report("debugger-error-callback", error);
        state.store(-1);
        queueReady.notify_one();
        return 0;
    }
    HResult LogMessage(Controller* c, Unknown*, int32_t, const char16_t*, const char16_t*) override { return Enqueue(c); }
    HResult LogSwitch(Controller* c, Unknown*, int32_t, uint32_t, const char16_t*, const char16_t*) override { return Enqueue(c); }
    HResult CreateAppDomain(Controller* c, Controller* domain) override { return Enqueue(c, domain, CallbackKind::Domain); }
    HResult ExitAppDomain(Controller* c, Controller*) override { return Enqueue(c); }
    HResult LoadAssembly(Controller* c, Unknown*) override { return Enqueue(c); }
    HResult UnloadAssembly(Controller* c, Unknown*) override { return Enqueue(c); }
    HResult ControlCTrap(Controller* c) override { return Enqueue(c); }
    HResult NameChange(Controller* c, Unknown*) override { return Enqueue(c); }
    HResult UpdateModuleSymbols(Controller* c, Unknown*, Unknown*) override { return Enqueue(c); }
    HResult EditAndContinueRemap(Controller* c, Unknown*, Unknown*, Bool) override { return Enqueue(c); }
    HResult BreakpointSetError(Controller* c, Unknown*, Unknown*, uint32_t) override { return Enqueue(c); }
    HResult FunctionRemapOpportunity(Controller* c, Unknown*, Unknown*, Unknown*, uint32_t) override { return Enqueue(c); }
    HResult CreateConnection(Controller* c, uint32_t, const char16_t*) override { return Enqueue(c); }
    HResult ChangeConnection(Controller* c, uint32_t) override { return Enqueue(c); }
    HResult DestroyConnection(Controller* c, uint32_t) override { return Enqueue(c); }
    HResult Exception(Controller* c, Unknown*, Unknown*, uint32_t, uint32_t, uint32_t) override { return Enqueue(c); }
    HResult ExceptionUnwind(Controller* c, Unknown*, uint32_t, uint32_t) override { return Enqueue(c); }
    HResult FunctionRemapComplete(Controller* c, Unknown*, Unknown*) override { return Enqueue(c); }
    HResult MDANotification(Controller* c, Unknown*, Unknown*) override { return Enqueue(c); }
};
Callbacks handler;

template<typename T = Unknown> struct Ref {
    T* value = nullptr;
    ~Ref() { if (value) value->Release(); }
};

std::string Hex(const void* bytes, size_t count) {
    std::string result;
    auto* data = static_cast<const uint8_t*>(bytes);
    for (size_t i = 0; i < count; ++i) {
        result += "0123456789ABCDEF"[data[i] >> 4];
        result += "0123456789ABCDEF"[data[i] & 15];
    }
    return result;
}

void ClearFrames() {
    for (auto& frame : frames) frame.second->Release();
    frames.clear();
}

void ContinueStop() {
    if (!stoppedController) throw std::runtime_error("InvalidState");
    Controller* owner = stoppedController;
    stoppedController = nullptr;
    stopId = 0;
    stopReported = false;
    ClearFrames();
    if (stoppedThread) { stoppedThread->Release(); stoppedThread = nullptr; }
    HResult hr = owner->Continue(0);
    owner->Release();
    Report("breakpoint-continue-return", hr);
    Require(hr);
}

bool MatchesModule(Unknown* module) {
    Ref metadata;
    HResult hr = static_cast<Module*>(module)->GetMetaDataInterface(MetadataId, &metadata.value);
    if (hr < 0 || !metadata.value) return false;
    char16_t name[1024]{};
    uint32_t length = 0;
    Guid mvid{};
    hr = static_cast<MetadataImport*>(metadata.value)->GetScopeProps(name, 1024, &length, &mvid);
    return hr >= 0 && Hex(&mvid, sizeof(mvid)) == location.at("Mvid").get<std::string>();
}

bool MatchesFrame(Unknown* frame, uint32_t token) {
    if (token != location.at("MethodToken").get<uint32_t>()) return false;
    Ref<Function> function;
    Ref<Module> module;
    Require(static_cast<Frame*>(frame)->GetFunction(&function.value));
    Require(function.value->GetModule(&module.value));
    return MatchesModule(module.value);
}

void BindModule(Unknown* module) {
    if (location.is_null() || functionBreakpoint || !MatchesModule(module)) return;
    Ref<Function> function;
    Ref<Code> code;
    Ref<Breakpoint> breakpoint;
    Require(static_cast<Module*>(module)->GetFunctionFromToken(location.at("MethodToken").get<uint32_t>(), &function.value));
    Require(function.value->GetILCode(&code.value));
    Require(code.value->CreateBreakpoint(location.at("IlOffset").get<uint32_t>(), &breakpoint.value));
    Require(breakpoint.value->Activate(1));
    functionBreakpoint = breakpoint.value;
    breakpoint.value = nullptr;
    Report("portable-pdb-breakpoint-bound");
}

void Pump() {
    heartbeat.store(Milliseconds());
    if (cancel.load()) throw std::runtime_error("Canceled");
    if (exited.load()) throw std::runtime_error("TargetExited");
    for (int i = 0; i < 256; ++i) {
        Callback callback{};
        {
            std::lock_guard<std::mutex> guard(queueMutex);
            if (callbacks.empty()) return;
            callback = callbacks.front();
            callbacks.pop_front();
        }
        Ref<Controller> controller{callback.controller};
        Ref<> object{callback.object};
        if (callback.kind == CallbackKind::Breakpoint && !stoppedController && !cleaning) {
            stoppedController = callback.controller;
            stoppedThread = callback.object;
            controller.value = nullptr; object.value = nullptr;
            stopId = NewId();
            Report("managed-breakpoint-callback");
            return;
        }
        if (callback.kind == CallbackKind::Module) BindModule(callback.object);
        if (callback.kind == CallbackKind::Domain) Require(static_cast<AppDomain*>(callback.object)->Attach());
        HResult hr = callback.controller->Continue(0);
        Report("callback-continue-return", hr);
        Require(hr);
    }
}

Json Stack() {
    if (!stoppedThread) throw std::runtime_error("InvalidState");
    Ref<Frame> active;
    Report("stack-active-frame-enter");
    Require(static_cast<Thread*>(stoppedThread)->GetActiveFrame(&active.value));
    Report("stack-active-frame-return");
    Json result = Json::array();
    for (int i = 0; active.value && i < 32; ++i) {
        uint32_t token = 0, offset = 0, mapping = 0;
        Report("stack-function-token-enter");
        HResult tokenHr = active.value->GetFunctionToken(&token);
        Ref<ILFrame> il;
        Report("stack-il-interface-enter");
        HResult ilHr = active.value->QueryInterface(ILFrameId, reinterpret_cast<void**>(&il.value));
        Report("stack-il-ip-enter");
        if (tokenHr >= 0 && ilHr >= 0 && il.value->GetIP(&offset, &mapping) >= 0) {
            Report("stack-module-identity-enter");
            bool matches = MatchesFrame(active.value, token);
            int64_t handle = NewId();
            if (frames.size() >= 4096) throw std::runtime_error("HandleLimit");
            active.value->AddRef();
            frames.emplace(handle, active.value);
            char fallback[32];
            std::snprintf(fallback, sizeof(fallback), "method 0x%08x", token);
            result.push_back({{"Handle", handle}, {"Name", matches ? location.at("Method").get<std::string>() : std::string(fallback)},
                {"Source", matches ? breakpoints.at("Source") : Json(nullptr)},
                {"Line", matches && offset == location.at("IlOffset").get<uint32_t>() ? breakpoints.at("Lines")[0] : Json(0)},
                {"MethodToken", token}, {"IlOffset", offset}, {"Mapping", mapping}});
        }
        Ref<Frame> caller;
        Report("stack-caller-enter");
        HResult callerHr = active.value->GetCaller(&caller.value);
        active.value->Release();
        active.value = callerHr >= 0 ? caller.value : nullptr;
        if (callerHr >= 0) caller.value = nullptr;
    }
    Report("stack-complete");
    return result;
}

Json Evaluate(const Json& request) {
    int64_t handle = request.at("Frame").get<int64_t>();
    auto found = frames.find(handle);
    if (!stoppedController || found == frames.end()) throw std::runtime_error("InvalidState");
    uint32_t frameToken = 0;
    Require(static_cast<Frame*>(found->second)->GetFunctionToken(&frameToken));
    if (!MatchesFrame(found->second, frameToken)) throw std::runtime_error("InvalidState");
    std::string expression = request.at("Expression").get<std::string>();
    if (expression.size() > 256) throw std::runtime_error("InvalidState");
    size_t plus = expression.find('+');
    std::string name = expression.substr(0, plus);
    while (!name.empty() && name.back() == ' ') name.pop_back();
    while (!name.empty() && name.front() == ' ') name.erase(0, 1);
    int32_t add = 0;
    if (plus != std::string::npos) {
        std::string literal = expression.substr(plus + 1);
        size_t used = 0;
        long parsed = std::stol(literal, &used);
        if (used != literal.size() || parsed < INT32_MIN || parsed > INT32_MAX) throw std::runtime_error("InvalidState");
        add = static_cast<int32_t>(parsed);
    }
    Json variable;
    for (auto& local : location.at("Variables"))
        if (local.at("Name") == name) variable = local;
    if (variable.is_null()) throw std::runtime_error("InvalidState");
    Ref<ILFrame> il;
    Ref<Value> value;
    Ref<GenericValue> generic;
    Require(found->second->QueryInterface(ILFrameId, reinterpret_cast<void**>(&il.value)));
    uint32_t index = variable.at("Index").get<uint32_t>();
    if (variable.at("Parameter").get<bool>()) Require(il.value->GetArgument(index, &value.value));
    else Require(il.value->GetLocalVariable(index, &value.value));
    uint32_t type = 0, size = 0;
    Require(value.value->GetType(&type));
    Require(value.value->GetSize(&size));
    if (type != 8 || size != 4) throw std::runtime_error("UnsupportedValueType");
    Require(value.value->QueryInterface(GenericValueId, reinterpret_cast<void**>(&generic.value)));
    int32_t number = 0;
    Require(generic.value->GetValue(&number));
    int64_t answer = int64_t{number} + add;
    if (answer < INT32_MIN || answer > INT32_MAX) throw std::runtime_error("IntegerOverflow");
    return {{"Value", std::to_string(answer)}, {"Type", "int"}};
}

void StartDebugging() {
    if (debug || location.is_null()) throw std::runtime_error("InvalidState");
    Report("dllmain-enter");
    Bool initialized = dllMain(nullptr, 1, nullptr);
    Report("dllmain-return", initialized ? 0 : Fail);
    if (!initialized) throw std::runtime_error("PrivateInitializationFailed");
    // UTF-16 paths, never wchar_t (which is 32-bit on these targets).
    // Installed native-library paths on the supported fixtures are ASCII.
    std::u16string wideDac(dacPath.begin(), dacPath.end());
    Report("factory-enter");
    HResult hr = factory(3, static_cast<uint32_t>(getpid()), nullptr, wideDac.c_str(), runtimeBase, &debug);
    Report("factory-return", hr);
    if (hr < 0 || !debug) throw std::runtime_error("PrivateFactoryFailed");
    Report("initialize-enter");
    hr = debug->Initialize();
    Report("initialize-return", hr);
    ownsInitialize = hr >= 0;
    if (ownsInitialize) {
        hr = debug->SetManagedHandler(static_cast<ManagedCallback*>(&handler));
        Report("set-handler-return", hr);
    }
    if (hr >= 0) {
        Report("attach-enter");
        hr = debug->DebugActiveProcess(static_cast<uint32_t>(getpid()), 0, &process);
        Report("attach-return", hr);
    }
    if (hr < 0 || !process) throw std::runtime_error("PrivateAttachFailed");
    state.store(2);
}

void Cleanup() {
    cleaning = true;
    cancel.store(false);
    if (stoppedController) ContinueStop();
    ClearFrames();
    if (functionBreakpoint) {
        HResult hr = functionBreakpoint->Activate(0);
        Report("breakpoint-deactivate-return", hr);
        functionBreakpoint->Release();
        functionBreakpoint = nullptr;
    }
    if (process && !exited.load()) {
        // Drain callbacks exactly once before the explicit cleanup stop/detach.
        Pump();
        HResult hr = process->Stop(5000);
        Report("cleanup-stop-return", hr);
        Require(hr);
        hr = process->Detach();
        Report("cleanup-detach-return", hr);
        Require(hr);
    }
    if (process) { process->Release(); process = nullptr; }
    if (ownsInitialize) {
        Report("terminate-enter");
        HResult hr = debug->Terminate();
        Report("terminate-return", hr);
        Require(hr);
    }
    if (debug) { debug->Release(); debug = nullptr; }
    {
        std::lock_guard<std::mutex> guard(queueMutex);
        for (auto& callback : callbacks) {
            callback.controller->Release();
            if (callback.object) callback.object->Release();
        }
        callbacks.clear();
        if (callbackProcess) { callbackProcess->Release(); callbackProcess = nullptr; }
    }
}

void* Run(void*) {
    int connection = -1;
    try {
        auto acceptDeadline = Clock::now() + std::chrono::seconds(120);
        while (connection < 0) {
            Pump();
            if (Clock::now() >= acceptDeadline) throw std::runtime_error("HostConnectTimeout");
            pollfd fd{listener, POLLIN, 0};
            if (poll(&fd, 1, 20) > 0) connection = accept(listener, nullptr, nullptr);
        }
        fcntl(connection, F_SETFL, O_NONBLOCK);
#ifdef __APPLE__
        int yes = 1;
        setsockopt(connection, SOL_SOCKET, SO_NOSIGPIPE, &yes, sizeof(yes));
#endif
        Wire wire{connection, Pump, Clock::now() + std::chrono::seconds(5)};
        Json hello = wire.Read();
        if (hello.at("Version") != 1 || hello.at("Sequence") != 1 || hello.at("Operation") != 1) {
            wire.Reply(hello, Json::object(), "ProtocolMismatch");
            throw std::runtime_error("ProtocolMismatch");
        }
        std::string supplied = hello.at("Body").at("Token").get<std::string>();
        uint32_t mismatch = static_cast<uint32_t>(supplied.size() ^ sessionToken.size());
        for (size_t i = 0; i < sessionToken.size(); ++i)
            mismatch |= static_cast<uint8_t>(sessionToken[i]) ^ static_cast<uint8_t>(i < supplied.size() ? supplied[i] : 0);
        if (mismatch) {
            wire.Reply(hello, Json::object(), "AuthenticationFailed");
            throw std::runtime_error("AuthenticationFailed");
        }
        sessionToken.clear();
        wire.Reply(hello, Json::parse(capabilities));
        Report("authenticated-native-session");
        int64_t sequence = 1;
        bool done = false;
        while (!done) {
            wire.deadline = Clock::now() + std::chrono::seconds(30);
            Json request = wire.Read();
            if (request.at("Version") != 1 || request.at("Sequence") != ++sequence) throw std::runtime_error("SequenceMismatch");
            auto& body = request.at("Body");
            try {
                Json result = Json::object();
                switch (request.at("Operation").get<int>()) {
                    case 2:
                        if (debug || body.at("Source").get<std::string>().size() > 8192 || body.at("Lines").size() != 1 ||
                            body.at("Lines")[0].get<int>() <= 0 || body.at("Location").is_null()) throw std::runtime_error("InvalidState");
                        breakpoints = body;
                        location = body.at("Location");
                        if (location.at("Variables").size() > 256 || location.at("Mvid").get<std::string>().size() != 32)
                            throw std::runtime_error("InvalidState");
                        if ((location.at("MethodToken").get<uint32_t>() & 0xff000000) != 0x06000000 ||
                            location.at("IlOffset").get<int64_t>() < 0 || location.at("IlOffset").get<int64_t>() > UINT32_MAX ||
                            location.at("Method").get<std::string>().size() > 8192) throw std::runtime_error("InvalidState");
                        for (auto& variable : location.at("Variables"))
                            if (variable.at("Name").get<std::string>().size() > 256 || variable.at("Index").get<int>() < 0 ||
                                variable.at("Index").get<int>() > 65535) throw std::runtime_error("InvalidState");
                        result = Json::array({{{"Line", body.at("Lines")[0]}, {"Verified", false}}});
                        break;
                    case 3: StartDebugging(); break;
                    case 4:
                        if (!debug || stopReported) throw std::runtime_error("InvalidState");
                        while (!stoppedController) {
                            Pump();
                            if (Clock::now() >= wire.deadline) throw std::runtime_error("BreakpointTimeout");
                            char test;
                            if (recv(connection, &test, 1, MSG_PEEK) == 0) throw std::runtime_error("Disconnected");
                            usleep(1000);
                        }
                        {
                            uint32_t thread = 0;
                            Require(static_cast<Thread*>(stoppedThread)->GetID(&thread));
                            result = {{"Id", stopId}, {"Reason", "breakpoint"}, {"ThreadId", thread}};
                            stopReported = true;
                        }
                        break;
                    case 5:
                        if (!stopId || body.at("Id") != stopId) throw std::runtime_error("InvalidState");
                        result = Stack(); break;
                    case 6: result = Evaluate(body); break;
                    case 7:
                        if (!stopId || body.at("Id") != stopId) throw std::runtime_error("InvalidState");
                        ContinueStop(); break;
                    case 8: {
                        auto handles = body.at("Handles").get<std::vector<int64_t>>();
                        if (handles.size() > 4096) throw std::runtime_error("InvalidState");
                        std::unordered_map<int64_t, bool> seen;
                        for (auto handle : handles)
                            if (!frames.count(handle) || !seen.emplace(handle, true).second) throw std::runtime_error("InvalidState");
                        for (auto handle : handles) { frames.at(handle)->Release(); frames.erase(handle); }
                        break;
                    }
                    case 9: done = true; break;
                    default: wire.Reply(request, Json::object(), "UnsupportedOperation"); continue;
                }
                wire.Reply(request, result);
            } catch (const nlohmann::json::exception&) {
                Report("MalformedPayload", Fail);
            } catch (const std::exception& error) {
                if (std::strcmp(error.what(), "InvalidState") != 0) throw;
                wire.Reply(request, Json::object(), "InvalidState");
            }
        }
    } catch (const std::exception& error) {
        Report(error.what(), Fail);
    }
    if (connection >= 0) close(connection);
    if (listener >= 0) { close(listener); listener = -1; }
    try { Cleanup(); }
    catch (const std::exception& error) {
        Report(error.what(), Fail);
        Report("cleanup-failed-exiting-owned-target", Fail);
        _exit(87);
    }
    state.store(-2);
    Report("owner-finished");
    ownerFinished.store(true);
    std::lock_guard<std::mutex> guard(reportMutex);
    if (reportFd >= 0) { close(reportFd); reportFd = -1; }
    return nullptr;
}

void* Watchdog(void*) {
    // Private DBI calls have no reliable cancellation contract. This opt-in fixture
    // fails closed by exiting its own process, never by leaving managed threads suspended.
    while (!ownerFinished.load()) {
        if (Milliseconds() - heartbeat.load() >= 20000) {
            Report("native-call-or-cleanup-timeout", Fail);
            _exit(86);
        }
        usleep(100000);
    }
    return nullptr;
}
}

EXPORT const char* dncdbg_runtime_path() {
    if (!PlatformAllowed()) return nullptr;
    void* symbol = dlsym(RTLD_DEFAULT, "coreclr_initialize");
    if (!symbol) {
#ifdef __APPLE__
        const char* name = "libcoreclr.dylib";
#else
        const char* name = "libcoreclr.so";
#endif
        void* loaded = dlopen(name, RTLD_NOLOAD | RTLD_NOW);
        if (loaded) { symbol = dlsym(loaded, "coreclr_initialize"); dlclose(loaded); }
    }
    Dl_info info{};
    if (!symbol || !dladdr(symbol, &info)) return nullptr;
    runtimeBase = info.dli_fbase;
    runtimePath = info.dli_fname;
    return runtimePath.c_str();
}

EXPORT int dncdbg_prepare(const char* expectedRuntime, const char* expectedDbi, const char* expectedDac) {
    if (!PlatformAllowed() || state.load() != 0 || !expectedRuntime || !expectedDbi || !expectedDac ||
        runtimePath != expectedRuntime) return 0;
    std::string directory = runtimePath.substr(0, runtimePath.find_last_of('/') + 1);
#ifdef __APPLE__
    const char* extension = ".dylib";
#else
    const char* extension = ".so";
#endif
    dbiPath = directory + "libmscordbi" + extension;
    dacPath = directory + "libmscordaccore" + extension;
    if (dbiPath != expectedDbi || dacPath != expectedDac ||
        dacPath.find_first_not_of("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789/._-+~= ") != std::string::npos) return 0;
    // All three image identities/hashes are validated by the caller before this load.
    dbi = dlopen(dbiPath.c_str(), RTLD_NOW | RTLD_LOCAL);
    if (!dbi) return 0;
    dllMain = reinterpret_cast<DllMainFunction>(dlsym(dbi, "DllMain"));
    factory = reinterpret_cast<FactoryFunction>(dlsym(dbi, "CoreCLRCreateCordbObject3"));
    if (dllMain && factory) {
        Dl_info first{}, second{};
        char expectedPath[PATH_MAX]{}, loadedPath[PATH_MAX]{};
        if (!dladdr(reinterpret_cast<void*>(dllMain), &first) || !dladdr(reinterpret_cast<void*>(factory), &second) ||
            first.dli_fbase != second.dli_fbase || !realpath(dbiPath.c_str(), expectedPath) ||
            !realpath(first.dli_fname, loadedPath) || std::strcmp(expectedPath, loadedPath)) {
            dllMain = nullptr;
            factory = nullptr;
        }
    }
    return (dllMain ? 1 : 0) | (factory ? 2 : 0);
}

EXPORT int dncdbg_start(const char* report, const char* token, const char* capabilityJson) {
    int expected = 0;
    if (!PlatformAllowed() || !dllMain || !factory || !report || !token || !capabilityJson ||
        std::strlen(token) != 64 || std::strlen(capabilityJson) > 16384 || !state.compare_exchange_strong(expected, 1)) return 0;
    sessionToken = token;
    capabilities = capabilityJson;
    reportFd = open(report, O_WRONLY | O_CREAT | O_TRUNC | O_NOFOLLOW, 0600);
    if (reportFd < 0) { state.store(-1); return 0; }
    auto failStart = [] {
        state.store(-1);
        ownerFinished.store(true);
        Report("native-listener-or-thread-start-failed", Fail);
        std::lock_guard<std::mutex> guard(reportMutex);
        if (listener >= 0) { close(listener); listener = -1; }
        if (reportFd >= 0) { close(reportFd); reportFd = -1; }
        return 0;
    };
    listener = socket(AF_INET, SOCK_STREAM, 0);
    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (listener < 0 || bind(listener, reinterpret_cast<sockaddr*>(&address), sizeof(address)) ||
        listen(listener, 1)) return failStart();
    socklen_t addressSize = sizeof(address);
    if (getsockname(listener, reinterpret_cast<sockaddr*>(&address), &addressSize)) return failStart();
    Report("validated-native-owner-starting");
    pthread_t watchdog, owner;
    heartbeat.store(Milliseconds());
    int error = pthread_create(&watchdog, nullptr, Watchdog, nullptr);
    if (error) return failStart();
    pthread_detach(watchdog);
    error = pthread_create(&owner, nullptr, Run, nullptr);
    if (error) {
        return failStart();
    }
    pthread_detach(owner);
    return ntohs(address.sin_port);
}
EXPORT int dncdbg_status() { return state.load(); }
EXPORT void dncdbg_cancel() { cancel.store(true); queueReady.notify_one(); }
