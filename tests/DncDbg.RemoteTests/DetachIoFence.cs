using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

internal sealed class DetachIoFence : IDisposable
{
    private readonly string result;
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly Task<TcpClient> accepting;
    private TcpClient? peer;
    public string[] Arguments { get; }
    public bool Released { get; private set; }

    public DetachIoFence(string result)
    {
        this.result = result;
        listener.Start(1);
        Arguments = ["--detach-io", ((IPEndPoint)listener.LocalEndpoint).Port.ToString()];
        accepting = listener.AcceptTcpClientAsync();
    }

    public async Task ReadyAsync(int processId, CancellationToken token)
    {
        peer = await accepting.WaitAsync(TimeSpan.FromSeconds(10), token);
        using var reader = new StreamReader(peer.GetStream(), Encoding.UTF8, leaveOpen: true);
        string line = await reader.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(10), token)
            ?? throw new IOException("Missing pre-detach file ownership record.");
        using JsonDocument identity = JsonDocument.Parse(line);
        if (identity.RootElement.GetProperty("ProcessId").GetInt32() != processId ||
            identity.RootElement.GetProperty("RetainedLength").GetInt64() != "before\n"u8.Length)
            throw new InvalidDataException("Pre-detach file/control ownership mismatch.");
        File.WriteAllText(result + ".ownership.json", identity.RootElement.GetRawText());
    }

    public async Task ReleaseAsync(Process process)
    {
        if (peer == null) throw new InvalidOperationException("The target did not establish its pre-detach ownership fence.");
        if (process.HasExited || File.Exists(result) || File.Exists(result + ".pending"))
            throw new InvalidOperationException("The target performed post-detach I/O before debugger ownership teardown.");
        await peer.GetStream().WriteAsync("released\n"u8.ToArray());
        Released = true;
    }

    public void Dispose()
    {
        listener.Stop();
        if (peer != null) peer.Dispose();
        else _ = accepting.ContinueWith(task =>
        {
            if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose();
            else _ = task.Exception;
        }, TaskScheduler.Default);
    }
}
