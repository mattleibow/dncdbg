// Generated from contract.json by generate_contract.py. Do not edit.
#pragma once
#include "Wire.h"
namespace dncdbg {
enum class Operation {
    Hello = 1,
    SetBreakpoints = 2,
    Start = 3,
    WaitForStop = 4,
    Stack = 5,
    Evaluate = 6,
    Continue = 7,
    Release = 8,
    Disconnect = 9,
    Configure = 10,
    Poll = 11,
    Threads = 12,
    Modules = 13,
    Scopes = 14,
    Variables = 15,
    Step = 16,
    Pause = 17,
    Exceptions = 18,
    ExceptionInfo = 19,
};
constexpr int ModelVersion = 2;
inline void ValidateRequest(int operation, const Json& body) {
    if (!body.is_object()) throw std::runtime_error("MalformedPayload");
    switch (operation) {
        case 1:
            if (!body.at("Token").is_string()) throw std::runtime_error("MalformedPayload");
            break;
        case 2:
            if (!body.at("Source").is_string()) throw std::runtime_error("MalformedPayload");
            if (!body.at("Lines").is_array()) throw std::runtime_error("MalformedPayload");
            break;
        case 3:
            break;
        case 4:
            break;
        case 5:
            if (!body.at("Id").is_number_integer()) throw std::runtime_error("MalformedPayload");
            break;
        case 6:
            if (!body.at("Frame").is_number_integer()) throw std::runtime_error("MalformedPayload");
            if (!body.at("Expression").is_string()) throw std::runtime_error("MalformedPayload");
            break;
        case 7:
            if (!body.at("Id").is_number_integer()) throw std::runtime_error("MalformedPayload");
            break;
        case 8:
            if (!body.at("Handles").is_array()) throw std::runtime_error("MalformedPayload");
            break;
        case 9:
            break;
        case 10:
            if (!body.at("Modules").is_array()) throw std::runtime_error("MalformedPayload");
            break;
        case 11:
            break;
        case 12:
            break;
        case 13:
            break;
        case 14:
            if (!body.at("Id").is_number_integer()) throw std::runtime_error("MalformedPayload");
            break;
        case 15:
            if (!body.at("Handle").is_number_integer()) throw std::runtime_error("MalformedPayload");
            break;
        case 16:
            if (!body.at("Stop").is_number_integer()) throw std::runtime_error("MalformedPayload");
            if (!body.at("ThreadId").is_number_integer()) throw std::runtime_error("MalformedPayload");
            if (!body.at("Kind").is_string()) throw std::runtime_error("MalformedPayload");
            break;
        case 17:
            break;
        case 18:
            if (!body.at("Filters").is_array()) throw std::runtime_error("MalformedPayload");
            break;
        case 19:
            if (!body.at("Id").is_number_integer()) throw std::runtime_error("MalformedPayload");
            break;
        default: break;
    }
}
}
