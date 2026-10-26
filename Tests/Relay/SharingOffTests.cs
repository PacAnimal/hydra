using Cathedral.Extensions;
using Hydra.Config;
using Hydra.FileTransfer;
using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.Time.Testing;
using Tests.Setup;
using static Tests.Setup.ClipboardStreaming;

namespace Tests.Relay;

// clipboardSync Off turns off clipboard sync and file transfer with every peer, both ways, whichever side sets it: the
// master by its own profile or a slave's screen info, the slave by its own profile, even towards a master that never
// read that screen info
[TestFixture]
public class SharingOffTests : MasterClipboardFixture
{
    private readonly SlaveRelays _slaves = new();

    [TearDown]
    public Task StopSlaves() => _slaves.StopAll();

    // -- a master that is off --

    [Test]
    public async Task OffMaster_VisitingAndLeaving_SendsNoClipboard()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("secret");
        var (platform, relay, service) = await StartMaster(clipboard, profile: SharingOff);

        VisitAndLeaveRemote(platform);
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.Sent.Snapshot().Where(s => MessageKinds.IsClipboard(s.Kind)), Is.Empty);
            Assert.That(clipboard.ReadCallCount, Is.Zero);
        }
    }

    [Test]
    public async Task OffMaster_TakesNothingFromASlave()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("mine");
        var (platform, relay, service) = await StartMaster(clipboard, profile: SharingOff);
        VisitAndLeaveRemote(platform);
        var stream = StreamedAnswer(1, new ClipboardPullResponseMessage(1, null, ImagePng: BigImage(2)));

        await SimulatePullRequest(relay);
        await AnswerPull(relay, new ClipboardPullResponseMessage(1, "theirs"));
        await Begin(relay, stream);
        await Chunks(relay, stream);
        await service.Flush();
        await service.HashReads();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(clipboard.ClearClipboardCallCount, Is.Zero);
            Assert.That(clipboard.Text, Is.EqualTo("mine"));
            Assert.That(relay.Sent.Snapshot().Where(s => MessageKinds.IsClipboard(s.Kind)), Is.Empty);
        }
    }

    [TestCase(true, 'c')]
    [TestCase(false, 'c')]
    [TestCase(true, 'v')]
    [TestCase(false, 'v')]
    public async Task OffMaster_FileHotkeys_ShowItIsOffAndSendNothing(bool onTheRemote, char hotkey)
    {
        var finder = new GatedFileSelectionDetector();
        var osd = new RecordingOsd();
        var fileTransfer = FileTransferService.Null();
        fileTransfer.SetCopyBuffer("remote", ["/home/me/a.txt"]);
        var (platform, relay, service) = await Services.StartOnline(TransitionTestHelper.CreateService(
            profile: SharingOff, selectionDetector: finder, osd: osd, fileTransfer: fileTransfer));
        if (onTheRemote) TransitionTestHelper.EnterRemote(platform);
        relay.Sent.Clear();

        await Press(platform, hotkey);
        // the OSD proves the hotkey was handled, so the query it would have started is the latest one
        var shown = await Shown(onTheRemote, osd, relay);
        await service.LocalSelectionQuery.WaitAsync(Bound);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(shown, Is.EqualTo(FileTransferService.SharingOffOsd));
            Assert.That(finder.Queried.IsCompleted, Is.False, "the local file manager is never asked");
            Assert.That(relay.Sent.Snapshot().Where(s => IsFileKind(s.Kind)), Is.Empty);
        }
    }

    // -- a slave that says it is off --

    [Test]
    public async Task PeerAdvertisingSharingOff_IsSentNoClipboard()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("secret");
        var (platform, relay, service) = await StartMaster(clipboard);
        await TransitionTestHelper.AdvertiseScreens(relay, "remote", 1, sharingOff: true);
        relay.Sent.Clear();

        VisitAndLeaveRemote(platform);
        await SimulatePullRequest(relay);
        await service.Flush();
        await service.HashReads();

        Assert.That(relay.Sent.Snapshot().Where(s => MessageKinds.IsClipboard(s.Kind)), Is.Empty);
    }

    [Test]
    public async Task PeerTurningSharingBackOn_IsSentTheClipboardAgain()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("shared");
        var (platform, relay, service) = await StartMaster(clipboard);
        await TransitionTestHelper.AdvertiseScreens(relay, "remote", 1, sharingOff: true);
        await TransitionTestHelper.AdvertiseScreens(relay, "remote", 1);

        TransitionTestHelper.EnterRemote(platform);
        await service.HashReads();

        Assert.That(Hashes(relay, "remote"), Is.Not.Empty);
    }

    // a peer's flag goes with it, so the next one by that name is shared with unless it says otherwise
    [Test]
    public async Task PeerSharingOff_IsForgottenWhenItLeaves()
    {
        var world = new WorldState();
        world.SetPeerSharingOff("remote", true);
        world.SetPeerSharingOff("other", true);

        await world.UpdatePeers(["other"], ["remote", "other"]);
        var afterLeaving = world.PeerSharingOff("remote");
        await world.ClearPeers();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterLeaving, Is.False);
            Assert.That(world.PeerSharingOff("other"), Is.False);
        }
    }

    [Test]
    public async Task CopyHotkeyOnAPeerThatIsOff_ShowsItIsOffAndAsksNothing()
    {
        var osd = new RecordingOsd();
        var (platform, relay, _) = await Services.StartOnline(TransitionTestHelper.CreateService(osd: osd));
        await TransitionTestHelper.AdvertiseScreens(relay, "remote", 1, sharingOff: true);
        TransitionTestHelper.EnterRemote(platform);
        relay.Sent.Clear();

        await Press(platform, 'c');

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await Shown(true, osd, relay), Is.EqualTo(FileTransferService.SharingOffOsd));
            Assert.That(relay.Sent.Of(MessageKind.FileSelectionQuery), Is.Empty);
        }
    }

    // the target off (a paste onto it of files copied home), or the copy's source off (a paste home of files copied there)
    [TestCase("home", true)]
    [TestCase("remote", false)]
    public async Task PasteWithAPeerThatIsOff_ShowsItIsOffAndSendsNothing(string copiedFrom, bool pasteOnTheRemote)
    {
        var osd = new RecordingOsd();
        var fileTransfer = FileTransferService.Null();
        fileTransfer.SetCopyBuffer(copiedFrom, ["/home/me/a.txt"]);
        var (platform, relay, _) = await Services.StartOnline(
            TransitionTestHelper.CreateService(selectionDetector: new GatedFileSelectionDetector(), osd: osd, fileTransfer: fileTransfer));
        await TransitionTestHelper.AdvertiseScreens(relay, "remote", 1, sharingOff: true);
        if (pasteOnTheRemote) TransitionTestHelper.EnterRemote(platform);
        relay.Sent.Clear();

        await Press(platform, 'v');

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await Shown(pasteOnTheRemote, osd, relay), Is.EqualTo(FileTransferService.SharingOffOsd));
            Assert.That(relay.Sent.Snapshot().Where(s => IsFileKind(s.Kind)), Is.Empty);
        }
    }

    // a target that refuses a transfer for being off is shown so, where the user is pasting
    [Test]
    public async Task TargetAbortingForSharingOff_IsShownItIsOff()
    {
        var fileTransfer = FileTransferService.Null();
        fileTransfer.SetCopyBuffer("remote2", ["/home/me/a.txt"]);
        var (platform, relay, _) = await Services.StartOnline(
            TransitionTestHelper.CreateService(profile: TransitionTestHelper.ChainConfig, selectionDetector: new GatedFileSelectionDetector(),
                fileTransfer: fileTransfer), "remote", "remote2");
        TransitionTestHelper.EnterRemote(platform);
        await Press(platform, 'v');
        Assert.That(relay.Sent.TargetsOf(MessageKind.FileTransferRequest), Is.EqualTo(["remote"]), "the paste asked the target first");
        relay.Sent.Clear();

        await relay.FireMessageReceived("remote", MessageKind.FileTransferAbort,
            new FileTransferAbortMessage(FileTransferService.ReasonSharingOff).ToSaneJson());

        Assert.That(relay.Sent.Of<OsdMessage>(MessageKind.Osd, "remote").Select(m => m.Text), Is.EqualTo([FileTransferService.SharingOffOsd]));
    }

    // -- a slave that is off, its master unaware --

    [Test]
    public async Task OffSlave_SaysSoInItsScreenInfo()
    {
        var slave = OffSlave(new FakeClipboardSync());

        await slave.SimulateMasterConfig(Master);

        Assert.That(slave.Sent.Of<ScreenInfoMessage>(MessageKind.ScreenInfo, Master).Single().SharingOff, Is.True);
    }

    [Test]
    public async Task OnSlave_SaysSoInItsScreenInfo()
    {
        var slave = _slaves.Add(new TestableSlaveRelay(clipboard: new FakeClipboardSync()));

        await slave.SimulateMasterConfig(Master);

        Assert.That(slave.Sent.Of<ScreenInfoMessage>(MessageKind.ScreenInfo, Master).Single().SharingOff, Is.False);
    }

    [Test]
    public async Task OffSlave_AnswersAPullUnchanged_WithoutReadingItsClipboard()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("secret");
        var slave = OffSlave(clipboard);

        await Pull(slave, pullId: 7);
        await slave.Sent.WaitFor(MessageKind.ClipboardPullResponse).WaitAsync(Bound);

        var answer = slave.Sent.Of<ClipboardPullResponseMessage>(MessageKind.ClipboardPullResponse, Master).Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(answer.PullId, Is.EqualTo(7));
            Assert.That(answer.Unchanged, Is.True);
            Assert.That(answer.Text, Is.Null);
            Assert.That(clipboard.ReadCallCount, Is.Zero);
        }
    }

    [Test]
    public async Task OffSlave_AbortsAStreamSilently_AndKeepsItsClipboard()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("mine");
        var osd = new RecordingOsd();
        var slave = OffSlave(clipboard, osd);
        var stream = HashedPush(3, new ClipboardPushMessage(BigText('x')));

        await Begin(slave, stream);
        await Chunks(slave, stream);
        await slave.ClipboardIdle();

        var abort = slave.Sent.Of<ClipboardStreamAbortMessage>(MessageKind.ClipboardStreamAbort, Master).Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(abort.Id, Is.EqualTo(3));
            Assert.That(abort.Failed, Is.Not.True, "a failed abort would show Clipboard Transfer Failed on the master");
            Assert.That(clipboard.ClearClipboardCallCount, Is.Zero);
            Assert.That(clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(clipboard.Text, Is.EqualTo("mine"));
            Assert.That(osd.Shown.Count, Is.Zero);
        }
    }

    [Test]
    public async Task OffSlave_IgnoresHashesAndPushes()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("mine");
        var slave = OffSlave(clipboard);

        await slave.SimulateReceive(Master, MessageKind.ClipboardHash, new ClipboardHashMessage(42, Visit: 1).ToSaneJson());
        await slave.SimulateReceive(Master, MessageKind.ClipboardHash, new ClipboardHashMessage(null, Pending: true).ToSaneJson());
        await slave.SimulateReceive(Master, MessageKind.ClipboardPush, new ClipboardPushMessage("theirs").ToSaneJson());
        await slave.ClipboardIdle();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.Text, Is.EqualTo("mine"));
            Assert.That(clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(clipboard.ClearClipboardCallCount, Is.Zero);
            Assert.That(clipboard.ReadCallCount, Is.Zero);
            Assert.That(slave.Sent.Snapshot().Where(s => MessageKinds.IsClipboard(s.Kind)), Is.Empty);
        }
    }

    [Test]
    public async Task OffSlave_AnswersAFileSelectionQuery_ThatItIsOff()
    {
        var finder = new GatedFileSelectionDetector();
        var slave = _slaves.Add(new TestableSlaveRelay(clipboard: new FakeClipboardSync(), selectionDetector: finder, clipboardSync: ClipboardSyncMode.Off));

        await slave.SimulateReceive(Master, MessageKind.FileSelectionQuery, new FileSelectionQueryMessage().ToSaneJson());

        var answer = slave.Sent.Of<FileSelectionResponseMessage>(MessageKind.FileSelectionResponse, Master).Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(answer.Paths, Is.Null);
            Assert.That(answer.Message, Is.EqualTo(FileTransferService.SharingOffOsd));
            Assert.That(finder.Queried.IsCompleted, Is.False);
        }
    }

    [Test]
    public async Task OffSlave_RefusesATransferToIt_AndAStreamFromIt()
    {
        var slave = OffSlave(new FakeClipboardSync());

        await slave.SimulateReceive(Master, MessageKind.FileTransferRequest, new FileTransferRequestMessage().ToSaneJson());
        await slave.SimulateReceive(Master, MessageKind.FileStreamRequest, new FileStreamRequestMessage(["/home/me/a.txt"], "other").ToSaneJson());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slave.Sent.Of<FileTransferAbortMessage>(MessageKind.FileTransferAbort, Master).Select(a => a.Reason),
                    Is.EqualTo([FileTransferService.ReasonSharingOff, FileTransferService.ReasonSharingOff]));
            Assert.That(slave.Sent.Of<FileTransferAbortMessage>(MessageKind.FileTransferAbort, "other").Select(a => a.Reason),
                Is.EqualTo([FileTransferService.ReasonSharingOff]));
        }
    }

    // a dormant slave that is off refuses as off: silently, and saying so
    [Test]
    public async Task DormantOffSlave_RefusesAsOff()
    {
        var osd = new RecordingOsd();
        var slave = OffSlave(new FakeClipboardSync(), osd);
        await slave.Dormancy.Enter();
        var stream = HashedPush(4, new ClipboardPushMessage(BigText('x')));

        await Begin(slave, stream);
        await slave.SimulateReceive(Master, MessageKind.FileSelectionQuery, new FileSelectionQueryMessage().ToSaneJson());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slave.Sent.Of<ClipboardStreamAbortMessage>(MessageKind.ClipboardStreamAbort, Master).Single().Failed, Is.Not.True);
            Assert.That(slave.Sent.Of<FileSelectionResponseMessage>(MessageKind.FileSelectionResponse, Master).Single().Message,
                Is.EqualTo(FileTransferService.SharingOffOsd));
            Assert.That(osd.Shown.Count, Is.Zero);
        }
    }

    // a stream request from a master unaware of it: the target it set up for the stream is told too, or it waits out its watchdog
    [Test]
    public async Task OffSlave_RefusingAStreamToAnotherSlave_TellsThatTargetToo()
    {
        var slave = OffSlave(new FakeClipboardSync());

        await slave.SimulateReceive(Master, MessageKind.FileStreamRequest, new FileStreamRequestMessage(["/home/me/a.txt"], "other").ToSaneJson());

        Assert.That(slave.Sent.TargetsOf(MessageKind.FileTransferAbort), Is.EquivalentTo([Master, "other"]));
    }

    // -- helpers --

    private static readonly IHydraProfile SharingOff = TransitionTestHelper.ProfileWith(clipboardSync: ClipboardSyncMode.Off);

    private TestableSlaveRelay OffSlave(IClipboardSync clipboard, IOsdNotification? osd = null) =>
        _slaves.Add(new TestableSlaveRelay(clipboard: clipboard, osd: osd ?? new RecordingOsd(), clock: new FakeTimeProvider(),
            clipboardSync: ClipboardSyncMode.Off));

    // an OSD shows where the cursor is: on the remote through the relay, else on the master
    private static async Task<string> Shown(bool onTheRemote, RecordingOsd osd, FakeRelay relay) =>
        onTheRemote ? relay.Sent.Of<OsdMessage>(MessageKind.Osd, "remote").Single().Text : await osd.Shown.Next("the OSD");

    private static Task Press(FakePlatform platform, char letter) => Task.Run(() => platform.PressHotkey(letter)).WaitAsync(Bound);

    private static bool IsFileKind(MessageKind kind) =>
        kind is MessageKind.FileSelectionQuery or MessageKind.FileStreamRequest || FileTransferService.IsFileTransferMessage(kind);
}
