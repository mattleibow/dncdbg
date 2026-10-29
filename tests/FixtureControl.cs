using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DncDbg.Testing;

public sealed record FixtureEndpoint(int Port, string Token, string SessionId, int ProcessId, bool BrokerRequired);
public sealed record FixtureState(int Count, string Counter, string Result);
public sealed record FixtureRequest(string Token, string SessionId, string Command);
public sealed record FixtureResponse(bool Success, string? Error, FixtureState? State);

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(FixtureEndpoint))]
[JsonSerializable(typeof(FixtureRequest))]
[JsonSerializable(typeof(FixtureResponse))]
public partial class FixtureJson : JsonSerializerContext;

public static class FixtureFrames
{
    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > 4096) throw new InvalidDataException("Fixture control frame exceeds its bound.");
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        return payload;
    }

    public static async Task WriteAsync(Stream stream, byte[] payload, CancellationToken token)
    {
        if (payload.Length is < 1 or > 4096) throw new InvalidDataException("Fixture control frame exceeds its bound.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(payload, token).ConfigureAwait(false);
    }
}

/// <summary>Test-app scenario control, never a debugger execution controller or UI-automation transport.</summary>
public sealed class FixtureControlServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<TcpClient, byte> peers = new();
    private readonly ConcurrentDictionary<Task, byte> workers = new();
    private readonly Func<string, CancellationToken, Task<FixtureState>> execute;
    private readonly string descriptor;
    private readonly object gate = new();
    private readonly Task accepting;
    private Task? closing;
    public FixtureEndpoint Endpoint { get; }

    public FixtureControlServer(string descriptor, string session, Func<string, CancellationToken, Task<FixtureState>> execute,
        bool brokerRequired = false)
    {
        if (session.Length != 32 || !session.All(Uri.IsHexDigit)) throw new ArgumentException("Fixture control requires an owned generation.");
        this.descriptor = descriptor;
        this.execute = execute;
        listener.Start(8);
        Endpoint = new(((IPEndPoint)listener.LocalEndpoint).Port, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            session, Environment.ProcessId, brokerRequired);
        try
        {
            string pending = descriptor + ".pending";
            File.WriteAllText(pending, JsonSerializer.Serialize(Endpoint, FixtureJson.Default.FixtureEndpoint));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(pending, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(pending, descriptor, true);
        }
        catch
        {
            listener.Stop();
            lifetime.Dispose();
            throw;
        }
        accepting = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                TcpClient peer = await listener.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false);
                peers.TryAdd(peer, 0);
                Task worker = HandleAsync(peer);
                workers.TryAdd(worker, 0);
                _ = worker.ContinueWith(done => workers.TryRemove(done, out _),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception error) when (lifetime.IsCancellationRequested &&
            error is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task HandleAsync(TcpClient peer)
    {
        using (peer)
        {
            try
            {
                NetworkStream stream = peer.GetStream();
                FixtureRequest request = JsonSerializer.Deserialize(await FixtureFrames.ReadAsync(stream, lifetime.Token).ConfigureAwait(false),
                    FixtureJson.Default.FixtureRequest) ?? throw new InvalidDataException("Missing fixture request.");
                bool authenticated = request.SessionId == Endpoint.SessionId && request.Token?.Length == 64 &&
                    CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(request.Token), Encoding.ASCII.GetBytes(Endpoint.Token));
                FixtureResponse response;
                if (!authenticated) response = new(false, "Fixture ownership/authentication mismatch.", null);
                else if (request.Command is not ("reset" or "run" or "snapshot"))
                    response = new(false, "Unknown fixture operation.", null);
                else
                {
                    FixtureState state = await execute(request.Command, lifetime.Token).WaitAsync(lifetime.Token).ConfigureAwait(false);
                    response = new(true, null, state);
                }
                await FixtureFrames.WriteAsync(stream, JsonSerializer.SerializeToUtf8Bytes(response, FixtureJson.Default.FixtureResponse), lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException or JsonException) { }
            finally { peers.TryRemove(peer, out _); }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate) return new(closing ??= CloseAsync());
    }

    private async Task CloseAsync()
    {
        lifetime.Cancel();
        listener.Stop();
        foreach (TcpClient peer in peers.Keys) peer.Dispose();
        await accepting.ConfigureAwait(false);
        await Task.WhenAll(workers.Keys).ConfigureAwait(false);
        if (File.Exists(descriptor))
        {
            FixtureEndpoint? observed = JsonSerializer.Deserialize(File.ReadAllText(descriptor), FixtureJson.Default.FixtureEndpoint);
            if (observed?.SessionId == Endpoint.SessionId && observed.ProcessId == Endpoint.ProcessId) File.Delete(descriptor);
        }
        lifetime.Dispose();
    }
}

public sealed class FixtureControlClient(FixtureEndpoint endpoint)
{
    public async Task<FixtureState> CommandAsync(string command, CancellationToken token)
    {
        using var peer = new TcpClient();
        await peer.ConnectAsync(IPAddress.Loopback, endpoint.Port, token);
        NetworkStream stream = peer.GetStream();
        await FixtureFrames.WriteAsync(stream, JsonSerializer.SerializeToUtf8Bytes(
            new FixtureRequest(endpoint.Token, endpoint.SessionId, command), FixtureJson.Default.FixtureRequest), token);
        FixtureResponse response = JsonSerializer.Deserialize(await FixtureFrames.ReadAsync(stream, token),
            FixtureJson.Default.FixtureResponse) ?? throw new InvalidDataException("Missing fixture response.");
        if (!response.Success || response.State == null) throw new InvalidDataException(response.Error ?? "Fixture command failed.");
        return response.State;
    }
}
