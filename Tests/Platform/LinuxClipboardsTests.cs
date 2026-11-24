using Hydra.Config;
using Hydra.Platform.Linux;

namespace Tests.Platform;

// which clipboard a Linux run gets; a pure choice, so these run everywhere
[TestFixture]
public class LinuxClipboardsTests
{
    [TestCase(true, true, Mode.Master, LinuxClipboard.Portal)]
    [TestCase(true, false, Mode.Slave, LinuxClipboard.Portal)]
    [TestCase(false, true, Mode.Master, LinuxClipboard.Xorg)]
    [TestCase(false, true, Mode.Slave, LinuxClipboard.Xorg)]
    [TestCase(false, false, Mode.Master, LinuxClipboard.Memory)]
    [TestCase(false, false, Mode.Slave, LinuxClipboard.None)]
    public void Choose(bool wayland, bool hasXDisplay, Mode mode, LinuxClipboard expected) =>
        Assert.That(LinuxClipboards.Choose(wayland, hasXDisplay, mode), Is.EqualTo(expected));
}
