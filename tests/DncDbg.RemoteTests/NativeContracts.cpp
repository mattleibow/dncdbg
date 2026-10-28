// Host contract fakes only: no CoreCLR, DBI, DAC, or private export is loaded.
#include "../../src/DncDbg.Remote.Target/Bootstrap.cpp"
#include <thread>
#include <iostream>
#include <cmath>
#include <limits>

namespace {
int checks = 0;
void Check(bool valid, const char* name) {
    if (!valid) throw std::runtime_error(name);
    ++checks;
    std::cout << "PASS: " << name << '\n';
}
struct Fake final : Controller {
    uint32_t refs = 1;
    std::atomic<int> continues{0};
    std::atomic<int> stops{0}, detaches{0};
    HResult QueryInterface(const Guid&, void**) override { return Fail; }
    uint32_t AddRef() override { return ++refs; }
    uint32_t Release() override { return --refs; }
    HResult Stop(uint32_t) override { ++stops; return 0; }
    HResult Continue(Bool) override { ++continues; return 0; }
    HResult IsRunning(Bool*) override { return 0; }
    HResult HasQueuedCallbacks(void*, Bool*) override { return 0; }
    HResult EnumerateThreads(void**) override { return 0; }
    HResult SetAllThreadsDebugState(uint32_t, void*) override { return 0; }
    HResult Detach() override { ++detaches; return 0; }
    HResult Terminate(uint32_t) override { return 0; }
    HResult CanCommitChanges(uint32_t, Unknown**, Unknown**) override { return 0; }
    HResult CommitChanges(uint32_t, Unknown**, Unknown**) override { return 0; }
};
template<class T> struct FakeRef : T {
    uint32_t refs = 1;
    HResult QueryInterface(const Guid&, void**) override { return Fail; }
    uint32_t AddRef() override { return ++refs; }
    uint32_t Release() override { return --refs; }
};
struct Debugger final : FakeRef<CorDebug> {
    HResult Initialize() override { return 0; }
    HResult Terminate() override { return 0; }
    HResult SetManagedHandler(Unknown*) override { return Fail; }
    HResult SetUnmanagedHandler(Unknown*) override { return Fail; }
    HResult CreateProcess(const char16_t*, char16_t*, void*, void*, Bool, uint32_t, void*, const char16_t*, void*, void*, uint32_t, Controller**) override { return Fail; }
    HResult DebugActiveProcess(uint32_t, Bool, Controller**) override { return Fail; }
};
struct Primitive final : FakeRef<GenericValue> {
    uint32_t type, size;
    uint64_t bits = 0;
    template<class T> Primitive(uint32_t type, T value) : type(type), size(sizeof(T)) { std::memcpy(&bits, &value, sizeof(T)); }
    HResult QueryInterface(const Guid& id, void** result) override {
        if (std::memcmp(&id, &GenericValueId, sizeof(id))) return Fail;
        *result = this; AddRef(); return 0;
    }
    HResult GetType(uint32_t* result) override { *result = type; return 0; }
    HResult GetSize(uint32_t* result) override { *result = size; return 0; }
    HResult GetAddress(uint64_t*) override { return Fail; }
    HResult CreateBreakpoint(Unknown**) override { return Fail; }
    HResult GetValue(void* result) override { std::memcpy(result, &bits, size); return 0; }
    HResult SetValue(void*) override { return Fail; }
};
struct Array final : FakeRef<ArrayValue> {
    Primitive child{8, int32_t{0}};
    HResult QueryInterface(const Guid& id, void** result) override {
        if (std::memcmp(&id, &ArrayValueId, sizeof(id))) return Fail;
        *result = this; AddRef(); return 0;
    }
    HResult GetType(uint32_t* result) override { *result = 0x1d; return 0; }
    HResult GetSize(uint32_t* result) override { *result = 8; return 0; }
    HResult GetAddress(uint64_t*) override { return Fail; }
    HResult CreateBreakpoint(Unknown**) override { return Fail; }
    HResult IsValid(Bool* result) override { *result = 1; return 0; }
    HResult CreateRelocBreakpoint(Unknown**) override { return Fail; }
    HResult GetElementType(uint32_t* result) override { *result = 8; return 0; }
    HResult GetRank(uint32_t* result) override { *result = 1; return 0; }
    HResult GetCount(uint32_t* result) override { *result = 150; return 0; }
    HResult GetDimensions(uint32_t, uint32_t*) override { return Fail; }
    HResult HasBaseIndicies(Bool* result) override { *result = 0; return 0; }
    HResult GetBaseIndicies(uint32_t, uint32_t*) override { return Fail; }
    HResult GetElement(uint32_t, uint32_t*, Value**) override { return Fail; }
    HResult GetElementAtPosition(uint32_t index, Value** result) override {
        if (index >= 150) return Fail;
        child.bits = index; child.AddRef(); *result = &child; return 0;
    }
};
struct ExceptionMetadata final : FakeRef<MetadataImport> {
    std::u16string name = u"System.InvalidOperationException";
    void CloseEnum(void*) override {}
    HResult CountEnum(void*, uint32_t*) override { return Fail; }
    HResult ResetEnum(void*, uint32_t) override { return Fail; }
    HResult EnumTypeDefs(void**, uint32_t*, uint32_t, uint32_t*) override { return Fail; }
    HResult EnumInterfaceImpls(void**, uint32_t, uint32_t*, uint32_t, uint32_t*) override { return Fail; }
    HResult EnumTypeRefs(void**, uint32_t*, uint32_t, uint32_t*) override { return Fail; }
    HResult FindTypeDefByName(const char16_t*, uint32_t, uint32_t*) override { return Fail; }
    HResult GetScopeProps(char16_t*, uint32_t, uint32_t*, Guid*) override { return Fail; }
    HResult GetModuleFromScope(uint32_t*) override { return Fail; }
    HResult GetTypeDefProps(uint32_t, char16_t* result, uint32_t capacity, uint32_t* count, uint32_t*, uint32_t*) override {
        if (capacity <= name.size()) return Fail;
        std::copy(name.begin(), name.end(), result); result[name.size()] = 0;
        *count = static_cast<uint32_t>(name.size() + 1); return 0;
    }
};
struct ExceptionModule final : FakeRef<Module> {
    ExceptionMetadata metadata;
    HResult GetProcess(Controller**) override { return Fail; }
    HResult GetBaseAddress(uint64_t*) override { return Fail; }
    HResult GetAssembly(Unknown**) override { return Fail; }
    HResult GetName(uint32_t, uint32_t*, char16_t*) override { return Fail; }
    HResult EnableJITDebugging(Bool, Bool) override { return Fail; }
    HResult EnableClassLoadCallbacks(Bool) override { return Fail; }
    HResult GetFunctionFromToken(uint32_t, Function**) override { return Fail; }
    HResult GetFunctionFromRVA(uint64_t, Function**) override { return Fail; }
    HResult GetClassFromToken(uint32_t, Unknown**) override { return Fail; }
    HResult CreateBreakpoint(Unknown**) override { return Fail; }
    HResult GetEditAndContinueSnapshot(Unknown**) override { return Fail; }
    HResult GetMetaDataInterface(const Guid&, Unknown** result) override { metadata.AddRef(); *result = &metadata; return 0; }
};
struct ExceptionClass final : FakeRef<Class> {
    ExceptionModule module;
    HResult GetModule(Module** result) override { module.AddRef(); *result = &module; return 0; }
    HResult GetToken(uint32_t* result) override { *result = 0x02000001; return 0; }
    HResult GetStaticFieldValue(uint32_t, Frame*, Value**) override { return Fail; }
};
struct ExceptionValue final : FakeRef<ObjectValue> {
    ExceptionClass klass;
    HResult QueryInterface(const Guid& id, void** result) override {
        if (std::memcmp(&id, &ObjectValueId, sizeof(id))) return Fail;
        *result = this; AddRef(); return 0;
    }
    HResult GetType(uint32_t* result) override { *result = 0x12; return 0; }
    HResult GetSize(uint32_t* result) override { *result = 8; return 0; }
    HResult GetAddress(uint64_t*) override { return Fail; }
    HResult CreateBreakpoint(Unknown**) override { return Fail; }
    HResult GetClass(Class** result) override { klass.AddRef(); *result = &klass; return 0; }
    HResult GetFieldValue(Class*, uint32_t, Value**) override { return Fail; }
};
struct ExceptionThread final : FakeRef<Thread> {
    ExceptionValue exception;
    HResult GetProcess(Controller**) override { return Fail; }
    HResult GetID(uint32_t* result) override { *result = 7; return 0; }
    HResult GetHandle(void**) override { return Fail; }
    HResult GetAppDomain(AppDomain**) override { return Fail; }
    HResult SetDebugState(uint32_t) override { return Fail; }
    HResult GetDebugState(uint32_t*) override { return Fail; }
    HResult GetUserState(uint32_t*) override { return Fail; }
    HResult GetCurrentException(Value** result) override { exception.AddRef(); *result = &exception; return 0; }
    HResult ClearCurrentException() override { return Fail; }
    HResult CreateStepper(Unknown**) override { return Fail; }
    HResult EnumerateChains(Unknown**) override { return Fail; }
    HResult GetActiveChain(Unknown**) override { return Fail; }
    HResult GetActiveFrame(Frame**) override { return Fail; }
};
void FloatingChecks() {
    for (double input : {1e-10, 1.23456789012345, -0.0, std::numeric_limits<double>::infinity(),
                         -std::numeric_limits<double>::infinity(), std::numeric_limits<double>::quiet_NaN()}) {
        Primitive value{13, input};
        std::string text = Describe(&value).at("Value");
        double actual = std::stod(text);
        Check(std::isnan(input) ? std::isnan(actual) : actual == input && std::signbit(actual) == std::signbit(input),
              "native double formatting round-trips magnitude, precision, NaN, infinity and signed zero");
    }
    for (float input : {1e-10f, 1.2345678f, -0.0f, std::numeric_limits<float>::infinity(),
                        -std::numeric_limits<float>::infinity(), std::numeric_limits<float>::quiet_NaN()}) {
        Primitive value{12, input};
        std::string text = Describe(&value).at("Value");
        float actual = std::stof(text);
        Check(std::isnan(input) ? std::isnan(actual) : actual == input && std::signbit(actual) == std::signbit(input),
              "native float formatting round-trips magnitude, precision, NaN, infinity and signed zero");
    }
}
void VariableChecks() {
    Fake controller, thread;
    Array array;
    Enqueue(&controller, &thread, CallbackKind::Breakpoint); Pump();
    int64_t handle = Lease(&array);
    for (Json request : {Json{{"Handle", handle}}, Json{{"Handle", handle}, {"Count", 0}}}) {
        Json result = ModelVariables(request);
        Check(result.size() == 150 && result[149].at("Value") == "149",
              "native omitted/zero variable count returns every element of a 150-element array");
    }
    Json result = ModelVariables({{"Handle", handle}, {"Start", 141}, {"Count", 4}});
    Check(result.size() == 4 && result[0].at("Value") == "141" && result[3].at("Value") == "144",
          "native explicit variable pages remain bounded");
    ContinueStop();
    Check(array.refs == 1 && array.child.refs == 1, "native array inspection releases all element and array references");
}
int Connect() {
    listener = socket(AF_INET, SOCK_STREAM, 0);
    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (bind(listener, reinterpret_cast<sockaddr*>(&address), sizeof(address)) || listen(listener, 1)) throw std::runtime_error("listen");
    socklen_t size = sizeof(address);
    getsockname(listener, reinterpret_cast<sockaddr*>(&address), &size);
    int client = socket(AF_INET, SOCK_STREAM, 0);
    if (connect(client, reinterpret_cast<sockaddr*>(&address), size)) throw std::runtime_error("connect");
    sessionToken = std::string(64, 'a');
    capabilities = R"({"Runtime":"contract","Platform":"test","ManagedDebugging":false,"Blocker":"fake"})";
    state.store(1); cancel.store(false); exited.store(false); ownerFinished.store(false); cleaning = false;
    return client;
}
void Send(Wire& wire, int64_t sequence, int operation, Json body, int version = 1) {
    Json request{{"Version", version}, {"Sequence", sequence}, {"Operation", operation}, {"Body", body}};
    std::string text = request.dump();
    uint32_t header[]{0x31434e44, static_cast<uint32_t>(text.size())};
    wire.Transfer(header, 8, true);
    wire.Transfer(text.data(), text.size(), true);
}
void ExceptionChecks() {
    Fake controller;
    ExceptionThread thread;
    Debugger debugger;
    int fd = Connect();
    debugger.AddRef(); debug = &debugger;
    std::thread owner(Run, nullptr);
    Wire wire{fd, [] {}, Clock::now() + std::chrono::seconds(5)};
    Send(wire, 1, 1, {{"Token", std::string(64, 'a')}}); wire.Read();
    Send(wire, 2, 18, {{"Filters", {"all", "unhandled"}},
        {"Types", {"System.ArgumentException", "System.InvalidOperationException"}}});
    wire.Read();
    Enqueue(&controller, &thread, CallbackKind::ExceptionThrown);
    Send(wire, 3, 11, Json::object());
    Json response = wire.Read();
    bool caughtInvalidOperationIgnored = response.at("Error").is_null() && response.at("Body").is_array() && response.at("Body").empty();
    close(fd); owner.join();
    Check(caughtInvalidOperationIgnored,
          "caught first-chance InvalidOperationException does not match all:ArgumentException plus unhandled:InvalidOperationException");
}
}

