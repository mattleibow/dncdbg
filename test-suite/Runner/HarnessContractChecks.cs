using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DbgTest.DAP;
using DbgTestCore;
using Newtonsoft.Json;

namespace Runner
{
static class HarnessContractChecks
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
            checks++;
        }

        string Event(int seq, string name, string? category = null, string? output = null) =>
            JsonConvert.SerializeObject(new { seq, type = "event", @event = name, body = new { category, output } });
        Func<string, bool> Is(int seq) => text => (long?)JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(text)?["seq"] == seq;
        Func<string, bool> Output(string category, string output) => text =>
        {
            var message = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(text)!;
            return (string?)message["event"] == "output" && (string?)message["body"]?["category"] == category &&
                   (string?)message["body"]?["output"] == output;
        };

        var client = new ScheduledClient(
            (0, Event(1, "initialized")), (0, Event(2, "process")), (0, Event(3, "output", "stdout", "unrelated")),
            (150, Event(4, "output", "stdout", "first\n")), (160, Event(5, "output", "stderr", "second\n")),
            (170, Event(6, "output", "stdout", "third\n")));
        var dap = new DAPDebugger(client);
        Check(dap.IsNotStopEventReceived(Output("stdout", "first\n"), getNewEvents: true),
              "Positive output must wait for delayed exact output, not treat a short empty poll as failure.");
        var observed = new List<int>();
        Check(dap.IsEventReceived(text =>
        {
            observed.Add((int)JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(text)!["seq"]!);
            return Is(3)(text);
        }), "Unmatched earlier output must remain available.");
        Check(observed.SequenceEqual(new[] { 1, 2, 3 }), "Unmatched events must retain their original queue order.");
        Check(dap.IsNotStopEventReceived(Output("stderr", "second\n"), getNewEvents: true), "Sequential delayed stderr must be retained.");
        Check(dap.IsNotStopEventReceived(Output("stdout", "third\n"), getNewEvents: true), "Sequential delayed stdout must be retained.");
        Check(!dap.IsNotStopEventReceived(Is(4)), "A matching event must not be consumed twice.");
        Check(dap.IsNotStopEventReceived(Is(1)) && dap.IsNotStopEventReceived(Is(2)),
              "Unmatched non-stop entries must remain available after main-queue scanning.");

        var empty = new ScheduledClient();
        Check(!new DAPDebugger(empty).IsNotStopEventReceived(_ => true, getNewEvents: true), "Empty negative poll.");
        Check(empty.Timeouts.SequenceEqual(new[] { DebuggerClient.PollTimeout }), "Negative checks must remain short polls.");
        return checks;
    }

    sealed class ScheduledClient : DebuggerClient
    {
        readonly Queue<(int Due, string Message)> events;
        readonly Stopwatch clock = Stopwatch.StartNew();
        bool delayedRelease;
        public readonly List<int> Timeouts = new List<int>();

        public ScheduledClient(params (int Due, string Message)[] events)
        {
            this.events = new Queue<(int Due, string Message)>(events);
        }

        public override string[]? Receive(int timeout)
        {
            Timeouts.Add(timeout);
            if (events.Count == 0)
                return null;
            if (events.Peek().Due > 0 && !delayedRelease)
            {
                if (timeout == PollTimeout)
                    return null;
                delayedRelease = true;
                clock.Restart();
            }
            int delay = Math.Max(0, events.Peek().Due - (int)clock.ElapsedMilliseconds);
            if (delay > 0)
            {
                if (timeout == PollTimeout || delay > timeout)
                    return null;
                System.Threading.Thread.Sleep(delay);
            }
            return new[] { events.Dequeue().Message };
        }
    }
}
}
