using System;

using DbgTest;
using DbgTest.Script;

namespace TestStringEscaping
{
class Program
{
    static void Main(string[] args)
    {
        Label.Checkpoint("init", "escaping",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.Initialize(@"__FILE__:__LINE__");
                Context.AddBreakpoint(@"__FILE__:__LINE__", "bp");
                Context.SetBreakpoints(@"__FILE__:__LINE__");
                Context.ConfigurationDone(@"__FILE__:__LINE__");
                Context.Launch(@"__FILE__:__LINE__");
                Context.WasEntryPointHit(@"__FILE__:__LINE__");
                Context.Continue(@"__FILE__:__LINE__");
            });

        string controls = "\a\b\f\n\r\t\v\"'\\";
        char bell = '\a';
        char backspace = '\b';
        char formFeed = '\f';
        char verticalTab = '\v';
        GC.KeepAlive((controls, bell, backspace, formFeed, verticalTab));   Label.Breakpoint("bp");
        GC.KeepAlive(args);

        Label.Checkpoint("escaping", "finish",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.WasBreakpointHit(@"__FILE__:__LINE__", "bp");
                long frameId = Context.DetectFrameId(@"__FILE__:__LINE__", "bp");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId,
                    "\"\\a\\b\\f\\n\\r\\t\\v\\\"'\\\\\"", "string", "controls");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "7 '\\a'", "char", "bell");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "8 '\\b'", "char", "backspace");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "12 '\\f'", "char", "formFeed");
                Context.GetAndCheckValue(@"__FILE__:__LINE__", frameId, "11 '\\v'", "char", "verticalTab");
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
