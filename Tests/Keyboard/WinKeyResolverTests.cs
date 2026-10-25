using Hydra.Keyboard;
using Hydra.Platform.Windows;
using static Tests.Setup.WinHook;

namespace Tests.Keyboard;

[TestFixture]
public class WinKeyResolverTests
{
    [TestCase((uint)WinVirtualKey.Numpad0, SpecialKey.KP_0)]
    [TestCase((uint)WinVirtualKey.Numpad1, SpecialKey.KP_1)]
    [TestCase((uint)WinVirtualKey.Numpad5, SpecialKey.KP_5)]
    [TestCase((uint)WinVirtualKey.Numpad9, SpecialKey.KP_9)]
    public void NumpadDigit_WithNumLockOn_EmitsKpSpecialKey(uint vk, SpecialKey expected)
    {
        // VK_NUMPAD0-9 only appear in the LL hook when NumLock is active.
        // The resolver emits KP_0-KP_9 so the slave injects the physical numpad key.
        var r = new WinKeyResolver();
        var events = Down(r, vk);
        Assert.That(events, Has.Length.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(events![0].Key, Is.EqualTo(expected));
            Assert.That(events[0].Character, Is.Null);
            Assert.That(events[0].Type, Is.EqualTo(KeyEventType.KeyDown));
        }
    }

    [Test]
    public void NumpadDigit_KeyUp_ReplaysSameSpecialKey()
    {
        var r = new WinKeyResolver();
        Down(r, WinVirtualKey.Numpad7);
        var up = Up(r, WinVirtualKey.Numpad7);
        Assert.That(up, Has.Length.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(up![0].Key, Is.EqualTo(SpecialKey.KP_7));
            Assert.That(up[0].Type, Is.EqualTo(KeyEventType.KeyUp));
        }
    }

    [Test]
    public void NumpadDigit_AutoRepeat_EmitsRepeat()
    {
        var r = new WinKeyResolver();
        Down(r, WinVirtualKey.Numpad7);
        // master-driven repeats: a held key's OS auto-repeat is forwarded marked IsRepeat
        var repeat = Down(r, WinVirtualKey.Numpad7);
        Assert.That(repeat, Has.Length.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(repeat![0].Key, Is.EqualTo(SpecialKey.KP_7));
            Assert.That(repeat[0].IsRepeat, Is.True);
        }
    }

    // VK_RETURN is shared by both regular Enter (no extended flag) and KP_Enter (extended flag).
    // pressing one while the other is held must NOT be suppressed as auto-repeat.

    [Test]
    public void KpEnter_WhileReturnTracked_NotSuppressed()
    {
        var r = new WinKeyResolver();
        Down(r, WinVirtualKey.Return);  // regular Return tracked

        // KP_Enter (EXTENDED) arrives — must not be suppressed by the tracked Return
        var events = Down(r, WinVirtualKey.Return, NativeMethods.LLKHF_EXTENDED);
        Assert.That(events, Is.Not.Null);
        Assert.That(events!.Any(e => e.Key == SpecialKey.KP_Enter), Is.True,
            "KP_Enter must fire even while regular Return is held");
    }

    [Test]
    public void Return_WhileKpEnterTracked_NotSuppressed()
    {
        var r = new WinKeyResolver();
        Down(r, WinVirtualKey.Return, NativeMethods.LLKHF_EXTENDED);  // KP_Enter tracked

        // regular Return (no EXTENDED flag) arrives — must not be suppressed by the tracked KP_Enter
        var events = Down(r, WinVirtualKey.Return);
        Assert.That(events, Is.Not.Null);
        Assert.That(events!.Any(e => e.Key == SpecialKey.Return), Is.True,
            "regular Return must fire even while KP_Enter is held");
    }

    [Test]
    public void KpEnter_AutoRepeat_EmitsRepeat()
    {
        var r = new WinKeyResolver();
        Down(r, WinVirtualKey.Return, NativeMethods.LLKHF_EXTENDED);

        // master-driven repeats: a held KP_Enter's OS auto-repeat is forwarded marked IsRepeat
        var repeat = Down(r, WinVirtualKey.Return, NativeMethods.LLKHF_EXTENDED);
        Assert.That(repeat, Has.Length.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(repeat![0].Key, Is.EqualTo(SpecialKey.KP_Enter));
            Assert.That(repeat[0].IsRepeat, Is.True);
        }
    }

    // key-up must replay the correct key even when both Return and KP_Enter are held simultaneously.

