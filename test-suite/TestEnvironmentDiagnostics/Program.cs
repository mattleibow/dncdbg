using System;
using System.Collections.Generic;
using DbgTest;
using DbgTest.DAP;
using DbgTest.Script;
using Newtonsoft.Json;

namespace TestEnvironmentDiagnostics
{
class Program
{
    static void Main(string[] args)
    {
        Label.Checkpoint("init", "finish", (Object context) =>
        {
            Context Context = (Context)context;
            Context.CheckEnvironmentDiagnostics(@"__FILE__:__LINE__");
            Context.DebuggerExit(@"__FILE__:__LINE__");
        });
        Console.WriteLine("diagnostic-target-completed");
        Label.Checkpoint("finish", "", (Object context) => { });
    }
}
}

namespace DbgTest.Script
{
partial class Context
{
    public void CheckEnvironmentDiagnostics(string callerTrace)
    {
        const string inner = "DNCDBG_INNER_EXCEPTION_WALK_LIMIT";
        const string port = "DNCDBG_REMOTECONSOLE_PORT";
        var cases = new (string Name, string Value, string? Message)[]
        {
            (inner, "not-a-number", ParseDiagnostic(inner)),
            (inner, "", ParseDiagnostic(inner)),
            (inner, "2suffix", ParseDiagnostic(inner)),
            (inner, "4294967296", ParseDiagnostic(inner)),
            (inner, "-1", ParseDiagnostic(inner)),
            (port, "0", "Environment variable " + port + " is below the minimum allowed value, using the default value.\n"),
            (port, "65536", "Environment variable " + port + " exceeds the maximum allowed value, using the default value.\n"),
            (port, "4294967295", "Environment variable " + port + " exceeds the maximum allowed value, using the default value.\n"),
            (port, "4294967296", ParseDiagnostic(port)),
            (port, "+22534", ParseDiagnostic(port)),
            (port, "22534 ", ParseDiagnostic(port)),
            (inner, "0", null),
            (inner, "4294967295", null),
            (port, "1", null),
            (port, "65535", null),
        };
        foreach (var scenario in cases)
        {
            Initialize(callerTrace);
            ConfigurationDone(callerTrace);
            var request = new LaunchRequest();
            request.arguments.program = m_controlInfo.TargetAssemblyPath!;
            request.arguments.noDebug = true;
            request.arguments.env.Add(scenario.Name, scenario.Value);
            Assert.True(m_dapDebugger.Request(request).Success, callerTrace);
            CollectEnvironmentDiagnostics(scenario.Message == null ? Array.Empty<string>() : new[] { scenario.Message }, callerTrace, true);
        }

        foreach (string value in new[] { "2suffix", "32" })
        {
            Initialize(callerTrace);
            AddEnvMapEntry(inner, value);
            ConfigurationDone(callerTrace);
            StartTargetAndAttach(callerTrace, startSuspend: true, stopAtEntry: true);
            WasEntryPointHit(callerTrace);
            Continue(callerTrace);
            CollectEnvironmentDiagnostics(value == "32" ? Array.Empty<string>() : new[] { ParseDiagnostic(inner) }, callerTrace, false);
        }

        Initialize(callerTrace);
        ConfigurationDone(callerTrace);
        var initial = new LaunchRequest();
        initial.arguments.program = m_controlInfo.TargetAssemblyPath!;
        initial.arguments.noDebug = true;
        initial.arguments.env.Add(inner, "32");
        Assert.True(m_dapDebugger.Request(initial).Success, callerTrace);
        CollectEnvironmentDiagnostics(Array.Empty<string>(), callerTrace, true);
        var restart = new RestartWithLaunchArgumentsRequest();
        restart.arguments.arguments = initial.arguments;
        restart.arguments.arguments.env[inner] = "2suffix";
        Assert.True(m_dapDebugger.Request(restart).Success, callerTrace);
        CollectEnvironmentDiagnostics(new[] { ParseDiagnostic(inner) }, callerTrace, true);
        restart.arguments.arguments.env[inner] = "32";
        Assert.True(m_dapDebugger.Request(restart).Success, callerTrace);
        CollectEnvironmentDiagnostics(Array.Empty<string>(), callerTrace, true);
    }

    private static string ParseDiagnostic(string variable) =>
        "Failed to parse environment variable " + variable + " as a number, using the default value.\n";

    private void CollectEnvironmentDiagnostics(string[] expected, string callerTrace, bool redirectedOutput)
    {
        var messages = new List<string>();
        bool exited = false, terminated = false;
        string stdout = "";
        Assert.True(m_dapDebugger.IsEventReceived(json =>
        {
            var message = Newtonsoft.Json.Linq.JObject.Parse(json);
            string? name = (string?)message["event"];
            if (name == "output")
            {
                string? category = (string?)message["body"]?["category"];
                string text = (string?)message["body"]?["output"] ?? "";
                if (category == "stdout")
                    stdout += text;
            }
            if (name == "exited")
            {
                Assert.Equal(0, (int)message["body"]!["exitCode"]!, callerTrace);
                exited = true;
            }
            if (name == "terminated")
                terminated = true;
            return exited && terminated;
        }), callerTrace);
        while (m_dapDebugger.IsNotStopEventReceived(json =>
        {
            var message = Newtonsoft.Json.Linq.JObject.Parse(json);
            if ((string?)message["event"] != "output" || (string?)message["body"]?["category"] != "stderr")
                return false;
            messages.Add((string?)message["body"]?["output"] ?? "");
            return true;
        }))
        {
        }
        Assert.Equal(JsonConvert.SerializeObject(expected), JsonConvert.SerializeObject(messages), callerTrace);
        if (redirectedOutput)
            Assert.Equal("diagnostic-target-completed", stdout.TrimEnd('\r', '\n'), callerTrace);
    }
}
}
