using Hydra.Relay;

namespace Tests.Relay;

/// <summary>
/// How a peer's advertisement is written and read back.
///
/// <para><b>The forward-compatibility is the point.</b> Capabilities travel as NAMES so that a master meeting
/// one it has never heard of can ignore it — an enum array would throw in Cathedral's converter and take the
/// whole <c>ScreenInfoMessage</c> with it, screens included. Names also mean the enum carries no numbering
/// anyone has to preserve.</para>
/// </summary>
[TestFixture]
public class PeerCapabilitiesTests
{
    [Test]
    public void ThisBuildAdvertisesWhatItCanDo()
    {
        var advertised = PeerCapabilities.Advertise();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(advertised, Is.Not.Empty, "a build that advertises nothing is never sent anything optional");
            Assert.That(PeerCapabilities.Parse(advertised), Is.EquivalentTo(PeerCapabilities.Mine),
                "what we send must be exactly what we would read back — the two halves cannot be allowed to drift");
        }
    }

    /// <summary>
    /// A name from a NEWER peer is ignored, and the ones alongside it still count.
    ///
    /// <para>This is the case that decides the wire format. Parsing strictly would not merely lose the
    /// unknown capability, it would throw away the message carrying it.</para>
    /// </summary>
    [Test]
    public void AnUnknownCapabilityIsIgnoredWithoutLosingTheKnownOnes()
    {
        var parsed = PeerCapabilities.Parse(["KeyEventBatch", "SomethingFromTheFuture"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed, Does.Contain(PeerCapability.KeyEventBatch));
            Assert.That(parsed, Has.Count.EqualTo(1), "the unknown name must be dropped, not mapped onto something");
        }
    }

    [Test]
    public void SilenceIsNoCapabilities()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(PeerCapabilities.Parse(null), Is.Empty, "a build that predates capabilities sends nothing");
            Assert.That(PeerCapabilities.Parse([]), Is.Empty);
        }
    }

    /// <summary>Case is not a peer's problem — the same capability spelled differently is the same capability.</summary>
    [Test]
    public void NamesAreReadCaseInsensitively() =>
        Assert.That(PeerCapabilities.Parse(["keyeventbatch"]), Does.Contain(PeerCapability.KeyEventBatch));

    /// <summary>
    /// A NUMBER is not a name — including the ones that would land on a real member.
    ///
    /// <para><b>"0" is the case that matters, and an earlier version of this test missed it.</b> Asserting
    /// only "7" and "-1" passes against a parse guarded by <c>Enum.IsDefined</c>, because those are not
    /// defined — while "0" IS defined and was accepted as a genuine claim to whatever member happens to sit
    /// at zero. That collides head-on with the promise that members may be reordered freely: reordering
    /// would silently change what a peer sending "0" was taken to claim.</para>
    /// </summary>
    [TestCase("0")]
    [TestCase("1")]
    [TestCase("7")]
    [TestCase("-1")]
    [TestCase("KeyEventBatch,KeyEventBatch")]
    public void ANumberIsNotACapability(string name) =>
        Assert.That(PeerCapabilities.Parse([name]), Is.Empty, $"'{name}' is not a capability NAME");
}
