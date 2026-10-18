using System.Text.Json;
using Hydra.Keyboard;
using Hydra.Relay;
using Tests.Setup;

namespace Tests.Styx;

/// <summary>
/// Key events are BUNDLED — several to a frame — and not one of them is ever lost.
///
/// <para><b>Every test here is about loss, because loss is the only thing that matters.</b> A mouse move
/// that never arrives is invisible; the next one corrects it. A KeyDown that never arrives is a character
/// that was not typed, and a KeyUp that never arrives is a key held down on somebody else's machine until
/// they notice and press it themselves. So the assertions are on the exact sequence received, never on a
/// count or a sample.</para>
///
/// <para>The hazard is structural rather than hypothetical. Appending to an open bundle enqueues NOTHING —
/// it mutates an object an already-queued item points at — so an append that lands after the drain has
/// taken its snapshot goes nowhere at all. The same shape in the movement path provably loses a move when
/// its close is deleted. These tests park the drain to hold that window open on purpose.</para>
/// </summary>
[TestFixture]
public class KeyBundleTests : StyxFixtureBase
{
    private const string Receiver = "receiver";

    /// <summary>
    /// What the spill test's frame counts assume. Pinned against the real constant rather than read from
    /// it, so a retune fails an assertion that names the choice instead of silently sliding the goalposts.
    /// </summary>
    private const int ExpectedCapacity = 64;

    /// <summary>
    /// Every key event that arrived, flattened out of whatever frames carried them, in arrival order.
    ///
    /// <para>Flattening is the point: a test must not care whether an event came alone or in a bundle of
    /// forty, only that it came, once, in its turn. That is exactly the property bundling must not change.</para>
    /// </summary>
    private static List<string> KeysIn(IEnumerable<ReceivedMessage> messages)
    {
        var keys = new List<string>();
        foreach (var (_, kind, json) in messages)
        {
            switch (kind)
            {
                case MessageKind.KeyEvent:
                    keys.Add(One(JsonDocument.Parse(json).RootElement));
                    break;
                case MessageKind.KeyEventBatch:
                    foreach (var element in JsonDocument.Parse(json).RootElement.GetProperty("events").EnumerateArray())
                        keys.Add(One(element));
                    break;
            }
        }

        return keys;

        // Tolerant of how the serializer spells these: an enum may arrive as a name or a number, and a char
        // as a one-character string. The test is about which events arrived, not about the encoding — which
        // StyxWireFormatTests is the right place to pin.
        static string One(JsonElement e)
        {
            var type = e.GetProperty("type");
            var character = e.GetProperty("character");
            return Key(type.ValueKind == JsonValueKind.Number ? (KeyEventType)type.GetInt32() : Enum.Parse<KeyEventType>(type.GetString()!, ignoreCase: true),
                    character.ValueKind == JsonValueKind.Number ? (char)character.GetInt32() : character.GetString()![0]);
        }
    }

    /// <summary>Every frame that carried key events, so a test can assert HOW they were packed.</summary>
    private static List<ReceivedMessage> KeyFrames(IEnumerable<ReceivedMessage> frames) =>
        [.. frames.Where(f => f.Kind is MessageKind.KeyEvent or MessageKind.KeyEventBatch)];

    /// <summary>How many events one frame carried — 1 for a plain KeyEvent, N for a batch.</summary>
    private static int EventsIn(ReceivedMessage frame) =>
        frame.Kind == MessageKind.KeyEvent ? 1 : JsonDocument.Parse(frame.Json).RootElement.GetProperty("events").GetArrayLength();

