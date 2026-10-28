using System;
using System.Collections.Generic;

using DbgTest;
using DbgTest.DAP;
using DbgTest.Script;

using Newtonsoft.Json;

namespace TestHitConditionSyntax
{
class Program
{
    static void Main(string[] args)
    {
        Label.Checkpoint("init", "hit_conditions",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.Initialize(@"__FILE__:__LINE__");
                Context.CheckHitConditionRequests(@"__FILE__:__LINE__");
                Context.AddBreakpoint(@"__FILE__:__LINE__", "bare", null, "5");
                Context.AddBreakpoint(@"__FILE__:__LINE__", "equal", null, "==5");
                Context.AddBreakpoint(@"__FILE__:__LINE__", "greater_equal", null, ">=5");
                Context.AddBreakpoint(@"__FILE__:__LINE__", "modulo", null, "%5");
                Context.AddBreakpoint(@"__FILE__:__LINE__", "end");
                Context.SetBreakpoints(@"__FILE__:__LINE__");
                Context.ConfigurationDone(@"__FILE__:__LINE__");
                Context.Launch(@"__FILE__:__LINE__");
                Context.WasEntryPointHit(@"__FILE__:__LINE__");
                Context.Continue(@"__FILE__:__LINE__");
            });

        for (int i = 1; i <= 7; i++)
        {
            GC.KeepAlive(i);                                              Label.Breakpoint("bare");
            GC.KeepAlive(i);                                              Label.Breakpoint("equal");
            GC.KeepAlive(i);                                              Label.Breakpoint("greater_equal");
            GC.KeepAlive(i);                                              Label.Breakpoint("modulo");
        }
        GC.KeepAlive(args);                                               Label.Breakpoint("end");

        Label.Checkpoint("hit_conditions", "finish",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.CheckHitConditionSyntax(@"__FILE__:__LINE__");
            });

        Label.Checkpoint("finish", "",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExit(@"__FILE__:__LINE__");
                Context.DebuggerExit(@"__FILE__:__LINE__");
            });
    }
}
}

namespace DbgTest.Script
{
partial class Context
{
    public void CheckHitConditionSyntax(string callerTrace)
    {
        string[] names = { "bare", "equal", "greater_equal", "modulo" };
        string[] conditions = { "5", "==5", ">=5", "%5" };
        var hits = new List<string>[] { new(), new(), new(), new() };

        // Collect every case before comparing, so one syntax difference cannot mask the others.
        for (int stops = 0; ; stops++)
        {
            Assert.True(stops <= 28, callerTrace);
            Assert.True(m_dapDebugger.IsEventReceived(json =>
            {
                if (!m_dapDebugger.IsResponseContainProperty(json, "event", "stopped"))
                    return false;
                var stopped = JsonConvert.DeserializeObject<StoppedEvent>(json)!;
                Assert.Equal("breakpoint", stopped.body.reason, callerTrace);
                Assert.True(stopped.body.threadId.HasValue, callerTrace);
                m_threadId = stopped.body.threadId!.Value;
                return true;
            }), callerTrace);

            var stackRequest = new StackTraceRequest();
            stackRequest.arguments.threadId = m_threadId;
            stackRequest.arguments.levels = 1;
            var stackResult = m_dapDebugger.Request(stackRequest);
            Assert.True(stackResult.Success, callerTrace);
            var frame = JsonConvert.DeserializeObject<StackTraceResponse>(stackResult.ResponseStr)!.body.stackFrames[0];
            Assert.Equal(m_controlInfo.SourceFilesPath, frame.source.path, callerTrace);

            if (frame.line == ((LineBreakpoint)m_controlInfo.Breakpoints["end"]).NumLine)
                break;

            int index = -1;
            for (int j = 0; j < names.Length; j++)
            {
                if (frame.line == ((LineBreakpoint)m_controlInfo.Breakpoints[names[j]]).NumLine)
                    index = j;
            }
            Assert.True(index >= 0, callerTrace);

            var evaluateRequest = new EvaluateRequest();
            evaluateRequest.arguments.frameId = frame.id;
            evaluateRequest.arguments.expression = "i";
            var evaluateResult = m_dapDebugger.Request(evaluateRequest);
            Assert.True(evaluateResult.Success, callerTrace);
            hits[index].Add(JsonConvert.DeserializeObject<EvaluateResponse>(evaluateResult.ResponseStr)!.body.result);
            Continue(callerTrace);
        }

        var actual = new List<string>();
        for (int j = 0; j < conditions.Length; j++)
        {
            int errors = 0;
            while (m_dapDebugger.IsNotStopEventReceived(json =>
            {
                if (!m_dapDebugger.IsResponseContainProperty(json, "event", "output"))
                    return false;
                var output = JsonConvert.DeserializeObject<OutputEvent>(json)!;
                return output.body.category == "stderr" &&
                       output.body.output.Contains("hitCondition") &&
                       output.body.output.Contains("will be removed") &&
                       output.body.output.Contains("The hitCondition was '" + conditions[j] + "'");
            }))
            {
                errors++;
            }
            actual.Add(conditions[j] + ": hits=" + string.Join(",", hits[j]) + "; removal errors=" + errors);
        }

        string expected = "5: hits=5,6,7; removal errors=0\n" +
                          "==5: hits=5; removal errors=0\n" +
                          ">=5: hits=5,6,7; removal errors=0\n" +
                          "%5: hits=5; removal errors=0";
        Assert.Equal(expected, string.Join("\n", actual), callerTrace);
    }

