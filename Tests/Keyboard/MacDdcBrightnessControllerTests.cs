using Hydra.Platform.MacOs;

namespace Tests.Keyboard;

[TestFixture]
public class MacBrightnessControllerTests
{
    [Test]
    public void BrightnessRequests_HaveExpectedDdcPackets()
    {
        Assert.That(MacBrightnessController.CreateGetBrightnessRequest(), Is.EqualTo(new byte[] { 0x82, 0x01, 0x10, 0xAC }));
        Assert.That(MacBrightnessController.CreateSetBrightnessRequest(75), Is.EqualTo(new byte[] { 0x84, 0x03, 0x10, 0x00, 0x4B, 0xE3 }));
    }

    [Test]
    public void BrightnessReply_ParsesCurrentAndMaximum()
    {
        var reply = new byte[] { 0x6E, 0x88, 0x02, 0x00, 0x10, 0x00, 0x00, 0x64, 0x00, 0x47, 0x00 };

        var parsed = MacBrightnessController.TryParseBrightnessReply(reply, out var current, out var max);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Is.True);
            Assert.That(current, Is.EqualTo(71));
            Assert.That(max, Is.EqualTo(100));
        }
    }

    [Test]
    public void BrightnessReply_RejectsWrongCommandOrInvalidRange()
    {
        Assert.That(MacBrightnessController.TryParseBrightnessReply(new byte[11], out _, out _), Is.False);
        Assert.That(MacBrightnessController.TryParseBrightnessReply(new byte[] { 0x6E, 0x88, 0x02, 0x00, 0x10, 0x00, 0x00, 0x64, 0x00, 0x65, 0x00 }, out _, out _), Is.False);
    }
}
