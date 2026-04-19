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

    [TestCase((uint)WinVirtualKey.Numpad0, '0')]
    [TestCase((uint)WinVirtualKey.Numpad1, '1')]
    [TestCase((uint)WinVirtualKey.Numpad5, '5')]
    [TestCase((uint)WinVirtualKey.Numpad9, '9')]
    [TestCase((uint)WinVirtualKey.Decimal, '.')]
    public void NumpadDigit_WithNumLockOn_EmitsCharEvent(uint vk, char expected)
    {
        // WinVirtualKey.Numpad0-9 only appear in the LL hook when NumLock is active.
        // The resolver should emit a char event, not SpecialKey.KP_*, so the slave
        // doesn't need to have NumLock active to produce the digit.
        var r = new WinKeyResolver();
        var events = Down(r, vk);
        Assert.That(events, Has.Length.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(events![0].Character, Is.EqualTo(expected));
            Assert.That(events[0].Key, Is.Null);
            Assert.That(events[0].Type, Is.EqualTo(KeyEventType.KeyDown));
        }
    }

    [Test]
    public void NumpadDigit_KeyUp_ReplaysSameChar()
    {
        var r = new WinKeyResolver();
        Down(r, WinVirtualKey.Numpad7);
        var up = Up(r, WinVirtualKey.Numpad7);
        Assert.That(up, Has.Length.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(up![0].Character, Is.EqualTo('7'));
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
}
