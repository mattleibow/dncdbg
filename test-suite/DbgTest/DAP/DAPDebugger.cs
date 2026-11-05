using System;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using DbgTestCore;
using DbgTestCore.DAP;

using Newtonsoft.Json;

namespace DbgTest.DAP
{
public class DAPResult : Tuple<bool, string>
{
    public DAPResult(bool Success, string ResponseStr) : base(Success, ResponseStr)
    {
    }

    public bool Success
    {
        get {
            return this.Item1;
        }
    }
    public string ResponseStr
    {
        get {
            return this.Item2;
        }
    }
}

public class DAPDebugger
{
    // Exception info responses may contain deeply nested self-referencing innerException chains
    // (up to the debugger's DNCDBG_INNER_EXCEPTION_WALK_LIMIT). Each nested innerException adds
    // two JSON depth levels, which exceeds Newtonsoft's default MaxDepth of 64, so allow deeper
    // nesting when parsing responses.
    const int m_maxJsonDepth = 512;

    static JsonTextReader CreateJsonReader(string stringJSON)
    {
        return new JsonTextReader(new StringReader(stringJSON)) { MaxDepth = m_maxJsonDepth };
    }

    public bool IsResponseContainProperty(string stringJSON, string testField, string testValue)
    {
        JsonTextReader reader = CreateJsonReader(stringJSON);
        while (reader.Read())
        {
            if (reader.Value != null && reader.TokenType.ToString() == "PropertyName" &&
                reader.Value.ToString() == testField && reader.Read() && reader.Value != null &&
                reader.Value.ToString() == testValue)
            {
                return true;
            }
        }

        return false;
    }

    public object? GetResponsePropertyValue(string stringJSON, string testField)
    {
        JsonTextReader reader = CreateJsonReader(stringJSON);
        while (reader.Read())
        {
            if (reader.Value != null && reader.TokenType.ToString() == "PropertyName" &&
                reader.Value.ToString() == testField && reader.Read())
            {
                return reader.Value;
            }
        }

        return null;
    }

    public DAPResult Request(Request command, bool asyncExecution = false, int timeout = -1)
    {
        command.seq = m_requestSeq++;
        string stringJSON = JsonConvert.SerializeObject(
            command, Formatting.None, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

        Logger.LogLine("-> (C) " + stringJSON);

        if (!m_client.Send(stringJSON))
        {
            throw new DebuggerNotResponses();
        }

        if (asyncExecution)
        {
            return new DAPResult(true, "success");
        }

        while (true)
        {
            string[] response = m_client.Receive(timeout) ?? throw new DebuggerNotResponses();
            string line = response[0];

            if (IsResponseContainProperty(line, "type", "response"))
            {
                Logger.LogLine("<- (R) " + line);
                if ((Int64)GetResponsePropertyValue(line, "request_seq")! == command.seq)
                    return new DAPResult((bool)GetResponsePropertyValue(line, "success")!, line);
                else
                    throw new WrongResponseSequence();
            }
            else
            {
                RecordEvent(line);
            }
        }
    }

    void ReceiveEvents(int timeout = -1)
    {
        while (ReceiveEvent(timeout, out bool stopEvent))
        {
            if (stopEvent)
                return;
        }
    }

    bool ReceiveEvent(int timeout, out bool stopEvent)
    {
        stopEvent = false;
        string[]? response = m_client.Receive(timeout);
        if (response == null)
        {
            if (timeout == DebuggerClient.PollTimeout)
                return false;
            throw new DebuggerNotResponses();
        }
        string line = response[0];

        // Response for async requests
        if (IsResponseContainProperty(line, "type", "response"))
        {
            Logger.LogLine("<- (R) " + line);
            if (!(bool)GetResponsePropertyValue(line, "success")!)
                throw new FailResponse();
        }
        else
        {
            stopEvent = RecordEvent(line);
        }
        return true;
    }

    bool RecordEvent(string line)
    {
        Logger.LogLine("<- (E) " + line);
        var received = new ReceivedEvent(line);
        m_eventQueue.Enqueue(received);

        foreach (var entry in m_stopEvents)
        {
            if (IsResponseContainProperty(line, "event", entry))
                return true;
        }
        m_notStopEventList.Add(received);
        return false;
    }

    bool TakeNotStopEvent(Func<string, bool> filter)
    {
        for (int i = 0; i < m_notStopEventList.Count; i++)
        {
            var received = m_notStopEventList[i];
            if (!filter(received.Message))
                continue;
            m_notStopEventList.RemoveAt(i);
            int queued = m_eventQueue.Count;
            for (int j = 0; j < queued; j++)
            {
                var entry = m_eventQueue.Dequeue();
                if (!ReferenceEquals(entry, received))
                    m_eventQueue.Enqueue(entry);
            }
            return true;
        }
        return false;
    }

    public bool WaitForNotStopEvent(Func<string, bool> filter, int timeout = 10000)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timeout);
        var elapsed = Stopwatch.StartNew();
        if (TakeNotStopEvent(filter))
            return true;
        while (elapsed.ElapsedMilliseconds < timeout)
        {
            int remaining = timeout - (int)elapsed.ElapsedMilliseconds;
            if (remaining <= 0)
                break;
            ReceiveEvent(remaining, out _);
            if (elapsed.ElapsedMilliseconds >= timeout)
                break;
            if (TakeNotStopEvent(filter))
                return true;
        }
        return false;
    }

    bool TakeEvent(Func<string, bool> filter)
    {
        while (m_eventQueue.Count > 0)
        {
            var received = m_eventQueue.Dequeue();
            if (filter(received.Message))
            {
                m_notStopEventList.Remove(received);
                return true;
            }
        }
        return false;
    }

    public bool IsEventReceived(Func<string, bool> filter)
    {
        // Check previously received events first.
        if (TakeEvent(filter))
            return true;

        // Receive new events and check them.
        // Note, stop events check must wait: the corresponding stop event may
        // come much later than the last response. For example, after restart
        // the newly started debuggee exits only after it finishes its work.
        ReceiveEvents(10000);
        return TakeEvent(filter);
    }

    public bool IsNotStopEventReceived(Func<string, bool> filter, bool getNewEvents = false)
    {
        // Receive new events and check them.
        if (getNewEvents)
        {
            ReceiveEvents(DebuggerClient.PollTimeout);
        }
        return TakeNotStopEvent(filter);
    }

    public DAPDebugger(DebuggerClient debuggerClient)
    {
        m_client = debuggerClient;
    }

    sealed class ReceivedEvent
    {
        public readonly string Message;
        public ReceivedEvent(string message) { Message = message; }
    }

    Queue<ReceivedEvent> m_eventQueue = new Queue<ReceivedEvent>();
    List<ReceivedEvent> m_notStopEventList = new List<ReceivedEvent>();
    readonly DebuggerClient m_client;
    static readonly string[] m_stopEvents = { "stopped", "terminated" };
    int m_requestSeq = 1;
}
}
