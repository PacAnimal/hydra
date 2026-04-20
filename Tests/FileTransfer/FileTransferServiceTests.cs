using Hydra.FileTransfer;
using Hydra.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.FileTransfer;

[TestFixture]
public class FileTransferServiceTests
{
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

    private Task Simulate(MessageKind kind, object msg) => Simulate(_service, "master", kind, msg, _relay);

    private static async Task Simulate(FileTransferService svc, string host, MessageKind kind, object msg, FakeRelay relay)
    {
        var decoded = MessageSerializer.Decode(MessageSerializer.Encode(kind, msg));
        await svc.OnMessageAsync(host, decoded.Kind, decoded.Json, relay);
    }

    // -- OnMessageAsync: FileTransferStart --

    [Test]
    public async Task OnMessage_FileTransferStart_ShowsTransferringDialog()
    {
        await Simulate(MessageKind.FileTransferStart, new FileTransferStartMessage(["a.txt"], 100));
        Assert.That(_dialog.LastState, Is.EqualTo("transferring"));
    }

    [Test]
    public async Task OnMessage_FileTransferAbort_ShowsErrorWithReason()
    {
        await Simulate(MessageKind.FileTransferStart, new FileTransferStartMessage(["a.txt"], 100));
        await Simulate(MessageKind.FileTransferAbort, new FileTransferAbortMessage("disk full"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_dialog.LastState, Is.EqualTo("error"));
            Assert.That(_dialog.LastError, Does.Contain("disk full"));
        }
    }

    // -- CancelRequested --

    [Test]
    public async Task CancelRequested_DuringReceive_ClosesDialog()
    {
        await Simulate(MessageKind.FileTransferStart, new FileTransferStartMessage(["a.txt"], 100));
        _dialog.TriggerCancel();
        Assert.That(_dialog.LastState, Is.EqualTo("closed"));
    }

    // -- Abort --

