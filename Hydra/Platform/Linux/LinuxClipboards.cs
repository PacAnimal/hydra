using Hydra.Config;

namespace Hydra.Platform.Linux;

public enum LinuxClipboard { Portal, Xorg, Memory, None }

public static class LinuxClipboards
{
    // a headless master (no display server, always remoteOnly) has no OS clipboard, but still carries a copy from one
    // slave to the next
    public static LinuxClipboard Choose(bool wayland, bool hasXDisplay, Mode mode) =>
        wayland ? LinuxClipboard.Portal :
        hasXDisplay ? LinuxClipboard.Xorg :
        mode == Mode.Master ? LinuxClipboard.Memory :
        LinuxClipboard.None;
}
