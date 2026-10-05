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
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

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
            await Task.Run(() => relay.SimulateReceive("master-pc", MessageKind.FileSelectionQuery, "{}")).WaitAsync(Timeout);
            await finder.Queried.WaitAsync(Timeout);
            await relay.SimulateReceive("master-pc", MessageKind.KeyEvent, MessageSerializer.Decode(TestMessages.Key(0)).Json).WaitAsync(Timeout);

            Assert.That(relay.Output.Keys, Has.Count.EqualTo(1), "the key queued behind the query should already be injected");
        }
        finally
        {
            finder.Release(new FileSelectionResult(true, ["/Users/me/report.pdf"]));
        }

        await relay.WaitForSent(MessageKind.FileSelectionResponse, Timeout);
        var (targets, _, json) = relay.Snapshot().Single(s => s.Kind == MessageKind.FileSelectionResponse);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(targets, Is.EqualTo(["master-pc"]));
            Assert.That(json, Does.Contain("report.pdf"));
        }
    }

    [Test]
    public async Task SelectionQueryThatCouldNotAskTheFileManager_AnswersCopyFailed()
    {
        var finder = new GatedFileSelectionDetector();
        var relay = new TestableSlaveRelay(selectionDetector: finder);
        await relay.SimulateConnected();
        await relay.SimulateMasterConfig("master-pc");

        await relay.SimulateReceive("master-pc", MessageKind.FileSelectionQuery, "{}").WaitAsync(Timeout);
        finder.Release(FileSelectionResult.Failure);

        await relay.WaitForSent(MessageKind.FileSelectionResponse, Timeout);
        Assert.That(relay.Snapshot().Single(s => s.Kind == MessageKind.FileSelectionResponse).Json, Does.Contain("Copy failed"));
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
            await relay.SimulateReceive("master-pc", MessageKind.FileSelectionQuery, "{}").WaitAsync(Timeout);
            await finder.Queried.WaitAsync(Timeout);
            await relay.SimulateReceive("other-pc", MessageKind.FileSelectionQuery, "{}").WaitAsync(Timeout);

            var (targets, _, json) = relay.Snapshot().Single(s => s.Kind == MessageKind.FileSelectionResponse);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(targets, Is.EqualTo(["other-pc"]));
                Assert.That(json, Does.Contain(FileSelectionResult.InProgressMessage));
            }
            relay.ClearSent();
        }
        finally
        {
            finder.Release(0, new FileSelectionResult(true, ["/Users/me/first.txt"]));
            finder.Release(1, new FileSelectionResult(true, ["/Users/me/second.txt"]));
        }

        await relay.WaitForSent(MessageKind.FileSelectionResponse, Timeout);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(finder.QueriedAt(1).IsCompleted, Is.False, "the second query reached Finder alongside the first");
            Assert.That(relay.Snapshot().Single(s => s.Kind == MessageKind.FileSelectionResponse).Json, Does.Contain("first.txt"));
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
            await relay.SimulateReceive("master-pc", MessageKind.FileSelectionQuery, "{}").WaitAsync(Timeout);
            await finder.Queried.WaitAsync(Timeout);
            await relay.SimulateReceive("master-pc", MessageKind.FileSelectionQuery, "{}").WaitAsync(Timeout);

            Assert.That(relay.Snapshot().Where(s => s.Kind == MessageKind.FileSelectionResponse), Is.Empty);
        }
        finally
        {
            finder.Release(0, new FileSelectionResult(true, ["/Users/me/first.txt"]));
            finder.Release(1, new FileSelectionResult(true, ["/Users/me/second.txt"]));
        }

        await relay.WaitForSent(MessageKind.FileSelectionResponse, Timeout);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(finder.QueriedAt(1).IsCompleted, Is.False, "the second query reached Finder alongside the first");
            Assert.That(relay.Snapshot().Single(s => s.Kind == MessageKind.FileSelectionResponse).Json, Does.Contain("first.txt"));
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

        await relay.SimulateReceive("master-pc", MessageKind.FileSelectionQuery, "{}").WaitAsync(Timeout);

        var (targets, _, json) = relay.Snapshot().Single(s => s.Kind == MessageKind.FileSelectionResponse);
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

        await relay.SimulateReceive("master-pc", MessageKind.FileSelectionQuery, "{}").WaitAsync(Timeout);
        await relay.WaitForSent(MessageKind.FileSelectionResponse, Timeout);
        await relay.SimulateReceive("master-pc", MessageKind.FileSelectionQuery, "{}").WaitAsync(Timeout);

        await finder.QueriedAt(1).WaitAsync(Timeout);
    }
}
