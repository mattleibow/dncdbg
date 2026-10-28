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
inline bool ContractInteger(const Json& value, int64_t minimum, int64_t maximum) {
    if (!value.is_number_integer()) return false;
    if (value.is_number_unsigned()) { uint64_t number = value.get<uint64_t>();
        return number <= static_cast<uint64_t>(maximum) && (minimum <= 0 || number >= static_cast<uint64_t>(minimum)); }
    int64_t number = value.get<int64_t>(); return number >= minimum && number <= maximum;
}
inline void ValidateEnvelope(const Json& body) {
    if (!body.is_object() || !ContractInteger(body.at("Version"), 0, 2147483647LL) ||
        !ContractInteger(body.at("Operation"), 0, 2147483647LL) ||
        !ContractInteger(body.at("Sequence"), 1, 9223372036854775807LL)) throw std::runtime_error("MalformedPayload");
}
inline void Validate_RemoteHello(const Json& body);
inline void Validate_RemoteCapabilities(const Json& body);
inline void Validate_RemoteEndpoint(const Json& body);
inline void Validate_RemoteBreakpoints(const Json& body);
inline void Validate_RemoteManagedLocation(const Json& body);
inline void Validate_RemoteManagedVariable(const Json& body);
inline void Validate_RemoteBreakpoint(const Json& body);
inline void Validate_RemoteStop(const Json& body);
inline void Validate_RemoteFrame(const Json& body);
inline void Validate_RemoteEvaluation(const Json& body);
inline void Validate_RemoteValue(const Json& body);
inline void Validate_RemoteHandles(const Json& body);
inline void Validate_RemoteId(const Json& body);
inline void Validate_RemoteEmpty(const Json& body);
inline void Validate_RemoteConfiguration(const Json& body);
inline void Validate_RemoteModuleSymbols(const Json& body);
inline void Validate_RemoteMethodSymbols(const Json& body);
inline void Validate_RemoteSequencePoint(const Json& body);
inline void Validate_RemoteTypeSymbols(const Json& body);
inline void Validate_RemoteFieldSymbols(const Json& body);
inline void Validate_RemoteEvent(const Json& body);
inline void Validate_RemoteThread(const Json& body);
inline void Validate_RemoteModule(const Json& body);
inline void Validate_RemoteScope(const Json& body);
inline void Validate_RemoteVariables(const Json& body);
inline void Validate_RemoteStep(const Json& body);
inline void Validate_RemoteExceptions(const Json& body);
inline void Validate_RemoteException(const Json& body);
inline void Validate_RemoteHello(const Json& body) {
    if (!body.is_object()) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Token").is_string())) throw std::runtime_error("MalformedPayload");
}
inline void Validate_RemoteCapabilities(const Json& body) {
    if (!body.is_object()) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Runtime").is_string())) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Platform").is_string())) throw std::runtime_error("MalformedPayload");
    if (!(body.at("ManagedDebugging").is_boolean())) throw std::runtime_error("MalformedPayload");
    if (body.contains("Blocker") && !body.at("Blocker").is_null()) {
        if (!(body.at("Blocker").is_string())) throw std::runtime_error("MalformedPayload");
    }
    if (body.contains("Bootstrap") && !body.at("Bootstrap").is_null()) {
        if (!(body.at("Bootstrap").is_string())) throw std::runtime_error("MalformedPayload");
    }
    if (body.contains("SupportedBuild") && !body.at("SupportedBuild").is_null()) {
        if (!(body.at("SupportedBuild").is_object())) throw std::runtime_error("MalformedPayload");
    }
    if (body.contains("Scope") && !body.at("Scope").is_null()) {
        if (!(body.at("Scope").is_string())) throw std::runtime_error("MalformedPayload");
    }
    if (body.contains("ModelVersion")) {
        if (!(ContractInteger(body.at("ModelVersion"), -2147483648LL, 2147483647LL))) throw std::runtime_error("MalformedPayload");
    }
    if (body.contains("Provider") && !body.at("Provider").is_null()) {
        if (!(body.at("Provider").is_string())) throw std::runtime_error("MalformedPayload");
    }
    if (body.contains("Features") && !body.at("Features").is_null()) {
        if (!body.at("Features").is_array()) throw std::runtime_error("MalformedPayload");
        for (const auto& element : body.at("Features")) {
            if (!(element.is_string())) throw std::runtime_error("MalformedPayload");
        }
    }
}
inline void Validate_RemoteEndpoint(const Json& body) {
    if (!body.is_object()) throw std::runtime_error("MalformedPayload");
    if (!(ContractInteger(body.at("Port"), -2147483648LL, 2147483647LL))) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Token").is_string())) throw std::runtime_error("MalformedPayload");
    if (body.contains("SessionId") && !body.at("SessionId").is_null()) {
        if (!(body.at("SessionId").is_string())) throw std::runtime_error("MalformedPayload");
    }
    if (body.contains("ProcessId")) {
        if (!(ContractInteger(body.at("ProcessId"), -2147483648LL, 2147483647LL))) throw std::runtime_error("MalformedPayload");
    }
}
inline void Validate_RemoteBreakpoints(const Json& body) {
    if (!body.is_object()) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Source").is_string())) throw std::runtime_error("MalformedPayload");
    if (!body.at("Lines").is_array()) throw std::runtime_error("MalformedPayload");
    for (const auto& element : body.at("Lines")) {
        if (!(ContractInteger(element, -2147483648LL, 2147483647LL))) throw std::runtime_error("MalformedPayload");
    }
    if (body.contains("Location") && !body.at("Location").is_null()) {
        Validate_RemoteManagedLocation(body.at("Location"));
    }
    if (body.contains("Locations") && !body.at("Locations").is_null()) {
        if (!body.at("Locations").is_array()) throw std::runtime_error("MalformedPayload");
        for (const auto& element : body.at("Locations")) {
            Validate_RemoteManagedLocation(element);
        }
    }
}
inline void Validate_RemoteManagedLocation(const Json& body) {
    if (!body.is_object()) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Mvid").is_string())) throw std::runtime_error("MalformedPayload");
    if (!(ContractInteger(body.at("MethodToken"), -2147483648LL, 2147483647LL))) throw std::runtime_error("MalformedPayload");
    if (!(ContractInteger(body.at("IlOffset"), -2147483648LL, 2147483647LL))) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Method").is_string())) throw std::runtime_error("MalformedPayload");
    if (!body.at("Variables").is_array()) throw std::runtime_error("MalformedPayload");
    for (const auto& element : body.at("Variables")) {
        Validate_RemoteManagedVariable(element);
    }
}
inline void Validate_RemoteManagedVariable(const Json& body) {
    if (!body.is_object()) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Name").is_string())) throw std::runtime_error("MalformedPayload");
    if (!(ContractInteger(body.at("Index"), -2147483648LL, 2147483647LL))) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Parameter").is_boolean())) throw std::runtime_error("MalformedPayload");
    if (body.contains("StartOffset")) {
        if (!(ContractInteger(body.at("StartOffset"), -2147483648LL, 2147483647LL))) throw std::runtime_error("MalformedPayload");
    }
    if (body.contains("EndOffset")) {
        if (!(ContractInteger(body.at("EndOffset"), -2147483648LL, 2147483647LL))) throw std::runtime_error("MalformedPayload");
    }
}
inline void Validate_RemoteBreakpoint(const Json& body) {
    if (!body.is_object()) throw std::runtime_error("MalformedPayload");
    if (!(ContractInteger(body.at("Line"), -2147483648LL, 2147483647LL))) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Verified").is_boolean())) throw std::runtime_error("MalformedPayload");
    if (body.contains("Message") && !body.at("Message").is_null()) {
        if (!(body.at("Message").is_string())) throw std::runtime_error("MalformedPayload");
    }
}
inline void Validate_RemoteStop(const Json& body) {
    if (!body.is_object()) throw std::runtime_error("MalformedPayload");
    if (!(ContractInteger(body.at("Id"), (-9223372036854775807LL - 1), 9223372036854775807LL))) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Reason").is_string())) throw std::runtime_error("MalformedPayload");
    if (!(ContractInteger(body.at("ThreadId"), -2147483648LL, 2147483647LL))) throw std::runtime_error("MalformedPayload");
}
inline void Validate_RemoteFrame(const Json& body) {
    if (!body.is_object()) throw std::runtime_error("MalformedPayload");
    if (!(ContractInteger(body.at("Handle"), (-9223372036854775807LL - 1), 9223372036854775807LL))) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Name").is_string())) throw std::runtime_error("MalformedPayload");
    if (body.contains("Source") && !body.at("Source").is_null()) {
        if (!(body.at("Source").is_string())) throw std::runtime_error("MalformedPayload");
    }
    if (!(ContractInteger(body.at("Line"), -2147483648LL, 2147483647LL))) throw std::runtime_error("MalformedPayload");
    if (body.contains("StopId")) {
        if (!(ContractInteger(body.at("StopId"), (-9223372036854775807LL - 1), 9223372036854775807LL))) throw std::runtime_error("MalformedPayload");
    }
    if (body.contains("Mvid") && !body.at("Mvid").is_null()) {
        if (!(body.at("Mvid").is_string())) throw std::runtime_error("MalformedPayload");
    }
    if (body.contains("MethodToken")) {
        if (!(ContractInteger(body.at("MethodToken"), -2147483648LL, 2147483647LL))) throw std::runtime_error("MalformedPayload");
    }
    if (body.contains("IlOffset")) {
        if (!(ContractInteger(body.at("IlOffset"), -2147483648LL, 2147483647LL))) throw std::runtime_error("MalformedPayload");
    }
}
inline void Validate_RemoteEvaluation(const Json& body) {
    if (!body.is_object()) throw std::runtime_error("MalformedPayload");
    if (!(ContractInteger(body.at("Frame"), (-9223372036854775807LL - 1), 9223372036854775807LL))) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Expression").is_string())) throw std::runtime_error("MalformedPayload");
}
inline void Validate_RemoteValue(const Json& body) {
    if (!body.is_object()) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Value").is_string())) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Type").is_string())) throw std::runtime_error("MalformedPayload");
    if (body.contains("Handle")) {
        if (!(ContractInteger(body.at("Handle"), (-9223372036854775807LL - 1), 9223372036854775807LL))) throw std::runtime_error("MalformedPayload");
    }
    if (body.contains("Name") && !body.at("Name").is_null()) {
        if (!(body.at("Name").is_string())) throw std::runtime_error("MalformedPayload");
    }
    if (body.contains("IndexedVariables") && !body.at("IndexedVariables").is_null()) {
        if (!(ContractInteger(body.at("IndexedVariables"), -2147483648LL, 2147483647LL))) throw std::runtime_error("MalformedPayload");
    }
    if (body.contains("NamedVariables") && !body.at("NamedVariables").is_null()) {
        if (!(ContractInteger(body.at("NamedVariables"), -2147483648LL, 2147483647LL))) throw std::runtime_error("MalformedPayload");
    }
    if (body.contains("EvaluateName") && !body.at("EvaluateName").is_null()) {
        if (!(body.at("EvaluateName").is_string())) throw std::runtime_error("MalformedPayload");
    }
}
inline void Validate_RemoteHandles(const Json& body) {
    if (!body.is_object()) throw std::runtime_error("MalformedPayload");
    if (!body.at("Handles").is_array()) throw std::runtime_error("MalformedPayload");
    for (const auto& element : body.at("Handles")) {
        if (!(ContractInteger(element, (-9223372036854775807LL - 1), 9223372036854775807LL))) throw std::runtime_error("MalformedPayload");
    }
}
inline void Validate_RemoteId(const Json& body) {
    if (!body.is_object()) throw std::runtime_error("MalformedPayload");
    if (!(ContractInteger(body.at("Id"), (-9223372036854775807LL - 1), 9223372036854775807LL))) throw std::runtime_error("MalformedPayload");
}
inline void Validate_RemoteEmpty(const Json& body) {
    if (!body.is_object()) throw std::runtime_error("MalformedPayload");
}
inline void Validate_RemoteConfiguration(const Json& body) {
    if (!body.is_object()) throw std::runtime_error("MalformedPayload");
    if (!body.at("Modules").is_array()) throw std::runtime_error("MalformedPayload");
    for (const auto& element : body.at("Modules")) {
        Validate_RemoteModuleSymbols(element);
    }
    if (body.contains("Append")) {
        if (!(body.at("Append").is_boolean())) throw std::runtime_error("MalformedPayload");
    }
}
inline void Validate_RemoteModuleSymbols(const Json& body) {
    if (!body.is_object()) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Mvid").is_string())) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Name").is_string())) throw std::runtime_error("MalformedPayload");
    if (!body.at("Methods").is_array()) throw std::runtime_error("MalformedPayload");
    for (const auto& element : body.at("Methods")) {
        Validate_RemoteMethodSymbols(element);
    }
    if (!body.at("Types").is_array()) throw std::runtime_error("MalformedPayload");
    for (const auto& element : body.at("Types")) {
        Validate_RemoteTypeSymbols(element);
    }
}
inline void Validate_RemoteMethodSymbols(const Json& body) {
    if (!body.is_object()) throw std::runtime_error("MalformedPayload");
    if (!(ContractInteger(body.at("Token"), -2147483648LL, 2147483647LL))) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Name").is_string())) throw std::runtime_error("MalformedPayload");
    if (!body.at("Points").is_array()) throw std::runtime_error("MalformedPayload");
    for (const auto& element : body.at("Points")) {
        Validate_RemoteSequencePoint(element);
    }
    if (!body.at("Variables").is_array()) throw std::runtime_error("MalformedPayload");
    for (const auto& element : body.at("Variables")) {
        Validate_RemoteManagedVariable(element);
    }
}
inline void Validate_RemoteSequencePoint(const Json& body) {
    if (!body.is_object()) throw std::runtime_error("MalformedPayload");
    if (!(ContractInteger(body.at("Offset"), -2147483648LL, 2147483647LL))) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Source").is_string())) throw std::runtime_error("MalformedPayload");
    if (!(ContractInteger(body.at("Line"), -2147483648LL, 2147483647LL))) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Checksum").is_string())) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Algorithm").is_string())) throw std::runtime_error("MalformedPayload");
}
inline void Validate_RemoteTypeSymbols(const Json& body) {
    if (!body.is_object()) throw std::runtime_error("MalformedPayload");
    if (!(ContractInteger(body.at("Token"), -2147483648LL, 2147483647LL))) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Name").is_string())) throw std::runtime_error("MalformedPayload");
    if (!body.at("Fields").is_array()) throw std::runtime_error("MalformedPayload");
    for (const auto& element : body.at("Fields")) {
        Validate_RemoteFieldSymbols(element);
    }
}
inline void Validate_RemoteFieldSymbols(const Json& body) {
    if (!body.is_object()) throw std::runtime_error("MalformedPayload");
    if (!(ContractInteger(body.at("Token"), -2147483648LL, 2147483647LL))) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Name").is_string())) throw std::runtime_error("MalformedPayload");
}
inline void Validate_RemoteEvent(const Json& body) {
    if (!body.is_object()) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Kind").is_string())) throw std::runtime_error("MalformedPayload");
    if (!(ContractInteger(body.at("Generation"), (-9223372036854775807LL - 1), 9223372036854775807LL))) throw std::runtime_error("MalformedPayload");
    if (body.contains("Stop") && !body.at("Stop").is_null()) {
        Validate_RemoteStop(body.at("Stop"));
    }
    if (body.contains("Thread") && !body.at("Thread").is_null()) {
        Validate_RemoteThread(body.at("Thread"));
    }
    if (body.contains("Module") && !body.at("Module").is_null()) {
        Validate_RemoteModule(body.at("Module"));
    }
    if (body.contains("Breakpoint") && !body.at("Breakpoint").is_null()) {
        Validate_RemoteBreakpoint(body.at("Breakpoint"));
    }
    if (body.contains("ExitCode") && !body.at("ExitCode").is_null()) {
        if (!(ContractInteger(body.at("ExitCode"), -2147483648LL, 2147483647LL))) throw std::runtime_error("MalformedPayload");
    }
}
inline void Validate_RemoteThread(const Json& body) {
    if (!body.is_object()) throw std::runtime_error("MalformedPayload");
    if (!(ContractInteger(body.at("Id"), -2147483648LL, 2147483647LL))) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Name").is_string())) throw std::runtime_error("MalformedPayload");
}
inline void Validate_RemoteModule(const Json& body) {
    if (!body.is_object()) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Id").is_string())) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Name").is_string())) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Path").is_string())) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Mvid").is_string())) throw std::runtime_error("MalformedPayload");
    if (!(ContractInteger(body.at("Generation"), (-9223372036854775807LL - 1), 9223372036854775807LL))) throw std::runtime_error("MalformedPayload");
}
inline void Validate_RemoteScope(const Json& body) {
    if (!body.is_object()) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Name").is_string())) throw std::runtime_error("MalformedPayload");
    if (!(ContractInteger(body.at("Handle"), (-9223372036854775807LL - 1), 9223372036854775807LL))) throw std::runtime_error("MalformedPayload");
    if (!(ContractInteger(body.at("Count"), -2147483648LL, 2147483647LL))) throw std::runtime_error("MalformedPayload");
}
inline void Validate_RemoteVariables(const Json& body) {
    if (!body.is_object()) throw std::runtime_error("MalformedPayload");
    if (!(ContractInteger(body.at("Handle"), (-9223372036854775807LL - 1), 9223372036854775807LL))) throw std::runtime_error("MalformedPayload");
    if (body.contains("Start")) {
        if (!(ContractInteger(body.at("Start"), -2147483648LL, 2147483647LL))) throw std::runtime_error("MalformedPayload");
    }
    if (body.contains("Count")) {
        if (!(ContractInteger(body.at("Count"), -2147483648LL, 2147483647LL))) throw std::runtime_error("MalformedPayload");
    }
    if (body.contains("Filter") && !body.at("Filter").is_null()) {
        if (!(body.at("Filter").is_string())) throw std::runtime_error("MalformedPayload");
    }
}
inline void Validate_RemoteStep(const Json& body) {
    if (!body.is_object()) throw std::runtime_error("MalformedPayload");
    if (!(ContractInteger(body.at("Stop"), (-9223372036854775807LL - 1), 9223372036854775807LL))) throw std::runtime_error("MalformedPayload");
    if (!(ContractInteger(body.at("ThreadId"), -2147483648LL, 2147483647LL))) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Kind").is_string())) throw std::runtime_error("MalformedPayload");
}
inline void Validate_RemoteExceptions(const Json& body) {
    if (!body.is_object()) throw std::runtime_error("MalformedPayload");
    if (!body.at("Filters").is_array()) throw std::runtime_error("MalformedPayload");
    for (const auto& element : body.at("Filters")) {
        if (!(element.is_string())) throw std::runtime_error("MalformedPayload");
    }
    if (body.contains("Types") && !body.at("Types").is_null()) {
        if (!body.at("Types").is_array()) throw std::runtime_error("MalformedPayload");
        for (const auto& element : body.at("Types")) {
            if (!(element.is_string())) throw std::runtime_error("MalformedPayload");
        }
    }
}
inline void Validate_RemoteException(const Json& body) {
    if (!body.is_object()) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Type").is_string())) throw std::runtime_error("MalformedPayload");
    if (!(body.at("Description").is_string())) throw std::runtime_error("MalformedPayload");
    if (!(body.at("BreakMode").is_string())) throw std::runtime_error("MalformedPayload");
}
inline void ValidateRequest(int operation, const Json& body) {
    switch (operation) {
        case 1: Validate_RemoteHello(body); break;
        case 2: Validate_RemoteBreakpoints(body); break;
        case 3: Validate_RemoteEmpty(body); break;
        case 4: Validate_RemoteEmpty(body); break;
        case 5: Validate_RemoteId(body); break;
        case 6: Validate_RemoteEvaluation(body); break;
        case 7: Validate_RemoteId(body); break;
        case 8: Validate_RemoteHandles(body); break;
        case 9: Validate_RemoteEmpty(body); break;
        case 10: Validate_RemoteConfiguration(body); break;
        case 11: Validate_RemoteEmpty(body); break;
        case 12: Validate_RemoteEmpty(body); break;
        case 13: Validate_RemoteEmpty(body); break;
        case 14: Validate_RemoteId(body); break;
        case 15: Validate_RemoteVariables(body); break;
        case 16: Validate_RemoteStep(body); break;
        case 17: Validate_RemoteEmpty(body); break;
        case 18: Validate_RemoteExceptions(body); break;
        case 19: Validate_RemoteId(body); break;
        default: break;
    }
}
}
