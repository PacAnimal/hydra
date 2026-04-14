using Hydra.FileTransfer;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.FileTransfer;

[TestFixture]
public class FileTransferServiceTests
{
    private FakeFileDragSource _dragSource = null!;
    private FakeFileTransferDialog _dialog = null!;
    private FakeRelay _relay = null!;
    private FileTransferService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _dragSource = new FakeFileDragSource();
        _dialog = new FakeFileTransferDialog();
        _relay = new FakeRelay();
        _service = new FileTransferService(_dragSource, _dialog, new NullDropTargetResolver(), NullLogger<FileTransferService>.Instance);
        _tempRoot = Path.Combine(Path.GetTempPath(), "hydra-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);
    }

    private string _tempRoot = null!;

    [TearDown]
    public void TearDown()
    {
        _service.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best effort */ }
    }

    private void BeginDrag(string target = "slave") { _dragSource.Paths = ["/tmp/a.txt"]; _service.TryBeginDrag(target, _relay); }
    private void BeginDragAndDrop(string target = "slave") { BeginDrag(target); _service.Drop(_relay); }

    private Task Simulate(MessageKind kind, object msg) => Simulate(_service, "master", kind, msg, _relay);

    private static async Task Simulate(FileTransferService svc, string host, MessageKind kind, object msg, FakeRelay relay)
    {
        var decoded = MessageSerializer.Decode(MessageSerializer.Encode(kind, msg));
        await svc.OnMessageAsync(host, decoded.Kind, decoded.Json, relay);
    }

    // -- TryBeginDrag --

    [Test]
    public void TryBeginDrag_NoPaths_ReturnsFalseAndSendsNothing()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_service.TryBeginDrag("slave", _relay), Is.False);
            Assert.That(_relay.Sent, Is.Empty);
        }
    }

    [Test]
    public void TryBeginDrag_WithPaths_ReturnsTrueAndSendsDragEnter()
    {
        _dragSource.Paths = ["/tmp/a.txt"];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_service.TryBeginDrag("slave", _relay), Is.True);
            Assert.That(_relay.Sent.Single().Kind, Is.EqualTo(MessageKind.FileDragEnter));
            Assert.That(_relay.Sent.Single().Targets, Is.EqualTo(["slave"]));
            Assert.That(_dialog.LastState, Is.EqualTo("pending"));
        }
    }

    // -- ReTargetDrag --

    [Test]
    public void ReTargetDrag_SendsCancelToOldHostAndEnterToNewHost()
    {
        _dragSource.Paths = ["/tmp/a.txt"];
        _service.TryBeginDrag("slave1", _relay);
        _relay.Sent.Clear();

        _service.ReTargetDrag("slave2", _relay);

        Assert.That(_relay.Sent, Has.Count.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_relay.Sent[0].Kind, Is.EqualTo(MessageKind.FileDragCancel));
            Assert.That(_relay.Sent[0].Targets, Is.EqualTo(["slave1"]));
            Assert.That(_relay.Sent[1].Kind, Is.EqualTo(MessageKind.FileDragEnter));
            Assert.That(_relay.Sent[1].Targets, Is.EqualTo(["slave2"]));
        }
    }

    // -- CancelDrag --

    [Test]
    public void CancelDrag_SendsCancelAndClosesDialog()
    {
        _dragSource.Paths = ["/tmp/a.txt"];
        _service.TryBeginDrag("slave", _relay);
        _relay.Sent.Clear();

        _service.CancelDrag(_relay);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_relay.Sent.Single().Kind, Is.EqualTo(MessageKind.FileDragCancel));
            Assert.That(_dialog.LastState, Is.EqualTo("closed"));
        }
    }

    // -- Drop --

    [Test]
    public void Drop_ShowsTransferringAndSetsState()
    {
        _dragSource.Paths = ["/tmp/nonexistent.txt"];
        _service.TryBeginDrag("slave", _relay);

        _service.Drop(_relay);

        Assert.That(_dialog.LastState, Is.EqualTo("transferring"));
    }

    // -- Abort --

    [Test]
    public void Abort_DuringDrag_SendsCancelAndClosesDialog()
    {
        _dragSource.Paths = ["/tmp/a.txt"];
        _service.TryBeginDrag("slave", _relay);
        _relay.Sent.Clear();

        _service.Abort(_relay, "connection lost");

        using (Assert.EnterMultipleScope())
        {
            // drag-only phase uses FileDragCancel, not FileTransferAbort
            Assert.That(_relay.Sent.Single().Kind, Is.EqualTo(MessageKind.FileDragCancel));
            Assert.That(_dialog.LastState, Is.EqualTo("closed"));
        }
    }

    // -- OnMessageAsync --

    [Test]
    public async Task OnMessage_FileDragEnter_ShowsPendingDialog()
    {
        await Simulate(MessageKind.FileDragEnter, new FileDragEnterMessage(["a.txt"], 100));
        Assert.That(_dialog.LastState, Is.EqualTo("pending"));
    }

    [Test]
    public async Task OnMessage_FileDragCancel_ClosesDialog()
    {
        await Simulate(MessageKind.FileDragEnter, new FileDragEnterMessage(["a.txt"], 100));
        await Simulate(MessageKind.FileDragCancel, new FileDragCancelMessage());
        Assert.That(_dialog.LastState, Is.EqualTo("closed"));
    }

    [Test]
    public async Task OnMessage_FileTransferAbort_ShowsErrorWithReason()
    {
        await Simulate(MessageKind.FileDragEnter, new FileDragEnterMessage(["a.txt"], 100));
        await Simulate(MessageKind.FileTransferAbort, new FileTransferAbortMessage("disk full"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_dialog.LastState, Is.EqualTo("error"));
            Assert.That(_dialog.LastError, Does.Contain("disk full"));
        }
    }

    // -- CancelRequested --

    [Test]
    public async Task CancelRequested_DuringPendingReceive_ClosesDialog()
    {
        await Simulate(MessageKind.FileDragEnter, new FileDragEnterMessage(["a.txt"], 100));
        _dialog.TriggerCancel();
        Assert.That(_dialog.LastState, Is.EqualTo("closed"));
    }

    // -- Abort during active send --

    [Test]
    public void Abort_DuringActiveSend_SendsAbortAndClosesDialog()
    {
        _dragSource.Paths = ["/tmp/a.txt"];
        _service.TryBeginDrag("slave", _relay);
        _service.Drop(_relay);
        _relay.Sent.Clear();

        _service.Abort(_relay, "peer disconnected");

        using (Assert.EnterMultipleScope())
        {
            // active send → FileTransferAbort (not FileDragCancel)
            Assert.That(_relay.Sent.Any(m => m.Kind == MessageKind.FileTransferAbort), Is.True);
            Assert.That(_dialog.LastState, Is.EqualTo("closed"));
            Assert.That(_service.FileTransferOngoing, Is.False);
        }
    }

    [Test]
    public async Task Abort_DuringActiveReceive_ClosesDialogAndSendsAbort()
    {
        await Simulate(MessageKind.FileDragEnter, new FileDragEnterMessage(["a.txt"], 100));
        await Simulate(MessageKind.FileTransferStart, new FileTransferStartMessage());
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
        _dragSource.Paths = ["/tmp/a.txt"];
        _service.TryBeginDrag("slave", _relay);
        _service.Drop(_relay);
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
    public void FileTransferOngoing_TrueWhileDragPending()
    {
        _dragSource.Paths = ["/tmp/a.txt"];
        _service.TryBeginDrag("slave", _relay);
        Assert.That(_service.FileTransferOngoing, Is.True);
    }

    [Test]
    public void FileTransferOngoing_TrueWhileSending()
    {
        _dragSource.Paths = ["/tmp/a.txt"];
        _service.TryBeginDrag("slave", _relay);
        _service.Drop(_relay);
        Assert.That(_service.FileTransferOngoing, Is.True);
    }

    [Test]
    public async Task FileTransferOngoing_TrueWhileReceiving()
    {
        await Simulate(MessageKind.FileDragEnter, new FileDragEnterMessage(["a.txt"], 100));
        Assert.That(_service.FileTransferOngoing, Is.True);
    }

    [Test]
    public void FileTransferOngoing_FalseAfterAbort()
    {
        _dragSource.Paths = ["/tmp/a.txt"];
        _service.TryBeginDrag("slave", _relay);
        _service.Abort(_relay, "test");
        Assert.That(_service.FileTransferOngoing, Is.False);
    }

    // -- slave-initiated reverse transfer (TryBeginDrag + Drop in sequence) --

    [Test]
    public void SlaveInitiatedTransfer_SendsDragEnterThenTransferStart()
    {
        // simulates what SlaveRelayConnection does on LeaveScreen: TryBeginDrag then Drop
        _dragSource.Paths = ["/tmp/a.txt"];
        var began = _service.TryBeginDrag("master", _relay);
        Assert.That(began, Is.True);
        _service.Drop(_relay);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_relay.Sent.Any(m => m.Kind == MessageKind.FileDragEnter && m.Targets.Contains("master")), Is.True);
            // FileTransferStart is sent by StreamAsync on a Task.Run — we only verify the drag/send state was established
            Assert.That(_dialog.LastState, Is.EqualTo("transferring"));
        }
    }

    // -- FileTransferAbort when we are the sender --

    [Test]
    public async Task OnMessage_FileTransferAbort_WhenSending_CancelsSendAndClosesDialog()
    {
        _dragSource.Paths = ["/tmp/a.txt"];
        _service.TryBeginDrag("slave", _relay);
        _service.Drop(_relay);
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
        await Simulate(MessageKind.FileDragEnter, new FileDragEnterMessage(["a.txt"], 100));
        await Simulate(MessageKind.FileTransferStart, new FileTransferStartMessage());
        _relay.Sent.Clear();

        _dialog.TriggerCancel();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_relay.Sent.Any(m => m.Kind == MessageKind.FileTransferAbort && m.Targets.Contains("master")), Is.True);
            Assert.That(_dialog.LastState, Is.EqualTo("closed"));
            Assert.That(_service.FileTransferOngoing, Is.False);
        }
    }

    // -- ReTargetDrag when no drag active --

    [Test]
    public void ReTargetDrag_WhenNoDragActive_DoesNothing()
    {
        _service.ReTargetDrag("slave2", _relay);
        Assert.That(_relay.Sent, Is.Empty);
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
            new NullFileDragSource(), _dialog,
            new FakeDropTargetResolver(destDir),
            NullLogger<FileTransferService>.Instance);

        // generate chunks
        var chunks = new List<(byte[] data, int seq)>();
        var sha = await TarGzStreamer.StreamAsync([srcFile],
            (data, seq, _) => { chunks.Add((data, seq)); return Task.CompletedTask; },
            CancellationToken.None);

        // simulate protocol: enter → start → chunks → done
        await Simulate(service, "master", MessageKind.FileDragEnter, new FileDragEnterMessage(["hello.txt"], 11), _relay);
        await Simulate(service, "master", MessageKind.FileTransferStart, new FileTransferStartMessage(), _relay);
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
            new NullFileDragSource(), _dialog,
            new FakeDropTargetResolver(destDir),
            NullLogger<FileTransferService>.Instance);

        var chunks = new List<(byte[] data, int seq)>();
        await TarGzStreamer.StreamAsync([srcFile],
            (data, seq, _) => { chunks.Add((data, seq)); return Task.CompletedTask; },
            CancellationToken.None);

        await Simulate(service, "master", MessageKind.FileDragEnter, new FileDragEnterMessage(["data.txt"], 4), _relay);
        await Simulate(service, "master", MessageKind.FileTransferStart, new FileTransferStartMessage(), _relay);
        foreach (var (data, seq) in chunks)
            await Simulate(service, "master", MessageKind.FileTransferChunk, new FileTransferChunkMessage(seq, data), _relay);

        // send wrong hash — all zeros
        await Simulate(service, "master", MessageKind.FileTransferDone, new FileTransferDoneMessage(0, new byte[32]), _relay);

        Assert.That(_dialog.LastState, Is.EqualTo("error"));
    }
    // -- relay send failure during active transfer --

    [Test]
    public async Task Drop_RelayThrows_ShowsError()
    {
        var file = Path.Combine(_tempRoot, "data.txt");
        await File.WriteAllTextAsync(file, "hello");
        _dragSource.Paths = [file];
        _service.TryBeginDrag("slave", _relay);

        _service.Drop(new ThrowingRelay());

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
            new NullFileDragSource(), _dialog, new NullDropTargetResolver(),
            NullLogger<FileTransferService>.Instance, watchdogTimeoutMs: 100);

        await Simulate(service, "master", MessageKind.FileDragEnter, new FileDragEnterMessage(["a.txt"], 100), _relay);
        await Simulate(service, "master", MessageKind.FileTransferStart, new FileTransferStartMessage(), _relay);

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

    // -- dispose during active transfer --

    [Test]
    public void Dispose_DuringActiveSend_CleansUp()
    {
        BeginDragAndDrop();
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
        await Simulate(MessageKind.FileDragEnter, new FileDragEnterMessage(["a.txt"], 100));
        await Simulate(MessageKind.FileTransferStart, new FileTransferStartMessage());

        _service.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_dialog.LastState, Is.EqualTo("closed"));
            Assert.That(_service.FileTransferOngoing, Is.False);
        }
    }
}

// -- fakes (only used by this test class) --

internal sealed class FakeFileDragSource : IFileDragSource
{
    public List<string>? Paths { get; set; }
    public List<string>? GetDraggedPaths() => Paths;
    public void UpdateActiveEdges(List<ActiveEdgeRange> ranges) { }
}

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
    public string GetDirectoryUnderCursor() => directory;
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
