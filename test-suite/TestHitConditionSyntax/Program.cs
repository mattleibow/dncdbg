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
    public void CheckHitConditionSyntax(string caller_trace)
    {
        string[] names = { "bare", "equal", "greater_equal", "modulo" };
        string[] conditions = { "5", "==5", ">=5", "%5" };
        var hits = new List<string>[] { new(), new(), new(), new() };

        // Collect every case before comparing, so one syntax difference cannot mask the others.
        for (int stops = 0; ; stops++)
        {
            Assert.True(stops <= 28, caller_trace);
            Assert.True(DAPDebugger.IsEventReceived(json =>
            {
                if (!DAPDebugger.IsResponseContainProperty(json, "event", "stopped"))
                    return false;
                var stopped = JsonConvert.DeserializeObject<StoppedEvent>(json)!;
                Assert.Equal("breakpoint", stopped.body.reason, caller_trace);
                Assert.True(stopped.body.threadId.HasValue, caller_trace);
                threadId = stopped.body.threadId!.Value;
                return true;
            }), caller_trace);

            var stackRequest = new StackTraceRequest();
            stackRequest.arguments.threadId = threadId;
            stackRequest.arguments.levels = 1;
            var stackResult = DAPDebugger.Request(stackRequest);
            Assert.True(stackResult.Success, caller_trace);
            var frame = JsonConvert.DeserializeObject<StackTraceResponse>(stackResult.ResponseStr)!.body.stackFrames[0];
            Assert.Equal(ControlInfo.SourceFilesPath, frame.source.path, caller_trace);

            if (frame.line == ((LineBreakpoint)ControlInfo.Breakpoints["end"]).NumLine)
                break;

            int index = -1;
            for (int j = 0; j < names.Length; j++)
            {
                if (frame.line == ((LineBreakpoint)ControlInfo.Breakpoints[names[j]]).NumLine)
                    index = j;
            }
            Assert.True(index >= 0, caller_trace);

            var evaluateRequest = new EvaluateRequest();
            evaluateRequest.arguments.frameId = frame.id;
            evaluateRequest.arguments.expression = "i";
            var evaluateResult = DAPDebugger.Request(evaluateRequest);
            Assert.True(evaluateResult.Success, caller_trace);
            hits[index].Add(JsonConvert.DeserializeObject<EvaluateResponse>(evaluateResult.ResponseStr)!.body.result);
            Continue(caller_trace);
        }

        var actual = new List<string>();
        for (int j = 0; j < conditions.Length; j++)
        {
            int errors = 0;
            while (DAPDebugger.IsNotStopEventReceived(json =>
            {
                if (!DAPDebugger.IsResponseContainProperty(json, "event", "output"))
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

        // Native constructs hitCount > hitCondition, and removes an expression that fails evaluation.
        string expected = "5: hits=6,7; removal errors=0\n" +
                          "==5: hits=1,2,3,4,5,6,7; removal errors=1\n" +
                          ">=5: hits=1,2,3,4,5,6,7; removal errors=1\n" +
                          "%5: hits=1,2,3,4,5,6,7; removal errors=1";
        Assert.Equal(expected, string.Join("\n", actual), caller_trace);
    }
}
}
