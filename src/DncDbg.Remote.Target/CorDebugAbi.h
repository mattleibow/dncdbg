// Public signatures from dotnet/runtime src/coreclr/inc/cordebug.idl (MIT).
// Bootstrap topology adapted from JaneySprings/clrdbg; see THIRD-PARTY-NOTICES.md.
#pragma once
#include <cstdint>

namespace dncdbg {
using HResult = int32_t;
using Bool = int32_t;
struct Guid { uint32_t a; uint16_t b, c; uint8_t d[8]; };
static_assert(sizeof(Guid) == 16);
constexpr Guid UnknownId{0, 0, 0, {0xc0, 0, 0, 0, 0, 0, 0, 0x46}};
constexpr Guid CallbackId{0x3d6f5f60, 0x7538, 0x11d3, {0x8d, 0x5b, 0, 0x10, 0x4b, 0x35, 0xe7, 0xef}};
constexpr Guid Callback2Id{0x250e5eea, 0xdb5c, 0x4c76, {0xb6, 0xf3, 0x8c, 0x46, 0xf1, 0x2e, 0x32, 3}};
struct Unknown {
    virtual HResult QueryInterface(const Guid&, void**) = 0;
    virtual uint32_t AddRef() = 0;
    virtual uint32_t Release() = 0;
};
struct Controller : Unknown {
    virtual HResult Stop(uint32_t timeout) = 0;
    virtual HResult Continue(Bool outOfBand) = 0;
    virtual HResult IsRunning(Bool*) = 0;
    virtual HResult HasQueuedCallbacks(void* thread, Bool*) = 0;
    virtual HResult EnumerateThreads(void**) = 0;
    virtual HResult SetAllThreadsDebugState(uint32_t, void*) = 0;
    virtual HResult Detach() = 0;
    virtual HResult Terminate(uint32_t) = 0;
    virtual HResult CanCommitChanges(uint32_t, Unknown**, Unknown**) = 0;
    virtual HResult CommitChanges(uint32_t, Unknown**, Unknown**) = 0;
};
struct AppDomain : Controller {
    virtual HResult GetProcess(Controller**) = 0;
    virtual HResult EnumerateAssemblies(Unknown**) = 0;
    virtual HResult GetModuleFromMetaDataInterface(Unknown*, Unknown**) = 0;
    virtual HResult EnumerateBreakpoints(Unknown**) = 0;
    virtual HResult EnumerateSteppers(Unknown**) = 0;
    virtual HResult IsAttached(Bool*) = 0;
    virtual HResult GetName(uint32_t, uint32_t*, char16_t*) = 0;
    virtual HResult GetObject(Unknown**) = 0;
    virtual HResult Attach() = 0;
    virtual HResult GetID(uint32_t*) = 0;
};
struct Module;
struct Function;
struct Code;
struct Frame;
struct Value;
struct Breakpoint : Unknown {
    virtual HResult Activate(Bool) = 0;
    virtual HResult IsActive(Bool*) = 0;
};
struct Function : Unknown {
    virtual HResult GetModule(Module**) = 0;
    virtual HResult GetClass(Unknown**) = 0;
    virtual HResult GetToken(uint32_t*) = 0;
    virtual HResult GetILCode(Code**) = 0;
    virtual HResult GetNativeCode(Code**) = 0;
    virtual HResult CreateBreakpoint(Breakpoint**) = 0;
    virtual HResult GetLocalVarSigToken(uint32_t*) = 0;
    virtual HResult GetCurrentVersionNumber(uint32_t*) = 0;
};
struct Module : Unknown {
    virtual HResult GetProcess(Controller**) = 0;
    virtual HResult GetBaseAddress(uint64_t*) = 0;
    virtual HResult GetAssembly(Unknown**) = 0;
    virtual HResult GetName(uint32_t, uint32_t*, char16_t*) = 0;
    virtual HResult EnableJITDebugging(Bool, Bool) = 0;
    virtual HResult EnableClassLoadCallbacks(Bool) = 0;
    virtual HResult GetFunctionFromToken(uint32_t, Function**) = 0;
    virtual HResult GetFunctionFromRVA(uint64_t, Function**) = 0;
    virtual HResult GetClassFromToken(uint32_t, Unknown**) = 0;
    virtual HResult CreateBreakpoint(Unknown**) = 0;
    virtual HResult GetEditAndContinueSnapshot(Unknown**) = 0;
    virtual HResult GetMetaDataInterface(const Guid&, Unknown**) = 0;
};
struct Code : Unknown {
    virtual HResult IsIL(Bool*) = 0;
    virtual HResult GetFunction(Function**) = 0;
    virtual HResult GetAddress(uint64_t*) = 0;
    virtual HResult GetSize(uint32_t*) = 0;
    virtual HResult CreateBreakpoint(uint32_t, Breakpoint**) = 0;
};
struct Frame : Unknown {
    virtual HResult GetChain(Unknown**) = 0;
    virtual HResult GetCode(Code**) = 0;
    virtual HResult GetFunction(Function**) = 0;
    virtual HResult GetFunctionToken(uint32_t*) = 0;
    virtual HResult GetStackRange(uint64_t*, uint64_t*) = 0;
    virtual HResult GetCaller(Frame**) = 0;
    virtual HResult GetCallee(Frame**) = 0;
    virtual HResult CreateStepper(Unknown**) = 0;
};
struct ILFrame : Frame {
    virtual HResult GetIP(uint32_t*, uint32_t*) = 0;
    virtual HResult SetIP(uint32_t) = 0;
    virtual HResult EnumerateLocalVariables(Unknown**) = 0;
    virtual HResult GetLocalVariable(uint32_t, Value**) = 0;
    virtual HResult EnumerateArguments(Unknown**) = 0;
    virtual HResult GetArgument(uint32_t, Value**) = 0;
};
struct Enum : Unknown {
    virtual HResult Skip(uint32_t) = 0;
    virtual HResult Reset() = 0;
    virtual HResult Clone(Unknown**) = 0;
    virtual HResult GetCount(uint32_t*) = 0;
};
struct FrameEnum : Enum {
    virtual HResult Next(uint32_t, Frame**, uint32_t*) = 0;
};
struct Chain : Unknown {
    virtual HResult GetThread(Unknown**) = 0;
    virtual HResult GetStackRange(uint64_t*, uint64_t*) = 0;
    virtual HResult GetContext(Unknown**) = 0;
    virtual HResult GetCaller(Chain**) = 0;
    virtual HResult GetCallee(Chain**) = 0;
    virtual HResult GetPrevious(Chain**) = 0;
    virtual HResult GetNext(Chain**) = 0;
    virtual HResult IsManaged(Bool*) = 0;
    virtual HResult EnumerateFrames(FrameEnum**) = 0;
    virtual HResult GetActiveFrame(Frame**) = 0;
    virtual HResult GetRegisterSet(Unknown**) = 0;
    virtual HResult GetReason(uint32_t*) = 0;
};
struct ChainEnum : Enum {
    virtual HResult Next(uint32_t, Chain**, uint32_t*) = 0;
};
struct Thread : Unknown {
    virtual HResult GetProcess(Controller**) = 0;
    virtual HResult GetID(uint32_t*) = 0;
    virtual HResult GetHandle(void**) = 0;
    virtual HResult GetAppDomain(AppDomain**) = 0;
    virtual HResult SetDebugState(uint32_t) = 0;
    virtual HResult GetDebugState(uint32_t*) = 0;
    virtual HResult GetUserState(uint32_t*) = 0;
    virtual HResult GetCurrentException(Value**) = 0;
    virtual HResult ClearCurrentException() = 0;
    virtual HResult CreateStepper(Unknown**) = 0;
    virtual HResult EnumerateChains(Unknown**) = 0;
    virtual HResult GetActiveChain(Unknown**) = 0;
    virtual HResult GetActiveFrame(Frame**) = 0;
};
struct Value : Unknown {
    virtual HResult GetType(uint32_t*) = 0;
    virtual HResult GetSize(uint32_t*) = 0;
    virtual HResult GetAddress(uint64_t*) = 0;
    virtual HResult CreateBreakpoint(Unknown**) = 0;
};
struct GenericValue : Value {
    virtual HResult GetValue(void*) = 0;
    virtual HResult SetValue(void*) = 0;
};
struct StepRange { uint32_t start, end; };
struct Stepper : Unknown {
    virtual HResult IsActive(Bool*) = 0;
    virtual HResult Deactivate() = 0;
    virtual HResult SetInterceptMask(uint32_t) = 0;
    virtual HResult SetUnmappedStopMask(uint32_t) = 0;
    virtual HResult Step(Bool) = 0;
    virtual HResult StepRange(Bool, dncdbg::StepRange*, uint32_t) = 0;
    virtual HResult StepOut() = 0;
    virtual HResult SetRangeIL(Bool) = 0;
};
struct Class : Unknown {
    virtual HResult GetModule(Module**) = 0;
    virtual HResult GetToken(uint32_t*) = 0;
    virtual HResult GetStaticFieldValue(uint32_t, Frame*, Value**) = 0;
};
struct ReferenceValue : Value {
    virtual HResult IsNull(Bool*) = 0;
    virtual HResult GetValue(uint64_t*) = 0;
    virtual HResult SetValue(uint64_t) = 0;
    virtual HResult Dereference(Value**) = 0;
    virtual HResult DereferenceStrong(Value**) = 0;
};
struct HeapValue : Value {
    virtual HResult IsValid(Bool*) = 0;
    virtual HResult CreateRelocBreakpoint(Unknown**) = 0;
};
struct ObjectValue : Value {
    virtual HResult GetClass(Class**) = 0;
    virtual HResult GetFieldValue(Class*, uint32_t, Value**) = 0;
};
struct StringValue : HeapValue {
    virtual HResult GetLength(uint32_t*) = 0;
    virtual HResult GetString(uint32_t, uint32_t*, char16_t*) = 0;
};
struct ArrayValue : HeapValue {
    virtual HResult GetElementType(uint32_t*) = 0;
    virtual HResult GetRank(uint32_t*) = 0;
    virtual HResult GetCount(uint32_t*) = 0;
    virtual HResult GetDimensions(uint32_t, uint32_t*) = 0;
    virtual HResult HasBaseIndicies(Bool*) = 0;
    virtual HResult GetBaseIndicies(uint32_t, uint32_t*) = 0;
    virtual HResult GetElement(uint32_t, uint32_t*, Value**) = 0;
    virtual HResult GetElementAtPosition(uint32_t, Value**) = 0;
};
struct MetadataImport : Unknown {
    virtual void CloseEnum(void*) = 0;
    virtual HResult CountEnum(void*, uint32_t*) = 0;
    virtual HResult ResetEnum(void*, uint32_t) = 0;
    virtual HResult EnumTypeDefs(void**, uint32_t*, uint32_t, uint32_t*) = 0;
    virtual HResult EnumInterfaceImpls(void**, uint32_t, uint32_t*, uint32_t, uint32_t*) = 0;
    virtual HResult EnumTypeRefs(void**, uint32_t*, uint32_t, uint32_t*) = 0;
    virtual HResult FindTypeDefByName(const char16_t*, uint32_t, uint32_t*) = 0;
    virtual HResult GetScopeProps(char16_t*, uint32_t, uint32_t*, Guid*) = 0;
    virtual HResult GetModuleFromScope(uint32_t*) = 0;
    virtual HResult GetTypeDefProps(uint32_t, char16_t*, uint32_t, uint32_t*, uint32_t*, uint32_t*) = 0;
};
struct CorDebug : Unknown {
    virtual HResult Initialize() = 0;
    virtual HResult Terminate() = 0;
    virtual HResult SetManagedHandler(Unknown*) = 0;
    virtual HResult SetUnmanagedHandler(Unknown*) = 0;
    virtual HResult CreateProcess(const char16_t*, char16_t*, void*, void*, Bool, uint32_t, void*, const char16_t*, void*, void*, uint32_t, Controller**) = 0;
    virtual HResult DebugActiveProcess(uint32_t pid, Bool native, Controller**) = 0;
};
struct ManagedCallback : Unknown {
    virtual HResult Breakpoint(Controller*, Unknown*, Unknown*) = 0;
    virtual HResult StepComplete(Controller*, Unknown*, Unknown*, uint32_t) = 0;
    virtual HResult Break(Controller*, Unknown*) = 0;
    virtual HResult Exception(Controller*, Unknown*, Bool) = 0;
    virtual HResult EvalComplete(Controller*, Unknown*, Unknown*) = 0;
    virtual HResult EvalException(Controller*, Unknown*, Unknown*) = 0;
    virtual HResult CreateProcess(Controller*) = 0;
    virtual HResult ExitProcess(Controller*) = 0;
    virtual HResult CreateThread(Controller*, Unknown*) = 0;
    virtual HResult ExitThread(Controller*, Unknown*) = 0;
    virtual HResult LoadModule(Controller*, Unknown*) = 0;
    virtual HResult UnloadModule(Controller*, Unknown*) = 0;
    virtual HResult LoadClass(Controller*, Unknown*) = 0;
    virtual HResult UnloadClass(Controller*, Unknown*) = 0;
    virtual HResult DebuggerError(Controller*, HResult, uint32_t) = 0;
    virtual HResult LogMessage(Controller*, Unknown*, int32_t, const char16_t*, const char16_t*) = 0;
    virtual HResult LogSwitch(Controller*, Unknown*, int32_t, uint32_t, const char16_t*, const char16_t*) = 0;
    virtual HResult CreateAppDomain(Controller*, Controller*) = 0;
    virtual HResult ExitAppDomain(Controller*, Controller*) = 0;
    virtual HResult LoadAssembly(Controller*, Unknown*) = 0;
    virtual HResult UnloadAssembly(Controller*, Unknown*) = 0;
    virtual HResult ControlCTrap(Controller*) = 0;
    virtual HResult NameChange(Controller*, Unknown*) = 0;
    virtual HResult UpdateModuleSymbols(Controller*, Unknown*, Unknown*) = 0;
    virtual HResult EditAndContinueRemap(Controller*, Unknown*, Unknown*, Bool) = 0;
    virtual HResult BreakpointSetError(Controller*, Unknown*, Unknown*, uint32_t) = 0;
};
struct ManagedCallback2 : Unknown {
    virtual HResult FunctionRemapOpportunity(Controller*, Unknown*, Unknown*, Unknown*, uint32_t) = 0;
    virtual HResult CreateConnection(Controller*, uint32_t, const char16_t*) = 0;
    virtual HResult ChangeConnection(Controller*, uint32_t) = 0;
    virtual HResult DestroyConnection(Controller*, uint32_t) = 0;
    virtual HResult Exception(Controller*, Unknown*, Unknown*, uint32_t, uint32_t, uint32_t) = 0;
    virtual HResult ExceptionUnwind(Controller*, Unknown*, uint32_t, uint32_t) = 0;
    virtual HResult FunctionRemapComplete(Controller*, Unknown*, Unknown*) = 0;
    virtual HResult MDANotification(Controller*, Unknown*, Unknown*) = 0;
};
}
