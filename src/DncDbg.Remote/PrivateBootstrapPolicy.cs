namespace DncDbg.Remote;

public sealed record PrivateBootstrapIdentity(
    string Platform, string Architecture, string RuntimeVersion,
    string RuntimeBuildId, string RuntimeHash, string DbiBuildId, string DbiHash,
    string DacBuildId, string DacHash);

/// <summary>Experimental, version-fragile private ABI allowlist. Matching product versions alone are insufficient.</summary>
public static class PrivateBootstrapPolicy
{
    public const string RuntimeVersion = "11.0.0-rc.1.26425.128";
    public const string Bootstrap = "private-dbi-v1 (experimental; runtime-version-fragile)";
    public static IReadOnlyList<PrivateBootstrapIdentity> SupportedBuilds { get; } = Array.AsReadOnly(new[]
    {
        new PrivateBootstrapIdentity("iOSSimulator", "Arm64", RuntimeVersion,
            "6907233237423C4CB8189F481F1CF5DA", "2AD1DCECBA0C66483A774B7CC77345E47C11456320602A48BCA5E4AB40155476",
            "E5520AE2A6593A3A9CBE2DD3B8B60DA7", "A6D01239286F0141DC3D6A90E425C022F96340CA052B41757D2AB26CC84FC0DC",
            "2267F3F5CDC63995B4E0D3B89F52C148", "81CB0104D53425EC22F006C382D706370C4B2A5A99A54FE816866C25ACF06928"),
        new PrivateBootstrapIdentity("AndroidEmulator", "Arm64", RuntimeVersion,
            "F741D77EAE68D01DA37161129423DEC3AA9DFBF9", "E2FAE5E1F10FA61CDE54D9CC8E6E4537C3EDCE24B8E0216E078F655B04EACC92",
            "25A4607527A49B76F797C582AFB6D45338D2DB85", "363A647DCADB9D8200F2098B9C4313A6EC38735D970B6F2890D7A9951932A52A",
            "5A52173745A3C9B69D48A6A27B9E045C5DEAC1B4", "7956F691D19540801FC55E91D7F2F7258E26C0107C3F7E2B8AF709850E0AA3DB"),
        new PrivateBootstrapIdentity("AndroidEmulator", "X64", RuntimeVersion,
            "A3ABA7AECC5E3B02CEF376BA080FEEFD5D3C992F", "CEB01AE9B0CCCE2437C768CB2EE1BE0E719D6B60903F5521AEDFA1C7D16FD736",
            "6182AB42360380FF67C9F829EBCEBEFE500D51D2", "911AEE78E1143BD419228C4D2B768DE7092DEC2586867AF07CB08F274935C3A1",
            "7EB51F456695097C1DCD69996D6929B605F9685A", "FD0CA02BF29CB75A23C7E96F53E1B10CAFB5ED0829A9DD39E3C7AB1C20B8C6E8"),
    });
    public static IReadOnlyList<PrivateBootstrapIdentity> NativeHostBuilds { get; } = Array.AsReadOnly(new[]
    {
        new PrivateBootstrapIdentity("MacOSNativeHost", "Arm64", RuntimeVersion,
            "6BB5037327BB336CB48F3CAE461D3F9D", "0B5E07630AAE52ABDC391D98ABF22C90C7C9BFC1A6BEFBB7C557569CB61C7451",
            "7D6858E2ECF73718A9271DA4ADFF870F", "A2C8AE76F6672443386E13857BAF5D0E3E864755C2BE35E491BDE78E2979BA02",
            "08BAFC49ED99342587271663A11B22BF", "4BE3A23608BDBF9025280B6FB6F5EE94EFF14C6C08CD539A8FCBB9FDA0816FD8"),
        new PrivateBootstrapIdentity("LinuxNativeHost", "X64", RuntimeVersion,
            "8690E42653274E4C3B79432A004274ECF37F6BAF", "6D38A042803BED1CA3C6D98A76B0A0707A572366DA0F88918FA06E4B87FDBB3D",
            "500279BBE68B8C45A2F7B2CD3B9163A86542D99C", "64B5CA1BD26EF8E833F6CBC4D39EED7BC3B4D31D1C0D1C6C822FD6A32ABAAD42",
            "E83F7863DE84B2BA1E35BF2456755A73D0CF6E0B", "B2E2C7CFACDFE131912AEAEC034BB9BF91D7C711104E76C3480EAEE02E643F9E"),
        new PrivateBootstrapIdentity("LinuxNativeHost", "Arm64", RuntimeVersion,
            "B56DA2DFF7E15872E3C012F99722A86FA15832DD", "9220A3E5BDEDFB63F26756A8E3A86B58392E72C4DC10925809F5D8D28FCD5D77",
            "DACF9A0DEE93E650AE0A4168CCD583763C02BCF1", "92511F95D3DEF82531841CC76233DFC5DA28E37A07BCB9036006D36CBB6F3143",
            "F83DA6CAEEEFA7377A0D76FBCF069288BD969D72", "63435707CC1716E04988C1E8BA83EFCDFF4A445D826710E57543DAB2D2884127"),
    });

