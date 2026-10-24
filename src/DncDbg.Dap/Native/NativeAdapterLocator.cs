using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using DncDbg.Protocol;

namespace DncDbg.Dap.Native;

internal sealed record NativeAdapterDescriptor(string Kind, string Path, string[] Arguments, string Source,
    string? Version, string Rid, string[] Warnings);

internal static class NativeAdapterLocator
{
    public static NativeAdapterDescriptor Resolve(string kind, string? explicitPath, string[] arguments)
    {
        if (kind is not ("lldb" or "dap"))
            throw new ArgumentException("Error: Native debugger must be 'lldb' or 'dap'.");
        if (explicitPath != null)
            return Candidate(kind, explicitPath, arguments, "explicit", []);
        if (kind == "dap")
            throw new ArgumentException("Error: --native-debugger=dap requires --native-debugger-path.");

        var warnings = new List<string>();
        var candidates = new List<(string Path, string Source)>();
        if (OperatingSystem.IsMacOS())
        {
            string? xcrun = OnPath("xcrun");
            if (xcrun != null)
            {
                var result = Run(xcrun, ["--find", "lldb-dap"]);
                if (result.ExitCode == 0 && result.Output.Trim() is { Length: > 0 } path)
                    candidates.Add((path, "Xcode toolchain (used in place)"));
            }
        }
        foreach (string name in new[] { "lldb-dap", "lldb-vscode", "lldb-dap-21", "lldb-dap-20", "lldb-dap-19", "lldb-dap-18" })
            if (OnPath(name) is { } path)
                candidates.Add((path, "PATH"));
        if (OperatingSystem.IsWindows())
            candidates.Add((System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "LLVM", "bin", "lldb-dap.exe"), "LLVM installation"));
        string? sdk = Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") ?? Environment.GetEnvironmentVariable("ANDROID_HOME");
        if (!string.IsNullOrEmpty(sdk))
        {
            string lldb = System.IO.Path.Combine(sdk, "lldb");
            if (Directory.Exists(lldb))
                foreach (string version in Directory.GetDirectories(lldb).OrderDescending(StringComparer.Ordinal))
                    candidates.Add((System.IO.Path.Combine(version, "bin", Executable("lldb-dap")), "Android SDK host tool"));
            string ndk = System.IO.Path.Combine(sdk, "ndk");
            if (Directory.Exists(ndk))
                foreach (string version in Directory.GetDirectories(ndk).OrderDescending(StringComparer.Ordinal))
                    AddNdk(version);
        }
        foreach (string variable in new[] { "ANDROID_NDK_ROOT", "ANDROID_NDK_HOME" })
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } ndk)
                AddNdk(ndk);
        string bundle = System.IO.Path.Combine(AppContext.BaseDirectory, "native-debuggers", "lldb", RuntimeInformation.RuntimeIdentifier);
        if (Directory.Exists(bundle))
            candidates.Add((bundle, "bundle"));
        foreach (var candidate in candidates.Distinct())
        {
            if (!File.Exists(candidate.Path) && !Directory.Exists(candidate.Path))
                continue;
            try { return Candidate("lldb", candidate.Path, arguments, candidate.Source, warnings.ToArray()); }
            catch (ArgumentException error) { warnings.Add(error.Message); }
        }
        throw new ArgumentException("Error: Native debugger lldb-dap was not found. Install a public LLVM/Xcode toolchain or provide "
            + "--native-debugger-path. lldb and lldb-server are not DAP adapters."
            + (warnings.Count == 0 ? "" : " " + string.Join(" ", warnings)));

        void AddNdk(string root)
        {
            string prebuilt = System.IO.Path.Combine(root, "toolchains", "llvm", "prebuilt");
            if (Directory.Exists(prebuilt))
                foreach (string host in Directory.GetDirectories(prebuilt))
                    candidates.Add((System.IO.Path.Combine(host, "bin", Executable("lldb-dap")), "Android NDK host tool"));
        }
    }

    private static NativeAdapterDescriptor Candidate(string kind, string path, string[] arguments, string source, string[] warnings)
    {
        string? expectedVersion = null;
        if (Directory.Exists(path))
        {
            string root = System.IO.Path.GetFullPath(path);
            try
            {
                using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(root, "native-adapter.json")));
                JsonElement entry = manifest.RootElement;
                if (entry.GetProperty("provider").GetString() != "llvm"
                    || entry.GetProperty("licenseExpression").GetString() != "Apache-2.0 WITH LLVM-exception")
                    throw new ArgumentException("Error: Only an explicitly licensed upstream LLVM bundle is supported; do not bundle Xcode.");
                if (entry.GetProperty("rid").GetString() != RuntimeInformation.RuntimeIdentifier)
                    throw new ArgumentException("Error: Native debugger bundle RID does not match this host.");
                expectedVersion = entry.GetProperty("version").GetString();
                path = Under(root, entry.GetProperty("executable").GetString()!);
                if (!File.Exists(Under(root, entry.GetProperty("licenseFile").GetString()!)))
                    throw new ArgumentException("Error: Native debugger bundle license file is missing.");
                source = "upstream LLVM bundle (manifest declaration)";
            }
            catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
            { throw new ArgumentException("Error: Invalid native debugger bundle manifest: " + error.Message, error); }
        }
        path = System.IO.Path.GetFullPath(path);
        if (!File.Exists(path))
            throw new ArgumentException($"Error: Native debugger '{path}' was not found.");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path)
            & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
            throw new ArgumentException($"Error: Native debugger '{path}' is not executable.");
        string? version = null;
        if (kind == "lldb")
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (name is "lldb" or "lldb-server" or "debugserver")
                throw new ArgumentException("Error: Select lldb-dap (or lldb-vscode), not the LLDB CLI or target-side server.");
            var result = Run(path, [.. arguments, "--version"]);
            version = ParseVersion(result.Output);
            if (result.ExitCode != 0 || !result.Output.Contains("lldb", StringComparison.OrdinalIgnoreCase) || version == null)
                version = ProbeDapVersion(path, arguments);
            if (expectedVersion != null && expectedVersion != version)
                throw new ArgumentException($"Error: Native debugger bundle version mismatch: manifest {expectedVersion}, executable {version}.");
        }
        return new NativeAdapterDescriptor(kind, path, arguments, source, version, RuntimeInformation.RuntimeIdentifier, warnings);
    }

    internal static string? ParseVersion(string text)
    {
        // Xcode reports an LLVM adapter version and a different liblldb version; DAP reports the latter.
        Match runtime = Regex.Match(text, @"\blldb(?:\s+version\s+|-)(\d+(?:\.\d+){1,3})",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (runtime.Success)
            return runtime.Groups[1].Value;
        Match version = Regex.Match(text, @"\d+(?:\.\d+){1,3}", RegexOptions.CultureInvariant);
        return version.Success ? version.Value : null;
    }

    internal static string DapVersion(NativeAdapterSession session, JsonElement capabilities, CancellationToken cancellation)
    {
        string? text = capabilities.TryGetProperty("$__lldb_version", out var version) && version.ValueKind == JsonValueKind.String
            ? version.GetString() : null;
        if (text == null)
        {
            DapMessage response = session.Request("evaluate", new { expression = "`version", context = "repl" }, cancellation);
            if (response.Success == true && response.Body is JsonElement body && body.TryGetProperty("result", out var result))
                text = result.GetString();
        }
        if (text == null || !text.Contains("lldb", StringComparison.OrdinalIgnoreCase) || ParseVersion(text) is not { } found)
            throw new ArgumentException("Error: The native adapter did not report an LLDB runtime version through DAP.");
        return found;
    }

    private static string ProbeDapVersion(string path, string[] arguments)
    {
        var diagnostics = new List<string>();
        try
        {
            using var session = new NativeAdapterSession(new NativeAdapterDescriptor("lldb", path, arguments,
                "DAP version probe", null, RuntimeInformation.RuntimeIdentifier, []), _ => { },
                error => { lock (diagnostics) diagnostics.Add(error.Message); },
                message => { lock (diagnostics) diagnostics.Add(message); });
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            DapMessage response = session.Request("initialize", new
            {
                adapterID = "dncdbg-version-probe", pathFormat = "path", linesStartAt1 = true,
                columnsStartAt1 = true,
            }, deadline.Token);
            if (response.Success != true || response.Body is not JsonElement { ValueKind: JsonValueKind.Object } capabilities)
                throw new ArgumentException("Error: The native debugger rejected the DAP initialization probe.");
            return DapVersion(session, capabilities, deadline.Token);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            lock (diagnostics)
                throw new ArgumentException($"Error: Native debugger '{path}' could not complete its DAP version probe: "
                    + error.Message + " " + string.Join(" ", diagnostics), error);
        }
    }

    private static string Under(string root, string relative)
    {
        string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
        if (System.IO.Path.IsPathRooted(relative) || !path.StartsWith(root + System.IO.Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Error: Native debugger bundle paths must stay inside the bundle.");
        return path;
    }

    private static string Executable(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    private static string? OnPath(string name)
    {
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator))
        {
            if (!System.IO.Path.IsPathRooted(directory))
                continue;
            string path = System.IO.Path.Combine(directory, Executable(name));
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    private static (int ExitCode, string Output) Run(string executable, string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            foreach (string argument in arguments)
                start.ArgumentList.Add(argument);
            using Process process = Process.Start(start) ?? throw new ArgumentException("Error: Cannot start " + executable);
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw new ArgumentException("Error: Native debugger discovery/version probe timed out: " + executable);
            }
            return (process.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
        }
        catch (System.ComponentModel.Win32Exception error)
        { throw new ArgumentException("Error: Cannot execute native debugger: " + error.Message, error); }
    }
}
