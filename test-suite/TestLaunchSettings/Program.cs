using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using DbgTest;
using DbgTest.DAP;
using DbgTest.Script;

namespace TestLaunchSettings
{
class Program
{
    static void Main(string[] args)
    {
        Label.Checkpoint("init", "finish", (Object context) =>
        {
            Context Context = (Context)context;
            Context.CheckLaunchSettings(@"__FILE__:__LINE__");
            Context.DebuggerExit(@"__FILE__:__LINE__");
        });

        Console.WriteLine("PROFILE:" + JsonConvert.SerializeObject(new
        {
            args,
            cwd = Directory.GetCurrentDirectory(),
            value = Environment.GetEnvironmentVariable("DNCDBG_PROFILE_VALUE"),
            added = Environment.GetEnvironmentVariable("DNCDBG_PROFILE_ADDED"),
            numeric = Environment.GetEnvironmentVariable("DNCDBG_PROFILE_NUMBER"),
            url = Environment.GetEnvironmentVariable("ASPNETCORE_URLS"),
            console = Environment.GetEnvironmentVariable("DNCDBG_CONSOLE"),
        }, new JsonSerializerSettings { StringEscapeHandling = StringEscapeHandling.EscapeNonAscii }));
        Label.Checkpoint("finish", "", (Object context) => { });
    }
}
}

namespace DbgTest.Script
{
partial class Context
{
    sealed class ProfileRequest : Request
    {
        public Newtonsoft.Json.Linq.JObject arguments = new();
    }

