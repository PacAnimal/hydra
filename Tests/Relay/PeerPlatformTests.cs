using Hydra.Relay;

namespace Tests.Relay;

[TestFixture]
public class PeerPlatformTests
{
    [TestCase(PeerPlatform.MacOs, "macOS")]
    [TestCase(PeerPlatform.Windows, "Windows")]
    [TestCase(PeerPlatform.Linux, "Linux")]
    [TestCase(PeerPlatform.Unknown, "unknown")]
    public void DisplayName_IsWhatPeopleCallIt(PeerPlatform platform, string expected) =>
        Assert.That(platform.DisplayName(), Is.EqualTo(expected));
}
