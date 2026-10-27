using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using ClrDebug;
using DncDbg.Engine.Launch;
using DncDbg.Engine.Symbols;
using DncDbg.Engine.Values;

namespace DncDbg.Engine;

public sealed record ManagedInspectionFrame(FrameInfo Frame, ulong InstructionPointer);

/// <summary>
/// A read-only view of an externally stopped CoreCLR process. The caller owns execution and must discard
/// the view before any resume or memory mutation. No managed debugger is attached to the target.
/// </summary>
public sealed class CoreClrInspection : IDisposable
{
    private static readonly Lazy<CLRDebugging> Debugger = new(() => DebugEngine.LoadDbgShim().CLRCreateInstance().CLRDebugging);
    private readonly ICorDebugDataTarget _target;
    private readonly InspectionLibraries _libraries;
    private readonly CorDebugProcess _process;
    private readonly Action<string>? _log;
    private readonly Dictionary<string, ModuleMetadata?> _metadata = new(StringComparer.Ordinal);
    private readonly Dictionary<int, CorDebugILFrame> _frames = [];
    private readonly Dictionary<int, IReadOnlyList<ManagedInspectionFrame>> _stacks = [];
    private int _nextFrame;
    private bool _disposed;

    public CoreClrInspection(ulong runtimeBase, ICorDebugDataTarget target, string? runtimeLibraryDirectory = null,
        Action<string>? log = null)
    {
        if (!BitConverter.IsLittleEndian)
            throw new DebuggerException("Read-only CoreCLR inspection currently requires a little-endian host.");
        if (runtimeLibraryDirectory != null && !Directory.Exists(runtimeLibraryDirectory))
            throw new DebuggerException($"Managed inspection runtime library directory '{runtimeLibraryDirectory}' does not exist.");
        _target = target;
        _log = log;
        _libraries = new InspectionLibraries(new DebuggingLibraryProvider(log, runtimeLibraryDirectory));
        var version = new CLR_DEBUGGING_VERSION();
        var maximum = new CLR_DEBUGGING_VERSION { wMajor = 4, wBuild = ushort.MaxValue, wRevision = ushort.MaxValue };
        OpenVirtualProcessResult opened = Debugger.Value.OpenVirtualProcess(checked((long)runtimeBase), target,
            _libraries, maximum, typeof(ICorDebugProcess).GUID, ref version);
        _process = new CorDebugProcess((ICorDebugProcess)opened.ppProcess);
    }

