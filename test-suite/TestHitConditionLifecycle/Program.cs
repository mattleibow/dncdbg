using System;
using System.Collections.Generic;
using DbgTest;
using DbgTest.DAP;
using DbgTest.Script;
using Newtonsoft.Json;

namespace TestHitConditionLifecycle
{
class Program
{
    static void Target(int visit)
    {                                                                 Label.Breakpoint("function");
        GC.KeepAlive(visit);
    }
    static void Conditional(int visit)
    {                                                                 Label.Breakpoint("conditional_function");
        GC.KeepAlive(visit);
    }
    static void Main(string[] args)
    {
        Label.Checkpoint("init", "walk",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.Initialize(@"__FILE__:__LINE__");
                Context.ConfigureHitLifecycle(@"__FILE__:__LINE__", "2", true);
                Context.ConfigurationDone(@"__FILE__:__LINE__");
                Context.Launch(@"__FILE__:__LINE__");
                Context.WasEntryPointHit(@"__FILE__:__LINE__");
                Context.Continue(@"__FILE__:__LINE__");
            });
        for (int visit = 1; visit <= 7; visit++)
        {
            GC.KeepAlive(visit);                                       Label.Breakpoint("source");
            GC.KeepAlive(visit);                                       Label.Breakpoint("conditional_source");
            GC.KeepAlive(visit);                                       Label.Breakpoint("logpoint");
            Target(visit);
            Conditional(visit);
            GC.KeepAlive(visit);                                       Label.Breakpoint("stage");
        }
        Label.Checkpoint("walk", "finish",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.CheckHitLifecycle(@"__FILE__:__LINE__");
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
    int sourceHitId;
    int functionHitId;

    public void ConfigureHitLifecycle(string trace, string hit, bool enabled)
    {
        var source = new SetBreakpointsRequest();
        source.arguments.source.path = ControlInfo.SourceFilesPath;
        source.arguments.breakpoints.Add(new SourceBreakpoint(Line("stage")));
        source.arguments.breakpoints.Add(new SourceBreakpoint(Line("conditional_source"), "visit % 2 == 0", "==2"));
        if (enabled)
        {
            source.arguments.breakpoints.Add(new SourceBreakpoint(Line("source"), HitCondition: hit));
            source.arguments.breakpoints.Add(new SourceBreakpoint(Line("logpoint"), HitCondition: hit, LogMessage: "hit-log:{visit}"));
        }
        var response = DAPDebugger.Request(source);
        Assert.True(response.Success, trace);
        if (enabled)
        {
            int? returned = JsonConvert.DeserializeObject<SetBreakpointsResponse>(response.ResponseStr)!.body.breakpoints[2].id;
            Assert.True(returned.HasValue, trace);
            int id = returned!.Value;
            if (sourceHitId != 0)
                Assert.Equal(sourceHitId, id, trace);
            sourceHitId = id;
        }

        var functions = new SetFunctionBreakpointsRequest();
        functions.arguments.breakpoints.Add(new FunctionBreakpoint("TestHitConditionLifecycle.Program.Conditional",
            "visit % 2 == 0", "==2"));
        if (enabled)
            functions.arguments.breakpoints.Add(new FunctionBreakpoint("TestHitConditionLifecycle.Program.Target", HitCondition: hit));
        response = DAPDebugger.Request(functions);
        Assert.True(response.Success, trace);
        if (enabled)
        {
            int? returned = JsonConvert.DeserializeObject<SetBreakpointsResponse>(response.ResponseStr)!.body.breakpoints[1].id;
            Assert.True(returned.HasValue, trace);
            int id = returned!.Value;
            if (functionHitId != 0)
                Assert.Equal(functionHitId, id, trace);
            functionHitId = id;
        }

        int Line(string label) => ((LineBreakpoint)ControlInfo.Breakpoints[label]).NumLine;
    }

    public void CheckHitLifecycle(string trace)
    {
        var hits = new List<string>();
        for (int visit = 1; visit <= 7; visit++)
        {
            while (true)
            {
                Assert.True(DAPDebugger.IsEventReceived(json =>
                {
                    if (!DAPDebugger.IsResponseContainProperty(json, "event", "stopped"))
                        return false;
                    var stop = JsonConvert.DeserializeObject<StoppedEvent>(json)!;
                    Assert.Equal("breakpoint", stop.body.reason, trace);
                    threadId = stop.body.threadId!.Value;
                    return true;
                }), trace);
                var stack = new StackTraceRequest();
                stack.arguments.threadId = threadId;
                stack.arguments.levels = 1;
                var response = DAPDebugger.Request(stack);
                Assert.True(response.Success, trace);
                var frame = JsonConvert.DeserializeObject<StackTraceResponse>(response.ResponseStr)!.body.stackFrames[0];
                string label = "";
                foreach (string name in new[] { "source", "conditional_source", "function", "conditional_function", "stage" })
                    if (frame.line == ((LineBreakpoint)ControlInfo.Breakpoints[name]).NumLine)
                        label = name;
                Assert.True(label.Length > 0, trace);
                if (label == "stage")
                    break;
                hits.Add(label + ":" + visit);
                Continue(trace);
            }
            if (visit == 1)
                ConfigureHitLifecycle(trace, ">=2", true);
            if (visit == 2)
                ConfigureHitLifecycle(trace, " > = 2 ", true);
            if (visit == 3)
                ConfigureHitLifecycle(trace, "==4", true);
            if (visit == 4)
            {
                var badSource = new SetBreakpointsRequest();
                badSource.arguments.source.path = ControlInfo.SourceFilesPath;
                badSource.arguments.breakpoints.Add(new SourceBreakpoint(((LineBreakpoint)ControlInfo.Breakpoints["stage"]).NumLine));
                badSource.arguments.breakpoints.Add(new SourceBreakpoint(((LineBreakpoint)ControlInfo.Breakpoints["source"]).NumLine, HitCondition: "%0"));
                Assert.False(DAPDebugger.Request(badSource).Success, trace);
                var badFunctions = new SetFunctionBreakpointsRequest();
                badFunctions.arguments.breakpoints.Add(new FunctionBreakpoint("TestHitConditionLifecycle.Program.Target", HitCondition: "abc()"));
                Assert.False(DAPDebugger.Request(badFunctions).Success, trace);
            }
            if (visit == 5)
                ConfigureHitLifecycle(trace, "2", false);
            if (visit == 6)
            {
                sourceHitId = functionHitId = 0;
                ConfigureHitLifecycle(trace, "1", true);
            }
            if (visit < 7)
                Continue(trace);
        }
        Assert.Equal("source:2,function:2,source:3,function:3,source:4,conditional_source:4,function:4,conditional_function:4,source:7,function:7",
            string.Join(",", hits), trace);
        foreach (int visit in new[] { 2, 3, 4, 7 })
            WasOutputEvent(trace, "console", "hit-log:" + visit + "\n");
    }
}
}
