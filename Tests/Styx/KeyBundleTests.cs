using System.Text.Json;
using Hydra.Config;
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
public class KeyBundleTests
{
    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<global::Styx.Program>? _factory;

    private const string Receiver = "receiver";

    [OneTimeSetUp]
    public static void OneTimeSetUp()
    {
        _factory = StyxTestServer.Create();
        _ = _factory.Server;
    }

    [OneTimeTearDown]
    public static async Task OneTimeTearDown()
    {
        if (_factory != null) await _factory.DisposeAsync();
    }

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// What the spill test's frame counts assume. Pinned against the real constant rather than read from
    /// it, so a retune fails an assertion that names the choice instead of silently sliding the goalposts.
    /// </summary>
    private const int ExpectedCapacity = 64;

    /// <param name="peerTakesBundles">
    /// What the receiver advertised. FALSE is the un-upgraded peer, and it is not a corner case — it is
    /// every slave in the field until it is updated.
    /// </param>
    private static async Task<(HydraTestClient Sender, HydraTestClient Receiver)> ConnectedPair(bool peerTakesBundles = true)
    {
        var cfg = await StyxTestServer.BuildNetworkConfig(_factory!, Guid.NewGuid());

        var sender = new HydraTestClient(_factory!, TransitionTestHelper.Profile("sender", new HydraConfig { Mode = Mode.Master, NetworkConfig = cfg }));
        var receiver = new HydraTestClient(_factory!, TransitionTestHelper.Profile(Receiver, new HydraConfig { Mode = Mode.Master, NetworkConfig = cfg }));

        sender.World.SetPeerCapabilities(Receiver,
            PeerCapabilities.Parse(peerTakesBundles ? PeerCapabilities.Advertise() : null));

        await sender.StartAsync(CancellationToken.None);
        await receiver.StartAsync(CancellationToken.None);
        await sender.WaitForReady();
        await receiver.WaitForReady();

        return (sender, receiver);
    }

    /// <summary>One key event, identified by the character it carries so a sequence can be checked exactly.</summary>
    private static KeyEventMessage KeyMessage(int n, KeyEventType type = KeyEventType.KeyDown) =>
        new(type, KeyModifiers.None, (char)('a' + n % 26), null);

    private static byte[] Key(int n, KeyEventType type = KeyEventType.KeyDown) =>
        MessageSerializer.Encode(MessageKind.KeyEvent, KeyMessage(n, type));

    /// <summary>
    /// One key, through whichever of the two entry points the case names.
    ///
    /// <para><b>Both, because production and these tests were using different ones.</b>
    /// <c>InputRouter</c> forwards a keystroke through <c>SendKeyEvent</c>; every test here called
    /// <c>Send</c> with an encoded payload. Measured: replacing <c>RelayConnection.SendKeyEvent</c>'s body
    /// with a silent drop left <b>297</b> tests green — a master that types nothing, ever, with nothing to
    /// say so. The two are meant to be one behaviour, and the only thing that keeps them one is asserting
    /// every claim below against each.</para>
    ///
    /// <para><c>typed: true</c> is the production path.</para>
    /// </summary>
    private static void SendKey(bool typed, HydraTestClient sender, string[] targets, int n, KeyEventType type = KeyEventType.KeyDown)
    {
        if (typed) sender.SendKeyEvent(targets, KeyMessage(n, type));
        else sender.Send(targets, Key(n, type));
    }

    private static byte[] Move(int n) => MessageSerializer.Encode(MessageKind.MouseMove, new MouseMoveMessage("", n, n));

    /// <summary>
    /// Every key event that arrived, flattened out of whatever frames carried them, in arrival order.
    ///
    /// <para>Flattening is the point: a test must not care whether an event came alone or in a bundle of
    /// forty, only that it came, once, in its turn. That is exactly the property bundling must not change.</para>
    /// </summary>
    private static List<(KeyEventType Type, char Character)> KeysIn(IEnumerable<(string Source, MessageKind Kind, string Json)> messages)
    {
        var keys = new List<(KeyEventType, char)>();
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
        static (KeyEventType, char) One(JsonElement e)
        {
            var type = e.GetProperty("type");
            var character = e.GetProperty("character");
            return (type.ValueKind == JsonValueKind.Number ? (KeyEventType)type.GetInt32() : Enum.Parse<KeyEventType>(type.GetString()!, ignoreCase: true),
                    character.ValueKind == JsonValueKind.Number ? (char)character.GetInt32() : character.GetString()![0]);
        }
    }

