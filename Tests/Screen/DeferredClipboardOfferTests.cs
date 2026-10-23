using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Tests.Screen;

[TestFixture]
public class DeferredClipboardOfferTests
{
    // a host streaming its answer to an older pull has a newer pull out too: only the streaming pull waits untimed, so
    // the newer one, never answered, still runs out
    [Test]
    public void Defer_ANewerPullBesideAStream_StillRunsOut()
    {
        var clock = new FakeTimeProvider();
        using var offer = new DeferredClipboardOffer(clock, NullLogger.Instance);
        var streaming = new OutstandingPull("a", 1);
        string? timedOut = null;
        offer.TimedOut += host => timedOut = host;
        offer.Defer("b", [streaming, new OutstandingPull("a", 2)], p => p.Is(streaming));

        var afterStream = offer.Abandoned("a", streamOnly: true, pullId: 1);
        clock.Advance(DeferredClipboardOffer.Wait);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterStream, Is.Null, "pre-condition: still waits on the newer pull");
            Assert.That(timedOut, Is.EqualTo("b"));
            Assert.That(offer.IsWaiting("b"), Is.False);
        }
    }

    // an older stream's late failure ends only its own pull's wait, not the newer stream's from that host
    [Test]
    public void Abandoned_WithAPullId_EndsOnlyThatPullsWait()
    {
        using var offer = new DeferredClipboardOffer(new FakeTimeProvider(), NullLogger.Instance);
        offer.Defer("b", [new OutstandingPull("a", 1), new OutstandingPull("a", 2)], _ => true);

        var afterOlder = offer.Abandoned("a", streamOnly: true, pullId: 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterOlder, Is.Null, "still waits on the newer stream");
            Assert.That(offer.IsWaiting("b"), Is.True);
            Assert.That(offer.Abandoned("a", streamOnly: true, pullId: 2), Is.EqualTo("b"));
        }
    }
}
