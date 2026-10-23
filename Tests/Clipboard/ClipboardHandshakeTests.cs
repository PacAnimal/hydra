using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Clipboard;

// EchoFallback and ClipboardSlave on their own, against a fake clipboard
[TestFixture]
public class ClipboardHandshakeTests
{
    private static readonly NullLogger Log = NullLogger.Instance;
    private static readonly Func<string, ulong?> NothingPending = _ => null;

    [Test]
    public void EchoFallback_SelectionOnly_KeepsTheClipboardFallback()
    {
        // the fake reads nothing back, as a host does its own write
        var echo = new EchoFallback(new FakeClipboardSync(), Log);
        echo.Remember(new ClipboardSnapshot("written", null, null, Html: "<b>written</b>"));

        echo.Remember(new ClipboardSnapshot(null, "new selection", null));

        Assert.That(echo.Read("test")!.Html, Is.EqualTo("<b>written</b>"));
    }

    [Test]
    public void EchoFallback_Clipboard_ReplacesTheFallback()
    {
        var echo = new EchoFallback(new FakeClipboardSync(), Log);
        echo.Remember(new ClipboardSnapshot("old", "selection", null));

        echo.Remember(new ClipboardSnapshot("new", null, null));

        Assert.That(echo.Read("test")!.Text, Is.EqualTo("new"));
    }

    [Test]
    public void SlaveHashCheck_MasterWithNoClipboard_ReadsOnlyTheSelection()
    {
        var sync = new FakeClipboardSync();
        sync.SetupImage([1, 2, 3]);
        var slave = new ClipboardSlave(sync, Log, NothingPending);

        var need = slave.OnHash(new ClipboardHashMessage(null, "selected on the master"), "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(need, Is.EqualTo(PushNeed.None));
            Assert.That(sync.PrimaryText, Is.EqualTo("selected on the master"));
            Assert.That(sync.ReadCallCount, Is.Zero);
        }
    }

