using Hydra.Keyboard;
using Hydra.Platform.MacOs;

namespace Tests.Keyboard;

[TestFixture]
[Platform("MacOsX")]
public class MacKeyResolverTests
{
    // creates a CGKeyDown event for the given Mac virtual key code
    private static nint KeyDownEvent(ulong vk, ulong flags = 0)
    {
        var ev = NativeMethods.CGEventCreateKeyboardEvent(nint.Zero, (ushort)vk, true);
        if (flags != 0) NativeMethods.CGEventSetFlags(ev, flags);
        return ev;
    }

    // creates a CGFlagsChanged event for a modifier key press.
    // CGEventCreateKeyboardEvent creates a kCGEventKeyDown internally; Resolve() trusts the
    // eventType argument passed at call-site, not the event's internal type field.
    private static nint ModifierEvent(ulong vk, ulong flags)
    {
        var ev = NativeMethods.CGEventCreateKeyboardEvent(nint.Zero, (ushort)vk, true);
        NativeMethods.CGEventSetFlags(ev, flags);
        return ev;
    }

    // -- ScrollLock toggle --

    [Test]
    public void F14_KeyDown_TogglesScrollLockOn_EventCarriesScrollLockModifier()
    {
        var r = new MacKeyResolver();
        var ev = KeyDownEvent(MacVirtualKey.F14);
        try
        {
            var events = r.Resolve(NativeMethods.KCGEventKeyDown, ev);
            Assert.That(events, Is.Not.Null);
            Assert.That(events!.Any(e => e?.Key == SpecialKey.ScrollLock && e.Modifiers.HasFlag(KeyModifiers.ScrollLock)), Is.True,
                "first F14 press must toggle ScrollLock on and set the bit on the returned event");
        }
        finally { NativeMethods.CFRelease(ev); }
    }

    [Test]
    public void AfterScrollLockToggled_SubsequentModifierEvent_CarriesScrollLockBit()
    {
        var r = new MacKeyResolver();
        var f14 = KeyDownEvent(MacVirtualKey.F14);
        try { r.Resolve(NativeMethods.KCGEventKeyDown, f14); }
        finally { NativeMethods.CFRelease(f14); }

        // a modifier event (Shift press) must carry the ScrollLock bit even though
        // Shift has no CGEventFlag for ScrollLock — it comes from _scrollLockOn.
        var shiftEv = ModifierEvent(MacVirtualKey.Shift, NativeMethods.KCGEventFlagMaskShift);
        try
        {
            var events = r.Resolve(NativeMethods.KCGEventFlagsChanged, shiftEv);
            Assert.That(events, Is.Not.Null);
            Assert.That(events!.Any(e => e?.Modifiers.HasFlag(KeyModifiers.ScrollLock) == true), Is.True,
                "ScrollLock bit must travel on all events while toggle is active");
        }
        finally { NativeMethods.CFRelease(shiftEv); }
    }

    // -- Reset() preserves ScrollLock --

    [Test]
    public void Reset_PreservesScrollLockToggleState()
    {
        var r = new MacKeyResolver();
        var f14 = KeyDownEvent(MacVirtualKey.F14);
        try { r.Resolve(NativeMethods.KCGEventKeyDown, f14); }
        finally { NativeMethods.CFRelease(f14); }

        r.Reset();

        // after Reset(), _scrollLockOn must still be true — it is a persistent lock state,
        // not per-grab transient. verify by checking a modifier event still carries the bit.
        var shiftEv = ModifierEvent(MacVirtualKey.Shift, NativeMethods.KCGEventFlagMaskShift);
        try
        {
            var events = r.Resolve(NativeMethods.KCGEventFlagsChanged, shiftEv);
            Assert.That(events, Is.Not.Null);
            Assert.That(events!.Any(e => e?.Modifiers.HasFlag(KeyModifiers.ScrollLock) == true), Is.True,
                "scroll lock must survive Reset() — it is persistent lock state, not per-grab transient");
        }
        finally { NativeMethods.CFRelease(shiftEv); }
    }

    [Test]
    public void Reset_ClearsKeyDownId_F14NotSuppressedAsAutoRepeat()
    {
        var r = new MacKeyResolver();
        // toggle scroll lock on
        var f14 = KeyDownEvent(MacVirtualKey.F14);
        try { r.Resolve(NativeMethods.KCGEventKeyDown, f14); }
        finally { NativeMethods.CFRelease(f14); }

        r.Reset();

        // after Reset(), pressing F14 again must NOT be suppressed as auto-repeat
        // (_keyDownId was cleared) — it fires a second toggle turning ScrollLock off.
        var f14B = KeyDownEvent(MacVirtualKey.F14);
        try
        {
            var events = r.Resolve(NativeMethods.KCGEventKeyDown, f14B);
            Assert.That(events, Is.Not.Null, "F14 after Reset() must not be suppressed as auto-repeat");
            Assert.That(events!.Any(e => e?.Key == SpecialKey.ScrollLock && !e.Modifiers.HasFlag(KeyModifiers.ScrollLock)), Is.True,
                "second F14 press after Reset() toggles ScrollLock off");
        }
        finally { NativeMethods.CFRelease(f14B); }
    }
}
