using System.Buffers.Binary;
using System.Text.Json;

namespace Hydra.Management;

internal static class ManagementFraming
{
    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancel)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, ManagementJson.Options);
        if (payload.Length > ManagementProtocol.MaxMessageBytes)
            throw new InvalidOperationException("Management message is too large.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, cancel);
        await stream.WriteAsync(payload, cancel);
        await stream.FlushAsync(cancel);
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancel)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancel);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 or > ManagementProtocol.MaxMessageBytes)
            throw new InvalidDataException("Invalid management message length.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancel);
        return JsonSerializer.Deserialize<T>(payload, ManagementJson.Options)
            ?? throw new InvalidDataException("Invalid management message.");
    }
}