    [Test]
    public void EchoFallback_Empty_ClearsAndReadsTheOwnedEmptyClipboardAsEmpty()
    {
        var sync = new FakeClipboardSync();
        var echo = new EchoFallback(sync, Log);
        echo.Remember(new ClipboardSnapshot("written", null, null));

        echo.Empty();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sync.ClearClipboardCallCount, Is.EqualTo(1));
            Assert.That(echo.Read("test")!.IsEmpty, Is.True);
        }
    }

    [Test]
    public void SlaveEmpty_AfterAPush_ClearsAndForgetsIt()
    {
        var sync = new FakeClipboardSync();
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        slave.OnPush(new ClipboardPushMessage("pushed"), "master");
        Assert.That(sync.Text, Is.EqualTo("pushed"));

        slave.Empty();
        var answer = slave.OnPull(new ClipboardPullMessage(1, ClipboardUtils.StableHash("anything")), "master");
        slave.Entered("master");
        var need = slave.OnHash(ClipboardWire.ToHash(new ClipboardSnapshot("pushed", null, null)), "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sync.ClearClipboardCallCount, Is.EqualTo(2), "the empty, then the differing hash's");
            Assert.That(sync.Text, Is.Null);
            Assert.That(answer, Is.EqualTo(new ClipboardPullResponseMessage(1, null, Unchanged: true)), "nothing was copied here");
            Assert.That(need, Is.EqualTo(PushNeed.Clipboard), "the push is forgotten");
        }
    }

    [Test]
    public void MasterEmpty_AfterAPullAnswer_ClearsAndOffersNothing()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        var pull = master.Pull("slave");
        master.TakePullAnswer("slave", new ClipboardPullResponseMessage(pull.PullId, "from the slave"));
        Assert.That(master.Hash(), Is.Not.Null);

        master.Empty();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sync.ClearClipboardCallCount, Is.EqualTo(1));
            Assert.That(sync.Text, Is.Null);
            Assert.That(master.Hash(), Is.Null);
            Assert.That(master.Push(), Is.Null);
        }
    }

    [Test]
    public void MasterPulledTwoHosts_TheNewerAnswersUnchanged_TheOlderIsStillTaken()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        var toA = master.Pull("a");
        var toC = master.Pull("c");

        var fromC = master.TakePullAnswer("c", new ClipboardPullResponseMessage(toC.PullId, null, Unchanged: true));
        var fromA = master.TakePullAnswer("a", new ClipboardPullResponseMessage(toA.PullId, "from a"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fromC.Taken, Is.True);
            Assert.That(fromC.Applied, Is.Null);
            Assert.That(fromC.Superseded, Is.Empty);
            Assert.That(fromA.Taken, Is.True);
            Assert.That(sync.Text, Is.EqualTo("from a"));
            Assert.That(master.Outstanding, Is.Empty);
        }
    }

    [Test]
    public void MasterPulledTwoHosts_TheOlderChangesTheClipboardFirst_TheNewerIsStillTaken()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        var toA = master.Pull("a");
        var toC = master.Pull("c");

        var fromA = master.TakePullAnswer("a", new ClipboardPullResponseMessage(toA.PullId, "from a"));
        var fromC = master.TakePullAnswer("c", new ClipboardPullResponseMessage(toC.PullId, "from c"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fromA.Superseded, Is.Empty);
            Assert.That(fromC.Taken, Is.True);
            Assert.That(sync.Text, Is.EqualTo("from c"));
        }
    }

    [Test]
    public void MasterPulledTwoHosts_TheNewerChangesTheClipboard_SupersedesTheOlder()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        var toA = master.Pull("a");
        var toC = master.Pull("c");

        var fromC = master.TakePullAnswer("c", new ClipboardPullResponseMessage(toC.PullId, "from c"));
        var fromA = master.TakePullAnswer("a", new ClipboardPullResponseMessage(toA.PullId, "from a"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fromC.Superseded, Is.EqualTo([new OutstandingPull("a", toA.PullId)]));
            Assert.That(fromA.Taken, Is.False);
            Assert.That(sync.Text, Is.EqualTo("from c"));
            Assert.That(master.Outstanding, Is.Empty);
        }
    }

    [Test]
    public void MasterPull_AnOlderIdOrAnotherHost_IsIgnored()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        var first = master.Pull("a");
        var latest = master.Pull("a");

        var stale = master.TakePullAnswer("a", new ClipboardPullResponseMessage(first.PullId, "stale"));
        var elsewhere = master.TakePullAnswer("c", new ClipboardPullResponseMessage(latest.PullId, "from c"));
        var outstanding = master.Outstanding;
        var answer = master.TakePullAnswer("a", new ClipboardPullResponseMessage(latest.PullId, "latest"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stale.Taken, Is.False);
            Assert.That(elsewhere.Taken, Is.False);
            Assert.That(outstanding, Is.EqualTo([new OutstandingPull("a", latest.PullId)]));
            Assert.That(answer.Taken, Is.True);
            Assert.That(sync.Text, Is.EqualTo("latest"));
        }
    }

    [Test]
    public void MasterForgetPull_OnlyThatHostsPullIsForgotten()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        var toA = master.Pull("a");
        var toC = master.Pull("c");

        master.ForgetPull("a");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(master.TakePullAnswer("a", new ClipboardPullResponseMessage(toA.PullId, "from a")).Taken, Is.False);
            Assert.That(master.Outstanding, Is.EqualTo([new OutstandingPull("c", toC.PullId)]));
        }
    }

    // -- a pull while host's answer streams in, and a copy on the master since the pull --

    private const ulong StreamingHash = 0x5eed;

    [Test]
    public void MasterPull_WhileHostsAnswerStreams_CarriesTheStreamsHash()
    {
        var master = new ClipboardMaster(new FakeClipboardSync(), Log);
        var first = master.Pull("a");
        master.Streaming(new OutstandingPull("a", first.PullId));

        var second = master.Pull("a", StreamingHash);

        Assert.That(second.MasterHash, Is.EqualTo(StreamingHash));
    }

    [Test]
    public void MasterPull_NothingStreaming_IgnoresAStaleHash()
    {
        var sync = new FakeClipboardSync();
        sync.SetupText("on the master");
        var master = new ClipboardMaster(sync, Log);
        // the entry's read, which the pull goes against
        master.Hash();

        var pull = master.Pull("a", StreamingHash);

        Assert.That(pull.MasterHash, Is.EqualTo(ClipboardUtils.ClipboardHash(new ClipboardSnapshot("on the master", null, null))));
    }

    [Test]
    public void MasterStreamingAnswer_ANewerPullAnsweredUnchanged_TheStreamStillLandsAsItsOwnPullsAnswer()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        var first = master.Pull("a");
        master.Streaming(new OutstandingPull("a", first.PullId));
        var second = master.Pull("a", StreamingHash);

        var unchanged = master.TakePullAnswer("a", new ClipboardPullResponseMessage(second.PullId, null, Unchanged: true));
        var outstanding = master.Outstanding;
        var landed = master.TakePullAnswer("a", new ClipboardPullResponseMessage(first.PullId, "streamed from a"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unchanged.Taken, Is.True);
            Assert.That(unchanged.Applied, Is.Null);
            Assert.That(outstanding, Is.EqualTo([new OutstandingPull("a", first.PullId)]), "the stream's pull is still answered by it");
            Assert.That(landed.Taken, Is.True);
            Assert.That(sync.Text, Is.EqualTo("streamed from a"));
            Assert.That(master.Outstanding, Is.Empty);
        }
    }

    [Test]
    public void MasterStreamingAnswer_ANewerPullAnsweredWithAChange_SupersedesTheStream()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        var first = master.Pull("a");
        master.Streaming(new OutstandingPull("a", first.PullId));
        var second = master.Pull("a", StreamingHash);

        var newer = master.TakePullAnswer("a", new ClipboardPullResponseMessage(second.PullId, "copied on a since"));
        var older = master.TakePullAnswer("a", new ClipboardPullResponseMessage(first.PullId, "streamed from a"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(newer.Superseded, Is.EqualTo([new OutstandingPull("a", first.PullId)]));
            Assert.That(older.Taken, Is.False);
            Assert.That(sync.Text, Is.EqualTo("copied on a since"));
        }
    }

    [Test]
    public void MasterPull_WhileABeginIsStillBeingTaken_TheStreamStillLandsAsItsOwnPullsAnswer()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        var first = master.Pull("a");
        // the Begin for the first pull is registered with the receiver (so its hash is pending), and a newer pull goes
        // out before the router has called Streaming for it
        var answering = master.StreamFor("a", first.PullId);
        var second = master.Pull("a", StreamingHash);
        var superseded = master.Streaming(answering!);

        var unchanged = master.TakePullAnswer("a", new ClipboardPullResponseMessage(second.PullId, null, Unchanged: true));
        var landed = master.TakePullAnswer("a", new ClipboardPullResponseMessage(first.PullId, "streamed from a"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.MasterHash, Is.EqualTo(StreamingHash), "the newer pull carries the stream's hash");
            Assert.That(superseded, Is.Empty);
            Assert.That(unchanged.Taken, Is.True);
            Assert.That(landed.Taken, Is.True, "the stream's pull is still answered by it");
            Assert.That(sync.Text, Is.EqualTo("streamed from a"));
            Assert.That(master.Outstanding, Is.Empty);
        }
    }

    [Test]
    public void MasterPull_ABeginNotedStreamingWhileThePullIsMade_TheStreamStillLandsAsItsOwnPullsAnswer()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        var first = master.Pull("a");
        // the router asks the receiver for the pending stream through the pull itself, so a Begin for the first pull
        // taken and noted Streaming before the pull is made is seen by it, never read as nothing streaming
        master.Streaming(new OutstandingPull("a", first.PullId));
        var askedUnderTheLock = false;
        var second = master.Pull("a", _ =>
        {
            askedUnderTheLock = master.Locked;
            return StreamingHash;
        });

        var unchanged = master.TakePullAnswer("a", new ClipboardPullResponseMessage(second.PullId, null, Unchanged: true));
        var landed = master.TakePullAnswer("a", new ClipboardPullResponseMessage(first.PullId, "streamed from a"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(askedUnderTheLock, Is.True, "the pending stream is read with the pull, so no Begin noted between them is undone");
            Assert.That(second.MasterHash, Is.EqualTo(StreamingHash), "the newer pull carries the stream's hash");
            Assert.That(unchanged.Taken, Is.True);
            Assert.That(landed.Taken, Is.True, "the stream's pull is still answered by it");
            Assert.That(sync.Text, Is.EqualTo("streamed from a"));
            Assert.That(master.Outstanding, Is.Empty);
        }
    }

    // the receiver removed the completed stream, so nothing is pending from a, before its answer is taken: a pull made
    // between them still leaves the stream's pull streaming, and the stream lands once the newer pull finds nothing newer
    [Test]
    public void MasterPull_BetweenAStreamsCompletionAndItsAnswer_TheStreamStillLands()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        var first = master.Pull("a");
        var answering = master.StreamFor("a", first.PullId);
        master.Streaming(answering!);

        var second = master.Pull("a", _ => null);
        var held = master.TakePullAnswer("a", new ClipboardPullResponseMessage(first.PullId, "streamed from a"));
        var landed = master.TakePullAnswer("a", new ClipboardPullResponseMessage(second.PullId, null, Unchanged: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(held.Taken, Is.True, "the stream's pull is still answered by it");
            Assert.That(landed.Applied?.Text, Is.EqualTo("streamed from a"));
            Assert.That(sync.Text, Is.EqualTo("streamed from a"));
            Assert.That(master.Outstanding, Is.Empty);
        }
    }

    // a Begin accepted for the first pull, not yet registered with the receiver (so nothing is pending from a), when a
    // newer pull goes out: the accepted Begin's pull still streams in, and its answer lands
    [Test]
    public void MasterPull_BetweenABeginsAcceptAndItsRegistration_TheStreamStillLands()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        var first = master.Pull("a");
        var answering = master.StreamFor("a", first.PullId);

        var second = master.Pull("a", _ => null);
        var superseded = master.Streaming(answering!);
        var outstanding = master.Outstanding;
        var held = master.TakePullAnswer("a", new ClipboardPullResponseMessage(first.PullId, "streamed from a"));
        var landed = master.TakePullAnswer("a", new ClipboardPullResponseMessage(second.PullId, null, Unchanged: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(superseded, Is.Empty);
            Assert.That(outstanding, Is.EqualTo([new OutstandingPull("a", first.PullId), new OutstandingPull("a", second.PullId)]));
            Assert.That(held.Taken, Is.True, "the stream's pull is still answered by it");
            Assert.That(landed.Applied?.Text, Is.EqualTo("streamed from a"));
            Assert.That(sync.Text, Is.EqualTo("streamed from a"));
            Assert.That(master.Outstanding, Is.Empty);
        }
    }

    [Test]
    public void MasterStreamFor_NotesThePullStreaming_UntilItsStreamEnds()
    {
        var master = new ClipboardMaster(new FakeClipboardSync(), Log);
        var first = master.Pull("a");
        master.StreamFor("a", first.PullId);
        var second = master.Pull("a", _ => StreamingHash);
        var outstanding = master.Outstanding;

        master.StreamEnded("a", first.PullId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.MasterHash, Is.EqualTo(StreamingHash), "the newer pull carries the accepted stream's hash");
            Assert.That(outstanding, Is.EqualTo([new OutstandingPull("a", first.PullId), new OutstandingPull("a", second.PullId)]));
            Assert.That(master.Outstanding, Is.EqualTo([new OutstandingPull("a", second.PullId)]));
        }
    }

    [Test]
    public void MasterStreamFor_ThenRefused_LeavesNothingOutstanding()
    {
        var sync = new FakeClipboardSync();
        sync.SetupText("pulled against");
        var master = new ClipboardMaster(sync, Log);
        var pull = master.Pull("a");
        var answering = master.StreamFor("a", pull.PullId);

        sync.SetupText("copied on the master");
        var refused = master.RefuseStream(answering!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refused, Is.True);
            Assert.That(master.Outstanding, Is.Empty);
        }
    }

    [Test]
    public void MasterStreamFor_TheBeginsPullId_NotTheLatest()
    {
        var master = new ClipboardMaster(new FakeClipboardSync(), Log);
        var first = master.Pull("a");
        master.Streaming(new OutstandingPull("a", first.PullId));
        var second = master.Pull("a", StreamingHash);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(master.StreamFor("a", first.PullId), Is.EqualTo(new OutstandingPull("a", first.PullId)));
            Assert.That(master.StreamFor("a", second.PullId), Is.EqualTo(new OutstandingPull("a", second.PullId)));
            Assert.That(master.StreamFor("a", second.PullId + 1), Is.Null);
            Assert.That(master.StreamFor("c", first.PullId), Is.Null);
        }
    }

    [Test]
    public void MasterPullAnswer_AfterACopyOnTheMaster_IsIgnored()
    {
        var sync = new FakeClipboardSync();
        sync.SetupText("pulled against");
        var master = new ClipboardMaster(sync, Log);
        var pull = master.Pull("a");

        sync.SetupText("copied on the master");
        var taken = master.TakePullAnswer("a", new ClipboardPullResponseMessage(pull.PullId, "from a"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(taken.Taken, Is.True, "the pull is resolved");
            Assert.That(taken.Applied, Is.Null);
            Assert.That(sync.Text, Is.EqualTo("copied on the master"));
            Assert.That(sync.SetClipboardCallCount, Is.Zero);
            Assert.That(master.Outstanding, Is.Empty);
        }
    }

    [Test]
    public void MasterPullAnswer_TheClipboardUnreadable_IsStillTaken()
    {
        var sync = new FakeClipboardSync();
        sync.SetupText("pulled against");
        var master = new ClipboardMaster(sync, Log);
        var pull = master.Pull("a");

        sync.SetupText("copied on the master");
        sync.ReadFailsValue = true;
        var taken = master.TakePullAnswer("a", new ClipboardPullResponseMessage(pull.PullId, "from a"));

        Assert.That(taken.Applied?.Text, Is.EqualTo("from a"));
    }

    [Test]
    public void MasterPullAnswer_TheClipboardStillHydrasOwnWrite_IsTaken()
    {
        var sync = new FakeClipboardSync();
        sync.SetupText("pulled against");
        var master = new ClipboardMaster(sync, Log);
        // the entry's read, which the pulls go against
        master.Hash();
        var toA = master.Pull("a");
        var toC = master.Pull("c");
        master.TakePullAnswer("a", new ClipboardPullResponseMessage(toA.PullId, "from a"));

        var fromC = master.TakePullAnswer("c", new ClipboardPullResponseMessage(toC.PullId, "from c"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fromC.Applied?.Text, Is.EqualTo("from c"), "a's answer is hydra's write, no user copy");
            Assert.That(sync.Text, Is.EqualTo("from c"));
        }
    }

    [Test]
    public void SlaveHashCheck_PendingFromTheMaster_MatchesTheClipboard()
    {
        var sync = new FakeClipboardSync();
        sync.SetupText("old");
        var offered = ClipboardWire.ToHash(new ClipboardSnapshot("streaming", null, null));
        var slave = new ClipboardSlave(sync, Log, host => host == "master" ? offered.Hash : null);

        Assert.That(slave.OnHash(offered, "master"), Is.EqualTo(PushNeed.None));
    }

    [Test]
    public void SlaveHashCheck_PendingFromTheMaster_AsksForTheSelectionAlone()
    {
        var sync = new FakeClipboardSync();
        sync.SetupText("old");
        var offered = ClipboardWire.ToHash(new ClipboardSnapshot("streaming", new string('s', 5000), null));
        var slave = new ClipboardSlave(sync, Log, host => host == "master" ? offered.Hash : null);

        Assert.That(slave.OnHash(offered, "master"), Is.EqualTo(PushNeed.Selection));
    }

    [Test]
    public void SlaveHashCheck_PendingFromTheMaster_ThePushThatFollows_IsAppliedWhole()
    {
        var sync = new FakeClipboardSync();
        var selection = new string('s', 5000);
        var offered = ClipboardWire.ToHash(new ClipboardSnapshot("streaming", selection, null));
        var slave = new ClipboardSlave(sync, Log, host => host == "master" ? offered.Hash : null);

        Assert.That(slave.OnHash(offered, "master"), Is.EqualTo(PushNeed.Selection));
        slave.OnPush(new ClipboardPushMessage("streaming", selection), "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sync.Text, Is.EqualTo("streaming"));
            Assert.That(sync.PrimaryText, Is.EqualTo(selection));
        }
    }

    [Test]
    public void SlaveHashCheck_PendingOfAnotherClipboard_AsksForTheClipboard()
    {
        var sync = new FakeClipboardSync();
        sync.SetupText("old");
        var offered = ClipboardWire.ToHash(new ClipboardSnapshot("streaming", null, null));
        var slave = new ClipboardSlave(sync, Log, _ => offered.Hash + 1);

        Assert.That(slave.OnHash(offered, "master"), Is.EqualTo(PushNeed.Clipboard));
    }

    [Test]
    public void SlaveHashCheck_UserCopiedAfterPending_KeepsTheCopyAndAsksForNothing()
    {
        var sync = new FakeClipboardSync();
        sync.SetupText("old");
        var slave = new ClipboardSlave(sync, Log, NothingPending);

        Assert.That(slave.OnHash(new ClipboardHashMessage(null, Pending: true), "master"), Is.EqualTo(PushNeed.None));
        sync.SetupText("copied here");
        sync.CanUseEchoFallbackValue = false;
        var need = slave.OnHash(ClipboardWire.ToHash(new ClipboardSnapshot("streamed", null, null)), "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(need, Is.EqualTo(PushNeed.None));
            Assert.That(sync.Text, Is.EqualTo("copied here"));
        }
        // the copy was kept once; a later hash is checked as usual
        Assert.That(slave.OnHash(ClipboardWire.ToHash(new ClipboardSnapshot("newer", null, null)), "master"), Is.EqualTo(PushNeed.Clipboard));
    }

    [Test]
    public void SlaveHashCheck_UserCopiedAfterPending_ADifferentSelection_IsAskedForAlone()
    {
        var sync = new FakeClipboardSync();
        var selection = new string('s', 5000);
        var slave = new ClipboardSlave(sync, Log, NothingPending);

        slave.OnHash(new ClipboardHashMessage(null, Pending: true), "master");
        sync.SetupText("copied here");
        sync.CanUseEchoFallbackValue = false;

        Assert.That(slave.OnHash(ClipboardWire.ToHash(new ClipboardSnapshot("streamed", selection, null)), "master"), Is.EqualTo(PushNeed.Selection));
        slave.OnPush(new ClipboardPushMessage("streamed", selection), "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sync.Text, Is.EqualTo("copied here"));
            Assert.That(sync.PrimaryText, Is.EqualTo(selection));
        }
    }

    [Test]
    public void SlaveHashCheck_StillOursAfterPending_AsksForTheClipboard()
    {
        var sync = new FakeClipboardSync();
        sync.SetupText("old");
        var slave = new ClipboardSlave(sync, Log, NothingPending);

        slave.OnHash(new ClipboardHashMessage(null, Pending: true), "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(slave.OnHash(ClipboardWire.ToHash(new ClipboardSnapshot("streamed", null, null)), "master"), Is.EqualTo(PushNeed.Clipboard));
            Assert.That(sync.Text, Is.Null);
        }
    }

    [Test]
    public void SlavePull_PendingFromTheMaster_AnswersUnchanged()
    {
        var sync = new FakeClipboardSync();
        sync.SetupText("old");
        var pending = ClipboardUtils.ClipboardHash(new ClipboardSnapshot("streaming", null, null));
        var slave = new ClipboardSlave(sync, Log, host => host == "master" ? pending : null);

        var answer = slave.OnPull(new ClipboardPullMessage(3, pending), "master");

        Assert.That(answer, Is.EqualTo(new ClipboardPullResponseMessage(3, null, Unchanged: true)));
    }

    [Test]
    public void SlavePull_PendingFromTheMaster_CarriesADifferentSelectionBeside()
    {
        var sync = new FakeClipboardSync();
        sync.SetupText("old");
        sync.SetPrimaryText("selected here");
        var pending = ClipboardUtils.ClipboardHash(new ClipboardSnapshot("streaming", null, null));
        var slave = new ClipboardSlave(sync, Log, _ => pending);

        var answer = slave.OnPull(new ClipboardPullMessage(3, pending, ClipboardUtils.StableHash("selected there")), "master");

        Assert.That(answer, Is.EqualTo(new ClipboardPullResponseMessage(3, null, "selected here", Unchanged: true)));
    }

    [Test]
    public void SlavePull_PendingOfAnotherClipboard_AnswersWithTheClipboard()
    {
        var sync = new FakeClipboardSync();
        sync.SetupText("old");
        var pending = ClipboardUtils.ClipboardHash(new ClipboardSnapshot("streaming", null, null));
        var slave = new ClipboardSlave(sync, Log, _ => pending + 1);

        var answer = slave.OnPull(new ClipboardPullMessage(3, pending), "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(answer.Unchanged, Is.Null);
            Assert.That(answer.Text, Is.EqualTo("old"));
        }
    }

    [Test]
    public void SlavePull_PendingFromTheMaster_UserCopiedSince_AnswersWithTheCopy()
    {
        var sync = new FakeClipboardSync();
        var pending = ClipboardUtils.ClipboardHash(new ClipboardSnapshot("streaming", null, null));
        var slave = new ClipboardSlave(sync, Log, _ => pending);
        slave.Begun("master", pending);
        sync.SetupText("copied here");
        sync.CanUseEchoFallbackValue = false;

        var answer = slave.OnPull(new ClipboardPullMessage(3, pending), "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(answer.Unchanged, Is.Null);
            Assert.That(answer.Text, Is.EqualTo("copied here"));
        }
    }

    // -- nothing copied here since Hydra's write: a pull finds nothing new --

    [Test]
    public void SlavePull_StillHydrasWrite_AnswersUnchangedWhateverTheHash()
    {
        var sync = new FakeClipboardSync();
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        slave.OnPush(new ClipboardPushMessage("pushed"), "master");

        var answer = slave.OnPull(new ClipboardPullMessage(3, ClipboardUtils.StableHash("another")), "master");

        Assert.That(answer, Is.EqualTo(new ClipboardPullResponseMessage(3, null, Unchanged: true)));
    }

    [Test]
    public void SlavePull_StillHydrasEmptying_AnswersUnchanged()
    {
        var sync = new FakeClipboardSync();
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        slave.Empty();

        var answer = slave.OnPull(new ClipboardPullMessage(3, ClipboardUtils.StableHash("the master's")), "master");

        Assert.That(answer, Is.EqualTo(new ClipboardPullResponseMessage(3, null, Unchanged: true)));
    }

    [Test]
    public void SlavePull_StillHydrasWrite_CarriesADifferentSelectionBeside()
    {
        var sync = new FakeClipboardSync();
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        slave.OnPush(new ClipboardPushMessage("pushed"), "master");
        sync.SetPrimaryText("selected here");

        var answer = slave.OnPull(new ClipboardPullMessage(3, ClipboardUtils.StableHash("another"), ClipboardUtils.StableHash("selected there")), "master");

        Assert.That(answer, Is.EqualTo(new ClipboardPullResponseMessage(3, null, "selected here", Unchanged: true)));
    }

    [Test]
    public void SlavePull_UserCopiedSinceHydrasWrite_AnswersWithTheCopy()
    {
        var sync = new FakeClipboardSync();
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        slave.OnPush(new ClipboardPushMessage("pushed"), "master");
        sync.SetupText("copied here");
        sync.CanUseEchoFallbackValue = false;

        var answer = slave.OnPull(new ClipboardPullMessage(3, ClipboardUtils.StableHash("another")), "master");

        Assert.That(answer.Text, Is.EqualTo("copied here"));
    }

    // -- a copy already synced with the master is nothing new, Hydra's write or not --

    [Test]
    public void SlavePull_TheUsersCopyAlreadyAnswered_AFlickAnswersUnchanged()
    {
        var sync = new FakeClipboardSync();
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        sync.SetupText("copied here");
        sync.CanUseEchoFallbackValue = false;
        Assert.That(slave.OnPull(new ClipboardPullMessage(1, ClipboardUtils.StableHash("older")), "master").Text, Is.EqualTo("copied here"));

        // the master copied newer since; a flick across here brings no hash, only the pull on leaving
        slave.Entered("master");
        var answer = slave.OnPull(new ClipboardPullMessage(2, ClipboardUtils.StableHash("newer")), "master");

        Assert.That(answer, Is.EqualTo(new ClipboardPullResponseMessage(2, null, Unchanged: true)));
    }

    [Test]
    public void SlavePull_TheUsersCopyAnsweredToAnotherMaster_IsStillNewToThisOne()
    {
        var sync = new FakeClipboardSync();
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        sync.SetupText("copied here");
        sync.CanUseEchoFallbackValue = false;
        slave.OnPull(new ClipboardPullMessage(1, ClipboardUtils.StableHash("older")), "other");

        var answer = slave.OnPull(new ClipboardPullMessage(2, ClipboardUtils.StableHash("newer")), "master");

        Assert.That(answer.Text, Is.EqualTo("copied here"));
    }

    // a deliberate re-copy after the sync is a new copy, which the change counter tells from Hydra's write
    [Test]
    public void SlavePull_ThePushedClipboardRecopiedSinceTheSync_AnswersInFull()
    {
        var sync = new FakeClipboardSync();
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        slave.OnPush(new ClipboardPushMessage("pushed"), "master");
        // the user copies the same content again, no longer Hydra's write
        sync.SetupText("pushed");
        sync.CanUseEchoFallbackValue = false;

        var answer = slave.OnPull(new ClipboardPullMessage(3, ClipboardUtils.StableHash("newer")), "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(answer.Unchanged, Is.Null);
            Assert.That(answer.Text, Is.EqualTo("pushed"));
        }
    }

    // -- "copied since" by the clipboard's change counter, never a stale baseline or matching content --

    [Test]
    public void SlavePull_TheAnsweredCopyRecopiedNextVisit_AnswersInFull()
    {
        var sync = new FakeClipboardSync();
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        sync.CanUseEchoFallbackValue = false;
        sync.SetupText("copied here");
        slave.OnPull(new ClipboardPullMessage(1, ClipboardUtils.StableHash("older")), "master");

        slave.Entered("master");
        sync.SetupText("copied here");
        var answer = slave.OnPull(new ClipboardPullMessage(2, ClipboardUtils.StableHash("newer")), "master");

        Assert.That(answer.Text, Is.EqualTo("copied here"));
    }

    [Test]
    public void SlaveHash_LateOfferAfterAnIdenticalRecopy_KeepsTheCopy()
    {
        var sync = new FakeClipboardSync();
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        slave.Entered("master");
        slave.OnHash(ClipboardWire.ToHash(new ClipboardSnapshot("entry", null, null)), "master");
        slave.OnPush(new ClipboardPushMessage("entry"), "master");
        sync.CanUseEchoFallbackValue = false;
        sync.SetupText("entry");

        var need = slave.OnHash(ClipboardWire.ToHash(new ClipboardSnapshot("late", null, null)), "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(need, Is.EqualTo(PushNeed.None));
            Assert.That(sync.Text, Is.EqualTo("entry"));
        }
    }

    // the placeholder in place of a concealed push whose markers were refused is an empty clipboard here
    [Test]
    public void SlavePush_ConcealedMarkersRefused_IsAnEmptyAndAFailure()
    {
        var sync = new FakeClipboardSync();
        var log = new CapturingLog<ClipboardSlave>();
        var slave = new ClipboardSlave(sync, log, NothingPending);
        slave.Entered("master");
        slave.OnPush(new ClipboardPushMessage("older"), "master");
        sync.RefusesMarkersValue = true;
        var at = sync.ChangeCount();

        var failed = slave.OnPush(new ClipboardPushMessage("hunter2", Concealed: true), "master");
        var need = slave.OnHash(ClipboardWire.ToHash(new ClipboardSnapshot("older", null, null)), "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failed, Is.True);
            Assert.That(need, Is.EqualTo(PushNeed.Clipboard), "empty here, never the older copy");
            Assert.That(ClipboardChanges.Of(sync).CopiedSince(at), Is.False, "Hydra's own write");
            Assert.That(log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message), Has.Some.Contains("master").And.Contains("concealed"));
        }
    }

    // a push that could not be written is a failure, shown here
    [Test]
    public void SlavePush_CannotBeWritten_IsAFailure()
    {
        var sync = new FakeClipboardSync { WriteFailsValue = true };
        var log = new CapturingLog<ClipboardSlave>();
        var slave = new ClipboardSlave(sync, log, NothingPending);

        var failed = slave.OnPush(new ClipboardPushMessage("x"), "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failed, Is.True);
            var warnings = log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message);
            Assert.That(warnings, Has.Some.Contains("master").And.Contains("could not be written"));
        }
    }

    // a push whose every format the clipboard refused left it empty: a failure too
    [Test]
    public void SlavePush_EveryFormatRefused_IsAFailure()
    {
        var sync = new FakeClipboardSync();
        sync.RefuseEveryFormat();
        var log = new CapturingLog<ClipboardSlave>();
        var slave = new ClipboardSlave(sync, log, NothingPending);

        var failed = slave.OnPush(new ClipboardPushMessage("x"), "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failed, Is.True);
            var warnings = log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message);
            Assert.That(warnings, Has.Some.Contains("master").And.Contains("could not be written"));
        }
    }

    // a hash whose clear failed still marks where it stood, so a copy made since outranks the push
    [Test]
    public void SlaveHash_ItsClearFails_ACopySinceStillOutranksThePush()
    {
        var sync = new FakeClipboardSync { ClearFailsValue = true };
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        slave.Entered("master");
        slave.OnHash(ClipboardWire.ToHash(new ClipboardSnapshot("entry", null, null)), "master");
        sync.SetupText("copied here");

        Assert.That(slave.CopiedSinceHash("master"), Is.True);
    }

    [Test]
    public void Slave_HydrasOwnWritesAndClears_NeverCountAsACopy()
    {
        var sync = new FakeClipboardSync();
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        slave.Entered("master");
        slave.OnHash(ClipboardWire.ToHash(new ClipboardSnapshot("entry", null, null)), "master");
        var refused = slave.CopiedSinceHash("master");
        slave.OnPush(new ClipboardPushMessage("entry"), "master");
        slave.Empty();

        var need = slave.OnHash(ClipboardWire.ToHash(new ClipboardSnapshot("late", null, null)), "master");
        var answer = slave.OnPull(new ClipboardPullMessage(3, ClipboardUtils.StableHash("another")), "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refused, Is.False);
            Assert.That(need, Is.EqualTo(PushNeed.Clipboard));
            Assert.That(answer, Is.EqualTo(new ClipboardPullResponseMessage(3, null, Unchanged: true)));
        }
    }

    [Test]
    public void MasterPull_AgainstAStaleBaseline_TheSlavesNewerCopyWins()
    {
        var sync = new FakeClipboardSync();
        sync.SetupText("old on the master");
        var master = new ClipboardMaster(sync, Log);
        master.Hash();
        // b's answer streams in, emptying the master
        var toB = master.Pull("b");
        master.Streaming(new OutstandingPull("b", toB.PullId));
        master.Begun("b");
        // the user copies y on the master; the cursor enters a with its entry hash deferred (no read), the user copies z
        // there and leaves
        sync.CanUseEchoFallbackValue = false;
        sync.SetupText("y");
        var toA = master.Pull("a");

        var refused = master.RefuseStream(new OutstandingPull("a", toA.PullId));
        var fromA = master.TakePullAnswer("a", new ClipboardPullResponseMessage(toA.PullId, "z"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refused, Is.False);
            Assert.That(fromA.Applied?.Text, Is.EqualTo("z"));
            Assert.That(sync.Text, Is.EqualTo("z"));
        }
    }

    [Test]
    public void MasterPull_AnIdenticalRecopyOfHydrasWriteSinceThePull_Wins()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        var toA = master.Pull("a");
        master.TakePullAnswer("a", new ClipboardPullResponseMessage(toA.PullId, "from a"));
        var toB = master.Pull("b");
        var toC = master.Pull("c");
        sync.CanUseEchoFallbackValue = false;
        sync.SetupText("from a");

        var refused = master.RefuseStream(new OutstandingPull("b", toB.PullId));
        var fromC = master.TakePullAnswer("c", new ClipboardPullResponseMessage(toC.PullId, "from c"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refused, Is.True);
            Assert.That(fromC.Applied, Is.Null);
            Assert.That(sync.Text, Is.EqualTo("from a"));
        }
    }

    [Test]
    public void MasterPull_HydrasOwnWritesAndClearsSinceThePull_NeverCountAsACopy()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        var toA = master.Pull("a");
        var toB = master.Pull("b");
        master.TakePullAnswer("a", new ClipboardPullResponseMessage(toA.PullId, "from a"));
        master.Empty();

        var refused = master.RefuseStream(new OutstandingPull("b", toB.PullId));
        _ = master.TakePullAnswer("b", new ClipboardPullResponseMessage(toB.PullId, "from b"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refused, Is.False);
            Assert.That(sync.Text, Is.EqualTo("from b"));
        }
    }

    [Test]
    public void MasterPull_NoChangeCounter_FallsBackToTheHashes()
    {
        var sync = new FakeClipboardSync { ChangeCountSupported = false };
        var master = new ClipboardMaster(sync, Log);
        var toA = master.Pull("a");
        sync.CanUseEchoFallbackValue = false;
        sync.SetupText("copied on the master");

        master.TakePullAnswer("a", new ClipboardPullResponseMessage(toA.PullId, "from a"));

        Assert.That(sync.Text, Is.EqualTo("copied on the master"));
    }

    // -- a hash that differs empties the clipboard before its push --

    [Test]
    public void SlaveHash_ClipboardDiffers_EmptiesRightAway()
    {
        var sync = new FakeClipboardSync();
        sync.SetupText("old");
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        slave.Entered("master");

        var need = slave.OnHash(ClipboardWire.ToHash(new ClipboardSnapshot("the master's", null, null)), "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(need, Is.EqualTo(PushNeed.Clipboard));
            Assert.That(sync.ClearClipboardCallCount, Is.EqualTo(1));
            Assert.That(sync.Text, Is.Null);
        }
    }

    [Test]
    public void SlaveHash_ClipboardDiffers_CopiedFilesAreKept()
    {
        var sync = new FakeClipboardSync { HasFileClipboardValue = true };
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        slave.Entered("master");

        slave.OnHash(ClipboardWire.ToHash(new ClipboardSnapshot("the master's", null, null)), "master");

        Assert.That(sync.ClearClipboardCallCount, Is.Zero);
    }

    [Test]
    public void SlaveHash_OnlyTheSelectionDiffers_LeavesTheClipboard()
    {
        var sync = new FakeClipboardSync();
        sync.SetupText("same");
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        slave.Entered("master");

        var need = slave.OnHash(ClipboardWire.ToHash(new ClipboardSnapshot("same", new string('s', 5000), null)), "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(need, Is.EqualTo(PushNeed.Selection));
            Assert.That(sync.ClearClipboardCallCount, Is.Zero);
            Assert.That(sync.Text, Is.EqualTo("same"));
        }
    }

    // -- a late offer never beats a copy made here since the entry --

    [Test]
    public void SlaveHash_LateOfferAfterAUserCopy_KeepsTheCopy()
    {
        var sync = new FakeClipboardSync();
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        slave.Entered("master");
        Assert.That(slave.OnHash(ClipboardWire.ToHash(new ClipboardSnapshot("entry", null, null)), "master"), Is.EqualTo(PushNeed.Clipboard));
        slave.OnPush(new ClipboardPushMessage("entry"), "master");
        sync.SetupText("copied here");
        sync.CanUseEchoFallbackValue = false;

        var need = slave.OnHash(ClipboardWire.ToHash(new ClipboardSnapshot("late", null, null)), "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(need, Is.EqualTo(PushNeed.None));
            Assert.That(sync.Text, Is.EqualTo("copied here"));
        }
    }

    [Test]
    public void SlaveHash_LateOfferAfterAUserCopy_ADifferentSelection_IsAskedForAlone()
    {
        var sync = new FakeClipboardSync();
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        slave.Entered("master");
        slave.OnHash(ClipboardWire.ToHash(new ClipboardSnapshot("entry", null, null)), "master");
        slave.OnPush(new ClipboardPushMessage("entry"), "master");
        sync.SetupText("copied here");
        sync.CanUseEchoFallbackValue = false;
        var selection = new string('s', 5000);

        Assert.That(slave.OnHash(ClipboardWire.ToHash(new ClipboardSnapshot("late", selection, null)), "master"), Is.EqualTo(PushNeed.Selection));
        slave.OnPush(new ClipboardPushMessage("late", selection), "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sync.Text, Is.EqualTo("copied here"));
            Assert.That(sync.PrimaryText, Is.EqualTo(selection));
        }
    }

    [Test]
    public void SlaveHash_LateOfferStillOurs_AsksForTheClipboard()
    {
        var sync = new FakeClipboardSync();
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        slave.Entered("master");
        slave.OnHash(ClipboardWire.ToHash(new ClipboardSnapshot("entry", null, null)), "master");
        slave.OnPush(new ClipboardPushMessage("entry"), "master");

        Assert.That(slave.OnHash(ClipboardWire.ToHash(new ClipboardSnapshot("late", null, null)), "master"), Is.EqualTo(PushNeed.Clipboard));
    }

    [Test]
    public void SlaveHash_EntryOfferAfterAUserCopy_AsksForTheClipboard()
    {
        var sync = new FakeClipboardSync();
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        slave.Entered("master");
        slave.OnHash(ClipboardWire.ToHash(new ClipboardSnapshot("entry", null, null)), "master");
        slave.OnPush(new ClipboardPushMessage("entry"), "master");
        sync.SetupText("copied here");
        sync.CanUseEchoFallbackValue = false;

        slave.Entered("master");

        Assert.That(slave.OnHash(ClipboardWire.ToHash(new ClipboardSnapshot("newer", null, null)), "master"), Is.EqualTo(PushNeed.Clipboard));
    }

    // the first hash of a visit may come late too (deferred), after the user copied here since the entry
    [Test]
    public void SlaveHash_FirstHashAfterAUserCopySinceTheEntry_KeepsTheCopy()
    {
        var sync = new FakeClipboardSync();
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        slave.OnPush(new ClipboardPushMessage("pushed"), "master");
        slave.Entered("master");
        sync.SetupText("copied here");
        sync.CanUseEchoFallbackValue = false;

        var need = slave.OnHash(ClipboardWire.ToHash(new ClipboardSnapshot("newer", null, null)), "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(need, Is.EqualTo(PushNeed.None));
            Assert.That(sync.Text, Is.EqualTo("copied here"));
            Assert.That(sync.ClearClipboardCallCount, Is.Zero);
        }
    }

    // -- an unreadable clipboard is not an empty one --

    [Test]
    public void SlavePull_UnreadableClipboard_AnswersUnchanged()
    {
        var sync = new FakeClipboardSync();
        sync.SetupText("the user's copy");
        sync.ReadFailsValue = true;
        var slave = new ClipboardSlave(sync, Log, NothingPending);

        var answer = slave.OnPull(new ClipboardPullMessage(4, ClipboardUtils.StableHash("anything")), "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(answer.PullId, Is.EqualTo(4));
            Assert.That(answer.Unchanged, Is.True);
            Assert.That(answer.Text, Is.Null);
            Assert.That(answer.PrimaryText, Is.Null);
        }
    }

    [Test]
    public void SlaveHash_UnreadableClipboard_AsksForNoPush()
    {
        var sync = new FakeClipboardSync();
        sync.SetupText("the user's copy");
        sync.ReadFailsValue = true;
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        var hash = ClipboardWire.ToHash(new ClipboardSnapshot("the master's copy", null, null));

        var need = slave.OnHash(hash, "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(need, Is.EqualTo(PushNeed.None));
            Assert.That(sync.SetClipboardCallCount, Is.Zero);
            Assert.That(sync.Text, Is.EqualTo("the user's copy"));
        }
    }

    [Test]
    public void SlaveHash_ReadableAgain_AsksForThePush()
    {
        var sync = new FakeClipboardSync { ReadFailsValue = true };
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        var hash = ClipboardWire.ToHash(new ClipboardSnapshot("the master's copy", null, null));
        slave.OnHash(hash, "master");

        sync.ReadFailsValue = false;

        Assert.That(slave.OnHash(hash, "master"), Is.EqualTo(PushNeed.Clipboard));
    }

    [Test]
    public void Master_UnreadableClipboard_OffersNothing()
    {
        var sync = new FakeClipboardSync();
        sync.SetupText("copied on the master");
        sync.ReadFailsValue = true;
        var master = new ClipboardMaster(sync, Log);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(master.Hash(), Is.Null);
            Assert.That(master.Push(), Is.Null);
        }
    }

    [Test]
    public void MasterPull_UnreadableClipboard_PullsAgainstTheLastKnownOne()
    {
        var sync = new FakeClipboardSync();
        sync.SetupText("copied on the master");
        var master = new ClipboardMaster(sync, Log);
        Assert.That(master.Hash(), Is.Not.Null);

        sync.ReadFailsValue = true;
        var pull = master.Pull("slave");

        Assert.That(pull.MasterHash, Is.EqualTo(ClipboardUtils.ClipboardHash(new ClipboardSnapshot("copied on the master", null, null))));
    }

    [Test]
    public void MasterPull_UnreadableClipboard_AnUnchangedAnswerChangesNothing()
    {
        var sync = new FakeClipboardSync();
        sync.SetupText("copied on the master");
        var master = new ClipboardMaster(sync, Log);
        master.Hash();
        sync.ReadFailsValue = true;
        var pull = master.Pull("slave");

        var taken = master.TakePullAnswer("slave", new ClipboardPullResponseMessage(pull.PullId, null, Unchanged: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(taken.Taken, Is.True);
            Assert.That(taken.Applied, Is.Null);
            Assert.That(sync.SetClipboardCallCount, Is.Zero);
        }
    }

    // -- a partially refused write is not a complete one --

    [Test]
    public void SlavePush_RefusedFormat_IsNotRemembered()
    {
        // the fake reads nothing back of Hydra's write, so a read is the echo fallback
        var sync = new FakeClipboardSync { HidesOwnWriteValue = true };
        sync.RefusedFormats.Add(ClipboardFormat.Html);
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        var pushed = new ClipboardSnapshot("plain", null, null, Html: "<b>rich</b>");

        slave.OnPush(ClipboardWire.ToPush(pushed), "master");
        var answer = slave.OnPull(new ClipboardPullMessage(1, ClipboardUtils.ClipboardHash(pushed)), "master");
        slave.Entered("master");
        var need = slave.OnHash(ClipboardWire.ToHash(pushed), "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sync.Html, Is.Null);
            Assert.That(answer, Is.EqualTo(new ClipboardPullResponseMessage(1, null, Unchanged: true)), "nothing was copied here");
            Assert.That(need, Is.EqualTo(PushNeed.Clipboard), "the clipboard here lacks the html, so it differs from the master's");
        }
    }

    [Test]
    public void SlaveHash_AfterARefusedFormat_AsksForThePushAgain()
    {
        var sync = new FakeClipboardSync { HidesOwnWriteValue = true };
        sync.RefusedFormats.Add(ClipboardFormat.Html);
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        var pushed = new ClipboardSnapshot("plain", null, null, Html: "<b>rich</b>");
        slave.OnPush(ClipboardWire.ToPush(pushed), "master");

        Assert.That(slave.OnHash(ClipboardWire.ToHash(pushed), "master"), Is.EqualTo(PushNeed.Clipboard));
    }

    [Test]
    public void SlaveHash_AfterACompleteWrite_Matches()
    {
        var sync = new FakeClipboardSync { HidesOwnWriteValue = true };
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        var pushed = new ClipboardSnapshot("plain", null, null, Html: "<b>rich</b>");
        slave.OnPush(ClipboardWire.ToPush(pushed), "master");

        Assert.That(slave.OnHash(ClipboardWire.ToHash(pushed), "master"), Is.EqualTo(PushNeed.None));
    }

    // -- a concealed copy cleared at its source is cleared where it was relayed --

    private static readonly ClipboardSnapshot Password = new("hunter2", null, null, Concealed: true);

    [Test]
    public void SlaveConcealedPush_ClearedHash_ClearsHydrasWriteOfIt()
    {
        var sync = new FakeClipboardSync { HidesOwnWriteValue = true };
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        slave.OnPush(ClipboardWire.ToPush(Password), "master");
        Assert.That(sync.ConcealedValue, Is.True, "written with the markers");

        slave.Entered("master");
        var need = slave.OnHash(new ClipboardHashMessage(null, Cleared: true), "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(need, Is.EqualTo(PushNeed.None));
            Assert.That(sync.ClearClipboardCallCount, Is.EqualTo(1));
            Assert.That(sync.Text, Is.Null);
        }
    }

    [Test]
    public void SlaveConcealedPush_ThenTheUsersCopy_ClearedHashLeavesIt()
    {
        var sync = new FakeClipboardSync();
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        slave.OnPush(ClipboardWire.ToPush(Password), "master");
        sync.ConcealedValue = false;
        sync.SetupText("copied here");

        slave.Entered("master");
        slave.OnHash(new ClipboardHashMessage(null, Cleared: true), "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sync.ClearClipboardCallCount, Is.Zero);
            Assert.That(sync.Text, Is.EqualTo("copied here"));
        }
    }

    [Test]
    public void SlavePlainPush_ClearedHashLeavesIt()
    {
        var sync = new FakeClipboardSync { HidesOwnWriteValue = true };
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        slave.OnPush(new ClipboardPushMessage("plain"), "master");

        slave.Entered("master");
        slave.OnHash(new ClipboardHashMessage(null, Cleared: true), "master");

        Assert.That(sync.ClearClipboardCallCount, Is.Zero);
    }

    [Test]
    public void SlaveAnsweredConcealed_ThenEmptied_AnswersCleared_Once()
    {
        var sync = new FakeClipboardSync { ConcealedValue = true };
        sync.SetupText("hunter2");
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        var first = slave.OnPull(new ClipboardPullMessage(1), "master");
        sync.ClearClipboard();
        sync.CanUseEchoFallbackValue = false;

        var second = slave.OnPull(new ClipboardPullMessage(2, ClipboardUtils.ClipboardHash(Password)), "master");
        var third = slave.OnPull(new ClipboardPullMessage(3, ClipboardUtils.ClipboardHash(Password)), "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Concealed, Is.True);
            Assert.That(second.Cleared, Is.True);
            Assert.That(third.Cleared, Is.Null, "told once");
        }
    }

    [Test]
    public void SlaveAnsweredConcealed_ThenHydraEmptiedIt_AnswersNoClear()
    {
        var sync = new FakeClipboardSync { ConcealedValue = true };
        sync.SetupText("hunter2");
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        slave.OnPull(new ClipboardPullMessage(1), "master");

        slave.Empty();
        var answer = slave.OnPull(new ClipboardPullMessage(2, ClipboardUtils.ClipboardHash(Password)), "master");

        Assert.That(answer.Cleared, Is.Null);
    }

    [Test]
    public void SlaveAnsweredPlain_ThenEmptied_AnswersNoClear()
    {
        var sync = new FakeClipboardSync();
        sync.SetupText("plain");
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        slave.OnPull(new ClipboardPullMessage(1), "master");
        sync.ClearClipboard();
        sync.CanUseEchoFallbackValue = false;

        var answer = slave.OnPull(new ClipboardPullMessage(2, ClipboardUtils.StableHash("x")), "master");

        Assert.That(answer.Cleared, Is.Null);
    }

    [Test]
    public void MasterConcealedAnswer_ThenACleared_ClearsAndTellsTheSlaves()
    {
        var sync = new FakeClipboardSync { HidesOwnWriteValue = true };
        var master = new ClipboardMaster(sync, Log);
        var first = master.Pull("a");
        master.TakePullAnswer("a", new ClipboardPullResponseMessage(first.PullId, "hunter2", Concealed: true));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sync.ConcealedValue, Is.True, "written with the markers");
            Assert.That(master.Push()?.Concealed, Is.True, "relayed concealed");
        }

        var second = master.Pull("a");
        var taken = master.TakePullAnswer("a", new ClipboardPullResponseMessage(second.PullId, null, Cleared: true));
        var hash = master.Hash();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(taken.Taken, Is.True);
            Assert.That(taken.Applied, Is.EqualTo(ClipboardSnapshot.Placeholder), "the clear is what it wrote, offered on");
            Assert.That(sync.ClearClipboardCallCount, Is.EqualTo(1));
            Assert.That(sync.Text, Is.Null);
            Assert.That(hash?.Cleared, Is.True);
            Assert.That(hash?.Hash, Is.Null);
            Assert.That(master.Push(), Is.Null);
        }
    }

    [Test]
    public void MasterPlainClipboard_ClearedAnswerLeavesIt()
    {
        var sync = new FakeClipboardSync { HidesOwnWriteValue = true };
        var master = new ClipboardMaster(sync, Log);
        var first = master.Pull("a");
        master.TakePullAnswer("a", new ClipboardPullResponseMessage(first.PullId, "plain"));

        var second = master.Pull("a");
        master.TakePullAnswer("a", new ClipboardPullResponseMessage(second.PullId, null, Cleared: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sync.ClearClipboardCallCount, Is.Zero);
            Assert.That(master.Hash()?.Hash, Is.Not.Null);
        }
    }

    [Test]
    public void MasterEmptyForAStream_TellsNoClear()
    {
        var sync = new FakeClipboardSync
        {
            ConcealedValue = true
        };
        sync.SetupText("hunter2");
        var master = new ClipboardMaster(sync, Log);
        Assert.That(master.Hash()?.Hash, Is.Not.Null);

        master.Empty();

        Assert.That(master.Hash(), Is.Null);
    }

    [Test]
    public void MasterConcealedClear_EmptyFails_Warns()
    {
        var sync = new FakeClipboardSync { HidesOwnWriteValue = true };
        var log = new CapturingLog<ClipboardMaster>();
        var master = new ClipboardMaster(sync, log);
        var first = master.Pull("a");
        master.TakePullAnswer("a", new ClipboardPullResponseMessage(first.PullId, "hunter2", Concealed: true));
        sync.ClearFailsValue = true;

        var second = master.Pull("a");
        master.TakePullAnswer("a", new ClipboardPullResponseMessage(second.PullId, null, Cleared: true));

        Assert.That(log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message), Has.Some.Contains("a").And.Contains("empt"));
    }

    // a's stream for the first pull completes after the second went out, so it is held for the second's answer: the second
    private static ClipboardPullMessage HoldStream(ClipboardMaster master, string? selection = null)
    {
        var first = master.Pull("a");
        master.Streaming(new OutstandingPull("a", first.PullId));
        var second = master.Pull("a", StreamingHash);
        var held = master.TakePullAnswer("a", new ClipboardPullResponseMessage(first.PullId, "x1 from a", selection));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(held.Taken, Is.True, "pre-condition: held");
            Assert.That(held.Applied, Is.Null, "pre-condition: held");
        }
        return second;
    }

    [Test]
    public void MasterHeldStream_ItsPullSupersededByBsCopy_NeverReplaysOverIt()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        HoldStream(master);

        var third = master.Pull("b");
        master.TakePullAnswer("b", new ClipboardPullResponseMessage(third.PullId, "y from b"));
        var fourth = master.Pull("a");
        var unchanged = master.TakePullAnswer("a", new ClipboardPullResponseMessage(fourth.PullId, null, Unchanged: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unchanged.Applied, Is.Null);
            Assert.That(sync.Text, Is.EqualTo("y from b"));
        }
    }

    // a further pull replacing the one the answer is held for carries its content, so a's unchanged answer lands it
    [Test]
    public void MasterHeldStream_AFurtherPullReplacesItsPull_CarriesItAndLandsOnUnchanged()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        var second = HoldStream(master);

        var third = master.Pull("a");
        var late = master.TakePullAnswer("a", new ClipboardPullResponseMessage(second.PullId, null, Unchanged: true));
        var landed = master.TakePullAnswer("a", new ClipboardPullResponseMessage(third.PullId, null, Unchanged: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(third.MasterHash, Is.EqualTo(ClipboardUtils.ClipboardHash(new ClipboardSnapshot("x1 from a", null, null))), "the held content");
            Assert.That(late.Taken, Is.False, "the replaced pull's answer");
            Assert.That(landed.Applied?.Text, Is.EqualTo("x1 from a"));
            Assert.That(sync.Text, Is.EqualTo("x1 from a"));
        }
    }

    [Test]
    public void MasterHeldStream_AFurtherPullAnsweredChanged_DropsIt()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        HoldStream(master);

        var third = master.Pull("a");
        master.TakePullAnswer("a", new ClipboardPullResponseMessage(third.PullId, "x2 from a"));
        var landed = master.ForgetPull("a");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sync.Text, Is.EqualTo("x2 from a"));
            Assert.That(landed, Is.Empty);
        }
    }

    [Test]
    public void MasterHeldStream_ItsPullAnsweredUnchanged_LandsWithTheNewerSelection()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        var second = HoldStream(master, "old selection");

        var landed = master.TakePullAnswer("a", new ClipboardPullResponseMessage(second.PullId, null, "new selection", Unchanged: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(landed.Taken, Is.True);
            Assert.That(sync.Text, Is.EqualTo("x1 from a"));
            Assert.That(sync.PrimaryText, Is.EqualTo("new selection"));
        }
    }

    // its own pull was answered, so a host leaving before answering the newer one lands it rather than lose it
    [Test]
    public void MasterHeldStream_ItsHostLeaves_LandsAsItsOwnPullsAnswer()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        sync.SetupText("old on the master");
        var first = master.Pull("a");
        master.Begun("a");
        master.Streaming(new OutstandingPull("a", first.PullId));
        master.Pull("a", StreamingHash);
        master.TakePullAnswer("a", new ClipboardPullResponseMessage(first.PullId, "x1 from a"));
        Assert.That(sync.Text, Is.Null, "pre-condition: held, the Begin's clear standing");

        var landed = master.ForgetPull("a");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sync.Text, Is.EqualTo("x1 from a"));
            Assert.That(landed.Select(l => l.Pull), Is.EqualTo([new OutstandingPull("a", first.PullId)]));
            Assert.That(landed.Select(l => l.Answer?.Applied?.Text), Is.EqualTo(["x1 from a"]));
            Assert.That(master.Outstanding, Is.Empty);
        }
    }

    [Test]
    public void MasterHeldStream_ItsHostLeavesAfterACopyOnTheMaster_KeepsTheCopy()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        HoldStream(master);
        sync.SetupText("copied on the master");

        var landed = master.ForgetPull("a");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(landed, Is.Empty);
            Assert.That(sync.Text, Is.EqualTo("copied on the master"));
        }
    }

    // b's pull went out after a's held one, so b's copy is newer
    [Test]
    public void MasterHeldStream_ItsHostLeavesAfterANewerCopyFromB_KeepsBs()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        var first = master.Pull("a");
        master.Streaming(new OutstandingPull("a", first.PullId));
        var toB = master.Pull("b");
        master.Pull("a", StreamingHash);
        var held = master.TakePullAnswer("a", new ClipboardPullResponseMessage(first.PullId, "x1 from a"));
        master.TakePullAnswer("b", new ClipboardPullResponseMessage(toB.PullId, "y from b"));
        Assert.That(held.Applied, Is.Null, "pre-condition: held");

        var landed = master.ForgetPull("a");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(landed, Is.Empty);
            Assert.That(sync.Text, Is.EqualTo("y from b"));
        }
    }

    [Test]
    public void MasterHeldStream_ItsHostLeaves_AndItCannotBeWritten_Warns()
    {
        var sync = new FakeClipboardSync();
        var log = new CapturingLog<ClipboardMaster>();
        var master = new ClipboardMaster(sync, log);
        HoldStream(master);
        sync.WriteFailsValue = true;

        var landed = master.ForgetPull("a");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(landed.Select(l => l.Answer), Is.EqualTo(new PullAnswer?[] { null }), "a failure");
            var warnings = log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message);
            Assert.That(warnings, Has.Some.Contains("a").And.Contains("could not be written"));
        }
    }

    // the placeholder in place of a concealed copy whose markers were refused is an empty clipboard, never the
    // older copy the echo fallback still holds
    [Test]
    public void MasterApply_ConcealedMarkersRefused_IsAnEmptyAndAFailure()
    {
        var sync = new FakeClipboardSync();
        var log = new CapturingLog<ClipboardMaster>();
        var master = new ClipboardMaster(sync, log);
        var first = master.Pull("a");
        master.TakePullAnswer("a", new ClipboardPullResponseMessage(first.PullId, "older from a"));
        sync.RefusesMarkersValue = true;
        var at = sync.ChangeCount();

        var second = master.Pull("a");
        var taken = master.TakePullAnswer("a", new ClipboardPullResponseMessage(second.PullId, "hunter2", Concealed: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(taken.Failed, Is.True);
            Assert.That(master.Hash(), Is.Null, "nothing to offer, never the older copy");
            Assert.That(master.Push(), Is.Null);
            Assert.That(ClipboardChanges.Of(sync).CopiedSince(at), Is.False, "Hydra's own write");
            Assert.That(master.Pull("b").MasterHash, Is.EqualTo(ClipboardUtils.ClipboardHash(ClipboardSnapshot.Empty)));
            Assert.That(log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message), Has.Some.Contains("a").And.Contains("concealed"));
        }
    }

    // an answer that could not be written is a failure, to be shown where the cursor is
    [Test]
    public void MasterApply_CannotBeWritten_IsAFailure()
    {
        var sync = new FakeClipboardSync { WriteFailsValue = true };
        var log = new CapturingLog<ClipboardMaster>();
        var master = new ClipboardMaster(sync, log);
        var pull = master.Pull("a");

        var taken = master.TakePullAnswer("a", new ClipboardPullResponseMessage(pull.PullId, "x from a"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(taken.Failed, Is.True);
            var warnings = log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message);
            Assert.That(warnings, Has.Some.Contains("a").And.Contains("could not be written"));
        }
    }

    // an answer whose every format the clipboard refused left it empty: a failure too
    [Test]
    public void MasterApply_EveryFormatRefused_IsAFailure()
    {
        var sync = new FakeClipboardSync();
        sync.RefuseEveryFormat();
        var log = new CapturingLog<ClipboardMaster>();
        var master = new ClipboardMaster(sync, log);
        var pull = master.Pull("a");

        var taken = master.TakePullAnswer("a", new ClipboardPullResponseMessage(pull.PullId, "x from a"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(taken.Failed, Is.True);
            var warnings = log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message);
            Assert.That(warnings, Has.Some.Contains("a").And.Contains("could not be written"));
        }
    }

    // a failed write leaves the counter where the user's copy during it put it, which stays the user's
    [Test]
    public void ChangesWrite_FailsWhileTheUserCopies_TheCopyIsNotHydras()
    {
        var sync = new FakeClipboardSync { WriteFailsValue = true };
        var changes = ClipboardChanges.Of(sync);
        var at = changes.Now();
        sync.AfterWrite = () => sync.SetupText("copied meanwhile");

        changes.Write(() => sync.SetClipboard(new ClipboardSnapshot("x", null, null)), w => w != null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes.CopiedSince(at), Is.True);
            Assert.That(changes.StillOwnWrite(), Is.False);
        }
    }

    [Test]
    public void ChangesWrite_AUserCopyLandsDuringIt_IsNotHydras()
    {
        var sync = new FakeClipboardSync();
        var changes = ClipboardChanges.Of(sync);
        var at = changes.Now();
        sync.AfterWrite = () => sync.SetupText("copied meanwhile");

        changes.Write(() => sync.SetClipboard(new ClipboardSnapshot("x", null, null)), w => w != null);

        Assert.That(changes.CopiedSince(at), Is.True);
    }

    [Test]
    public void ChangesWrite_Succeeds_IsHydras()
    {
        var sync = new FakeClipboardSync();
        var changes = ClipboardChanges.Of(sync);
        var at = changes.Now();

        changes.Write(() => sync.SetClipboard(new ClipboardSnapshot("x", null, null)), w => w != null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes.CopiedSince(at), Is.False);
            Assert.That(changes.StillOwnWrite(), Is.True);
        }
    }

    // a rejected Begin's pull, accepted as streaming before the receiver took the Begin, ends with it
    [Test]
    public void MasterStreamEnded_ForTheAcceptedPull_ForgetsIt()
    {
        var master = new ClipboardMaster(new FakeClipboardSync(), Log);
        var pull = master.Pull("a");
        master.StreamFor("a", pull.PullId);

        master.StreamEnded("a", pull.PullId);

        Assert.That(master.Outstanding, Is.Empty);
    }

    // the rejected Begin's accept replaced the older stream from that host too
    [Test]
    public void MasterStreamEnded_ForTheAcceptedPullBesideAStream_ForgetsBoth()
    {
        var master = new ClipboardMaster(new FakeClipboardSync(), Log);
        var first = master.Pull("a");
        master.StreamFor("a", first.PullId);
        var second = master.Pull("a", StreamingHash);
        master.StreamFor("a", second.PullId);

        master.StreamEnded("a", second.PullId);

        Assert.That(master.Outstanding, Is.Empty);
    }

    [Test]
    public void MasterStreamEnded_ForTheStreamingPull_KeepsTheNewerLatest()
    {
        var master = new ClipboardMaster(new FakeClipboardSync(), Log);
        var first = master.Pull("a");
        master.Streaming(new OutstandingPull("a", first.PullId));
        var second = master.Pull("a", StreamingHash);

        master.StreamEnded("a", first.PullId);

        Assert.That(master.Outstanding, Is.EqualTo([new OutstandingPull("a", second.PullId)]));
    }

    // -- the master asks a resend of an answer it did not take --

    // the answer could not be written (Windows' clipboard busy): the slave, synced with it, must answer it again
    [Test]
    public void MasterApply_CannotBeWritten_TheNextPullAsksAResendFromIt()
    {
        var sync = new FakeClipboardSync { WriteFailsValue = true };
        var master = new ClipboardMaster(sync, Log);
        var first = master.Pull("a");
        master.TakePullAnswer("a", new ClipboardPullResponseMessage(first.PullId, "copied on a"));

        var next = master.Pull("a");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.ResendFrom, Is.Null);
            Assert.That(next.ResendFrom, Is.EqualTo(first.PullId));
        }
    }

    [Test]
    public void MasterApply_Written_TheNextPullAsksNoResend()
    {
        var sync = new FakeClipboardSync { WriteFailsValue = true };
        var master = new ClipboardMaster(sync, Log);
        var first = master.Pull("a");
        master.TakePullAnswer("a", new ClipboardPullResponseMessage(first.PullId, "copied on a"));
        sync.WriteFailsValue = false;
        var resent = master.Pull("a");
        master.TakePullAnswer("a", new ClipboardPullResponseMessage(resent.PullId, "copied on a"));

        var next = master.Pull("a");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resent.ResendFrom, Is.EqualTo(first.PullId), "pre-condition: asked again");
            Assert.That(sync.Text, Is.EqualTo("copied on a"));
            Assert.That(next.ResendFrom, Is.Null);
        }
    }

    // a newer copy from another host superseded a's stream: a's older copy must never come back over it
    [Test]
    public void MasterStreamSuperseded_TheNextPullAsksNoResend()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        var fromA = master.Pull("a");
        master.StreamFor("a", fromA.PullId);
        var fromB = master.Pull("b");
        master.TakePullAnswer("b", new ClipboardPullResponseMessage(fromB.PullId, "newer on b"));
        master.StreamEnded("a");

        var next = master.Pull("a");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sync.Text, Is.EqualTo("newer on b"), "pre-condition: b's copy taken");
            Assert.That(next.ResendFrom, Is.Null);
        }
    }

    // a's stream failed (bad blob hash, unreadable, watchdog): a's next pull asks again
    [Test]
    public void MasterStreamFailed_TheNextPullAsksAResendFromIt()
    {
        var master = new ClipboardMaster(new FakeClipboardSync(), Log);
        var pull = master.Pull("a");
        master.StreamFor("a", pull.PullId);

        master.StreamEnded("a", pull.PullId, failed: true);

        Assert.That(master.Pull("a").ResendFrom, Is.EqualTo(pull.PullId));
    }

    // a stream failing once a newer copy from another host was taken asks nothing of a: that copy outranks it
    [Test]
    public void MasterStreamFailed_ThenAnotherHostsCopyTaken_TheNextPullAsksNoResend()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        var fromA = master.Pull("a");
        master.StreamFor("a", fromA.PullId);
        master.StreamEnded("a", fromA.PullId, failed: true);
        master.TakePullAnswer("b", new ClipboardPullResponseMessage(master.Pull("b").PullId, "newer on b"));

        Assert.That(master.Pull("a").ResendFrom, Is.Null);
    }

    // the user copied on the master since: that copy outranks the answer not taken
    [Test]
    public void MasterStreamFailed_ThenCopiedHere_TheNextPullAsksNoResend()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        var pull = master.Pull("a");
        master.StreamFor("a", pull.PullId);
        master.StreamEnded("a", pull.PullId, failed: true);

        sync.SetupText("copied on the master");

        Assert.That(master.Pull("a").ResendFrom, Is.Null);
    }

    // the host left (or the relay went) before its answer landed: whatever it sent was never taken
    [Test]
    public void MasterPullForgotten_Unanswered_TheNextPullAsksAResendFromIt()
    {
        var master = new ClipboardMaster(new FakeClipboardSync(), Log);
        var toA = master.Pull("a");
        master.TakePullAnswer("b", new ClipboardPullResponseMessage(master.Pull("b").PullId, null, Unchanged: true));

        master.ForgetPull("a");
        master.ForgetPull();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(master.Pull("a").ResendFrom, Is.EqualTo(toA.PullId));
            Assert.That(master.Pull("b").ResendFrom, Is.Null, "b's answer was taken");
        }
    }

    // a newer pull replaced one whose answer was still on its way, which is ignored when it lands: never taken
    [Test]
    public void MasterPullReplaced_TheIgnoredAnswerIsAskedAgainFromIt()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        var first = master.Pull("a");
        var second = master.Pull("a");
        var ignored = master.TakePullAnswer("a", new ClipboardPullResponseMessage(first.PullId, "copied on a"));
        master.TakePullAnswer("a", new ClipboardPullResponseMessage(second.PullId, null, Unchanged: true));

        var next = master.Pull("a");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ignored.Taken, Is.False, "pre-condition: ignored");
            Assert.That(second.ResendFrom, Is.EqualTo(first.PullId));
            Assert.That(next.ResendFrom, Is.EqualTo(first.PullId), "the unchanged answer took nothing");
        }
    }

    // the first answer not taken is the one asked from, a later one adding nothing
    [Test]
    public void MasterTwoAnswersNotTaken_TheResendIsFromTheFirst()
    {
        var master = new ClipboardMaster(new FakeClipboardSync(), Log);
        var first = master.Pull("a");
        master.StreamFor("a", first.PullId);
        master.StreamEnded("a", first.PullId, failed: true);
        master.Pull("a");
        master.ForgetPull("a");

        Assert.That(master.Pull("a").ResendFrom, Is.EqualTo(first.PullId));
    }

    // the streaming older pull and the newer one both forgotten: the older, whose answer synced the slave, is asked from
    [Test]
    public void MasterPullsForgotten_TheStreamingOlderPullIsAskedFrom()
    {
        var master = new ClipboardMaster(new FakeClipboardSync(), Log);
        var streaming = master.Pull("a");
        master.StreamFor("a", streaming.PullId);
        master.Pull("a", StreamingHash);

        master.ForgetPull("a");

        Assert.That(master.Pull("a").ResendFrom, Is.EqualTo(streaming.PullId));
    }

    // a copy made on the master since the pull went out outranks its answer, which is ignored when it lands: the entry
    // hash asks no resend, so the slave takes the master's copy rather than keep the older one it answered with
    [TestCase(true)]
    [TestCase(false)]
    public void MasterResendFrom_CopiedOnTheMasterSinceThePullWentOut_IsNull(bool counter)
    {
        var sync = new FakeClipboardSync { ChangeCountSupported = counter };
        sync.SetupText("old on the master");
        var master = new ClipboardMaster(sync, Log);
        master.Hash();
        master.Pull("a");

        sync.CanUseEchoFallbackValue = false;
        sync.SetupText("z on the master");
        master.Hash();

        Assert.That(master.ResendFrom("a"), Is.Null);
    }

    // the same, the pull then forgotten unanswered: no record, so no later pull asks the older copy back either
    [TestCase(true)]
    [TestCase(false)]
    public void MasterPullForgotten_CopiedOnTheMasterSince_TheNextPullAsksNoResend(bool counter)
    {
        var sync = new FakeClipboardSync { ChangeCountSupported = counter };
        sync.SetupText("old on the master");
        var master = new ClipboardMaster(sync, Log);
        master.Hash();
        master.Pull("a");
        sync.CanUseEchoFallbackValue = false;
        sync.SetupText("z on the master");
        master.Hash();

        master.ForgetPull("a");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(master.ResendFrom("a"), Is.Null);
            Assert.That(master.Pull("a").ResendFrom, Is.Null);
        }
    }

    // A's answer is taken just after the hash's read: the hash and its ResendFrom are of one clipboard, the answer's, so
    // A is never offered the older clipboard without the resend that answer ended (which would empty its copy)
    [Test]
    public void MasterHashForAHost_AnAnswerFromItTakenMidRead_IsReadAgain()
    {
        var sync = new FakeClipboardSync();
        sync.SetupText("old on the master");
        var master = new ClipboardMaster(sync, Log);
        master.Hash();
        var toA = master.Pull("a");
        sync.OnChangeCount = () =>
        {
            sync.OnChangeCount = null;
            master.TakePullAnswer("a", new ClipboardPullResponseMessage(toA.PullId, "copied on a"));
        };

        var hash = master.Hash("a");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sync.Text, Is.EqualTo("copied on a"), "pre-condition: taken mid-read");
            Assert.That(hash?.Hash, Is.EqualTo(ClipboardUtils.ClipboardHash(new ClipboardSnapshot("copied on a", null, null))));
            Assert.That(hash?.ResendFrom, Is.Null);
        }
    }

    // the entry hash carries what a pull would: the first answer not taken, else the oldest pull still out, else nothing
    [Test]
    public void MasterResendFrom_IsTheAnswerNotTakenElseThePullStillOut()
    {
        var sync = new FakeClipboardSync { WriteFailsValue = true };
        var master = new ClipboardMaster(sync, Log);
        var none = master.ResendFrom("a");
        var toA = master.Pull("a");
        var whileOut = master.ResendFrom("a");
        master.TakePullAnswer("a", new ClipboardPullResponseMessage(toA.PullId, "copied on a"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(none, Is.Null);
            Assert.That(whileOut, Is.EqualTo(toA.PullId));
            Assert.That(master.ResendFrom("a"), Is.EqualTo(toA.PullId), "its write failed");
            Assert.That(master.ResendFrom("b"), Is.Null);
        }
    }

    // a pull made as an older answer is decided on is the one it is held for, so that pull's unchanged answer lands it
    [Test]
    public void MasterHold_APullMadeAsItIsDecided_TheHeldAnswerLandsOnItsUnchangedAnswer()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        var first = master.Pull("a");
        master.StreamFor("a", first.PullId);
        master.Pull("a", StreamingHash);
        ClipboardPullMessage? third = null;
        sync.OnChangeCount = () =>
        {
            sync.OnChangeCount = null;
            third = master.Pull("a");
        };

        var held = master.TakePullAnswer("a", new ClipboardPullResponseMessage(first.PullId, "x1 from a"));
        var landed = master.TakePullAnswer("a", new ClipboardPullResponseMessage(third!.PullId, null, Unchanged: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(held.Taken, Is.True, "pre-condition: held");
            Assert.That(held.Applied, Is.Null, "pre-condition: held");
            Assert.That(landed.Applied?.Text, Is.EqualTo("x1 from a"));
            Assert.That(sync.Text, Is.EqualTo("x1 from a"));
        }
    }

    // the deadline lands the held answer, but the pull it was held for stays out: its slow answer, a newer copy, still lands
    // over the held one
    [Test]
    public void MasterLandHeld_ALateChangedAnswerToThePullItWasHeldFor_StillWins()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        var second = HoldStream(master);

        var held = master.LandHeld(new OutstandingPull("a", second.PullId));
        var late = master.TakePullAnswer("a", new ClipboardPullResponseMessage(second.PullId, "y, newer from a"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(held?.Answer?.Applied?.Text, Is.EqualTo("x1 from a"), "pre-condition: the held answer landed");
            Assert.That(late.Taken, Is.True);
            Assert.That(sync.Text, Is.EqualTo("y, newer from a"));
            Assert.That(master.Pull("a").ResendFrom, Is.Null, "its answer was taken");
        }
    }

    // ... and its late unchanged answer takes only its selection, the held copy staying
    [Test]
    public void MasterLandHeld_ALateUnchangedAnswerToThePullItWasHeldFor_TakesOnlyItsSelection()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        var second = HoldStream(master);
        master.LandHeld(new OutstandingPull("a", second.PullId));

        var late = master.TakePullAnswer("a", new ClipboardPullResponseMessage(second.PullId, null, "selected on a", Unchanged: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(late.Taken, Is.True);
            Assert.That(sync.Text, Is.EqualTo("x1 from a"));
            Assert.That(sync.PrimaryText, Is.EqualTo("selected on a"));
        }
    }

    // the held answer's deadline lands it from a timer while b's newer answer is delivered: one write at a time, so b's
    // answer, arriving mid-landing, waits its turn and is written after it, never the older held copy over b's
    [Test]
    public async Task MasterLandHeld_ANewerAnswerArrivingMidLanding_IsWrittenAfterIt()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        var second = HoldStream(master);
        var toB = master.Pull("b");
        var bWaits = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<PullAnswer>? fromB = null;
        var bWaited = false;
        sync.OnChangeCount = () =>
        {
            // the landing's copied-since check: b's answer is delivered now, on the relay's thread
            sync.OnChangeCount = null;
            master.WaitingToWrite = () => bWaits.TrySetResult();
            fromB = Task.Run(() => master.TakePullAnswer("b", new ClipboardPullResponseMessage(toB.PullId, "newest from b")));
            bWaited = bWaits.Task.Wait(TestBound.Bound);
        };

        var landed = master.LandHeld(new OutstandingPull("a", second.PullId));
        var taken = await fromB!.WaitAsync(TestBound.Bound);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bWaited, Is.True, "pre-condition: b's answer waited behind the landing");
            Assert.That(landed?.Answer?.Applied?.Text, Is.EqualTo("x1 from a"), "pre-condition: the held answer landed first");
            Assert.That(taken.Applied?.Text, Is.EqualTo("newest from b"));
            Assert.That(sync.Text, Is.EqualTo("newest from b"));
        }
    }

    // b's newer answer taken just before the landing's turn: the held answer is still in state, so b's supersede drops it
    // and it never lands
    [Test]
    public async Task MasterLandHeld_ANewerAnswerTakenFirst_SupersedesIt()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        var second = HoldStream(master);
        var toB = master.Pull("b");
        master.WaitingToWrite = () =>
        {
            master.WaitingToWrite = null;
            master.TakePullAnswer("b", new ClipboardPullResponseMessage(toB.PullId, "newest from b"));
        };

        var landed = await Task.Run(() => master.LandHeld(new OutstandingPull("a", second.PullId)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(landed, Is.Null);
            Assert.That(sync.Text, Is.EqualTo("newest from b"));
        }
    }

    // on X11 the counter can ask the X server, which must never happen while the state's lock is held
    [Test]
    public void MasterPull_ReadsTheChangeCounterOutsideTheLock()
    {
        var sync = new FakeClipboardSync { WriteFailsValue = true };
        var master = new ClipboardMaster(sync, Log);
        master.TakePullAnswer("a", new ClipboardPullResponseMessage(master.Pull("a").PullId, "copied on a"));
        List<bool> locked = [];
        sync.OnChangeCount = () => locked.Add(master.Locked);

        master.Pull("a");
        master.Pull("a");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(locked, Is.Not.Empty, "pre-condition: the counter was read");
            Assert.That(locked, Has.None.True);
        }
    }

    // a left before its answer to the newer pull landed; b's answer to the older pull lands after: that older copy never
    // outranks a's, which is still asked again
    [Test]
    public void MasterOlderAnswerFromAnotherHostWritten_TheNewerAnswerNotTakenIsStillAskedAgain()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        var toB = master.Pull("b");
        var toA = master.Pull("a");
        master.ForgetPull("a");

        master.TakePullAnswer("b", new ClipboardPullResponseMessage(toB.PullId, "older on b"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sync.Text, Is.EqualTo("older on b"), "pre-condition: b's copy written");
            Assert.That(master.Pull("a").ResendFrom, Is.EqualTo(toA.PullId));
        }
    }

    // a selection alone changes no clipboard, so it outranks no answer not taken
    [Test]
    public void MasterStreamFailed_ThenASelectionAloneApplied_TheNextPullStillAsksAResend()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        var toA = master.Pull("a");
        master.StreamFor("a", toA.PullId);
        master.StreamEnded("a", toA.PullId, failed: true);

        var taken = master.TakePullAnswer("b", new ClipboardPullResponseMessage(master.Pull("b").PullId, null, "selected on b", Unchanged: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(taken.Applied?.IsSelectionOnly, Is.True, "pre-condition: the selection applied");
            Assert.That(master.Pull("a").ResendFrom, Is.EqualTo(toA.PullId));
        }
    }

    // a changed answer that could not be written is still the newest copy: it supersedes the older pulls, so an older
    // copy from another host never lands over it, and its own host is asked again
    [Test]
    public void MasterApply_CannotBeWritten_StillSupersedesTheOlderPulls()
    {
        var sync = new FakeClipboardSync();
        sync.SetupText("on the master");
        var master = new ClipboardMaster(sync, Log);
        var toB = master.Pull("b");
        var toA = master.Pull("a");
        sync.WriteFailsValue = true;

        var failed = master.TakePullAnswer("a", new ClipboardPullResponseMessage(toA.PullId, "newer on a"));
        sync.WriteFailsValue = false;
        var older = master.TakePullAnswer("b", new ClipboardPullResponseMessage(toB.PullId, "older on b"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failed.Failed, Is.True);
            Assert.That(failed.Superseded, Is.EqualTo([new OutstandingPull("b", toB.PullId)]));
            Assert.That(older.Taken, Is.False);
            Assert.That(sync.Text, Is.EqualTo("on the master"));
            Assert.That(master.Pull("a").ResendFrom, Is.EqualTo(toA.PullId));
        }
    }

    // an unchanged answer while a resend is asked leaves it asked: the slave had nothing to send (unreadable, or
    // Hydra's own write), and its copy still waits
    [Test]
    public void MasterStreamFailed_ThenAnsweredUnchanged_TheNextPullStillAsksAResend()
    {
        var master = new ClipboardMaster(new FakeClipboardSync(), Log);
        var pull = master.Pull("a");
        master.StreamFor("a", pull.PullId);
        master.StreamEnded("a", pull.PullId, failed: true);
        master.TakePullAnswer("a", new ClipboardPullResponseMessage(master.Pull("a").PullId, null, Unchanged: true));

        Assert.That(master.Pull("a").ResendFrom, Is.EqualTo(pull.PullId));
    }

    [Test]
    public void MasterIsStreaming_OnlyTheAcceptedPull()
    {
        var master = new ClipboardMaster(new FakeClipboardSync(), Log);
        var first = master.Pull("a");
        master.StreamFor("a", first.PullId);
        var second = master.Pull("a", StreamingHash);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(master.IsStreaming(new OutstandingPull("a", first.PullId)), Is.True);
            Assert.That(master.IsStreaming(new OutstandingPull("a", second.PullId)), Is.False);
        }
    }

    // -- the slave answers a resend --

    // its last full answer synced the clipboard, but the master did not take it: answered again, never Unchanged
    [Test]
    public void SlavePull_ResendFromItsSyncingAnswer_AnswersTheSyncedClipboardInFull()
    {
        var sync = new FakeClipboardSync();
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        sync.SetupText("copied here");
        sync.CanUseEchoFallbackValue = false;
        slave.OnPull(new ClipboardPullMessage(1, ClipboardUtils.StableHash("older")), "master");

        var plain = slave.OnPull(new ClipboardPullMessage(2, ClipboardUtils.StableHash("older")), "master");
        var resend = slave.OnPull(new ClipboardPullMessage(3, ClipboardUtils.StableHash("older"), ResendFrom: 1), "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(plain.Unchanged, Is.True, "pre-condition: synced");
            Assert.That(resend.Unchanged, Is.Null);
            Assert.That(resend.Text, Is.EqualTo("copied here"));
        }
    }

    // its sync came from an older answer the master took: the newer one not taken was Unchanged, so the master's newer
    // clipboard stays and the older copy never comes back over it
    [Test]
    public void SlavePull_ResendFromAfterItsSyncingAnswer_AnswersUnchanged()
    {
        var sync = new FakeClipboardSync();
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        sync.SetupText("copied here");
        sync.CanUseEchoFallbackValue = false;
        slave.OnPull(new ClipboardPullMessage(1, ClipboardUtils.StableHash("older")), "master");

        var answer = slave.OnPull(new ClipboardPullMessage(3, ClipboardUtils.StableHash("newer"), ResendFrom: 2), "master");

        Assert.That(answer.Unchanged, Is.True);
    }

    // its full answer was never taken, and the master's entry hash, older, says so: the copy is kept, never emptied for
    // the older push, and the next pull resends it
    [Test]
    public void SlaveHash_ResendFromItsSyncingAnswer_KeepsTheClipboardForTheResend()
    {
        var sync = new FakeClipboardSync();
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        sync.SetupText("copied here");
        sync.CanUseEchoFallbackValue = false;
        slave.OnPull(new ClipboardPullMessage(1, ClipboardUtils.StableHash("older")), "master");

        var need = slave.OnHash(new ClipboardHashMessage(ClipboardUtils.StableHash("older"), ResendFrom: 1), "master");
        var resend = slave.OnPull(new ClipboardPullMessage(2, ClipboardUtils.StableHash("older"), ResendFrom: 1), "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(need, Is.Not.EqualTo(PushNeed.Clipboard));
            Assert.That(sync.ClearClipboardCallCount, Is.Zero);
            Assert.That(resend.Text, Is.EqualTo("copied here"));
        }
    }

    // a hash asking no resend, or one from after its sync (that answer was taken), is the master's newer clipboard
    [TestCase(null)]
    [TestCase(2)]
    public void SlaveHash_NoResendOfItsSyncingAnswer_AsksForThePush(int? resendFrom)
    {
        var sync = new FakeClipboardSync();
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        sync.SetupText("copied here");
        sync.CanUseEchoFallbackValue = false;
        slave.OnPull(new ClipboardPullMessage(1, ClipboardUtils.StableHash("older")), "master");

        var need = slave.OnHash(new ClipboardHashMessage(ClipboardUtils.StableHash("newer"), ResendFrom: resendFrom), "master");

        Assert.That(need, Is.EqualTo(PushNeed.Clipboard));
    }

    // a push from that master is the master's own clipboard, which it has: still Unchanged
    [Test]
    public void SlavePull_Resend_StillHydrasWrite_AnswersUnchanged()
    {
        var sync = new FakeClipboardSync();
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        slave.OnPush(new ClipboardPushMessage("pushed"), "master");

        var answer = slave.OnPull(new ClipboardPullMessage(2, ClipboardUtils.StableHash("on the master"), ResendFrom: 1), "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(answer.Unchanged, Is.True);
            Assert.That(answer.Text, Is.Null);
        }
    }

    // the slave resends the copy it first answered to an older pull: the answer names that pull
    [Test]
    public void SlavePull_Resend_NamesThePullItFirstAnswered()
    {
        var sync = new FakeClipboardSync();
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        sync.SetupText("x on a");
        sync.CanUseEchoFallbackValue = false;
        var first = slave.OnPull(new ClipboardPullMessage(5, ClipboardUtils.StableHash("older")), "master");

        var resend = slave.OnPull(new ClipboardPullMessage(8, ClipboardUtils.StableHash("older"), ResendFrom: 5), "master");
        var again = slave.OnPull(new ClipboardPullMessage(9, ClipboardUtils.StableHash("older"), ResendFrom: 5), "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Resends, Is.Null, "a fresh copy keeps its own pull");
            Assert.That(resend.Text, Is.EqualTo("x on a"));
            Assert.That(resend.Resends, Is.EqualTo(5));
            Assert.That(again.Resends, Is.EqualTo(5), "still the pull that first carried it");
        }
    }

    // after a relay drop A:5 and B:6 both stand; A resends its older copy on pull 8: ranked as pull 5's answer, it
    // never drops B's newer record, so B's copy is still asked again
    [Test]
    public void MasterResentAnswer_IsRankedByThePullItResends()
    {
        var sync = new FakeClipboardSync();
        var master = new ClipboardMaster(sync, Log);
        var toA = master.Pull("a");
        var toB = master.Pull("b");
        master.ForgetPull();

        var resend = master.Pull("a");
        master.TakePullAnswer("a", new ClipboardPullResponseMessage(resend.PullId, "x on a", Resends: toA.PullId));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resend.ResendFrom, Is.EqualTo(toA.PullId), "pre-condition: asked again");
            Assert.That(sync.Text, Is.EqualTo("x on a"));
            Assert.That(master.Pull("b").ResendFrom, Is.EqualTo(toB.PullId));
        }
    }

    // the same, streamed: its Begin names the pull it resends, so B's newer pull still out stays outstanding
    [Test]
    public void MasterResentStream_IsRankedByThePullItResends()
    {
        var master = new ClipboardMaster(new FakeClipboardSync(), Log);
        var toA = master.Pull("a");
        master.ForgetPull("a");
        var toB = master.Pull("b");
        var resend = master.Pull("a");
        master.StreamFor("a", resend.PullId);

        var superseded = master.Streaming(new OutstandingPull("a", resend.PullId), resends: toA.PullId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(superseded, Is.Empty);
            Assert.That(master.Outstanding, Does.Contain(new OutstandingPull("b", toB.PullId)));
        }
    }

    // told Pending while its copy the master never took is still there: kept for the resend, never emptied
    [Test]
    public void SlaveHash_PendingWithResendFromItsSyncingAnswer_KeepsTheClipboard()
    {
        var sync = new FakeClipboardSync();
        var slave = new ClipboardSlave(sync, Log, NothingPending);
        sync.SetupText("x on a");
        sync.CanUseEchoFallbackValue = false;
        slave.OnPull(new ClipboardPullMessage(5, ClipboardUtils.StableHash("older")), "master");

        slave.OnHash(new ClipboardHashMessage(null, Pending: true, ResendFrom: 5), "master");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sync.ClearClipboardCallCount, Is.Zero);
            Assert.That(sync.Text, Is.EqualTo("x on a"));
        }
    }

    // A synced by an answer the master took; the user copies Z on the master; A's Unchanged answer to the next pull is lost
    // as the relay drops. The pull after asks a resend from the lost one, newer than A's sync: Unchanged, and Z stays
    [Test]
    public void LostUnchangedAnswer_TheResendFindsTheSlavesSyncOlder_AndTheMastersCopyStays()
    {
        var onMaster = new FakeClipboardSync();
        var onA = new FakeClipboardSync();
        var master = new ClipboardMaster(onMaster, Log);
        var slave = new ClipboardSlave(onA, Log, NothingPending);
        onA.SetupText("x on a");
        onA.CanUseEchoFallbackValue = false;
        var taken = master.Pull("a");
        master.TakePullAnswer("a", slave.OnPull(taken, "master"));
        Assert.That(onMaster.Text, Is.EqualTo("x on a"), "pre-condition: taken");

        onMaster.CanUseEchoFallbackValue = false;
        onMaster.SetupText("z on the master");
        master.Hash();
        var lost = master.Pull("a");
        var unchanged = slave.OnPull(lost, "master");
        master.ForgetPull();
        var next = master.Pull("a");
        var answer = slave.OnPull(next, "master");
        master.TakePullAnswer("a", answer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unchanged.Unchanged, Is.True, "pre-condition: the lost answer was unchanged");
            Assert.That(next.ResendFrom, Is.EqualTo(lost.PullId));
            Assert.That(answer.Unchanged, Is.True);
            Assert.That(onMaster.Text, Is.EqualTo("z on the master"));
        }
    }

    [Test]
    public void PullMessage_ResendFromOmittedWhenNotAsked()
    {
        var plain = MessageSerializer.Encode(MessageKind.ClipboardPull, ClipboardWire.ToPull(ClipboardBaseline.Of(ClipboardSnapshot.Empty), 1));
        var resend = ClipboardWire.ToPull(ClipboardBaseline.Of(ClipboardSnapshot.Empty), 2, resendFrom: 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(System.Text.Encoding.UTF8.GetString(plain), Does.Not.Contain("resend").IgnoreCase);
            Assert.That(MessageSerializer.Decode(MessageSerializer.Encode(MessageKind.ClipboardPull, resend)).Deserialize<ClipboardPullMessage>().ResendFrom,
                Is.EqualTo(1));
        }
    }
}
