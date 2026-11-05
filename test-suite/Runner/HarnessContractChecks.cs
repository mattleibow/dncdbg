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
            (0, Event(8, "output", "stderr", "first\n")), (0, Event(7, "stopped")),
            (150, Event(4, "output", "stdout", "first\n")), (160, Event(5, "output", "stderr", "second\n")),
            (170, Event(6, "output", "stdout", "third\n")));
        var dap = new DAPDebugger(client);
        Check(dap.WaitForNotStopEvent(Output("stdout", "first\n")),
              "Positive output must wait for delayed exact output, not treat a short empty poll as failure.");
        var observed = new List<int>();
        Check(dap.IsEventReceived(text =>
        {
            observed.Add((int)JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(text)!["seq"]!);
            return Is(3)(text);
        }), "Unmatched earlier output must remain available.");
        Check(observed.SequenceEqual(new[] { 1, 2, 3 }), "Unmatched events must retain their original queue order.");
        Check(dap.IsNotStopEventReceived(Is(8)), "Identical output in the wrong category must remain unmatched.");
        Check(!dap.IsNotStopEventReceived(Is(3)), "A matching main-queue event must not be replayed from the non-stop list.");
        Check(dap.WaitForNotStopEvent(Output("stderr", "second\n")), "Sequential delayed stderr must be retained.");
        Check(dap.WaitForNotStopEvent(Output("stdout", "third\n")), "Sequential delayed stdout must be retained.");
        Check(!dap.IsNotStopEventReceived(Is(4)), "A matching event must not be consumed twice.");
        Check(dap.IsNotStopEventReceived(Is(1)) && dap.IsNotStopEventReceived(Is(2)),
              "Unmatched non-stop entries must remain available after main-queue scanning.");
        Check(dap.IsEventReceived(Is(7)), "Positive output waits must preserve intervening stop events.");

        var empty = new ScheduledClient();
        Check(!new DAPDebugger(empty).IsNotStopEventReceived(_ => true, getNewEvents: true), "Empty negative poll.");
        Check(empty.Timeouts.SequenceEqual(new[] { DebuggerClient.PollTimeout }), "Negative checks must remain short polls.");

        var cachedClient = new ScheduledClient((0, Event(21, "initialized")), (0, Event(22, "output", "stdout", "cached")),
                                               (0, Event(23, "stopped")));
        var cached = new DAPDebugger(cachedClient);
        Check(cached.IsEventReceived(Is(23)), "Populate both event stores before checking cached output.");
        int receives = cachedClient.Timeouts.Count;
        Check(cached.WaitForNotStopEvent(Output("stdout", "cached")), "Positive wait must first scan cached exact output.");
        Check(cachedClient.Timeouts.Count == receives, "Cached matches must not perform an extra receive.");
        Check(cached.WaitForNotStopEvent(Is(21), timeout: 0), "Unmatched cached events must survive the positive wait.");

        string duplicate = Event(31, "output", "stdout", "duplicate");
        var duplicates = new DAPDebugger(new ScheduledClient((0, duplicate), (0, duplicate), (0, Event(32, "stopped"))));
        Check(!duplicates.IsNotStopEventReceived(_ => false, getNewEvents: true), "Populate repeated identical event frames.");
        Check(duplicates.WaitForNotStopEvent(Output("stdout", "duplicate"), timeout: 0), "Consume only the first identical frame.");
        Check(duplicates.IsEventReceived(Is(32)), "The other identical frame must not disrupt the following stop.");
        Check(duplicates.WaitForNotStopEvent(Output("stdout", "duplicate"), timeout: 0), "The second identical frame must remain.");
        Check(!duplicates.IsNotStopEventReceived(Output("stdout", "duplicate")), "Both identical frames must now be consumed.");

        bool eof = false;
        try { new DAPDebugger(new ScheduledClient()).WaitForNotStopEvent(_ => true); }
        catch (DebuggerNotResponses) { eof = true; }
        Check(eof, "EOF during a positive wait must fail, not report a match.");

        bool protocolFailure = false;
        try
        {
            new DAPDebugger(new ScheduledClient((0, "{\"seq\":1,\"type\":\"response\",\"success\":false}")))
                .WaitForNotStopEvent(_ => true);
        }
        catch (FailResponse) { protocolFailure = true; }
        Check(protocolFailure, "A failed asynchronous response must terminate the positive wait honestly.");

        var budgetClient = new ScheduledClient((0, Event(41, "initialized")), (30, Event(42, "process")),
                                               (60, Event(43, "output", "stdout", "unrelated")),
                                               (20000, Event(44, "output", "stdout", "too late")));
        bool timedOut = false;
        try { new DAPDebugger(budgetClient).WaitForNotStopEvent(Output("stdout", "too late")); }
        catch (DebuggerNotResponses) { timedOut = true; }
        Check(timedOut, "Unrelated events must not count as a positive match.");
        Check(budgetClient.Timeouts[0] <= 10000 && budgetClient.Timeouts.All(timeout => timeout > 0) &&
              budgetClient.Timeouts.Last() <= budgetClient.Timeouts.First() - 50,
              "Every receive must use the remainder of one existing 10-second deadline, never a fresh budget.");

        var lateClient = new ScheduledClient((0, Event(51, "output", "stdout", "late"))) { ReceiveDelay = 100 };
        var late = new DAPDebugger(lateClient);
        Check(!late.WaitForNotStopEvent(Output("stdout", "late"), timeout: 50),
              "A receive completing after the deadline must not become success.");
        Check(lateClient.Timeouts.Count == 1 && late.IsNotStopEventReceived(Is(51)),
              "Deadline exhaustion must stop receiving without losing the late unmatched event.");
        return checks;
    }

    sealed class ScheduledClient : DebuggerClient
    {
        readonly Queue<(int Due, string Message)> events;
        readonly Stopwatch clock = Stopwatch.StartNew();
        bool delayedRelease;
        public int ReceiveDelay;
        public readonly List<int> Timeouts = new List<int>();

        public ScheduledClient(params (int Due, string Message)[] events)
        {
            this.events = new Queue<(int Due, string Message)>(events);
        }

        public override string[]? Receive(int timeout)
        {
            Timeouts.Add(timeout);
            if (ReceiveDelay > 0)
                System.Threading.Thread.Sleep(ReceiveDelay);
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
