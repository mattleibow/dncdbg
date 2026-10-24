using System.Diagnostics;
using DncDbg.Protocol;

namespace DncDbg.Dap.Native;

/// <summary>A native debugger process reached exclusively through standard DAP; no execution engine is reimplemented.</summary>
internal sealed class NativeAdapterSession : IDisposable
{
    private readonly Process _process;
    private readonly DapConnection _connection;
    private readonly Action<string> _log;
    private readonly Task _reader;
    private readonly Task _stderr;
    private volatile bool _disposing;
    private bool _disconnected;
    private bool _transportStopped;
    private readonly Action<Exception> _failed;

    public int ProcessId => _process.Id;
    public bool IsAlive => !_process.HasExited;

    public NativeAdapterSession(NativeAdapterDescriptor descriptor, Action<DapMessage> receive,
        Action<Exception> failed, Action<string> log)
    {
        _log = log;
        _failed = failed;
        var start = new ProcessStartInfo(descriptor.Path)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in descriptor.Arguments)
            start.ArgumentList.Add(argument);
        _process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start the native debug adapter.");
        _connection = new DapConnection(_process.StandardOutput.BaseStream, _process.StandardInput.BaseStream);
        _stderr = Task.Run(async () =>
        {
            while (await _process.StandardError.ReadLineAsync() is { } line)
                _log("Native debugger stderr: " + line);
        });
        _reader = Task.Run(() =>
        {
            try
            {
                while (_connection.Read() is { } message)
                {
                    if (message.Type == "response")
                        _connection.CompleteRequest(message);
                    else
                        receive(message);
                }
                if (!_disposing && !_disconnected)
                    failed(new IOException("The native debug adapter closed its DAP connection."
                        + (_process.HasExited ? $" Exit code: 0x{unchecked((uint)_process.ExitCode):X8}." : "")));
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
            {
                if (!_disposing)
                    failed(error);
            }
            finally { _connection.CancelPendingRequests(); }
        });
    }

    public async Task<DapMessage> SendAsync(string command, object? arguments, CancellationToken cancellation = default)
    {
        try { return await _connection.SendRequestAsync(command, arguments, cancellation).ConfigureAwait(false); }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
        {
            if (_connection.WriteFailed && !_disposing)
                _failed(new IOException("The native DAP write did not complete; the transport is unusable.", error));
            throw;
        }
    }

    public DapMessage Request(string command, object? arguments, CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            DapMessage response = SendAsync(command, arguments, timeout.Token).GetAwaiter().GetResult();
            _disconnected |= command == "disconnect" && response.Success == true;
            return response;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException($"Native debug adapter timed out handling '{command}'.");
        }
    }

    public void SendMessage(DapMessage message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { _connection.SendMessageAsync(message, timeout.Token).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException or TimeoutException)
        {
            if (_connection.WriteFailed && !_disposing)
                _failed(new IOException("The native DAP response write did not complete; the transport is unusable.", error));
            throw;
        }
    }

    public void StopTransport(bool terminateTarget)
    {
        if (_transportStopped)
            return;
        _disposing = true;
        try
        {
            if (!_process.HasExited && !_disconnected)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    DapMessage response = Request("disconnect", new { terminateDebuggee = terminateTarget }, timeout.Token);
                    if (response.Success != true)
                        _log("Native disconnect failed: " + response.Message);
                }
                catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException)
                { _log("Native disconnect failed: " + error.Message); }
            }
            if (!_process.HasExited && !_process.WaitForExit(5000))
            {
                _log(terminateTarget ? "Native adapter did not exit; terminating its owned process tree."
                    : "Native adapter did not exit after detach; terminating only the adapter, not its relinquished target.");
                _process.Kill(entireProcessTree: terminateTarget);
                _process.WaitForExit();
            }
            _reader.Wait(TimeSpan.FromSeconds(5));
            _stderr.Wait(TimeSpan.FromSeconds(5));
        }
        finally { _connection.Dispose(); _transportStopped = true; }
    }

    public void Dispose(bool terminateTarget)
    {
        StopTransport(terminateTarget);
        _process.Dispose();
    }

    public void Dispose() => Dispose(terminateTarget: true);
}
