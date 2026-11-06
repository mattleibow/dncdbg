using System;
using DbgTest;
using DbgTest.DAP;
using DbgTest.Script;
using Newtonsoft.Json;

namespace TestLogpointTokens
{
class Program
{
    static void Main(string[] args)
    {
        Label.Checkpoint("init", "inspect", (Object context) =>
        {
            Context Context = (Context)context;
            Context.Initialize(@"__FILE__:__LINE__");
            Context.AddBreakpoint(@"__FILE__:__LINE__", "tokens", logMessage: Context.TokenLogTemplate());
            Context.AddBreakpoint(@"__FILE__:__LINE__", "literal", logMessage: "literal=$FILEPOS\\tail\"quoted\"");
            Context.AddBreakpoint(@"__FILE__:__LINE__", "after");
            Context.SetBreakpoints(@"__FILE__:__LINE__");
            Context.ConfigurationDone(@"__FILE__:__LINE__");
            Context.Launch(@"__FILE__:__LINE__");
            Context.WasEntryPointHit(@"__FILE__:__LINE__");
            Context.CaptureTokenProcessId(@"__FILE__:__LINE__");
            Context.Continue(@"__FILE__:__LINE__");
        });

        string logText = "text \"quoted\"\\tail\nnext\tend";
        for (int i = 0; i < 2; i++)
        {
            GC.KeepAlive(logText);                                    Label.Breakpoint("tokens");
            GC.KeepAlive(args);                                       Label.Breakpoint("literal");
        }
        GC.KeepAlive(args);                                           Label.Breakpoint("after");

        Label.Checkpoint("inspect", "finish", (Object context) =>
        {
            Context Context = (Context)context;
            Context.WasBreakpointHit(@"__FILE__:__LINE__", "after");
            Context.CheckTokenLogOutput(@"__FILE__:__LINE__");
            Context.Continue(@"__FILE__:__LINE__");
        });
        Label.Checkpoint("finish", "", (Object context) =>
        {
            Context Context = (Context)context;
            Context.WasExit(@"__FILE__:__LINE__");
            Context.Restart(@"__FILE__:__LINE__");
            Context.WasEntryPointHit(@"__FILE__:__LINE__");
            Context.CaptureTokenProcessId(@"__FILE__:__LINE__");
            Context.Continue(@"__FILE__:__LINE__");
            Context.WasBreakpointHit(@"__FILE__:__LINE__", "after");
            Context.CheckTokenLogOutput(@"__FILE__:__LINE__");
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
    public string TokenProcessId = "";

    public void CaptureTokenProcessId(string callerTrace)
    {
        var request = new StackTraceRequest();
        request.arguments.threadId = m_threadId;
        var result = m_dapDebugger.Request(request);
        Assert.True(result.Success, callerTrace);
        var stack = JsonConvert.DeserializeObject<StackTraceResponse>(result.ResponseStr)!;
        GetResultAsString(callerTrace, stack.body.stackFrames[0].id, "$pid", out TokenProcessId);
    }

    public string TokenLogTemplate() =>
        "literal=$PID|file={$FILEPOS}|plain={$FILEPOS,nq}|address={$ADDRESS}|same={$ADDRESS == $ADDRESS}" +
        "|tid={$TID}|pid={$PID}|count={$HITCOUNT}|joined={$HITCOUNT$HITCOUNT}|math={$HITCOUNT + 10}|lower={$pid}" +
        "|fileTail={$FILEPOS + @\"\\tail\" + '\"',nq}|quotedFileTail={$FILEPOS + @\"\\tail\" + '\"'}" +
        "|addressTail={$ADDRESS + @\"\\tail\" + '\"',nq}|countTail={$HITCOUNT + @\"\\tail\" + '\"',nq}" +
        "|text={logText,nq}|textTail={logText + @\"\\tail\",nq}|verbatim={@\"C:\\tail\" + '\"',nq}" +
        "|quotedText={logText}";

    public void CheckTokenLogOutput(string callerTrace)
    {
        var breakpoint = (LineBreakpoint)m_controlInfo.Breakpoints["tokens"];
        string source = m_controlInfo.SourceFilesPath!.Split(';')[0] + ":" + breakpoint.NumLine;
        for (int count = 1; count <= 2; count++)
        {
            string output = "";
            Assert.True(m_dapDebugger.IsNotStopEventReceived(json =>
            {
                var message = Newtonsoft.Json.Linq.JObject.Parse(json);
                if ((string?)message["event"] != "output" || (string?)message["body"]?["category"] != "console")
                    return false;
                output = (string?)message["body"]?["output"] ?? "";
                return output.StartsWith("literal=");
            }), callerTrace);
            string[] parts = output.TrimEnd('\n').Split('|');
            Assert.Equal(19, parts.Length, callerTrace + ": " + output);
            Assert.Equal("literal=$PID", parts[0], callerTrace);
            Assert.Equal("file=" + JsonConvert.SerializeObject(source), parts[1], callerTrace);
            Assert.Equal("plain=" + source, parts[2], callerTrace);
            string address = parts[3].Substring("address=".Length);
            Assert.True(address.StartsWith("\"0x") && address.EndsWith("\"") && address.Length == 20, callerTrace);
            Assert.True(ulong.Parse(address.Substring(3, 16), System.Globalization.NumberStyles.HexNumber) != 0, callerTrace);
            Assert.Equal("same=true", parts[4], callerTrace);
            Assert.Equal("tid=" + m_threadId, parts[5], callerTrace);
            Assert.Equal("pid=" + TokenProcessId, parts[6], callerTrace);
            Assert.Equal("count=" + count, parts[7], callerTrace);
            Assert.Equal("joined=" + count + count, parts[8], callerTrace);
            Assert.Equal("math=" + (count + 10), parts[9], callerTrace);
            Assert.Equal("lower=" + TokenProcessId, parts[10], callerTrace);
            string quotedFileTail = JsonConvert.SerializeObject(source + "\\tail\"");
            Assert.Equal("fileTail=" + quotedFileTail.Substring(1, quotedFileTail.Length - 2), parts[11], callerTrace);
            Assert.Equal("quotedFileTail=" + quotedFileTail, parts[12], callerTrace);
            string quotedAddressTail = JsonConvert.SerializeObject(address.Substring(1, address.Length - 2) + "\\tail\"");
            Assert.Equal("addressTail=" + quotedAddressTail.Substring(1, quotedAddressTail.Length - 2), parts[13], callerTrace);
            Assert.Equal("countTail=" + count + "\\\\tail\\\"", parts[14], callerTrace);
            string quotedText = JsonConvert.SerializeObject("text \"quoted\"\\tail\nnext\tend");
            Assert.Equal("text=" + quotedText.Substring(1, quotedText.Length - 2), parts[15], callerTrace);
            string quotedTextTail = JsonConvert.SerializeObject("text \"quoted\"\\tail\nnext\tend\\tail");
            Assert.Equal("textTail=" + quotedTextTail.Substring(1, quotedTextTail.Length - 2), parts[16], callerTrace);
            Assert.Equal("verbatim=C:\\\\tail\\\"", parts[17], callerTrace);
            Assert.Equal("quotedText=" + quotedText, parts[18], callerTrace);
            Assert.True(output.EndsWith("\n") && !output.EndsWith("\n\n"), callerTrace);
            WasOutputEvent(callerTrace, "console", "literal=$FILEPOS\\tail\"quoted\"\n");
        }
    }
}
}
