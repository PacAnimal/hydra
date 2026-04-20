using System.Text;
using System.Text.Json;
using Cathedral.Config;
using Hydra.Keyboard;
using Hydra.Mouse;
using Microsoft.Extensions.Logging;

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
    MouseMoveDelta = 10,
    ScreensaverSync = 11,
    ClipboardPush = 12,         // master → slave: apply this clipboard (text/image/files)
    ClipboardPull = 13,         // master → slave: send me your clipboard
    ClipboardPullResponse = 14, // slave → master: here's my clipboard
    FileTransferRequest = 17,   // master → receiver: can you receive? (SourceHost = actual data sender if different)
    FileTransferStart = 26,     // data source → receiver: here's what's coming (FileNames + TotalBytes)
    FileTransferChunk = 18,     // data source → receiver: chunk of tar.gz data
    FileTransferDone = 19,      // data source → receiver: all data sent
    FileTransferAbort = 20,     // either → either: abort and clean up
    FileTransferAccepted = 25,  // receiver → master: destination validated, ready to receive
    FileSelectionQuery = 21,    // master → slave: what files are selected?
    FileSelectionResponse = 22, // slave → master: here are the selected files
    FileStreamRequest = 23,     // master → source slave: stream these files to target
    Osd = 24,                   // master → slave: display an on-screen notification
}

public record MouseMoveMessage(string Screen, int X, int Y);
public record MouseMoveDeltaMessage(int Dx, int Dy);
public record ScreenInfoEntry(string Name, int X, int Y, int Width, int Height, decimal MouseScale);

// ReSharper disable once InconsistentNaming
public enum PeerPlatform : byte { Unknown = 0, Linux = 1, MacOS = 2, Windows = 3 }

public record ScreenInfoMessage(List<ScreenInfoEntry> Screens, PeerPlatform? Platform = null);
public record MasterConfigMessage(LogLevel? LogLevel);
public record SlaveLogMessage(int Level, string Category, string Message, string? Exception);
public record KeyEventMessage(KeyEventType Type, KeyModifiers Modifiers, char? Character, SpecialKey? Key, int? RepeatDelayMs = null, int? RepeatRateMs = null);
public record MouseButtonMessage(MouseButton Button, bool IsPressed);
public record MouseScrollMessage(short XDelta, short YDelta);
public record EnterScreenMessage(string Screen, int X, int Y, int Width, int Height);
public record ScreensaverSyncMessage(bool Active);
public record LeaveScreenMessage;
public record ClipboardPullMessage;
public record ClipboardPushMessage(string Text, string? PrimaryText = null, byte[]? ImagePng = null);
public record ClipboardPullResponseMessage(string? Text, string? PrimaryText = null, byte[]? ImagePng = null);

public record FileTransferRequestMessage(string? SourceHost = null);
public record FileTransferStartMessage(string[] FileNames, long TotalBytes);
public record FileTransferChunkMessage(int Sequence, byte[] Data);
public record FileTransferDoneMessage(long TotalBytesSent, byte[] Sha256);
public record FileTransferAbortMessage(string Reason);
public record FileTransferAcceptedMessage;
public record FileSelectionQueryMessage;
public record FileSelectionResponseMessage(string[]? Paths, string? NotFocusedMessage = null);
public record FileStreamRequestMessage(string[] Paths, string TargetHost);
public record OsdMessage(string Text);

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

    public static DecodedMessage Decode(byte[] payload)
    {
        if (payload.Length == 0) throw new ArgumentException("Empty payload", nameof(payload));
        var kind = (MessageKind)payload[0];
        return new DecodedMessage(kind, Encoding.UTF8.GetString(payload, 1, payload.Length - 1));
    }
}

public record DecodedMessage(MessageKind Kind, string Json);
