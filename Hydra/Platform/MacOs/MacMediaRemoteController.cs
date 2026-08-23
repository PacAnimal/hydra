using System.Runtime.InteropServices;
using Hydra.Keyboard;

namespace Hydra.Platform.MacOs;

// MediaRemote is the private framework used by macOS media controls. It targets the active Now
// Playing client, unlike an app-specific AppleScript adapter or a synthetic media-key event.
internal sealed class MacMediaRemoteController
{
    private const string Framework = "/System/Library/PrivateFrameworks/MediaRemote.framework/MediaRemote";
    private const uint PlayPause = 2;
    private const uint NextTrack = 4;
    private const uint PreviousTrack = 5;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SendCommandDelegate(uint command, nint completion);

    private readonly SendCommandDelegate? _sendCommand;

    internal MacMediaRemoteController()
    {
        try
        {
            var library = NativeLibrary.Load(Framework);
            _sendCommand = Marshal.GetDelegateForFunctionPointer<SendCommandDelegate>(
                NativeLibrary.GetExport(library, "MRMediaRemoteSendCommand"));
        }
        catch
        {
            _sendCommand = null;
        }
    }

    internal bool TrySend(SpecialKey key)
    {
        var command = CommandFor(key);
        return command is { } value && TrySend(value);
    }

    internal static uint? CommandFor(SpecialKey key) => key switch
    {
        SpecialKey.AudioPlay => PlayPause,
        SpecialKey.AudioNext => NextTrack,
        SpecialKey.AudioPrev => PreviousTrack,
        _ => null,
    };

    private bool TrySend(uint command)
    {
        if (_sendCommand is null) return false;
        try
        {
            _sendCommand(command, nint.Zero);
            return true;
        }
        catch { return false; }
    }
}
