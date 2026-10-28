using System;

using DbgTest;
using DbgTest.Script;

namespace TestUnhandledExceptionCondition
{
class Program
{
    static void ThrowExcludedException()
    {
        throw new InvalidOperationException("excluded by exception conditions"); Label.Breakpoint("throw");
    }

    static void Main(string[] args)
    {
        Label.Checkpoint("init", "caught",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.Initialize(@"__FILE__:__LINE__");
                Context.AddBreakpoint(@"__FILE__:__LINE__", "after_caught");
                Context.SetBreakpoints(@"__FILE__:__LINE__");
                Context.AddExceptionBreakpointFilterAllWithConditions("!System.InvalidOperationException");
                Context.AddExceptionBreakpointFilterUserUnhandledWithConditions("!System.InvalidOperationException");
                Context.SetExceptionBreakpoints(@"__FILE__:__LINE__");
                Context.ConfigurationDone(@"__FILE__:__LINE__");
                Context.Launch(@"__FILE__:__LINE__", jmc: true);
                Context.WasEntryPointHit(@"__FILE__:__LINE__");
                Context.Continue(@"__FILE__:__LINE__");
            });

        try
        {
            ThrowExcludedException();
        }
        catch (InvalidOperationException)
        {
        }
        GC.KeepAlive(args);                                               Label.Breakpoint("after_caught");

        Label.Checkpoint("caught", "unhandled",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.WasBreakpointHit(@"__FILE__:__LINE__", "after_caught");
                Context.Continue(@"__FILE__:__LINE__");
            });

        ThrowExcludedException();

        Label.Checkpoint("unhandled", "finish",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.WasExceptionBreakpointHit(@"__FILE__:__LINE__", "throw", "CLR", "unhandled",
                                                  "System.InvalidOperationException");
            });

        Label.Checkpoint("finish", "",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.AbortExecution(@"__FILE__:__LINE__");
                Context.WasExit(@"__FILE__:__LINE__", checkExitCode: null);
                Context.DebuggerExit(@"__FILE__:__LINE__");
            });
    }
}
}