    /// <summary>
    /// A message nothing else here sends, last, to mark the end of what a test expects.
    ///
    /// <para><b>A kind that coalesces is the wrong terminator.</b> This was a MouseMove, and it survived
    /// only because a key happened to precede it every time and the key path closes the movement batch —
    /// put a move before the sentinel and it merges into that batch, the reader returns early, and every
    /// test in the fixture fails for a reason with nothing to do with keys. <c>Osd</c> neither coalesces
    /// nor bundles, so it takes a frame of its own whatever came before it.</para>
    /// </summary>
    private static byte[] Sentinel() => MessageSerializer.Encode(MessageKind.Osd, new OsdMessage("end-of-test"));

    /// <summary>Every frame that carried key events, so a test can assert HOW they were packed.</summary>
    private static List<(string Source, MessageKind Kind, string Json)> KeyFrames(IEnumerable<(string Source, MessageKind Kind, string Json)> frames) =>
        [.. frames.Where(f => f.Kind is MessageKind.KeyEvent or MessageKind.KeyEventBatch)];

    /// <summary>How many events one frame carried — 1 for a plain KeyEvent, N for a batch.</summary>
    private static int EventsIn((string Source, MessageKind Kind, string Json) frame) =>
        frame.Kind == MessageKind.KeyEvent ? 1 : JsonDocument.Parse(frame.Json).RootElement.GetProperty("events").GetArrayLength();

