using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Platform;
using Hydra.Relay;

namespace Tests.Platform;

[TestFixture]
public class CoalescingOutputWrapperTests
{
    private RecordingOutput _inner = null!;
    private CoalescingOutputWrapper _wrapper = null!;

    [SetUp]
    public void SetUp()
    {
        _inner = new RecordingOutput();
        _wrapper = new CoalescingOutputWrapper(_inner);
    }

    [TearDown]
    public void TearDown()
    {
        _wrapper.Dispose();
        _inner.Dispose();
    }

    [Test]
    public void AbsoluteMoves_AreCoalesced_ToLatest()
    {
        _wrapper.MoveMouse(100, 200);
        _wrapper.MoveMouse(300, 400);
        _wrapper.MoveMouse(500, 600);

        _wrapper.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, 'a', null));
        Drain();

        var moves = _inner.Events.OfType<MoveEvent>().ToList();
        Assert.That(moves, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(moves[0].X, Is.EqualTo(500));
            Assert.That(moves[0].Y, Is.EqualTo(600));
            Assert.That(moves[0].Absolute, Is.True);
        }
    }

    [Test]
    public void RelativeMoves_AreAccumulated()
    {
        _wrapper.MoveMouseRelative(10, 5);
        _wrapper.MoveMouseRelative(20, -3);
        _wrapper.MoveMouseRelative(-5, 8);

        _wrapper.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, 'a', null));
        Drain();

        var moves = _inner.Events.OfType<MoveEvent>().ToList();
        Assert.That(moves, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(moves[0].X, Is.EqualTo(25));
            Assert.That(moves[0].Y, Is.EqualTo(10));
            Assert.That(moves[0].Absolute, Is.False);
        }
    }

    [Test]
    public void AbsoluteOverridesRelative()
    {
        _wrapper.MoveMouseRelative(10, 5);
        _wrapper.MoveMouse(300, 400);

        _wrapper.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, 'a', null));
        Drain();

        var moves = _inner.Events.OfType<MoveEvent>().ToList();
        // relative was pending but absolute flushes it then replaces with absolute
        Assert.That(moves.Any(m => m.Absolute), Is.True, "should have an absolute move");
        var lastMove = moves.Last();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(lastMove.X, Is.EqualTo(300));
            Assert.That(lastMove.Y, Is.EqualTo(400));
            Assert.That(lastMove.Absolute, Is.True);
        }
    }

    [Test]
    public void NonMoveEvents_AreQueuedInOrder()
    {
        var key1 = new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, 'a', null);
        var key2 = new KeyEventMessage(KeyEventType.KeyUp, KeyModifiers.None, 'a', null);
        var key3 = new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, 'b', null);

        _wrapper.InjectKey(key1);
        _wrapper.InjectKey(key2);
        _wrapper.InjectKey(key3);
        Drain();

        var keys = _inner.Events.OfType<KeyEvent>().Select(e => e.Msg).ToList();
        Assert.That(keys, Is.EqualTo([key1, key2, key3]));
    }

    [Test]
    public void PendingMove_IsFlushedBeforeNonMoveEvent()
    {
        _wrapper.MoveMouse(100, 200);
        _wrapper.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, 'a', null));
        Drain();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_inner.Events[0], Is.InstanceOf<MoveEvent>(), "move should come before key");
            Assert.That(_inner.Events[1], Is.InstanceOf<KeyEvent>());
        }
    }

    [Test]
    public void PendingRelativeMove_IsFlushedBeforeMouseButton()
    {
        _wrapper.MoveMouseRelative(5, 3);
        _wrapper.InjectMouseButton(new MouseButtonMessage(MouseButton.Left, true));
        Drain();

        Assert.That(_inner.Events[0], Is.InstanceOf<MoveEvent>(), "relative move should come before button");
        var move = (MoveEvent)_inner.Events[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(move.X, Is.EqualTo(5));
            Assert.That(move.Y, Is.EqualTo(3));
            Assert.That(move.Absolute, Is.False);
        }
        Assert.That(_inner.Events[1], Is.InstanceOf<ButtonEvent>());
    }

    [Test]
    public void Dispose_DeliversPendingMove()
    {
        _wrapper.MoveMouse(77, 88);
        _wrapper.Dispose();
        // after Dispose, drain thread has stopped; inner should have received the move
        Assert.That(_inner.Events.OfType<MoveEvent>().Any(), Is.True, "dispose should flush pending move");
    }

    // wait for all enqueued events to be delivered by the drain thread
    private static void Drain() => Thread.Sleep(50);

    // -- recording infrastructure --

    private sealed class RecordingOutput : IPlatformOutput
    {
        public readonly List<object> Events = [];

        public void MoveMouse(int x, int y) { lock (Events) Events.Add(new MoveEvent(x, y, Absolute: true)); }
        public void MoveMouseRelative(int dx, int dy) { lock (Events) Events.Add(new MoveEvent(dx, dy, Absolute: false)); }
        public void InjectKey(KeyEventMessage msg) { lock (Events) Events.Add(new KeyEvent(msg)); }
        public void InjectMouseButton(MouseButtonMessage msg) { lock (Events) Events.Add(new ButtonEvent()); }
        public void InjectMouseScroll(MouseScrollMessage msg) { lock (Events) Events.Add(new ScrollEvent()); }
        public void Dispose() { }
    }

    private record MoveEvent(int X, int Y, bool Absolute);
    private record KeyEvent(KeyEventMessage Msg);
    private record ButtonEvent;
    private record ScrollEvent;
}