    public void CheckLaunchSettings(string callerTrace)
    {
        string root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(m_controlInfo.TargetAssemblyPath!))!,
            "dncdbg-profile-" + Guid.NewGuid().ToString("N"));
        string properties = Path.Combine(root, "Properties");
        Directory.CreateDirectory(properties);
        string work = Directory.CreateDirectory(Path.Combine(properties, "work")).FullName;
        string file = Path.Combine(properties, "launchSettings.json");
        string program = Path.GetFullPath(m_controlInfo.TargetAssemblyPath!);
        string relativeProgram = Path.GetRelativePath(properties, program);
        try
        {
            var profile = new Newtonsoft.Json.Linq.JObject
            {
                ["commandName"] = "Project",
                ["executablePath"] = relativeProgram,
                ["workingDirectory"] = "work",
                ["commandLineArgs"] = "profile \"two words\" tail",
                ["environmentVariables"] = new Newtonsoft.Json.Linq.JObject
                {
                    ["DNCDBG_PROFILE_VALUE"] = "profile",
                    ["DNCDBG_PROFILE_ADDED"] = "added",
                    ["DNCDBG_PROFILE_NUMBER"] = 42,
                },
                ["applicationUrl"] = "http://ignored.invalid",
                ["launchBrowser"] = true,
            };
            var profiles = new Newtonsoft.Json.Linq.JObject
            {
                ["ExecutableFirst"] = new Newtonsoft.Json.Linq.JObject { ["commandName"] = "Executable" },
                ["WrongCase"] = new Newtonsoft.Json.Linq.JObject { ["commandName"] = "project" },
                ["NotObject"] = 17,
                ["First"] = profile,
                ["Second"] = new Newtonsoft.Json.Linq.JObject
                {
                    ["commandName"] = "Project", ["commandLineArgs"] = "second",
                },
                ["Tool"] = new Newtonsoft.Json.Linq.JObject
                {
                    ["commandName"] = "Executable", ["executablePath"] = relativeProgram,
                    ["commandLineArgs"] = "tool",
                },
                ["Unsupported"] = new Newtonsoft.Json.Linq.JObject { ["commandName"] = "IISExpress" },
                ["Untyped"] = new Newtonsoft.Json.Linq.JObject { ["commandName"] = 3 },
                ["IgnoredTypes"] = new Newtonsoft.Json.Linq.JObject
                {
                    ["commandName"] = "Project", ["executablePath"] = 3, ["workingDirectory"] = false,
                    ["commandLineArgs"] = 17, ["environmentVariables"] = "ignored",
                },
            };
            File.WriteAllText(file, "// comments accepted\n" + new Newtonsoft.Json.Linq.JObject { ["profiles"] = profiles });

            var basic = new Newtonsoft.Json.Linq.JObject { ["program"] = program, ["cwd"] = root };
            var defaultArgs = new[] { "profile", "two words", "tail" };
            ProfileSuccess(basic, defaultArgs, root, "profile", "added", callerTrace);
            foreach (var empty in new Newtonsoft.Json.Linq.JToken[]
            {
                Newtonsoft.Json.Linq.JValue.CreateNull(), new Newtonsoft.Json.Linq.JArray(), "", " \t\r\n",
            })
            {
                var options = (Newtonsoft.Json.Linq.JObject)basic.DeepClone();
                options["args"] = empty;
                ProfileSuccess(options, defaultArgs, root, "profile", "added", callerTrace);
            }

            foreach (var disabled in new Newtonsoft.Json.Linq.JToken[] { Newtonsoft.Json.Linq.JValue.CreateNull(), "" })
            {
                var options = (Newtonsoft.Json.Linq.JObject)basic.DeepClone();
                options["launchSettingsProfile"] = disabled;
                options["launchSettingsFilePath"] = 42;
                ProfileSuccess(options, Array.Empty<string>(), root, null, null, callerTrace);
            }
            var named = (Newtonsoft.Json.Linq.JObject)basic.DeepClone();
            named["launchSettingsProfile"] = "Second";
            ProfileSuccess(named, new[] { "second" }, root, null, null, callerTrace);
            var array = (Newtonsoft.Json.Linq.JObject)basic.DeepClone();
            array["args"] = new Newtonsoft.Json.Linq.JArray("request", "two words");
            array["env"] = new Newtonsoft.Json.Linq.JObject { ["DNCDBG_PROFILE_VALUE"] = "request" };
            ProfileSuccess(array, new[] { "request", "two words" }, root, "request", "added", callerTrace);
            var text = (Newtonsoft.Json.Linq.JObject)basic.DeepClone();
            text["args"] = "request \"two words\" a\u00a0b";
            ProfileSuccess(text, new[] { "request", "two words", "a\u00a0b" }, root, "profile", "added", callerTrace);

            var explicitFile = new Newtonsoft.Json.Linq.JObject { ["launchSettingsFilePath"] = file };
            ProfileSuccess(explicitFile, defaultArgs, work, "profile", "added", callerTrace);
            var tool = new Newtonsoft.Json.Linq.JObject { ["launchSettingsFilePath"] = file, ["launchSettingsProfile"] = "Tool" };
            ProfileSuccess(tool, new[] { "tool" }, Path.GetDirectoryName(program)!, null, null, callerTrace, checkCwd: false);
            var relativeFile = (Newtonsoft.Json.Linq.JObject)basic.DeepClone();
            relativeFile["launchSettingsFilePath"] = Path.Combine("Properties", "launchSettings.json");
            ProfileSuccess(relativeFile, defaultArgs, root, "profile", "added", callerTrace);
            foreach (var defaultFile in new Newtonsoft.Json.Linq.JToken[] { Newtonsoft.Json.Linq.JValue.CreateNull(), "" })
            {
                var options = (Newtonsoft.Json.Linq.JObject)basic.DeepClone();
                options["launchSettingsFilePath"] = defaultFile;
                ProfileSuccess(options, defaultArgs, root, "profile", "added", callerTrace);
            }
            var ignoredTypes = (Newtonsoft.Json.Linq.JObject)basic.DeepClone();
            ignoredTypes["launchSettingsProfile"] = "IgnoredTypes";
            ProfileSuccess(ignoredTypes, Array.Empty<string>(), root, null, null, callerTrace);

            // A profile configures the child, not the debugger's own console-relay settings.
            ((Newtonsoft.Json.Linq.JObject)profile["environmentVariables"]!)["DNCDBG_CONSOLE"] = "remoteConsole";
            File.WriteAllText(file, new Newtonsoft.Json.Linq.JObject { ["profiles"] = profiles }.ToString());
            ProfileSuccess(basic, defaultArgs, root, "profile", "added", callerTrace, expectedConsole: "remoteConsole");
            ((Newtonsoft.Json.Linq.JObject)profile["environmentVariables"]!).Remove("DNCDBG_CONSOLE");
            File.WriteAllText(file, new Newtonsoft.Json.Linq.JObject { ["profiles"] = profiles }.ToString());

            var missingProfile = (Newtonsoft.Json.Linq.JObject)basic.DeepClone();
            missingProfile["launchSettingsProfile"] = "Missing";
            ProfileError(missingProfile, "Can't find the 'Missing' profile in the launchSettings.json file '" + file + "'.", callerTrace);
            foreach (string unsupported in new[] { "Unsupported", "Untyped" })
            {
                var options = (Newtonsoft.Json.Linq.JObject)basic.DeepClone();
                options["launchSettingsProfile"] = unsupported;
                ProfileError(options, "The '" + unsupported + "' profile in the launchSettings.json file '" + file +
                    "' has an unsupported 'commandName' value: only 'Project' and 'Executable' are supported.", callerTrace);
            }
            foreach (var invalid in new (string Name, Newtonsoft.Json.Linq.JToken Value, string Error)[]
            {
                ("launchSettingsProfile", 42, "The 'launchSettingsProfile' option must be a string or null."),
                ("launchSettingsFilePath", false, "The 'launchSettingsFilePath' option must be a string or null."),
                ("program", 42, "The 'program' option must be a string."),
                ("cwd", false, "The 'cwd' option must be a string."),
                ("args", 42, "The 'args' option must be a string or an array of strings."),
                ("args", new Newtonsoft.Json.Linq.JArray("ok", 42), "The 'args' option must be a string or an array of strings."),
            })
            {
                var options = (Newtonsoft.Json.Linq.JObject)basic.DeepClone();
                options[invalid.Name] = invalid.Value;
                ProfileError(options, invalid.Error, callerTrace);
            }
            var missingFile = (Newtonsoft.Json.Linq.JObject)basic.DeepClone();
            string absent = Path.Combine(root, "absent.json");
            missingFile["launchSettingsFilePath"] = absent;
            ProfileError(missingFile, "Can't open the launchSettings.json file '" + absent + "'.", callerTrace);

            foreach (string content in new[] { "{broken", "{\"profiles\":{},}", "{\"profiles\":{}}", "{\"profiles\":{\"Only\":{\"commandName\":\"Executable\"}}}", "{\"Profiles\":{\"First\":{\"commandName\":\"Project\"}}}" })
            {
                File.WriteAllText(file, content);
                ProfileSuccess(basic, Array.Empty<string>(), root, null, null, callerTrace);
                var options = (Newtonsoft.Json.Linq.JObject)basic.DeepClone();
                options["launchSettingsFilePath"] = file;
                string error = content == "{broken" || content.EndsWith(",}") ? "Can't parse the launchSettings.json file '" + file + "'." :
                    content.Contains("\"Only\"") ? "Can't find a profile with \"commandName\": \"Project\" in the launchSettings.json file '" + file + "'." :
                    "Can't find profiles in the launchSettings.json file '" + file + "'.";
                ProfileError(options, error, callerTrace);
            }
            File.Delete(file);
            ProfileSuccess(basic, Array.Empty<string>(), root, null, null, callerTrace);
            var required = (Newtonsoft.Json.Linq.JObject)basic.DeepClone();
            required["launchSettingsProfile"] = "First";
            ProfileError(required, "Can't open the launchSettings.json file '" + file + "'.", callerTrace);
            var noProgram = (Newtonsoft.Json.Linq.JObject)basic.DeepClone();
            noProgram.Remove("program");
            ProfileError(noProgram, "The 'program' option is required.", callerTrace);

            File.WriteAllText(file, new Newtonsoft.Json.Linq.JObject { ["profiles"] = profiles }.ToString());
            ProfileSuccess(basic, defaultArgs, root, "profile", "added", callerTrace);
            profile["commandLineArgs"] = "changed-after-launch";
            File.WriteAllText(file, new Newtonsoft.Json.Linq.JObject { ["profiles"] = profiles }.ToString());
            ProfileSuccess(basic, defaultArgs, root, "profile", "added", callerTrace, restart: true);
            var updated = (Newtonsoft.Json.Linq.JObject)basic.DeepClone();
            updated["launchSettingsProfile"] = "Second";
            ProfileSuccess(updated, new[] { "second" }, root, null, null, callerTrace, restart: true, update: true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private void ProfileSuccess(Newtonsoft.Json.Linq.JObject options, string[] expectedArgs, string expectedCwd,
        string? value, string? added, string callerTrace, bool checkCwd = true, string? expectedConsole = null,
        bool restart = false, bool update = false)
    {
        if (!restart)
        {
            Initialize(callerTrace);
            ConfigurationDone(callerTrace);
        }
        options = (Newtonsoft.Json.Linq.JObject)options.DeepClone();
        options["noDebug"] = true;
        var request = new ProfileRequest
        {
            command = restart ? "restart" : "launch",
            arguments = restart ? (update ? new Newtonsoft.Json.Linq.JObject { ["arguments"] = options } : new()) : options,
        };
        var result = m_dapDebugger.Request(request);
        Assert.True(result.Success, callerTrace + ": " + result.ResponseStr);
        string output = "";
        Assert.True(m_dapDebugger.IsEventReceived(json =>
        {
            var message = Newtonsoft.Json.Linq.JObject.Parse(json);
            if ((string?)message["event"] != "output" || (string?)message["body"]?["category"] != "stdout")
                return false;
            output += (string?)message["body"]?["output"];
            return output.Contains("\n");
        }), callerTrace);
        Assert.True(output.StartsWith("PROFILE:"), callerTrace + ": " + output);
        var observed = Newtonsoft.Json.Linq.JObject.Parse(output.Substring("PROFILE:".Length));
        Assert.Equal(JsonConvert.SerializeObject(expectedArgs), observed["args"]!.ToString(Formatting.None), callerTrace);
        if (checkCwd)
            Assert.Equal(Path.GetFullPath(expectedCwd), (string?)observed["cwd"], callerTrace);
        Assert.Equal(value, (string?)observed["value"], callerTrace);
        Assert.Equal(added, (string?)observed["added"], callerTrace);
        Assert.Equal<string?>(null, (string?)observed["numeric"], callerTrace);
        Assert.Equal<string?>(null, (string?)observed["url"], callerTrace);
        Assert.Equal(expectedConsole, (string?)observed["console"], callerTrace);
        WasExit(callerTrace);
    }

    private void ProfileError(Newtonsoft.Json.Linq.JObject options, string expected, string callerTrace)
    {
        Initialize(callerTrace);
        ConfigurationDone(callerTrace);
        var result = m_dapDebugger.Request(new ProfileRequest { command = "launch", arguments = options });
        Assert.False(result.Success, callerTrace);
        var response = Newtonsoft.Json.Linq.JObject.Parse(result.ResponseStr);
        Assert.Equal(expected, (string?)(response["body"]?["message"] ?? response["message"]), callerTrace);
    }
}
}
