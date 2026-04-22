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
        _tempRoot = Path.Combine(Path.GetTempPath(), "hydra-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);
        _service = new FileTransferService(_dialog, new FakeDropTargetResolver(_tempRoot), NullLogger<FileTransferService>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _service.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best effort */ }
    }

    // -- helpers --

    private string CreateTempFile(string name = "a.txt", string content = "hi")
    {
        var path = Path.Combine(_tempRoot, name);
        File.WriteAllText(path, content);
        return path;
    }

    // polls until dialog leaves "transferring" (background tasks complete), or times out
    private static async Task WaitForDialogNotTransferring(FakeFileTransferDialog dialog, int timeoutSecs = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSecs);
        while (dialog.LastState == "transferring" && DateTime.UtcNow < deadline)
            await Task.Delay(20);
    }

    // waits for a specific message kind to appear in relay.Sent (background tasks may produce it async)
    private static async Task WaitForMessage(FakeRelay relay, MessageKind kind, int timeoutSecs = 3)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSecs);
        while (!relay.Sent.Any(m => m.Kind == kind) && DateTime.UtcNow < deadline)
            await Task.Delay(10);
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
        await svc.OnMessageAsync(host, decoded.Kind, decoded.Json, relay);
    }

    // computes chunks + sha for a file without sending them anywhere
    private static async Task<(List<(byte[] data, int seq)> chunks, byte[] sha, long totalSent)> ComputeChunks(string path)
    {
        var chunks = new List<(byte[] data, int seq)>();
        var sha = await TarGzStreamer.StreamAsync([path],
            (data, seq, _) => { chunks.Add((data, seq)); return Task.CompletedTask; },
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

        var accepted = _relay.Sent.FirstOrDefault(m => m.Kind == MessageKind.FileTransferAccepted);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accepted, Is.Not.EqualTo(default(ValueTuple<string[], MessageKind, string>)));
            Assert.That(accepted.Targets, Contains.Item("master"));
        }
    }

    [Test]
    public async Task OnMessage_FileTransferRequest_NoPasteDir_SendsAbortWithReasonNoFolder()
    {
        using var service = new FileTransferService(_dialog, new NullDropTargetResolver(), NullLogger<FileTransferService>.Instance);

        await Simulate(service, "master", MessageKind.FileTransferRequest, new FileTransferRequestMessage(), _relay);

        var abort = _relay.Sent.FirstOrDefault(m => m.Kind == MessageKind.FileTransferAbort);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(abort, Is.Not.EqualTo(default(ValueTuple<string[], MessageKind, string>)));
            Assert.That(abort.Json, Does.Contain(FileTransferService.ReasonNoFolder));
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

    // -- FileTransferAbort (inbound) --

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
        _service.StartSend([CreateTempFile()], "slave", _relay);
        _relay.Sent.Clear();

        // receiver ("slave") aborts — we were the sender, so we close without sending our own abort
        await Simulate("slave", MessageKind.FileTransferAbort, new FileTransferAbortMessage("disk full"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_dialog.LastState, Is.EqualTo("closed"));
            Assert.That(_service.FileTransferOngoing, Is.False);
            Assert.That(_relay.Sent.Any(m => m.Kind == MessageKind.FileTransferAbort), Is.False);
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
        _service.StartSend([CreateTempFile()], "slave", _relay);
        _relay.Sent.Clear();

        _dialog.TriggerCancel();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_relay.Sent.Any(m => m.Kind == MessageKind.FileTransferAbort && m.Targets.Contains("slave")), Is.True);
            Assert.That(_dialog.LastState, Is.EqualTo("closed"));
        }
    }

    [Test]
    public async Task CancelRequested_DuringReceive_SendsAbortAndClosesDialog()
    {
        await Simulate(MessageKind.FileTransferRequest, new FileTransferRequestMessage());
        _relay.Sent.Clear();

        _dialog.TriggerCancel();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_relay.Sent.Any(m => m.Kind == MessageKind.FileTransferAbort && m.Targets.Contains("master")), Is.True);
            Assert.That(_dialog.LastState, Is.EqualTo("closed"));
            Assert.That(_service.FileTransferOngoing, Is.False);
        }
    }

    // -- programmatic abort --

    [Test]
    public void Abort_DuringSend_SendsAbortAndClosesDialog()
    {
        _service.StartSend([CreateTempFile()], "slave", _relay);
        _relay.Sent.Clear();

        _service.Abort(_relay, "peer disconnected");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_relay.Sent.Any(m => m.Kind == MessageKind.FileTransferAbort), Is.True);
            Assert.That(_dialog.LastState, Is.EqualTo("closed"));
            Assert.That(_service.FileTransferOngoing, Is.False);
        }
    }

    [Test]
    public async Task Abort_DuringReceive_SendsAbortAndClosesDialog()
    {
        await Simulate(MessageKind.FileTransferRequest, new FileTransferRequestMessage());
        _relay.Sent.Clear();

        _service.Abort(_relay, "peer disconnected");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_relay.Sent.Any(m => m.Kind == MessageKind.FileTransferAbort), Is.True);
            Assert.That(_dialog.LastState, Is.EqualTo("closed"));
            Assert.That(_service.FileTransferOngoing, Is.False);
        }
    }

    [Test]
    public void Abort_DuringCoordinating_SendsAbortToTargetAndClearsState()
    {
        var copyBuffer = new FileTransferService.FileCopyState("source-slave", ["/remote/file.txt"]);
        _service.InitiatePaste(copyBuffer, "target-slave", "local-host", _relay);
        _relay.Sent.Clear();

        _service.Abort(_relay, "peer disconnected");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_relay.Sent.Any(m => m.Kind == MessageKind.FileTransferAbort && m.Targets.Contains("target-slave")), Is.True);
            Assert.That(_service.FileTransferOngoing, Is.False);
        }
    }

    // -- state queries --

    [Test]
    public void FileTransferOngoing_TrueWhileSending()
    {
        _service.StartSend([CreateTempFile()], "slave", _relay);
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
        _service.StartSend([CreateTempFile()], "slave", _relay);

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

        var (targets, _, _) = _relay.Sent.FirstOrDefault(m => m.Kind == MessageKind.FileStreamRequest);
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
            Assert.That(_relay.Sent, Is.Empty);
        }
    }

    [Test]
    public void InitiatePaste_SlaveToSlave_SendsFileTransferRequestWithSourceHostToTarget()
    {
        var copyBuffer = new FileTransferService.FileCopyState("source-slave", ["/remote/file.txt"]);

        _service.InitiatePaste(copyBuffer, "target-slave", "local-host", _relay);

        var (targets, _, json) = _relay.Sent.FirstOrDefault(m => m.Kind == MessageKind.FileTransferRequest);
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

        var (targets, _, _) = _relay.Sent.FirstOrDefault(m => m.Kind == MessageKind.FileStreamRequest);
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

    // -- StartSend --

    [Test]
    public async Task StartSend_IncludesLocalHostAsSourceHostInRequestMessage()
    {
        // StartSend runs StreamAsync on Task.Run — poll until it produces FileTransferRequest
        _service.StartSend([CreateTempFile()], "slave", _relay, "my-machine");
        await WaitForMessage(_relay, MessageKind.FileTransferRequest);

        var (_, _, json) = _relay.Sent.FirstOrDefault(m => m.Kind == MessageKind.FileTransferRequest);
        var msg = json.FromSaneJson<FileTransferRequestMessage>();
        Assert.That(msg?.SourceHost, Is.EqualTo("my-machine"));
    }

    // -- StreamToHost (slave-side) --

    [Test]
    public async Task StreamToHost_SendsFileTransferStartWithTotalBeforeChunks()
    {
        // slave informs target of total size before streaming so target can show accurate progress
        await _service.StreamToHost([CreateTempFile()], "target", _relay);

        var startIdx = _relay.Sent.FindIndex(m => m.Kind == MessageKind.FileTransferStart);
        var firstChunkIdx = _relay.Sent.FindIndex(m => m.Kind == MessageKind.FileTransferChunk);
        var (_, _, json) = _relay.Sent.FirstOrDefault(m => m.Kind == MessageKind.FileTransferStart);
        var msg = json.FromSaneJson<FileTransferStartMessage>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(startIdx, Is.GreaterThanOrEqualTo(0), "FileTransferStart was not sent");
            Assert.That(firstChunkIdx, Is.GreaterThan(startIdx), "FileTransferStart must arrive before first chunk");
            Assert.That(msg?.TotalBytes, Is.GreaterThan(0), "TotalBytes must be set");
            Assert.That(msg?.FileNames, Is.Not.Empty, "FileNames must be set");
            Assert.That(_relay.Sent.Any(m => m.Kind == MessageKind.FileTransferDone), Is.True);
        }
    }

    [Test]
    public async Task StreamToHost_RelayThrows_ShowsError()
    {
        // StreamToHost is awaitable — no polling needed
        await _service.StreamToHost([CreateTempFile()], "target", new ThrowingRelay());
        Assert.That(_dialog.LastState, Is.EqualTo("error"));
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
        await Simulate(service, "master", MessageKind.FileTransferRequest, new FileTransferRequestMessage(), _relay);
        await Simulate(service, "master", MessageKind.FileTransferStart, new FileTransferStartMessage(["a.txt"], 100), _relay);

        await WaitForDialogNotTransferring(_dialog);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_dialog.LastState, Is.EqualTo("error"));
            Assert.That(service.FileTransferOngoing, Is.False);
        }
    }

    [Test]
    public async Task StartSend_NoAcceptedResponseWithinTimeout_ShowsError()
    {
        using var service = FastTimeoutService();
        service.StartSend([CreateTempFile()], "slave", _relay);

        await WaitForDialogNotTransferring(_dialog);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_dialog.LastState, Is.EqualTo("error"));
            Assert.That(service.FileTransferOngoing, Is.False);
            Assert.That(_relay.Sent.Any(m => m.Kind == MessageKind.FileTransferAbort), Is.True);
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
        var json = MessageSerializer.Decode(MessageSerializer.Encode(MessageKind.FileSelectionResponse, new FileSelectionResponseMessage(["a.txt", "b.txt"]))).Json;
        _service.HandleSelectionResponse("remote-host", json);

        var buf = _service.GetCopyBuffer();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(buf!.SourceHost, Is.EqualTo("remote-host"));
            Assert.That(buf.Paths, Is.EqualTo(["a.txt", "b.txt"]));
        }
    }

    [Test]
    public void HandleSelectionResponse_NullPaths_DoesNotUpdateBuffer()
    {
        _service.SetCopyBuffer("original-host", ["existing.txt"]);
        var json = MessageSerializer.Decode(MessageSerializer.Encode(MessageKind.FileSelectionResponse, new FileSelectionResponseMessage(null))).Json;
        _service.HandleSelectionResponse("other-host", json);

        Assert.That(_service.GetCopyBuffer()?.SourceHost, Is.EqualTo("original-host"));
    }

    // -- HandleBusy --

    [Test]
    public void HandleBusy_DuringSend_CancelsAndClosesDialog()
    {
        _service.StartSend([CreateTempFile()], "slave", _relay);

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
        _service.StartSend([CreateTempFile()], "slave", _relay);

        _service.HandleBusy("other-host");

        Assert.That(_service.FileTransferOngoing, Is.True);
    }

    [Test]
    public async Task HandleFileTransferRequest_WhenBusy_SendsBusyInsteadOfAccepted()
    {
        // put service into sending state (FileTransferOngoing = true)
        _service.StartSend([CreateTempFile()], "slave", _relay);
        _relay.Sent.Clear();

        // another host now tries to transfer to us
        await Simulate("other-master", MessageKind.FileTransferRequest, new FileTransferRequestMessage());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_relay.Sent.Any(m => m.Kind == MessageKind.FileTransferBusy && m.Targets.Contains("other-master")), Is.True);
            Assert.That(_relay.Sent.Any(m => m.Kind == MessageKind.FileTransferAccepted), Is.False);
        }
    }

    // -- dispose --

    [Test]
    public void Dispose_DuringSend_CleansUp()
    {
        _service.StartSend([CreateTempFile()], "slave", _relay);
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
        _relay.Sent.Clear();

        _service.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_dialog.LastState, Is.EqualTo("closed"));
            Assert.That(_service.FileTransferOngoing, Is.False);
            Assert.That(_relay.Sent.Any(m => m.Kind == MessageKind.FileTransferAbort && m.Targets.Contains("target-slave")), Is.True);
        }
    }
}

