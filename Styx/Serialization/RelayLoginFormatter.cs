using Cathedral.Extensions;
using Common.DTO;
using MessagePack;
using MessagePack.Formatters;

namespace Styx.Serialization;

// MessagePack matches map keys byte-for-byte, so a client sending {"hostName": …} where our property is
// named HostName deserializes to a login with null fields — an internal error rather than a useful answer.
// The JSON hub protocol is case-insensitive, so match keys the same way here and let third-party clients
// use whichever casing their language favours.
public class RelayLoginFormatter : IMessagePackFormatter<RelayLogin?>
{
    public void Serialize(ref MessagePackWriter writer, RelayLogin? value, MessagePackSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNil();
            return;
        }

        writer.WriteMapHeader(2);
        writer.Write(nameof(RelayLogin.Authorization));
        writer.Write(value.Authorization);
        writer.Write(nameof(RelayLogin.HostName));
        writer.Write(value.HostName);
    }

    public RelayLogin? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil()) return null;

        options.Security.DepthStep(ref reader);
        try
        {
            string? authorization = null;
            string? hostName = null;

            var count = reader.ReadMapHeader();
            for (var i = 0; i < count; i++)
            {
                var key = reader.ReadString();
                if (nameof(RelayLogin.Authorization).EqualsIgnoreCase(key)) authorization = reader.ReadString();
                else if (nameof(RelayLogin.HostName).EqualsIgnoreCase(key)) hostName = reader.ReadString();
                else reader.Skip();
            }

            // missing fields stay empty; the hub refuses the login rather than failing to bind the argument
            return new RelayLogin { Authorization = authorization ?? "", HostName = hostName ?? "" };
        }
        finally
        {
            reader.Depth--;
        }
    }
}
