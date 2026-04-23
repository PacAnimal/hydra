using Cathedral.Extensions;
using Microsoft.Extensions.Logging;

namespace Hydra.Relay;

internal static class MessageDeserializer
{
    // deserializes body as T, logging a warning and returning null on failure
    internal static T? ParseMessage<T>(this ReadOnlyMemory<byte> body, ILogger log, string context) where T : class
    {
        var result = body.FromSaneJson<T>();
        if (result == null) log.LogWarning("Failed to deserialize {Type} ({Context}) — dropping", typeof(T).Name, context);
        return result;
    }
}
