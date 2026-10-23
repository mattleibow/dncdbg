using System.Text;

namespace DncDbg.Engine;

public sealed partial class DebugEngine
{
    private readonly Lock _inputGate = new();
    private Task? _pendingInput;

    public bool WriteInputIfRunning(string text)
    {
        Stream input;
        lock (_lock)
        {
            if (_stopped || _processExited || _launched == null)
                return false;
            input = _launched.StdIn;
        }
        if (Encoding.UTF8.GetByteCount(text) > 1024 * 1024)
            throw new DebuggerException("Console input is limited to 1048576 bytes per request.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        byte[] bytes = Encoding.UTF8.GetBytes(text + Environment.NewLine);
        Task write;
        lock (_inputGate)
        {
            if (_pendingInput is { IsCompleted: false })
                throw new DebuggerException("Console input is still being written.");
            write = _pendingInput = Task.Run(async () =>
            {
                await input.WriteAsync(bytes, timeout.Token);
                await input.FlushAsync(timeout.Token);
            }, timeout.Token);
            _ = write.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
        try { write.WaitAsync(timeout.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (!_lifetime.Token.IsCancellationRequested)
        {
            throw new DebuggerException("Console input timed out: the target is not reading.");
        }
        return true;
    }
}
