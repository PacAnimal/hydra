using Hydra.Config;
using Hydra.FileTransfer;
using Hydra.Keyboard;
using Hydra.Platform;
using Hydra.Relay;
using Hydra.Screen;
using Tests.Setup;

namespace Tests.Screen;

/// <summary>The copy hotkey asks the local file manager, which may take seconds; routing carries on meanwhile.</summary>
[TestFixture]
public class CopyFilesHotkeyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private const KeyModifiers HotkeyModifiers = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Super;

    private readonly StartedServices _services = new();

    [TearDown]
    public Task TearDown() => _services.StopAll();

    [Test]
    public async Task SlowLocalSelection_DoesNotStallTheRouter()
    {
        var finder = new GatedFileSelectionDetector();
        var osd = new RecordingOsd();
        var (platform, relay, _) = await _services.Start(TransitionTestHelper.CreateService(selectionDetector: finder, osd: osd));
        await TransitionTestHelper.BringRemoteOnline(relay);
        try
        {
            // each Fire waits for the router to process it, so a stalled router fails the wait
            await Task.Run(() => platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'c', HotkeyModifiers))).WaitAsync(Timeout);
            await finder.Queried.WaitAsync(Timeout);
            await Task.Run(() => platform.FireMouseMove(100, 100)).WaitAsync(Timeout);
        }
        finally
        {
            finder.Release(new FileSelectionResult(true, ["/Users/me/a.txt", "/Users/me/b.txt"]));
        }

        Assert.That(await osd.Shown.Next((int)Timeout.TotalMilliseconds, "the copy OSD"), Is.EqualTo("2 items copied"));
    }

    [Test]
    public async Task PasteWhileLocalCopyIsPending_ShowsCopyInProgressAndDoesNothingElse()
    {
        var finder = new GatedFileSelectionDetector();
        var osd = new RecordingOsd();
        var fileTransfer = FileTransferService.Null();
        fileTransfer.SetCopyBuffer("remote", ["/home/me/old.txt"]);
        var (platform, relay, _) = await _services.Start(TransitionTestHelper.CreateService(selectionDetector: finder, osd: osd, fileTransfer: fileTransfer));
        await TransitionTestHelper.BringRemoteOnline(relay);
        try
        {
            await Press(platform, 'c');
            await finder.Queried.WaitAsync(Timeout);
            var sentBefore = relay.Snapshot().Count;

            await Press(platform, 'v');

            using (Assert.EnterMultipleScope())
            {
                Assert.That(await osd.Shown.Next((int)Timeout.TotalMilliseconds, "the paste OSD"), Is.EqualTo("Copy in progress"));
                Assert.That(relay.Snapshot().Skip(sentBefore).Select(s => s.Kind), Is.Empty, "a paste refused for a pending copy must send nothing");
            }
        }
        finally
        {
            finder.Release(new FileSelectionResult(true, ["/Users/me/a.txt"]));
        }

        Assert.That(await osd.Shown.Next((int)Timeout.TotalMilliseconds, "the copy OSD"), Is.EqualTo("1 item copied"));
    }

    [Test]
    public async Task DoubleCopy_FirstQueryAnsweringLast_KeepsTheSecondResult()
    {
        var finder = new GatedFileSelectionDetector();
        var osd = new RecordingOsd();
        var fileTransfer = FileTransferService.Null();
        var (platform, relay, service) = await _services.Start(TransitionTestHelper.CreateService(selectionDetector: finder, osd: osd, fileTransfer: fileTransfer));
        await TransitionTestHelper.BringRemoteOnline(relay);
        try
        {
            await Press(platform, 'c');
            var first = service.LocalSelectionQuery;
            await finder.QueriedAt(0).WaitAsync(Timeout);
            await Press(platform, 'c');
            var second = service.LocalSelectionQuery;
            await finder.QueriedAt(1).WaitAsync(Timeout);

            finder.Release(1, new FileSelectionResult(true, ["/Users/me/new.txt"]));
            await second.WaitAsync(Timeout);
            await service.FlushAsync();
            finder.Release(0, new FileSelectionResult(true, ["/Users/me/old-a.txt", "/Users/me/old-b.txt"]));
            await first.WaitAsync(Timeout);
            await service.FlushAsync();
        }
        finally
        {
            finder.Release(0, new FileSelectionResult(true, []));
            finder.Release(1, new FileSelectionResult(true, []));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fileTransfer.GetCopyBuffer()?.Paths, Is.EqualTo(["/Users/me/new.txt"]));
            Assert.That(await osd.Shown.Next((int)Timeout.TotalMilliseconds, "the copy OSD"), Is.EqualTo("1 item copied"));
            Assert.That(osd.Shown.Count, Is.Zero, "the superseded query must not show an OSD");
        }
    }

    [Test]
    public async Task DoubleCopy_SupersededQueryAnswering_LeavesTheCopyPending()
    {
        var finder = new GatedFileSelectionDetector();
        var osd = new RecordingOsd();
        var (platform, relay, service) = await _services.Start(TransitionTestHelper.CreateService(selectionDetector: finder, osd: osd));
        await TransitionTestHelper.BringRemoteOnline(relay);
        try
        {
            await Press(platform, 'c');
            var first = service.LocalSelectionQuery;
            await finder.QueriedAt(0).WaitAsync(Timeout);
            await Press(platform, 'c');
            await finder.QueriedAt(1).WaitAsync(Timeout);

            finder.Release(0, new FileSelectionResult(true, ["/Users/me/old.txt"]));
            await first.WaitAsync(Timeout);
            await service.FlushAsync();
            await Press(platform, 'v');

            Assert.That(await osd.Shown.Next((int)Timeout.TotalMilliseconds, "the paste OSD"), Is.EqualTo("Copy in progress"));
        }
        finally
        {
            finder.Release(1, new FileSelectionResult(true, []));
        }
    }

    [Test]
    public async Task FailedLocalSelectionQuery_ReportsAndClearsThePendingCopy()
    {
        var osd = new RecordingOsd();
        var (platform, relay, service) = await _services.Start(TransitionTestHelper.CreateService(selectionDetector: new ThrowingFileSelectionDetector(), osd: osd));
        await TransitionTestHelper.BringRemoteOnline(relay);

        await Press(platform, 'c');
        await service.LocalSelectionQuery.WaitAsync(Timeout);
        await service.FlushAsync();
        await Press(platform, 'v');

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await osd.Shown.Next((int)Timeout.TotalMilliseconds, "the copy OSD"), Is.EqualTo("Copy failed"));
            Assert.That(await osd.Shown.Next((int)Timeout.TotalMilliseconds, "the paste OSD"), Is.EqualTo("Nothing to paste"));
        }
    }

    [Test]
    public async Task LocalSelectionQueryThatCouldNotAskTheFileManager_ReportsCopyFailed()
    {
        var finder = new GatedFileSelectionDetector();
        var osd = new RecordingOsd();
        var (platform, relay, service) = await _services.Start(TransitionTestHelper.CreateService(selectionDetector: finder, osd: osd));
        await TransitionTestHelper.BringRemoteOnline(relay);

        await Press(platform, 'c');
        finder.Release(FileSelectionResult.Failure);
        await service.LocalSelectionQuery.WaitAsync(Timeout);
        await service.FlushAsync();

        Assert.That(await osd.Shown.Next((int)Timeout.TotalMilliseconds, "the copy OSD"), Is.EqualTo("Copy failed"));
    }

    [Test]
    public async Task LocalAnswerArrivingAfterARemoteCopy_LeavesTheRemoteFilesInTheBuffer()
    {
        var finder = new GatedFileSelectionDetector();
        var fileTransfer = FileTransferService.Null();
        var (platform, relay, service) = await _services.Start(TransitionTestHelper.CreateService(selectionDetector: finder, fileTransfer: fileTransfer));
        await TransitionTestHelper.BringRemoteOnline(relay);
        try
        {
            await Press(platform, 'c');
            var local = service.LocalSelectionQuery;
            await finder.Queried.WaitAsync(Timeout);

            platform.FireMouseMove(2559, 720);
            Assert.That(platform.IsOnVirtualScreen, Is.True, "pre-condition: on the remote screen");
            await Press(platform, 'c');
            await relay.FireMessageReceived("remote", MessageKind.FileSelectionResponse, """{"paths":["/home/r/remote.txt"]}""");

            finder.Release(new FileSelectionResult(true, ["/Users/me/local.txt"]));
            await local.WaitAsync(Timeout);
            await service.FlushAsync();
        }
        finally
        {
            finder.Release(new FileSelectionResult(true, []));
        }

        Assert.That(fileTransfer.GetCopyBuffer()?.Paths, Is.EqualTo(["/home/r/remote.txt"]));
    }

    [Test]
    public async Task RemoteAnswerArrivingAfterALocalCopy_LeavesTheLocalFilesInTheBuffer()
    {
        var finder = new GatedFileSelectionDetector();
        var fileTransfer = FileTransferService.Null();
        var (platform, relay, service) = await _services.Start(TransitionTestHelper.CreateService(selectionDetector: finder, fileTransfer: fileTransfer));
        await TransitionTestHelper.BringRemoteOnline(relay);
        finder.Release(new FileSelectionResult(true, ["/Users/me/local.txt"]));

        platform.FireMouseMove(2559, 720);
        Assert.That(platform.IsOnVirtualScreen, Is.True, "pre-condition: on the remote screen");
        await Press(platform, 'c');
        for (var i = 0; i < 40 && platform.IsOnVirtualScreen; i++)
            platform.FireMouseDelta(-100, 0);
        Assert.That(platform.IsOnVirtualScreen, Is.False, "pre-condition: back home");
        await Press(platform, 'c');
        await service.LocalSelectionQuery.WaitAsync(Timeout);
        await service.FlushAsync();

        await relay.FireMessageReceived("remote", MessageKind.FileSelectionResponse, """{"paths":["/home/r/remote.txt"]}""");

        Assert.That(fileTransfer.GetCopyBuffer()?.Paths, Is.EqualTo(["/Users/me/local.txt"]));
    }

    [Test]
    public async Task PasteWhileRemoteCopyIsUnanswered_ShowsCopyInProgressAndSendsNothing()
    {
        var fileTransfer = FileTransferService.Null();
        fileTransfer.SetCopyBuffer("elsewhere", ["/x/old.txt"]);
        var (platform, relay, _) = await StartForRemoteCopy(fileTransfer: fileTransfer);
        await TransitionTestHelper.BringRemoteOnline(relay);
        EnterRemote(platform);
        await Press(platform, 'c');
        var sentBefore = relay.Snapshot().Count;

        await Press(platform, 'v');

        Assert.That(SentSince(relay, sentBefore), Is.EqualTo([Osd("remote", "Copy in progress")]), "a paste refused for a pending copy must send nothing else");
    }

    [Test]
    public async Task RemotePressAfterAPendingLocalCopy_PasteOnAnotherSlave_ShowsCopyInProgress()
    {
        var finder = new GatedFileSelectionDetector();
        var fileTransfer = FileTransferService.Null();
        fileTransfer.SetCopyBuffer("elsewhere", ["/x/old.txt"]);
        var (platform, relay, _) = await _services.Start(TransitionTestHelper.CreateService(
            profile: TransitionTestHelper.ChainConfig, selectionDetector: finder, fileTransfer: fileTransfer));
        await TransitionTestHelper.BringHostsOnline(relay, ["remote", "remote2"]);
        try
        {
            await Press(platform, 'c');
            await finder.Queried.WaitAsync(Timeout);
            EnterRemote(platform);
            await Press(platform, 'c');
            MoveRightTo(platform, relay, "remote2");
            var sentBefore = relay.Snapshot().Count;

            await Press(platform, 'v');

            Assert.That(SentSince(relay, sentBefore), Is.EqualTo([Osd("remote2", "Copy in progress")]));
        }
        finally
        {
            finder.Release(new FileSelectionResult(true, []));
        }
    }

    [Test]
    public async Task RemoteCopyAnswered_ClearsThePendingCopy()
    {
        var (platform, relay, _) = await StartForRemoteCopy();
        await TransitionTestHelper.BringRemoteOnline(relay);
        EnterRemote(platform);
        await Press(platform, 'c');
        await relay.FireMessageReceived("remote", MessageKind.FileSelectionResponse, """{"paths":[]}""");
        var sentBefore = relay.Snapshot().Count;

        await Press(platform, 'v');

        Assert.That(SentSince(relay, sentBefore), Is.EqualTo([Osd("remote", "Nothing to paste")]));
    }

    [Test]
    public async Task RemoteCopyRefusedAsBusy_ClearsThePendingCopy()
    {
        var (platform, relay, service) = await StartForRemoteCopy();
        await TransitionTestHelper.BringRemoteOnline(relay);
        EnterRemote(platform);
        await Press(platform, 'c');
        await relay.FireMessageReceived("remote", MessageKind.FileTransferBusy, "{}");
        await service.FlushAsync();
        var sentBefore = relay.Snapshot().Count;

        await Press(platform, 'v');

        Assert.That(SentSince(relay, sentBefore), Is.EqualTo([Osd("remote", "Nothing to paste")]));
    }

    [Test]
    public async Task RemoteCopyHostLeaving_ClearsThePendingCopy()
    {
        var osd = new RecordingOsd();
        var (platform, relay, _) = await StartForRemoteCopy(osd: osd);
        await TransitionTestHelper.BringRemoteOnline(relay);
        EnterRemote(platform);
        await Press(platform, 'c');
        await relay.FirePeersChanged();
        Assert.That(platform.IsOnVirtualScreen, Is.False, "pre-condition: back home");

        await Press(platform, 'v');

        Assert.That(await osd.Shown.Next((int)Timeout.TotalMilliseconds, "the paste OSD"), Is.EqualTo("Nothing to paste"));
    }

    [Test]
    public async Task RelayDisconnecting_ClearsThePendingRemoteCopy()
    {
        var osd = new RecordingOsd();
        var (platform, relay, _) = await StartForRemoteCopy(osd: osd);
        await TransitionTestHelper.BringRemoteOnline(relay);
        EnterRemote(platform);
        await Press(platform, 'c');
        await relay.FireDisconnected();
        Assert.That(platform.IsOnVirtualScreen, Is.False, "pre-condition: back home");

        await Press(platform, 'v');

        Assert.That(await osd.Shown.Next((int)Timeout.TotalMilliseconds, "the paste OSD"), Is.EqualTo("Nothing to paste"));
    }

    [Test]
    public async Task OtherHostBusy_LeavesTheRemoteCopyPending()
    {
        var (platform, relay, service) = await StartForRemoteCopy(profile: TransitionTestHelper.ChainConfig);
        await TransitionTestHelper.BringHostsOnline(relay, ["remote", "remote2"]);
        EnterRemote(platform);
        await Press(platform, 'c');
        await relay.FireMessageReceived("remote2", MessageKind.FileTransferBusy, "{}");
        await service.FlushAsync();
        var sentBefore = relay.Snapshot().Count;

        await Press(platform, 'v');

        Assert.That(SentSince(relay, sentBefore), Is.EqualTo([Osd("remote", "Copy in progress")]));
    }

    [Test]
    public async Task TwoAnswersFromTheSameHostForOnePress_KeepsTheFirst()
    {
        var fileTransfer = FileTransferService.Null();
        var (platform, relay, _) = await StartForRemoteCopy(fileTransfer: fileTransfer);
        await TransitionTestHelper.BringRemoteOnline(relay);
        EnterRemote(platform);
        await Press(platform, 'c');

        await relay.FireMessageReceived("remote", MessageKind.FileSelectionResponse, """{"paths":["/home/r/first.txt"]}""");
        await relay.FireMessageReceived("remote", MessageKind.FileSelectionResponse, """{"paths":["/home/r/second.txt"]}""");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fileTransfer.GetCopyBuffer()?.Paths, Is.EqualTo(["/home/r/first.txt"]));
            Assert.That(relay.Snapshot().Count(s => s.Kind == MessageKind.Osd), Is.EqualTo(1), "the second answer must not show an OSD");
        }
    }

    [Test]
    public async Task DoublePressAgainstASlowSlave_PastesTheSelection()
    {
        var finder = new GatedFileSelectionDetector();
        var slave = new TestableSlaveRelay(selectionDetector: finder);
        await slave.SimulateConnected();
        await slave.SimulateMasterConfig("home");
        var fileTransfer = FileTransferService.Null();
        fileTransfer.SetCopyBuffer("elsewhere", ["/x/old.txt"]);
        var (platform, relay, service) = await StartForRemoteCopy(profile: TransitionTestHelper.ChainConfig, fileTransfer: fileTransfer);
        await TransitionTestHelper.BringHostsOnline(relay, ["remote", "remote2"]);
        EnterRemote(platform);
        try
        {
            await Press(platform, 'c');
            await Press(platform, 'c');
            foreach (var (_, _, json) in relay.Snapshot().Where(s => s.Kind == MessageKind.FileSelectionQuery).ToList())
                await slave.SimulateReceive("home", MessageKind.FileSelectionQuery, json).WaitAsync(Timeout);
            await finder.Queried.WaitAsync(Timeout);
            await ForwardSelectionAnswers(slave, relay, service);
        }
        finally
        {
            finder.Release(0, new FileSelectionResult(true, ["/home/r/report.pdf"]));
            finder.Release(1, new FileSelectionResult(true, []));
        }
        await slave.WaitForSent(MessageKind.FileSelectionResponse, Timeout);
        await ForwardSelectionAnswers(slave, relay, service);
        MoveRightTo(platform, relay, "remote2");
        var sentBefore = relay.Snapshot().Count;

        await Press(platform, 'v');

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fileTransfer.GetCopyBuffer()?.Paths, Is.EqualTo(["/home/r/report.pdf"]));
            Assert.That(relay.Snapshot().Skip(sentBefore).Single(s => s.Kind == MessageKind.FileTransferRequest).Json, Does.Contain("\"remote\""));
        }
    }

    [Test]
    public async Task RemoteCopyOnOneHostThenAnother_DropsTheFirstHostsLateAnswer()
    {
        var fileTransfer = FileTransferService.Null();
        var (platform, relay, _) = await StartForRemoteCopy(profile: TransitionTestHelper.ChainConfig, fileTransfer: fileTransfer);
        await TransitionTestHelper.BringHostsOnline(relay, ["remote", "remote2"]);
        EnterRemote(platform);
        await Press(platform, 'c');
        MoveRightTo(platform, relay, "remote2");
        await Press(platform, 'c');

        await relay.FireMessageReceived("remote", MessageKind.FileSelectionResponse, """{"paths":["/home/r/stale.txt"]}""");
        Assert.That(fileTransfer.GetCopyBuffer(), Is.Null, "the first host's answer was taken for the second press");
        await relay.FireMessageReceived("remote2", MessageKind.FileSelectionResponse, """{"paths":["/home/r2/fresh.txt"]}""");

        Assert.That(fileTransfer.GetCopyBuffer()?.Paths, Is.EqualTo(["/home/r2/fresh.txt"]));
    }

    [Test]
    public async Task SecondRemotePressToTheSameHost_TakesOneAnswer()
    {
        var fileTransfer = FileTransferService.Null();
        var (platform, relay, _) = await StartForRemoteCopy(fileTransfer: fileTransfer);
        await TransitionTestHelper.BringRemoteOnline(relay);
        EnterRemote(platform);
        await Press(platform, 'c');
        await Press(platform, 'c');

        await relay.FireMessageReceived("remote", MessageKind.FileSelectionResponse, """{"paths":["/home/r/a.txt"]}""");
        await relay.FireMessageReceived("remote", MessageKind.FileSelectionResponse, """{"paths":["/home/r/b.txt"]}""");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.Snapshot().Count(s => s.Kind == MessageKind.FileSelectionQuery), Is.EqualTo(2));
            Assert.That(fileTransfer.GetCopyBuffer()?.Paths, Is.EqualTo(["/home/r/a.txt"]));
        }
    }

    [Test]
    public async Task RemoteCopyAnswer_IsShownWhereTheCursorIsNow()
    {
        var osd = new RecordingOsd();
        var (platform, relay, _) = await StartForRemoteCopy(osd: osd);
        await TransitionTestHelper.BringRemoteOnline(relay);
        EnterRemote(platform);
        await Press(platform, 'c');
        ReturnHome(platform);
        var sentBefore = relay.Snapshot().Count;

        await relay.FireMessageReceived("remote", MessageKind.FileSelectionResponse, """{"paths":["/home/r/a.txt"]}""");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await osd.Shown.Next((int)Timeout.TotalMilliseconds, "the copy OSD"), Is.EqualTo("1 item copied"));
            Assert.That(relay.Snapshot().Skip(sentBefore).Where(s => s.Kind == MessageKind.Osd), Is.Empty, "the OSD went to the host the cursor left");
        }
    }

    [Test]
    public async Task MalformedRemoteAnswer_ReportsCopyFailedAndClearsThePendingCopy()
    {
        var fileTransfer = FileTransferService.Null();
        fileTransfer.SetCopyBuffer("elsewhere", ["/x/old.txt"]);
        var (platform, relay, _) = await StartForRemoteCopy(fileTransfer: fileTransfer);
        await TransitionTestHelper.BringRemoteOnline(relay);
        EnterRemote(platform);
        await Press(platform, 'c');
        var sentBefore = relay.Snapshot().Count;

        await relay.FireMessageReceived("remote", MessageKind.FileSelectionResponse, """{"paths":42}""");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SentSince(relay, sentBefore), Is.EqualTo([Osd("remote", "Copy failed")]));
            Assert.That(fileTransfer.GetCopyBuffer()?.Paths, Is.EqualTo(["/x/old.txt"]), "an unreadable answer must not touch the buffer");
        }
    }

    [Test]
    public async Task Stop_CancelsTheLocalQueryRatherThanWaitingForIt()
    {
        var finder = new GatedFileSelectionDetector();
        var (platform, relay, service) = await _services.Start(TransitionTestHelper.CreateService(selectionDetector: finder));
        await TransitionTestHelper.BringRemoteOnline(relay);
        try
        {
            await Press(platform, 'c');
            await finder.Queried.WaitAsync(Timeout);

            await _services.Stop(service).WaitAsync(Timeout);

            await finder.Cancelled.WaitAsync(Timeout);
            await service.LocalSelectionQuery.WaitAsync(Timeout);
        }
        finally
        {
            finder.Release(new FileSelectionResult(true, []));
        }
    }

    [Test]
    public async Task CopyPressQueuedDuringStop_StartsNoQuery()
    {
        var finder = new GatedFileSelectionDetector();
        var tracker = new HeldActivityTracker();
        var (platform, relay, service) = await _services.Start(TransitionTestHelper.CreateService(selectionDetector: finder, activityTracker: tracker));
        await TransitionTestHelper.BringRemoteOnline(relay);
        // queue the press without waiting for the router, which is held on it until stopping has begun
        platform.AfterFireCallback = null;
        tracker.Hold();
        try
        {
            platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, 'c', HotkeyModifiers));
            await tracker.Held.WaitAsync(Timeout);
            var stop = _services.Stop(service);
            tracker.Release();
            await stop.WaitAsync(Timeout);

            await service.LocalSelectionQuery.WaitAsync(Timeout);
            Assert.That(finder.Queried.IsCompleted, Is.False, "a press handled after stopping began still asked the file manager");
        }
        finally
        {
            tracker.Release();
            finder.Release(new FileSelectionResult(true, []));
        }
    }

    // a file manager that supports transfer but is never asked, for copies made on a remote screen
    private Task<TestServiceBundle> StartForRemoteCopy(IHydraProfile? profile = null, IOsdNotification? osd = null, FileTransferService? fileTransfer = null) =>
        _services.Start(TransitionTestHelper.CreateService(profile: profile, selectionDetector: new GatedFileSelectionDetector(), osd: osd, fileTransfer: fileTransfer));

    private static void EnterRemote(FakePlatform platform)
    {
        platform.FireMouseMove(2559, 720);
        Assert.That(platform.IsOnVirtualScreen, Is.True, "pre-condition: on the remote screen");
    }

    private static void ReturnHome(FakePlatform platform)
    {
        for (var i = 0; i < 40 && platform.IsOnVirtualScreen; i++)
            platform.FireMouseDelta(-100, 0);
        Assert.That(platform.IsOnVirtualScreen, Is.False, "pre-condition: back home");
    }

    private static void MoveRightTo(FakePlatform platform, FakeRelay relay, string host)
    {
        bool Entered() => relay.Snapshot().Any(s => s.Kind == MessageKind.EnterScreen && s.Targets.Contains(host));
        for (var i = 0; i < 40 && !Entered(); i++)
            platform.FireMouseDelta(100, 0);
        Assert.That(Entered(), Is.True, $"pre-condition: on {host}");
    }

    // hands the master whatever "remote" has answered so far, in order
    private static async Task ForwardSelectionAnswers(TestableSlaveRelay slave, FakeRelay relay, InputRouter service)
    {
        var answers = slave.TakeAll().Where(s => s.Kind == MessageKind.FileSelectionResponse).ToList();
        foreach (var (_, kind, json) in answers)
            await relay.FireMessageReceived("remote", kind, json);
        await service.FlushAsync();
    }

    // what was sent from the given index on, one line per message
    private static string[] SentSince(FakeRelay relay, int index) =>
        [.. relay.Snapshot().Skip(index).Select(s => $"{s.Kind} to {string.Join(",", s.Targets)}: {s.Json}")];

    private static string Osd(string host, string text) =>
        $"{MessageKind.Osd} to {host}: {MessageSerializer.Decode(MessageSerializer.Encode(MessageKind.Osd, new OsdMessage(text))).Json}";

    // each Fire waits for the router to process it
    private static Task Press(FakePlatform platform, char key) => Task.Run(() =>
    {
        platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyDown, key, HotkeyModifiers));
        platform.FireKeyEvent(KeyEvent.Char(KeyEventType.KeyUp, key, HotkeyModifiers));
    }).WaitAsync(Timeout);
}

// holds the router on the first input after Hold, until released
file sealed class HeldActivityTracker : IActivityTracker
{
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _holding;

    public Task Held => _held.Task;
    public long MsSinceLocalActivity => 0;

    public void Hold() => _holding = true;
    public void Release() => _released.TrySetResult();

    public async ValueTask LocalActivity()
    {
        if (!_holding) return;
        _held.TrySetResult();
        await _released.Task;
    }

    public async ValueTask RemoteActivity(string sourcePeer) => await ValueTask.CompletedTask;
    public async ValueTask IncomingPing() => await ValueTask.CompletedTask;
}

file sealed class ThrowingFileSelectionDetector : IFileSelectionDetector
{
    public string FileManagerName => "Finder";
    public bool IsFileTransferSupported => true;
    public FileSelectionResult GetSelectedPaths(CancellationToken cancel) => throw new InvalidOperationException("Finder went away");
}
