using System;

using DbgTest;
using DbgTest.Script;

namespace TestContinueResponse
{
class Program
{
    static void Main(string[] args)
    {
        Label.Checkpoint("init", "omitted",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.Initialize(@"__FILE__:__LINE__");
                Context.AddBreakpoint(@"__FILE__:__LINE__", "first");
                Context.AddBreakpoint(@"__FILE__:__LINE__", "second");
                Context.AddBreakpoint(@"__FILE__:__LINE__", "third");
                Context.SetBreakpoints(@"__FILE__:__LINE__");
                Context.ConfigurationDone(@"__FILE__:__LINE__");
                Context.Launch(@"__FILE__:__LINE__");
                Context.WasEntryPointHit(@"__FILE__:__LINE__");
                Context.Continue(@"__FILE__:__LINE__", SingleThread: false, ExpectedAllThreadsContinued: true);
            });

        GC.KeepAlive(args);                                               Label.Breakpoint("first");

        Label.Checkpoint("omitted", "single_thread",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.WasBreakpointHit(@"__FILE__:__LINE__", "first");
                Context.Continue(@"__FILE__:__LINE__", ExpectedAllThreadsContinued: true);
            });

        GC.KeepAlive(args);                                               Label.Breakpoint("second");

        Label.Checkpoint("single_thread", "resume_all",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.WasBreakpointHit(@"__FILE__:__LINE__", "second");
                Context.Continue(@"__FILE__:__LINE__", SingleThread: true, ExpectedAllThreadsContinued: false);
            });

        GC.KeepAlive(args);                                               Label.Breakpoint("third");

        Label.Checkpoint("resume_all", "finish",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.WasBreakpointHit(@"__FILE__:__LINE__", "third");
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
