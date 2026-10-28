using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using ClrDebug;

[GeneratedComClass]
internal sealed partial class StartupLibraryProvider(string directory) : ICLRDebuggingLibraryProvider3
{
    public List<object> Requests { get; } = [];

    public HRESULT ProvideWindowsLibrary(string file, string runtime, LIBRARY_PROVIDER_INDEX_TYPE index,
        uint timestamp, int size, out nint resolved)
    {
        resolved = 0;
        return HRESULT.E_NOTIMPL;
    }

    public HRESULT ProvideUnixLibrary(string file, string runtime, LIBRARY_PROVIDER_INDEX_TYPE index,
        byte[] buildId, int buildIdSize, out nint resolved)
    {
        resolved = 0;
        if (index is not (LIBRARY_PROVIDER_INDEX_TYPE.Identity or LIBRARY_PROVIDER_INDEX_TYPE.Runtime))
            return HRESULT.E_INVALIDARG;
        string candidate = Path.Combine(directory, Path.GetFileName(file));
        if (Path.GetFileName(file) is not ("libmscordbi.dylib" or "libmscordaccore.dylib"))
            return HRESULT.E_INVALIDARG;
        try
        {
            string identityFile = index == LIBRARY_PROVIDER_INDEX_TYPE.Runtime ? Path.Combine(directory, "libcoreclr.dylib") : candidate;
            byte[]? actual = MachOUuid.Read(identityFile);
            bool matches = buildIdSize > 0 && buildIdSize <= buildId.Length &&
                actual != null && actual.AsSpan().SequenceEqual(buildId.AsSpan(0, buildIdSize));
            Requests.Add(new
            {
                File = file, Runtime = runtime, Index = index.ToString(), Candidate = candidate,
                ExpectedBuildId = Convert.ToHexString(buildId.AsSpan(0, Math.Clamp(buildIdSize, 0, buildId.Length))),
                ActualBuildId = actual == null ? null : Convert.ToHexString(actual), IdentityMatches = matches,
            });
            if (!matches || !File.Exists(candidate))
                return HRESULT.E_FAIL;
            resolved = Marshal.StringToCoTaskMemUni(candidate);
            return HRESULT.S_OK;
        }
        catch (Exception error)
        {
            Requests.Add(new { File = file, Candidate = candidate, Error = error.GetType().Name, error.Message });
            return HRESULT.E_FAIL;
        }
    }
}

internal static class MachOUuid
{
    public static byte[]? Read(string path)
    {
        if (!File.Exists(path))
            return null;
        using FileStream stream = File.OpenRead(path);
        return Read(stream);
    }

    // LC_UUID is public Mach-O file metadata, not a CLR runtime data-structure offset.
    public static byte[]? Read(Stream stream)
    {
        Span<byte> header = stackalloc byte[32];
        if (stream.Read(header) != header.Length || BinaryPrimitives.ReadUInt32LittleEndian(header) != 0xfeedfacf)
            return null;
        uint commands = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        if (commands > 4096)
            return null;
        Span<byte> command = stackalloc byte[8];
        for (uint i = 0; i < commands; i++)
        {
            if (stream.Read(command) != command.Length)
                return null;
            uint kind = BinaryPrimitives.ReadUInt32LittleEndian(command);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(command[4..]);
            if (size < 8 || size - 8 > stream.Length - stream.Position)
                return null;
            if (kind == 0x1b && size == 24)
            {
                byte[] uuid = new byte[16];
                stream.ReadExactly(uuid);
                return uuid;
            }
            stream.Seek(size - 8, SeekOrigin.Current);
        }
        return null;
    }
}
