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
    private Exception? _transportFailure;
    private readonly Dictionary<int, OwnedLocalTarget> _ownedTargets = [];
    private readonly CancellationTokenSource _readerCancellation = new();
    private readonly DateTime? _adapterStarted;

    public int ProcessId => _process.Id;
    public bool IsAlive => !_process.HasExited;
    public bool TransportFailed => Volatile.Read(ref _transportFailure) != null || _connection.WriteFailed || _reader.IsCompleted;

    public void TrackLaunchedLocalTarget(int pid)
    {
        lock (_ownedTargets)
        {
            if (_adapterStarted is { } started && !_ownedTargets.ContainsKey(pid)
                && OwnedLocalTarget.Capture(pid, _process.Id, started, _log) is { } target)
                _ownedTargets.Add(pid, target);
        }
    }

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
        try { _adapterStarted = _process.StartTime.ToUniversalTime(); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        { _log("Cannot verify native adapter creation time; independent target adoption is disabled: " + error.Message); }
        _connection = new DapConnection(new CancellablePipeInput(_process.StandardOutput.BaseStream, _readerCancellation.Token),
            _process.StandardInput.BaseStream);
        _stderr = Task.Run(() =>
        {
            try
            {
                using var reader = new StreamReader(new CancellablePipeInput(_process.StandardError.BaseStream, _readerCancellation.Token));
                while (reader.ReadLine() is { } line)
                    _log("Native debugger stderr: " + line);
            }
            catch (OperationCanceledException) when (_readerCancellation.IsCancellationRequested) { }
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
                    Fail(new IOException("The native debug adapter closed its DAP connection."
                        + (_process.HasExited ? $" Exit code: 0x{unchecked((uint)_process.ExitCode):X8}." : "")));
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
            {
                if (!_disposing)
                    Fail(error);
            }
            catch (OperationCanceledException) when (_readerCancellation.IsCancellationRequested) { }
            finally { _connection.CancelPendingRequests(); }
        });
    }

    public async Task<DapMessage> SendAsync(string command, object? arguments, CancellationToken cancellation = default)
    {
        try { return await _connection.SendRequestAsync(command, arguments, cancellation).ConfigureAwait(false); }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
        {
            if (_connection.WriteFailed && !_disposing)
                Fail(new IOException("The native DAP write did not complete; the transport is unusable.", error));
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
        catch (OperationCanceledException error) when (TransportFailed)
        {
            throw new IOException($"Native debug adapter transport ended handling '{command}'.", _transportFailure ?? error);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"Native debug adapter timed out handling '{command}'.");
        }
        catch (OperationCanceledException error) when (!cancellation.IsCancellationRequested)
        {
            throw new IOException($"Native debug adapter connection ended handling '{command}'.", error);
        }
    }

    public void SendMessage(DapMessage message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { _connection.SendMessageAsync(message, timeout.Token).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException or TimeoutException)
        {
            if (_connection.WriteFailed && !_disposing)
                Fail(new IOException("The native DAP response write did not complete; the transport is unusable.", error));
            throw;
        }
    }

    private void Fail(Exception error)
    {
        if (Interlocked.CompareExchange(ref _transportFailure, error, null) == null)
            _failed(error);
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
                if (!_process.WaitForExit(5000))
                    _log("Native adapter did not exit within its forced-termination budget.");
            }
            if (!_reader.Wait(TimeSpan.FromSeconds(5)))
            {
                _log("Native adapter reader remained open after process exit; cancelling inherited pipe input.");
                _readerCancellation.Cancel();
                if (!_reader.Wait(TimeSpan.FromSeconds(1)))
                    _log("Native adapter reader did not stop within its cancellation budget.");
            }
            _readerCancellation.Cancel();
            _stderr.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { _log("Native adapter transport shutdown failed: " + error.Message); }
        finally
        {
            _readerCancellation.Cancel();
            try { _connection.Dispose(); }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            { _log("Native adapter pipe disposal failed: " + error.Message); }
            lock (_ownedTargets)
            {
                foreach (OwnedLocalTarget target in _ownedTargets.Values)
                {
                    if (terminateTarget)
                        target.Terminate(_log);
                    target.Dispose();
                }
                _ownedTargets.Clear();
            }
            _transportStopped = true;
        }
    }

    public void Dispose(bool terminateTarget)
    {
        StopTransport(terminateTarget);
        _process.Dispose();
        _readerCancellation.Dispose();
    }

    public void Dispose() => Dispose(terminateTarget: true);
}