int main(int argc, char** argv) {
    if (argc == 2) {
        if (std::strcmp(argv[1], "--floating") == 0) FloatingChecks();
        else if (std::strcmp(argv[1], "--variables") == 0) VariableChecks();
        else if (std::strcmp(argv[1], "--exceptions") == 0) ExceptionChecks();
        else throw std::runtime_error("Unknown test selection");
        return 0;
    }
    FloatingChecks();
    VariableChecks();
    ExceptionChecks();
    {
        char16_t text[]{'A', 0xd83d, 0xde80, 0xd800};
        Check(Utf8(text, 4) == "A\xf0\x9f\x9a\x80\xef\xbf\xbd", "UTF-16 strings preserve surrogate pairs and replace isolated surrogates");
        bool rejected = false;
        try { ValidateRequest(static_cast<int>(Operation::Step), {{"Stop", 1}}); }
        catch (const std::exception&) { rejected = true; }
        Check(rejected, "generated native schema rejects a malformed typed step before execution");
        rejected = false;
        try { ValidateRequest(static_cast<int>(Operation::Variables), {{"Handle", 1}, {"Start", 1.5}, {"Count", 1}}); }
        catch (const std::exception&) { rejected = true; }
        Check(rejected, "generated schema rejects fractional optional indices before native integer conversion");
        Fake value;
        int64_t handle = Lease(&value);
        Check(value.refs == 2 && modelLeases.count(handle), "typed value lease retains exactly one native reference");
        ClearFrames();
        Check(value.refs == 1 && modelLeases.empty(), "stop invalidation releases typed values/scopes exactly once");
        rejected = false;
        try { ModelVariables({{"Handle", handle}}); }
        catch (const std::runtime_error& error) { rejected = std::strcmp(error.what(), "InvalidState") == 0; }
        Check(rejected && value.refs == 1, "stale typed value generation cannot read or release a new stop");
        rejected = false;
        try { ModelConfigure({{"Modules", Json::array({{{"Mvid", "bad"}}})}}); }
        catch (const std::exception&) { rejected = true; }
        Check(rejected && catalog.empty(), "invalid module catalog cannot partially configure the owner");
        Json point{{"Offset", 0}, {"Source", "fixture.cs"}, {"Line", 1}, {"Checksum", "AA"}, {"Algorithm", "SHA256"}};
        Json method{{"Token", 0x06000001}, {"Name", "Fixture.Main"}, {"Variables", Json::array()}, {"Points", Json::array({point})}};
        Json module{{"Mvid", std::string(32, 'A')}, {"Name", "fixture"}, {"Types", Json::array()}, {"Methods", Json::array({method})}};
        ModelConfigure({{"Modules", Json::array({module})}});
        module["Methods"][0]["Points"][0]["Offset"] = 1;
        ModelConfigure({{"Modules", Json::array({module})}, {"Append", true}});
        Check(catalog.size() == 1 && catalog[0]["Methods"].size() == 1 && catalog[0]["Methods"][0]["Points"].size() == 2,
            "bounded symbol fragments merge by exact MVID and method identity");
        Json original = catalog;
        rejected = false;
        try { ModelConfigure({{"Modules", Json::array({module})}, {"Append", true}}); }
        catch (const std::runtime_error&) { rejected = true; }
        Check(rejected && catalog == original, "duplicate symbol fragment fails without partially mutating the catalog");
        ModelCleanup();
    }
    Check(!PlatformAllowed() && dncdbg_runtime_path() == nullptr &&
        dncdbg_start("unused", std::string(64, 'a').c_str(), "{}") == 0, "host cannot activate target private ABI");
    {
        Fake controller, thread, frame;
        Enqueue(&controller, &thread, CallbackKind::Breakpoint);
        Pump();
        int64_t first = stopId;
        frame.AddRef(); frames.emplace(NewId(), &frame);
        Check(first > 0 && controller.continues == 0, "native callback transfers one stopped lease to its owner");
        ContinueStop();
        Check(controller.continues == 1 && controller.refs == 1 && thread.refs == 1 && frame.refs == 1 && frames.empty(),
            "native continue consumes callback, thread and frame handouts exactly once");
        try { ContinueStop(); Check(false, "duplicate continue accepted"); }
        catch (const std::runtime_error&) { Check(controller.continues == 1, "native duplicate continue rejected"); }
        Enqueue(&controller, &thread, CallbackKind::Breakpoint); Pump();
        Check(stopId > first, "native stop generations never reused");
        ContinueStop();
    }
    for (bool wrongVersion : {false, true}) {
        int fd = Connect();
        std::thread owner(Run, nullptr);
        Wire wire{fd, [] {}, Clock::now() + std::chrono::seconds(5)};
        Send(wire, 1, 1, {{"Token", std::string(64, 'b')}}, wrongVersion ? 99 : 1);
        Json response = wire.Read();
        Check(response.at("Error") == (wrongVersion ? "ProtocolMismatch" : "AuthenticationFailed"),
            wrongVersion ? "native wrong version rejected before activation" : "native stale/wrong token rejected before activation");
        close(fd); owner.join();
        Check(!debug && !process && ownerFinished.load(), "rejected native handshake cannot create a debugger");
    }
    {
        int fd = Connect();
        std::thread owner(Run, nullptr);
        Wire wire{fd, [] {}, Clock::now() + std::chrono::seconds(5)};
        Send(wire, 1, 1, {{"Token", std::string(64, 'a')}}); wire.Read();
        Send(wire, 1, 3, Json::object());
        bool rejected = false;
        try { wire.Read(); } catch (const std::runtime_error&) { rejected = true; }
        close(fd); owner.join();
        Check(rejected && !debug, "native replayed sequence closes connection before execution");
    }
    {
        Fake controller, thread, frame;
        int fd = Connect();
        Enqueue(&controller, &thread, CallbackKind::Breakpoint);
        Pump();
        frame.AddRef(); frames.emplace(NewId(), &frame);
        std::thread owner(Run, nullptr);
        Wire wire{fd, [] {}, Clock::now() + std::chrono::seconds(5)};
        Send(wire, 1, 1, {{"Token", std::string(64, 'a')}}); wire.Read();
        close(fd); owner.join();
        Check(controller.continues == 1 && controller.refs == 1 && thread.refs == 1 && frame.refs == 1 && frames.empty(),
            "native disconnect while stopped relinquishes every owned lease once");
    }
    bool malformedBounded = true;
    for (int malformedOperation : {8, 15}) for (bool stopped : {false, true}) {
        Fake controller, thread, frame, value;
        int fd = Connect();
        controller.AddRef(); process = &controller;
        if (stopped) {
            Enqueue(&controller, &thread, CallbackKind::Breakpoint); Pump();
            frame.AddRef(); frames.emplace(NewId(), &frame);
            Lease(&value);
        }
        std::thread owner(Run, nullptr);
        Wire wire{fd, [] {}, Clock::now() + std::chrono::seconds(5)};
        Send(wire, 1, 1, {{"Token", std::string(64, 'a')}}); wire.Read();
        wire.deadline = Clock::now() + std::chrono::seconds(1);
        Send(wire, 2, malformedOperation, malformedOperation == 8 ? Json::object() :
            Json{{"Handle", 1}, {"Start", 1.5}}); // Missing required field or fractional optional index.
        bool bounded = false;
        try {
            Json response = wire.Read();
            bounded = response.at("Version") == 1 && response.at("Sequence") == 2 &&
                response.at("Operation") == malformedOperation && response.at("Error") == "MalformedPayload";
        } catch (const std::runtime_error& error) {
            bounded = std::strcmp(error.what(), "Disconnected") == 0;
        }
        bool completedBeforeClientClose = false;
        while (Clock::now() < wire.deadline) {
            if (ownerFinished.load()) { completedBeforeClientClose = true; break; }
            usleep(1000);
        }
        close(fd); owner.join();
        malformedBounded &= bounded && completedBeforeClientClose;
        Check(controller.continues == (stopped ? 1 : 0) && controller.stops == 1 && controller.detaches == 1 &&
            controller.refs == 1 && thread.refs == 1 && frame.refs == 1 && value.refs == 1 && frames.empty() && modelLeases.empty() && !process &&
            !stoppedController && !stoppedThread && listener == -1 && ownerFinished.load(),
            stopped ? "malformed stopped typed request cleans/continues/detaches exactly once" :
                      "malformed idle typed request cleans/detaches exactly once");
        std::cout << (bounded && completedBeforeClientClose ? "PASS" : "FAIL")
                  << ": malformed " << (stopped ? "stopped" : "idle") << " operation " << malformedOperation << " terminates within 1s\n";
    }
    Check(malformedBounded, "authenticated malformed typed requests get correlated MalformedPayload or EOF and cleanup within 1s");
    {
        Fake controller;
        int fd = Connect();
        controller.AddRef(); process = &controller;
        std::thread owner(Run, nullptr);
        Wire wire{fd, [] {}, Clock::now() + std::chrono::seconds(5)};
        Send(wire, 1, 1, {{"Token", std::string(64, 'a')}}); wire.Read();
        Send(wire, 2, 9, Json::object());
        Json response = wire.Read();
        bool detachedBeforeAck = controller.detaches == 1 && controller.stops == 1;
        close(fd); owner.join();
        Check(response.at("Error").is_null() && detachedBeforeAck && !process && controller.refs == 1,
            "native disconnect acknowledges only after owned detach and reference cleanup");
    }
    std::cout << "Passed " << checks << " native contract checks (fakes, not mobile runtime proof).\n";
}