// -- fakes --

internal sealed class FakeFileTransferDialog : IFileTransferDialog
{
    public string LastState { get; private set; } = "none";
    public string? LastError { get; private set; }

    public void ShowPending(FileTransferInfo info) => LastState = "pending";
    public void ShowTransferring(FileTransferInfo info) => LastState = "transferring";
    public void UpdateTotal(FileTransferInfo info) { }
    public void SetCurrentFile(string fileName) { }
    public void UpdateProgress(long bytesTransferred, double bytesPerSecond) { }
    public void ShowCompleted() => LastState = "completed";
    public void ShowError(string message) { LastState = "error"; LastError = message; }
    public void Close() => LastState = "closed";
    public event Action? CancelRequested;
    public void TriggerCancel() => CancelRequested?.Invoke();
}

internal sealed class FakeDropTargetResolver(string directory) : IDropTargetResolver
{
    public string GetPasteDirectory() => directory;
    public void MoveToDestination(string tempDir, string destDir) => FileUtils.MoveTo(tempDir, destDir);
}

internal sealed class ThrowingRelay : IRelaySender
{
    public bool IsConnected => true;
#pragma warning disable CS0067
    public event Func<string[], Task>? PeersChanged;
    public event Func<string, MessageKind, string, Task>? MessageReceived;
    public event Func<Task>? Disconnected;
#pragma warning restore CS0067
    public void Send(string[] targetHosts, byte[] payload) => throw new InvalidOperationException("relay unavailable");
}
