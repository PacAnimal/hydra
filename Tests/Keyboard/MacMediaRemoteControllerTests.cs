using Hydra.Keyboard;
using Hydra.Platform.MacOs;

namespace Tests.Keyboard;

[TestFixture]
public class MacMediaRemoteControllerTests
{
    [TestCase(SpecialKey.AudioPlay, 2u)]
    [TestCase(SpecialKey.AudioNext, 4u)]
    [TestCase(SpecialKey.AudioPrev, 5u)]
    public void MediaKeys_MapToActiveNowPlayingCommands(SpecialKey key, uint expectedCommand)
    {
        Assert.That(MacMediaRemoteController.CommandFor(key), Is.EqualTo(expectedCommand));
    }

    [Test]
    public void NonMediaKey_HasNoMediaRemoteCommand()
    {
        Assert.That(MacMediaRemoteController.CommandFor(SpecialKey.AudioMute), Is.Null);
    }
}