    [Test]
    public void Return_KeyUp_WhileKpEnterAlsoHeld_ReplaysReturn()
    {
        var r = new WinKeyResolver();
        Down(r, WinVirtualKey.Return);
        Down(r, WinVirtualKey.Return, NativeMethods.LLKHF_EXTENDED);  // KP_Enter also down

        var up = Up(r, WinVirtualKey.Return);
        Assert.That(up, Is.Not.Null);
        Assert.That(up!.Any(e => e is { Key: SpecialKey.Return, Type: KeyEventType.KeyUp }), Is.True,
            "Return key-up must replay Return, not KP_Enter");
    }

    [Test]
    public void KpEnter_KeyUp_WhileReturnAlsoHeld_ReplaysKpEnter()
    {
        var r = new WinKeyResolver();
        Down(r, WinVirtualKey.Return);
        Down(r, WinVirtualKey.Return, NativeMethods.LLKHF_EXTENDED);  // KP_Enter also down

        var up = Up(r, WinVirtualKey.Return, NativeMethods.LLKHF_EXTENDED);
        Assert.That(up, Is.Not.Null);
        Assert.That(up!.Any(e => e is { Key: SpecialKey.KP_Enter, Type: KeyEventType.KeyUp }), Is.True,
            "KP_Enter key-up must replay KP_Enter, not Return");
    }

    [Test]
    public void Return_KeyUp_AfterKpEnterReleased_StillReplaysReturn()
    {
        var r = new WinKeyResolver();
        Down(r, WinVirtualKey.Return);
        Down(r, WinVirtualKey.Return, NativeMethods.LLKHF_EXTENDED);   // KP_Enter down
        Up(r, WinVirtualKey.Return, NativeMethods.LLKHF_EXTENDED);     // KP_Enter up

        var up = Up(r, WinVirtualKey.Return);
        Assert.That(up, Is.Not.Null);
        Assert.That(up!.Any(e => e is { Key: SpecialKey.Return, Type: KeyEventType.KeyUp }), Is.True,
            "Return key-up must still fire after KP_Enter was separately released");
    }

    // -- PhysicalKey: the key each event is tracked under, pairing a repeat or up with its down --

    [Test]
    public void SpecialKey_DownRepeatAndUp_CarryItsVk()
    {
        var r = new WinKeyResolver();
        KeyEvent[] events = [.. Down(r, WinVirtualKey.F5)!, .. Down(r, WinVirtualKey.F5)!, .. Up(r, WinVirtualKey.F5)!];
        Assert.That(events.Select(e => e.PhysicalKey), Is.EqualTo([WinVirtualKey.F5, WinVirtualKey.F5, WinVirtualKey.F5]));
    }

