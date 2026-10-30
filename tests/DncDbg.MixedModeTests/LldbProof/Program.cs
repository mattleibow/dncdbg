using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text.Json;
using ClrDebug;
using DncDbg.Protocol;

if (args is not [var adapter, var fixture, var sources, var dotnet])
    throw new ArgumentException("Supply lldb-dap, Fixture.dll, fixture source directory and dotnet host.");
if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
    throw new PlatformNotSupportedException("The initial read-only CLR proof targets macOS arm64.");

using var peer = new NativePeer(Path.GetFullPath(adapter));
peer.Request("initialize", new { adapterID = "lldb", pathFormat = "path", linesStartAt1 = true, columnsStartAt1 = true });
Task<DapMessage> launch = peer.Send("launch", new
{
    program = dotnet, args = new[] { Path.GetFullPath(fixture) },
    env = new Dictionary<string, string> { ["DNCDBG_MIXED_CHECKPOINTS"] = "1" },
});
peer.Event("initialized");
string nativeSource = Path.GetFullPath(Path.Combine(sources, "native.c"));
int nativeLine = Array.FindIndex(File.ReadAllLines(nativeSource), l => l.Contains("NATIVE_BREAK", StringComparison.Ordinal)) + 1;
int checkpointLine = Array.FindIndex(File.ReadAllLines(nativeSource), l => l.Contains("NATIVE_CHECKPOINT", StringComparison.Ordinal)) + 1;
peer.Request("setBreakpoints", new { source = new { path = nativeSource }, breakpoints = new[] { new { line = nativeLine }, new { line = checkpointLine } } });
peer.Request("configurationDone", new { });
NativePeer.Success(launch.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult());
string shimPath = Path.Combine(AppContext.BaseDirectory, "runtimes", "osx-arm64", "native", "libdbgshim.dylib");
if (!File.Exists(shimPath))
    shimPath = Path.Combine(AppContext.BaseDirectory, "libdbgshim.dylib");
var shim = new DbgShim(NativeLibrary.Load(shimPath));
CLRDebugging clr = shim.CLRCreateInstance().CLRDebugging;
using var pe = new PEReader(File.OpenRead(fixture));
MetadataReader metadata = pe.GetMetadataReader();
MethodDefinitionHandle method = metadata.MethodDefinitions.Single(h =>
    metadata.GetString(metadata.GetMethodDefinition(h).Name) == "ManagedCall");
int methodToken = MetadataTokens.GetToken(method);
using var pdb = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(Path.ChangeExtension(fixture, ".pdb")));
MetadataReader symbols = pdb.GetMetadataReader();
int resultSlot = symbols.GetLocalScopes(method).SelectMany(s => symbols.GetLocalScope(s).GetLocalVariables())
    .Select(symbols.GetLocalVariable).Single(v => symbols.GetString(v.Name) == "result").Index;

