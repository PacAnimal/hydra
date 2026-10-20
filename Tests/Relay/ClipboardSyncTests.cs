using Cathedral.Extensions;
using Hydra.Config;
using Hydra.Platform;
using Hydra.Relay;
using Tests.Setup;

namespace Tests.Relay;

[TestFixture]
public class ClipboardSyncTests : ServicesFixture
{
    // -- master pushes clipboard on screen enter --

    [Test]
    public async Task OnEnterRemoteScreen_PushesClipboardToSlave()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("hello from master");
        var (platform, relay, _) = await StartMaster(clipboard);

        TransitionTestHelper.EnterRemote(platform); // cross right edge → sends clipboard hash
        Assert.That(relay.Sent.Of(MessageKind.ClipboardHash), Is.Not.Empty);

        await SimulatePullRequest(relay);

        var push = Pushes(relay);
        Assert.That(push, Has.Count.EqualTo(1));
        Assert.That(push[0].Text, Is.EqualTo("hello from master"));
    }

    [Test]
    public async Task SystemClipboardSync_MacToMac_SendsNoClipboardMessages()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("handled by Universal Clipboard");
        var (platform, relay, _) = await StartMaster(clipboard, PeerPlatform.MacOs, SystemSync, PeerPlatform.MacOs);

        VisitAndLeaveRemote(platform);

        Assert.That(relay.Sent.Snapshot().Where(s => s.Kind is MessageKind.ClipboardHash or MessageKind.ClipboardPush or MessageKind.ClipboardPull), Is.Empty);
    }

    [TestCase(PeerPlatform.Windows)]
    [TestCase(PeerPlatform.Linux)]
    public async Task SystemClipboardSync_NonMacPeer_StillUsesHydra(PeerPlatform remotePlatform)
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("cross-platform");
        var (platform, relay, _) = await StartMaster(clipboard, remotePlatform, SystemSync, PeerPlatform.MacOs);

        TransitionTestHelper.EnterRemote(platform);

        Assert.That(relay.Sent.Of(MessageKind.ClipboardHash), Is.Not.Empty);
    }

    [Test]
    public async Task OnEnterRemoteScreen_EmptyClipboard_NoPushSent()
    {
        var clipboard = new FakeClipboardSync(); // GetText returns null
        var (platform, relay, _) = await StartMaster(clipboard);

        TransitionTestHelper.EnterRemote(platform);

        using (Assert.EnterMultipleScope())
        {
            // empty clipboard → no hash query and no push
            Assert.That(relay.Sent.Of(MessageKind.ClipboardHash), Is.Empty);
            Assert.That(relay.Sent.Of(MessageKind.ClipboardPush), Is.Empty);
        }
    }

    [Test]
    public async Task OnEnterRemoteScreen_OversizedText_NoPushSent()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText(new string('x', 16 * 1024 * 1024 + 1)); // > 16 MiB UTF-8
        var (platform, relay, _) = await StartMaster(clipboard);

        TransitionTestHelper.EnterRemote(platform);

        Assert.That(relay.Sent.Of(MessageKind.ClipboardPush), Is.Empty);
    }

    // -- master pulls clipboard on screen leave (return to local) --

    [Test]
    public async Task OnLeaveRemoteScreen_PullsSentToSlave()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("something");
        var (platform, relay, _) = await StartMaster(clipboard);

        TransitionTestHelper.EnterRemote(platform);
        relay.Sent.Clear();

        // simulate post-warp artifact (big jump dropped by bogus filter), then a real small move back
        platform.FireMouseMove(1280, 720); // warp artifact — dropped
        platform.FireMouseMove(1275, 720); // dx=-5 → cursor exits left edge of remote → return to local

        Assert.That(relay.Sent.Of(MessageKind.ClipboardPull), Is.Not.Empty);
    }

    // -- master handles pull response --

    [Test]
    public async Task OnClipboardPullResponse_SetsLocalClipboard()
    {
        var clipboard = new FakeClipboardSync();
        var (platform, relay, _) = await StartMaster(clipboard);

        VisitAndLeaveRemote(platform);

        await AnswerPull(relay, new ClipboardPullResponseMessage(LatestPullId(relay), "from slave"));

        Assert.That(clipboard.Text, Is.EqualTo("from slave"));
    }

    [Test]
    public async Task OnClipboardPullResponse_LocalFileClipboard_IsPreserved()
    {
        var clipboard = new FakeClipboardSync();
        var (platform, relay, _) = await StartMaster(clipboard);

        VisitAndLeaveRemote(platform);
        clipboard.HasFileClipboardValue = true;
        var before = clipboard.SetClipboardCallCount;

        await AnswerPull(relay, new ClipboardPullResponseMessage(LatestPullId(relay), "incoming text"));

        Assert.That(clipboard.SetClipboardCallCount, Is.EqualTo(before));
    }

    [Test]
    public async Task OnClipboardPullResponse_WhenOnRemoteScreen_ForwardsToActiveHost()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("master text");
        var bundle = await StartMaster(clipboard);
        var pullId = PullThenReenter(bundle);

        // pull response arrives while cursor is still on remote → forwards via hash query
        await AnswerPull(bundle.Relay, new ClipboardPullResponseMessage(pullId, "slave had this"));

        Assert.That(bundle.Relay.Sent.Of(MessageKind.ClipboardHash), Is.Not.Empty);
        await SimulatePullRequest(bundle.Relay);

        var push = Pushes(bundle.Relay);
        Assert.That(push, Has.Count.EqualTo(1));
        Assert.That(push[0].Text, Is.EqualTo("slave had this"));
    }

    // -- PRIMARY selection: master push to Linux vs non-Linux peers --

    [Test]
    public async Task OnEnterLinuxSlave_PushesPrimaryText()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("clipboard text");
        clipboard.SetPrimaryText("primary text");
        var (platform, relay, _) = await StartMaster(clipboard);

        TransitionTestHelper.EnterRemote(platform); // cross right edge → hash query
        await SimulatePullRequest(relay);

        var push = Pushes(relay);
        Assert.That(push, Has.Count.EqualTo(1));
        Assert.That(push[0].PrimaryText, Is.EqualTo("primary text"));
    }

    [Test]
    public async Task OnEnterNonLinuxSlave_StillIncludesPrimaryText()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("clipboard text");
        clipboard.SetPrimaryText("primary text");
        var (platform, relay, _) = await StartMaster(clipboard, PeerPlatform.Windows);

        TransitionTestHelper.EnterRemote(platform);
        await SimulatePullRequest(relay);

        var push = Pushes(relay);
        Assert.That(push, Has.Count.EqualTo(1));
        Assert.That(push[0].PrimaryText, Is.EqualTo("primary text"));
    }

    [Test]
    public async Task OnClipboardPullResponse_SetsPrimaryText()
    {
        var clipboard = new FakeClipboardSync();
        var (platform, relay, _) = await StartMaster(clipboard);

        VisitAndLeaveRemote(platform);

        await AnswerPull(relay, new ClipboardPullResponseMessage(LatestPullId(relay), "from slave", "primary from slave"));

        Assert.That(clipboard.PrimaryText, Is.EqualTo("primary from slave"));
    }

    [Test]
    public async Task OnClipboardPullResponse_ForwardsPrimaryToLinuxSlave()
    {
        // master has no local PRIMARY (GetPrimaryText returns null) but receives it from slave A;
        // cursor is still on that slave so primary text from pull response should be forwarded in the push
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("master clipboard");
        // no SetPrimaryText — simulates a non-Linux master
        var bundle = await StartMaster(clipboard);
        var pullId = PullThenReenter(bundle);

        // pull response arrives while cursor is still on the Linux slave → forward via hash query
        await AnswerPull(bundle.Relay, new ClipboardPullResponseMessage(pullId, "slave clipboard", "highlighted text"));

        await SimulatePullRequest(bundle.Relay);

        var push = Pushes(bundle.Relay);
        Assert.That(push, Has.Count.EqualTo(1));
        Assert.That(push[0].PrimaryText, Is.EqualTo("highlighted text"));
    }

    // -- slave receives push --

    [Test]
    public async Task SlaveReceivesClipboardPush_SetsLocalClipboard()
    {
        var clipboard = new FakeClipboardSync();
        var slave = new TestableSlaveRelay(clipboard: clipboard);

        var push = new ClipboardPushMessage("pushed text");
        await slave.SimulateReceive("master-pc", MessageKind.ClipboardPush, push.ToSaneJson());

        Assert.That(clipboard.Text, Is.EqualTo("pushed text"));
    }

    [Test]
    public async Task SlaveReceivesClipboardPush_SetsPrimaryText()
    {
        var clipboard = new FakeClipboardSync();
        var slave = new TestableSlaveRelay(clipboard: clipboard);

        var push = new ClipboardPushMessage("text", "highlighted selection");
        await slave.SimulateReceive("master-pc", MessageKind.ClipboardPush, push.ToSaneJson());

        Assert.That(clipboard.PrimaryText, Is.EqualTo("highlighted selection"));
    }

    [Test]
    public async Task SlaveReceivesClipboardPull_CallsGetPrimaryText()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetPrimaryText("selected on slave");

        var slave = new TestableSlaveRelay(clipboard: clipboard);
        var before = clipboard.GetPrimaryTextCallCount;

        await slave.SimulateReceive("master-pc", MessageKind.ClipboardPull, new ClipboardPullMessage(1).ToSaneJson());

        Assert.That(clipboard.GetPrimaryTextCallCount, Is.GreaterThan(before));
    }

    [Test]
    public async Task SlaveReceivesClipboardPull_CallsGetText()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("slave content");

        var slave = new TestableSlaveRelay(clipboard: clipboard);
        var before = clipboard.GetTextCallCount;

        await slave.SimulateReceive("master-pc", MessageKind.ClipboardPull, new ClipboardPullMessage(1).ToSaneJson());

        Assert.That(clipboard.GetTextCallCount, Is.GreaterThan(before));
    }

    // -- image clipboard sync --

    [Test]
    public async Task OnEnterRemoteScreen_PushesImageToSlave()
    {
        var png = TestImages.Png;
        var clipboard = new FakeClipboardSync();
        clipboard.SetupImage(png);
        var (platform, relay, _) = await StartMaster(clipboard);

        TransitionTestHelper.EnterRemote(platform);
        await SimulatePullRequest(relay);

        var push = Pushes(relay);
        Assert.That(push, Has.Count.EqualTo(1));
        Assert.That(push[0].ImagePng, Is.EqualTo(png));
    }

    [Test]
    public async Task OnEnterRemoteScreen_OversizedImage_NoPushSent()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupImage(new byte[16 * 1024 * 1024 + 1]); // > 16 MiB
        var (platform, relay, _) = await StartMaster(clipboard);

        TransitionTestHelper.EnterRemote(platform);

        using (Assert.EnterMultipleScope())
        {
            // oversized image dropped during trim → nothing to push, no hash query sent
            Assert.That(relay.Sent.Of(MessageKind.ClipboardHash), Is.Empty);
            Assert.That(relay.Sent.Of(MessageKind.ClipboardPush), Is.Empty);
        }
    }

    [Test]
    public async Task OnEnterRemoteScreen_ImageAndText_ImageWins()
    {
        // when both text and image are on the clipboard, image wins — text is just a fallback representation
        var png = TestImages.Png;
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("alt text");
        clipboard.SetupImage(png);
        var (platform, relay, _) = await StartMaster(clipboard);

        TransitionTestHelper.EnterRemote(platform);
        await SimulatePullRequest(relay);

        var push = Pushes(relay);
        Assert.That(push, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(push[0].Text, Is.Empty);
            Assert.That(push[0].ImagePng, Is.EqualTo(png));
        }
    }

    [Test]
    public async Task OnClipboardPullResponse_SetsLocalImage()
    {
        var png = TestImages.Png;
        var clipboard = new FakeClipboardSync();
        var (platform, relay, _) = await StartMaster(clipboard);

        VisitAndLeaveRemote(platform);

        await AnswerPull(relay, new ClipboardPullResponseMessage(LatestPullId(relay), null, null, png));

        Assert.That(clipboard.ImagePng, Is.EqualTo(png));
    }

    [Test]
    public async Task SlaveReceivesClipboardPush_SetsImage()
    {
        var png = TestImages.Png;
        var clipboard = new FakeClipboardSync();
        var slave = new TestableSlaveRelay(clipboard: clipboard);

        var push = new ClipboardPushMessage("", null, png);
        await slave.SimulateReceive("master-pc", MessageKind.ClipboardPush, push.ToSaneJson());

        Assert.That(clipboard.ImagePng, Is.EqualTo(png));
    }

    [Test]
    public async Task SlaveReceivesClipboardPull_CallsGetImagePng()
    {
        var png = TestImages.Png;
        var clipboard = new FakeClipboardSync();
        clipboard.SetupImage(png);

        var slave = new TestableSlaveRelay(clipboard: clipboard);
        var before = clipboard.GetImagePngCallCount;

        await slave.SimulateReceive("master-pc", MessageKind.ClipboardPull, new ClipboardPullMessage(1).ToSaneJson());

        Assert.That(clipboard.GetImagePngCallCount, Is.GreaterThan(before));
    }

    // -- clipboard hash exchange: master sends its hash on enter, slave decides to pull --

    [Test]
    public async Task OnEnterRemoteScreen_SendsClipboardHashNotPushDirectly()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("some text");
        var (platform, relay, _) = await StartMaster(clipboard);

        TransitionTestHelper.EnterRemote(platform);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.Sent.Of(MessageKind.ClipboardHash), Is.Not.Empty);
            Assert.That(relay.Sent.Of(MessageKind.ClipboardPush), Is.Empty);
        }
    }

    [Test]
    public async Task OnEnterRemoteScreen_ClipboardHashCarriesMasterHash()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("master content");
        var (platform, relay, _) = await StartMaster(clipboard);

        TransitionTestHelper.EnterRemote(platform);

        var hashes = relay.Sent.Of<ClipboardHashMessage>(MessageKind.ClipboardHash);
        Assert.That(hashes, Has.Count.EqualTo(1));
        var expected = ClipboardUtils.ClipboardHash(new ClipboardSnapshot("master content", null, null));
        Assert.That(hashes[0].Hash, Is.EqualTo(expected));
    }

    [Test]
    public async Task SlaveReceivesClipboardHash_HashDiffers_SendsPullRequest()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("slave has something else");
        var slave = new TestableSlaveRelay(clipboard: clipboard);

        // master sends a hash that won't match slave's clipboard
        var hashMsg = new ClipboardHashMessage(0UL);
        await slave.SimulateReceive("master-pc", MessageKind.ClipboardHash, hashMsg.ToSaneJson());

        Assert.That(slave.Sent.Of(MessageKind.ClipboardPullRequest), Is.Not.Empty);
    }

    [Test]
    public async Task SlaveReceivesClipboardHash_HashMatches_NoResponse()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("identical content");
        var slave = new TestableSlaveRelay(clipboard: clipboard);

        var matchingHash = ClipboardUtils.ClipboardHash(new ClipboardSnapshot("identical content", null, null));
        var hashMsg = new ClipboardHashMessage(matchingHash);
        await slave.SimulateReceive("master-pc", MessageKind.ClipboardHash, hashMsg.ToSaneJson());

        Assert.That(slave.Sent.Of(MessageKind.ClipboardPullRequest), Is.Empty);
    }

    [Test]
    public async Task SlaveReceivesClipboardHash_HashMatchesWithANewSelection_SetsOnlyTheSelection()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("identical content");
        var slave = new TestableSlaveRelay(clipboard: clipboard);

        var hashMsg = ClipboardWire.ToHash(new ClipboardSnapshot("identical content", "master selection", null));
        await slave.SimulateReceive("master-pc", MessageKind.ClipboardHash, hashMsg.ToSaneJson());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.PrimaryText, Is.EqualTo("master selection"));
            Assert.That(clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(slave.Sent.Of(MessageKind.ClipboardPullRequest), Is.Empty);
        }
    }

    [Test]
    public async Task SlaveReceivesClipboardHash_SelectionIsTheText_TakesItFromItsOwnText()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("copied on the master");
        var slave = new TestableSlaveRelay(clipboard: clipboard);

        var hashMsg = ClipboardWire.ToHash(new ClipboardSnapshot("copied on the master", "copied on the master", null));
        await slave.SimulateReceive("master-pc", MessageKind.ClipboardHash, hashMsg.ToSaneJson());

        Assert.That(clipboard.PrimaryText, Is.EqualTo("copied on the master"));
    }

    [Test]
    public async Task OnClipboardPullRequest_SendsFullPush()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("master has this");
        var (platform, relay, _) = await StartMaster(clipboard);

        TransitionTestHelper.EnterRemote(platform); // enter remote (guard: cursor must be on remote)
        await SimulatePullRequest(relay);

        var push = Pushes(relay);
        Assert.That(push, Has.Count.EqualTo(1));
        Assert.That(push[0].Text, Is.EqualTo("master has this"));
    }

    // -- pull: slave skips full response when hashes match --

    [Test]
    public async Task SlaveReceivesClipboardPull_HashMatches_SendsUnchangedResponse()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("same text");
        var slave = new TestableSlaveRelay(clipboard: clipboard);

        var slaveHash = ClipboardUtils.ClipboardHash(new ClipboardSnapshot("same text", null, null));
        var pull = new ClipboardPullMessage(1, slaveHash); // master sends slave's own hash
        await slave.SimulateReceive("master-pc", MessageKind.ClipboardPull, pull.ToSaneJson());

        var msg = slave.Sent.Of<ClipboardPullResponseMessage>(MessageKind.ClipboardPullResponse).Single();
        Assert.That(msg.Unchanged, Is.True);
    }

    [Test]
    public async Task SlaveReceivesClipboardPull_HashMatches_UnchangedCarriesTheSelection()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("same text");
        clipboard.SetPrimaryText("slave selection");
        var slave = new TestableSlaveRelay(clipboard: clipboard);

        var pull = new ClipboardPullMessage(1, ClipboardUtils.ClipboardHash(new ClipboardSnapshot("same text", null, null)));
        await slave.SimulateReceive("master-pc", MessageKind.ClipboardPull, pull.ToSaneJson());

        var msg = slave.Sent.Of<ClipboardPullResponseMessage>(MessageKind.ClipboardPullResponse).Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(msg.Unchanged, Is.True);
            Assert.That(msg.Text, Is.Null);
            Assert.That(msg.PrimaryText, Is.EqualTo("slave selection"));
        }
    }

    [Test]
    public async Task SlaveReceivesClipboardPull_HashDiffers_SendsFullResponse()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("slave has this");
        var slave = new TestableSlaveRelay(clipboard: clipboard);

        var pull = new ClipboardPullMessage(1, 0UL); // hash won't match
        await slave.SimulateReceive("master-pc", MessageKind.ClipboardPull, pull.ToSaneJson());

        var msg = slave.Sent.Of<ClipboardPullResponseMessage>(MessageKind.ClipboardPullResponse).Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(msg.Text, Is.EqualTo("slave has this"));
            Assert.That(msg.Unchanged, Is.Not.True);
        }
    }

    // -- security guards --

    [Test]
    public async Task OnClipboardPullRequest_CursorNotOnThatScreen_Ignored()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("master has this");
        var (_, relay, _) = await StartMaster(clipboard);

        // cursor never moved to remote — guard should block the push
        await SimulatePullRequest(relay);

        Assert.That(relay.Sent.Of(MessageKind.ClipboardPush), Is.Empty);
    }

    [Test]
    public async Task OnClipboardPullResponse_NotFromLastPulledHost_Ignored()
    {
        var clipboard = new FakeClipboardSync();
        var (platform, relay, _) = await StartMaster(clipboard);

        VisitAndLeaveRemote(platform);

        // response arrives from a different host — guard should block SetClipboard
        var before = clipboard.SetClipboardCallCount;
        await AnswerPull(relay, new ClipboardPullResponseMessage(LatestPullId(relay), "from attacker"), host: "other-host");

        Assert.That(clipboard.SetClipboardCallCount, Is.EqualTo(before));
    }

    [Test]
    public async Task OnClipboardPullResponse_Unchanged_SkipsSetClipboard()
    {
        var clipboard = new FakeClipboardSync();
        var (platform, relay, _) = await StartMaster(clipboard);

        VisitAndLeaveRemote(platform);

        var before = clipboard.SetClipboardCallCount;
        await AnswerPull(relay, new ClipboardPullResponseMessage(LatestPullId(relay), null, Unchanged: true));

        Assert.That(clipboard.SetClipboardCallCount, Is.EqualTo(before));
    }

    [Test]
    public async Task OnClipboardPullResponse_UnchangedWithASelection_SetsOnlyTheSelection()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("master clipboard");
        var (platform, relay, _) = await StartMaster(clipboard);

        VisitAndLeaveRemote(platform);

        await AnswerPull(relay, new ClipboardPullResponseMessage(LatestPullId(relay), null, "slave selection", Unchanged: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.PrimaryText, Is.EqualTo("slave selection"));
            Assert.That(clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(clipboard.Text, Is.EqualTo("master clipboard"));
        }
    }

    [Test]
    public async Task OnClipboardPullResponse_UnchangedSelectionIsTheText_IsTheTextThatWasPulledAgainst()
    {
        // a copy on the master while the pull is in flight is not what the slave matched
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("pulled against");
        var (platform, relay, _) = await StartMaster(clipboard);

        VisitAndLeaveRemote(platform);
        clipboard.SetupText("copied meanwhile");

        await AnswerPull(relay, new ClipboardPullResponseMessage(LatestPullId(relay), null, Unchanged: true, PrimaryIsText: true));

        Assert.That(clipboard.PrimaryText, Is.EqualTo("pulled against"));
    }

    [Test]
    public async Task OnClipboardPullResponse_LateAnswerToAnEarlierPull_IsIgnoredAndLeavesTheLatestOne()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("master clipboard");
        var (platform, relay, _) = await StartMaster(clipboard);

        VisitAndLeaveRemote(platform);
        var earlier = LatestPullId(relay);
        VisitAndLeaveRemote(platform);

        await AnswerPull(relay, new ClipboardPullResponseMessage(earlier, null, "older selection", Unchanged: true));
        var afterLate = clipboard.PrimaryText;
        await AnswerPull(relay, new ClipboardPullResponseMessage(LatestPullId(relay), null, "latest selection", Unchanged: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterLate, Is.Null);
            Assert.That(clipboard.PrimaryText, Is.EqualTo("latest selection"));
        }
    }

    [Test]
    public async Task SlaveReceivesClipboardHash_LargeSelectionChanged_RequestsThePush()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("identical content");
        var slave = new TestableSlaveRelay(clipboard: clipboard);

        var hashMsg = ClipboardWire.ToHash(new ClipboardSnapshot("identical content", new string('s', 5000), null));
        await slave.SimulateReceive("master-pc", MessageKind.ClipboardHash, hashMsg.ToSaneJson());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hashMsg.PrimaryText, Is.Null, "a large selection crosses as its hash");
            Assert.That(slave.Sent.Of(MessageKind.ClipboardPullRequest), Is.Not.Empty);
        }
    }

    [Test]
    public async Task SlaveReceivesClipboardPush_AskedForTheSelectionAlone_LeavesTheClipboard()
    {
        // a large selection crosses as its hash; the push it asks for then brings only that
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("identical content");
        var slave = new TestableSlaveRelay(clipboard: clipboard);
        var master = new ClipboardSnapshot("identical content", new string('s', 5000), null);

        await slave.SimulateReceive("master-pc", MessageKind.ClipboardHash, ClipboardWire.ToHash(master).ToSaneJson());
        await slave.SimulateReceive("master-pc", MessageKind.ClipboardPush, ClipboardWire.ToPush(master).ToSaneJson());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.SetClipboardCallCount, Is.Zero);
            Assert.That(clipboard.PrimaryText, Is.EqualTo(master.PrimaryText));
        }
    }

    [Test]
    public async Task SlaveReceivesClipboardPull_HashMatchesWithALargeChangedSelection_UnchangedCarriesIt()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("same text");
        var selection = new string('s', 5000);
        clipboard.SetPrimaryText(selection);
        var slave = new TestableSlaveRelay(clipboard: clipboard);

        var pull = ClipboardWire.ToPull(ClipboardBaseline.Of(new ClipboardSnapshot("same text", null, null)), 1);
        await slave.SimulateReceive("master-pc", MessageKind.ClipboardPull, pull.ToSaneJson());

        var msg = slave.Sent.Of<ClipboardPullResponseMessage>(MessageKind.ClipboardPullResponse).Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(msg.Unchanged, Is.True, "the clipboard matched, so it is not sent again");
            Assert.That(msg.Text, Is.Null);
            Assert.That(msg.PrimaryText, Is.EqualTo(selection));
        }
    }

    [Test]
    public async Task SlaveReceivesClipboardPull_HashAndSelectionMatch_UnchangedCarriesNoSelection()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("same text");
        clipboard.SetPrimaryText(new string('s', 5000));
        var slave = new TestableSlaveRelay(clipboard: clipboard);

        var pull = ClipboardWire.ToPull(ClipboardBaseline.Of(new ClipboardSnapshot("same text", new string('s', 5000), null)), 1);
        await slave.SimulateReceive("master-pc", MessageKind.ClipboardPull, pull.ToSaneJson());

        var msg = slave.Sent.Of<ClipboardPullResponseMessage>(MessageKind.ClipboardPullResponse).Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(msg.Unchanged, Is.True);
            Assert.That(msg.PrimaryText, Is.Null);
        }
    }

    [Test]
    public async Task SlaveReceivesClipboardPush_AskedForTheSelectionButTheClipboardChangedSince_TakesBoth()
    {
        var clipboard = new FakeClipboardSync();
        clipboard.SetupText("identical content");
        var slave = new TestableSlaveRelay(clipboard: clipboard);
        var selection = new string('s', 5000);

        var checkedWith = ClipboardWire.ToHash(new ClipboardSnapshot("identical content", selection, null));
        await slave.SimulateReceive("master-pc", MessageKind.ClipboardHash, checkedWith.ToSaneJson());
        var pushed = ClipboardWire.ToPush(new ClipboardSnapshot("copied on the master since", selection, null));
        await slave.SimulateReceive("master-pc", MessageKind.ClipboardPush, pushed.ToSaneJson());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(clipboard.Text, Is.EqualTo("copied on the master since"));
            Assert.That(clipboard.PrimaryText, Is.EqualTo(selection));
        }
    }

    // -- helpers --

    private static readonly IHydraProfile SystemSync = TransitionTestHelper.ProfileWith(clipboardSync: ClipboardSyncMode.System);

    // a master with "remote" online, advertising remotePlatform
    private async Task<TestServiceBundle> StartMaster(IClipboardSync clipboard, PeerPlatform remotePlatform = PeerPlatform.Linux, IHydraProfile? profile = null,
        PeerPlatform? localPlatform = null)
    {
        return await Services.StartOnline(TransitionTestHelper.CreateService(profile: profile, clipboard: clipboard, localPlatform: localPlatform), remotePlatform);
    }

    // onto the remote and back off its left edge, which pulls from it; the middle move is the warp artifact, dropped
    private static void VisitAndLeaveRemote(FakePlatform platform)
    {
        TransitionTestHelper.EnterRemote(platform);
        platform.FireMouseMove(1280, 720);
        platform.FireMouseMove(1275, 720);
    }

    // visits and leaves, which pulls, then goes back onto the remote with nothing sent yet; the pull's id
    private static int PullThenReenter(TestServiceBundle bundle)
    {
        VisitAndLeaveRemote(bundle.Platform);
        var pullId = LatestPullId(bundle.Relay);
        TransitionTestHelper.EnterRemote(bundle.Platform);
        bundle.Relay.Sent.Clear();
        return pullId;
    }

    private static List<ClipboardPushMessage> Pushes(FakeRelay relay) => relay.Sent.Of<ClipboardPushMessage>(MessageKind.ClipboardPush);

    private static int LatestPullId(FakeRelay relay) => relay.Sent.Of<ClipboardPullMessage>(MessageKind.ClipboardPull).Last().PullId;

    private static Task AnswerPull(FakeRelay relay, ClipboardPullResponseMessage response, string host = "remote") =>
        relay.FireMessageReceived(host, MessageKind.ClipboardPullResponse, response.ToSaneJson());

    // the slave deciding its hash differs from the master's, and asking for a full push
    private static Task SimulatePullRequest(FakeRelay relay, string host = "remote") =>
        relay.FireMessageReceived(host, MessageKind.ClipboardPullRequest, new ClipboardPullRequestMessage().ToSaneJson());
}