    [Test]
    public void KpEnterAndReturn_CarryDistinctKeys_EachTheSameDownToUp()
    {
        var r = new WinKeyResolver();
        var kpDown = Down(r, WinVirtualKey.Return, NativeMethods.LLKHF_EXTENDED)!.Single();
        var kpRepeat = Down(r, WinVirtualKey.Return, NativeMethods.LLKHF_EXTENDED)!.Single();
        var returnDown = Down(r, WinVirtualKey.Return)!.Single();
        var kpUp = Up(r, WinVirtualKey.Return, NativeMethods.LLKHF_EXTENDED)!.Single();
        var returnUp = Up(r, WinVirtualKey.Return)!.Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(returnDown.PhysicalKey, Is.EqualTo(WinVirtualKey.Return));
            Assert.That(returnUp.PhysicalKey, Is.EqualTo(WinVirtualKey.Return));
            Assert.That(kpDown.PhysicalKey, Is.Not.EqualTo(WinVirtualKey.Return).And.Not.Zero);
            Assert.That(kpRepeat.PhysicalKey, Is.EqualTo(kpDown.PhysicalKey));
            Assert.That(kpUp.PhysicalKey, Is.EqualTo(kpDown.PhysicalKey));
        }
    }

    [Test]
    public void DeferredLeftCtrl_FlushedAheadOfADown_CarriesLeftCtrl()
    {
        var r = new WinKeyResolver();
        Assert.That(Down(r, WinVirtualKey.LControl), Is.Null, "pre-condition: deferred");

        var events = Down(r, WinVirtualKey.F5)!;

        Assert.That(events.Select(e => (e.Key, e.PhysicalKey)),
            Is.EqualTo([(SpecialKey.Control_L, WinVirtualKey.LControl), (SpecialKey.F5, WinVirtualKey.F5)]));
    }

    [Test]
    public void DeferredLeftCtrl_FlushedAheadOfAnUp_CarriesLeftCtrl()
    {
        var r = new WinKeyResolver();
        Down(r, WinVirtualKey.F5);
        Assert.That(Down(r, WinVirtualKey.LControl), Is.Null, "pre-condition: deferred");

        var events = Up(r, WinVirtualKey.F5)!;

        Assert.That(events.Select(e => (e.Key, e.Type, e.PhysicalKey)), Is.EqualTo([
            (SpecialKey.Control_L, KeyEventType.KeyDown, WinVirtualKey.LControl), (SpecialKey.F5, KeyEventType.KeyUp, WinVirtualKey.F5)]));
    }

    [Test]
    public void DeferredLeftCtrl_FlushedOnItsOwn_CarriesLeftCtrl()
    {
        var r = new WinKeyResolver();
        Down(r, WinVirtualKey.LControl);

        var flushed = r.FlushDeferredLCtrl();

        Assert.That(flushed, Is.EqualTo(KeyEvent.Special(KeyEventType.KeyDown, SpecialKey.Control_L, KeyModifiers.Control) with
        {
            PhysicalKey = WinVirtualKey.LControl,
        }));
    }

    [Test]
    public void AltGr_CarriesRightAlt()
    {
        var r = new WinKeyResolver();
        Down(r, WinVirtualKey.LControl, time: 42);

        var events = Down(r, WinVirtualKey.RMenu, time: 42)!;

        Assert.That(events.Select(e => (e.Key, e.PhysicalKey)), Is.EqualTo([(SpecialKey.AltGr, WinVirtualKey.RMenu)]));
    }

    // numlock off, the keypad gives the navigation VKs without the extended flag the dedicated keys carry

    [TestCase((uint)WinVirtualKey.Left, SpecialKey.KP_Left)]
    [TestCase((uint)WinVirtualKey.Home, SpecialKey.KP_Home)]
    [TestCase((uint)WinVirtualKey.Next, SpecialKey.KP_PageDown)]
    [TestCase((uint)WinVirtualKey.Delete, SpecialKey.KP_Delete)]
    [TestCase((uint)WinVirtualKey.Clear, SpecialKey.KP_Begin)]
    public void KeypadNavigation_IsTheKeypadKey(uint vk, SpecialKey expected)
    {
        var r = new WinKeyResolver();
        Assert.That(Down(r, vk)!.Select(e => e.Key), Is.EqualTo([expected]));
    }

    [Test]
    public void ExtendedNavigation_IsTheDedicatedKey()
    {
        var r = new WinKeyResolver();
        Assert.That(Down(r, WinVirtualKey.Left, NativeMethods.LLKHF_EXTENDED)!.Select(e => e.Key), Is.EqualTo([SpecialKey.Left]));
    }

    [Test]
    public void InjectedNavigation_WithoutTheExtendedFlag_IsTheDedicatedKey()
    {
        // tools that SendInput an arrow often leave the extended flag off; Windows still takes it as the arrow
        var r = new WinKeyResolver();
        Assert.That(Down(r, WinVirtualKey.Left, NativeMethods.LLKHF_INJECTED)!.Select(e => e.Key), Is.EqualTo([SpecialKey.Left]));
    }

    // Shift changes what NumLock gives a keypad key, so a key's up can arrive as the other VK of its keypad position

    [Test]
    public void KeypadLeft_ReleasedAsNumpad4_ReleasesKeypadLeft()
    {
        var r = new WinKeyResolver();
        Down(r, WinVirtualKey.Left);
        var up = Up(r, WinVirtualKey.Numpad4);
        Assert.That(up!.Select(e => (e.Type, e.Key, e.PhysicalKey)), Is.EqualTo([(KeyEventType.KeyUp, SpecialKey.KP_Left, r.Tracking(Hook(WinVirtualKey.Left)))]));
    }

    [Test]
    public void KeypadLeft_RepeatingAsNumpad4_RepeatsKeypadLeft()
    {
        var r = new WinKeyResolver();
        Down(r, WinVirtualKey.Left);
        var repeat = Down(r, WinVirtualKey.Numpad4);
        var up = Up(r, WinVirtualKey.Numpad4);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(repeat!.Select(e => (e.Key, e.IsRepeat)), Is.EqualTo([(SpecialKey.KP_Left, true)]));
            Assert.That(up!.Select(e => (e.Type, e.Key)), Is.EqualTo([(KeyEventType.KeyUp, SpecialKey.KP_Left)]));
        }
    }

    [Test]
    public void Numpad4_ReleasedAsKeypadLeft_ReleasesKp4()
    {
        var r = new WinKeyResolver();
        Down(r, WinVirtualKey.Numpad4);
        var up = Up(r, WinVirtualKey.Left);
        Assert.That(up!.Select(e => (e.Type, e.Key, e.PhysicalKey)), Is.EqualTo([(KeyEventType.KeyUp, SpecialKey.KP_4, WinVirtualKey.Numpad4)]));
    }

    [Test]
    public void KeypadLeft_AndLeft_HeldTogether_AreTrackedApart()
    {
        var r = new WinKeyResolver();
        Down(r, WinVirtualKey.Left, NativeMethods.LLKHF_EXTENDED);
        var kpDown = Down(r, WinVirtualKey.Left);

        var leftUp = Up(r, WinVirtualKey.Left, NativeMethods.LLKHF_EXTENDED);
        var kpUp = Up(r, WinVirtualKey.Left);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(kpDown!.Select(e => (e.Key, e.IsRepeat)), Is.EqualTo([(SpecialKey.KP_Left, false)]), "a press of its own, not a repeat");
            Assert.That(leftUp!.Select(e => e.Key), Is.EqualTo([SpecialKey.Left]));
            Assert.That(kpUp!.Select(e => e.Key), Is.EqualTo([SpecialKey.KP_Left]));
        }
    }
}

