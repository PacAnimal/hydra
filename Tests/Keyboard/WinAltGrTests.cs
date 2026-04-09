using Hydra.Keyboard;
using Hydra.Platform.Windows;

namespace Tests.Keyboard;

[TestFixture]
public class WinAltGrTests
{
    // helper to build a KBDLLHOOKSTRUCT for a given vk/flags/time
    private static KBDLLHOOKSTRUCT Hook(uint vk, uint flags = 0, uint time = 1) =>
        new() { vkCode = vk, scanCode = 0, flags = flags, time = time, dwExtraInfo = 0 };

    private static KeyEvent[]? Down(WinKeyResolver r, uint vk, uint flags = 0, uint time = 1) =>
        r.Resolve(NativeMethods.WM_KEYDOWN, Hook(vk, flags, time));

    private static KeyEvent[]? Up(WinKeyResolver r, uint vk, uint flags = 0, uint time = 1) =>
        r.Resolve(NativeMethods.WM_KEYUP, Hook(vk, flags, time));

    // AltGr on Windows: synthetic LCtrl (same timestamp as RMenu) should produce a single AltGr event
    [Test]
    public void AltGr_SameTimestamp_EmitsAltGrNotControl()
    {
        var r = new WinKeyResolver();

        // synthetic LCtrl (not injected, same time as RMenu)
        var lctrlEvents = Down(r, WinVirtualKey.LControl, time: 42);
        Assert.That(lctrlEvents, Is.Null, "LCtrl should be deferred, not emitted yet");

        // RMenu (AltGr) at same timestamp
        var altGrEvents = Down(r, WinVirtualKey.RMenu, time: 42);
        Assert.That(altGrEvents, Has.Length.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(altGrEvents![0].Key, Is.EqualTo(SpecialKey.AltGr));
            Assert.That(altGrEvents[0].Modifiers, Is.EqualTo(KeyModifiers.AltGr));
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(altGrEvents[0].Modifiers & KeyModifiers.Control, Is.EqualTo(KeyModifiers.None));
            Assert.That(altGrEvents[0].Modifiers & KeyModifiers.Alt, Is.EqualTo(KeyModifiers.None));
        }
    }

    // AltGr KeyUp: should emit AltGr KeyUp (not Alt_R), no LCtrl KeyUp
    [Test]
    public void AltGr_KeyUp_EmitsAltGrUp()
    {
        var r = new WinKeyResolver();
        Down(r, WinVirtualKey.LControl, time: 42);
        Down(r, WinVirtualKey.RMenu, time: 42);

        var upEvents = Up(r, WinVirtualKey.RMenu, time: 43);
        Assert.That(upEvents, Has.Length.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(upEvents![0].Key, Is.EqualTo(SpecialKey.AltGr));
            Assert.That(upEvents[0].Type, Is.EqualTo(KeyEventType.KeyUp));
        }
    }

    // synthetic LCtrl KeyUp should be suppressed (was never emitted as KeyDown)
    [Test]
    public void AltGr_LCtrl_KeyUp_IsSuppressed()
    {
        var r = new WinKeyResolver();
        Down(r, WinVirtualKey.LControl, time: 42);
        Down(r, WinVirtualKey.RMenu, time: 42);

        var lctrlUp = Up(r, WinVirtualKey.LControl);
        Assert.That(lctrlUp, Is.Null, "LCtrl KeyUp should be suppressed since KeyDown was consumed by AltGr");
    }

    // AltGr detected via LLKHF_INJECTED path: injected LCtrl followed by RMenu
    [Test]
    public void AltGr_InjectedLCtrl_EmitsAltGr()
    {
        var r = new WinKeyResolver();

        // LCtrl with LLKHF_INJECTED — should be suppressed by existing check AND trigger AltGr detection
        var lctrlEvents = Down(r, WinVirtualKey.LControl, flags: NativeMethods.LLKHF_INJECTED, time: 10);
        Assert.That(lctrlEvents, Is.Null);

        // RMenu at any timestamp — injected path doesn't rely on matching time
        var altGrEvents = Down(r, WinVirtualKey.RMenu, time: 10);
        Assert.That(altGrEvents, Has.Length.EqualTo(1));
        Assert.That(altGrEvents![0].Key, Is.EqualTo(SpecialKey.AltGr));
    }

    // real LCtrl (different timestamp from following RMenu) should be emitted as Control_L
    [Test]
    public void RealLCtrl_DifferentTimestamp_EmitsControlL()
    {
        var r = new WinKeyResolver();

        // LCtrl at time=1
        var lctrlEvents = Down(r, WinVirtualKey.LControl, time: 1);
        Assert.That(lctrlEvents, Is.Null, "LCtrl is deferred until next event");

        // RMenu at a different time — not AltGr; LCtrl is flushed first
        var rMenuEvents = Down(r, WinVirtualKey.RMenu, time: 999);
        Assert.That(rMenuEvents, Has.Length.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rMenuEvents![0].Key, Is.EqualTo(SpecialKey.Control_L)); // deferred LCtrl flushed first
            Assert.That(rMenuEvents[1].Key, Is.EqualTo(SpecialKey.Alt_R));      // RMenu as regular Alt_R
        }
    }

    // real LCtrl followed by a non-RMenu key: LCtrl should be emitted alongside the next key
    [Test]
    public void RealLCtrl_FollowedByOtherKey_FlushesLCtrl()
    {
        var r = new WinKeyResolver();

        Down(r, WinVirtualKey.LControl, time: 1);

        // Shift at a different time — LCtrl should be flushed, then Shift emitted
        var shiftEvents = Down(r, WinVirtualKey.LShift, time: 5);
        Assert.That(shiftEvents, Has.Length.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(shiftEvents![0].Key, Is.EqualTo(SpecialKey.Control_L));
            Assert.That(shiftEvents[1].Key, Is.EqualTo(SpecialKey.Shift_L));
        }
    }

    // AltGr modifier flag strips Control and Alt from modifier state
    [Test]
    public void AltGr_ModifierFlags_StripsControlAndAlt()
    {
        var r = new WinKeyResolver();
        Down(r, WinVirtualKey.LControl, time: 42);
        var events = Down(r, WinVirtualKey.RMenu, time: 42);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(events![0].Modifiers & KeyModifiers.Control, Is.EqualTo(KeyModifiers.None));
            Assert.That(events[0].Modifiers & KeyModifiers.Alt, Is.EqualTo(KeyModifiers.None));
            Assert.That(events[0].Modifiers & KeyModifiers.AltGr, Is.EqualTo(KeyModifiers.AltGr));
        }
    }
}
