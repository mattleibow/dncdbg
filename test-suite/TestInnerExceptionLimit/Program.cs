using System;

using DbgTest;
using DbgTest.DAP;
using DbgTest.Script;
using Newtonsoft.Json;

namespace TestInnerExceptionLimit
{
class Program
{
    static void Main(string[] args)
    {
        Label.Checkpoint("init", "test",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.Initialize(@"__FILE__:__LINE__");
                Context.AddExceptionBreakpointFilterAll();
                Context.SetExceptionBreakpoints(@"__FILE__:__LINE__");
                Context.ConfigurationDone(@"__FILE__:__LINE__");
                Context.Launch(@"__FILE__:__LINE__", JMC: true);
                Context.WasEntryPointHit(@"__FILE__:__LINE__");
                Context.Continue(@"__FILE__:__LINE__");
            });

        var e = new InvalidOperationException("innermost");
        for (int i = 0; i < 64; ++i)
        {
            e = new InvalidOperationException("level " + i, e);
        }
        try { throw e; } catch (Exception) { Console.WriteLine("caught"); }  Label.Breakpoint("point1");

        Label.Checkpoint("test", "finish",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.WasExceptionBreakpointHit(@"__FILE__:__LINE__", "point1", "CLR", "always", "System.InvalidOperationException");
                Context.TestInnerExceptionMessages(@"__FILE__:__LINE__", ExpectedFirst: "level 62", ExpectedLast: "level 31");
                Context.CheckInnerExceptionCount(@"__FILE__:__LINE__", 32);
                Context.Continue(@"__FILE__:__LINE__");
            });

        Label.Checkpoint("finish", "",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.WasExit(@"__FILE__:__LINE__");
                Context.CheckInnerExceptionConfiguration(@"__FILE__:__LINE__");
                Context.DebuggerExit(@"__FILE__:__LINE__");
            });
    }
}
}

namespace DbgTest.Script
{
partial class Context
{
    public void CheckInnerExceptionCount(string caller_trace, int expected)
    {
        var request = new ExceptionInfoRequest();
        request.arguments.threadId = threadId;
        var result = DAPDebugger.Request(request);
        Assert.True(result.Success, caller_trace);
        var response = JsonConvert.DeserializeObject<ExceptionInfoResponse>(result.ResponseStr, ExceptionInfoJsonSettings)!;
        Assert.Equal("always", response.body.breakMode, caller_trace);
        ExceptionDetails node = response.body.details!;
        for (int depth = 0; ; depth++)
        {
            Assert.Equal("System.InvalidOperationException", node.fullTypeName, caller_trace);
            Assert.Equal(depth == 64 ? "innermost" : "level " + (63 - depth), node.message, caller_trace);
            int children = node.innerException?.Count ?? 0;
            Assert.Equal(depth == expected ? 0 : 1, children, caller_trace);
            if (depth == expected)
                break;
            node = node.innerException![0];
        }
    }

    public void CheckInnerExceptionConfiguration(string caller_trace)
    {
        var cases = new (string? Limit, int Expected, bool Attach)[]
        {
            ("0", 0, false),
            ("1", 1, false),
            ("2", 2, false),
            ("8", 8, false),
            ("32", 32, false),
            ("64", 64, false),
            ("4294967295", 64, false),
            ("not-a-number", 32, false),
            ("", 32, false),
            ("-1", 32, false),
            ("4294967296", 32, false),
            (" 2", 32, false),
            ("+2", 32, false),
            ("2suffix", 2, false),
            ("2 ", 2, false),
            (null, 32, false),
            ("0", 0, true),
            ("2", 2, true),
        };
        foreach (var scenario in cases)
        {
            Initialize(caller_trace);
            if (scenario.Limit != null)
                AddEnvMapEntry("DNCDBG_INNER_EXCEPTION_WALK_LIMIT", scenario.Limit);
            AddExceptionBreakpointFilterAll();
            SetExceptionBreakpoints(caller_trace);
            ConfigurationDone(caller_trace);
            if (scenario.Attach)
                StartTargetAndAttach(caller_trace, StartSuspend: true, StopAtEntry: true);
            else
                Launch(caller_trace, JMC: true);
            WasEntryPointHit(caller_trace);
            Continue(caller_trace);
            WasExceptionBreakpointHit(caller_trace, "point1", "CLR", "always", "System.InvalidOperationException");
            CheckInnerExceptionCount(caller_trace, scenario.Expected);
            Continue(caller_trace);
            WasExit(caller_trace);
        }
    }
}
}
