using System;
using System.Collections.Generic;

using DbgTest;
using DbgTest.DAP;
using DbgTest.Script;

using Newtonsoft.Json;

namespace TestExceptionConditionMatching
{
class Program
{
    static void Main(string[] args)
    {
        Label.Checkpoint("init", "conditions",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.Initialize(@"__FILE__:__LINE__");
                Context.AddBreakpoint(@"__FILE__:__LINE__", "after");
                Context.SetBreakpoints(@"__FILE__:__LINE__");
                Context.ConfigurationDone(@"__FILE__:__LINE__");
                Context.Launch(@"__FILE__:__LINE__", jmc: true);
                Context.WasEntryPointHit(@"__FILE__:__LINE__");
            });

        for (int i = 0; i < 37; i++)
        {
            try
            {
                throw new InvalidOperationException();                    Label.Breakpoint("invalid");
            }
            catch (InvalidOperationException)
            {
            }
            try
            {
                throw new NullReferenceException();                       Label.Breakpoint("null");
            }
            catch (NullReferenceException)
            {
            }
            GC.KeepAlive(i);                                              Label.Breakpoint("after");
        }

        Label.Checkpoint("conditions", "finish",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.CheckExceptionConditions(@"__FILE__:__LINE__");
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
    public void CheckExceptionConditions(string callerTrace)
    {
        string invalid = "System.InvalidOperationException";
        string nullRef = "System.NullReferenceException";
        var cases = new (bool All, string[] Conditions, string Expected)[]
        {
            (false, new[] { "!" + invalid }, ""),
            (false, new[] { "!" + invalid + " " + nullRef }, nullRef),
            (false, new[] { nullRef + " !" + invalid }, nullRef),
            (false, new[] { invalid + " !" + invalid }, ""),
            (false, new[] { "!" }, invalid + "," + nullRef),
            (false, new[] { "" }, invalid + "," + nullRef),
            (true, new[] { nullRef }, invalid + "," + nullRef),
            (true, new[] { "!" + invalid }, nullRef),
            (false, new[] { invalid, nullRef }, invalid + "," + nullRef),
            (false, new[] { invalid, "!" + invalid }, ""),
            (false, new[] { "!" + invalid, invalid }, ""),
            (false, new[] { "!, !" + invalid + ",\t" + nullRef }, nullRef),
            (true, new[] { "!\f !" + invalid + "\v" + nullRef }, nullRef),
            (false, new[] { "*" }, invalid + "," + nullRef),
            (false, new[] { "System.*" }, invalid + "," + nullRef),
            (false, new[] { "System.Invalid*" }, invalid),
            (false, new[] { "System.Missing*" }, ""),
            (false, new[] { "System.*, !System.Null*" }, invalid),
            (false, new[] { "System.*, !*" }, ""),
            (false, new[] { "!*" }, ""),
            (false, new[] { "!System.Invalid*" }, ""),
            (false, new[] { "*, !System.Invalid*" }, nullRef),
            (false, new[] { "!System.Invalid*, *" }, nullRef),
            (false, new[] { "*", "!System.Invalid*" }, nullRef),
            (false, new[] { "!System.Invalid*", "*" }, nullRef),
            (false, new[] { invalid, "!System.Invalid*" }, ""),
            (false, new[] { "System.Invalid*", "!" + invalid }, ""),
            (false, new[] { "System.invalid*" }, ""),
            (false, new[] { "System.*Exception" }, ""),
            (false, new[] { "System.*Exception*" }, ""),
            (false, new[] { "System.Invalid**" }, ""),
            (false, new[] { "**" }, ""),
            (false, new[] { "System.InvalidOperationExceptio?" }, ""),
            (false, new[] { invalid + "*" }, invalid),
            (false, new[] { "System.Invalid*, System.Invalid*, System.Null*" }, invalid + "," + nullRef),
            (true, new[] { "!System.*" }, ""),
            (true, new[] { "!System.Missing*" }, invalid + "," + nullRef),
        };
        var actual = new List<string>();
        var expected = new List<string>();
        foreach (var scenario in cases)
        {
            var request = new SetExceptionBreakpointsRequest();
            if (scenario.All)
                request.arguments.filters.Add("all");
            request.arguments.filterOptions = new List<ExceptionFilterOptions>();
            foreach (string condition in scenario.Conditions)
                request.arguments.filterOptions.Add(new ExceptionFilterOptions { filterId = "all", condition = condition });
            Assert.True(m_dapDebugger.Request(request).Success, callerTrace);
            Continue(callerTrace);

            var hits = new List<string>();
            for (int stops = 0; ; stops++)
            {
                Assert.True(stops <= 2, callerTrace);
                string reason = "";
                Assert.True(m_dapDebugger.IsEventReceived(json =>
                {
                    if (!m_dapDebugger.IsResponseContainProperty(json, "event", "stopped"))
                        return false;
                    var stopped = JsonConvert.DeserializeObject<StoppedEvent>(json)!;
                    m_threadId = stopped.body.threadId!.Value;
                    reason = stopped.body.reason;
                    return true;
                }), callerTrace);
                var stack = new StackTraceRequest();
                stack.arguments.threadId = m_threadId;
                stack.arguments.levels = 1;
                var result = m_dapDebugger.Request(stack);
                Assert.True(result.Success, callerTrace);
                var frame = JsonConvert.DeserializeObject<StackTraceResponse>(result.ResponseStr)!.body.stackFrames[0];
                Assert.Equal(m_controlInfo.SourceFilesPath, frame.source.path, callerTrace);
                if (reason == "breakpoint")
                {
                    Assert.Equal(((LineBreakpoint)m_controlInfo.Breakpoints["after"]).NumLine, frame.line, callerTrace);
                    break;
                }
                Assert.Equal("exception", reason, callerTrace);
                string type;
                if (frame.line == ((LineBreakpoint)m_controlInfo.Breakpoints["invalid"]).NumLine)
                    type = invalid;
                else
                {
                    Assert.Equal(((LineBreakpoint)m_controlInfo.Breakpoints["null"]).NumLine, frame.line, callerTrace);
                    type = nullRef;
                }
                TestExceptionInfo(callerTrace, "CLR", "always", type);
                hits.Add(type);
                Continue(callerTrace);
            }
            actual.Add(string.Join(",", hits));
            expected.Add(scenario.Expected);
        }
        Assert.Equal(string.Join("\n", expected), string.Join("\n", actual), callerTrace);
    }
}
}
