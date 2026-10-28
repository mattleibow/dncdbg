#define NETHOST_USE_AS_STATIC
#include <nethost.h>
#include <hostfxr.h>
#include <coreclr_delegates.h>
#include "json.hpp"
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <dlfcn.h>
#include <filesystem>
#include <fstream>
#include <fcntl.h>
#include <stdexcept>
#include <string>
#include <sys/stat.h>
#include <thread>
#include <unistd.h>

namespace fs = std::filesystem;
using Json = nlohmann::json;

template<typename T> T Symbol(void* library, const char* name) {
    auto function = reinterpret_cast<T>(dlsym(library, name));
    if (!function) throw std::runtime_error(std::string("Missing native symbol: ") + name);
    return function;
}

void Phase(const char* name) { std::printf("NATIVE_HOST_PHASE %s\n", name); std::fflush(stdout); }
void Check(int result, const char* stage) {
    if (result != 0) throw std::runtime_error(std::string(stage) + ": " + std::to_string(result));
}

int main(int argc, char** argv) {
    try {
        if (argc != 7) throw std::runtime_error("Usage: native-host runtime-directory agent payload.dll capabilities.json session-directory runtimeconfig.json");
        fs::path runtime = fs::canonical(argv[1]), session = fs::canonical(argv[5]);
        if (!std::freopen((session / "host.log").c_str(), "a", stdout) ||
            !std::freopen((session / "host-stderr.log").c_str(), "a", stderr))
            throw std::runtime_error("Cannot open owned native-host logs");
        std::string token = std::getenv("DNCDBG_NATIVE_TOKEN") ? std::getenv("DNCDBG_NATIVE_TOKEN") : "";
        std::string nonce = std::getenv("DNCDBG_SESSION_ID") ? std::getenv("DNCDBG_SESSION_ID") : "";
        if (token.size() != 64 || nonce.size() != 32) throw std::runtime_error("Missing owned startup credentials");
        unsetenv("DNCDBG_NATIVE_TOKEN");
        std::ifstream input(argv[4]);
        Json capabilities = Json::parse(input);
        void* agent = dlopen(argv[2], RTLD_NOW | RTLD_LOCAL);
        if (!agent) throw std::runtime_error(dlerror());
        Phase("agent-loaded-before-runtime");
        auto contractVersion = Symbol<int (*)()>(agent, "dncdbg_contract_version");
        if (contractVersion() != capabilities.at("ModelVersion").get<int>())
            throw std::runtime_error("Native agent/generated contract version mismatch");
        auto runtimePath = Symbol<const char* (*)()>(agent, "dncdbg_runtime_path");
        auto prepare = Symbol<int (*)(const char*, const char*, const char*)>(agent, "dncdbg_prepare");
        auto start = Symbol<int (*)(const char*, const char*, const char*)>(agent, "dncdbg_start");
        auto status = Symbol<int (*)()>(agent, "dncdbg_status");
        auto cancel = Symbol<void (*)()>(agent, "dncdbg_cancel");
        fs::path root = runtime.parent_path().parent_path().parent_path();
        get_hostfxr_parameters parameters{sizeof(get_hostfxr_parameters), nullptr, root.c_str()};
        char buffer[4096];
        size_t size = sizeof(buffer);
        Check(get_hostfxr_path(buffer, &size, &parameters), "get_hostfxr_path");
#ifdef __APPLE__
        constexpr const char* extension = ".dylib";
#else
        constexpr const char* extension = ".so";
#endif
        fs::path expectedFxr = root / "host/fxr" / runtime.filename() / (std::string("libhostfxr") + extension);
        if (fs::canonical(buffer) != fs::canonical(expectedFxr)) throw std::runtime_error("Unpinned hostfxr selected");
        void* fxr = dlopen(buffer, RTLD_NOW | RTLD_LOCAL);
        if (!fxr) throw std::runtime_error(dlerror());
        auto initialize = Symbol<hostfxr_initialize_for_runtime_config_fn>(fxr, "hostfxr_initialize_for_runtime_config");
        auto getDelegate = Symbol<hostfxr_get_runtime_delegate_fn>(fxr, "hostfxr_get_runtime_delegate");
        auto close = Symbol<hostfxr_close_fn>(fxr, "hostfxr_close");
        hostfxr_handle context = nullptr;
        hostfxr_initialize_parameters initialization{sizeof(hostfxr_initialize_parameters), nullptr, root.c_str()};
        Phase("hostfxr-initialize");
        Check(initialize(argv[6], &initialization, &context), "hostfxr_initialize_for_runtime_config");
        load_assembly_and_get_function_pointer_fn load = nullptr;
        Check(getDelegate(context, hdt_load_assembly_and_get_function_pointer, reinterpret_cast<void**>(&load)),
              "hostfxr_get_runtime_delegate");
        Check(close(context), "hostfxr_close");
        Phase("runtime-ready-before-payload");
        const char* observed = runtimePath();
        fs::path clr = runtime / (std::string("libcoreclr") + extension);
        fs::path dbi = runtime / (std::string("libmscordbi") + extension);
        fs::path dac = runtime / (std::string("libmscordaccore") + extension);
        if (!observed || fs::canonical(observed) != fs::canonical(clr)) throw std::runtime_error("Wrong loaded CoreCLR");
        Check(prepare(observed, dbi.c_str(), dac.c_str()) == 3 ? 0 : -1, "private DBI exports");
        int port = start((session / "native-report.jsonl").c_str(), token.c_str(), capabilities.dump().c_str());
        if (port <= 0) throw std::runtime_error("Native agent did not open its authenticated endpoint");
        Json descriptor{{"Port", port}, {"Token", token}, {"ProcessId", getpid()}, {"SessionId", nonce}};
        fs::path descriptorFile = session / "endpoint.json";
        {
            fs::path staging = session / "endpoint.staging";
            int file = open(staging.c_str(), O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW, 0600);
            if (file < 0) throw std::runtime_error("Cannot create owner-only endpoint credential");
            std::string text = descriptor.dump();
            for (size_t offset = 0; offset < text.size();) {
                ssize_t count = write(file, text.data() + offset, text.size() - offset);
                if (count <= 0) { ::close(file); throw std::runtime_error("Endpoint descriptor write failed"); }
                offset += static_cast<size_t>(count);
            }
            if (::close(file)) throw std::runtime_error("Endpoint descriptor close failed");
            fs::rename(staging, descriptorFile);
        }
        token.clear();
        Phase("private-agent-listening");
        auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(60);
        while (status() == 1) {
            if (std::chrono::steady_clock::now() >= deadline) throw std::runtime_error("DAP configuration did not activate native owner");
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
        }
        if (status() != 2) throw std::runtime_error("Private bootstrap failed before managed payload");
        component_entry_point_fn entry = nullptr;
        Check(load(argv[3], "NativeHostPayload.Entry, Payload", "Run", UNMANAGEDCALLERSONLY_METHOD,
                   nullptr, reinterpret_cast<void**>(&entry)), "load_assembly_and_get_function_pointer");
        Phase("native-host-calls-managed-entry");
        std::string directory = session.string();
        int result = entry(directory.data(), static_cast<int>(directory.size()));
        Phase("managed-entry-returned-to-native-host");
        fs::remove(descriptorFile);
        cancel();
        return result;
    } catch (const std::exception& error) {
        std::fprintf(stderr, "Native host failed: %s\n", error.what());
        return 1;
    }
}