    /// <summary>
    /// Reads frames until the sentinel arrives, and hands back every key event among them.
    ///
    /// <para><b>Bounded by the sentinel and never by a count of what SHOULD arrive.</b> Waiting for a
    /// number of events turns every lost key into a fifteen-second timeout, and a timeout says "something
    /// is slow" where these tests have to say "a key went missing". Losing keys must fail on the
    /// assertion, naming the sequence that arrived.</para>
    /// </summary>
    private static async Task<List<(string Source, MessageKind Kind, string Json)>> ReadUntilSentinel(HydraTestClient receiver)
    {
        var frames = new List<(string Source, MessageKind Kind, string Json)>();
        while (true)
        {
            var frame = await receiver.WaitForNextMessage();
            if (frame.Kind == MessageKind.Osd) return frames;
            frames.Add(frame);
        }
    }

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
    [TestCase(false)]
    [TestCase(true)]
    public async Task ABurstOfKeysArrivesCompleteAndInOrder(bool typed)
    {
        var (sender, receiver) = await ConnectedPair();
        await using var _ = sender;
        await using var __ = receiver;

        const int count = 40;

        sender.HoldLane(RelayLane.Input);
        var blocker = sender.SendReliableAsync([Receiver], Key(0)).AsTask();
        await WaitFor(() => sender.Held == 1, "the input lane to park");

        for (var i = 1; i <= count; i++) SendKey(typed, sender, [Receiver], i);

        sender.Send([Receiver], Sentinel());
        sender.ReleaseLane();
        await blocker.WaitAsync(Bound);

        var frames = await ReadUntilSentinel(receiver);
        var expected = Enumerable.Range(0, count + 1).Select(i => (KeyEventType.KeyDown, (char)('a' + i % 26))).ToList();

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
    [TestCase(false)]
    [TestCase(true)]
    public async Task ADownAndItsUpInOneBundleBothArriveInOrder(bool typed)
    {
        var (sender, receiver) = await ConnectedPair();
        await using var _ = sender;
        await using var __ = receiver;

        sender.HoldLane(RelayLane.Input);
        var blocker = sender.SendReliableAsync([Receiver], Move(1)).AsTask();
        await WaitFor(() => sender.Held == 1, "the input lane to park");

        SendKey(typed, sender, [Receiver], 0);
        SendKey(typed, sender, [Receiver], 0, KeyEventType.KeyUp);

        sender.Send([Receiver], Sentinel());
        sender.ReleaseLane();
        await blocker.WaitAsync(Bound);

        var frames = await ReadUntilSentinel(receiver);
        var carrying = KeyFrames(frames);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(KeysIn(frames), Is.EqualTo([(KeyEventType.KeyDown, 'a'), (KeyEventType.KeyUp, 'a')]),
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
    [TestCase(false)]
    [TestCase(true)]
    public async Task MoreKeysThanOneBundleHoldsSpillIntoTheNextWithoutLoss(bool typed)
    {
        var (sender, receiver) = await ConnectedPair();
        await using var _ = sender;
        await using var __ = receiver;

        // Comfortably over the cap, so at least three frames are needed.
        const int count = 150;

        sender.HoldLane(RelayLane.Input);
        var blocker = sender.SendReliableAsync([Receiver], Move(1)).AsTask();
        await WaitFor(() => sender.Held == 1, "the input lane to park");

        for (var i = 0; i < count; i++) SendKey(typed, sender, [Receiver], i);

        sender.Send([Receiver], Sentinel());
        sender.ReleaseLane();
        await blocker.WaitAsync(Bound);

        var expected = Enumerable.Range(0, count).Select(i => (KeyEventType.KeyDown, (char)('a' + i % 26))).ToList();

        var frames = await ReadUntilSentinel(receiver);
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
        var (sender, receiver) = await ConnectedPair();
        await using var _ = sender;
        await using var __ = receiver;

        sender.HoldLane(RelayLane.Input);
        var blocker = sender.SendReliableAsync([Receiver], Key(25)).AsTask();
        await WaitFor(() => sender.Held == 1, "the input lane to park");

        sender.Send([Receiver], Key(0));
        sender.Send([Receiver], Move(7));
        sender.Send([Receiver], Key(1));

        sender.Send([Receiver], Sentinel());
        sender.ReleaseLane();
        await blocker.WaitAsync(Bound);

        var frames = await ReadUntilSentinel(receiver);
        var order = frames.Select(f => f.Kind).Where(k => k is MessageKind.KeyEvent or MessageKind.KeyEventBatch or MessageKind.MouseMove).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(KeysIn(frames), Is.EqualTo([(KeyEventType.KeyDown, 'z'), (KeyEventType.KeyDown, 'a'), (KeyEventType.KeyDown, 'b')]));
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
    /// ENCRYPT step, which is after <c>TryReadQueued</c> has taken the snapshot. So when <c>Held</c> reaches
    /// one, the first bundle has provably been read — and the key sent next is landing in exactly the gap
    /// that loses it. With the clear in place it starts a fresh bundle; without it, it vanishes.</para>
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task AKeySentAfterTheDrainTookTheBundleStillArrives(bool typed)
    {
        var (sender, receiver) = await ConnectedPair();
        await using var _ = sender;
        await using var __ = receiver;

        sender.HoldLane(RelayLane.Input);

        // Read and snapshotted by the drain, which then parks — so the bundle behind it is spent.
        SendKey(typed, sender, [Receiver], 0);
        await WaitFor(() => sender.Held == 1, "the drain to take the first bundle and park");

        // Into the gap. This must NOT join the bundle that has already been read.
        SendKey(typed, sender, [Receiver], 1);

        sender.Send([Receiver], Sentinel());
        sender.ReleaseLane();

        Assert.That(KeysIn(await ReadUntilSentinel(receiver)),
            Is.EqualTo([(KeyEventType.KeyDown, 'a'), (KeyEventType.KeyDown, 'b')]),
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
        var (sender, receiver) = await ConnectedPair();
        await using var _ = sender;
        await using var __ = receiver;

        sender.HoldLane(RelayLane.Input);
        var blocker = sender.SendReliableAsync([Receiver], Move(1)).AsTask();
        await WaitFor(() => sender.Held == 1, "the input lane to park");

        sender.Send([Receiver], Key(0));
        sender.SendMouseDelta([Receiver], 7, 9);
        sender.Send([Receiver], Key(1));

        sender.Send([Receiver], Sentinel());
        sender.ReleaseLane();
        await blocker.WaitAsync(Bound);

        var frames = await ReadUntilSentinel(receiver);
        var kinds = frames.Select(f => f.Kind).ToList();
        var delta = kinds.IndexOf(MessageKind.MouseMoveDelta);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(KeysIn(frames), Is.EqualTo([(KeyEventType.KeyDown, 'a'), (KeyEventType.KeyDown, 'b')]),
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
        var (sender, receiver) = await ConnectedPair();
        await using var _ = sender;
        await using var __ = receiver;

        sender.HoldLane(RelayLane.Input);
        var blocker = sender.SendReliableAsync([Receiver], Move(1)).AsTask();
        await WaitFor(() => sender.Held == 1, "the input lane to park");

        sender.Send([Receiver], Key(0));
        var between = sender.SendReliableAsync([Receiver], MessageSerializer.Encode(MessageKind.LockScreen, new LockScreenMessage(0))).AsTask();
        sender.Send([Receiver], Key(1));

        sender.Send([Receiver], Sentinel());
        sender.ReleaseLane();
        await Task.WhenAll(blocker, between).WaitAsync(Bound);

        var frames = await ReadUntilSentinel(receiver);
        var kinds = frames.Select(f => f.Kind).ToList();
        var lockScreen = kinds.IndexOf(MessageKind.LockScreen);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(KeysIn(frames), Is.EqualTo([(KeyEventType.KeyDown, 'a'), (KeyEventType.KeyDown, 'b')]));
            Assert.That(lockScreen, Is.GreaterThan(0), "the control message must not overtake the key before it");
            Assert.That(lockScreen, Is.LessThan(kinds.Count - 1), "nor the key after it overtake the control message");
        }
    }

    // ── and nothing is bundled where it would be discarded ───────────────────────────────────────

    /// <summary>
    /// A peer that has NOT advertised support is sent one <c>KeyEvent</c> per frame, never a batch.
    ///
    /// <para><b>This is the safety property the whole design turns on.</b> An unrecognised message kind
    /// reaches <c>RelayConnection.OnReceive</c>'s base implementation, which does nothing whatsoever — so a
    /// batch sent to a slave that predates this feature would not fail, or warn, or retry. Every key in it
    /// would simply never be typed.</para>
    ///
    /// <para><b>REMOVE AFTER 2026-10-30, with the negotiation itself.</b> This is the one test here about
    /// asking rather than about bundling, so it is the one that goes. Everything else in this fixture is
    /// about not losing keys and outlives the upgrade window — see <see cref="PeerCapabilities.Mine"/> for
    /// the full removal list.</para>
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task APeerThatNeverAdvertisedSupportIsNeverSentABatch(bool typed)
    {
        var (sender, receiver) = await ConnectedPair(peerTakesBundles: false);
        await using var _ = sender;
        await using var __ = receiver;

        const int count = 20;

        sender.HoldLane(RelayLane.Input);
        var blocker = sender.SendReliableAsync([Receiver], Move(1)).AsTask();
        await WaitFor(() => sender.Held == 1, "the input lane to park");

        for (var i = 0; i < count; i++) SendKey(typed, sender, [Receiver], i);

        sender.Send([Receiver], Sentinel());
        sender.ReleaseLane();
        await blocker.WaitAsync(Bound);

        var frames = await ReadUntilSentinel(receiver);
        var expected = Enumerable.Range(0, count).Select(i => (KeyEventType.KeyDown, (char)('a' + i % 26))).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(frames.Any(f => f.Kind == MessageKind.KeyEventBatch), Is.False,
                "a batch was sent to a peer that never said it understands one — it would discard every key in it silently");
            Assert.That(KeysIn(frames), Is.EqualTo(expected), "and the keys must still all arrive, one frame each");
            Assert.That(KeyFrames(frames), Has.Count.EqualTo(count), "one frame per key is what an un-advertising peer must get");
        }
    }

    /// <summary>
    /// The positive control for <see cref="APeerThatNeverAdvertisedSupportIsNeverSentABatch"/>: a peer that
    /// DID advertise is sent one.
    ///
    /// <para><b>A negative assertion with no positive counterpart is satisfied by the feature not
    /// existing.</b> "No batch appears" is trivially true of a build that never bundles, so on its own it
    /// proves the gate works only in the sense that nothing works.</para>
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task APeerThatAdvertisedSupportIsSentOne(bool typed)
    {
        var (sender, receiver) = await ConnectedPair();
        await using var _ = sender;
        await using var __ = receiver;

        sender.HoldLane(RelayLane.Input);
        var blocker = sender.SendReliableAsync([Receiver], Move(1)).AsTask();
        await WaitFor(() => sender.Held == 1, "the input lane to park");

        for (var i = 0; i < 20; i++) SendKey(typed, sender, [Receiver], i);

        sender.Send([Receiver], Sentinel());
        sender.ReleaseLane();
        await blocker.WaitAsync(Bound);

        var frames = await ReadUntilSentinel(receiver);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(frames.Any(f => f.Kind == MessageKind.KeyEventBatch), Is.True,
                "an advertising peer was sent no batch at all — the gate is refusing everyone, or nothing bundles");
            Assert.That(KeyFrames(frames), Has.Count.LessThan(20), "and the keys were actually packed together");
        }
    }

    /// <summary>
    /// Randomised, because a fixed sequence only ever proves the fixed sequence.
    ///
    /// <para>A fixed seed, so a failure is reproducible rather than a story about one unlucky run.</para>
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task ARandomisedStreamOfKeysRoundTripsExactly(bool typed)
    {
        var (sender, receiver) = await ConnectedPair();
        await using var _ = sender;
        await using var __ = receiver;

        var random = new Random(20260921);
        var sent = new List<(KeyEventType Type, char Character)>();

        sender.HoldLane(RelayLane.Input);
        var blocker = sender.SendReliableAsync([Receiver], Move(1)).AsTask();
        await WaitFor(() => sender.Held == 1, "the input lane to park");

        for (var i = 0; i < 300; i++)
        {
            var type = random.Next(2) == 0 ? KeyEventType.KeyDown : KeyEventType.KeyUp;
            var n = random.Next(26);
            SendKey(typed, sender, [Receiver], n, type);
            sent.Add((type, (char)('a' + n)));
        }

        sender.Send([Receiver], Sentinel());
        sender.ReleaseLane();
        await blocker.WaitAsync(Bound);

        Assert.That(KeysIn(await ReadUntilSentinel(receiver)), Is.EqualTo(sent));
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
        var (sender, receiver) = await ConnectedPair();
        await using var _ = sender;
        await using var __ = receiver;

        sender.HoldLane(RelayLane.Input);
        var blocker = sender.SendReliableAsync([Receiver], Key(25)).AsTask();
        await WaitFor(() => sender.Held == 1, "the input lane to park");

        sender.Send([Receiver], Move(3));
        sender.Send([Receiver], Key(0));
        sender.Send([Receiver], Move(9));

        sender.Send([Receiver], Sentinel());
        sender.ReleaseLane();
        await blocker.WaitAsync(Bound);

        var frames = await ReadUntilSentinel(receiver);
        var order = frames.Select(f => f.Kind).Where(k => k is MessageKind.KeyEvent or MessageKind.KeyEventBatch or MessageKind.MouseMove).ToList();

        Assert.That(order, Is.EqualTo([MessageKind.KeyEvent, MessageKind.MouseMove, MessageKind.KeyEvent, MessageKind.MouseMove]),
            "the move made after the key merged into the batch queued before it, so it was delivered ahead of the keystroke and at the wrong position");
    }

    /// <summary>
    /// One key travels as an ordinary <c>KeyEvent</c>, never as a batch of one.
    ///
    /// <para>The common case by far — nobody types fast enough to queue two keys behind one frame most of
    /// the time — and the wire shape a peer that predates batching depends on. The lane is parked so the
    /// event provably goes through the bundling path rather than round it: this is <c>Snapshot</c>'s rule,
    /// not an accident of the drain being quick.</para>
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task ALoneKeyTravelsAsAPlainKeyEventNotABatchOfOne(bool typed)
    {
        var (sender, receiver) = await ConnectedPair();
        await using var _ = sender;
        await using var __ = receiver;

        sender.HoldLane(RelayLane.Input);
        var blocker = sender.SendReliableAsync([Receiver], Move(1)).AsTask();
        await WaitFor(() => sender.Held == 1, "the input lane to park");

        SendKey(typed, sender, [Receiver], 0);

        sender.Send([Receiver], Sentinel());
        sender.ReleaseLane();
        await blocker.WaitAsync(Bound);

        var frames = await ReadUntilSentinel(receiver);
        var carrying = KeyFrames(frames);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(KeysIn(frames), Is.EqualTo([(KeyEventType.KeyDown, 'a')]));
            Assert.That(carrying.Select(f => f.Kind), Is.EqualTo([MessageKind.KeyEvent]),
                "a single event was wrapped in a batch — an older peer discards that kind in silence, so the keystroke is simply lost");
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
    /// arriving HERE could only have come out of this receiver's bundle. It is given the capability by hand
    /// because otherwise the batch gate refuses it first and the guard under test is never reached.</para>
    /// </summary>
    [Test]
    public async Task AKeyForAnotherTargetNeverJoinsThisOnesFrame()
    {
        var (sender, receiver) = await ConnectedPair();
        await using var _ = sender;
        await using var __ = receiver;

        const string other = "somebody-else";
        sender.World.SetPeerCapabilities(other, PeerCapabilities.Parse(PeerCapabilities.Advertise()));

        sender.HoldLane(RelayLane.Input);
        var blocker = sender.SendReliableAsync([Receiver], Move(1)).AsTask();
        await WaitFor(() => sender.Held == 1, "the input lane to park");

        sender.Send([Receiver], Key(0));
        sender.Send([other], Key(1));
        sender.Send([Receiver], Key(2));

        sender.Send([Receiver], Sentinel());
        sender.ReleaseLane();
        await blocker.WaitAsync(Bound);

        Assert.That(KeysIn(await ReadUntilSentinel(receiver)),
            Is.EqualTo([(KeyEventType.KeyDown, 'a'), (KeyEventType.KeyDown, 'c')]),
            "a key addressed to another host was delivered here, which means it was never delivered there");
    }

    private static async Task WaitFor(Func<bool> condition, string what, int timeoutMs = 15000)
    {
        using var cancel = new CancellationTokenSource(timeoutMs);
        try
        {
            while (!condition()) await Task.Delay(10, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"Timed out waiting for {what}");
        }
    }
}
