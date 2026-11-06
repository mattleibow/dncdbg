using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using DbgTest;
using DbgTest.Script;
using Newtonsoft.Json;

namespace TestLogpointCallStack
{
class Program
{
    static void Main(string[] args)
    {
        Label.Checkpoint("init", "inspect", (Object context) =>
        {
            Context Context = (Context)context;
            Context.Initialize(@"__FILE__:__LINE__");
            foreach (string name in new[] { "generic", "hidden", "kickoff", "resumed" })
                Context.AddBreakpoint(@"__FILE__:__LINE__", name, logMessage: Context.CallStackTemplate(name));
            Context.AddBreakpoint(@"__FILE__:__LINE__", "after");
            Context.SetBreakpoints(@"__FILE__:__LINE__");
            Context.ConfigurationDone(@"__FILE__:__LINE__");
            Context.Launch(@"__FILE__:__LINE__");
            Context.WasEntryPointHit(@"__FILE__:__LINE__");
            Context.Continue(@"__FILE__:__LINE__");
        });

        for (int i = 0; i < 2; i++)
            Generic(i);
        NonUserOuter();
        var gate = new TaskCompletionSource();
        Task pending = OuterAsync(gate.Task);
        gate.SetResult();
        pending.GetAwaiter().GetResult();
        GC.KeepAlive(args);                                           Label.Breakpoint("after");

        Label.Checkpoint("inspect", "finish", (Object context) =>
        {
            Context Context = (Context)context;
            Context.WasBreakpointHit(@"__FILE__:__LINE__", "after");
            Context.CheckCallStackOutput(@"__FILE__:__LINE__", true);
            Context.Continue(@"__FILE__:__LINE__");
        });
        Label.Checkpoint("finish", "", (Object context) =>
        {
            Context Context = (Context)context;
            Context.WasExit(@"__FILE__:__LINE__");
            foreach (bool jmc in new[] { true, false })
            {
                if (jmc)
                    Context.Restart(@"__FILE__:__LINE__");
                else
                    Context.RestartWithLaunchArguments(@"__FILE__:__LINE__", jmc: false, stepFiltering: false);
                Context.WasEntryPointHit(@"__FILE__:__LINE__");
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasBreakpointHit(@"__FILE__:__LINE__", "after");
                Context.CheckCallStackOutput(@"__FILE__:__LINE__", jmc);
                Context.Continue(@"__FILE__:__LINE__");
                Context.WasExit(@"__FILE__:__LINE__");
            }
            Context.DebuggerExit(@"__FILE__:__LINE__");
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Generic<T>(T value)
    {
        GC.KeepAlive(value);                                          Label.Breakpoint("generic");
    }

    [DebuggerNonUserCode]
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void NonUserOuter() => NonUserInner();

    [DebuggerNonUserCode]
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void NonUserInner() => Visible();

    static void Visible()
    {
        GC.KeepAlive(0);                                              Label.Breakpoint("hidden");
    }

    static async Task OuterAsync(Task gate)
    {
        await InnerAsync(gate);
    }

    static async Task InnerAsync(Task gate)
    {
        GC.KeepAlive(gate);                                           Label.Breakpoint("kickoff");
        await gate;
        GC.KeepAlive(gate);                                           Label.Breakpoint("resumed");
    }
}
}

namespace DbgTest.Script
{
partial class Context
{
    public string CallStackTemplate(string label) =>
        label + ":literal=$CALLSTACK|stack={$CALLSTACK}|plain={$CALLSTACK,nq}" +
        "|same={$CALLSTACK == $CALLSTACK}|hit={$HITCOUNT}" +
        "|mixed={$CALLSTACK + @\" \"\"quoted\"\"\\tail\",nq}";

    public void CheckCallStackOutput(string callerTrace, bool jmc)
    {
        const string prefix = "TestLogpointCallStack.Program.";
        const string main = prefix + "Main(string[] args)";
        string generic = prefix + "Generic<int>(int value)\n" + main;
        CheckCallStackLine(callerTrace, "generic", generic, 1);
        CheckCallStackLine(callerTrace, "generic", generic, 2);
        CheckCallStackLine(callerTrace, "hidden", prefix + "Visible()\n" +
            (jmc ? "[External Code]" : prefix + "NonUserInner()\n" + prefix + "NonUserOuter()") + "\n" + main, 1);
        CheckCallStackLine(callerTrace, "kickoff",
            prefix + "InnerAsync(System.Threading.Tasks.Task gate)\n" +
            prefix + "OuterAsync(System.Threading.Tasks.Task gate)\n" + main, 1);
        if (jmc)
            CheckCallStackLine(callerTrace, "resumed",
                prefix + "InnerAsync(System.Threading.Tasks.Task gate)\n[External Code]\n" + main, 1);
        else
            CheckUnfilteredResumedStack(callerTrace);
    }

    void CheckCallStackLine(string callerTrace, string label, string stack, int hit)
    {
        string quoted = JsonConvert.SerializeObject(stack);
        string mixed = JsonConvert.SerializeObject(stack + " \"quoted\"\\tail");
        WasOutputEvent(callerTrace, "console", label + ":literal=$CALLSTACK|stack=" + quoted +
            "|plain=" + quoted.Substring(1, quoted.Length - 2) + "|same=true|hit=" + hit +
            "|mixed=" + mixed.Substring(1, mixed.Length - 2) + "\n");
    }

    void CheckUnfilteredResumedStack(string callerTrace)
    {
        string output = "";
        Assert.True(m_dapDebugger.IsNotStopEventReceived(json =>
        {
            var message = Newtonsoft.Json.Linq.JObject.Parse(json);
            if ((string?)message["event"] != "output" || (string?)message["body"]?["category"] != "console")
                return false;
            output = (string?)message["body"]?["output"] ?? "";
            return output.StartsWith("resumed:literal=", StringComparison.Ordinal);
        }), callerTrace);
        string[] parts = output.TrimEnd('\n').Split('|');
        Assert.Equal(6, parts.Length, callerTrace + ": " + output);
        string quoted = parts[1].Substring("stack=".Length);
        string stack = JsonConvert.DeserializeObject<string>(quoted)!;
        string[] frames = stack.Split('\n');
        Assert.Equal("TestLogpointCallStack.Program.InnerAsync(System.Threading.Tasks.Task gate)", frames[0], callerTrace);
        Assert.Equal("TestLogpointCallStack.Program.Main(string[] args)", frames[frames.Length - 1], callerTrace);
        Assert.True(Array.Exists(frames, f => f == "System.Threading.Tasks.TaskCompletionSource.SetResult()"), callerTrace);
        for (int i = 1; i < frames.Length - 1; i++)
            Assert.True(frames[i].StartsWith("System.", StringComparison.Ordinal), callerTrace);
        Assert.True(!stack.Contains("OuterAsync") && !stack.Contains("[Async Call Stack]") &&
            !stack.Contains("System.Runtime.CompilerServices.AsyncMethodBuilderCore") &&
            !stack.Contains("System.Runtime.CompilerServices.AsyncTaskMethodBuilder"), callerTrace);
        string mixed = JsonConvert.SerializeObject(stack + " \"quoted\"\\tail");
        Assert.Equal("resumed:literal=$CALLSTACK|stack=" + quoted +
            "|plain=" + quoted.Substring(1, quoted.Length - 2) + "|same=true|hit=1" +
            "|mixed=" + mixed.Substring(1, mixed.Length - 2) + "\n", output, callerTrace);
    }
}
}
