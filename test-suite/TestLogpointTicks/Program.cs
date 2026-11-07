using System;
using System.Diagnostics;
using DbgTest;
using DbgTest.DAP;
using DbgTest.Script;

namespace TestLogpointTicks
{
class Program
{
    static void Main(string[] args)
    {
        Label.Checkpoint("init", "inspect", (Object context) =>
        {
            Context Context = (Context)context;
            Context.Initialize(@"__FILE__:__LINE__");
            Context.AddBreakpoint(@"__FILE__:__LINE__", "tick",
                logMessage: "literal=$TICK|value={$TICK}|repeat={$TICK}|difference={$TICK - $TICK}|plus={$TICK + 1}");
            Context.AddBreakpoint(@"__FILE__:__LINE__", "after");
            Context.SetBreakpoints(@"__FILE__:__LINE__");
            Context.ConfigurationDone(@"__FILE__:__LINE__");
            Context.Launch(@"__FILE__:__LINE__");
            Context.WasEntryPointHit(@"__FILE__:__LINE__");
            Context.CheckTickHits(@"__FILE__:__LINE__");
        });

        for (int i = 0; i < 2; i++)
        {
            GC.KeepAlive(args);                                       Label.Breakpoint("tick");
            GC.KeepAlive(args);                                       Label.Breakpoint("after");
        }

        Label.Checkpoint("inspect", "finish", (Object context) =>
        {
            Context Context = (Context)context;
            Context.Continue(@"__FILE__:__LINE__");
            Context.WasExit(@"__FILE__:__LINE__");
            Context.Restart(@"__FILE__:__LINE__");
            Context.WasEntryPointHit(@"__FILE__:__LINE__");
            Context.CheckTickHits(@"__FILE__:__LINE__");
            Context.Continue(@"__FILE__:__LINE__");
        });
        Label.Checkpoint("finish", "", (Object context) =>
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
    public long? PreviousTick;
    public long PreviousTickBefore;
    public long PreviousTickAfter;

    public void CheckTickHits(string callerTrace)
    {
        for (int hit = 0; hit < 2; hit++)
        {
            long before = Stopwatch.GetTimestamp();
            Continue(callerTrace);
            WasBreakpointHit(callerTrace, "after");
            long after = Stopwatch.GetTimestamp();
            string output = "";
            Assert.True(m_dapDebugger.IsNotStopEventReceived(json =>
            {
                var message = Newtonsoft.Json.Linq.JObject.Parse(json);
                if ((string?)message["event"] != "output" || (string?)message["body"]?["category"] != "console")
                    return false;
                output = (string?)message["body"]?["output"] ?? "";
                return output.StartsWith("literal=", StringComparison.Ordinal);
            }), callerTrace);

            string[] parts = output.TrimEnd('\n').Split('|');
            Assert.Equal(5, parts.Length, callerTrace + ": " + output);
            Assert.Equal("literal=$TICK", parts[0], callerTrace);
            Assert.True(parts[1].StartsWith("value=", StringComparison.Ordinal), callerTrace);
            Assert.True(long.TryParse(parts[1].Substring("value=".Length), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out long tick), callerTrace + ": " + output);
            Assert.True(tick >= 0, callerTrace);
            Assert.Equal("repeat=" + tick.ToString(System.Globalization.CultureInfo.InvariantCulture), parts[2], callerTrace);
            Assert.Equal("difference=0", parts[3], callerTrace);
            Assert.Equal("plus=" + (tick + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), parts[4], callerTrace);
            Assert.True(output.EndsWith("\n") && !output.EndsWith("\n\n"), callerTrace);

            if (PreviousTick.HasValue)
            {
                long delta = tick - PreviousTick.Value;
                Assert.True(delta >= 0, callerTrace + ": ticks must not decrease, including across restart");
                // Each hit is inside its continue/stop window; allow whole-millisecond quantization.
                double minimum = Stopwatch.GetElapsedTime(PreviousTickAfter, before).TotalMilliseconds;
                double maximum = Stopwatch.GetElapsedTime(PreviousTickBefore, after).TotalMilliseconds;
                Assert.True(delta >= Math.Max(0, Math.Floor(minimum) - 1)
                    && delta <= Math.Ceiling(maximum) + 1,
                    callerTrace + ": millisecond delta " + delta + " outside elapsed bounds " + minimum + ".." + maximum);
            }
            PreviousTick = tick;
            PreviousTickBefore = before;
            PreviousTickAfter = after;
        }
    }
}
}
