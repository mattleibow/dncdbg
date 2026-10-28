using DncDbg.Remote;
using System.Buffers.Binary;

internal static class PrivateBootstrapChecks
{
    public static void Run(Action<bool, string> check)
    {
        PrivateBootstrapIdentity supported = PrivateBootstrapPolicy.SupportedBuilds[0];
        string[] exports = ["DllMain", "CoreCLRCreateCordbObject3"];
        check(PrivateBootstrapPolicy.Validate(supported, exports) == null, "exact pinned private bootstrap identity accepted");
        Reject(supported with { RuntimeVersion = "11.0.0" }, exports, "runtime version");
        Reject(supported with { Platform = "macOS" }, exports, "platform");
        Reject(supported with { Architecture = "x64" }, exports, "architecture");
        Reject(supported with { RuntimeBuildId = new string('0', 32) }, exports, "runtime build identity");
        Reject(supported with { RuntimeHash = new string('0', 64) }, exports, "runtime code hash");
        Reject(supported with { DbiBuildId = new string('0', 32) }, exports, "DBI build identity");
        Reject(supported with { DbiHash = new string('0', 64) }, exports, "DBI code hash");
        Reject(supported with { DacHash = new string('0', 64) }, exports, "DAC code hash");
        Reject(supported with { DacBuildId = new string('0', 32) }, exports, "DAC build identity");
        Reject(supported, ["CoreCLRCreateCordbObject3"], "missing DllMain export");
        Reject(supported, ["DllMain"], "missing factory export");
        check(PrivateBootstrapPolicy.SupportedBuilds.Count == 2, "only simulator/emulator arm64 tuples allowlisted");
        foreach (string platform in new[] { "iOSSimulator", "AndroidEmulator", "macOS" })
        {
            foreach (int length in new[] { 0, 3, 32, 63 })
            {
                try { _ = NativeImageIdentity.Read(new byte[length], platform); }
                catch (InvalidDataException) { check(true, $"reject truncated {platform} image ({length} bytes)"); continue; }
                throw new InvalidOperationException("Malformed native image accepted.");
            }
        }
        byte[] image = new byte[236];
        Write(0, 0xfeedfacf);
        Write(4, 0x0100000c);
        Write(16, 3);
        Write(20, 200);
        Write(32, 0x1b); Write(36, 24);
        Write(56, 0x32); Write(60, 24); Write(64, 7);
        Write(80, 0x19); Write(84, 152); Write(144, 1);
        "__text"u8.CopyTo(image.AsSpan(152));
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(192), 4);
        Write(200, 232);
        NativeImageIdentity valid = NativeImageIdentity.Read(image, "iOSSimulator");
        check(valid.BuildId.Length == 32 && valid.CodeHash.Length == 64, "read simulator UUID and signature-independent code hash");
        Write(64, 1);
        RejectImage("reject desktop Mach-O before target activation");
        Write(64, 7); Write(4, 0x01000007);
        RejectImage("reject x64 Mach-O before target activation");
        using var prior = new RemoteLeaseTable<int>(_ => { });
        using var next = new RemoteLeaseTable<int>(_ => { });
        long stale = prior.Add(1), current = next.Add(2);
        check(stale != current, "different connection generations never alias frame handles");
        try { _ = next.Get(stale); }
        catch (InvalidOperationException) { check(true, "stale connection generation rejected"); return; }
        throw new InvalidOperationException("Stale generation accepted.");

        void Reject(PrivateBootstrapIdentity identity, string[] available, string name) =>
            check(PrivateBootstrapPolicy.Validate(identity, available) is { Length: > 0 }, "reject " + name + " before private activation");
        void Write(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset), value);
        void RejectImage(string name)
        {
            try { _ = NativeImageIdentity.Read(image, "iOSSimulator"); }
            catch (InvalidDataException) { check(true, name); return; }
            throw new InvalidOperationException(name + " accepted invalid native image.");
        }
    }
}
