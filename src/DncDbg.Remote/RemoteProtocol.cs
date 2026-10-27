using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DncDbg.Remote;

public enum RemoteOperation
{
    Hello = 1,
    SetBreakpoints = 2,
    Start = 3,
    WaitForStop = 4,
    Stack = 5,
    Evaluate = 6,
    Continue = 7,
    Release = 8,
    Disconnect = 9,
}

public sealed record RemoteEnvelope(int Version, long Sequence, RemoteOperation Operation, JsonElement Body, string? Error = null);
public sealed record RemoteHello(string Token);
public sealed record RemoteCapabilities(string Runtime, string Platform, bool ManagedDebugging, string? Blocker);
public sealed record RemoteEndpoint(int Port, string Token);
public sealed record RemoteBreakpoints(string Source, int[] Lines);
public sealed record RemoteBreakpoint(int Line, bool Verified);
public sealed record RemoteStop(long Id, string Reason, int ThreadId);
public sealed record RemoteFrame(long Handle, string Name, string? Source, int Line);
public sealed record RemoteEvaluation(long Frame, string Expression);
public sealed record RemoteValue(string Value, string Type);
public sealed record RemoteHandles(long[] Handles);
public sealed record RemoteId(long Id);
public sealed record RemoteEmpty;

[JsonSerializable(typeof(RemoteEnvelope))]
[JsonSerializable(typeof(RemoteHello))]
[JsonSerializable(typeof(RemoteCapabilities))]
[JsonSerializable(typeof(RemoteEndpoint))]
[JsonSerializable(typeof(RemoteBreakpoints))]
[JsonSerializable(typeof(RemoteBreakpoint[]))]
[JsonSerializable(typeof(RemoteStop))]
[JsonSerializable(typeof(RemoteFrame[]))]
[JsonSerializable(typeof(RemoteEvaluation))]
[JsonSerializable(typeof(RemoteValue))]
[JsonSerializable(typeof(RemoteHandles))]
[JsonSerializable(typeof(RemoteId))]
[JsonSerializable(typeof(RemoteEmpty))]
public partial class RemoteJson : JsonSerializerContext;

public static class RemoteProtocol
{
    public const int Version = 1;
    public const int MaximumPayload = 256 * 1024;
    private const uint Magic = 0x31434e44; // DNC1, little endian

    public static JsonElement Pack<T>(T value) => JsonSerializer.SerializeToElement(value, TypeInfo<T>());
    public static T Unpack<T>(JsonElement body) => body.Deserialize(TypeInfo<T>())
        ?? throw new InvalidDataException("A remote message body cannot be null.");

    private static System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> TypeInfo<T>() =>
        (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)(RemoteJson.Default.GetTypeInfo(typeof(T))
            ?? throw new NotSupportedException(typeof(T).Name));

    public static async ValueTask WriteAsync(Stream stream, RemoteEnvelope message, CancellationToken cancellationToken)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message, RemoteJson.Default.RemoteEnvelope);
        if (body.Length > MaximumPayload)
            throw new InvalidDataException("Remote payload exceeds the protocol limit.");
        byte[] header = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), body.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }

    public static async ValueTask<RemoteEnvelope> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[8];
        await stream.ReadExactlyAsync(header, cancellationToken);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
            throw new InvalidDataException("Invalid remote protocol magic.");
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length is <= 0 or > MaximumPayload)
            throw new InvalidDataException("Invalid remote payload length.");
        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken);
        return JsonSerializer.Deserialize(body, RemoteJson.Default.RemoteEnvelope)
            ?? throw new InvalidDataException("Missing remote envelope.");
    }
}
