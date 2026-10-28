// Host contract fakes only: no CoreCLR, DBI, DAC, or private export is loaded.
#include "../../src/DncDbg.Remote.Target/Bootstrap.cpp"
#include <thread>
#include <iostream>

namespace {
int checks = 0;
void Check(bool valid, const char* name) {
    if (!valid) throw std::runtime_error(name);
    ++checks;
    std::cout << "PASS: " << name << '\n';
}
struct Fake final : Controller {
    uint32_t refs = 1;
    int continues = 0;
    int stops = 0, detaches = 0;
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
}

int main() {
    {
        char16_t text[]{'A', 0xd83d, 0xde80, 0xd800};
        Check(Utf8(text, 4) == "A\xf0\x9f\x9a\x80\xef\xbf\xbd", "UTF-16 strings preserve surrogate pairs and replace isolated surrogates");
        bool rejected = false;
        try { ValidateRequest(static_cast<int>(Operation::Step), {{"Stop", 1}}); }
        catch (const std::exception&) { rejected = true; }
        Check(rejected, "generated native schema rejects a malformed typed step before execution");
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
    for (bool stopped : {false, true}) {
        Fake controller, thread, frame;
        int fd = Connect();
        controller.AddRef(); process = &controller;
        if (stopped) {
            Enqueue(&controller, &thread, CallbackKind::Breakpoint); Pump();
            frame.AddRef(); frames.emplace(NewId(), &frame);
        }
        std::thread owner(Run, nullptr);
        Wire wire{fd, [] {}, Clock::now() + std::chrono::seconds(5)};
        Send(wire, 1, 1, {{"Token", std::string(64, 'a')}}); wire.Read();
        wire.deadline = Clock::now() + std::chrono::seconds(1);
        Send(wire, 2, 8, Json::object()); // Authenticated Release missing Handles.
        bool bounded = false;
        try {
            Json response = wire.Read();
            bounded = response.at("Version") == 1 && response.at("Sequence") == 2 &&
                response.at("Operation") == 8 && response.at("Error") == "MalformedPayload";
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
            controller.refs == 1 && thread.refs == 1 && frame.refs == 1 && frames.empty() && !process &&
            !stoppedController && !stoppedThread && listener == -1 && ownerFinished.load(),
            stopped ? "malformed stopped Release cleans/continues/detaches exactly once" :
                      "malformed idle Release cleans/detaches exactly once");
        std::cout << (bounded && completedBeforeClientClose ? "PASS" : "FAIL")
                  << ": malformed " << (stopped ? "stopped" : "idle") << " Release terminates within 1s\n";
    }
    Check(malformedBounded, "authenticated malformed Release gets correlated MalformedPayload or EOF and cleanup within 1s");
    std::cout << "Passed " << checks << " native contract checks (fakes, not mobile runtime proof).\n";
}
