using System.Security.Cryptography;
using System.Text.Json;

namespace DncDbg.Remote;

public sealed record TargetBundleManifest(int SchemaVersion, string Rid, string RuntimeVersion,
    Dictionary<string, string> Files);

public static class TargetBundle
{
    public static string ManifestHash(string directory)
    {
        string path = Path.Combine(directory, "integrity.json");
        if (new FileInfo(path).Length > 1024 * 1024)
            throw new InvalidDataException("Target bundle inventory exceeds its size bound.");
        using FileStream file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    public static void Validate(string directory, string expectedHash, string rid)
    {
        if (new DirectoryInfo(directory).LinkTarget != null)
            throw new InvalidDataException("A target bundle cannot be a symbolic link.");
        if (expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit) ||
            !ManifestHash(directory).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Target bundle inventory differs from the trusted deployment.");
        TargetBundleManifest manifest = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(directory, "integrity.json")),
            TargetJson.Default.TargetBundleManifest) ?? throw new InvalidDataException("Missing target bundle inventory.");
        if (manifest.SchemaVersion != 1 || manifest.Rid != rid || manifest.Files is not { Count: > 0 and <= 10000 })
            throw new InvalidDataException("Target bundle version, RID or file count is invalid.");
        var files = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(directory);
        long length = 0;
        int entries = 0;
        while (pending.TryPop(out string? current))
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(current))
            {
                if (++entries > 20000) throw new InvalidDataException("Target bundle exceeds its entry bound.");
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Target deployment cannot contain symbolic links.");
                if (Directory.Exists(path)) { pending.Push(path); continue; }
                string relative = Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');
                if (relative == "integrity.json") continue;
                length += new FileInfo(path).Length;
                if (length > 2L * 1024 * 1024 * 1024 || files.Count >= 10000 ||
                    !manifest.Files.TryGetValue(relative, out string? hash) || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                    throw new InvalidDataException("Unexpected file or exceeded target bundle size bound.");
                using FileStream file = File.OpenRead(path);
                if (!Convert.ToHexString(SHA256.HashData(file)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Target deployment file content differs from its integrity inventory: " + relative);
                files.Add(relative);
            }
        }
        if (!files.SetEquals(manifest.Files.Keys))
            throw new InvalidDataException("Target deployment is incomplete.");
    }
}
