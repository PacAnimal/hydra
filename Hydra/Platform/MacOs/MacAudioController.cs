using System.Diagnostics;

namespace Hydra.Platform.MacOs;

// CoreAudio is the public API behind macOS's default output volume. It avoids the synthetic
// NX_SYSDEFINED path, which current macOS releases no longer accept from ordinary user processes.
internal sealed class MacAudioController
{
    private const int KernSuccess = 0;
    private const uint UInt32Size = sizeof(uint);
    private const uint FloatSize = sizeof(float);
    private const float Step = 1f / 16f;

    private static readonly AudioObjectPropertyAddress DefaultOutputAddress = new(
        NativeMethods.KAudioHardwarePropertyDefaultOutputDevice,
        NativeMethods.KAudioObjectPropertyScopeGlobal,
        NativeMethods.KAudioObjectPropertyElementMain);

    private static readonly AudioObjectPropertyAddress VolumeAddress = new(
        NativeMethods.KAudioHardwareServiceDevicePropertyVirtualMainVolume,
        NativeMethods.KAudioObjectPropertyScopeOutput,
        NativeMethods.KAudioObjectPropertyElementMain);

    private static readonly AudioObjectPropertyAddress MuteAddress = new(
        NativeMethods.KAudioDevicePropertyMute,
        NativeMethods.KAudioObjectPropertyScopeOutput,
        NativeMethods.KAudioObjectPropertyElementMain);

    internal bool TryAdjustVolume(bool increase)
    {
        if (TryAdjustVolumeWithCoreAudio(increase)) return true;

        // Audio hardware (notably some display and USB output devices) can expose the default
        // output before their virtual main-volume property is initialised. System Settings wakes it
        // up on its first slider movement; this fallback performs that same normal macOS operation
        // so the first forwarded key works too.
        return TryAdjustVolumeWithAppleScript(increase);
    }

    internal bool TryToggleMute()
    {
        if (!TryGetDefaultOutputDevice(out var device)) return false;
        var size = UInt32Size;
        if (NativeMethods.AudioObjectGetPropertyData(device, in MuteAddress, 0, nint.Zero, ref size, out uint current) != KernSuccess)
            return false;

        var next = current == 0 ? 1u : 0u;
        return NativeMethods.AudioObjectSetPropertyData(device, in MuteAddress, 0, nint.Zero, UInt32Size, in next) == KernSuccess;
    }

    private static bool TryGetDefaultOutputDevice(out uint device)
    {
        var size = UInt32Size;
        return NativeMethods.AudioObjectGetPropertyData(NativeMethods.KAudioObjectSystemObject, in DefaultOutputAddress,
            0, nint.Zero, ref size, out device) == KernSuccess && device != 0;
    }

    private static bool TryAdjustVolumeWithCoreAudio(bool increase)
    {
        if (!TryGetDefaultOutputDevice(out var device)) return false;
        var size = FloatSize;
        if (NativeMethods.AudioObjectGetPropertyData(device, in VolumeAddress, 0, nint.Zero, ref size, out float current) != KernSuccess)
            return false;

        var next = Math.Clamp(current + (increase ? Step : -Step), 0f, 1f);
        return NativeMethods.AudioObjectSetPropertyData(device, in VolumeAddress, 0, nint.Zero, FloatSize, in next) == KernSuccess;
    }

    private static bool TryAdjustVolumeWithAppleScript(bool increase)
    {
        const string script = "set currentVolume to output volume of (get volume settings)\n"
            + "set nextVolume to currentVolume + VOLUME_DELTA\n"
            + "if nextVolume > 100 then set nextVolume to 100\n"
            + "if nextVolume < 0 then set nextVolume to 0\n"
            + "set volume output volume nextVolume";
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/osascript",
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "-e", script.Replace("VOLUME_DELTA", increase ? "6" : "-6") },
            });
            return process is not null && process.WaitForExit(1000) && process.ExitCode == 0;
        }
        catch { return false; }
    }
}
