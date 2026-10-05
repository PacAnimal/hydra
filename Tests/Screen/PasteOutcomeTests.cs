using Hydra.FileTransfer;
using Hydra.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Screen;

/// <summary>What the user is told about a paste, judged by what the transfer really did.</summary>
[TestFixture]
public class PasteOutcomeTests
{
    private readonly StartedServices _services = new();
    private FakeFileTransferDialog _dialog = null!;
    private RecordingOsd _osd = null!;
    private FileTransferService _fileTransfer = null!;
    private string _tempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        _dialog = new FakeFileTransferDialog();
        _osd = new RecordingOsd();
        _tempRoot = TestPaths.FreshFixtureRoot(nameof(PasteOutcomeTests));
        _fileTransfer = new FileTransferService(_dialog, new FakeDropTargetResolver(_tempRoot), NullLogger<FileTransferService>.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _services.StopAll();
        _fileTransfer.Dispose();
    }

    [Test]
    public async Task ReceivedTransfer_ShowsPasted()
    {
        var relay = await StartReceivingFromRemote();

        foreach (var (kind, body) in await StreamOf("a.txt"))
            await relay.FireMessageReceived("remote", kind, body);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_dialog.LastState, Is.EqualTo("completed"));
            Assert.That(await _osd.Shown.Next(1000, "the paste OSD"), Is.EqualTo("Pasted!"));
        }
    }

    [Test]
    public async Task MalformedDone_DoesNotShowPasted()
    {
        var relay = await StartReceivingFromRemote();
        await relay.FireMessageReceived("remote", MessageKind.FileTransferStart, Json(MessageKind.FileTransferStart, new FileTransferStartMessage(["a.txt"], 5)));
        relay.ClearSent();

        await relay.FireMessageReceived("remote", MessageKind.FileTransferDone, "{");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_osd.Shown.Count, Is.Zero, "the files were discarded, so nothing was pasted");
            Assert.That(_dialog.LastState, Is.EqualTo("error"));
            Assert.That(relay.Snapshot().Select(s => s.Kind), Is.EqualTo([MessageKind.FileTransferAbort]));
            Assert.That(relay.Snapshot()[0].Targets, Is.EqualTo(["remote"]));
        }
    }

    // accepting only means the target is ready, so the paste may yet fail
    [Test]
    public async Task AcceptedSend_ShowsPastingOnTheTarget()
    {
        var (_, relay, _) = await _services.Start(TransitionTestHelper.CreateService(osd: _osd, fileTransfer: _fileTransfer));
        _fileTransfer.InitiateSend([Path.Combine(_tempRoot, "a.txt")], "remote", relay, "home");
        await relay.WaitForSent(MessageKind.FileTransferRequest, TimeSpan.FromSeconds(10));

        await relay.FireMessageReceived("remote", MessageKind.FileTransferAccepted, "{}");

        var shown = relay.Snapshot().Where(s => s.Kind == MessageKind.Osd).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(shown.Select(s => s.Json), Is.EqualTo([Json(MessageKind.Osd, new OsdMessage("Pasting…"))]));
            Assert.That(shown[0].Targets, Is.EqualTo(["remote"]));
        }
    }

    [Test]
    public async Task SlaveCompletingAReceive_ShowsPasted()
    {
        var slave = new TestableSlaveRelay(osd: _osd, fileTransfer: _fileTransfer);
        await slave.SimulateReceive("home", MessageKind.FileTransferRequest, Json(MessageKind.FileTransferRequest, new FileTransferRequestMessage("home")));

        foreach (var (kind, body) in await StreamOf("a.txt"))
            await slave.SimulateReceive("home", kind, body);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_dialog.LastState, Is.EqualTo("completed"));
            Assert.That(await _osd.Shown.Next(1000, "the paste OSD"), Is.EqualTo("Pasted!"));
        }
    }

    [Test]
    public async Task SlaveFailingAReceive_DoesNotShowPasted()
    {
        var slave = new TestableSlaveRelay(osd: _osd, fileTransfer: _fileTransfer);
        await slave.SimulateReceive("home", MessageKind.FileTransferRequest, Json(MessageKind.FileTransferRequest, new FileTransferRequestMessage("home")));
        await slave.SimulateReceive("home", MessageKind.FileTransferStart, Json(MessageKind.FileTransferStart, new FileTransferStartMessage(["a.txt"], 5)));

        await slave.SimulateReceive("home", MessageKind.FileTransferDone, "{");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_dialog.LastState, Is.EqualTo("error"));
            Assert.That(_osd.Shown.Count, Is.Zero);
        }
    }

    // the slave reads a malformed abort as an abort, so the master must too
    [Test]
    public async Task MalformedAbort_WhileSending_StopsTheSend()
    {
        var (_, relay, _) = await _services.Start(TransitionTestHelper.CreateService(osd: _osd, fileTransfer: _fileTransfer));
        _fileTransfer.InitiateSend([Path.Combine(_tempRoot, "a.txt")], "remote", relay, "home");
        await relay.WaitForSent(MessageKind.FileTransferRequest, TimeSpan.FromSeconds(10));

        await relay.FireMessageReceived("remote", MessageKind.FileTransferAbort, "{");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_fileTransfer.FileTransferOngoing, Is.False);
            Assert.That(_dialog.LastState, Is.EqualTo("closed"));
        }
    }

    // the master as the paste target, waiting for the remote's stream
    private async Task<FakeRelay> StartReceivingFromRemote()
    {
        var (_, relay, _) = await _services.Start(TransitionTestHelper.CreateService(osd: _osd, fileTransfer: _fileTransfer));
        _fileTransfer.InitiatePaste(new FileTransferService.FileCopyState("remote", ["/r/a.txt"]), "home", "home", relay);
        return relay;
    }

    // a whole transfer of one small file, start to done, as the sender would put it on the wire
    private async Task<List<WireMessage>> StreamOf(string name)
    {
        var source = Path.Combine(_tempRoot, name);
        await File.WriteAllTextAsync(source, "hello");
        var chunks = new List<FileTransferChunkMessage>();
        var sha = await TarGzStreamer.StreamAsync([source], (data, seq, _) =>
        {
            chunks.Add(new FileTransferChunkMessage(seq, data));
            return ValueTask.CompletedTask;
        }, _ => { }, CancellationToken.None);
        var done = new FileTransferDoneMessage(chunks.Sum(c => (long)c.Data.Length), sha);
        return
        [
            new WireMessage(MessageKind.FileTransferStart, Json(MessageKind.FileTransferStart, new FileTransferStartMessage([name], 5))),
            .. chunks.Select(c => new WireMessage(MessageKind.FileTransferChunk, Json(MessageKind.FileTransferChunk, c))),
            new WireMessage(MessageKind.FileTransferDone, Json(MessageKind.FileTransferDone, done)),
        ];
    }

    private static string Json(MessageKind kind, object message) => MessageSerializer.Decode(MessageSerializer.Encode(kind, message)).Json;

    private sealed record WireMessage(MessageKind Kind, string Body);
}
