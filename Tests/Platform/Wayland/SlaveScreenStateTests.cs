using System.Runtime.CompilerServices;
using Hydra.Platform;
using Hydra.Platform.Linux.Wayland;

namespace Tests.Platform.Wayland;

// a Wayland slave's readiness and screens (SlaveScreenState, extracted from WaylandSession's SlaveLive, which needs a
// portal bus to build)
[TestFixture]
public class SlaveScreenStateTests
{
    private static readonly DetectedScreen Screen = new(0, 0, 1920, 1080, "region 1", null, null);

    private readonly Lock _gate = new();
    private bool _lost;
    private SlaveScreenState _state = null!;

    [SetUp]
    public void Create()
    {
        _lost = false;
        _state = new SlaveScreenState(_gate, () => _lost);
    }

    [Test]
    public void Regions_BeforeReady_AreKnown()
    {
        _state.Update(false, [Screen]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_state.Known().IsCompletedSuccessfully, Is.True);
            Assert.That(_state.Ready().IsCompleted, Is.False);
            Assert.That(_state.Regions(), Is.EqualTo((DetectedScreen[])[Screen]));
        }
    }

    [Test]
    public void NoRegionsAndNotReady_ResetsKnown()
    {
        _state.Update(true, null);
        var known = _state.Known();
        _state.Update(false, null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(known.IsCompletedSuccessfully, Is.True, "a waiter that already saw it known keeps its result");
            Assert.That(_state.Known().IsCompleted, Is.False);
            Assert.That(_state.Ready().IsCompleted, Is.False);
        }
    }

    [Test]
    public void EmptyRegionsAndNotReady_ResetsKnown()
    {
        _state.Update(false, [Screen]);
        _state.Update(false, []);
        Assert.That(_state.Known().IsCompleted, Is.False);
    }

    [Test]
    public void RemovedAbsoluteDevice_KeepsKnownWithTheLastRegions()
    {
        _state.Update(true, [Screen]);
        var changed = _state.Update(false, null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(changed, Is.False);
            Assert.That(_state.Known().IsCompletedSuccessfully, Is.True);
            Assert.That(_state.Ready().IsCompleted, Is.False);
            Assert.That(_state.Regions(), Is.EqualTo((DetectedScreen[])[Screen]));
        }
    }

    [Test]
    public void OtherRegions_AreAChange()
    {
        _state.Update(false, [Screen]);
        Assert.That(_state.Update(false, [Screen with { Width = 2560, Height = 1440 }]), Is.True);
    }

    [Test]
    public void Lost_FailsPendingKnownAndReadyWaiters()
    {
        var known = _state.Known();
        var ready = _state.Ready();
        _lost = true;
        _state.Lost();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(known.IsFaulted, Is.True);
            Assert.That(known.Exception?.InnerException, Is.InstanceOf<PortalException>());
            Assert.That(ready.IsFaulted, Is.True);
            Assert.That(ready.Exception?.InnerException, Is.InstanceOf<PortalException>());
        }
    }

    [Test]
    public void UpdateAfterLost_ChangesNothing()
    {
        _lost = true;
        _state.Lost();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_state.Update(true, [Screen]), Is.False);
            Assert.That(_state.Known().IsFaulted, Is.True);
            Assert.That(_state.Regions(), Is.Empty);
        }
    }

    // the start thread and the EIS dispatch thread both re-read the devices; the read happens under the gate, so an
    // update another thread made meanwhile is never overwritten by an older read
    [Test]
    public void Read_IsUnderTheGate()
    {
        var held = false;
        _state.Update(() =>
        {
            held = _gate.IsHeldByCurrentThread;
            return (true, [Screen]);
        });
        Assert.That(held, Is.True);
    }

    [Test]
    public void LaterUpdate_ReadsTheLatestState_AndLeavesReady()
    {
        var device = new StrongBox<bool>(false);
        _state.Update(() => (device.Value, null));
        // the device resumed before the second update reads it
        device.Value = true;
        _state.Update(() => (device.Value, null));
        Assert.That(_state.Ready().IsCompletedSuccessfully, Is.True);
    }

    [Test]
    public void StaleReader_WaitsForTheGate_AndReadsTheNewerState()
    {
        var device = new StrongBox<bool>(false);
        Task stale;
        lock (_gate)
        {
            // the dispatch thread's update holds the gate; the start thread's reader cannot read until it is done
            stale = Task.Run(() => _state.Update(() => (Volatile.Read(ref device.Value), null)));
            Assert.That(stale.Wait(100), Is.False, "the reader must wait for the gate");
            Volatile.Write(ref device.Value, true);
            _state.Update(() => (Volatile.Read(ref device.Value), null));
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stale.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(_state.Ready().IsCompletedSuccessfully, Is.True);
        }
    }

    [Test]
    public void NotReadyAfterReady_StillResetsReady()
    {
        _state.Update(() => (true, [Screen]));
        var ready = _state.Ready();
        _state.Update(() => (false, null));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ready.IsCompletedSuccessfully, Is.True, "a waiter that already saw it ready keeps its result");
            Assert.That(_state.Ready().IsCompleted, Is.False, "a paused device is not ready");
        }
    }
}
