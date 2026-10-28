#pragma once
#include "json.hpp"
#include <arpa/inet.h>
#include <chrono>
#include <cerrno>
#include <cstring>
#include <functional>
#include <poll.h>
#include <stdexcept>
#include <sys/socket.h>

namespace dncdbg {
using Json = nlohmann::json;
using Clock = std::chrono::steady_clock;
struct Wire {
    int socket;
    std::function<void()> pump;
    Clock::time_point deadline;
    void Transfer(void* bytes, size_t count, bool write) {
        auto* cursor = static_cast<uint8_t*>(bytes);
        while (count) {
            pump();
            if (Clock::now() >= deadline) throw std::runtime_error("TransportTimeout");
            pollfd fd{socket, static_cast<short>(write ? POLLOUT : POLLIN), 0};
            int ready = poll(&fd, 1, 20);
            if (ready < 0 && errno == EINTR) continue;
            if (ready < 0 || (fd.revents & (POLLERR | POLLNVAL))) throw std::runtime_error("Disconnected");
            if (!ready) continue;
#ifdef MSG_NOSIGNAL
            constexpr int sendFlags = MSG_NOSIGNAL;
#else
            constexpr int sendFlags = 0;
#endif
            ssize_t n = write ? send(socket, cursor, count, sendFlags) : recv(socket, cursor, count, 0);
            if (n < 0 && (errno == EAGAIN || errno == EINTR)) continue;
            if (n <= 0) throw std::runtime_error("Disconnected");
            cursor += n;
            count -= static_cast<size_t>(n);
        }
    }
    Json Read() {
        uint8_t header[8];
        Transfer(header, 8, false);
        uint32_t magic = 0, size = 0;
        std::memcpy(&magic, header, 4); std::memcpy(&size, header + 4, 4);
        if (magic != 0x31434e44 || size == 0 || size > 262144) throw std::runtime_error("InvalidFrame");
        std::string text(size, '\0');
        Transfer(text.data(), text.size(), false);
        return Json::parse(text, [](int depth, Json::parse_event_t, Json&) {
            if (depth > 32) throw std::runtime_error("JsonDepthExceeded");
            return true;
        });
    }
    void Reply(const Json& request, const Json& body, const char* error = nullptr) {
        Json response{{"Version", 1}, {"Sequence", request.at("Sequence")}, {"Operation", request.at("Operation")}, {"Body", body},
                      {"Error", error ? Json(error) : Json(nullptr)}};
        std::string text = response.dump();
        if (text.size() > 262144) throw std::runtime_error("ResponseLimit");
        uint32_t header[]{0x31434e44, static_cast<uint32_t>(text.size())};
        Transfer(header, 8, true);
        Transfer(text.data(), text.size(), true);
    }
};

inline void Require(HResult hr) {
    if (hr < 0) throw std::runtime_error("CorDebugCallFailed");
}
}
