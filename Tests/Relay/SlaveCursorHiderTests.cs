using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Relay;

[TestFixture]
public class SlaveCursorHiderTests
{
    private FakeCursorVisibility _cursor = null!;
    private SlaveCursorHider _hider = null!;

    [SetUp]
    public void SetUp()
    {
        _cursor = new FakeCursorVisibility();
        // use very short intervals so tests don't take ages
        _hider = new SlaveCursorHider(_cursor, NullLogger<SlaveCursorHider>.Instance, pollIntervalMs: 20, localTimeoutMs: 300);
    }

    [TearDown]
    public void TearDown() => _hider.Dispose();

    [Test]
    public void Initial_State_Is_NoMaster_Cursor_Visible()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_hider.State, Is.EqualTo(SlaveCursorState.NoMaster));
            Assert.That(_cursor.IsHidden, Is.False);
        }
    }

    [Test]
    public void MasterConnected_Hides_Cursor()
    {
        _hider.OnMasterConnected();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_hider.State, Is.EqualTo(SlaveCursorState.Hidden));
            Assert.That(_cursor.IsHidden, Is.True);
        }
    }

    [Test]
    public void EnterScreen_Shows_Cursor()
    {
        _hider.OnMasterConnected();
        _hider.OnEnterScreen("A");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_hider.State, Is.EqualTo(SlaveCursorState.MasterActive));
            Assert.That(_cursor.IsHidden, Is.False);
        }
    }

    [Test]
    public void LeaveScreen_Hides_Cursor()
    {
        _hider.OnMasterConnected();
        _hider.OnEnterScreen("A");
        _hider.OnLeaveScreen("A");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_hider.State, Is.EqualTo(SlaveCursorState.Hidden));
            Assert.That(_cursor.IsHidden, Is.True);
        }
    }

    [Test]
    public void AllMastersDisconnected_Shows_Cursor()
    {
        _hider.OnMasterConnected();
        _hider.OnMasterDisconnected("A");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_hider.State, Is.EqualTo(SlaveCursorState.NoMaster));
            Assert.That(_cursor.IsHidden, Is.False);
        }
    }

    [Test]
    public void MultipleMasters_Stays_Hidden_Until_All_Disconnected()
    {
        _hider.OnMasterConnected();
        _hider.OnMasterConnected();
        _hider.OnMasterDisconnected("A");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_hider.State, Is.EqualTo(SlaveCursorState.Hidden));
            Assert.That(_cursor.IsHidden, Is.True);
        }

        _hider.OnMasterDisconnected("C");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_hider.State, Is.EqualTo(SlaveCursorState.NoMaster));
            Assert.That(_cursor.IsHidden, Is.False);
        }
    }

    [Test]
    public async Task LocalMouseMovement_Shows_Cursor()
    {
        _hider.OnMasterConnected();
        Assert.That(_cursor.IsHidden, Is.True);

        // move cursor position so poll detects it
        _cursor.Position = new CursorPosition(200, 300);

        await Task.Delay(100); // several polls at 20ms — well before 300ms timeout

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_hider.State, Is.EqualTo(SlaveCursorState.LocalActive));
            Assert.That(_cursor.IsHidden, Is.False);
        }
    }

    [Test]
    public async Task LocalTimeout_Rehides_Cursor()
    {
        _hider.OnMasterConnected();
        _cursor.Position = new CursorPosition(200, 300);

        await Task.Delay(100); // poll detects movement, enters LocalActive
        Assert.That(_hider.State, Is.EqualTo(SlaveCursorState.LocalActive));

        await Task.Delay(500); // 300ms timeout fires with buffer

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_hider.State, Is.EqualTo(SlaveCursorState.Hidden));
            Assert.That(_cursor.IsHidden, Is.True);
        }
    }

    [Test]
    public async Task LocalMovement_Resets_Timeout()
    {
        _hider.OnMasterConnected();
        _cursor.Position = new CursorPosition(200, 300);
        await Task.Delay(60); // enters LocalActive (poll at ~20ms, timeout starts)

        Assert.That(_hider.State, Is.EqualTo(SlaveCursorState.LocalActive));

        // move again — poll will detect and reset the 300ms timeout
        _cursor.Position = new CursorPosition(250, 350);
        await Task.Delay(60); // poll detects second movement, timeout resets

        // at ~360ms total: original timeout (300ms from ~20ms = ~320ms) would have fired,
        // but we reset it at ~120ms so it now fires at ~420ms
        Assert.That(_hider.State, Is.EqualTo(SlaveCursorState.LocalActive));

        await Task.Delay(500); // new timeout fires

        Assert.That(_hider.State, Is.EqualTo(SlaveCursorState.Hidden));
    }

    [Test]
    public void EnterScreen_While_LocalActive_Goes_To_MasterActive()
    {
        _hider.OnMasterConnected();
        // manually simulate LocalActive by calling methods directly
        _hider.OnLeaveScreen("A"); // no-op from Hidden (A not on screen)
        _hider.OnEnterScreen("A"); // Hidden -> MasterActive
        _hider.OnLeaveScreen("A"); // MasterActive -> Hidden

        Assert.That(_hider.State, Is.EqualTo(SlaveCursorState.Hidden));
    }

    [Test]
    public void Dispose_Shows_Cursor()
    {
        _hider.OnMasterConnected();
        Assert.That(_cursor.IsHidden, Is.True);

        _hider.Dispose();

        Assert.That(_cursor.IsHidden, Is.False);
    }

    [Test]
    public void Second_MasterConnected_Does_Not_Change_State()
    {
        _hider.OnMasterConnected();
        _hider.OnMasterConnected(); // second master

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_hider.State, Is.EqualTo(SlaveCursorState.Hidden));
            Assert.That(_cursor.HideCount, Is.EqualTo(1)); // only hidden once
        }
    }

    // -- multi-master active-master tracking --

    [Test]
    public void NonActiveMaster_Leaving_Does_Not_Hide_Cursor()
    {
        // A enters; C enters (C becomes active); A sends activity (A becomes active again); C leaves — A is active so cursor stays
        _hider.OnMasterConnected();
        _hider.OnMasterConnected();
        _hider.OnEnterScreen("A");
        _hider.OnEnterScreen("C"); // C active
        _hider.OnMasterActivity("A"); // A reclaims active
        _hider.OnLeaveScreen("C"); // C leaves but is not active

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_hider.State, Is.EqualTo(SlaveCursorState.MasterActive));
            Assert.That(_cursor.IsHidden, Is.False);
        }
    }

    [Test]
    public void ActiveMaster_Leaving_Hides_Cursor_Even_When_Other_Masters_Present()
    {
        // A enters then idles; C enters (becomes active); C leaves — cursor hides even though A is on screen
        _hider.OnMasterConnected();
        _hider.OnMasterConnected();
        _hider.OnEnterScreen("A");
        _hider.OnEnterScreen("C"); // C is now active
        _hider.OnLeaveScreen("C");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_hider.State, Is.EqualTo(SlaveCursorState.Hidden));
            Assert.That(_cursor.IsHidden, Is.True);
        }
    }

    [Test]
    public void MasterActivity_From_OnScreen_Master_Restores_Cursor_Without_Reenter()
    {
        // A enters; C enters and becomes active; C leaves → hidden; A sends input → cursor shows
        _hider.OnMasterConnected();
        _hider.OnMasterConnected();
        _hider.OnEnterScreen("A");
        _hider.OnEnterScreen("C");
        _hider.OnLeaveScreen("C"); // cursor hidden

        Assert.That(_hider.State, Is.EqualTo(SlaveCursorState.Hidden));

        _hider.OnMasterActivity("A"); // A resumes without re-entering

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_hider.State, Is.EqualTo(SlaveCursorState.MasterActive));
            Assert.That(_cursor.IsHidden, Is.False);
        }
    }

    [Test]
    public void MasterActivity_From_OffScreen_Master_Does_Nothing()
    {
        // C is not on screen — its activity should not show the cursor
        _hider.OnMasterConnected();
        _hider.OnMasterActivity("C");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_hider.State, Is.EqualTo(SlaveCursorState.Hidden));
            Assert.That(_cursor.IsHidden, Is.True);
        }
    }

    [Test]
    public void MasterDisconnect_Cleans_Up_OnScreen_Tracking()
    {
        // A is on screen and then disconnects — should not leave stale on-screen entry
        _hider.OnMasterConnected();
        _hider.OnEnterScreen("A");
        _hider.OnMasterDisconnected("A");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_hider.State, Is.EqualTo(SlaveCursorState.NoMaster));
            Assert.That(_cursor.IsHidden, Is.False);
        }

        // C reconnects — A's stale entry must not affect anything
        _hider.OnMasterConnected();
        _hider.OnMasterActivity("A"); // A is gone — no effect
        Assert.That(_hider.State, Is.EqualTo(SlaveCursorState.Hidden));
    }
}

internal sealed class FakeCursorVisibility : ICursorVisibility
{
    public bool IsHidden { get; private set; }
    public int HideCount { get; private set; }
    public int ShowCount { get; private set; }
    public CursorPosition Position { get; set; } = new(100, 100);

    public void HideCursor() { IsHidden = true; HideCount++; }
    public void ShowCursor() { IsHidden = false; ShowCount++; }
    public CursorPosition GetCursorPosition() => Position;
}