    public IReadOnlyList<ManagedInspectionFrame> GetStackTrace(int osThreadId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stacks.TryGetValue(osThreadId, out var stack))
            return stack;
        var result = new List<ManagedInspectionFrame>();
        CorDebugThread? thread = _process.Threads.SingleOrDefault(t => t.Id == osThreadId);
        if (thread == null)
            return _stacks[osThreadId] = result;
        CorDebugStackWalk walk = thread.CreateStackWalk();
        for (int depth = 0; depth < 1024; depth++)
        {
            HRESULT frameResult = walk.TryGetFrame(out CorDebugFrame frame);
            if (frameResult == HRESULT.S_OK && frame is CorDebugILFrame il)
            {
                ModuleMetadata? metadata = GetMetadata(il.Function.Module);
                int token = checked((int)il.Function.Token.Value);
                SourceLocation? source = metadata?.GetSourceLocation(token, il.IP.pnOffset, out _);
                string name = metadata?.GetMethodName(token) ?? $"<managed method 0x{token:x8}>";
                var native = new CorDebugNativeFrame((ICorDebugNativeFrame)il.Raw);
                ulong address = checked(native.Code.Address.Value + (ulong)native.IP);
                int id = ++_nextFrame;
                _frames.Add(id, il);
                var info = new FrameInfo(id, "[Managed] " + name, source?.Path, source?.Line ?? 0,
                    source?.Column ?? 0, source?.EndLine ?? 0, source?.EndColumn ?? 0);
                result.Add(new ManagedInspectionFrame(info, address));
            }
            else if (frameResult is not (HRESULT.S_OK or HRESULT.S_FALSE or HRESULT.CORDBG_E_MISSING_METADATA))
                frameResult.ThrowOnNotOK();
            HRESULT next = walk.TryNext();
            if (next is HRESULT.S_FALSE or HRESULT.CORDBG_S_AT_END_OF_STACK)
                return _stacks[osThreadId] = result;
            next.ThrowOnNotOK();
        }
        throw new DebuggerException("Managed inspection exceeded the 1024-frame stack limit.");
    }

    public IReadOnlyList<VariableInfo> GetVariables(int frameId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_frames.TryGetValue(frameId, out CorDebugILFrame? frame))
            throw new DebuggerException("The managed inspection frame is stale.");
        ModuleMetadata? metadata = GetMetadata(frame.Function.Module);
        int token = checked((int)frame.Function.Token.Value);
        var result = new Dictionary<string, VariableInfo>(StringComparer.Ordinal);
        (bool isStatic, string[] names) = metadata?.GetMethodParameters(token) ?? (true, []);
        int count = frame.Arguments.Length;
        for (int index = 0; index < count; index++)
        {
            int slot = index;
            int nameIndex = isStatic ? index : index - 1;
            string name = !isStatic && index == 0 ? "this"
                : nameIndex < names.Length ? names[nameIndex] : $"arg{index}";
            result[name] = Describe(name, () => frame.GetArgument(slot));
        }
        if (metadata != null)
            foreach ((int index, string name) in metadata.GetLocals(token, frame.IP.pnOffset))
                if (!name.StartsWith('<') && !name.StartsWith("CS$", StringComparison.Ordinal))
                    result[name] = Describe(name, () => frame.GetLocalVariable(index));
        return result.Values.ToArray();
    }

    public VariableInfo Evaluate(int frameId, string expression) =>
        GetVariables(frameId).FirstOrDefault(v => v.Name == expression)
        ?? throw new DebuggerException("Managed inspection is read-only and supports scalar local/argument names only, not calls or expressions.");

    private static VariableInfo Describe(string name, Func<CorDebugValue> getter)
    {
        try
        {
            CorDebugValue value = getter();
            if (value.Type is CorElementType.Boolean or CorElementType.Char or CorElementType.I1 or CorElementType.U1
                or CorElementType.I2 or CorElementType.U2 or CorElementType.I4 or CorElementType.U4 or CorElementType.I8
                or CorElementType.U8 or CorElementType.R4 or CorElementType.R8 or CorElementType.I or CorElementType.U)
            {
                (string text, string? type) = ValueInspector.FormatHost(ValueInspector.ReadPrimitive(value));
                return new VariableInfo { Name = name, Value = text, Type = type, EvaluateName = name };
            }
            return new VariableInfo { Name = name, Value = "<not supported by read-only scalar inspection>", Type = value.Type.ToString() };
        }
        catch (DebugException error)
        {
            return new VariableInfo { Name = name, Value = "<unavailable: " + ErrorText.Describe(error) + ">" };
        }
    }

    private ModuleMetadata? GetMetadata(CorDebugModule module)
    {
        string path = module.Name;
        if (_metadata.TryGetValue(path, out ModuleMetadata? existing))
            return existing;
        ModuleMetadata? metadata = ModuleMetadata.TryOpen(path, _log);
        if (metadata != null)
        {
            var import = new MetaDataImport((IMetaDataImport)module.GetMetaDataInterface(typeof(IMetaDataImport).GUID));
            if (import.ScopeProps.pmvid != metadata.ModuleVersionId)
            {
                metadata.Dispose();
                throw new DebuggerException($"Managed module '{path}' does not match the loaded module's MVID.");
            }
        }
        _metadata.Add(path, metadata);
        return metadata;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            // The data target implements no write/control interface, so this only releases the virtual view.
            HRESULT result = _process.TryDetach();
            if (result != HRESULT.S_OK)
                _log?.Invoke("Virtual CLR view cleanup: " + result);
        }
        finally
        {
            foreach (ModuleMetadata? metadata in _metadata.Values)
                metadata?.Dispose();
            _frames.Clear();
            _stacks.Clear();
            GC.KeepAlive(_target);
            GC.KeepAlive(_libraries);
        }
    }
}

[GeneratedComClass]
internal sealed partial class InspectionLibraries(DebuggingLibraryProvider provider)
    : ICLRDebuggingLibraryProvider, ICLRDebuggingLibraryProvider3
{
    public HRESULT ProvideLibrary(string name, uint timestamp, int size, out IntPtr module)
    {
        module = IntPtr.Zero;
        return HRESULT.E_NOTIMPL; // A build-identity-aware provider3 lookup is required.
    }

    public HRESULT ProvideWindowsLibrary(string name, string runtime, LIBRARY_PROVIDER_INDEX_TYPE type,
        uint timestamp, int size, out IntPtr path) =>
        provider.ProvideWindowsLibrary(name, runtime, type, timestamp, size, out path);

    public HRESULT ProvideUnixLibrary(string name, string runtime, LIBRARY_PROVIDER_INDEX_TYPE type,
        byte[] buildId, int size, out IntPtr path) =>
        provider.ProvideUnixLibrary(name, runtime, type, buildId, size, out path);
}
