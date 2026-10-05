using Cathedral.Extensions;
using Hydra.FileTransfer;
using Hydra.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.FileTransfer;

[TestFixture]
public class FileTransferServiceTests
{
    private static readonly Action<string> NoFileStart = _ => { };
    private FakeFileTransferDialog _dialog = null!;
    private FakeRelay _relay = null!;
    private FileTransferService _service = null!;
    private string _tempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _dialog = new FakeFileTransferDialog();
        _relay = new FakeRelay();
        _tempRoot = TestPaths.FreshFixtureRoot(nameof(FileTransferServiceTests));
        _service = new FileTransferService(_dialog, new FakeDropTargetResolver(_tempRoot), NullLogger<FileTransferService>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _service.Dispose();
    }

    // -- helpers --

    private string CreateTempFile(string name = "a.txt", string content = "hi")
    {
        var path = Path.Combine(_tempRoot, name);
        File.WriteAllText(path, content);
        return path;
    }

    // completes once the dialog is given the transfer's outcome, faulting if the slot was still held by then.
    // Arm it before starting the transfer: the outcome may be reported on another thread at any moment.
    private static Task WhenTransferSettles(FileTransferService service, FakeFileTransferDialog dialog)
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.OnOutcome = () =>
        {
            if (service.FileTransferOngoing) settled.TrySetException(new InvalidOperationException("the outcome was reported before the slot was released"));
            else settled.TrySetResult();
        };
        return settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // creates a service with an aggressive 100ms watchdog/accept timeout for timeout tests
    private FileTransferService FastTimeoutService() =>
        new(_dialog, new FakeDropTargetResolver(_tempRoot), NullLogger<FileTransferService>.Instance, watchdogTimeoutMs: 100);

    // routes a message to _service as if it came from the given host
    private Task Simulate(string host, MessageKind kind, object msg) => Simulate(_service, host, kind, msg, _relay);

    // routes a message to _service from the default "master" host
    private Task Simulate(MessageKind kind, object msg) => Simulate(_service, "master", kind, msg, _relay);

    private static async Task Simulate(FileTransferService svc, string host, MessageKind kind, object msg, FakeRelay relay)
    {
        var decoded = MessageSerializer.Decode(MessageSerializer.Encode(kind, msg));
        await svc.OnMessageAsync(host, decoded.Kind, decoded.Bytes, relay);
    }

    // routes a body that is not valid JSON, as a broken or hostile peer might send
    private Task<bool> SimulateMalformed(MessageKind kind) => _service.OnMessageAsync("master", kind, "{"u8.ToArray(), _relay);

    // computes chunks + sha for a file without sending them anywhere
    private static async Task<(List<(byte[] data, int seq)> chunks, byte[] sha, long totalSent)> ComputeChunks(string path)
    {
        var chunks = new List<(byte[] data, int seq)>();
        var sha = await TarGzStreamer.StreamAsync([path],
            (data, seq, _) => { chunks.Add((data, seq)); return ValueTask.CompletedTask; },
            NoFileStart, CancellationToken.None);
        return (chunks, sha, chunks.Sum(c => (long)c.data.Length));
    }

    // drives a complete receive protocol (request → start → chunks → done) against a given service
    private static async Task SimulateTransfer(
        FileTransferService svc, FakeRelay relay, string sourceHost, string fileName,
        List<(byte[] data, int seq)> chunks, long totalSent, byte[] sha)
    {
        await Simulate(svc, sourceHost, MessageKind.FileTransferRequest, new FileTransferRequestMessage(), relay);
        await Simulate(svc, sourceHost, MessageKind.FileTransferStart, new FileTransferStartMessage([fileName], totalSent), relay);
        foreach (var (data, seq) in chunks)
            await Simulate(svc, sourceHost, MessageKind.FileTransferChunk, new FileTransferChunkMessage(seq, data), relay);
        await Simulate(svc, sourceHost, MessageKind.FileTransferDone, new FileTransferDoneMessage(totalSent, sha), relay);
    }

    // -- FileTransferRequest (receiver negotiation) --

