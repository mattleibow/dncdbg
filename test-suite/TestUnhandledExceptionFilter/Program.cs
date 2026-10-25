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
    public void CheckUnhandledExceptionFilters(string caller_trace)
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
            Initialize(caller_trace);
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
            Assert.True(DAPDebugger.Request(request).Success, caller_trace);
            ConfigurationDone(caller_trace);
            Launch(caller_trace, JMC: true);
            WasEntryPointHit(caller_trace);
            Continue(caller_trace);
            if (scenario.Thrown)
            {
                WasExceptionBreakpointHit(caller_trace, "throw", "CLR", "always", "System.InvalidOperationException");
                Continue(caller_trace);
            }
            if (scenario.Stop)
            {
                WasExceptionBreakpointHit(caller_trace, "throw", "CLR", "unhandled", "System.InvalidOperationException");
                Continue(caller_trace);
                WasExit(caller_trace, CheckExitCode: null);
            }
            else
            {
                bool exited = false;
                bool terminated = false;
                Assert.True(DAPDebugger.IsEventReceived(json =>
                {
                    Assert.False(DAPDebugger.IsResponseContainProperty(json, "event", "stopped"), caller_trace);
                    if (DAPDebugger.IsResponseContainProperty(json, "event", "exited"))
                    {
                        exited = true;
                        Assert.NotEqual(0, JsonConvert.DeserializeObject<ExitedEvent>(json)!.body.exitCode, caller_trace);
                    }
                    terminated |= DAPDebugger.IsResponseContainProperty(json, "event", "terminated");
                    return exited && terminated;
                }), caller_trace);
            }
        }
        DebuggerExit(caller_trace);
    }
}
}