    public void CheckHitConditionRequests(string callerTrace)
    {
        string[] valid = { "", " ", "0", "1", "4294967295", "<0", "<=0", "==0", ">0", ">=0",
            "<4294967295", "<=4294967295", "==4294967295", ">4294967295", ">=4294967295",
            "%1", "%4294967295", "  > =  2 ", "= = 2", "1 0", "0002", " % 2 " };
        string[] invalid = { "%0", "4294967296", "-1", "+1", "=1", "!=1", "===1",
            "1+3", "0+1+1", "ret_hit_cond()", "abc()", "1x", "1.0", "0x10", "<", ">=",
            "==", "%", "\t", "\t2", "2\n", "2\r", "2\f", "2\v", "\u00a02", "9223372036854775807",
            "2\0", "2\0x" };
        foreach (string condition in valid)
        {
            Assert.True(m_dapDebugger.Request(SourceRequest(condition)).Success, callerTrace + ": " + condition);
            Assert.True(m_dapDebugger.Request(FunctionRequest(condition)).Success, callerTrace + ": " + condition);
        }
        foreach (string condition in invalid)
        {
            CheckRejected(m_dapDebugger.Request(SourceRequest(condition)), condition);
            CheckRejected(m_dapDebugger.Request(FunctionRequest(condition)), condition);
        }
        Assert.True(m_dapDebugger.Request(new SetBreakpointsRequest
        {
            arguments = new SetBreakpointsArguments { source = new Source { path = m_controlInfo.SourceFilesPath } }
        }).Success, callerTrace);
        Assert.True(m_dapDebugger.Request(new SetFunctionBreakpointsRequest()).Success, callerTrace);

        SetBreakpointsRequest SourceRequest(string condition) => new()
        {
            arguments = new SetBreakpointsArguments
            {
                source = new Source { path = m_controlInfo.SourceFilesPath },
                breakpoints = new List<SourceBreakpoint>
                {
                    new(((LineBreakpoint)m_controlInfo.Breakpoints["bare"]).NumLine, hitCondition: "2"),
                    new(((LineBreakpoint)m_controlInfo.Breakpoints["equal"]).NumLine, hitCondition: condition),
                },
            },
        };
        SetFunctionBreakpointsRequest FunctionRequest(string condition) => new()
        {
            arguments = new SetFunctionBreakpointsArguments
            {
                breakpoints = new List<FunctionBreakpoint>
                {
                    new("TestHitConditionSyntax.Program.Main", hitCondition: "2"),
                    new("TestHitConditionSyntax.Program.Main", hitCondition: condition),
                },
            },
        };
        void CheckRejected(DAPResult result, string condition)
        {
            Assert.False(result.Success, callerTrace + ": " + condition);
            string message = m_dapDebugger.GetResponsePropertyValue(result.ResponseStr, "message")?.ToString() ?? "";
            Assert.Equal(condition.Replace(" ", "") == "%0"
                ? "Invalid hit condition: N must be greater than 0 for %N"
                : "Invalid hit condition. Supported hit conditions: <N, <=N, ==N, >N, N or >=N, and %N, where N is an integer within the range 0 - 4294967295",
                message, callerTrace + ": " + condition);
        }
    }
}
}
