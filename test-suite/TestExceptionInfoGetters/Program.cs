using System;

using DbgTest;
using DbgTest.DAP;
using DbgTest.Script;
using Newtonsoft.Json;

namespace TestExceptionInfoGetters
{
class ThrowingGettersException : Exception
{
    public ThrowingGettersException(string message, Exception inner) : base(message, inner) { }
    public override string StackTrace => throw new InvalidOperationException("stack getter failed");
    public override string Source => throw new InvalidOperationException("source getter failed");
}

class ThrowingSourceException : Exception
{
    public ThrowingSourceException(string message, Exception inner) : base(message, inner) { }
    public override string Source => throw new InvalidOperationException("source getter failed");
}

class WrongTypeMembersException : Exception
{
    public WrongTypeMembersException(string message, Exception inner) : base(message, inner) { }
    public object _message = new object();
    public new int StackTrace => 42;
    public new object Source => new object();
    public new int InnerException => 17;
}

class NullMembersException : Exception
{
    public NullMembersException(string message, Exception inner) : base(message, inner) { }
    public object? _message = null;
    public override string? StackTrace => null;
    public override string? Source => null;
    public new Exception? InnerException => null;
}

class Program
{
    static void Main()
    {
        Label.Checkpoint("init", "getters",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.Initialize(@"__FILE__:__LINE__");
                Context.AddExceptionBreakpointFilterAll();
                Context.SetExceptionBreakpoints(@"__FILE__:__LINE__");
                Context.ConfigurationDone(@"__FILE__:__LINE__");
                Context.Launch(@"__FILE__:__LINE__", jmc: true);
                Context.WasEntryPointHit(@"__FILE__:__LINE__");
                Context.Continue(@"__FILE__:__LINE__");
            });

        Console.WriteLine("RUNTIME " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        Exception leaf = new InvalidOperationException("leaf");
        Exception[] exceptions =
        {
            new ThrowingGettersException("throwing", new ArgumentException("middle", leaf)),
            new ThrowingSourceException("throwing source", leaf),
            new WrongTypeMembersException("wrong type", leaf),
            new NullMembersException("null members", leaf),
            new Exception("ordinary", leaf),
        };
        foreach (Exception exception in exceptions)
        {
            try
            {
                throw exception;                                         Label.Breakpoint("throw");
            }
            catch (Exception)
            {
                Console.WriteLine("caught");
            }
        }

        Label.Checkpoint("getters", "finish",
            (Object context) =>
            {
                Context Context = (Context)context;
                Context.CheckExceptionInfoGetters(@"__FILE__:__LINE__");
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
    public void CheckExceptionInfoGetters(string callerTrace)
    {
        var cases = new (string Type, string Message, bool HasStack, string[] InnerTypes, string[] InnerMessages)[]
        {
            ("TestExceptionInfoGetters.ThrowingGettersException", "throwing", false,
                new[] { "System.ArgumentException", "System.InvalidOperationException" }, new[] { "middle", "leaf" }),
            ("TestExceptionInfoGetters.ThrowingSourceException", "throwing source", true,
                new[] { "System.InvalidOperationException" }, new[] { "leaf" }),
            ("TestExceptionInfoGetters.WrongTypeMembersException", "wrong type", true,
                new[] { "System.InvalidOperationException" }, new[] { "leaf" }),
            ("TestExceptionInfoGetters.NullMembersException", "null members", false,
                new[] { "System.InvalidOperationException" }, new[] { "leaf" }),
            ("System.Exception", "ordinary", true,
                new[] { "System.InvalidOperationException" }, new[] { "leaf" }),
        };
        foreach (var scenario in cases)
        {
            WasExceptionBreakpointHit(callerTrace, "throw", "CLR", "always", scenario.Type);
            for (int repeat = 0; repeat < 2; repeat++)
            {
                var request = new ExceptionInfoRequest();
                request.arguments.threadId = m_threadId;
                var result = m_dapDebugger.Request(request);
                Assert.True(result.Success, callerTrace);
                var response = JsonConvert.DeserializeObject<ExceptionInfoResponse>(result.ResponseStr, ExceptionInfoJsonSettings)!;
                Assert.Equal("always", response.body.breakMode, callerTrace);
                Assert.Equal("CLR/" + scenario.Type, response.body.exceptionId, callerTrace);
                ExceptionDetails node = response.body.details!;
                Assert.Equal(scenario.Type, node.fullTypeName, callerTrace);
                Assert.NotNull(node.message, callerTrace + "\n" + scenario.Type);
                Assert.Equal(scenario.Message, node.message, callerTrace);
                if (scenario.HasStack)
                    Assert.True(node.stackTrace.Contains("TestExceptionInfoGetters.Program.Main()"), callerTrace);
                else
                    Assert.Equal("", node.stackTrace, callerTrace);
                for (int depth = 0; depth < scenario.InnerTypes.Length; depth++)
                {
                    Assert.Equal(1, node.innerException?.Count ?? 0, callerTrace);
                    node = node.innerException![0];
                    Assert.Equal(scenario.InnerTypes[depth], node.fullTypeName, callerTrace);
                    Assert.Equal(scenario.InnerMessages[depth], node.message, callerTrace);
                }
                Assert.Equal(0, node.innerException?.Count ?? 0, callerTrace);
            }
            Continue(callerTrace);
        }
    }
}
}
