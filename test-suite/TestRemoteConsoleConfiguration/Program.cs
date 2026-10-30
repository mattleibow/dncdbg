using System;
using System.Collections.Generic;

using DbgTest;
using DbgTest.DAP;
using DbgTest.Script;

namespace TestRemoteConsoleConfiguration
{
class Program
{
    static void Main(string[] args)
    {
        if (args.Length != 0 && args[0] == "--exit")
            return;

        Label.Checkpoint("init", "finish",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.CheckRemoteConsoleConfiguration(@"__FILE__:__LINE__");
                Context.DebuggerExit(@"__FILE__:__LINE__");
            });

        Console.WriteLine("ready");
        Console.WriteLine("echo:" + Console.ReadLine());

        Label.Checkpoint("finish", "", (Object context) => { });
    }
}
}

namespace DbgTest.Script
{
partial class Context
{
    public void CheckRemoteConsoleConfiguration(string callerTrace)
    {
        const string variable = "DNCDBG_REMOTECONSOLE_PORT";
        int baseline = Environment.GetEnvironmentVariable(variable) is { } inherited
            ? int.Parse(inherited, System.Globalization.CultureInfo.InvariantCulture) : 22534;
        int customPort = AvailableConsolePort();
        var cases = new (string? Value, int Expected, bool Legacy)[]
        {
            (customPort.ToString(System.Globalization.CultureInfo.InvariantCulture), customPort, false),
            (null, baseline, false),
            ("000" + customPort.ToString(System.Globalization.CultureInfo.InvariantCulture), customPort, false),
            ("65535", 65535, false),
            ("", 22534, false),
            ("0", 22534, false),
            ("65536", 22534, false),
            ("4294967295", 22534, false),
            ("4294967296", 22534, false),
            ("-1", 22534, false),
            ("+22534", 22534, false),
            (" 22534", 22534, false),
            ("22534 ", 22534, false),
            ("22534suffix", 22534, false),
            ("not-a-number", 22534, false),
            (customPort.ToString(System.Globalization.CultureInfo.InvariantCulture), baseline, true),
        };
        foreach (var scenario in cases)
        {
            ResetConsole(callerTrace);
            var request = ConsoleLaunch();
            if (scenario.Value != null)
                request.arguments.env.Add(scenario.Legacy ? "DNCDBG_REMOTECONSOLEPORT" : variable, scenario.Value);
            Assert.True(m_dapDebugger.Request(request).Success, callerTrace + ": " + scenario.Value);
            using var console = new RemoteConsole(scenario.Expected);
            CompleteConsoleSession(console, callerTrace);
        }

        ResetConsole(callerTrace);
        var initial = ConsoleLaunch();
        initial.arguments.env.Add(variable, customPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(m_dapDebugger.Request(initial).Success, callerTrace);
        using (var console = new RemoteConsole(customPort))
            CompleteConsoleSession(console, callerTrace);

        int restartPort = AvailableConsolePort();
        var restart = new RestartWithLaunchArgumentsRequest();
        restart.arguments.arguments = ConsoleLaunch().arguments;
        restart.arguments.arguments.env.Add(variable, restartPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(m_dapDebugger.Request(restart).Success, callerTrace);
        using (var console = new RemoteConsole(restartPort))
            CompleteConsoleSession(console, callerTrace);

        ResetConsole(callerTrace);
        var retry = ConsoleLaunch();
        using (var occupied = new ConsolePortReservation())
        {
            int port = occupied.Port;
            retry.arguments.env.Add(variable, port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.False(m_dapDebugger.Request(retry).Success, callerTrace + ": occupied port must fail");
        }
        Assert.True(m_dapDebugger.Request(retry).Success, callerTrace + ": retry after bind failure");
        using (var console = new RemoteConsole(int.Parse(retry.arguments.env[variable], System.Globalization.CultureInfo.InvariantCulture)))
            CompleteConsoleSession(console, callerTrace);

        ResetConsole(callerTrace);
    }

    private LaunchRequest ConsoleLaunch() => new()
    {
        arguments = new LaunchRequestArguments
        {
            program = m_controlInfo.TargetAssemblyPath!,
            noDebug = true,
            env = new Dictionary<string, string> { ["DNCDBG_CONSOLE"] = "remoteConsole" },
        },
    };

    private void ResetConsole(string callerTrace)
    {
        Initialize(callerTrace);
        ConfigurationDone(callerTrace);
        var reset = new LaunchRequest();
        reset.arguments.program = m_controlInfo.TargetAssemblyPath!;
        reset.arguments.noDebug = true;
        reset.arguments.console = "internalConsole";
        reset.arguments.args.Add("--exit");
        Assert.True(m_dapDebugger.Request(reset).Success, callerTrace);
        WasExit(callerTrace);
        Initialize(callerTrace);
        ConfigurationDone(callerTrace);
    }

    private void CompleteConsoleSession(RemoteConsole console, string callerTrace)
    {
        Assert.Equal("ready", console.Receive(5000), callerTrace);
        console.Send("configuration-proof");
        Assert.Equal("echo:configuration-proof", console.Receive(5000), callerTrace);
        WasExit(callerTrace);
    }

    private static int AvailableConsolePort()
    {
        using var reservation = new ConsolePortReservation();
        return reservation.Port;
    }
}
}
