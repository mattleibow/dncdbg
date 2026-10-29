using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
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
    public static readonly ManualResetEventSlim Entered = new ManualResetEventSlim();
    public static readonly ManualResetEventSlim Released = new ManualResetEventSlim();

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

        using var ready = new ManualResetEventSlim();
        var worker = new System.Threading.Thread(() =>
        {
            ready.Set();
            Entered.Wait();
            Released.Set();
        });
        worker.Start();
        ready.Wait();
        Console.WriteLine("RUNTIME " + RuntimeInformation.FrameworkDescription);
        TestStruct5 value = new TestStruct5();
        WorkerDependency blocked = new WorkerDependency();
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
        GC.KeepAlive(blocked);
        worker.Join();

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

public struct WorkerDependency
{
    public int val1 { get { return 111; } }
    public int val2
    {
        get
        {
            Program.Entered.Set();
            Program.Released.Wait();
            return 222;
        }
    }
    public string val3 { get { return "text_333"; } }
    public float val5 { get { return 555.5f; } }
}
}

namespace DbgTest.Script
{
partial class Context
{
    public void CheckWorkerDependency(string trace)
    {
        Int64 frameId = DetectFrameId(trace, "inspect");
        int locals = GetVariablesReference(trace, frameId, "Locals");
        int reference = GetChildVariablesReference(trace, locals, "blocked");
        EvalVariable(trace, reference, "", "val2", "<error>");
        GetAndCheckValue(trace, frameId, "111", "int", "blocked.val1");
        GetAndCheckValue(trace, frameId, "\"text_333\"", "string", "blocked.val3");
        GetAndCheckValue(trace, frameId, "555.5", "float", "blocked.val5");
    }

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
            if (breakpoint == "inspect" && request == 9)
                CheckWorkerDependency(requestTrace);
        }
    }
}
}
