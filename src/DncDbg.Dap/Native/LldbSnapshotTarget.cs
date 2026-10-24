using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClrDebug;
using DncDbg.Engine;
using DncDbg.Protocol;

namespace DncDbg.Dap.Native;

/// <summary>Read-only CLR data target backed by the sole LLDB controller's standard DAP memory/register views.</summary>
[GeneratedComClass]
internal sealed partial class LldbSnapshotTarget(
    NativeAdapterSession session,
    Func<CancellationToken> operationToken,
    CancellationToken snapshotToken,
    Action<string> log) : ICorDebugDataTarget
{
    private readonly Dictionary<int, Dictionary<string, string>> _registers = [];
    private readonly Dictionary<ulong, byte[]> _pages = [];

    public HRESULT GetPlatform(out CorDebugPlatform platform)
    {
        // POSIX enum entries are public in .NET 10 cordebug.idl, but absent from ClrDebug 0.4.2.
        platform = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => OperatingSystem.IsWindows() ? CorDebugPlatform.CORDB_PLATFORM_WINDOWS_ARM64 : (CorDebugPlatform)11,
            Architecture.X64 => OperatingSystem.IsWindows() ? CorDebugPlatform.CORDB_PLATFORM_WINDOWS_AMD64 : (CorDebugPlatform)8,
            _ => throw new PlatformNotSupportedException("CoreCLR inspection supports matching-host x64/arm64 only."),
        };
        return HRESULT.S_OK;
    }

    public byte[] Read(ulong address, int count)
    {
        if (count is < 0 or > 16 * 1024 * 1024)
            throw new DebuggerException("Managed snapshot reads are limited to 16 MiB.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(snapshotToken, operationToken());
        cancellation.Token.ThrowIfCancellationRequested();
        byte[] result = new byte[count];
        int read = 0;
        while (read < count)
        {
            ulong current = checked(address + (ulong)read);
            ulong page = current & ~4095UL;
            int offset = (int)(current - page), take = Math.Min(count - read, 4096 - offset);
            if (!_pages.TryGetValue(page, out byte[]? bytes))
            {
                try { bytes = Fetch(page, 4096); }
                catch (DebuggerException) when (read > 0 && !cancellation.IsCancellationRequested) { break; }
                _pages.Add(page, bytes);
            }
            if (offset >= bytes.Length)
                break;
            int available = Math.Min(take, bytes.Length - offset);
            bytes.AsSpan(offset, available).CopyTo(result.AsSpan(read));
            read += available;
            cancellation.Token.ThrowIfCancellationRequested();
            if (available != take)
                break;
        }
        cancellation.Token.ThrowIfCancellationRequested();
        return read == count ? result : result.AsSpan(0, read).ToArray();

        byte[] Fetch(ulong start, int length)
        {
            JsonElement body = Body(session.Request("readMemory", new { memoryReference = $"0x{start:x}", count = length }, cancellation.Token));
            byte[] bytes = body.TryGetProperty("data", out var data) ? Convert.FromBase64String(data.GetString()!) : [];
            if (bytes.Length > length)
                throw new DebuggerException("Native adapter returned more memory than requested.");
            return bytes;
        }
    }

    public void ValidateRuntimeImage(ulong address)
    {
        if (!BitConverter.IsLittleEndian)
            throw new DebuggerException("Managed inspection requires a little-endian host.");
        byte[] header = Read(address, 64);
        if (header.Length < 64)
            throw new DebuggerException("The CoreCLR image header is unreadable.");
        bool arm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        bool valid;
        if (OperatingSystem.IsMacOS())
            valid = BinaryPrimitives.ReadUInt32LittleEndian(header) == 0xFEEDFACF
                && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) == (arm64 ? 0x0100000C : 0x01000007);
        else if (OperatingSystem.IsLinux())
            valid = header.AsSpan(0, 4).SequenceEqual("\u007fELF"u8) && header[4] == 2 && header[5] == 1
                && BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18)) == (arm64 ? 183 : 62);
        else if (OperatingSystem.IsWindows() && header[0] == 'M' && header[1] == 'Z')
        {
            int offset = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0x3c));
            if (offset is < 0 or > 1024 * 1024)
                throw new DebuggerException("Invalid CoreCLR PE header offset.");
            byte[] pe = Read(checked(address + (ulong)offset), 6);
            valid = pe.Length == 6 && pe.AsSpan(0, 4).SequenceEqual("PE\0\0"u8)
                && BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(4)) == (arm64 ? 0xAA64 : 0x8664);
        }
        else
            valid = false;
        if (!valid)
            throw new DebuggerException("Managed inspection requires a local CoreCLR image matching the host OS, architecture and endianness.");
    }

    public HRESULT ReadVirtual(CORDB_ADDRESS address, IntPtr buffer, int requested, out int read)
    {
        read = 0;
        try
        {
            if (requested is < 0 or > 16 * 1024 * 1024)
                return HRESULT.E_INVALIDARG;
            byte[] bytes = Read(address.Value, requested);
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            read = bytes.Length;
            return read > 0 || requested == 0 ? HRESULT.S_OK : HRESULT.E_FAIL;
        }
        catch (OperationCanceledException) { return HRESULT.E_ABORT; }
        catch (Exception error) when (error is DebuggerException or IOException or TimeoutException or JsonException or FormatException)
        {
            log($"Managed snapshot read 0x{address.Value:x}/{requested} failed: {error.Message}");
            return HRESULT.E_FAIL;
        }
    }

    public unsafe HRESULT GetThreadContext(int threadId, ContextFlags requestedFlags, int size, IntPtr context)
    {
        try
        {
            snapshotToken.ThrowIfCancellationRequested();
            operationToken().ThrowIfCancellationRequested();
            Dictionary<string, string> registers = Registers(threadId);
            if (size is < 0 or > 16384)
                return HRESULT.E_INVALIDARG;
            var bytes = new Span<byte>((void*)context, size);
            bytes.Clear();
            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            {
                if (size < 0x318)
                    return HRESULT.E_INVALIDARG;
                // Public PAL/WinNT ARM64 CONTEXT layout. Hardware-debug/SVE banks are not claimed.
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, ((uint)requestedFlags & 7) | 0x00400000);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], checked((uint)Number(registers, "cpsr")));
                for (int i = 0; i < 29; i++)
                    BinaryPrimitives.WriteUInt64LittleEndian(bytes[(8 + i * 8)..], Number(registers, "x" + i));
                foreach (var (name, offset) in new[]
                    {
                        (registers.ContainsKey("fp") ? "fp" : "x29", 0xf0),
                        (registers.ContainsKey("lr") ? "lr" : "x30", 0xf8), ("sp", 0x100), ("pc", 0x108),
                    })
                    BinaryPrimitives.WriteUInt64LittleEndian(bytes[offset..], Number(registers, name));
                if (((uint)requestedFlags & 4) != 0)
                {
                    for (int i = 0; i < 32; i++)
                        Vector(registers, "v" + i).CopyTo(bytes[(0x110 + i * 16)..]);
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes[0x310..], checked((uint)Number(registers, "fpcr")));
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes[0x314..], checked((uint)Number(registers, "fpsr")));
                }
            }
            else if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
            {
                if (size < sizeof(AMD64_CONTEXT))
                    return HRESULT.E_INVALIDARG;
                var value = new AMD64_CONTEXT
                {
                    ContextFlags = (ContextFlags)(((uint)requestedFlags & ~0x10u) | 0x00100000),
                    Rax = unchecked((long)Number(registers, "rax")), Rbx = unchecked((long)Number(registers, "rbx")),
                    Rcx = unchecked((long)Number(registers, "rcx")), Rdx = unchecked((long)Number(registers, "rdx")),
                    Rsi = unchecked((long)Number(registers, "rsi")), Rdi = unchecked((long)Number(registers, "rdi")),
                    Rsp = unchecked((long)Number(registers, "rsp")), Rbp = unchecked((long)Number(registers, "rbp")),
                    Rip = unchecked((long)Number(registers, "rip")),
                    R8 = unchecked((long)Number(registers, "r8")), R9 = unchecked((long)Number(registers, "r9")),
                    R10 = unchecked((long)Number(registers, "r10")), R11 = unchecked((long)Number(registers, "r11")),
                    R12 = unchecked((long)Number(registers, "r12")), R13 = unchecked((long)Number(registers, "r13")),
                    R14 = unchecked((long)Number(registers, "r14")), R15 = unchecked((long)Number(registers, "r15")),
                    EFlags = (X86_CONTEXT_FLAGS)Number(registers, registers.ContainsKey("rflags") ? "rflags" : "eflags"),
                };
                Marshal.StructureToPtr(value, context, false);
                foreach (var (name, offset) in new[] { ("cs", 0x38), ("ds", 0x3a), ("es", 0x3c), ("fs", 0x3e), ("gs", 0x40), ("ss", 0x42) })
                    if (registers.ContainsKey(name))
                        BinaryPrimitives.WriteUInt16LittleEndian(bytes[offset..], checked((ushort)Number(registers, name)));
                    else
                        value.ContextFlags &= ~(ContextFlags)4;
                if (((uint)requestedFlags & 8) != 0)
                {
                    for (int i = 0; i < 16; i++)
                        Vector(registers, "xmm" + i).CopyTo(bytes[(0x1a0 + i * 16)..]);
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes[0x34..], checked((uint)Number(registers, "mxcsr")));
                }
                BinaryPrimitives.WriteUInt32LittleEndian(bytes[0x30..], (uint)value.ContextFlags);
            }
            else
                return HRESULT.E_NOTIMPL;
            operationToken().ThrowIfCancellationRequested();
            snapshotToken.ThrowIfCancellationRequested();
            return HRESULT.S_OK;
        }
        catch (OperationCanceledException) { return HRESULT.E_ABORT; }
        catch (Exception error) when (error is DebuggerException or IOException or TimeoutException or JsonException
            or FormatException or KeyNotFoundException or OverflowException)
        {
            log($"Managed snapshot context for OS thread {threadId} failed: {error.Message}");
            return HRESULT.E_FAIL;
        }
    }

    private Dictionary<string, string> Registers(int threadId)
    {
        if (_registers.TryGetValue(threadId, out var cached))
            return cached;
        JsonElement frames = Request("stackTrace", new { threadId, levels = 1 }).GetProperty("stackFrames");
        if (frames.GetArrayLength() == 0)
            throw new DebuggerException("LLDB did not expose the requested OS thread's context.");
        int frameId = frames[0].GetProperty("id").GetInt32();
        JsonElement scope = Request("scopes", new { frameId }).GetProperty("scopes").EnumerateArray()
            .FirstOrDefault(s => s.TryGetProperty("presentationHint", out var hint) && hint.GetString() == "registers"
                || s.GetProperty("name").GetString() == "Registers");
        if (scope.ValueKind != JsonValueKind.Object)
            throw new DebuggerException("The native backend did not expose a DAP register scope.");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Add(scope.GetProperty("variablesReference").GetInt32(), 0);
        _registers.Add(threadId, values);
        return values;

        void Add(int reference, int depth)
        {
            if (depth > 4)
                throw new DebuggerException("Native register scopes exceeded the nesting limit.");
            foreach (JsonElement item in Request("variables", new { variablesReference = reference, format = new { hex = true } })
                .GetProperty("variables").EnumerateArray())
            {
                string name = item.GetProperty("name").GetString()!;
                string value = item.GetProperty("value").GetString()!;
                if (!string.IsNullOrEmpty(value))
                    values.TryAdd(name, value);
                else if (item.GetProperty("variablesReference").GetInt32() is > 0 and var child)
                    Add(child, depth + 1);
            }
        }
    }

    private JsonElement Request(string command, object arguments)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(snapshotToken, operationToken());
        return Body(session.Request(command, arguments, cancellation.Token));
    }

    internal static JsonElement Body(DapMessage response)
    {
        if (response.Success != true || response.Body is not JsonElement { ValueKind: JsonValueKind.Object } body)
            throw new DebuggerException($"Native '{response.Command}' failed: {response.Message ?? response.Body?.ToString()}");
        return body;
    }

    private static ulong Number(Dictionary<string, string> values, string name)
    {
        Match number = Regex.Match(values[name], @"^0x([0-9a-fA-F]+)", RegexOptions.CultureInvariant);
        if (!number.Success)
            throw new DebuggerException($"Native register '{name}' was not hexadecimal.");
        return ulong.Parse(number.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static byte[] Vector(Dictionary<string, string> values, string name)
    {
        string value = values[name];
        MatchCollection lanes = Regex.Matches(value, @"0x([0-9a-fA-F]+)", RegexOptions.CultureInvariant);
        if (lanes.Count is 1 or 2 or 4 or 8 or 16)
        {
            int width = 16 / lanes.Count;
            var result = new byte[16];
            for (int i = 0; i < lanes.Count; i++)
            {
                string digits = lanes[i].Groups[1].Value;
                if (digits.Length > width * 2)
                    throw new DebuggerException($"Native register '{name}' has an oversized vector lane.");
                byte[] lane = Convert.FromHexString(digits.PadLeft(width * 2, '0'));
                Array.Reverse(lane);
                lane.CopyTo(result, i * width);
            }
            return result;
        }
        throw new DebuggerException($"Native register '{name}' was not a supported 128-bit vector representation.");
    }
}