for (int visit = 0; visit < 3; visit++)
{
    JsonElement stop = peer.Event("stopped");
    int threadId = stop.GetProperty("threadId").GetInt32();
    JsonElement nativeFrames = peer.Request("stackTrace", new { threadId }).GetProperty("stackFrames");
    JsonElement nativeFrame = nativeFrames[0];
    int expectedLine = visit == 1 ? nativeLine : checkpointLine;
    Require(nativeFrame.GetProperty("line").GetInt32() == expectedLine, "Unexpected native source stop.");
    var locals = NativeLocals(nativeFrame.GetProperty("id").GetInt32());
    Require(visit == 1 ? locals["left"] == "19" && locals["right"] == "23"
        : locals["phase"] == (visit == 0 ? "1" : "2") && locals["result"] == "42", "Native arguments differ.");

    JsonElement module = peer.Request("modules", new { }).GetProperty("modules").EnumerateArray()
        .Single(m => m.GetProperty("name").GetString() == "libcoreclr.dylib");
    string runtime = module.GetProperty("path").GetString()!;
    string baseText = module.GetProperty("addressRange").GetString()!.Split(' ')[0];
    long moduleBase = long.Parse(baseText.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    var data = new ReadOnlyTarget(peer);
    var libraries = new LocalLibraries(Path.GetDirectoryName(runtime)!);
    var maximum = new CLR_DEBUGGING_VERSION { wMajor = 4, wMinor = 0, wBuild = ushort.MaxValue, wRevision = ushort.MaxValue };
    var actual = new CLR_DEBUGGING_VERSION();
    OpenVirtualProcessResult opened = clr.OpenVirtualProcess(moduleBase, data, libraries, maximum,
        typeof(ICorDebugProcess).GUID, ref actual);
    var process = new CorDebugProcess((ICorDebugProcess)opened.ppProcess);
    CorDebugThread thread = process.Threads.Single(t => t.Id == threadId);
    CorDebugStackWalk walk = thread.CreateStackWalk();
    bool sawManaged = false;
    do
    {
        HRESULT found = walk.TryGetFrame(out CorDebugFrame frame);
        if (found == HRESULT.S_OK && frame is CorDebugILFrame il && il.Function.Token.Value == methodToken
            && il.Function.Module.Name == Path.GetFullPath(fixture))
        {
            sawManaged = true;
            var native = new CorDebugNativeFrame((ICorDebugNativeFrame)il.Raw);
            ulong address = native.Code.Address.Value + (ulong)native.IP;
            Require(nativeFrames.EnumerateArray().Any(f => ulong.Parse(f.GetProperty("instructionPointerReference")
                .GetString()!.AsSpan(2), NumberStyles.HexNumber) == address), "Managed/native frame PCs do not match.");
            Require(ReadInt(il.GetArgument(0)) == 19 && ReadInt(il.GetArgument(1)) == 23, "Managed arguments differ.");
            if (visit == 2)
                Require(ReadInt(il.GetLocalVariable(resultSlot)) == 42, "Managed result is stale or incorrect.");
            Console.WriteLine($"PASS visit {visit}: managed source/PC identity, arguments 19/23"
                + (visit == 2 ? ", managed result=42" : ""));
        }
        else if (found is not (HRESULT.S_FALSE or HRESULT.S_OK))
            found.ThrowOnNotOK();
    } while (walk.TryNext() == HRESULT.S_OK);
    Require(sawManaged, "No managed caller was inspected.");
    process.Detach();
    GC.KeepAlive(data);
    GC.KeepAlive(libraries);
    if (visit == 1)
    {
        peer.Request("next", new { threadId });
        JsonElement stepped = peer.Event("stopped");
        JsonElement top = peer.Request("stackTrace", new { threadId }).GetProperty("stackFrames")[0];
        Require(NativeLocals(top.GetProperty("id").GetInt32())["sum"] == "42", "LLDB native stepping/locals failed.");
        Require(stepped.GetProperty("threadId").GetInt32() == threadId, "Native step changed threads.");
        Console.WriteLine("PASS LLDB native source step and sum=42");
    }
    peer.Request("continue", new { threadId });
}
Require(peer.Event("exited").GetProperty("exitCode").GetInt32() == 0, "Target did not exit cleanly.");
peer.Request("disconnect", new { });
Console.WriteLine("PASS LLDB-only execution + read-only CLR before/native/after inspection and clean exit");

Dictionary<string, string> NativeLocals(int frame)
{
    JsonElement scope = peer.Request("scopes", new { frameId = frame }).GetProperty("scopes").EnumerateArray()
        .Single(s => s.GetProperty("name").GetString() == "Locals");
    return peer.Request("variables", new { variablesReference = scope.GetProperty("variablesReference").GetInt32() })
        .GetProperty("variables").EnumerateArray().ToDictionary(v => v.GetProperty("name").GetString()!, v => v.GetProperty("value").GetString()!);
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static unsafe int ReadInt(CorDebugValue value)
{
    int result = 0;
    ((CorDebugGenericValue)value).GetValue((IntPtr)(&result));
    return result;
}

internal sealed class NativePeer : IDisposable
{
    private readonly Process _process;
    private readonly DapConnection _connection;
    private readonly BlockingCollection<DapMessage> _events = [];
    private readonly List<DapMessage> _saved = [];
    private readonly Task _reader;
    private bool _disconnected;

    public NativePeer(string executable)
    {
        _process = Process.Start(new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Cannot start native adapter.");
        _connection = new DapConnection(_process.StandardOutput.BaseStream, _process.StandardInput.BaseStream);
        _ = Task.Run(async () => Console.Error.Write(await _process.StandardError.ReadToEndAsync()));
        _reader = Task.Run(() =>
        {
            try
            {
                while (_connection.Read() is { } message)
                    if (message.Type == "response")
                        _connection.CompleteRequest(message);
                    else
                        _events.Add(message);
            }
            finally { _connection.CancelPendingRequests(); _events.CompleteAdding(); }
        });
    }

    public Task<DapMessage> Send(string command, object arguments) => _connection.SendRequestAsync(command, arguments);

    public JsonElement Request(string command, object arguments)
    {
        JsonElement result = Success(Send(command, arguments).WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult());
        _disconnected |= command == "disconnect";
        return result;
    }

    public static JsonElement Success(DapMessage response)
    {
        if (response.Success != true)
            throw new InvalidOperationException($"{response.Command}: {response.Message}: {response.Body}");
        return response.Body is JsonElement body ? body : JsonSerializer.SerializeToElement(new { });
    }

    public JsonElement Event(string name)
    {
        int saved = _saved.FindIndex(m => m.Event == name);
        if (saved >= 0)
        {
            DapMessage message = _saved[saved];
            _saved.RemoveAt(saved);
            return message.Body is JsonElement body ? body : JsonSerializer.SerializeToElement(new { });
        }
        while (_events.TryTake(out DapMessage? message, TimeSpan.FromSeconds(20)))
        {
            if (message.Event == name)
                return message.Body is JsonElement body ? body : JsonSerializer.SerializeToElement(new { });
            _saved.Add(message);
        }
        throw new TimeoutException($"Missing native event {name}.");
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            try { if (!_disconnected) Request("disconnect", new { terminateDebuggee = true }); }
            catch (Exception e) when (e is InvalidOperationException or TimeoutException or TaskCanceledException)
            { Console.Error.WriteLine("Native cleanup: " + e.Message); }
            if (!_process.WaitForExit(5000))
                _process.Kill(entireProcessTree: true);
        }
        _connection.Dispose();
        _reader.Wait(TimeSpan.FromSeconds(5));
        _process.Dispose();
    }
}

[GeneratedComClass]
internal sealed partial class ReadOnlyTarget(NativePeer peer) : ICorDebugDataTarget
{
    public HRESULT GetPlatform(out CorDebugPlatform platform)
    {
        // CORDB_PLATFORM_POSIX_ARM64 in the public .NET 10 cordebug.idl; ClrDebug's enum predates it.
        platform = (CorDebugPlatform)11;
        return HRESULT.S_OK;
    }

    public HRESULT ReadVirtual(CORDB_ADDRESS address, IntPtr buffer, int requested, out int read)
    {
        read = 0;
        try
        {
            JsonElement body = peer.Request("readMemory", new { memoryReference = $"0x{address.Value:x}", count = requested });
            byte[] bytes = Convert.FromBase64String(body.GetProperty("data").GetString()!);
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            read = bytes.Length;
            return bytes.Length > 0 ? HRESULT.S_OK : HRESULT.E_FAIL;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Memory read 0x{address.Value:x}/{requested}: {error.Message}");
            return HRESULT.E_FAIL;
        }
    }

    public unsafe HRESULT GetThreadContext(int id, ContextFlags flags, int size, IntPtr context)
    {
        Console.WriteLine($"CONTEXT requested thread {id}, flags={flags}, bytes={size}");
        try
        {
            int frame = peer.Request("stackTrace", new { threadId = id, levels = 1 }).GetProperty("stackFrames")[0]
                .GetProperty("id").GetInt32();
            JsonElement registers = peer.Request("scopes", new { frameId = frame }).GetProperty("scopes").EnumerateArray()
                .Single(s => s.GetProperty("name").GetString() == "Registers");
            JsonElement groups = peer.Request("variables", new { variablesReference = registers.GetProperty("variablesReference").GetInt32() })
                .GetProperty("variables");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonElement group in groups.EnumerateArray())
                foreach (JsonElement register in peer.Request("variables", new { variablesReference = group.GetProperty("variablesReference").GetInt32() })
                    .GetProperty("variables").EnumerateArray())
                    values.Add(register.GetProperty("name").GetString()!, register.GetProperty("value").GetString()!);
            if (size < 0x318)
                return HRESULT.E_INVALIDARG;
            new Span<byte>((void*)context, size).Clear();
            Marshal.WriteInt32(context, unchecked((int)(flags & ~ContextFlags.ARM64ContextDebugRegisters | ContextFlags.ARM64Context)));
            for (int i = 0; i < 29; i++)
                Marshal.WriteInt64(context, 8 + i * 8, unchecked((long)Number("x" + i)));
            Marshal.WriteInt64(context, 0xf0, unchecked((long)Number("fp")));
            Marshal.WriteInt64(context, 0xf8, unchecked((long)Number("lr")));
            Marshal.WriteInt64(context, 0x100, unchecked((long)Number("sp")));
            Marshal.WriteInt64(context, 0x108, unchecked((long)Number("pc")));
            Marshal.WriteInt32(context, 4, unchecked((int)Number("cpsr")));
            for (int i = 0; i < 32; i++)
            {
                var bytes = System.Text.RegularExpressions.Regex.Matches(values["v" + i], @"0x([0-9a-fA-F]{2})");
                if (bytes.Count != 16)
                    throw new InvalidOperationException("Expected a 128-bit vector.");
                for (int j = 0; j < 16; j++)
                    Marshal.WriteByte(context, 0x110 + i * 16 + j, byte.Parse(bytes[j].Groups[1].Value, NumberStyles.HexNumber));
            }
            Marshal.WriteInt32(context, 0x310, unchecked((int)Number("fpcr")));
            Marshal.WriteInt32(context, 0x314, unchecked((int)Number("fpsr")));
            return HRESULT.S_OK;

            ulong Number(string name)
            {
                string value = values[name].Split(' ')[0];
                return ulong.Parse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Context read {id}: {error}");
            return HRESULT.E_FAIL;
        }
    }
}

[GeneratedComClass]
internal sealed partial class LocalLibraries(string directory) : ICLRDebuggingLibraryProvider, ICLRDebuggingLibraryProvider3
{
    public HRESULT ProvideLibrary(string name, uint timestamp, int size, out IntPtr module)
    {
        string path = Path.Combine(directory, Path.GetFileName(name));
        Console.WriteLine("LOAD " + path);
        return NativeLibrary.TryLoad(path, out module) ? HRESULT.S_OK : HRESULT.E_FAIL;
    }

    public HRESULT ProvideWindowsLibrary(string name, string runtime, LIBRARY_PROVIDER_INDEX_TYPE type,
        uint timestamp, int size, out IntPtr path)
    {
        path = IntPtr.Zero;
        return HRESULT.E_NOTIMPL;
    }

    public HRESULT ProvideUnixLibrary(string name, string runtime, LIBRARY_PROVIDER_INDEX_TYPE type,
        byte[] buildId, int size, out IntPtr path)
    {
        string file = Path.Combine(directory, Path.GetFileName(name));
        Console.WriteLine($"RESOLVE {type}: {file}");
        path = File.Exists(file) ? Marshal.StringToCoTaskMemUni(file) : IntPtr.Zero;
        return path != IntPtr.Zero ? HRESULT.S_OK : HRESULT.E_FAIL;
    }
}
