using System.Text.Json;
using Cathedral.Config;
using Hydra.Keyboard;
using Hydra.Relay;
using Tests.Setup;

namespace Tests.Relay;

/// <summary>
/// What a slave actually does with a bundle: it injects every event, in order, into the local machine.
///
/// <para><b>The send-side tests prove a bundle reaches the peer; these prove it is not then dropped on the
/// floor.</b> Those are two different failures with the same symptom — a character that was never typed, or
/// a key left held down — and only one of them is visible from the sending end.</para>
/// </summary>
[TestFixture]
public class KeyBatchDeliveryTests
{
    private const string Master = "master-pc";

    private static string Json<T>(T message) => JsonSerializer.Serialize(message, SaneJson.Options);

    private static KeyEventMessage Key(char c, KeyEventType type = KeyEventType.KeyDown) =>
        new(type, KeyModifiers.None, c, null);

    [Test]
    public async Task EveryEventInABatchIsInjectedInOrder()
    {
        var slave = new TestableSlaveRelay();

        KeyEventMessage[] events =
        [
            Key('h'), Key('h', KeyEventType.KeyUp),
            Key('i'), Key('i', KeyEventType.KeyUp)
        ];

        await slave.SimulateReceive(Master, MessageKind.KeyEventBatch, Json(new KeyEventBatchMessage(events)));

        Assert.That(slave.Output.Keys.Select(k => (k.Type, k.Character)),
            Is.EqualTo(events.Select(e => (e.Type, e.Character))),
            "a bundle must be applied whole and in order — a missing KeyUp leaves that key held down here");
    }

    /// <summary>
    /// A batch of one is the same as the single event it carries. The sender only emits a batch when it has
    /// more than one, but a peer must not depend on that to be correct.
    /// </summary>
    [Test]
    public async Task ABatchOfOneIsAppliedLikeAnyOtherKey()
    {
        var slave = new TestableSlaveRelay();

        await slave.SimulateReceive(Master, MessageKind.KeyEventBatch, Json(new KeyEventBatchMessage([Key('q')])));

        Assert.That(slave.Output.Keys.Select(k => k.Character), Is.EqualTo(new char?[] { 'q' }));
    }

    /// <summary>
    /// An empty batch injects nothing and does not throw. Nothing sends one; a peer that did must not be
    /// able to take the slave's receive loop down with it.
    /// </summary>
    [Test]
    public async Task AnEmptyBatchIsHarmless()
    {
        var slave = new TestableSlaveRelay();

        await slave.SimulateReceive(Master, MessageKind.KeyEventBatch, Json(new KeyEventBatchMessage([])));

        Assert.That(slave.Output.Keys, Is.Empty);
    }

    /// <summary>
    /// A batch with no event list at all injects nothing, rather than throwing on a null.
    ///
    /// <para>Deliberately NOT a test that malformed JSON is survivable: that is <c>RelayConnection.Receive</c>'s
    /// catch-all, which every message kind relies on and which this harness calls past. What belongs here is
    /// the shape this handler is responsible for — a payload that parses cleanly and carries nothing.</para>
    /// </summary>
    [Test]
    public async Task ABatchWithNoEventsAtAllIsHarmless()
    {
        var slave = new TestableSlaveRelay();

        await slave.SimulateReceive(Master, MessageKind.KeyEventBatch, "{\"events\":null}");

        Assert.That(slave.Output.Keys, Is.Empty);
    }

    /// <summary>
    /// The slave TELLS the master it can take bundles, through the capability list it puts on ScreenInfo.
    /// Without this the master never sends one — which is the safe direction, and exactly why forgetting to
    /// advertise would be invisible: everything keeps working, one frame per key, and the feature is simply
    /// never used.
    /// </summary>
    [Test]
    public async Task TheSlaveAdvertisesThatItAppliesBundles()
    {
        var slave = new TestableSlaveRelay();

        // MasterConfig is what makes a slave answer with its screens — and therefore with what it can do.
        await slave.SimulateReceive(Master, MessageKind.MasterConfig, Json(new MasterConfigMessage(null)));

        var screenInfo = slave.Sent.LastOrDefault(s => s.Kind == MessageKind.ScreenInfo);
        Assert.That(screenInfo.Json, Is.Not.Null, "the slave never sent ScreenInfo, so it never advertised anything");

        var advertised = JsonDocument.Parse(screenInfo.Json).RootElement.TryGetProperty("capabilities", out var list)
            ? PeerCapabilities.Parse([.. list.EnumerateArray().Select(e => e.GetString()!)])
            : PeerCapabilities.Parse(null);

        Assert.That(advertised, Does.Contain(PeerCapability.KeyEventBatch),
            "a slave that does not advertise is never sent a bundle, and the feature is dead");
    }
}