    // ── nothing is lost ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A burst enqueued while the lane is parked arrives COMPLETE and in order, bundled into fewer frames
    /// than there were events.
    ///
    /// <para>The parked lane is what makes this a burst at all: with the drain running, each event would go
    /// out on its own and there would be nothing to bundle and nothing to lose. The frame count is asserted
    /// too, because a test that only checked the events would pass against an implementation that quietly
    /// stopped bundling — and then this whole change would be dead code nobody noticed.</para>
    /// </summary>
    [Test]
    public async Task ABurstOfKeysArrivesCompleteAndInOrder()
    {
        await using var pair = await ConnectedPair();
        var (sender, receiver) = pair;

        const int count = 40;

        sender.HoldLane(RelayLane.Input);
        var blocker = sender.SendReliableAsync([Receiver], TestMessages.Key(0)).AsTask();
        await sender.WaitUntilLaneHeld();

        for (var i = 1; i <= count; i++) sender.SendKeyEvent([Receiver], TestMessages.KeyMessage(i));

        sender.Send([Receiver], TestMessages.Sentinel());
        sender.ReleaseLane();
        await blocker.WaitAsync(Bound);

        var frames = await receiver.ReadUntilSentinel();
        var expected = Enumerable.Range(0, count + 1).Select(i => Key(KeyEventType.KeyDown, (char)('a' + i % 26))).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(KeysIn(frames), Is.EqualTo(expected), "every key, exactly once, in the order it was typed");
            Assert.That(frames, Has.Count.LessThan(count + 1), "nothing was bundled at all — the frames are one per event");
        }
    }

    /// <summary>
    /// A KeyDown and its KeyUp riding in ONE bundle both arrive, in that order.
    ///
    /// <para>This is the case the whole feature is named for and the one with teeth: typing fast enough that
    /// both halves of a keystroke are queued before either goes out. Deliver only the down and the key is
    /// held on the remote machine for ever.</para>
    /// </summary>
    [Test]
    public async Task ADownAndItsUpInOneBundleBothArriveInOrder()
    {
        await using var pair = await ConnectedPair();
        var (sender, receiver) = pair;

        sender.HoldLane(RelayLane.Input);
        var blocker = sender.SendReliableAsync([Receiver], TestMessages.Move(1)).AsTask();
        await sender.WaitUntilLaneHeld();

        sender.SendKeyEvent([Receiver], TestMessages.KeyMessage(0));
        sender.SendKeyEvent([Receiver], TestMessages.KeyMessage(0, KeyEventType.KeyUp));

        sender.Send([Receiver], TestMessages.Sentinel());
        sender.ReleaseLane();
        await blocker.WaitAsync(Bound);

        var frames = await receiver.ReadUntilSentinel();
        var carrying = KeyFrames(frames);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(KeysIn(frames), Is.EqualTo([Key(KeyEventType.KeyDown, 'a'), Key(KeyEventType.KeyUp, 'a')]),
                "a keystroke that was bundled must still be a press followed by a release");

            // WITHOUT THIS THE TEST IS ABOUT NOTHING. KeysIn flattens a batch and two lone frames into the
            // same list deliberately — right for proving no key was lost, blind to whether the pair was ever
            // bundled. Delete the entire feature and the assertion above still passes.
            Assert.That(carrying, Has.Count.EqualTo(1), "the down and the up must have shared ONE frame — that is the feature");
            Assert.That(carrying[0].Kind, Is.EqualTo(MessageKind.KeyEventBatch));
        }
    }

    /// <summary>
    /// More events than one bundle may hold spill into the next one, losing none and reordering none.
    ///
    /// <para>The cap exists so a stalled relay cannot grow a single frame without limit. It must bound the
    /// frame, never the input — an implementation that dropped the event which did not fit would pass a test
    /// that only counted frames.</para>
    /// </summary>
    [Test]
    public async Task MoreKeysThanOneBundleHoldsSpillIntoTheNextWithoutLoss()
    {
        await using var pair = await ConnectedPair();
        var (sender, receiver) = pair;

        // Comfortably over the cap, so at least three frames are needed.
        const int count = 150;

        sender.HoldLane(RelayLane.Input);
        var blocker = sender.SendReliableAsync([Receiver], TestMessages.Move(1)).AsTask();
        await sender.WaitUntilLaneHeld();

        for (var i = 0; i < count; i++) sender.SendKeyEvent([Receiver], TestMessages.KeyMessage(i));

        sender.Send([Receiver], TestMessages.Sentinel());
        sender.ReleaseLane();
        await blocker.WaitAsync(Bound);

        var expected = Enumerable.Range(0, count).Select(i => Key(KeyEventType.KeyDown, (char)('a' + i % 26))).ToList();

        var frames = await receiver.ReadUntilSentinel();
        var carrying = KeyFrames(frames);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(KeysIn(frames), Is.EqualTo(expected),
                "an event that did not fit the open bundle was dropped instead of starting a new one");

            // The cap bounds the FRAME, and these numbers are LITERAL on purpose. Written against
            // RelayConnection.KeyBundleCapacity the assertions moved with the constant and stopped saying
            // anything: at 10 000 one enormous frame satisfies both "<= capacity" and "count >= 150/10 000",
            // which is zero — so the very tuning the cap exists to refuse passed. Retuning the real constant
            // now fails the line below FIRST, which is where the decision belongs.
            Assert.That(RelayConnection.KeyBundleCapacity, Is.EqualTo(ExpectedCapacity),
                "the cap moved — the frame counts below were chosen for it, so retune them deliberately rather than letting them follow");
            Assert.That(carrying.Max(EventsIn), Is.LessThanOrEqualTo(ExpectedCapacity),
                "a frame carried more events than the cap allows — the cap is not bounding the frame");
            Assert.That(carrying, Has.Count.GreaterThanOrEqualTo(3),
                "150 events did not spill across at least three frames, so the cap is not being applied at all");
            Assert.That(carrying.Any(f => f.Kind == MessageKind.KeyEventBatch), Is.True, "and they were bundled rather than sent one by one");
        }
    }

    /// <summary>
    /// Keys and everything else keep their order relative to one another.
    ///
    /// <para>A bundle may only ever absorb events that are still at the END of the queue. A mouse move
    /// between two keys has to break it, or the second key would be delivered before a move that was made
    /// first — and on a KVM that is a click at the wrong place.</para>
    /// </summary>
    [Test]
    public async Task AMoveBetweenKeysKeepsEverythingInOrder()
    {
        await using var pair = await ConnectedPair();
        var (sender, receiver) = pair;

        sender.HoldLane(RelayLane.Input);
        var blocker = sender.SendReliableAsync([Receiver], TestMessages.Key(25)).AsTask();
        await sender.WaitUntilLaneHeld();

        sender.SendKeyEvent([Receiver], TestMessages.KeyMessage(0));
        sender.Send([Receiver], TestMessages.Move(7));
        sender.SendKeyEvent([Receiver], TestMessages.KeyMessage(1));

        sender.Send([Receiver], TestMessages.Sentinel());
        sender.ReleaseLane();
        await blocker.WaitAsync(Bound);

        var frames = await receiver.ReadUntilSentinel();
        var order = frames.Select(f => f.Kind).Where(k => k is MessageKind.KeyEvent or MessageKind.KeyEventBatch or MessageKind.MouseMove).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(KeysIn(frames), Is.EqualTo([Key(KeyEventType.KeyDown, 'z'), Key(KeyEventType.KeyDown, 'a'), Key(KeyEventType.KeyDown, 'b')]));
            Assert.That(order.IndexOf(MessageKind.MouseMove), Is.GreaterThan(0), "the move must not overtake the keys sent before it");
            Assert.That(order.IndexOf(MessageKind.MouseMove), Is.LessThan(order.Count - 1), "and the key sent after it must not overtake the move");
        }
    }

    /// <summary>
    /// A key sent AFTER the drain has already taken a bundle still arrives.
    ///
    /// <para><b>This is the structural hazard, and the one mutation the other tests here do not catch.</b>
    /// Appending to an open bundle enqueues nothing — it mutates an object the queued item points at — so
    /// once the drain has snapshotted that bundle, a further append writes into something nobody will read
    /// again and the key is gone without a trace. Nothing throws, nothing logs, and the key simply was not
    /// typed.</para>
    ///
    /// <para>The gate is what makes the window deterministic rather than a race: it parks the drain at the
    /// ENCRYPT step, which is after <c>TryReadQueued</c> has taken the snapshot. So once <c>WaitUntilLaneHeld</c>
    /// returns, the first bundle has provably been read — and the key sent next is landing in exactly the gap
    /// that loses it. With the clear in place it starts a fresh bundle; without it, it vanishes.</para>
    /// </summary>
    [Test]
    public async Task AKeySentAfterTheDrainTookTheBundleStillArrives()
    {
        await using var pair = await ConnectedPair();
        var (sender, receiver) = pair;

        sender.HoldLane(RelayLane.Input);

        // Read and snapshotted by the drain, which then parks — so the bundle behind it is spent.
        sender.SendKeyEvent([Receiver], TestMessages.KeyMessage(0));
        await sender.WaitUntilLaneHeld();

        // Into the gap. This must NOT join the bundle that has already been read.
        sender.SendKeyEvent([Receiver], TestMessages.KeyMessage(1));

        sender.Send([Receiver], TestMessages.Sentinel());
        sender.ReleaseLane();

        Assert.That(KeysIn(await receiver.ReadUntilSentinel()),
            Is.EqualTo([Key(KeyEventType.KeyDown, 'a'), Key(KeyEventType.KeyDown, 'b')]),
            "a key appended to a bundle the drain had already taken was never sent at all");
    }

    /// <summary>
    /// A MOUSE DELTA between two keys keeps everything in order — the production relative-mouse path.
    ///
    /// <para><b>This is the case the suite was missing, and it is the one that runs.</b>
    /// <c>AMoveBetweenKeysKeepsEverythingInOrder</c> goes through <c>Send</c> with a MouseMove, but
    /// <c>InputRouter</c> never does that: it calls <c>SendMouseDelta</c> directly, on the hot path, and
    /// says so in a comment. The close in THAT method could be deleted with the whole suite still green.</para>
    ///
    /// <para>Deleted, the second key joins the bundle already queued ahead of the delta and is delivered
    /// BEFORE a movement the user made first — a keystroke landing at the wrong cursor position. Relative
    /// mouse mode is exactly when the cursor is on a remote screen and keys are being forwarded, so this is
    /// the common concurrent case rather than a corner of it.</para>
    /// </summary>
    [Test]
    public async Task AMouseDeltaBetweenKeysKeepsEverythingInOrder()
    {
        await using var pair = await ConnectedPair();
        var (sender, receiver) = pair;

        sender.HoldLane(RelayLane.Input);
        var blocker = sender.SendReliableAsync([Receiver], TestMessages.Move(1)).AsTask();
        await sender.WaitUntilLaneHeld();

        sender.SendKeyEvent([Receiver], TestMessages.KeyMessage(0));
        sender.SendMouseDelta([Receiver], 7, 9);
        sender.SendKeyEvent([Receiver], TestMessages.KeyMessage(1));

        sender.Send([Receiver], TestMessages.Sentinel());
        sender.ReleaseLane();
        await blocker.WaitAsync(Bound);

        var frames = await receiver.ReadUntilSentinel();
        var kinds = frames.Select(f => f.Kind).ToList();
        var delta = kinds.IndexOf(MessageKind.MouseMoveDelta);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(KeysIn(frames), Is.EqualTo([Key(KeyEventType.KeyDown, 'a'), Key(KeyEventType.KeyDown, 'b')]),
                "both keys must arrive, in order");
            Assert.That(delta, Is.GreaterThan(0), "the delta must not overtake the key sent before it");
            Assert.That(delta, Is.LessThan(kinds.Count - 1), "and the key sent after the delta must not overtake it");
        }
    }

    /// <summary>
    /// And a reliable control message between two keys does the same. Same rule, third site — the one that
    /// carries clipboard pushes, OSD, lock-screen and remote-management requests.
    /// </summary>
    [Test]
    public async Task AReliableMessageBetweenKeysKeepsEverythingInOrder()
    {
        await using var pair = await ConnectedPair();
        var (sender, receiver) = pair;

        sender.HoldLane(RelayLane.Input);
        var blocker = sender.SendReliableAsync([Receiver], TestMessages.Move(1)).AsTask();
        await sender.WaitUntilLaneHeld();

        sender.SendKeyEvent([Receiver], TestMessages.KeyMessage(0));
        var between = sender.SendReliableAsync([Receiver], MessageSerializer.Encode(MessageKind.LockScreen, new LockScreenMessage(0))).AsTask();
        sender.SendKeyEvent([Receiver], TestMessages.KeyMessage(1));

        sender.Send([Receiver], TestMessages.Sentinel());
        sender.ReleaseLane();
        await Task.WhenAll(blocker, between).WaitAsync(Bound);

        var frames = await receiver.ReadUntilSentinel();
        var kinds = frames.Select(f => f.Kind).ToList();
        var lockScreen = kinds.IndexOf(MessageKind.LockScreen);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(KeysIn(frames), Is.EqualTo([Key(KeyEventType.KeyDown, 'a'), Key(KeyEventType.KeyDown, 'b')]));
            Assert.That(lockScreen, Is.GreaterThan(0), "the control message must not overtake the key before it");
            Assert.That(lockScreen, Is.LessThan(kinds.Count - 1), "nor the key after it overtake the control message");
        }
    }

    /// <summary>
    /// Randomised, because a fixed sequence only ever proves the fixed sequence.
    ///
    /// <para>A fixed seed, so a failure is reproducible rather than a story about one unlucky run.</para>
    /// </summary>
    [Test]
    public async Task ARandomisedStreamOfKeysRoundTripsExactly()
    {
        await using var pair = await ConnectedPair();
        var (sender, receiver) = pair;

        var random = new Random(20260921);
        var sent = new List<string>();

        sender.HoldLane(RelayLane.Input);
        var blocker = sender.SendReliableAsync([Receiver], TestMessages.Move(1)).AsTask();
        await sender.WaitUntilLaneHeld();

        for (var i = 0; i < 300; i++)
        {
            var type = random.Next(2) == 0 ? KeyEventType.KeyDown : KeyEventType.KeyUp;
            var n = random.Next(26);
            sender.SendKeyEvent([Receiver], TestMessages.KeyMessage(n, type));
            sent.Add(Key(type, (char)('a' + n)));
        }

        sender.Send([Receiver], TestMessages.Sentinel());
        sender.ReleaseLane();
        await blocker.WaitAsync(Bound);

        Assert.That(KeysIn(await receiver.ReadUntilSentinel()), Is.EqualTo(sent));
    }

    /// <summary>
    /// A move made AFTER a keystroke is never delivered before it.
    ///
    /// <para>The mirror of <see cref="AMoveBetweenKeysKeepsEverythingInOrder"/>, and it was the uncovered
    /// direction: that test is key→move→key, this one is move→key→move. Opening the key bundle has to close
    /// the movement batch, or the second move merges into a batch queued BEFORE the key — so a move the
    /// user made after typing is delivered first, and at the coalesced position of both. On a KVM that is a
    /// click at the wrong place.</para>
    ///
    /// <para>Deleting that one close breaks nothing else in this fixture, which is why it is here.</para>
    /// </summary>
    [Test]
    public async Task AMoveMadeAfterAKeyIsNeverDeliveredBeforeIt()
    {
        await using var pair = await ConnectedPair();
        var (sender, receiver) = pair;

        sender.HoldLane(RelayLane.Input);
        var blocker = sender.SendReliableAsync([Receiver], TestMessages.Key(25)).AsTask();
        await sender.WaitUntilLaneHeld();

        sender.Send([Receiver], TestMessages.Move(3));
        sender.SendKeyEvent([Receiver], TestMessages.KeyMessage(0));
        sender.Send([Receiver], TestMessages.Move(9));

        sender.Send([Receiver], TestMessages.Sentinel());
        sender.ReleaseLane();
        await blocker.WaitAsync(Bound);

        var frames = await receiver.ReadUntilSentinel();
        var order = frames.Select(f => f.Kind).Where(k => k is MessageKind.KeyEvent or MessageKind.KeyEventBatch or MessageKind.MouseMove).ToList();

        Assert.That(order, Is.EqualTo([MessageKind.KeyEvent, MessageKind.MouseMove, MessageKind.KeyEvent, MessageKind.MouseMove]),
            "the move made after the key merged into the batch queued before it, so it was delivered ahead of the keystroke and at the wrong position");
    }

    /// <summary>
    /// One key travels as an ordinary <c>KeyEvent</c>, never as a batch of one.
    ///
    /// <para>The common case by far — nobody types fast enough to queue two keys behind one frame most of
    /// the time. The lane is parked so the
    /// event provably goes through the bundling path rather than round it: this is <c>Snapshot</c>'s rule,
    /// not an accident of the drain being quick.</para>
    /// </summary>
    [Test]
    public async Task ALoneKeyTravelsAsAPlainKeyEventNotABatchOfOne()
    {
        await using var pair = await ConnectedPair();
        var (sender, receiver) = pair;

        sender.HoldLane(RelayLane.Input);
        var blocker = sender.SendReliableAsync([Receiver], TestMessages.Move(1)).AsTask();
        await sender.WaitUntilLaneHeld();

        sender.SendKeyEvent([Receiver], TestMessages.KeyMessage(0));

        sender.Send([Receiver], TestMessages.Sentinel());
        sender.ReleaseLane();
        await blocker.WaitAsync(Bound);

        var frames = await receiver.ReadUntilSentinel();
        var carrying = KeyFrames(frames);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(KeysIn(frames), Is.EqualTo([Key(KeyEventType.KeyDown, 'a')]));
            Assert.That(carrying.Select(f => f.Kind), Is.EqualTo([MessageKind.KeyEvent]),
                "a single event was wrapped in a batch");
        }
    }

    /// <summary>
    /// A key addressed to another host never joins the frame open for this one.
    ///
    /// <para>A bundle is keyed on its TARGET list and the frame goes to those targets, so absorbing an
    /// event meant for somebody else delivers it to the wrong machine AND never to the right one. The
    /// guard is defence in depth — switching screens emits an <c>EnterScreen</c>, which closes the bundle
    /// anyway — but nothing tested it, and "defended twice" is only true while both defences exist.</para>
    ///
    /// <para>The other host never connects, which is the point: its frame goes nowhere, so anything of its
    /// arriving HERE could only have come out of this receiver's bundle.</para>
    /// </summary>
    [Test]
    public async Task AKeyForAnotherTargetNeverJoinsThisOnesFrame()
    {
        await using var pair = await ConnectedPair();
        var (sender, receiver) = pair;

        const string other = "somebody-else";
        sender.HoldLane(RelayLane.Input);
        var blocker = sender.SendReliableAsync([Receiver], TestMessages.Move(1)).AsTask();
        await sender.WaitUntilLaneHeld();

        sender.SendKeyEvent([Receiver], TestMessages.KeyMessage(0));
        sender.SendKeyEvent([other], TestMessages.KeyMessage(1));
        sender.SendKeyEvent([Receiver], TestMessages.KeyMessage(2));

        sender.Send([Receiver], TestMessages.Sentinel());
        sender.ReleaseLane();
        await blocker.WaitAsync(Bound);

        Assert.That(KeysIn(await receiver.ReadUntilSentinel()),
            Is.EqualTo([Key(KeyEventType.KeyDown, 'a'), Key(KeyEventType.KeyDown, 'c')]),
            "a key addressed to another host was delivered here, which means it was never delivered there");
    }

    // a key event as the assertions name it
    private static string Key(KeyEventType type, char character) => $"{type} {character}";
}