    public static RemoteCapabilities NativeHostCapabilities(string runtimeDirectory)
    {
        string platform = OperatingSystem.IsMacOS() ? "MacOSNativeHost" :
            OperatingSystem.IsLinux() ? "LinuxNativeHost" : throw new PlatformNotSupportedException("Native hosting is POSIX-only.");
        string extension = OperatingSystem.IsMacOS() ? ".dylib" : ".so";
        NativeImageIdentity clr = NativeImageIdentity.Read(Path.Combine(runtimeDirectory, "libcoreclr" + extension), platform);
        NativeImageIdentity dbi = NativeImageIdentity.Read(Path.Combine(runtimeDirectory, "libmscordbi" + extension), platform);
        NativeImageIdentity dac = NativeImageIdentity.Read(Path.Combine(runtimeDirectory, "libmscordaccore" + extension), platform);
        var observed = new PrivateBootstrapIdentity(platform, System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            Path.GetFileName(Path.TrimEndingDirectorySeparator(runtimeDirectory)), clr.BuildId, clr.CodeHash,
            dbi.BuildId, dbi.CodeHash, dac.BuildId, dac.CodeHash);
        if (Validate(observed, ["DllMain", "CoreCLRCreateCordbObject3"]) is { } error)
            throw new PlatformNotSupportedException(error);
        return new(RuntimeVersion, platform, true, null, Bootstrap, observed,
            "Native-hosted, private build-pinned CoreCLR; the same target agent and generated model as mobile.",
            RemoteContract.ModelVersion, "private-coreclr",
            ["sourceBreakpoints", "threads", "modules", "scopes", "variables", "safeEvaluate", "pause", "stepIn", "next", "stepOut", "exceptions"]);
    }

    public static string? Validate(PrivateBootstrapIdentity observed, IReadOnlyCollection<string> exports)
    {
        PrivateBootstrapIdentity[] candidates = SupportedBuilds.Concat(NativeHostBuilds).Where(x =>
            x.Platform == observed.Platform && x.Architecture == observed.Architecture).ToArray();
        if (candidates.Length == 0)
            return $"Unsupported private bootstrap platform/architecture {observed.Platform}/{observed.Architecture}. Only explicitly pinned target tuples are allowed; no cross-platform DBI fallback.";
        if (!candidates.Contains(observed))
            return $"Private bootstrap build mismatch. Expected one of [{string.Join(", ", candidates.Select(x => x.ToString()))}]; observed {observed}. Rebuild with the exact pinned packs; do not bypass the allowlist or substitute desktop DBI.";
        foreach (string required in new[] { "DllMain", "CoreCLRCreateCordbObject3" })
            if (!exports.Contains(required))
                return $"Pinned target DBI is missing required private export '{required}'. Activation refused; never substitute desktop DBI.";
        return null;
    }
}
