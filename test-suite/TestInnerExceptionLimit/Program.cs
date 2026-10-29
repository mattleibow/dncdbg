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
                Context.Launch(@"__FILE__:__LINE__", jmc: true);
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
                Context.TestInnerExceptionMessages(@"__FILE__:__LINE__", expectedFirst: "level 62", expectedLast: "level 31");
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
    public void CheckInnerExceptionCount(string callerTrace, int expected)
    {
        var request = new ExceptionInfoRequest();
        request.arguments.threadId = m_threadId;
        var result = m_dapDebugger.Request(request);
        Assert.True(result.Success, callerTrace);
        var response = JsonConvert.DeserializeObject<ExceptionInfoResponse>(result.ResponseStr, ExceptionInfoJsonSettings)!;
        Assert.Equal("always", response.body.breakMode, callerTrace);
        ExceptionDetails node = response.body.details!;
        for (int depth = 0; ; depth++)
        {
            Assert.Equal("System.InvalidOperationException", node.fullTypeName, callerTrace);
            Assert.Equal(depth == 64 ? "innermost" : "level " + (63 - depth), node.message, callerTrace);
            int children = node.innerException?.Count ?? 0;
            Assert.Equal(depth == expected ? 0 : 1, children, callerTrace);
            if (depth == expected)
                break;
            node = node.innerException![0];
        }
    }

    public void CheckInnerExceptionConfiguration(string callerTrace)
    {
        var cases = new (string? Limit, int Expected, bool Attach)[]
        {
            ("0", 0, false),
            ("1", 1, false),
            ("2", 2, false),
            ("8", 8, false),
            ("32", 32, false),
            ("50", 50, false),
            ("64", 64, false),
            ("100", 64, false),
            ("4294967295", 64, false),
            ("not-a-number", 32, false),
            ("", 32, false),
            ("-1", 32, false),
            ("4294967296", 32, false),
            (" 2", 32, false),
            ("+2", 32, false),
            ("2suffix", 32, false),
            ("2 ", 32, false),
            (null, 32, false),
            ("0", 0, true),
            ("2", 2, true),
            ("2suffix", 32, true),
            ("2 ", 32, true),
        };
        foreach (var scenario in cases)
        {
            Initialize(callerTrace);
            if (scenario.Limit != null)
                AddEnvMapEntry("DNCDBG_INNER_EXCEPTION_WALK_LIMIT", scenario.Limit);
            AddExceptionBreakpointFilterAll();
            SetExceptionBreakpoints(callerTrace);
            ConfigurationDone(callerTrace);
            if (scenario.Attach)
                StartTargetAndAttach(callerTrace, startSuspend: true, stopAtEntry: true);
            else
                Launch(callerTrace, jmc: true);
            WasEntryPointHit(callerTrace);
            Continue(callerTrace);
            WasExceptionBreakpointHit(callerTrace, "point1", "CLR", "always", "System.InvalidOperationException");
            if (scenario.Limit == "50")
                TestInnerExceptionMessages(callerTrace, expectedFirst: "level 62", expectedLast: "level 13");
            if (scenario.Limit == "100")
                TestInnerExceptionMessages(callerTrace, expectedFirst: "level 62", expectedLast: "innermost");
            CheckInnerExceptionCount(callerTrace, scenario.Expected);
            Continue(callerTrace);
            WasExit(callerTrace);
        }
    }
}
}
