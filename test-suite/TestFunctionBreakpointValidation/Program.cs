using System;
using System.Collections.Generic;
using DbgTest;
using DbgTest.DAP;
using DbgTest.Script;
using Newtonsoft.Json;

namespace TestFunctionBreakpointValidation
{
class Program
{
    static void Plain()
    {                                                                 Label.Breakpoint("plain");
        GC.KeepAlive(0);
    }

    static void Typed(int value)
    {                                                                 Label.Breakpoint("typed");
        GC.KeepAlive(value);
    }

    static void Qualified()
    {                                                                 Label.Breakpoint("qualified");
        GC.KeepAlive(0);
    }

    static void Empty()
    {                                                                 Label.Breakpoint("empty");
        GC.KeepAlive(0);
    }

    static void Overload(int value)
    {                                                                 Label.Breakpoint("integer");
        GC.KeepAlive(value);
    }

    static void Overload(string value)
    {                                                                 Label.Breakpoint("string");
        GC.KeepAlive(value);
    }

    static void ReplacementBefore() => GC.KeepAlive(0);
    static void ReplacementAfter() => GC.KeepAlive(0);

    static void Main(string[] args)
    {
        Label.Checkpoint("init", "validate_bound",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.Initialize(@"__FILE__:__LINE__");
                Context.CheckAcceptedFunctionNames(@"__FILE__:__LINE__");
                Context.InstallFunctionValidationBreakpoints(@"__FILE__:__LINE__");
                Context.CheckRejectedFunctionNames(@"__FILE__:__LINE__");
                Context.AddBreakpoint(@"__FILE__:__LINE__", "stage");
                Context.SetBreakpoints(@"__FILE__:__LINE__");
                Context.ConfigurationDone(@"__FILE__:__LINE__");
                Context.Launch(@"__FILE__:__LINE__");
                Context.WasEntryPointHit(@"__FILE__:__LINE__");
                Context.Continue(@"__FILE__:__LINE__");
            });

        Plain();
        GC.KeepAlive(args);                                            Label.Breakpoint("stage");
        ReplacementBefore();
        Plain();
        ReplacementAfter();
        Typed(1);
        Typed(2);
        Qualified();
        Empty();
        Overload(42);
        Overload("42");

        Label.Checkpoint("validate_bound", "finish",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.WasBreakpointHit(@"__FILE__:__LINE__", "stage");
                Context.CheckRejectedFunctionNames(@"__FILE__:__LINE__");
                Context.RemoveBreakpoint(@"__FILE__:__LINE__", "stage");
                Context.SetBreakpoints(@"__FILE__:__LINE__");
                foreach (string label in new[] { "plain", "typed", "qualified", "empty", "integer", "string" })
                {
                    Context.Continue(@"__FILE__:__LINE__");
                    Context.WasBreakpointHit(@"__FILE__:__LINE__", label);
                }
                Context.ClearFunctionValidationBreakpoints(@"__FILE__:__LINE__");
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
    public void CheckAcceptedFunctionNames(string trace)
    {
        foreach (string name in new[]
        {
            "Plain", "Program.Typed", "TestFunctionBreakpointValidation.Program.Qualified",
            "Empty()", "Overload(int)", "Overload(string)", "", "Missing)",
            "Missing((int)", "Missing)Name(int)", " Missing", "Missing(unknown)",
        })
        {
            var result = m_dapDebugger.Request(FunctionNames(new FunctionBreakpoint(name)));
            Assert.True(result.Success, trace + ": " + name);
            Assert.Equal(1, JsonConvert.DeserializeObject<SetBreakpointsResponse>(result.ResponseStr)!.body.breakpoints.Count,
                trace + ": " + name);
        }
    }

    public void InstallFunctionValidationBreakpoints(string trace)
    {
        var result = m_dapDebugger.Request(FunctionNames(
            new FunctionBreakpoint("Plain", hitCondition: "==2"),
            new FunctionBreakpoint("Program.Typed(int)", condition: "value == 2", hitCondition: "==1"),
            new FunctionBreakpoint("TestFunctionBreakpointValidation.Program.Qualified"),
            new FunctionBreakpoint("Empty()"),
            new FunctionBreakpoint("Overload(int)"),
            new FunctionBreakpoint("Overload(string)")));
        Assert.True(result.Success, trace);
        Assert.Equal(6, JsonConvert.DeserializeObject<SetBreakpointsResponse>(result.ResponseStr)!.body.breakpoints.Count, trace);
    }

    public void CheckRejectedFunctionNames(string trace)
    {
        foreach (string name in new[]
        {
            "()", "(int)", "Foo(", "Foo(int", "Foo()tail", "Foo(int)tail",
            "Foo())", "Foo()()", "Foo((int))", "Foo() ", "Foo()\t", "Foo)(int",
        })
        {
            Check(FunctionNames(new FunctionBreakpoint(name)), NameError(name));
            Check(FunctionNames(
                new FunctionBreakpoint("TestFunctionBreakpointValidation.Program.ReplacementBefore"),
                new FunctionBreakpoint(name),
                new FunctionBreakpoint("TestFunctionBreakpointValidation.Program.ReplacementAfter")), NameError(name));
        }

        // Native validates each name before its hit condition, in request order.
        Check(FunctionNames(new FunctionBreakpoint("First(", hitCondition: "%0")), NameError("First("));
        Check(FunctionNames(
            new FunctionBreakpoint("Plain", hitCondition: "%0"),
            new FunctionBreakpoint("Second(")), "Invalid hit condition: N must be greater than 0 for %N");
        Check(FunctionNames(
            new FunctionBreakpoint("First("),
            new FunctionBreakpoint("Plain", hitCondition: "%0")), NameError("First("));
        Check(FunctionNames(new FunctionBreakpoint("First("), new FunctionBreakpoint("Second(")), NameError("First("));

        void Check(SetFunctionBreakpointsRequest request, string expected)
        {
            var result = m_dapDebugger.Request(request);
            Assert.False(result.Success, trace + ": " + expected);
            var response = JsonConvert.DeserializeObject<Response>(result.ResponseStr)!;
            Assert.Equal("setFunctionBreakpoints", response.command, trace);
            Assert.Equal(expected, response.message, trace);
        }
    }

    public void ClearFunctionValidationBreakpoints(string trace)
    {
        Assert.True(m_dapDebugger.Request(FunctionNames()).Success, trace);
    }

    static SetFunctionBreakpointsRequest FunctionNames(params FunctionBreakpoint[] breakpoints) => new()
    {
        arguments = new SetFunctionBreakpointsArguments
        {
            breakpoints = new List<FunctionBreakpoint>(breakpoints),
        },
    };

    static string NameError(string name) =>
        "Cannot parse the function breakpoint name '" + name +
        "'. The parameter list must be enclosed in parentheses at the end of the name, e.g. 'Foo(Bar)'.";
}
}
