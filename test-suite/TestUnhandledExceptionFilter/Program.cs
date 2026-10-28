using System;
using System.Collections.Generic;

using DbgTest;
using DbgTest.DAP;
using DbgTest.Script;

using Newtonsoft.Json;

namespace TestUnhandledExceptionFilter
{
class Program
{
    static void Main(string[] args)
    {
        Label.Checkpoint("init", "finish",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.CheckUnhandledExceptionFilters(@"__FILE__:__LINE__");
            });

        throw new InvalidOperationException();                            Label.Breakpoint("throw");

        Label.Checkpoint("finish", "",
            (Object context) =>
            {
            });
    }
}
}

namespace DbgTest.Script
{
partial class Context
{
    public void CheckUnhandledExceptionFilters(string callerTrace)
    {
        var cases = new (bool All, string[] Conditions, bool Stop, bool Thrown)[]
        {
            (false, Array.Empty<string>(), true, false),
            (false, new[] { "System.NullReferenceException" }, true, false),
            (true, new[] { "!System.InvalidOperationException" }, false, false),
            (false, new[] { "", "!System.InvalidOperationException" }, false, false),
            (false, new[] { "System.InvalidOperationException", "!System.InvalidOperationException" }, false, false),
            (false, new[] { "!System.InvalidOperationException", "System.InvalidOperationException" }, false, false),
            (false, new[] { "!" }, true, false),
            (false, new[] { "!System.NullReferenceException" }, true, false),
            (false, new[] { "!System.InvalidOperation*" }, false, false),
            (false, new[] { "!System.Missing*" }, true, false),
            (false, new[] { "!*" }, false, false),
            (false, new[] { "System.Missing*" }, true, false),
            (false, new[] { "*", "!System.InvalidOperation*" }, false, false),
            (false, new[] { "!System.InvalidOperation*", "*" }, false, false),
            (false, new[] { "!System.*Exception" }, true, false),
            (false, new[] { "!System.InvalidOperation*" }, false, true),
            (false, new[] { "!System.Exception" }, true, false),
            (false, new[] { "!*" }, false, true),
        };
        foreach (var scenario in cases)
        {
            Initialize(callerTrace);
            var request = new SetExceptionBreakpointsRequest();
            request.arguments.filterOptions = new List<ExceptionFilterOptions>();
            if (scenario.All)
            {
                request.arguments.filters.Add("all");
                request.arguments.filterOptions.Add(new ExceptionFilterOptions
                {
                    filterId = "all", condition = "!System.InvalidOperationException"
                });
            }
            if (scenario.Thrown)
                request.arguments.filterOptions.Add(new ExceptionFilterOptions { filterId = "all", condition = "*" });
            foreach (string condition in scenario.Conditions)
                request.arguments.filterOptions.Add(new ExceptionFilterOptions { filterId = "unhandled", condition = condition });
            Assert.True(m_dapDebugger.Request(request).Success, callerTrace);
            ConfigurationDone(callerTrace);
            Launch(callerTrace, jmc: true);
            WasEntryPointHit(callerTrace);
            Continue(callerTrace);
            if (scenario.Thrown)
            {
                WasExceptionBreakpointHit(callerTrace, "throw", "CLR", "always", "System.InvalidOperationException");
                Continue(callerTrace);
            }
            if (scenario.Stop)
            {
                WasExceptionBreakpointHit(callerTrace, "throw", "CLR", "unhandled", "System.InvalidOperationException");
                Continue(callerTrace);
                WasExit(callerTrace, checkExitCode: null);
            }
            else
            {
                bool exited = false;
                bool terminated = false;
                Assert.True(m_dapDebugger.IsEventReceived(json =>
                {
                    Assert.False(m_dapDebugger.IsResponseContainProperty(json, "event", "stopped"), callerTrace);
                    if (m_dapDebugger.IsResponseContainProperty(json, "event", "exited"))
                    {
                        exited = true;
                        Assert.NotEqual(0, JsonConvert.DeserializeObject<ExitedEvent>(json)!.body.exitCode, callerTrace);
                    }
                    terminated |= m_dapDebugger.IsResponseContainProperty(json, "event", "terminated");
                    return exited && terminated;
                }), callerTrace);
            }
        }
        DebuggerExit(callerTrace);
    }
}
}