    [Test]
    public void Abort_DuringActiveSend_SendsAbortAndClosesDialog()
    {
        var file = Path.Combine(_tempRoot, "a.txt");
        File.WriteAllText(file, "hi");
        _service.StartSend([file], "slave", _relay);
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
    public async Task Abort_DuringActiveReceive_ClosesDialogAndSendsAbort()
    {
        await Simulate(MessageKind.FileTransferStart, new FileTransferStartMessage(["a.txt"], 100));
        _relay.Sent.Clear();

        _service.Abort(_relay, "peer disconnected");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_relay.Sent.Any(m => m.Kind == MessageKind.FileTransferAbort), Is.True);
            Assert.That(_dialog.LastState, Is.EqualTo("closed"));
            Assert.That(_service.FileTransferOngoing, Is.False);
        }
    }

    // -- HandleCancelRequested during active send --

    [Test]
    public void CancelRequested_DuringActiveSend_SendsAbortAndClosesDialog()
    {
        var file = Path.Combine(_tempRoot, "a.txt");
        File.WriteAllText(file, "hi");
        _service.StartSend([file], "slave", _relay);
        _relay.Sent.Clear();

        _dialog.TriggerCancel();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_relay.Sent.Any(m => m.Kind == MessageKind.FileTransferAbort && m.Targets.Contains("slave")), Is.True);
            Assert.That(_dialog.LastState, Is.EqualTo("closed"));
        }
    }

    // -- FileTransferOngoing guard --

    [Test]
    public void FileTransferOngoing_TrueWhileSending()
    {
        var file = Path.Combine(_tempRoot, "a.txt");
        File.WriteAllText(file, "hi");
        _service.StartSend([file], "slave", _relay);
        Assert.That(_service.FileTransferOngoing, Is.True);
    }

    [Test]
    public async Task FileTransferOngoing_TrueWhileReceiving()
    {
        await Simulate(MessageKind.FileTransferStart, new FileTransferStartMessage(["a.txt"], 100));
        Assert.That(_service.FileTransferOngoing, Is.True);
    }

    [Test]
    public async Task FileTransferOngoing_FalseAfterAbort()
    {
        await Simulate(MessageKind.FileTransferStart, new FileTransferStartMessage(["a.txt"], 100));
        _service.Abort(_relay, "test");
        Assert.That(_service.FileTransferOngoing, Is.False);
    }

    // -- FileTransferAbort when we are the sender --

    [Test]
    public async Task OnMessage_FileTransferAbort_WhenSending_CancelsSendAndClosesDialog()
    {
        var file = Path.Combine(_tempRoot, "a.txt");
        File.WriteAllText(file, "hi");
        _service.StartSend([file], "slave", _relay);
        _relay.Sent.Clear();

        // remote receiver ("slave") sends abort back — we are the sender
        await Simulate(_service, "slave", MessageKind.FileTransferAbort, new FileTransferAbortMessage("disk full"), _relay);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_dialog.LastState, Is.EqualTo("closed"));
            Assert.That(_service.FileTransferOngoing, Is.False);
            // no abort sent back — we were told to stop, not the one aborting
            Assert.That(_relay.Sent.Any(m => m.Kind == MessageKind.FileTransferAbort), Is.False);
        }
    }

    // -- CancelRequested during active receive --

    [Test]
    public async Task CancelRequested_DuringActiveReceive_ClosesDialogAndSendsAbort()
    {
        await Simulate(MessageKind.FileTransferStart, new FileTransferStartMessage(["a.txt"], 100));
        _relay.Sent.Clear();

        _dialog.TriggerCancel();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_relay.Sent.Any(m => m.Kind == MessageKind.FileTransferAbort && m.Targets.Contains("master")), Is.True);
            Assert.That(_dialog.LastState, Is.EqualTo("closed"));
            Assert.That(_service.FileTransferOngoing, Is.False);
        }
    }

    // -- full receive flow --

    [Test]
    public async Task FullReceiveFlow_HashMatch_ShowsCompleted()
    {
        var srcFile = Path.Combine(_tempRoot, "hello.txt");
        await File.WriteAllTextAsync(srcFile, "hello world");

        var destDir = Path.Combine(_tempRoot, "dest");
        Directory.CreateDirectory(destDir);
        using var service = new FileTransferService(
            _dialog,
            new FakeDropTargetResolver(destDir),
            NullLogger<FileTransferService>.Instance);

        // generate chunks
        var chunks = new List<(byte[] data, int seq)>();
        var sha = await TarGzStreamer.StreamAsync([srcFile],
            (data, seq, _) => { chunks.Add((data, seq)); return Task.CompletedTask; },
            CancellationToken.None);

        // simulate protocol: start → chunks → done
        await Simulate(service, "master", MessageKind.FileTransferStart, new FileTransferStartMessage(["hello.txt"], 11), _relay);
        foreach (var (data, seq) in chunks)
            await Simulate(service, "master", MessageKind.FileTransferChunk, new FileTransferChunkMessage(seq, data), _relay);
        var totalSent = chunks.Sum(c => (long)c.data.Length);
        await Simulate(service, "master", MessageKind.FileTransferDone, new FileTransferDoneMessage(totalSent, sha), _relay);

        Assert.That(_dialog.LastState, Is.EqualTo("completed"));
    }

    [Test]
    public async Task FullReceiveFlow_HashMismatch_ShowsError()
    {
        var srcFile = Path.Combine(_tempRoot, "data.txt");
        await File.WriteAllTextAsync(srcFile, "data");

        var destDir = Path.Combine(_tempRoot, "dest");
        Directory.CreateDirectory(destDir);
        using var service = new FileTransferService(
            _dialog,
            new FakeDropTargetResolver(destDir),
            NullLogger<FileTransferService>.Instance);

        var chunks = new List<(byte[] data, int seq)>();
        await TarGzStreamer.StreamAsync([srcFile],
            (data, seq, _) => { chunks.Add((data, seq)); return Task.CompletedTask; },
            CancellationToken.None);

        await Simulate(service, "master", MessageKind.FileTransferStart, new FileTransferStartMessage(["data.txt"], 4), _relay);
        foreach (var (data, seq) in chunks)
            await Simulate(service, "master", MessageKind.FileTransferChunk, new FileTransferChunkMessage(seq, data), _relay);

        // send wrong hash — all zeros
        await Simulate(service, "master", MessageKind.FileTransferDone, new FileTransferDoneMessage(0, new byte[32]), _relay);

        Assert.That(_dialog.LastState, Is.EqualTo("error"));
    }

    // -- relay send failure during active transfer --

    [Test]
    public async Task Send_RelayThrows_ShowsError()
    {
        var file = Path.Combine(_tempRoot, "data.txt");
        await File.WriteAllTextAsync(file, "hello");
        _service.StartSend([file], "slave", new ThrowingRelay());

        // StreamAsync runs on Task.Run — poll until it completes
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_dialog.LastState == "transferring" && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_dialog.LastState, Is.EqualTo("error"));
            Assert.That(_service.FileTransferOngoing, Is.False);
        }
    }

    // -- watchdog timeout --

    [Test]
    public async Task Watchdog_NoChunksAfterTimeout_AbortsReceive()
    {
        using var service = new FileTransferService(
            _dialog, new FakeDropTargetResolver(_tempRoot),
            NullLogger<FileTransferService>.Instance, watchdogTimeoutMs: 100);

        await Simulate(service, "master", MessageKind.FileTransferStart, new FileTransferStartMessage(["a.txt"], 100), _relay);

        // no chunks arrive — watchdog should fire within ~200ms and abort
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_dialog.LastState != "error" && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_dialog.LastState, Is.EqualTo("error"));
            Assert.That(service.FileTransferOngoing, Is.False);
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
            Assert.That(buf, Is.Not.Null);
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
            Assert.That(buf, Is.Not.Null);
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

    // -- InitiatePaste --

    [Test]
    public void InitiatePaste_LocalSource_StartsLocalSend()
    {
        var file = Path.Combine(_tempRoot, "a.txt");
        File.WriteAllText(file, "hi");
        var copyBuffer = new FileTransferService.FileCopyState("local-host", [file]);

        _service.InitiatePaste(copyBuffer, "slave-host", "local-host", _relay);

        Assert.That(_service.FileTransferOngoing, Is.True);
    }

    [Test]
    public void InitiatePaste_RemoteSource_SendsFileStreamRequest()
    {
        var copyBuffer = new FileTransferService.FileCopyState("source-slave", ["/remote/file.txt"]);

        _service.InitiatePaste(copyBuffer, "target-slave", "local-host", _relay);

        var req = _relay.Sent.FirstOrDefault(m => m.Kind == MessageKind.FileStreamRequest);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(req, Is.Not.EqualTo(default(ValueTuple<string[], MessageKind, string>)));
            Assert.That(req.Targets, Contains.Item("source-slave"));
        }
    }

    // -- dispose during active transfer --

    [Test]
    public void Dispose_DuringActiveSend_CleansUp()
    {
        var file = Path.Combine(_tempRoot, "a.txt");
        File.WriteAllText(file, "hi");
        _service.StartSend([file], "slave", _relay);
        _service.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_dialog.LastState, Is.EqualTo("closed"));
            Assert.That(_service.FileTransferOngoing, Is.False);
        }
    }

    [Test]
    public async Task Dispose_DuringActiveReceive_CleansUp()
    {
        await Simulate(MessageKind.FileTransferStart, new FileTransferStartMessage(["a.txt"], 100));

        _service.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_dialog.LastState, Is.EqualTo("closed"));
            Assert.That(_service.FileTransferOngoing, Is.False);
        }
    }
}

// -- fakes (only used by this test class) --

internal sealed class FakeFileTransferDialog : IFileTransferDialog
{
    public string LastState { get; private set; } = "none";
    public string? LastError { get; private set; }

    public void ShowPending(FileTransferInfo info) => LastState = "pending";
    public void ShowTransferring(FileTransferInfo info) => LastState = "transferring";
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
    public ValueTask Send(string[] targetHosts, byte[] payload) => ValueTask.FromException(new InvalidOperationException("relay unavailable"));
}
