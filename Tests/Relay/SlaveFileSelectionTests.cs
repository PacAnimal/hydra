using Hydra;
using Hydra.FileTransfer;
using Hydra.Relay;
using Tests.Setup;

namespace Tests.Relay;

/// <summary>
/// A selection query can sit behind Finder's consent prompt for as long as the user takes to answer it, and
/// the user answers it with the master's mouse — so the input arriving behind the query must not wait for it.
/// </summary>
[TestFixture]
public class SlaveFileSelectionTests
{
    [Test]
    public async Task SlowSelectionQuery_DoesNotHoldUpTheInputBehindIt()
    {
        var finder = new GatedFileSelectionDetector();
        var relay = new TestableSlaveRelay(selectionDetector: finder);
        await relay.SimulateConnected();
        await relay.SimulateMasterConfig("master-pc");
        try
        {
            // off the test thread, since a query answered inline would block it right here
            await Task.Run(() => relay.SimulateReceive("master-pc", MessageKind.FileSelectionQuery, "{}")).WaitAsync(Bound);
            await finder.Queried.WaitAsync(Bound);
            await relay.SimulateReceive("master-pc", MessageKind.KeyEvent, MessageSerializer.Decode(TestMessages.Key(0)).Json).WaitAsync(Bound);

            Assert.That(relay.Output.Keys, Has.Count.EqualTo(1), "the key queued behind the query should already be injected");
        }
        finally
        {
            finder.Release(new FileSelectionResult(true, ["/Users/me/report.pdf"]));
        }

        await relay.Sent.WaitFor(MessageKind.FileSelectionResponse);
        var (targets, _, json) = relay.Sent.Of(MessageKind.FileSelectionResponse).Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(targets, Is.EqualTo(["master-pc"]));
            Assert.That(json, Does.Contain("report.pdf"));
        }
    }

    [Test]
    public async Task TheSelectionsPaths_AreLoggedMarkedAsFilePaths()
    {
        var finder = new GatedFileSelectionDetector();
        var log = new CapturingLog<RelayConnection>();
        var relay = new TestableSlaveRelay(selectionDetector: finder, log: log);
        await relay.SimulateConnected();
        await relay.SimulateMasterConfig("master-pc");

        await relay.SimulateReceive("master-pc", MessageKind.FileSelectionQuery, "{}").WaitAsync(Bound);
        finder.Release(new FileSelectionResult(true, ["/Users/me/a.pdf", "/Users/me/b.pdf"]));
        await relay.Sent.WaitFor(MessageKind.FileSelectionResponse);

        var entry = log.Entries.Single(e => e.Message.Contains("/Users/me/a.pdf"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry.EventId, Is.EqualTo(LogEvents.FilePaths), "the management log drops it by this, not by its wording");
            Assert.That(entry.Message, Is.EqualTo("File selection query from master-pc: 2 files selected: /Users/me/a.pdf, /Users/me/b.pdf"));
        }
    }

    [Test]
    public async Task SelectionQueryThatCouldNotAskTheFileManager_AnswersCopyFailed()
    {
        var finder = new GatedFileSelectionDetector();
        var relay = new TestableSlaveRelay(selectionDetector: finder);
        await relay.SimulateConnected();
        await relay.SimulateMasterConfig("master-pc");

        await relay.SimulateReceive("master-pc", MessageKind.FileSelectionQuery, "{}").WaitAsync(Bound);
        finder.Release(FileSelectionResult.Failure);

        await relay.Sent.WaitFor(MessageKind.FileSelectionResponse);
        Assert.That(relay.Sent.Of(MessageKind.FileSelectionResponse).Single().Json, Does.Contain("Copy failed"));
    }

    // the master holds a copy pending until its query is answered, so a dropped query must still get one
    [Test]
    public async Task QueryFromAnotherMasterWhileOneRuns_IsAnsweredCopyInProgress()
    {
        var finder = new GatedFileSelectionDetector();
        var relay = new TestableSlaveRelay(selectionDetector: finder);
        await relay.SimulateConnected();
        await relay.SimulateMasterConfig("master-pc");
        await relay.SimulateMasterConfig("other-pc");
        try
        {
            await relay.SimulateReceive("master-pc", MessageKind.FileSelectionQuery, "{}").WaitAsync(Bound);
            await finder.Queried.WaitAsync(Bound);
            await relay.SimulateReceive("other-pc", MessageKind.FileSelectionQuery, "{}").WaitAsync(Bound);

            var (targets, _, json) = relay.Sent.Of(MessageKind.FileSelectionResponse).Single();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(targets, Is.EqualTo(["other-pc"]));
                Assert.That(json, Does.Contain(FileSelectionResult.InProgressMessage));
            }
            relay.Sent.Clear();
        }
        finally
        {
            finder.Release(0, new FileSelectionResult(true, ["/Users/me/first.txt"]));
            finder.Release(1, new FileSelectionResult(true, ["/Users/me/second.txt"]));
        }

        await relay.Sent.WaitFor(MessageKind.FileSelectionResponse);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(finder.QueriedAt(1).IsCompleted, Is.False, "the second query reached Finder alongside the first");
            Assert.That(relay.Sent.Of(MessageKind.FileSelectionResponse).Single().Json, Does.Contain("first.txt"));
        }
    }

    // a double press: the master takes its host's first answer, so the running query's must be that one
    [Test]
    public async Task QueryFromTheSameMasterWhileOneRuns_IsLeftToTheRunningOnesAnswer()
    {
        var finder = new GatedFileSelectionDetector();
        var relay = new TestableSlaveRelay(selectionDetector: finder);
        await relay.SimulateConnected();
        await relay.SimulateMasterConfig("master-pc");
        try
        {
            await relay.SimulateReceive("master-pc", MessageKind.FileSelectionQuery, "{}").WaitAsync(Bound);
            await finder.Queried.WaitAsync(Bound);
            await relay.SimulateReceive("master-pc", MessageKind.FileSelectionQuery, "{}").WaitAsync(Bound);

            Assert.That(relay.Sent.Of(MessageKind.FileSelectionResponse), Is.Empty);
        }
        finally
        {
            finder.Release(0, new FileSelectionResult(true, ["/Users/me/first.txt"]));
            finder.Release(1, new FileSelectionResult(true, ["/Users/me/second.txt"]));
        }

        await relay.Sent.WaitFor(MessageKind.FileSelectionResponse);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(finder.QueriedAt(1).IsCompleted, Is.False, "the second query reached Finder alongside the first");
            Assert.That(relay.Sent.Of(MessageKind.FileSelectionResponse).Single().Json, Does.Contain("first.txt"));
        }
    }

    [Test]
    public async Task QueryWhileDormant_IsAnsweredNotAvailable()
    {
        var finder = new GatedFileSelectionDetector();
        var relay = new TestableSlaveRelay(selectionDetector: finder);
        await relay.SimulateConnected();
        await relay.SimulateMasterConfig("master-pc");
        await relay.Dormancy.Enter();

        await relay.SimulateReceive("master-pc", MessageKind.FileSelectionQuery, "{}").WaitAsync(Bound);

        var (targets, _, json) = relay.Sent.Of(MessageKind.FileSelectionResponse).Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(targets, Is.EqualTo(["master-pc"]));
            Assert.That(json, Does.Contain(FileSelectionResult.UnavailableMessage));
            Assert.That(finder.Queried.IsCompleted, Is.False, "a dormant slave must not ask its file manager");
        }
    }

    [Test]
    public async Task QueryAfterTheLastFinished_RunsAgain()
    {
        var finder = new GatedFileSelectionDetector();
        var relay = new TestableSlaveRelay(selectionDetector: finder);
        await relay.SimulateConnected();
        await relay.SimulateMasterConfig("master-pc");
        finder.Release(0, new FileSelectionResult(true, ["/Users/me/first.txt"]));
        finder.Release(1, new FileSelectionResult(true, ["/Users/me/second.txt"]));

        await relay.SimulateReceive("master-pc", MessageKind.FileSelectionQuery, "{}").WaitAsync(Bound);
        await relay.Sent.WaitFor(MessageKind.FileSelectionResponse);
        await relay.SimulateReceive("master-pc", MessageKind.FileSelectionQuery, "{}").WaitAsync(Bound);

        await finder.QueriedAt(1).WaitAsync(Bound);
    }
}
