using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.Time.Testing;
using Tests.Setup;
using static Tests.Setup.ClipboardStreaming;

namespace Tests.Relay;

// a headless master (no display server, remoteOnly, e.g. a Raspberry Pi) has no OS clipboard; it still carries a copy
// from one slave to the next
[TestFixture]
public class HeadlessMasterClipboardTests : MasterClipboardFixture
{
    [Test]
    public async Task CopyOnOneSlave_ReachesTheNext()
    {
        var (platform, relay, service) = await StartHeadless();
        TransitionTestHelper.MoveRightTo(platform, relay, "win");

        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "mac"), "copied on mac"), "mac");
        await service.Flush();
        await service.HashReads();
        await SimulatePullRequest(relay, "win");
        await service.Flush();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.Sent.Of<ClipboardPushMessage>(MessageKind.ClipboardPush, "win").Select(p => p.Text), Is.EqualTo(["copied on mac"]));
            Assert.That(relay.Sent.Of(MessageKind.ClipboardPushNone, "win"), Is.Empty);
        }
    }

    [Test]
    public async Task BigImageStreamedFromOneSlave_StreamsOnToTheNext()
    {
        var (platform, relay, service) = await StartHeadless();
        TransitionTestHelper.MoveRightTo(platform, relay, "win");
        var image = BigImage(3);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "mac"), null, ImagePng: image));

        await Begin(relay, stream, "mac");
        await Chunks(relay, stream, host: "mac");
        await service.Flush();
        await service.HashReads();
        await SimulatePullRequest(relay, "win");
        var push = await SentPush(relay, "win", begins: 1);

        Assert.That(push.ImagePng, Is.EqualTo(image));
    }

    [Test]
    public async Task CopyOnTheNextSlave_ReachesTheFirstOnTheWayBack()
    {
        var (platform, relay, service) = await StartHeadless();
        TransitionTestHelper.MoveRightTo(platform, relay, "win");
        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "mac"), "copied on mac"), "mac");
        await service.Flush();

        TransitionTestHelper.MoveTo(platform, relay, "mac", -100, entries: 2);
        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "win"), "copied on win"), "win");
        await service.Flush();
        await service.HashReads();
        await SimulatePullRequest(relay, "mac");
        await service.Flush();

        Assert.That(relay.Sent.Of<ClipboardPushMessage>(MessageKind.ClipboardPush, "mac").Select(p => p.Text), Is.EqualTo(["copied on win"]));
    }

    [Test]
    // concealed: the master takes a Cleared answer only while it holds a concealed copy
    public async Task ConcealedCopyClearedAtItsSource_IsClearedForTheNextSlave()
    {
        var (platform, relay, service) = await StartHeadless();
        // mac's concealed copy, taken on the way to win
        TransitionTestHelper.MoveRightTo(platform, relay, "win");
        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "mac"), "secret", Concealed: true), "mac");
        await service.Flush();

        // back to mac and on to win again
        TransitionTestHelper.MoveTo(platform, relay, "mac", -100, entries: 2);
        await AnswerUnchanged(relay, "win");
        await service.Flush();
        TransitionTestHelper.MoveTo(platform, relay, "win", 100, entries: 2);
        var entered = Hashes(relay, "win").Count;
        // mac's password manager cleared it meanwhile, which mac answers on the way to win
        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "mac"), null, Cleared: true), "mac");
        await service.Flush();
        await service.HashReads();
        // win is told on its next visit
        TransitionTestHelper.MoveTo(platform, relay, "mac", -100, entries: 3);
        await AnswerUnchanged(relay, "win");
        TransitionTestHelper.MoveTo(platform, relay, "win", 100, entries: 3);
        await service.Flush();
        await service.HashReads();

        var last = Hashes(relay, "win").Skip(entered).Last();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(last.Hash, Is.Null);
            Assert.That(last.Cleared, Is.True);
        }
    }

    // "mac" left of "win", both online, the cursor on mac
    private Task<TestServiceBundle> StartHeadless() =>
        Services.StartOnline(TransitionTestHelper.CreateService(profile: TransitionTestHelper.RemoteOnlyPairConfig, clipboard: new MemoryClipboardSync(),
            osd: new RecordingOsd(), timeProvider: new FakeTimeProvider()), "mac", "win");
}
