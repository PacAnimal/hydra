using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Tests.Setup;
using static Tests.Setup.ClipboardStreaming;

namespace Tests.Relay.Edge;

// master modes: a remote-only master, the screensaver's return home and restore, the lock hotkey and a relay reconnect,
// each mid-stream: no exception, the stream lands or is cleanly dropped, never left pending, and the host the cursor
// lands on next is offered the newest clipboard, never a stale one
[TestFixture]
public class ClipboardEdgeModeTests : MasterClipboardFixture
{
    // -- 1. a remote-only master --

    [Test]
    public async Task RemoteOnly_BigClipboard_PullRequestFromTheFirstRemote_StreamsThePushAndItLands()
    {
        var clipboard = new FakeClipboardSync();
        var image = BigImage(1);
        clipboard.SetupImage(image);
        var log = new CapturingLog<InputRouter>();
        var (platform, relay, service) = await StartRemoteOnly(clipboard, log: log);
        Assert.That(relay.Sent.Of(MessageKind.EnterScreen, "mac"), Is.Not.Empty, "pre-condition: on mac");

        await SimulatePullRequest(relay, "mac");
        var push = await SentPush(relay, "mac", begins: 1);
        // the entry's hash, read off the actor
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(platform.IsOnVirtualScreen, Is.True);
            Assert.That(Hashes(relay, "mac").Select(h => h.Hash), Is.EqualTo([ImageHash(image)]));
            Assert.That(push.ImagePng, Is.EqualTo(image));
            Assert.That(relay.Sent.Of(MessageKind.ClipboardPush, "mac"), Is.Empty);
            Assert.That(relay.Sent.Of(MessageKind.ClipboardStreamAbort), Is.Empty);
            Assert.That(Errors(log), Is.Empty);
        }
    }

    [Test]
    public async Task RemoteOnly_SwitchingRemotes_TheFirstsAnswerStreamsIn_TheNextGetsPendingThenItsHashAndItsPush()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var log = new CapturingLog<InputRouter>();
        var (platform, relay, service) = await StartRemoteOnly(clipboard, log: log);
        TransitionTestHelper.MoveRightTo(platform, relay, "win");
        var early = Hashes(relay, "win").Count;
        var image = BigImage(2);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "mac"), null, ImagePng: image));

        await Begin(relay, stream, "mac");
        var atBegin = Hashes(relay, "win");
        await Chunks(relay, stream, host: "mac");
        await service.Flush();
        await service.HashReads();
        var hashes = Hashes(relay, "win");
        await SimulatePullRequest(relay, "win");
        var push = await SentPush(relay, "win", begins: 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(early, Is.Zero);
            Assert.That(atBegin, Is.EqualTo([new ClipboardHashMessage(null, Pending: true)]));
            Assert.That(clipboard.ImagePng, Is.EqualTo(image));
            Assert.That(hashes, Has.Count.EqualTo(2));
            Assert.That(hashes[^1].Hash, Is.EqualTo(ImageHash(image)));
            Assert.That(hashes[^1].Pending, Is.Null);
            Assert.That(push.ImagePng, Is.EqualTo(image));
            Assert.That(relay.Sent.Of(MessageKind.ClipboardPush, "win"), Is.Empty);
            Assert.That(relay.Sent.Of(MessageKind.ClipboardStreamAbort), Is.Empty);
            Assert.That(Errors(log), Is.Empty);
        }
    }

    // -- 2. the screensaver returns the cursor home mid-stream, and its end restores the visit --

    [Test]
    public async Task Screensaver_MidPushToTheCursorsHost_ThePushLands_AndTheRestoredVisitGetsItsHash()
    {
        var clipboard = new FakeClipboardSync();
        var image = BigImage(1);
        clipboard.SetupImage(image);
        var screensaver = new FakeScreenSaverSync();
        var log = new CapturingLog<InputRouter>();
        var (platform, relay, service) = await StartSingle(clipboard, screensaver, log);
        TransitionTestHelper.EnterRemote(platform);
        relay.Reliable.Hold();
        await SimulatePullRequest(relay);
        await relay.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 1, "remote");

        screensaver.FireActivated();
        await service.Flush();
        await service.HashReads();
        var wentHome = !platform.IsOnVirtualScreen;
        // the slave counts the push still streaming to it as applied
        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay), null, Unchanged: true));
        var before = Hashes(relay, "remote").Count;
        screensaver.FireDeactivated();
        await service.Flush();
        await service.HashReads();
        relay.Reliable.Release();
        var push = await SentPush(relay, "remote", begins: 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(wentHome, Is.True, "pre-condition: the screensaver returned the cursor home");
            Assert.That(platform.IsOnVirtualScreen, Is.True, "the visit is restored");
            Assert.That(push.ImagePng, Is.EqualTo(image));
            Assert.That(relay.Sent.Of(MessageKind.ClipboardStreamAbort), Is.Empty);
            Assert.That(Hashes(relay, "remote").Skip(before).Select(h => h.Hash), Is.EqualTo([ImageHash(image)]));
            Assert.That(Errors(log), Is.Empty);
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Screensaver_ItsReturnPullsAStreamedAnswer_TheRestoredVisitGetsTheAnswersHashNeverTheOld(bool restoreMidStream)
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var screensaver = new FakeScreenSaverSync();
        var log = new CapturingLog<InputRouter>();
        var (platform, relay, service) = await StartSingle(clipboard, screensaver, log);
        TransitionTestHelper.EnterRemote(platform);
        screensaver.FireActivated();
        await service.Flush();
        await service.HashReads();
        Assert.That(relay.Sent.Of(MessageKind.ClipboardPull, "remote"), Has.Count.EqualTo(1), "pre-condition: the return pulled");
        var before = Hashes(relay, "remote").Count;
        var image = BigImage(2);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay), null, ImagePng: image));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);

        if (restoreMidStream) await Restore(screensaver, service);
        await Chunks(relay, stream, from: 1);
        if (!restoreMidStream) await Restore(screensaver, service);
        await service.Flush();
        await service.HashReads();

        var hashes = Hashes(relay, "remote").Skip(before).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(platform.IsOnVirtualScreen, Is.True, "the visit is restored");
            Assert.That(clipboard.ImagePng, Is.EqualTo(image));
            Assert.That(hashes.Select(h => h.Hash), Has.None.EqualTo(TextHash("old on the master")));
            Assert.That(hashes, Is.Not.Empty);
            Assert.That(hashes[^1].Hash, Is.EqualTo(ImageHash(image)));
            Assert.That(hashes[^1].Pending, Is.Null);
            Assert.That(relay.Sent.Of(MessageKind.ClipboardStreamAbort), Is.Empty);
            Assert.That(Errors(log), Is.Empty);
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task Screensaver_WhileAnotherHostsAnswerStreamsToAPendingHost_TheRestoredVisitGetsTheAnswersHashNeverTheOld(bool completesWhileAway)
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var screensaver = new FakeScreenSaverSync();
        var log = new CapturingLog<InputRouter>();
        var (platform, relay, service) = await StartSides(clipboard, new FakeTimeProvider(), new RecordingOsd(), screensaver, log);
        VisitAndLeaveRemote(platform);
        var image = BigImage(2);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "remote"), null, ImagePng: image));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);
        EnterLeftHost(platform, relay);
        var told = Hashes(relay, "remote2");

        screensaver.FireActivated();
        await service.Flush();
        await service.HashReads();
        var wentHome = !platform.IsOnVirtualScreen;
        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "remote2"), null, Unchanged: true), "remote2");
        if (completesWhileAway)
        {
            await Chunks(relay, stream, from: 1);
            // its wait ended while away, so remote2, told Pending, is sent its hash then, before the restore
            await service.HashReads();
        }
        await Restore(screensaver, service);
        if (!completesWhileAway) await Chunks(relay, stream, from: 1);
        await service.Flush();
        await service.HashReads();

        var hashes = Hashes(relay, "remote2");
        var answer = ImageHash(image);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(told, Is.EqualTo([new ClipboardHashMessage(null, Pending: true)]), "pre-condition: remote2 told Pending");
            Assert.That(wentHome, Is.True, "pre-condition: the screensaver returned the cursor home");
            Assert.That(relay.Sent.Of(MessageKind.EnterScreen, "remote2"), Has.Count.EqualTo(2), "the visit is restored");
            Assert.That(clipboard.ImagePng, Is.EqualTo(image));
            Assert.That(hashes.Select(h => h.Hash), Has.None.EqualTo(TextHash("old on the master")));
            // owed while away: the answer's hash for the visit it was told on, then the restored visit's own; else the
            // restored visit, still waiting, is told Pending again and sent the answer's hash once
            Assert.That(hashes.Select(h => h.Pending), Is.EqualTo(completesWhileAway ? new bool?[] { true, null, null } : [true, true, null]));
            Assert.That(hashes.Select(h => h.Hash), Is.EqualTo(completesWhileAway ? new ulong?[] { null, answer, answer } : [null, null, answer]));
            if (completesWhileAway) Assert.That(hashes[1].Visit, Is.LessThan(hashes[^1].Visit ?? int.MinValue), "the owed hash names the earlier visit");
            Assert.That(relay.Sent.Of(MessageKind.ClipboardStreamAbort), Is.Empty);
            Assert.That(Errors(log), Is.Empty);
        }
    }

    // -- 3. the lock hotkey mid-stream --

    [Test]
    public async Task LockHotkeyMidAnswer_TheStreamLands_AndTheEntryAfterUnlockGetsItsHashNeverTheOld()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var log = new CapturingLog<InputRouter>();
        var (platform, relay, service) = await StartSingle(clipboard, new FakeScreenSaverSync(), log);
        VisitAndLeaveRemote(platform);
        var image = BigImage(2);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay), null, ImagePng: image));
        await Begin(relay, stream);
        await Chunk(relay, stream.Chunks[0]);

        platform.PressHotkey('l');
        await Chunk(relay, stream.Chunks[1]);
        platform.PressHotkey('l');
        var before = Hashes(relay, "remote").Count;
        TransitionTestHelper.EnterRemote(platform);
        await Chunks(relay, stream, from: 2);
        await service.Flush();
        await service.HashReads();

        var hashes = Hashes(relay, "remote").Skip(before).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.ImagePng, Is.EqualTo(image));
            Assert.That(hashes.Select(h => h.Hash), Has.None.EqualTo(TextHash("old on the master")));
            Assert.That(hashes, Is.Not.Empty);
            Assert.That(hashes[^1].Hash, Is.EqualTo(ImageHash(image)));
            Assert.That(hashes[^1].Pending, Is.Null);
            Assert.That(relay.Sent.Of(MessageKind.ClipboardStreamAbort), Is.Empty);
            Assert.That(Errors(log), Is.Empty);
        }
    }

    [Test]
    public async Task RemoteOnly_UnlockAndRelockMidPush_ThePushLands_AndTheReentryGetsItsHash()
    {
        var clipboard = new FakeClipboardSync();
        var image = BigImage(1);
        clipboard.SetupImage(image);
        var log = new CapturingLog<InputRouter>();
        var (platform, relay, service) = await StartRemoteOnly(clipboard, log: log);
        relay.Reliable.Hold();
        await SimulatePullRequest(relay, "mac");
        await relay.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 1, "mac");

        platform.PressHotkey('l');
        var unlockedHome = !platform.IsOnVirtualScreen;
        // the slave counts the push still streaming to it as applied
        await AnswerPull(relay, new ClipboardPullResponseMessage(PullIdTo(relay, "mac"), null, Unchanged: true), "mac");
        var before = Hashes(relay, "mac").Count;
        platform.PressHotkey('l');
        await service.Flush();
        await service.HashReads();
        relay.Reliable.Release();
        var push = await SentPush(relay, "mac", begins: 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unlockedHome, Is.True, "pre-condition: unlocking returned home");
            Assert.That(platform.IsOnVirtualScreen, Is.True, "relocking re-entered");
            Assert.That(push.ImagePng, Is.EqualTo(image));
            Assert.That(relay.Sent.Of(MessageKind.ClipboardStreamAbort), Is.Empty);
            Assert.That(Hashes(relay, "mac").Skip(before).Select(h => h.Hash), Is.EqualTo([ImageHash(image)]));
            Assert.That(Errors(log), Is.Empty);
        }
    }

    // -- 4. the master's relay reconnects while a remote-only master streams --

    [Test]
    public async Task RemoteOnly_RelayReconnectsMidPush_TheOldStreamStops_AndAFreshPushRunsEndToEnd()
    {
        var clipboard = new FakeClipboardSync();
        var image = BigImage(1);
        clipboard.SetupImage(image);
        var log = new CapturingLog<InputRouter>();
        var (platform, relay, service) = await StartRemoteOnly(clipboard, log: log);
        relay.Reliable.Hold();
        await SimulatePullRequest(relay, "mac");
        await relay.Sent.WaitFor(MessageKind.ClipboardStreamBegin, 1, "mac");
        var dropped = relay.Sent.Of<ClipboardStreamBeginMessage>(MessageKind.ClipboardStreamBegin, "mac").Single().Id;

        await relay.FireDisconnected();
        var wentHome = !platform.IsOnVirtualScreen;
        relay.Reliable.Release();
        var before = Hashes(relay, "mac").Count;
        await TransitionTestHelper.BringHostsOnline(relay, ["mac", "win"]);
        await service.Flush();
        await service.HashReads();
        var reentered = platform.IsOnVirtualScreen;
        await SimulatePullRequest(relay, "mac");
        var push = await SentPush(relay, "mac", begins: 2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(wentHome, Is.True, "pre-condition: the disconnect returned home");
            Assert.That(reentered, Is.True, "pre-condition: the reconnect re-entered");
            Assert.That(Hashes(relay, "mac").Skip(before).Select(h => h.Hash), Is.EqualTo([ImageHash(image)]));
            Assert.That(push.ImagePng, Is.EqualTo(image));
            Assert.That(relay.Sent.Of<ClipboardStreamChunkMessage>(MessageKind.ClipboardStreamChunk).Select(c => c.Id), Has.None.EqualTo(dropped));
            Assert.That(Errors(log), Is.Empty);
        }
    }

    [Test]
    public async Task RemoteOnly_RelayReconnectsMidAnswer_NothingLateApplies_TheFailureIsTold_AndAFreshPushRunsEndToEnd()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("old on the master");
        var osd = new RecordingOsd();
        var clock = new FakeTimeProvider();
        var log = new CapturingLog<InputRouter>();
        var (platform, relay, service) = await StartRemoteOnly(clipboard, osd, clock, log);
        TransitionTestHelper.MoveRightTo(platform, relay, "win");
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(PullIdTo(relay, "mac"), null, ImagePng: BigImage(2)));
        await Begin(relay, stream, "mac");
        await Chunk(relay, stream.Chunks[0], "mac");

        await relay.FireDisconnected();
        await Chunks(relay, stream, from: 1, host: "mac");
        clock.Advance(ClipboardStreamReceiver.Watchdog);
        await service.Flush();
        await service.HashReads();
        var whileDown = new { clipboard.Text, clipboard.ImagePng, clipboard.SetClipboardCallCount };
        var shown = await Shown(osd);
        // a copy made on the master while the relay was down
        var fresh = BigImage(3);
        clipboard.SetupImage(fresh);
        var before = Hashes(relay, "mac").Count;
        await TransitionTestHelper.BringHostsOnline(relay, ["mac", "win"]);
        await service.Flush();
        await service.HashReads();
        await SimulatePullRequest(relay, "mac");
        var push = await SentPush(relay, "mac", begins: 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whileDown.SetClipboardCallCount, Is.Zero);
            Assert.That(whileDown.Text, Is.Null);
            Assert.That(whileDown.ImagePng, Is.Null);
            // the answer the master emptied for never came: an honest empty, told
            Assert.That(shown, Does.Contain(ClipboardStreamReceiver.FailedOsd));
            Assert.That(log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message),
                Has.Some.Contains("mac").And.Contains("lipboard"));
            Assert.That(platform.IsOnVirtualScreen, Is.True, "the reconnect re-entered");
            Assert.That(Hashes(relay, "mac").Skip(before).Select(h => h.Hash), Is.EqualTo([ImageHash(fresh)]));
            Assert.That(push.ImagePng, Is.EqualTo(fresh));
            Assert.That(Errors(log), Is.Empty);
        }
    }

    // -- helpers --

    // a remote-only master, "mac" left of "win", both online, the cursor on mac
    private Task<TestServiceBundle> StartRemoteOnly(FakeClipboardSync clipboard, RecordingOsd? osd = null, FakeTimeProvider? clock = null,
        CapturingLog<InputRouter>? log = null) =>
        Services.StartOnline(TransitionTestHelper.CreateService(profile: TransitionTestHelper.RemoteOnlyPairConfig, clipboard: clipboard,
            osd: osd ?? new RecordingOsd(), timeProvider: clock ?? new FakeTimeProvider(), log: log), "mac", "win");

    // the standard master with "remote" online
    private Task<TestServiceBundle> StartSingle(FakeClipboardSync clipboard, FakeScreenSaverSync screensaver, CapturingLog<InputRouter> log) =>
        Services.StartOnline(TransitionTestHelper.CreateService(clipboard: clipboard, screenSaverSync: screensaver, osd: new RecordingOsd(),
            timeProvider: new FakeTimeProvider(), log: log));

    // the screensaver ends, which restores the visit it interrupted
    private static async Task Restore(FakeScreenSaverSync screensaver, InputRouter service)
    {
        screensaver.FireDeactivated();
        await service.Flush();
        await service.HashReads();
    }

    private static List<CapturedEntry> Errors(CapturingLog<InputRouter> log) => [.. log.Entries.Where(e => e.Level >= LogLevel.Error)];
}
