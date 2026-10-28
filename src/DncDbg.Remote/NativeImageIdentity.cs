using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DncDbg.Remote;

/// <summary>Reads public image metadata without loading code. Apple signatures are excluded from the __text hash.</summary>
public sealed record NativeImageIdentity(string BuildId, string CodeHash)
{
    public static NativeImageIdentity Read(string path, string platform)
    {
        using FileStream file = File.OpenRead(path);
        if (file.Length is < 64 or > 256 * 1024 * 1024)
            throw new InvalidDataException("Invalid native image size.");
        byte[] image = new byte[checked((int)file.Length)];
        file.ReadExactly(image);
        return Read(image, platform);
    }

    public static NativeImageIdentity Read(byte[] image, string platform)
    {
        try
        {
            return platform switch
            {
                "iOSSimulator" => MachO(image, 7),
                "MacOSNativeHost" => MachO(image, 1),
                "AndroidEmulator" or "LinuxNativeHost" => Elf(image),
                _ => throw new InvalidDataException("Unsupported native image platform."),
            };
        }
        catch (Exception error) when (error is ArgumentOutOfRangeException or OverflowException or IndexOutOfRangeException)
        {
            throw new InvalidDataException("Malformed native image metadata.", error);
        }
    }

    private static NativeImageIdentity MachO(byte[] b, uint platform)
    {
        if (U32(b, 0) != 0xfeedfacf || U32(b, 4) != 0x0100000c)
            throw new InvalidDataException("Expected thin arm64 Mach-O.");
        int commands = checked((int)U32(b, 16)), commandEnd = checked(32 + (int)U32(b, 20)), offset = 32;
        if (commands > 4096 || commandEnd > b.Length)
            throw new InvalidDataException("Invalid Mach-O commands.");
        string? uuid = null, hash = null;
        bool matchesPlatform = false;
        for (int i = 0; i < commands; i++)
        {
            uint command = U32(b, offset);
            int size = checked((int)U32(b, offset + 4));
            if (size < 8 || checked(offset + size) > commandEnd)
                throw new InvalidDataException("Invalid Mach-O command size.");
            if (command == 0x1b && size == 24)
                uuid = Convert.ToHexString(b.AsSpan(offset + 8, 16));
            if (command == 0x32 && size >= 24)
                matchesPlatform = U32(b, offset + 8) == platform;
            if (command == 0x19 && size >= 72)
            {
                int count = checked((int)U32(b, offset + 64));
                if (checked(72 + count * 80) > size)
                    throw new InvalidDataException("Invalid Mach-O sections.");
                for (int j = 0; j < count; j++)
                {
                    int section = offset + 72 + j * 80;
                    if (!b.AsSpan(section, 16).SequenceEqual("__text\0\0\0\0\0\0\0\0\0\0"u8))
                        continue;
                    int length = checked((int)U64(b, section + 40));
                    int start = checked((int)U32(b, section + 48));
                    hash = Convert.ToHexString(SHA256.HashData(b.AsSpan(start, length)));
                }
            }
            offset += size;
        }
        return matchesPlatform && uuid != null && hash != null
            ? new(uuid, hash) : throw new InvalidDataException("Missing expected platform, UUID or executable code.");
    }

    private static NativeImageIdentity Elf(byte[] b)
    {
        if (!b.AsSpan(0, 6).SequenceEqual(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F', 2, 1 }) ||
            BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(18)) is not (183 or 62))
            throw new InvalidDataException("Expected a little-endian 64-bit ELF for the explicit target platform.");
        int table = checked((int)U64(b, 32));
        int stride = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(54));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(56));
        if (stride < 56 || count > 4096 || checked(table + count * stride) > b.Length)
            throw new InvalidDataException("Invalid ELF program headers.");
        string? identity = null;
        for (int i = 0; i < count; i++)
        {
            int header = table + i * stride;
            if (U32(b, header) != 4) // PT_NOTE
                continue;
            int offset = checked((int)U64(b, header + 8));
            int end = checked(offset + (int)U64(b, header + 32));
            if (end > b.Length)
                throw new InvalidDataException("Invalid ELF note segment.");
            while (offset + 12 <= end)
            {
                int names = checked((int)U32(b, offset)), data = checked((int)U32(b, offset + 4));
                uint type = U32(b, offset + 8);
                int descriptor = checked(offset + 12 + Align(names)), next = checked(descriptor + Align(data));
                if (next > end || names > 4096)
                    throw new InvalidDataException("Invalid ELF build note.");
                if (type == 3 && names == 4 && b.AsSpan(offset + 12, 4).SequenceEqual("GNU\0"u8) && data is > 0 and <= 64)
                    identity = Convert.ToHexString(b.AsSpan(descriptor, data));
                offset = next;
            }
        }
        return identity != null ? new(identity, Convert.ToHexString(SHA256.HashData(b)))
            : throw new InvalidDataException("ELF build identity is missing.");
    }

    private static int Align(int value) => checked((value + 3) & ~3);
    private static uint U32(byte[] b, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset, 4));
    private static ulong U64(byte[] b, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(offset, 8));
}
