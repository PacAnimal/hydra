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

    // creates a CGKeyDown event with the OS autorepeat field set (as the OS marks genuine repeats)
    private static nint AutoRepeatKeyDownEvent(ulong vk)
    {
        var ev = NativeMethods.CGEventCreateKeyboardEvent(nint.Zero, (ushort)vk, true);
        NativeMethods.CGEventSetIntegerValueField(ev, NativeMethods.KCGKeyboardEventAutorepeat, 1);
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

    // -- master-driven auto-repeat --

    [Test]
    public void Character_AutoRepeat_EmitsRepeatWithSameChar()
    {
        var r = new MacKeyResolver();

        // keycode 0 (kVK_ANSI_A) produces a layout-dependent character; assert against whatever it resolves to.
        char? first;
        var ev1 = KeyDownEvent(0);
        try
        {
            var e1 = r.Resolve(NativeMethods.KCGEventKeyDown, ev1);
            Assume.That(e1?.FirstOrDefault()?.Character, Is.Not.Null, "key 0 must produce a character on this layout");
            first = e1!.First()!.Character;
        }
        finally { NativeMethods.CFRelease(ev1); }

        // a second key-down for the held key is an OS auto-repeat: emitted (not suppressed) and marked IsRepeat
        var ev2 = KeyDownEvent(0);
        try
        {
            var e2 = r.Resolve(NativeMethods.KCGEventKeyDown, ev2);
            Assert.That(e2, Is.Not.Null);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(e2!.Single()!.IsRepeat, Is.True, "auto-repeat must be forwarded as a repeat");
                Assert.That(e2.Single()!.Character, Is.EqualTo(first), "repeat re-resolves to the same character");
            }
        }
        finally { NativeMethods.CFRelease(ev2); }
    }

    [Test]
    public void Character_OsAutorepeatFlag_TreatedAsRepeat()
    {
        var r = new MacKeyResolver();

        // an event the OS marks with kCGKeyboardEventAutorepeat is a repeat even with no tracked prior press
        var ev = AutoRepeatKeyDownEvent(0);
        try
        {
            var e = r.Resolve(NativeMethods.KCGEventKeyDown, ev);
            Assume.That(e?.FirstOrDefault()?.Character, Is.Not.Null, "key 0 must produce a character on this layout");
            Assert.That(e!.Single()!.IsRepeat, Is.True, "the OS autorepeat flag alone marks the event a repeat");
        }
        finally { NativeMethods.CFRelease(ev); }
    }

    [Test]
    public void OptionSpace_PreservesPhysicalSpaceAndOptionModifier()
    {
        var resolver = new MacKeyResolver();
        var ev = KeyDownEvent(MacVirtualKey.Space, NativeMethods.KCGEventFlagMaskAlternate);
        try
        {
            var events = resolver.Resolve(NativeMethods.KCGEventKeyDown, ev);

            Assert.That(events, Is.Not.Null);
            var keyEvent = events!.Single()!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(keyEvent.Character, Is.EqualTo(' '));
                Assert.That(keyEvent.Modifiers.HasFlag(KeyModifiers.Alt), Is.True);
                Assert.That(keyEvent.Modifiers.HasFlag(KeyModifiers.AltGr), Is.False);
            }
        }
        finally { NativeMethods.CFRelease(ev); }
    }
}
