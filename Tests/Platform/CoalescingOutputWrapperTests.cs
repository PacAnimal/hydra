using System.Diagnostics;
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
        // manual drain mode: the test drives delivery via Drain(), so there is no background thread
        // racing a sleep — coalescing is observed deterministically.
        _wrapper = new CoalescingOutputWrapper(_inner, runDrainThread: false);
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
    public void AbsoluteMoveBurst_QueuesSingleDrainAction()
    {
        for (var i = 0; i < 10_000; i++)
            _wrapper.MoveMouse(i, i);

        Assert.That(_wrapper.PendingActionCount, Is.EqualTo(1));
        Assert.That(_wrapper.MaxPendingActionCount, Is.EqualTo(1));
        Drain();
        Assert.That(_inner.Events.OfType<MoveEvent>().Single().X, Is.EqualTo(9_999));
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
    public void RelativeThenAbsolute_PreservesModeOrder()
    {
        _wrapper.MoveMouseRelative(10, 5);
        _wrapper.MoveMouse(300, 400);

        _wrapper.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, 'a', null));
        Drain();

        var moves = _inner.Events.OfType<MoveEvent>().ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(moves, Has.Count.EqualTo(2));
            Assert.That(moves[0], Is.EqualTo(new MoveEvent(10, 5, Absolute: false)));
            Assert.That(moves[1], Is.EqualTo(new MoveEvent(300, 400, Absolute: true)));
        }
    }

    [Test]
    public void AbsoluteThenRelative_PreservesModeOrder()
    {
        _wrapper.MoveMouse(100, 200);
        _wrapper.MoveMouseRelative(5, 6);
        Drain();

        var moves = _inner.Events.OfType<MoveEvent>().ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(moves, Has.Count.EqualTo(2));
            Assert.That(moves[0], Is.EqualTo(new MoveEvent(100, 200, Absolute: true)));
            Assert.That(moves[1], Is.EqualTo(new MoveEvent(5, 6, Absolute: false)));
        }
    }

    [Test]
    public void MovementAfterKey_IsNotFoldedAheadOfKey()
    {
        _wrapper.MoveMouse(10, 20);
        _wrapper.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, 'a', null));
        _wrapper.MoveMouse(30, 40);
        Drain();

        Assert.That(_inner.Events[0], Is.EqualTo(new MoveEvent(10, 20, Absolute: true)));
        Assert.That(_inner.Events[1], Is.InstanceOf<KeyEvent>());
        Assert.That(_inner.Events[2], Is.EqualTo(new MoveEvent(30, 40, Absolute: true)));
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
    public void PlatformActionFailure_IsRecorded_AndLaterActionsStillDrain()
    {
        var inner = new FaultingOutput();
        using var wrapper = new CoalescingOutputWrapper(inner, runDrainThread: false);
        var first = new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, 'a', null);
        var second = new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, 'b', null);

        wrapper.InjectKey(first);
        wrapper.InjectKey(second);
        wrapper.DrainPending();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(wrapper.FaultCount, Is.EqualTo(1));
            Assert.That(inner.Delivered, Is.EqualTo([second]));
        }
    }

    [Test]
    public void Dispose_ReturnsAfterBound_WhenPlatformActionIsBlocked()
    {
        var inner = new BlockingOutput();
        var wrapper = new CoalescingOutputWrapper(inner, runDrainThread: true,
            shutdownTimeout: TimeSpan.FromMilliseconds(50));
        wrapper.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, 'a', null));
        Assert.That(inner.Entered.Wait(TimeSpan.FromSeconds(1)), Is.True);

        var started = Stopwatch.GetTimestamp();
        wrapper.Dispose();
        var elapsed = Stopwatch.GetElapsedTime(started);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(elapsed, Is.LessThan(TimeSpan.FromSeconds(1)));
            Assert.That(inner.Disposed.IsSet, Is.False,
                "native output must remain owned by the worker while an action is still executing");
        }

        inner.Release.Set();
        Assert.That(inner.Disposed.Wait(TimeSpan.FromSeconds(1)), Is.True);
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

    [Test]
    public void CallsAfterDispose_AreIgnoredWithoutQueueExceptions()
    {
        _wrapper.Dispose();

        Assert.DoesNotThrow(() =>
        {
            _wrapper.MoveMouse(1, 2);
            _wrapper.MoveMouseRelative(3, 4);
            _wrapper.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, 'a', null));
        });
    }

    // deliver all enqueued events synchronously — deterministic, no sleep
    private void Drain() => _wrapper.DrainPending();

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

    private sealed class FaultingOutput : IPlatformOutput
    {
        private bool _throwNext = true;
        public List<KeyEventMessage> Delivered { get; } = [];

        public void MoveMouse(int x, int y) { }
        public void MoveMouseRelative(int dx, int dy) { }
        public void InjectKey(KeyEventMessage msg)
        {
            if (_throwNext)
            {
                _throwNext = false;
                throw new InvalidOperationException("injected output failure");
            }
            Delivered.Add(msg);
        }
        public void InjectMouseButton(MouseButtonMessage msg) { }
        public void InjectMouseScroll(MouseScrollMessage msg) { }
        public void Dispose() { }
    }

    private sealed class BlockingOutput : IPlatformOutput
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public ManualResetEventSlim Disposed { get; } = new();

        public void MoveMouse(int x, int y) { }
        public void MoveMouseRelative(int dx, int dy) { }
        public void InjectKey(KeyEventMessage msg)
        {
            Entered.Set();
            Release.Wait();
        }
        public void InjectMouseButton(MouseButtonMessage msg) { }
        public void InjectMouseScroll(MouseScrollMessage msg) { }
        public void Dispose() => Disposed.Set();
    }

    private record MoveEvent(int X, int Y, bool Absolute);
    private record KeyEvent(KeyEventMessage Msg);
    private record ButtonEvent;
    private record ScrollEvent;
}
