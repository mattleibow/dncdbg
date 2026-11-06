using System;
using System.Threading;
using System.Threading.Tasks;
using DbgTest;
using DbgTest.Script;
using Newtonsoft.Json;

namespace TestLogpointNames
{
class Program
{
    static void Main(string[] args)
    {
        Label.Checkpoint("init", "inspect", (Object context) =>
        {
            Context Context = (Context)context;
            Context.Initialize(@"__FILE__:__LINE__");
            foreach (string name in new[] { "main", "named", "unnamed", "renamed", "generic" })
                Context.AddBreakpoint(@"__FILE__:__LINE__", name, logMessage: Context.NameTokenTemplate(name));
            Context.AddBreakpoint(@"__FILE__:__LINE__", "async", logMessage: "async={$FUNCTION}|plain={$FUNCTION,nq}");
            Context.AddBreakpoint(@"__FILE__:__LINE__", "after");
            Context.SetBreakpoints(@"__FILE__:__LINE__");
            Context.ConfigurationDone(@"__FILE__:__LINE__");
            Context.Launch(@"__FILE__:__LINE__");
            Context.WasEntryPointHit(@"__FILE__:__LINE__");
            Context.Continue(@"__FILE__:__LINE__");
        });

        GC.KeepAlive(args);                                           Label.Breakpoint("main");
        Thread.CurrentThread.Name = "named \"$PID\"\\thread\\";
        Named(42, "value");
        var worker = new Thread(Worker);
        worker.Start();
        worker.Join();
        Generic(42);
        Async(42, "value").GetAwaiter().GetResult();
        GC.KeepAlive(args);                                           Label.Breakpoint("after");

        Label.Checkpoint("inspect", "finish", (Object context) =>
        {
            Context Context = (Context)context;
            Context.WasBreakpointHit(@"__FILE__:__LINE__", "after");
            Context.CheckNameTokenOutput(@"__FILE__:__LINE__");
            Context.Continue(@"__FILE__:__LINE__");
        });
        Label.Checkpoint("finish", "", (Object context) =>
        {
            Context Context = (Context)context;
            Context.WasExit(@"__FILE__:__LINE__");
            Context.Restart(@"__FILE__:__LINE__");
            Context.WasEntryPointHit(@"__FILE__:__LINE__");
            Context.Continue(@"__FILE__:__LINE__");
            Context.WasBreakpointHit(@"__FILE__:__LINE__", "after");
            Context.CheckNameTokenOutput(@"__FILE__:__LINE__");
            Context.Continue(@"__FILE__:__LINE__");
            Context.WasExit(@"__FILE__:__LINE__");
            Context.DebuggerExit(@"__FILE__:__LINE__");
        });
    }

    static void Named(int number, string text)
    {
        GC.KeepAlive(number);                                         Label.Breakpoint("named");
        GC.KeepAlive(text);
    }

    static void Worker()
    {
        GC.KeepAlive(Thread.CurrentThread);                            Label.Breakpoint("unnamed");
        Thread.CurrentThread.Name = "worker \"$FUNCTION\"\\name";
        for (int i = 0; i < 2; i++)
            GC.KeepAlive(i);                                          Label.Breakpoint("renamed");
    }

    static void Generic<T>(T value)
    {
        GC.KeepAlive(value);                                          Label.Breakpoint("generic");
    }

    static async Task Async(int number, string text)
    {
        await Task.Yield();
        GC.KeepAlive(number);                                         Label.Breakpoint("async");
        GC.KeepAlive(text);
    }
}
}

namespace DbgTest.Script
{
partial class Context
{
    public string NameTokenTemplate(string label) =>
        label + ":literal=$FUNCTION/$TNAME|function={$FUNCTION}|plain={$FUNCTION,nq}" +
        "|thread={$TNAME}|plainThread={$TNAME,nq}|same={$TNAME == $TNAME}|hit={$HITCOUNT}" +
        "|mixed={$FUNCTION + @\" / \" + $TNAME + @\"\\tail\",nq}" +
        "|mixedFile={$FILEPOS + @\"\\\" + $TNAME,nq}";

    public void CheckNameTokenOutput(string callerTrace)
    {
        CheckNameTokenLine(callerTrace, "main", "Main(string[] args)", "Main Thread", 1);
        CheckNameTokenLine(callerTrace, "named", "Named(int number, string text)", "named \"$PID\"\\thread\\", 1);
        CheckNameTokenLine(callerTrace, "unnamed", "Worker()", "<No name>", 1);
        CheckNameTokenLine(callerTrace, "renamed", "Worker()", "worker \"$FUNCTION\"\\name", 1);
        CheckNameTokenLine(callerTrace, "renamed", "Worker()", "worker \"$FUNCTION\"\\name", 2);
        CheckNameTokenLine(callerTrace, "generic", "Generic<int>(int value)", "named \"$PID\"\\thread\\", 1);
        string function = "TestLogpointNames.Program.Async(int number, string text)";
        WasOutputEvent(callerTrace, "console", "async=" + JsonConvert.SerializeObject(function) + "|plain=" + function + "\n");
    }

    void CheckNameTokenLine(string callerTrace, string label, string method, string thread, int hit)
    {
        string function = "TestLogpointNames.Program." + method;
        string quotedThread = JsonConvert.SerializeObject(thread);
        string quotedMixed = JsonConvert.SerializeObject(function + " / " + thread + "\\tail");
        string quotedMixedFile = JsonConvert.SerializeObject(GetSourceFilesPath() + ":" + GetLineNum(label) + "\\" + thread);
        WasOutputEvent(callerTrace, "console",
            label + ":literal=$FUNCTION/$TNAME|function=" + JsonConvert.SerializeObject(function) + "|plain=" + function +
            "|thread=" + quotedThread + "|plainThread=" + quotedThread.Substring(1, quotedThread.Length - 2) +
            "|same=true|hit=" + hit + "|mixed=" + quotedMixed.Substring(1, quotedMixed.Length - 2) +
            "|mixedFile=" + quotedMixedFile.Substring(1, quotedMixedFile.Length - 2) + "\n");
    }
}
}
