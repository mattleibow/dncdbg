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
    });

    public static string? Validate(PrivateBootstrapIdentity observed, IReadOnlyCollection<string> exports)
    {
        PrivateBootstrapIdentity[] candidates = SupportedBuilds.Where(x =>
            x.Platform == observed.Platform && x.Architecture == observed.Architecture).ToArray();
        if (candidates.Length == 0)
            return $"Unsupported private bootstrap platform/architecture {observed.Platform}/{observed.Architecture}. Only pinned arm64 simulator/emulator builds are allowed; no desktop DBI fallback.";
        if (!candidates.Contains(observed))
            return $"Private bootstrap build mismatch. Expected one of [{string.Join(", ", candidates.Select(x => x.ToString()))}]; observed {observed}. Rebuild with the exact pinned packs; do not bypass the allowlist or substitute desktop DBI.";
        foreach (string required in new[] { "DllMain", "CoreCLRCreateCordbObject3" })
            if (!exports.Contains(required))
                return $"Pinned target DBI is missing required private export '{required}'. Activation refused; never substitute desktop DBI.";
        return null;
    }
}
