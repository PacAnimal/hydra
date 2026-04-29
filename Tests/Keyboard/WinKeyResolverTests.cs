using Hydra.Keyboard;
using Hydra.Platform.Windows;

namespace Tests.Keyboard;

[TestFixture]
public class WinKeyResolverTests
{
    private static KBDLLHOOKSTRUCT Hook(uint vk, uint flags = 0, uint time = 1) =>
        new() { vkCode = vk, scanCode = 0, flags = flags, time = time, dwExtraInfo = 0 };

    private static KeyEvent[]? Down(WinKeyResolver r, uint vk) =>
        r.Resolve(NativeMethods.WM_KEYDOWN, Hook(vk));

    private static KeyEvent[]? Up(WinKeyResolver r, uint vk) =>
        r.Resolve(NativeMethods.WM_KEYUP, Hook(vk));

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
    public void NumpadDigit_AutoRepeat_Suppressed()
    {
        var r = new WinKeyResolver();
        Down(r, WinVirtualKey.Numpad7);
        var repeat = Down(r, WinVirtualKey.Numpad7);
        Assert.That(repeat, Is.Null);
    }

    // VK_RETURN is shared by both regular Enter (no extended flag) and KP_Enter (extended flag).
    // pressing one while the other is held must NOT be suppressed as auto-repeat.

    private static KBDLLHOOKSTRUCT ExtendedHook(uint vk, uint time = 1) =>
        new() { vkCode = vk, scanCode = 0, flags = NativeMethods.LLKHF_EXTENDED, time = time, dwExtraInfo = 0 };

    [Test]
    public void KpEnter_WhileReturnTracked_NotSuppressed()
    {
        var r = new WinKeyResolver();
        Down(r, WinVirtualKey.Return);  // regular Return tracked

        // KP_Enter (EXTENDED) arrives — must not be suppressed by the tracked Return
        var events = r.Resolve(NativeMethods.WM_KEYDOWN, ExtendedHook(WinVirtualKey.Return));
        Assert.That(events, Is.Not.Null);
        Assert.That(events!.Any(e => e.Key == SpecialKey.KP_Enter), Is.True,
            "KP_Enter must fire even while regular Return is held");
    }

    [Test]
    public void Return_WhileKpEnterTracked_NotSuppressed()
    {
        var r = new WinKeyResolver();
        r.Resolve(NativeMethods.WM_KEYDOWN, ExtendedHook(WinVirtualKey.Return));  // KP_Enter tracked

        // regular Return (no EXTENDED flag) arrives — must not be suppressed by the tracked KP_Enter
        var events = Down(r, WinVirtualKey.Return);
        Assert.That(events, Is.Not.Null);
        Assert.That(events!.Any(e => e.Key == SpecialKey.Return), Is.True,
            "regular Return must fire even while KP_Enter is held");
    }

    [Test]
    public void KpEnter_AutoRepeat_Suppressed()
    {
        var r = new WinKeyResolver();
        r.Resolve(NativeMethods.WM_KEYDOWN, ExtendedHook(WinVirtualKey.Return));

        var repeat = r.Resolve(NativeMethods.WM_KEYDOWN, ExtendedHook(WinVirtualKey.Return));
        Assert.That(repeat, Is.Null);
    }

    // key-up must replay the correct key even when both Return and KP_Enter are held simultaneously.

    [Test]
    public void Return_KeyUp_WhileKpEnterAlsoHeld_ReplaysReturn()
    {
        var r = new WinKeyResolver();
        Down(r, WinVirtualKey.Return);
        r.Resolve(NativeMethods.WM_KEYDOWN, ExtendedHook(WinVirtualKey.Return));  // KP_Enter also down

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
        r.Resolve(NativeMethods.WM_KEYDOWN, ExtendedHook(WinVirtualKey.Return));  // KP_Enter also down

        var up = r.Resolve(NativeMethods.WM_KEYUP, ExtendedHook(WinVirtualKey.Return));
        Assert.That(up, Is.Not.Null);
        Assert.That(up!.Any(e => e is { Key: SpecialKey.KP_Enter, Type: KeyEventType.KeyUp }), Is.True,
            "KP_Enter key-up must replay KP_Enter, not Return");
    }

    [Test]
    public void Return_KeyUp_AfterKpEnterReleased_StillReplaysReturn()
    {
        var r = new WinKeyResolver();
        Down(r, WinVirtualKey.Return);
        r.Resolve(NativeMethods.WM_KEYDOWN, ExtendedHook(WinVirtualKey.Return));   // KP_Enter down
        r.Resolve(NativeMethods.WM_KEYUP, ExtendedHook(WinVirtualKey.Return));     // KP_Enter up

        var up = Up(r, WinVirtualKey.Return);
        Assert.That(up, Is.Not.Null);
        Assert.That(up!.Any(e => e is { Key: SpecialKey.Return, Type: KeyEventType.KeyUp }), Is.True,
            "Return key-up must still fire after KP_Enter was separately released");
    }
}

// dead-key + shortcut-flush tests require ToUnicodeEx (Windows only) to produce dead key state.
[TestFixture]
[Platform("Win")]
public class WinKeyResolverDeadKeyTests
{
    private static KBDLLHOOKSTRUCT Hook(uint vk, uint flags = 0) =>
        new() { vkCode = vk, scanCode = 0, flags = flags, time = 1, dwExtraInfo = 0 };

    private static KeyEvent[]? Down(WinKeyResolver r, uint vk, uint flags = 0) =>
        r.Resolve(NativeMethods.WM_KEYDOWN, Hook(vk, flags));

    [Test]
    public void DeadKey_ThenCtrl_FlushesSpacingFormBeforeShortcutChar()
    {
        var r = new WinKeyResolver();

        // VK_OEM_3 (0xC0) is dead_grave on US-International / US-Extended layouts.
        // if the running layout does not produce a dead key here, skip.
        // ReSharper disable InconsistentNaming
        const uint VkOem3 = 0xC0;
        const uint VkA = 0x41;
        // ReSharper restore InconsistentNaming
        var deadEvents = Down(r, VkOem3);
        if (deadEvents is null || deadEvents.All(e => e.Character == null))
            Assert.Ignore("VK_OEM_3 did not produce a character/dead-key on this layout — cannot test shortcut flush");

        // a dead key was registered (_pendingDeadKey set internally).
        // now press Ctrl+A: should flush spacing form first, then emit 'a' with Control.
        Down(r, WinVirtualKey.LControl);
        var events = Down(r, VkA);

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
