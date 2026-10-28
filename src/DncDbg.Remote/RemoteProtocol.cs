using System.Buffers.Binary;
using System.Text.Json;

namespace DncDbg.Remote;

public sealed record RemoteEnvelope(int Version, long Sequence, RemoteOperation Operation, JsonElement Body, string? Error = null);

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
