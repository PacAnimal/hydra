using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.Logging.Abstractions;
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

        MoveTo(platform, relay, "mac", -100, entries: 2);
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
        MoveTo(platform, relay, "mac", -100, entries: 2);
        await AnswerUnchanged(relay, "win");
        await service.Flush();
        MoveTo(platform, relay, "win", 100, entries: 2);
        var entered = Hashes(relay, "win").Count;
        // mac's password manager cleared it meanwhile, which mac answers on the way to win
        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "mac"), null, Cleared: true), "mac");
        await service.Flush();
        await service.HashReads();
        // win is told on its next visit
        MoveTo(platform, relay, "mac", -100, entries: 3);
        await AnswerUnchanged(relay, "win");
        MoveTo(platform, relay, "win", 100, entries: 3);
        await service.Flush();
        await service.HashReads();

        var last = Hashes(relay, "win").Skip(entered).Last();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(last.Hash, Is.Null);
            Assert.That(last.Cleared, Is.True);
        }
    }

    [Test]
    public void Clear_ReadsAsEmpty_KeepsTheSelection_AndMovesTheCounter()
    {
        IClipboardSync clipboard = new MemoryClipboardSync();
        var copy = new ClipboardSnapshot("copied", "selected", null);
        clipboard.SetClipboard(copy);
        var afterCopy = clipboard.ChangeCount();
        var cleared = clipboard.ClearClipboard();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleared, Is.True);
            Assert.That(ClipboardUtils.ReadWithFallback(clipboard, null, NullLogger.Instance, "test")?.HasClipboard, Is.False);
            Assert.That(clipboard.GetPrimaryText(), Is.EqualTo("selected"));
            Assert.That(clipboard.ChangeCount(), Is.EqualTo(afterCopy + 1));
        }
    }

    // "mac" left of "win", both online, the cursor on mac
    private Task<TestServiceBundle> StartHeadless() =>
        Services.StartOnline(TransitionTestHelper.CreateService(profile: TransitionTestHelper.RemoteOnlyPairConfig, clipboard: new MemoryClipboardSync(),
            osd: new RecordingOsd(), timeProvider: new FakeTimeProvider()), "mac", "win");

    // the cursor moved by dx until host has been entered entries times
    private static void MoveTo(FakePlatform platform, FakeRelay relay, string host, int dx, int entries)
    {
        bool Entered() => relay.Sent.Of(MessageKind.EnterScreen, host).Count >= entries;
        for (var i = 0; i < 40 && !Entered(); i++)
            platform.FireMouseDelta(dx, 0);
        Assert.That(Entered(), Is.True, $"pre-condition: on {host} ({entries} entries)");
    }
}
