using Cathedral.Extensions;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Hydra.Relay;

internal static class MessageDeserializer
{
    // the one way a message body becomes a T: null for a JSON null, JsonException for a malformed body
    internal static T? DecodeBody<T>(this ReadOnlyMemory<byte> body) => body.FromSaneJson<T>();

    // as DecodeBody, but a malformed body is a miss rather than a throw
    internal static T? TryDecodeBody<T>(this ReadOnlyMemory<byte> body) where T : class
    {
        try
        {
            return body.DecodeBody<T>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // as TryDecodeBody, logging a warning when a malformed or JSON-null body yields nothing
    internal static T? ParseMessage<T>(this ReadOnlyMemory<byte> body, ILogger log, string context) where T : class
    {
        var result = body.TryDecodeBody<T>();
        if (result == null) log.LogWarning("Failed to deserialize {Type} ({Context}) — dropping", typeof(T).Name, context);
        return result;
    }
}
