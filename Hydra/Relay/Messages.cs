using System.Text;
using System.Text.Json;
using Cathedral.Config;
using Hydra.Keyboard;
using Hydra.Mouse;

namespace Hydra.Relay;

public enum MessageKind : byte
{
    MouseMove = 1,
    KeyEvent = 2,
    MouseButton = 3,
    MouseScroll = 4,
    EnterScreen = 5,
    LeaveScreen = 6,
    MasterConfig = 7,
    ScreenInfo = 8,
    SlaveLog = 9,
}

public record MouseMoveMessage(string Screen, int X, int Y);
public record ScreenInfoEntry(string Name, int X, int Y, int Width, int Height, decimal Scale);
public record ScreenInfoMessage(List<ScreenInfoEntry> Screens);
public record SlaveLogMessage(int Level, string Category, string Message, string? Exception);
public record KeyEventMessage(KeyEventType Type, KeyModifiers Modifiers, char? Character, SpecialKey? Key);
public record MouseButtonMessage(MouseButton Button, bool IsPressed);
public record MouseScrollMessage(short XDelta, short YDelta);
public record EnterScreenMessage(string Screen, int X, int Y, int Width, int Height);

public static class MessageSerializer
{
    // wire format: [1 byte kind][utf-8 json]
    public static byte[] Encode<T>(MessageKind kind, T message)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, SaneJson.Options);
        var bytes = new byte[1 + json.Length];
        bytes[0] = (byte)kind;
        json.CopyTo(bytes, 1);
        return bytes;
    }

    public static (MessageKind Kind, string Json) Decode(byte[] payload)
    {
        if (payload.Length == 0) throw new ArgumentException("Empty payload", nameof(payload));
        var kind = (MessageKind)payload[0];
        var json = Encoding.UTF8.GetString(payload, 1, payload.Length - 1);
        return (kind, json);
    }
}