    [Test]
    public async Task OnMessage_FileTransferRequest_SendsAcceptedBackToSender()
    {
        await Simulate(MessageKind.FileTransferRequest, new FileTransferRequestMessage());

        var accepted = _relay.Snapshot().FirstOrDefault(m => m.Kind == MessageKind.FileTransferAccepted);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accepted, Is.Not.Null);
            Assert.That(accepted?.Targets, Contains.Item("master"));
        }
    }

    [Test]
    public async Task OnMessage_FileTransferRequest_NoPasteDir_SendsAbortWithReasonNoFolder()
    {
        using var service = new FileTransferService(_dialog, new NullDropTargetResolver(), NullLogger<FileTransferService>.Instance);

        await Simulate(service, "master", MessageKind.FileTransferRequest, new FileTransferRequestMessage(), _relay);

        var abort = _relay.Snapshot().FirstOrDefault(m => m.Kind == MessageKind.FileTransferAbort);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(abort, Is.Not.Null);
            Assert.That(abort?.Json, Does.Contain(FileTransferService.ReasonNoFolder));
            Assert.That(service.FileTransferOngoing, Is.False);
        }
    }

    // when master sets SourceHost, only that host's data is accepted (relay sender is not the source)
    [Test]
    public async Task OnMessage_FileTransferRequest_SourceHostOverridesExpectedDataSender()
    {
        // master sends FileTransferRequest declaring data will come from "real-sender", not "master"
        await Simulate(MessageKind.FileTransferRequest, new FileTransferRequestMessage(SourceHost: "real-sender"));

        // done from "master" (relay message sender) should be ignored — wrong data source
        await Simulate(MessageKind.FileTransferDone, new FileTransferDoneMessage(0, new byte[32]));

        // receiver still active — done from master was dropped
        Assert.That(_service.FileTransferOngoing, Is.True);
    }

    // -- FileTransferStart (receiver side — carries names/total from data source) --

    [Test]
    public async Task OnMessage_FileTransferStart_AfterRequest_ShowsTransferringDialog()
    {
        await Simulate(MessageKind.FileTransferRequest, new FileTransferRequestMessage());
        await Simulate(MessageKind.FileTransferStart, new FileTransferStartMessage(["a.txt"], 100));
        Assert.That(_dialog.LastState, Is.EqualTo("transferring"));
    }

    // -- FileTransferChunk (receiver side) --

    [Test]
    public async Task OnMessage_FileTransferChunk_FromWrongHost_IsDropped()
    {
        // sets up receiver expecting chunks from "master"
        await Simulate(MessageKind.FileTransferRequest, new FileTransferRequestMessage());

        // chunk and done from a different host — both dropped
        await Simulate("wrong-host", MessageKind.FileTransferChunk, new FileTransferChunkMessage(0, [1, 2, 3]));
        await Simulate("wrong-host", MessageKind.FileTransferDone, new FileTransferDoneMessage(0, new byte[32]));

        // receiver still active — wrong-host messages did not finalize or disrupt it
        Assert.That(_service.FileTransferOngoing, Is.True);
    }

    [Test]
    public async Task OnMessage_MalformedFileTransferDone_AbortsTheSourceAndShowsError()
    {
        await Simulate(MessageKind.FileTransferRequest, new FileTransferRequestMessage());
        await Simulate(MessageKind.FileTransferStart, new FileTransferStartMessage(["a.txt"], 100));
        _relay.ClearSent();

        await SimulateMalformed(MessageKind.FileTransferDone);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_dialog.LastState, Is.EqualTo("error"));
            Assert.That(_service.FileTransferOngoing, Is.False);
            Assert.That(_relay.Snapshot().Select(m => m.Kind), Is.EqualTo([MessageKind.FileTransferAbort]));
            Assert.That(_relay.Snapshot()[0].Targets, Is.EqualTo(["master"]));
        }
    }

    // -- FileTransferAbort (inbound) --

    [Test]
    public async Task OnMessage_MalformedFileTransferAbort_DuringReceive_ShowsAReason()
    {
        await Simulate(MessageKind.FileTransferRequest, new FileTransferRequestMessage());

        await SimulateMalformed(MessageKind.FileTransferAbort);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_dialog.LastState, Is.EqualTo("error"));
            Assert.That(_dialog.LastError, Is.EqualTo($"Transfer aborted: {FileTransferService.ReasonUnknown}"));
        }
    }

    [Test]
    public async Task OnMessage_FileTransferAbort_DuringReceive_ShowsError()
    {
        await Simulate(MessageKind.FileTransferRequest, new FileTransferRequestMessage());
        await Simulate(MessageKind.FileTransferAbort, new FileTransferAbortMessage("disk full"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_dialog.LastState, Is.EqualTo("error"));
            Assert.That(_dialog.LastError, Does.Contain("disk full"));
        }
    }

    [Test]
    public async Task OnMessage_FileTransferAbort_DuringSend_ClosesDialog()
    {
        _service.InitiateSend([CreateTempFile()], "slave", _relay);
        _relay.ClearSent();

        // receiver ("slave") aborts — we were the sender, so we close without sending our own abort
        await Simulate("slave", MessageKind.FileTransferAbort, new FileTransferAbortMessage("disk full"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_dialog.LastState, Is.EqualTo("closed"));
            Assert.That(_service.FileTransferOngoing, Is.False);
            Assert.That(_relay.Snapshot().Any(m => m.Kind == MessageKind.FileTransferAbort), Is.False);
        }
    }

    [Test]
    public async Task OnMessage_FileTransferAbort_FromUnknownHost_IsIgnored()
    {
        await Simulate(MessageKind.FileTransferRequest, new FileTransferRequestMessage());
        var stateBefore = _dialog.LastState;

        await Simulate("unknown-host", MessageKind.FileTransferAbort, new FileTransferAbortMessage("whatever"));

        Assert.That(_dialog.LastState, Is.EqualTo(stateBefore));
    }

    // -- user-requested cancel --

    [Test]
    public void CancelRequested_DuringSend_SendsAbortAndClosesDialog()
    {
        _service.InitiateSend([CreateTempFile()], "slave", _relay);
        _relay.ClearSent();

        _dialog.TriggerCancel();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_relay.Snapshot().Any(m => m.Kind == MessageKind.FileTransferAbort && m.Targets.Contains("slave")), Is.True);
            Assert.That(_dialog.LastState, Is.EqualTo("closed"));
        }
    }

    [Test]
    public async Task CancelRequested_DuringReceive_SendsAbortAndClosesDialog()
    {
        await Simulate(MessageKind.FileTransferRequest, new FileTransferRequestMessage());
        _relay.ClearSent();

        _dialog.TriggerCancel();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_relay.Snapshot().Any(m => m.Kind == MessageKind.FileTransferAbort && m.Targets.Contains("master")), Is.True);
            Assert.That(_dialog.LastState, Is.EqualTo("closed"));
            Assert.That(_service.FileTransferOngoing, Is.False);
        }
    }

    // -- programmatic abort --

    [Test]
    public void Abort_DuringSend_SendsAbortAndClosesDialog()
    {
        _service.InitiateSend([CreateTempFile()], "slave", _relay);
        _relay.ClearSent();

        _service.Abort(_relay, "peer disconnected");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_relay.Snapshot().Any(m => m.Kind == MessageKind.FileTransferAbort), Is.True);
            Assert.That(_dialog.LastState, Is.EqualTo("closed"));
            Assert.That(_service.FileTransferOngoing, Is.False);
        }
    }

    [Test]
    public async Task Abort_DuringReceive_SendsAbortAndClosesDialog()
    {
        await Simulate(MessageKind.FileTransferRequest, new FileTransferRequestMessage());
        _relay.ClearSent();

        _service.Abort(_relay, "peer disconnected");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_relay.Snapshot().Any(m => m.Kind == MessageKind.FileTransferAbort), Is.True);
            Assert.That(_dialog.LastState, Is.EqualTo("closed"));
            Assert.That(_service.FileTransferOngoing, Is.False);
        }
    }

    [Test]
    public void Abort_DuringCoordinating_SendsAbortToTargetAndClearsState()
    {
        var copyBuffer = new FileTransferService.FileCopyState("source-slave", ["/remote/file.txt"]);
        _service.InitiatePaste(copyBuffer, "target-slave", "local-host", _relay);
        _relay.ClearSent();

        _service.Abort(_relay, "peer disconnected");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_relay.Snapshot().Any(m => m.Kind == MessageKind.FileTransferAbort && m.Targets.Contains("target-slave")), Is.True);
            Assert.That(_service.FileTransferOngoing, Is.False);
        }
    }

    // -- state queries --

    [Test]
    public void FileTransferOngoing_TrueWhileSending()
    {
        _service.InitiateSend([CreateTempFile()], "slave", _relay);
        Assert.That(_service.FileTransferOngoing, Is.True);
    }

    [Test]
    public async Task FileTransferOngoing_TrueWhileReceiving()
    {
        await Simulate(MessageKind.FileTransferRequest, new FileTransferRequestMessage());
        Assert.That(_service.FileTransferOngoing, Is.True);
    }

    [Test]
    public void FileTransferOngoing_TrueWhileCoordinating()
    {
        var copyBuffer = new FileTransferService.FileCopyState("source-slave", ["/remote/file.txt"]);
        _service.InitiatePaste(copyBuffer, "target-slave", "local-host", _relay);
        Assert.That(_service.FileTransferOngoing, Is.True);
    }

    [Test]
    public async Task FileTransferOngoing_FalseAfterAbort()
    {
        await Simulate(MessageKind.FileTransferRequest, new FileTransferRequestMessage());
        _service.Abort(_relay, "test");
        Assert.That(_service.FileTransferOngoing, Is.False);
    }

    [Test]
    public void IsSendingTo_TrueForTargetHost_FalseOtherwise()
    {
        _service.InitiateSend([CreateTempFile()], "slave", _relay);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_service.IsSendingTo("slave"), Is.True);
            Assert.That(_service.IsSendingTo("other"), Is.False);
        }
    }

    [Test]
    public void IsCoordinatingTransferTo_TrueForTargetHost_FalseOtherwise()
    {
        var copyBuffer = new FileTransferService.FileCopyState("source-slave", ["/remote/file.txt"]);
        _service.InitiatePaste(copyBuffer, "target-slave", "local-host", _relay);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_service.IsCoordinatingTransferTo("target-slave"), Is.True);
            Assert.That(_service.IsCoordinatingTransferTo("other"), Is.False);
        }
    }

    // -- InitiatePaste --

    [Test]
    public void InitiatePaste_MasterSource_StartsSend()
    {
        var copyBuffer = new FileTransferService.FileCopyState("local-host", [CreateTempFile()]);
        _service.InitiatePaste(copyBuffer, "slave-host", "local-host", _relay);
        Assert.That(_service.FileTransferOngoing, Is.True);
    }

    [Test]
    public void InitiatePaste_MasterTarget_SetsUpReceiverAndSendsStreamRequest()
    {
        var copyBuffer = new FileTransferService.FileCopyState("source-slave", ["/remote/file.txt"]);

        var result = _service.InitiatePaste(copyBuffer, "local-host", "local-host", _relay);

        var (targets, _, _) = _relay.Snapshot().First(m => m.Kind == MessageKind.FileStreamRequest);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.True);
            Assert.That(targets, Contains.Item("source-slave"));
            Assert.That(_service.FileTransferOngoing, Is.True);
        }
    }

    [Test]
    public void InitiatePaste_MasterTarget_InvalidPasteDir_ReturnsFalse()
    {
        using var service = new FileTransferService(_dialog, new NullDropTargetResolver(), NullLogger<FileTransferService>.Instance);
        var copyBuffer = new FileTransferService.FileCopyState("source-slave", ["/remote/file.txt"]);

        var result = service.InitiatePaste(copyBuffer, "local-host", "local-host", _relay);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.False);
            Assert.That(_relay.Snapshot(), Is.Empty);
        }
    }

    [Test]
    public void InitiatePaste_SlaveToSlave_SendsFileTransferRequestWithSourceHostToTarget()
    {
        var copyBuffer = new FileTransferService.FileCopyState("source-slave", ["/remote/file.txt"]);

        _service.InitiatePaste(copyBuffer, "target-slave", "local-host", _relay);

        var (targets, _, json) = _relay.Snapshot().First(m => m.Kind == MessageKind.FileTransferRequest);
        var msg = json.FromSaneJson<FileTransferRequestMessage>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(targets, Contains.Item("target-slave"));
            Assert.That(msg?.SourceHost, Is.EqualTo("source-slave"));
        }
    }

    [Test]
    public async Task InitiatePaste_SlaveToSlave_OnAccepted_SendsFileStreamRequestToSource()
    {
        var copyBuffer = new FileTransferService.FileCopyState("source-slave", ["/remote/file.txt"]);
        _service.InitiatePaste(copyBuffer, "target-slave", "local-host", _relay);

        await Simulate("target-slave", MessageKind.FileTransferAccepted, new FileTransferAcceptedMessage());

        var (targets, _, _) = _relay.Snapshot().First(m => m.Kind == MessageKind.FileStreamRequest);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(targets, Contains.Item("source-slave"));
            Assert.That(_service.FileTransferOngoing, Is.False);
            Assert.That(_service.IsCoordinatingTransferTo("target-slave"), Is.False);
        }
    }

    [Test]
    public async Task InitiatePaste_SlaveToSlave_OnAbort_ClearsCoordinatorState()
    {
        var copyBuffer = new FileTransferService.FileCopyState("source-slave", ["/remote/file.txt"]);
        _service.InitiatePaste(copyBuffer, "target-slave", "local-host", _relay);

        await Simulate("target-slave", MessageKind.FileTransferAbort, new FileTransferAbortMessage(FileTransferService.ReasonNoFolder));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_service.IsCoordinatingTransferTo("target-slave"), Is.False);
            Assert.That(_service.FileTransferOngoing, Is.False);
        }
    }

    // -- InitiateSend --

    [Test]
    public async Task InitiateSend_IncludesLocalHostAsSourceHostInRequestMessage()
    {
        // InitiateSend runs RunSendAsync on Task.Run — wait until it produces FileTransferRequest
        _service.InitiateSend([CreateTempFile()], "slave", _relay, "my-machine");
        await _relay.WaitForSent(MessageKind.FileTransferRequest, TimeSpan.FromSeconds(3));

        var (_, _, json) = _relay.Snapshot().First(m => m.Kind == MessageKind.FileTransferRequest);
        var msg = json.FromSaneJson<FileTransferRequestMessage>();
        Assert.That(msg?.SourceHost, Is.EqualTo("my-machine"));
    }

    // -- ExecuteStreamRequest (slave-side) --

    [Test]
    public async Task ExecuteStreamRequest_SendsFileTransferStartWithTotalBeforeChunks()
    {
        // slave informs target of total size before streaming so target can show accurate progress
        await _service.ExecuteStreamRequest([CreateTempFile()], "target", _relay);

        var startIdx = _relay.Snapshot().FindIndex(m => m.Kind == MessageKind.FileTransferStart);
        var firstChunkIdx = _relay.Snapshot().FindIndex(m => m.Kind == MessageKind.FileTransferChunk);
        var (_, _, json) = _relay.Snapshot().First(m => m.Kind == MessageKind.FileTransferStart);
        var msg = json.FromSaneJson<FileTransferStartMessage>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(startIdx, Is.GreaterThanOrEqualTo(0), "FileTransferStart was not sent");
            Assert.That(firstChunkIdx, Is.GreaterThan(startIdx), "FileTransferStart must arrive before first chunk");
            Assert.That(msg?.TotalBytes, Is.GreaterThan(0), "TotalBytes must be set");
            Assert.That(msg?.FileNames, Is.Not.Empty, "FileNames must be set");
            Assert.That(_relay.Snapshot().Any(m => m.Kind == MessageKind.FileTransferDone), Is.True);
        }
    }

    [Test]
    public async Task ExecuteStreamRequest_WaitsForEachReliableChunkBeforeProducingNext()
    {
        var path = Path.Combine(_tempRoot, "random.bin");
        var bytes = new byte[TarGzStreamer.ChunkSize * 3];
        Random.Shared.NextBytes(bytes);
        await File.WriteAllBytesAsync(path, bytes);
        var relay = new AwaitedSendRelay();

        await _service.ExecuteStreamRequest([path], "target", relay).WaitAsync(TimeSpan.FromSeconds(5));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.MostOutstanding, Is.EqualTo(1), "each chunk's relay send must be awaited before the next one is sent");
            Assert.That(relay.ReliableSendCount, Is.GreaterThan(1));
        }
    }

    [Test]
    public async Task CancelledSend_CompletingLate_DoesNotClearReplacementSend()
    {
        var path = Path.Combine(_tempRoot, "random.bin");
        var bytes = new byte[TarGzStreamer.ChunkSize * 2];
        Random.Shared.NextBytes(bytes);
        await File.WriteAllBytesAsync(path, bytes);
        var firstRelay = new GatedReliableRelay(ignoreCancellation: true);
        var secondRelay = new GatedReliableRelay();

        var first = _service.ExecuteStreamRequest([path], "first-target", firstRelay);
        await firstRelay.FirstReliableSend.WaitAsync(TimeSpan.FromSeconds(3));
        _service.Abort(firstRelay, "replace transfer");

        var second = _service.ExecuteStreamRequest([path], "second-target", secondRelay);
        await secondRelay.FirstReliableSend.WaitAsync(TimeSpan.FromSeconds(3));
        firstRelay.ReleaseAll();
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_service.FileTransferOngoing, Is.True);
            Assert.That(_service.IsSendingTo("second-target"), Is.True);
            Assert.That(_dialog.LastState, Is.EqualTo("transferring"), "the cancelled send closed its replacement's dialog");
            Assert.That(_dialog.ProgressUpdates, Is.Zero, "the cancelled send's last chunk reported progress on its replacement's dialog");
        }

        secondRelay.ReleaseAll();
        await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task CancelledReceive_ChunkCompletingLate_ReportsNoProgress()
    {
        var path = Path.Combine(_tempRoot, "random.bin");
        var bytes = new byte[TarGzStreamer.ChunkSize * 2];
        Random.Shared.NextBytes(bytes);
        await File.WriteAllBytesAsync(path, bytes);
        var (chunks, _, totalSent) = await ComputeChunks(path);
        var extracting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _dialog.OnSetCurrentFile = () =>
        {
            stalled.TrySetResult();
            extracting.Task.Wait(TimeSpan.FromSeconds(10));
        };
        await Simulate(MessageKind.FileTransferRequest, new FileTransferRequestMessage());
        await Simulate(MessageKind.FileTransferStart, new FileTransferStartMessage(["random.bin"], totalSent));

        // held at its first file, the extractor stops reading, so a full chunk is left waiting on the pipe
        var chunk = Simulate(MessageKind.FileTransferChunk, new FileTransferChunkMessage(chunks[0].seq, chunks[0].data));
        await stalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(chunk.IsCompleted, Is.False, "the chunk was not waiting on the stalled extractor");

        // the cancel waits for the stalled extractor, so it runs aside while the chunk finishes
        var cancel = Task.Run(_dialog.TriggerCancel);
        await chunk.WaitAsync(TimeSpan.FromSeconds(5));
        extracting.SetResult();
        await cancel.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(_dialog.ProgressUpdates, Is.Zero, "the cancelled receive's last chunk reported progress on a dialog it no longer owns");
    }

    // a cancel landing between claim and stream disposes the slot's source, so the token must already be held
    [Test]
    public async Task ExecuteStreamRequest_CancelledBeforeStreaming_StopsQuietly()
    {
        _dialog.OnShowTransferring = _dialog.TriggerCancel;

        await _service.ExecuteStreamRequest([CreateTempFile()], "target", _relay);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_dialog.LastState, Is.EqualTo("closed"));
            Assert.That(_service.FileTransferOngoing, Is.False);
            Assert.That(_relay.Snapshot().Select(m => m.Kind), Does.Not.Contain(MessageKind.FileTransferStart));
        }
    }

    [Test]
    public async Task ExecuteStreamRequest_RelayThrows_ShowsError()
    {
        await _service.ExecuteStreamRequest([CreateTempFile()], "target", new ThrowingRelay());
        Assert.That(_dialog.LastState, Is.EqualTo("error"));
    }

    // the master already told the target to expect data, so it has to hear that none is coming
    [Test]
    public async Task ExecuteStreamRequest_UnreadablePath_AbortsTheTargetAndFreesTheSlot()
    {
        var path = UnreadableDirectory();
        try
        {
            await _service.ExecuteStreamRequest([path], "target", _relay);
        }
        finally
        {
            UnixMode.Set(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_dialog.LastState, Is.EqualTo("error"));
            Assert.That(_service.FileTransferOngoing, Is.False);
            Assert.That(_relay.Snapshot().Select(m => m.Kind), Is.EqualTo([MessageKind.FileTransferAbort]));
            Assert.That(_relay.Snapshot()[0].Targets, Is.EqualTo(["target"]));
        }
    }

    // nothing has been asked of the target yet, so there is nothing to abort
    [Test]
    public void InitiateSend_UnreadablePath_ShowsErrorWithoutContactingTheTarget()
    {
        var path = UnreadableDirectory();
        try
        {
            _service.InitiateSend([path], "slave", _relay);
        }
        finally
        {
            UnixMode.Set(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_dialog.LastState, Is.EqualTo("error"));
            Assert.That(_service.FileTransferOngoing, Is.False);
            Assert.That(_relay.Snapshot(), Is.Empty);
        }
    }

    [Test]
    public async Task ExecuteStreamRequest_WhileSending_LeavesTheSendInPlace()
    {
        _service.InitiateSend([CreateTempFile()], "slave", _relay);
        var other = new FakeRelay();

        await _service.ExecuteStreamRequest([CreateTempFile("b.txt")], "other", other);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_service.IsSendingTo("slave"), Is.True);
            Assert.That(_service.IsSendingTo("other"), Is.False);
            Assert.That(other.Snapshot(), Is.Empty);
        }
    }

    [Test]
    public async Task InitiateSend_WhileReceiving_IsIgnored()
    {
        await Simulate(MessageKind.FileTransferRequest, new FileTransferRequestMessage());
        var other = new FakeRelay();

        _service.InitiateSend([CreateTempFile()], "slave", other);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_service.IsReceivingFrom("master"), Is.True);
            Assert.That(_service.IsSendingTo("slave"), Is.False);
            Assert.That(other.Snapshot(), Is.Empty);
        }
    }

    // a directory the payload sizing cannot enumerate; the caller restores its mode so teardown can delete it
    private string UnreadableDirectory()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
            Assert.Ignore("needs a directory this process cannot read");
        var path = Path.Combine(_tempRoot, "unreadable");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "a.txt"), "hi");
        UnixMode.Set(path, UnixFileMode.None);
        return path;
    }

    // -- full receive flow --

    [Test]
    public async Task FullReceiveFlow_HashMatch_ShowsCompleted()
    {
        var srcFile = CreateTempFile("hello.txt", "hello world");
        var destDir = Path.Combine(_tempRoot, "dest");
        Directory.CreateDirectory(destDir);
        using var service = new FileTransferService(_dialog, new FakeDropTargetResolver(destDir), NullLogger<FileTransferService>.Instance);

        var (chunks, sha, totalSent) = await ComputeChunks(srcFile);
        await SimulateTransfer(service, _relay, "master", "hello.txt", chunks, totalSent, sha);

        Assert.That(_dialog.LastState, Is.EqualTo("completed"));
    }

    [Test]
    public async Task FullReceiveFlow_HashMismatch_ShowsError()
    {
        var srcFile = CreateTempFile("data.txt", "data");
        var destDir = Path.Combine(_tempRoot, "dest");
        Directory.CreateDirectory(destDir);
        using var service = new FileTransferService(_dialog, new FakeDropTargetResolver(destDir), NullLogger<FileTransferService>.Instance);

        var (chunks, _, totalSent) = await ComputeChunks(srcFile);
        await SimulateTransfer(service, _relay, "master", "data.txt", chunks, totalSent, new byte[32]);

        Assert.That(_dialog.LastState, Is.EqualTo("error"));
    }

    // -- watchdog / timeout --

    [Test]
    public async Task Watchdog_NoChunksAfterStart_AbortsReceive()
    {
        using var service = FastTimeoutService();
        var settled = WhenTransferSettles(service, _dialog);
        await Simulate(service, "master", MessageKind.FileTransferRequest, new FileTransferRequestMessage(), _relay);
        await Simulate(service, "master", MessageKind.FileTransferStart, new FileTransferStartMessage(["a.txt"], 100), _relay);

        await settled;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_dialog.LastState, Is.EqualTo("error"));
            Assert.That(service.FileTransferOngoing, Is.False);
        }
    }

    [Test]
    public async Task InitiateSend_NoAcceptedResponseWithinTimeout_ShowsError()
    {
        using var service = FastTimeoutService();
        var settled = WhenTransferSettles(service, _dialog);
        service.InitiateSend([CreateTempFile()], "slave", _relay);

        await settled;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_dialog.LastState, Is.EqualTo("error"));
            Assert.That(service.FileTransferOngoing, Is.False);
            Assert.That(_relay.Snapshot().Any(m => m.Kind == MessageKind.FileTransferAbort), Is.True);
        }
    }

    // -- copy buffer --

    [Test]
    public void GetCopyBuffer_InitiallyNull()
    {
        Assert.That(_service.GetCopyBuffer(), Is.Null);
    }

    [Test]
    public void SetCopyBuffer_StoresState()
    {
        _service.SetCopyBuffer("host-a", ["file1.txt", "file2.txt"]);

        var buf = _service.GetCopyBuffer();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buf!.SourceHost, Is.EqualTo("host-a"));
            Assert.That(buf.Paths, Is.EqualTo(["file1.txt", "file2.txt"]));
        }
    }

    [Test]
    public void HandleSelectionResponse_WithPaths_UpdatesBuffer()
    {
        _service.HandleSelectionResponse("remote-host", new FileSelectionResponseMessage(["a.txt", "b.txt"]));

        var buf = _service.GetCopyBuffer();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buf!.SourceHost, Is.EqualTo("remote-host"));
            Assert.That(buf.Paths, Is.EqualTo(["a.txt", "b.txt"]));
        }
    }

    [Test]
    public void HandleSelectionResponse_NothingSelected_ClearsBuffer()
    {
        _service.SetCopyBuffer("original-host", ["existing.txt"]);

        var osd = _service.HandleSelectionResponse("other-host", new FileSelectionResponseMessage(null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(osd, Is.EqualTo("0 items selected"));
            Assert.That(_service.GetCopyBuffer(), Is.Null, "an empty remote selection must clear the buffer, as an empty local one does");
        }
    }

    [Test]
    public void HandleSelectionResponse_NotFocused_KeepsBuffer()
    {
        _service.SetCopyBuffer("original-host", ["existing.txt"]);

        var osd = _service.HandleSelectionResponse("other-host", new FileSelectionResponseMessage(null, "Finder is not focused"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(osd, Is.EqualTo("Finder is not focused"));
            Assert.That(_service.GetCopyBuffer()?.SourceHost, Is.EqualTo("original-host"));
        }
    }

    // -- HandleBusy --

    [Test]
    public void HandleBusy_DuringSend_CancelsAndClosesDialog()
    {
        _service.InitiateSend([CreateTempFile()], "slave", _relay);

        _service.HandleBusy("slave");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_service.FileTransferOngoing, Is.False);
            Assert.That(_dialog.LastState, Is.EqualTo("closed"));
        }
    }

    [Test]
    public async Task HandleBusy_DuringReceive_ClearsReceiverAndClosesDialog()
    {
        await Simulate("source", MessageKind.FileTransferRequest, new FileTransferRequestMessage());

        _service.HandleBusy("source");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_service.FileTransferOngoing, Is.False);
            Assert.That(_dialog.LastState, Is.EqualTo("closed"));
        }
    }

    [Test]
    public void HandleBusy_DuringCoordinating_ClearsState()
    {
        var copyBuffer = new FileTransferService.FileCopyState("source-slave", ["/remote/file.txt"]);
        _service.InitiatePaste(copyBuffer, "target-slave", "local-host", _relay);

        _service.HandleBusy("target-slave");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_service.FileTransferOngoing, Is.False);
            Assert.That(_service.IsCoordinatingTransferTo("target-slave"), Is.False);
        }
    }

    [Test]
    public void HandleBusy_FromUnrelatedHost_IsIgnored()
    {
        _service.InitiateSend([CreateTempFile()], "slave", _relay);

        _service.HandleBusy("other-host");

        Assert.That(_service.FileTransferOngoing, Is.True);
    }

    [Test]
    public async Task HandleFileTransferRequest_WhenBusy_SendsBusyInsteadOfAccepted()
    {
        // put service into sending state (FileTransferOngoing = true)
        _service.InitiateSend([CreateTempFile()], "slave", _relay);
        _relay.ClearSent();

        // another host now tries to transfer to us
        await Simulate("other-master", MessageKind.FileTransferRequest, new FileTransferRequestMessage());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_relay.Snapshot().Any(m => m.Kind == MessageKind.FileTransferBusy && m.Targets.Contains("other-master")), Is.True);
            Assert.That(_relay.Snapshot().Any(m => m.Kind == MessageKind.FileTransferAccepted), Is.False);
        }
    }

    // -- dispose --

    [Test]
    public void Dispose_DuringSend_CleansUp()
    {
        _service.InitiateSend([CreateTempFile()], "slave", _relay);
        _service.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_dialog.LastState, Is.EqualTo("closed"));
            Assert.That(_service.FileTransferOngoing, Is.False);
        }
    }

    [Test]
    public async Task Dispose_DuringReceive_CleansUp()
    {
        await Simulate(MessageKind.FileTransferRequest, new FileTransferRequestMessage());
        _service.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_dialog.LastState, Is.EqualTo("closed"));
            Assert.That(_service.FileTransferOngoing, Is.False);
        }
    }

    [Test]
    public void Dispose_DuringCoordinating_CleansUp()
    {
        var copyBuffer = new FileTransferService.FileCopyState("source-slave", ["/remote/file.txt"]);
        _service.InitiatePaste(copyBuffer, "target-slave", "local-host", _relay);
        _relay.ClearSent();

        _service.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_dialog.LastState, Is.EqualTo("closed"));
            Assert.That(_service.FileTransferOngoing, Is.False);
            Assert.That(_relay.Snapshot().Any(m => m.Kind == MessageKind.FileTransferAbort && m.Targets.Contains("target-slave")), Is.True);
        }
    }
}

