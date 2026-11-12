using System;
using System.Collections.Generic;
using DbgTest;
using DbgTest.DAP;
using DbgTest.Script;
using Newtonsoft.Json;

namespace TestExceptionOptionTolerance
{
class Program
{
    static void Main(string[] args)
    {
        Label.Checkpoint("init", "options", (Object context) =>
        {
            Context Context = (Context)context;
            Context.Initialize(@"__FILE__:__LINE__");
            Context.AddBreakpoint(@"__FILE__:__LINE__", "after");
            Context.SetBreakpoints(@"__FILE__:__LINE__");
            Context.ConfigurationDone(@"__FILE__:__LINE__");
            Context.Launch(@"__FILE__:__LINE__", jmc: true);
            Context.WasEntryPointHit(@"__FILE__:__LINE__");
        });

        for (int i = 0; i < Context.ExceptionOptionPasses; i++)
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

        Label.Checkpoint("options", "finish", (Object context) =>
        {
            Context Context = (Context)context;
            Context.CheckExceptionOptions(@"__FILE__:__LINE__");
        });
        Label.Checkpoint("finish", "", (Object context) =>
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
    const string InvalidType = "System.InvalidOperationException";
    const string NullType = "System.NullReferenceException";
    const string BothTypes = InvalidType + "," + NullType;

    public static int ExceptionOptionPasses => AcceptedExceptionOptions().Count + RejectedExceptionOptions().Count * 2;

    static List<(string Arguments, string Expected)> AcceptedExceptionOptions()
    {
        var cases = new List<(string, string)>
        {
            ("{}", ""),
            ("{\"filters\":[]}", ""),
            ("{\"filters\":[\"all\"]}", BothTypes),
            ("{\"filterOptions\":[]}", ""),
        };
        foreach (string filter in new[] { "all", "user-unhandled", "unhandled" })
        foreach (string condition in new[] { "", ",\"condition\":\"\"", ",\"condition\":null", ",\"condition\":42",
            ",\"condition\":false", ",\"condition\":[]", ",\"condition\":{\"type\":\"ignored\"}" })
            cases.Add(("{\"filterOptions\":[{\"filterId\":\"" + filter + "\"" + condition +
                ",\"extraNull\":null,\"extraNumber\":42,\"extraBoolean\":true,\"extraArray\":[1],\"extraObject\":{\"nested\":[]}}]}",
                filter == "all" ? BothTypes : ""));
        cases.AddRange(new[]
        {
            ("{\"filterOptions\":[{\"filterId\":\"all\",\"condition\":\"" + InvalidType + "\"}]}", InvalidType),
            ("{\"filterOptions\":[{\"filterId\":\"all\",\"condition\":\"" + NullType + "\"}]}", NullType),
            ("{\"filters\":[\"all\"],\"filterOptions\":[{\"filterId\":\"all\",\"condition\":\"" + InvalidType + "\"}]}", BothTypes),
            ("{\"filters\":[\"all\"],\"filterOptions\":[{\"filterId\":\"all\",\"condition\":\"!" + InvalidType + "\"}]}", NullType),
            ("{\"filters\":[\"user-unhandled\"],\"filterOptions\":[{\"filterId\":\"all\",\"condition\":\"" + InvalidType + "\"}]}", InvalidType),
            ("{\"filters\":[\"unhandled\"],\"filterOptions\":[{\"filterId\":\"all\",\"condition\":\"" + NullType + "\"}]}", NullType),
            ("{\"filterOptions\":[{\"filterId\":\"all\",\"condition\":\"" + InvalidType + "\"},{\"filterId\":\"all\",\"condition\":\"" + NullType + "\"}]}", BothTypes),
            ("{\"filterOptions\":[{\"filterId\":\"all\",\"condition\":\"" + NullType + "\"},{\"filterId\":\"all\",\"condition\":\"" + InvalidType + "\"}]}", BothTypes),
            ("{\"filterOptions\":[{\"filterId\":\"all\",\"condition\":\"*\"},{\"filterId\":\"all\",\"condition\":\"!" + InvalidType + "\"}]}", NullType),
            ("{\"filterOptions\":[{\"filterId\":\"all\",\"condition\":\"!" + InvalidType + "\"},{\"filterId\":\"all\",\"condition\":\"*\"}]}", NullType),
            ("{\"filterOptions\":[{\"filterId\":\"all\",\"condition\":{}},{\"filterId\":\"all\",\"condition\":\"!" + InvalidType + "\"}]}", NullType),
            ("{\"filterOptions\":[{\"filterId\":\"all\",\"condition\":\"!" + InvalidType + "\"},{\"filterId\":\"all\",\"condition\":42}]}", NullType),
            ("{\"filterOptions\":[{\"filterId\":\"all\",\"condition\":\"" + InvalidType + "\"},{\"filterId\":\"all\",\"condition\":false}]}", BothTypes),
            ("{\"filterOptions\":[{\"filterId\":\"all\",\"condition\":[]},{\"filterId\":\"all\",\"condition\":\"" + NullType + "\"}]}", BothTypes),
        });
        return cases;
    }

    static List<string> RejectedExceptionOptions()
    {
        var cases = new List<string>();
        foreach (string field in new[] { "filters", "filterOptions" })
        foreach (string value in new[] { "null", "42", "false", "\"all\"", "{}" })
            cases.Add("{\"" + field + "\":" + value + "}");
        foreach (string value in new[] { "null", "42", "false", "[]", "{}", "\"\"", "\"missing\"" })
            cases.Add("{\"filters\":[\"all\"," + value + "]}");
        foreach (string entry in new[] { "null", "42", "false", "[]", "\"all\"", "{}",
            "{\"condition\":\"" + InvalidType + "\"}", "{\"filterId\":\"\"}", "{\"filterId\":\"missing\"}",
            "{\"filterId\":null}", "{\"filterId\":42}", "{\"filterId\":false}", "{\"filterId\":[]}", "{\"filterId\":{}}" })
        {
            cases.Add("{\"filterOptions\":[" + entry + "]}");
            cases.Add("{\"filters\":[\"all\"],\"filterOptions\":[{\"filterId\":\"all\",\"condition\":\"" +
                NullType + "\"}," + entry + ",{\"filterId\":\"all\"}]}");
        }
        cases.Add("{\"filters\":[\"all\",\"missing\"],\"filterOptions\":[{\"filterId\":\"all\"}]}");
        return cases;
    }

    sealed class RawExceptionOptionsRequest : Request
    {
        public Newtonsoft.Json.Linq.JObject arguments = new();
    }

    public void CheckExceptionOptions(string trace)
    {
        foreach (var scenario in AcceptedExceptionOptions())
        {
            Send(scenario.Arguments, true);
            CheckStops(scenario.Expected, scenario.Arguments);
        }
        foreach (string invalid in RejectedExceptionOptions())
        {
            Send("{\"filterOptions\":[{\"filterId\":\"all\",\"condition\":\"" + InvalidType + "\"}]}", true);
            Send(invalid, false);
            CheckStops(InvalidType, "failed replacement " + invalid);
            Send("{\"filters\":[\"all\"],\"filterOptions\":[{\"filterId\":\"all\",\"condition\":\"!" + InvalidType + "\"}]}", true);
            CheckStops(NullType, "same-command recovery " + invalid);
        }

        void Send(string arguments, bool expectedSuccess)
        {
            var request = new RawExceptionOptionsRequest
            {
                command = "setExceptionBreakpoints",
                arguments = Newtonsoft.Json.Linq.JObject.Parse(arguments),
            };
            var result = m_dapDebugger.Request(request);
            var response = Newtonsoft.Json.Linq.JObject.Parse(result.ResponseStr);
            Assert.Equal(expectedSuccess, result.Success, trace + ": " + arguments + ": " + result.ResponseStr);
            Assert.Equal(request.seq, (int)response["request_seq"]!, trace);
            Assert.Equal("setExceptionBreakpoints", (string?)response["command"], trace);
            Assert.Equal("response", (string?)response["type"], trace);
            if (!expectedSuccess)
                Assert.True(!string.IsNullOrEmpty((string?)response["message"]), trace + ": " + arguments);
        }

        void CheckStops(string expected, string scenario)
        {
            Continue(trace);
            var hits = new List<string>();
            for (int stops = 0; ; stops++)
            {
                Assert.True(stops <= 2, trace + ": " + scenario);
                string reason = "";
                Assert.True(m_dapDebugger.IsEventReceived(json =>
                {
                    if (!m_dapDebugger.IsResponseContainProperty(json, "event", "stopped"))
                        return false;
                    var stopped = JsonConvert.DeserializeObject<StoppedEvent>(json)!;
                    m_threadId = stopped.body.threadId!.Value;
                    reason = stopped.body.reason;
                    return true;
                }), trace);
                var stack = new StackTraceRequest();
                stack.arguments.threadId = m_threadId;
                stack.arguments.levels = 1;
                var result = m_dapDebugger.Request(stack);
                Assert.True(result.Success, trace);
                var frame = JsonConvert.DeserializeObject<StackTraceResponse>(result.ResponseStr)!.body.stackFrames[0];
                Assert.Equal(m_controlInfo.SourceFilesPath, frame.source.path, trace);
                if (reason == "breakpoint")
                {
                    Assert.Equal(((LineBreakpoint)m_controlInfo.Breakpoints["after"]).NumLine, frame.line, trace);
                    break;
                }
                Assert.Equal("exception", reason, trace);
                string type;
                if (frame.line == ((LineBreakpoint)m_controlInfo.Breakpoints["invalid"]).NumLine)
                    type = InvalidType;
                else
                {
                    Assert.Equal(((LineBreakpoint)m_controlInfo.Breakpoints["null"]).NumLine, frame.line, trace);
                    type = NullType;
                }
                TestExceptionInfo(trace, "CLR", "always", type);
                hits.Add(type);
                Continue(trace);
            }
            Assert.Equal(expected, string.Join(",", hits), trace + ": " + scenario);
        }
    }
}
}