// character-resolution tests require ToUnicodeEx (Windows only)
[TestFixture]
[Platform("Win")]
public class WinKeyResolverCharTests
{
    [Test]
    public void KeypadDecimal_ReleasedAsKeypadDelete_IsTrackedAsTheDecimal()
    {
        var r = new WinKeyResolver();
        Down(r, WinVirtualKey.Decimal);
        Assert.That(r.Tracking(Hook(WinVirtualKey.Delete)), Is.EqualTo(WinVirtualKey.Decimal));
    }

    [Test]
    public void ShiftedKey_ProducesShiftedCharacter()
    {
        // Shift+key (without Ctrl) must produce the shifted character, not the base character.
        // e.g. on a Norwegian layout, Shift+' produces '*' — not "'".
        // This was broken when Shift was stripped unconditionally rather than only under Ctrl.
        const uint vkA = 0x41;
        var r = new WinKeyResolver();
        Down(r, WinVirtualKey.LShift);
        var events = Down(r, vkA);
        Assert.That(events, Is.Not.Null, "Shift+A must produce events");
        var charEvent = events!.FirstOrDefault(e => e.Character != null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(charEvent, Is.Not.Null, "Shift+A must produce a character event");
            // 'A' (uppercase) is the shifted form; base is 'a'
            Assert.That(char.IsUpper(charEvent!.Character!.Value), Is.True,
                "Shift+A must produce an uppercase character, not the base character");
        }
    }

    [TestCase((uint)WinVirtualKey.A, 'a')]
    [TestCase((uint)0x43 /* C */, 'c')]
    [TestCase((uint)0x56 /* V */, 'v')]
    [TestCase((uint)0x5A /* Z */, 'z')]
    public void CtrlKey_ProducesAsciiShortcutCharacter(uint vk, char expectedChar)
    {
        var r = new WinKeyResolver();
        Down(r, WinVirtualKey.LControl);
        var events = Down(r, vk);
        Assert.That(events, Is.Not.Null);
        var charEvent = events!.FirstOrDefault(e => e.Character != null);
        Assert.That(charEvent, Is.Not.Null, $"Ctrl+{expectedChar} must produce character event");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(charEvent!.Character, Is.EqualTo(expectedChar));
            Assert.That(charEvent.Modifiers.HasFlag(KeyModifiers.Control), Is.True);
        }
    }
}

// dead-key + shortcut-flush tests require ToUnicodeEx (Windows only) to produce dead key state.
[TestFixture]
[Platform("Win")]
public class WinKeyResolverDeadKeyTests
{
    [Test]
    public void DeadKey_ThenCtrl_FlushesSpacingFormBeforeShortcutChar()
    {
        var r = new WinKeyResolver();

        // VK_OEM_3 (0xC0) is dead_grave on US-International / US-Extended layouts.
        // if the running layout does not produce a dead key here, skip.
        const uint vkOem3 = 0xC0;
        const uint vkA = 0x41;
        var deadEvents = Down(r, vkOem3);
        if (deadEvents is null || deadEvents.All(e => e.Character == null))
            Assert.Ignore("VK_OEM_3 did not produce a character/dead-key on this layout — cannot test shortcut flush");

        // a dead key was registered (_pendingDeadKey set internally).
        // now press Ctrl+A: should flush spacing form first, then emit 'a' with Control.
        Down(r, WinVirtualKey.LControl);
        var events = Down(r, vkA);

        Assert.That(events, Is.Not.Null, "Ctrl+A after dead key must produce events");
        var chars = events!.Where(e => e.Character != null).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chars, Is.Not.Empty, "at least a spacing form must have been emitted");
            // the shortcut char 'a' must carry Control modifier, not be composed with the dead key
            Assert.That(events.Any(e => e.Character == 'a' && (e.Modifiers & KeyModifiers.Control) != 0), Is.True,
                "Ctrl+A must produce 'a' with Control modifier, not a composed character");
        }
    }
}