// -- fakes --

internal sealed class ThrowingRelay : NullRelaySender
{
    public override bool IsConnected => true;
    public override void Send(string[] targetHosts, byte[] payload) => throw new InvalidOperationException("relay unavailable");
}

internal sealed class GatedReliableRelay(bool ignoreCancellation = false) : NullRelaySender
{
    private readonly SemaphoreSlim _gate = new(0);
    private readonly TaskCompletionSource _firstReliableSend = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override bool IsConnected => true;
    public Task FirstReliableSend => _firstReliableSend.Task;

    public override async ValueTask SendReliableAsync(string[] targetHosts, byte[] payload, CancellationToken cancel = default)
    {
        _firstReliableSend.TrySetResult();
        await _gate.WaitAsync(ignoreCancellation ? CancellationToken.None : cancel);
    }

    public void ReleaseAll() => _gate.Release(100);
}

// counts reliable sends and how many were outstanding at once; see AwaitedCalls
internal sealed class AwaitedSendRelay : NullRelaySender
{
    private readonly AwaitedCalls _sends = new();

    public override bool IsConnected => true;
    public int ReliableSendCount => _sends.Count;
    public int MostOutstanding => _sends.MostOutstanding;

    public override ValueTask SendReliableAsync(string[] targetHosts, byte[] payload, CancellationToken cancel = default) => _sends.Next();
}
