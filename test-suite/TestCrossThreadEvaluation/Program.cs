using System;
using System.Diagnostics;
using DbgTest;
using DbgTest.DAP;
using DbgTest.Script;
using Newtonsoft.Json;

namespace TestCrossThreadEvaluation
{
public struct TestStruct5
{
    public int val1 { get { return 111; } }
    public int val2
    {
        get
        {
            Debugger.NotifyOfCrossThreadDependency();
            return 222;
        }
    }
    public string val3 { get { return "text_333"; } }
    public float val4
    {
        get
        {
            Debugger.NotifyOfCrossThreadDependency();
            return 444.4f;
        }
    }
    public float val5 { get { return 555.5f; } }
}

class Program
{
    static void Main(string[] args)
    {
        Label.Checkpoint("init", "inspect",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.Initialize(@"__FILE__:__LINE__");
                Context.AddBreakpoint(@"__FILE__:__LINE__", "inspect");
                Context.AddBreakpoint(@"__FILE__:__LINE__", "after");
                Context.SetBreakpoints(@"__FILE__:__LINE__");
                Context.ConfigurationDone(@"__FILE__:__LINE__");
                Context.Launch(@"__FILE__:__LINE__");
                Context.WasEntryPointHit(@"__FILE__:__LINE__");
                Context.Continue(@"__FILE__:__LINE__");
            });

        TestStruct5 value = new TestStruct5();
        Debugger.NotifyOfCrossThreadDependency();
        GC.KeepAlive(value);                                           Label.Breakpoint("inspect");

        Label.Checkpoint("inspect", "after",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.CheckCrossThreadEvaluation(@"__FILE__:__LINE__", "inspect");
                Context.Continue(@"__FILE__:__LINE__");
            });

        GC.KeepAlive(value);                                           Label.Breakpoint("after");

        Label.Checkpoint("after", "finish",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.CheckCrossThreadEvaluation(@"__FILE__:__LINE__", "after");
                Context.Continue(@"__FILE__:__LINE__");
            });

        Label.Checkpoint("finish", "",
            (Object context) =>
            {
                Context Context = (Context)context;
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
    public void CheckCrossThreadEvaluation(string trace, string breakpoint)
    {
        WasBreakpointHit(trace, breakpoint);
        Int64 frameId = DetectFrameId(trace, breakpoint);
        int locals = GetVariablesReference(trace, frameId, "Locals");
        int reference = GetChildVariablesReference(trace, locals, "value");
        string[] names = { "val1", "val2", "val3", "val4", "val5" };
        string[] values = { "111", "<error>", "\"text_333\"", "<error>", "555.5" };
        string[] types = { "int", "", "string", "", "float" };

        for (int request = 0; request < 20; request++)
        {
            var variables = new VariablesRequest();
            variables.arguments.variablesReference = reference;
            var response = m_dapDebugger.Request(variables);
            Assert.True(response.Success, trace);
            var result = JsonConvert.DeserializeObject<VariablesResponse>(response.ResponseStr)!;
            string requestTrace = trace + "\n" + breakpoint + " variables request " + request;
            Assert.Equal(names.Length, result.body.variables.Count, requestTrace);
            for (int index = 0; index < names.Length; index++)
            {
                var variable = result.body.variables[index];
                Assert.Equal(names[index], variable.name, requestTrace);
                Assert.Equal(values[index], variable.value, requestTrace);
                Assert.Equal(types[index], variable.type, requestTrace);
            }
            GetAndCheckValue(requestTrace, frameId, "111", "int", "value.val1");
        }
    }
}
}
