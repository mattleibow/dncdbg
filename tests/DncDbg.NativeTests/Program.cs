using System.Runtime.InteropServices;
using ClrDebug;
using DncDbg.Dap.Native;

if (args.Length is < 2 or > 3)
    throw new ArgumentException("Supply the public Python executable and fake_native_adapter.py test peer.");
string python = args[0], fake = args[1];

if (args.Length == 2)
using (var backend = Session("partial-memory"))
{
    var target = new LldbSnapshotTarget(backend, () => CancellationToken.None, CancellationToken.None, Console.Error.WriteLine);
    IntPtr buffer = Marshal.AllocHGlobal(32);
    try
    {
        HRESULT result = target.ReadVirtual(new CORDB_ADDRESS(0x1ff0), buffer, 32, out int read);
        if (result != HRESULT.S_OK || read != 16)
            throw new InvalidOperationException($"Mapped/unmapped boundary discarded a valid prefix: {result}, bytes={read}.");
        for (int i = 0; i < read; i++)
            if (Marshal.ReadByte(buffer, i) != 0xf0 + i)
                throw new InvalidOperationException("Partial memory prefix was corrupted.");
    }
    finally { Marshal.FreeHGlobal(buffer); }
}
if (args.Length == 2)
    Console.WriteLine("PASS native snapshot partial reads preserve the readable prefix");

using (var backend = Session("register-context"))
{
    var target = new LldbSnapshotTarget(backend, () => CancellationToken.None, CancellationToken.None, Console.Error.WriteLine);
    IntPtr buffer = Marshal.AllocHGlobal(2048);
    try
    {
        ContextFlags flags = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? ContextFlags.ARM64ContextControl | ContextFlags.ARM64ContextInteger
            : ContextFlags.AMD64ContextControl | ContextFlags.AMD64ContextInteger;
        HRESULT result = target.GetThreadContext(7, flags, 2048, buffer);
        if (result != HRESULT.S_OK)
            throw new InvalidOperationException($"Control/integer context incorrectly required optional vector banks: {result}.");
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            && (Marshal.ReadInt64(buffer, 0xf0) != 0x2929 || Marshal.ReadInt64(buffer, 0xf8) != 0x3030))
            throw new InvalidOperationException("ARM64 x29/x30 aliases were not projected as fp/lr.");
    }
    finally { Marshal.FreeHGlobal(buffer); }
}
Console.WriteLine("PASS native snapshot register aliases and requested bank ownership");

NativeAdapterSession Session(string mode)
{
    string architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
    return new NativeAdapterSession(new NativeAdapterDescriptor("dap", python,
        [fake, "--mode=" + mode, "--arch=" + architecture], "test", null, RuntimeInformation.RuntimeIdentifier, []),
        _ => { }, error => Console.Error.WriteLine(error.Message), Console.Error.WriteLine);
}
