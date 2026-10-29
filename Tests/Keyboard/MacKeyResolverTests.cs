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

    // -- the keypad's decimal key, for modifierNumLock --

    [TestCase(MacVirtualKey.KeypadDecimal, true)]
    [TestCase(0x2FUL /* kVK_ANSI_Period */, false)]
    public void TheKeypadsDecimalKeys_AreStampedAsIt_DownAndUp(ulong vk, bool keypadDecimal)
    {
        var resolver = new MacKeyResolver();
        var down = KeyDownEvent(vk);
        try
        {
            KeyEvent[] events =
            [
                .. (resolver.Resolve(NativeMethods.KCGEventKeyDown, down) ?? []).OfType<KeyEvent>(),
                .. (resolver.Resolve(NativeMethods.KCGEventKeyUp, down) ?? []).OfType<KeyEvent>(),
            ];
            Assume.That(events, Is.Not.Empty, "the running layout gives this key something");
            Assert.That(events.Select(e => e.IsKeypadDecimal), Is.All.EqualTo(keypadDecimal));
        }
        finally { NativeMethods.CFRelease(down); }
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

    // -- the keypad: macOS flags each of its keys as from the numeric pad, which travels as NumLock (as in Synergy) --

    [Test]
    public void KeypadDigit_CarriesNumLock()
    {
        var r = new MacKeyResolver();
        var ev = KeyDownEvent(MacVirtualKey.Keypad4, NativeMethods.KCGEventFlagMaskNumericPad);
        try
        {
            var down = r.Resolve(NativeMethods.KCGEventKeyDown, ev)!.Single()!;
            Assert.That((down.Key, down.Modifiers), Is.EqualTo((SpecialKey.KP_4, KeyModifiers.NumLock)), "a slave must type the digit, not navigate");
        }
        finally { NativeMethods.CFRelease(ev); }
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

    [Test]
    public void OptionSpace_ReleasesTheSpaceItPressed()
    {
        // the Option+Space shortcut returns early from ResolveCharacter, so this pins that the key is
        // still tracked as held — without the key-up replay the slave would hold Space down forever.
        var resolver = new MacKeyResolver();
        var down = KeyDownEvent(MacVirtualKey.Space, NativeMethods.KCGEventFlagMaskAlternate);
        var up = KeyDownEvent(MacVirtualKey.Space, NativeMethods.KCGEventFlagMaskAlternate);
        try
        {
            resolver.Resolve(NativeMethods.KCGEventKeyDown, down);
            var events = resolver.Resolve(NativeMethods.KCGEventKeyUp, up);

            Assert.That(events, Is.Not.Null, "Option+Space must emit a key-up");
            var keyEvent = events!.Single()!;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(keyEvent.Type, Is.EqualTo(KeyEventType.KeyUp));
                Assert.That(keyEvent.Character, Is.EqualTo(' '));
            }
        }
        finally
        {
            NativeMethods.CFRelease(down);
            NativeMethods.CFRelease(up);
        }
    }

    // -- PhysicalKey: the keycode each event is tracked under, pairing a repeat or up with its down --

    [TestCase(MacVirtualKey.Return, TestName = "SpecialKey_DownRepeatAndUp_CarryItsKeycode")]
    [TestCase(0x01ul, TestName = "CharacterKey_DownRepeatAndUp_CarryItsKeycode")] // kVK_ANSI_S
    public void Key_DownRepeatAndUp_CarryItsKeycode(ulong vk)
    {
        var resolver = new MacKeyResolver();
        var down = KeyDownEvent(vk);
        var repeat = AutoRepeatKeyDownEvent(vk);
        try
        {
            KeyEvent?[] events =
            [
                .. resolver.Resolve(NativeMethods.KCGEventKeyDown, down)!,
                .. resolver.Resolve(NativeMethods.KCGEventKeyDown, repeat)!,
                .. resolver.Resolve(NativeMethods.KCGEventKeyUp, down)!,
            ];
            Assert.That(events.Select(e => e!.PhysicalKey), Is.EqualTo([(int)vk, (int)vk, (int)vk]));
        }
        finally
        {
            NativeMethods.CFRelease(down);
            NativeMethods.CFRelease(repeat);
        }
    }

    [Test]
    public void ModifierChange_CarriesItsKeycode()
    {
        var resolver = new MacKeyResolver();
        var press = ModifierEvent(MacVirtualKey.Shift, NativeMethods.KCGEventFlagMaskShift);
        var release = ModifierEvent(MacVirtualKey.Shift, 0);
        try
        {
            KeyEvent?[] events =
            [
                .. resolver.Resolve(NativeMethods.KCGEventFlagsChanged, press)!,
                .. resolver.Resolve(NativeMethods.KCGEventFlagsChanged, release)!,
            ];
            Assert.That(events.Select(e => (e!.Type, e.PhysicalKey)),
                Is.EqualTo([(KeyEventType.KeyDown, (int)MacVirtualKey.Shift), (KeyEventType.KeyUp, (int)MacVirtualKey.Shift)]));
        }
        finally
        {
            NativeMethods.CFRelease(press);
            NativeMethods.CFRelease(release);
        }
    }

    // -- modifier press/release read from the event's flags --

    private const ulong ShiftL = NativeMethods.KCGEventFlagMaskShift | NativeMethods.NxDeviceLShiftKeyMask;
    private const ulong ShiftR = NativeMethods.KCGEventFlagMaskShift | NativeMethods.NxDeviceRShiftKeyMask;
    private const ulong BothShifts = ShiftL | NativeMethods.NxDeviceRShiftKeyMask;

    // resolves one FlagsChanged for vk with the given flags and returns its type
    private static KeyEventType ModifierChange(MacKeyResolver resolver, ulong vk, ulong flags)
    {
        var ev = ModifierEvent(vk, flags);
        try { return resolver.Resolve(NativeMethods.KCGEventFlagsChanged, ev)!.Single()!.Type; }
        finally { NativeMethods.CFRelease(ev); }
    }

    [Test]
    public void ShiftL_DeviceBitSet_IsPress_GenericClear_IsRelease()
    {
        var resolver = new MacKeyResolver();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ModifierChange(resolver, MacVirtualKey.Shift, ShiftL), Is.EqualTo(KeyEventType.KeyDown));
            Assert.That(ModifierChange(resolver, MacVirtualKey.Shift, 0), Is.EqualTo(KeyEventType.KeyUp));
        }
    }

    [Test]
    public void ShiftL_HeldAcrossReset_ReleaseIsKeyUp()
    {
        var resolver = new MacKeyResolver();
        ModifierChange(resolver, MacVirtualKey.Shift, ShiftL);
        resolver.Reset();
        Assert.That(ModifierChange(resolver, MacVirtualKey.Shift, 0), Is.EqualTo(KeyEventType.KeyUp));
    }

    [Test]
    public void ShiftL_ReleasedWhileShiftRHeld_IsKeyUp()
    {
        var resolver = new MacKeyResolver();
        ModifierChange(resolver, MacVirtualKey.RightShift, ShiftR);
        ModifierChange(resolver, MacVirtualKey.Shift, BothShifts);
        Assert.That(ModifierChange(resolver, MacVirtualKey.Shift, ShiftR), Is.EqualTo(KeyEventType.KeyUp));
    }

    [Test]
    public void ShiftR_PressedWhileShiftLHeld_IsKeyDown()
    {
        var resolver = new MacKeyResolver();
        ModifierChange(resolver, MacVirtualKey.Shift, ShiftL);
        Assert.That(ModifierChange(resolver, MacVirtualKey.RightShift, BothShifts), Is.EqualTo(KeyEventType.KeyDown));
    }

    [Test]
    public void BothShiftsHeldAcrossReset_ShiftLReleasedWhileShiftRHeld_IsKeyUp()
    {
        var resolver = new MacKeyResolver();
        ModifierChange(resolver, MacVirtualKey.Shift, ShiftL);
        ModifierChange(resolver, MacVirtualKey.RightShift, BothShifts);
        resolver.Reset();
        Assert.That(ModifierChange(resolver, MacVirtualKey.Shift, ShiftR), Is.EqualTo(KeyEventType.KeyUp));
    }

    [Test]
    public void Shift_GenericWithoutDeviceBits_TogglesPressThenRelease()
    {
        // a synthetic event (e.g. a remapper) carries no device bits: fall back to tracking
        var resolver = new MacKeyResolver();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ModifierChange(resolver, MacVirtualKey.Shift, NativeMethods.KCGEventFlagMaskShift), Is.EqualTo(KeyEventType.KeyDown));
            Assert.That(ModifierChange(resolver, MacVirtualKey.Shift, NativeMethods.KCGEventFlagMaskShift), Is.EqualTo(KeyEventType.KeyUp));
        }
    }

    [Test]
    public void CapsLock_AlternatesPressAndRelease_WhateverItsFlags()
    {
        // the flag tracks the lock, not the key, so it cannot tell press from release: the toggle decides
        var resolver = new MacKeyResolver();
        var types = new[] { NativeMethods.KCGEventFlagMaskAlphaShift, NativeMethods.KCGEventFlagMaskAlphaShift, 0ul, 0ul }
            .Select(f => ModifierChange(resolver, MacVirtualKey.CapsLock, f));
        Assert.That(types, Is.EqualTo([KeyEventType.KeyDown, KeyEventType.KeyUp, KeyEventType.KeyDown, KeyEventType.KeyUp]));
    }
}
