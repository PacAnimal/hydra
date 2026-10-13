using Hydra.Keyboard;
using Hydra.Platform.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Platform.Windows;

// A key the OS holds down while the cursor is away was pressed while local: its up must reach the OS, or the key
// stays down there. Every event still goes to the router, which drops what it never forwarded the down of.
[TestFixture]
public class WindowsInputHandlerHeldKeyTests
{
    private sealed class Hooked
    {
        public readonly HashSet<int> OsHeld = [];
        public readonly List<object> Handed = [];
        public readonly WindowsInputHandler Handler;
        public int Now;

        public Hooked()
        {
            Handler = new WindowsInputHandler(NullLogger<WindowsInputHandler>.Instance, TransitionTestHelper.Profile("home"))
            {
                IsKeyDown = vk => OsHeld.Contains(vk),
                TickCount = () => Now,
            };
            Handler.Subscribe((_, _) => { }, null, e => Handed.Add(e), e => Handed.Add(e), e => Handed.Add(e));
        }

        public List<KeyEvent> Keys => [.. Handed.OfType<KeyEvent>()];

        public bool Down(int vk, uint flags = 0, uint time = 1) =>
            Handler.HandleKey(NativeMethods.WM_KEYDOWN, new KBDLLHOOKSTRUCT { vkCode = (uint)vk, flags = flags, time = time });

        public bool Up(int vk, uint flags = 0, uint time = 1) =>
            Handler.HandleKey(NativeMethods.WM_KEYUP, new KBDLLHOOKSTRUCT { vkCode = (uint)vk, flags = flags, time = time });

        public bool Click() => Handler.HandleMouse(NativeMethods.WM_LBUTTONDOWN, default, static (_, _) => { });

        public bool Wheel() => Handler.HandleMouse(NativeMethods.WM_MOUSEWHEEL, new MSLLHOOKSTRUCT { mouseData = 120u << 16 }, static (_, _) => { });

        // a key pressed while local reached the OS, so it holds it down
        public void PressLocally(int vk, uint flags = 0)
        {
            Assert.That(Down(vk, flags), Is.False, "pre-condition: a local press passes through");
            OsHeld.Add(vk);
        }
    }

    [Test]
    public void HeldAtEntry_ItsUpPassesThroughAndIsHanded_ItsRepeatsAreSwallowed()
    {
        var h = new Hooked();
        h.PressLocally(WinVirtualKey.F5);
        h.Handler.IsOnVirtualScreen = true;
        h.Handed.Clear();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Down(WinVirtualKey.F5), Is.True, "a repeat stays swallowed");
            Assert.That(h.Up(WinVirtualKey.F5), Is.False, "the up must reach the OS");
            Assert.That(h.Keys.Select(k => (k.Type, k.Key, k.IsRepeat)),
                Is.EqualTo([(KeyEventType.KeyDown, SpecialKey.F5, true), (KeyEventType.KeyUp, SpecialKey.F5, false)]));
        }
    }

    [Test]
    public void PressedDuringTheVisit_IsSwallowedAndHanded()
    {
        var h = new Hooked();
        h.Handler.IsOnVirtualScreen = true;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Down(WinVirtualKey.F5), Is.True);
            Assert.That(h.Up(WinVirtualKey.F5), Is.True);
            Assert.That(h.Keys.Select(k => (k.Type, k.Key)),
                Is.EqualTo([(KeyEventType.KeyDown, SpecialKey.F5), (KeyEventType.KeyUp, SpecialKey.F5)]));
        }
    }

    [Test]
    public void KpEnterHeldAtEntry_ItsUpPassesThrough()
    {
        // the OS knows KP_Enter as VK_RETURN, whatever the resolver tracks it under
        var h = new Hooked();
        h.PressLocally(WinVirtualKey.Return, NativeMethods.LLKHF_EXTENDED);
        h.Handler.IsOnVirtualScreen = true;

        Assert.That(h.Up(WinVirtualKey.Return, NativeMethods.LLKHF_EXTENDED), Is.False);
    }

    [TestCase(nameof(Hooked.Click))]
    [TestCase(nameof(Hooked.Wheel))]
    public void LeftCtrlHeldAtEntry_AMouseEventHandsNoControlDown_ItsUpPassesThroughAndIsHanded(string mouse)
    {
        var h = new Hooked();
        h.PressLocally(WinVirtualKey.LControl);
        h.Handler.IsOnVirtualScreen = true;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Fire(h, mouse), Is.True);
            Assert.That(h.Up(WinVirtualKey.LControl), Is.False, "the up must reach the OS");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Keys.Where(k => k.Type == KeyEventType.KeyDown), Is.Empty);
            Assert.That(h.Keys.Select(k => (k.Type, k.Key)), Is.EqualTo([(KeyEventType.KeyUp, SpecialKey.Control_L)]));
        }
    }

    [TestCase(nameof(Hooked.Click))]
    [TestCase(nameof(Hooked.Wheel))]
    public void LeftCtrlPressedDuringTheVisit_ReachesTheRouterBeforeAMouseEvent(string mouse)
    {
        var h = new Hooked();
        h.Handler.IsOnVirtualScreen = true;
        Assert.That(h.Down(WinVirtualKey.LControl), Is.True);

        Fire(h, mouse);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Handed, Has.Count.EqualTo(2));
            Assert.That(h.Handed[0], Is.EqualTo(KeyEvent.Special(KeyEventType.KeyDown, SpecialKey.Control_L, KeyModifiers.Control) with
            {
                PhysicalKey = WinVirtualKey.LControl,
            }));
            Assert.That(h.Handed[1], Is.Not.InstanceOf<KeyEvent>());
        }
    }

    [Test]
    public void LeftCtrlHeldAtEntry_ReleasedAlone_HandsNoControlDown()
    {
        // the up flushes the deferred down ahead of itself
        var h = new Hooked();
        h.PressLocally(WinVirtualKey.LControl);
        h.Handler.IsOnVirtualScreen = true;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Up(WinVirtualKey.LControl), Is.False);
            Assert.That(h.Keys.Select(k => (k.Type, k.Key)), Is.EqualTo([(KeyEventType.KeyUp, SpecialKey.Control_L)]));
        }
    }

    [Test]
    [Platform("Win")] // key resolution needs ToUnicodeEx
    public void LeftCtrlHeldAtEntry_ACharacterKeyHandsNoControlDown()
    {
        var h = new Hooked();
        h.PressLocally(WinVirtualKey.LControl);
        h.Handler.IsOnVirtualScreen = true;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Down(WinVirtualKey.A), Is.True);
            Assert.That(h.Keys.Select(k => (k.Type, k.Key, k.Character)), Is.EqualTo([(KeyEventType.KeyDown, (SpecialKey?)null, (char?)'a')]));
        }
    }

    [Test]
    public void LeftCtrlFlushedByARacingKeyBeforeTheFlip_IsHanded_AndSoIsItsUp()
    {
        // the router may process both after the entry: the down then counts as the visit's, so its up must reach it
        var h = new Hooked();
        h.PressLocally(WinVirtualKey.LControl);
        Assert.That(h.Down(WinVirtualKey.F5), Is.False);
        h.OsHeld.Add(WinVirtualKey.F5);
        h.Handler.IsOnVirtualScreen = true;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Up(WinVirtualKey.LControl), Is.False, "the up must reach the OS");
            Assert.That(h.Keys.Select(k => (k.Type, k.Key)), Is.EqualTo([
                (KeyEventType.KeyDown, SpecialKey.Control_L), (KeyEventType.KeyDown, SpecialKey.F5), (KeyEventType.KeyUp, SpecialKey.Control_L)]));
        }
    }

    [Test]
    public void PressedDuringTheVisit_HeldAfterReturn_ItsRepeatsAndUpAreSwallowed()
    {
        // hold Backspace on the slave, cross back: the master must not start deleting
        var h = new Hooked();
        h.Handler.IsOnVirtualScreen = true;
        Assert.That(h.Down(WinVirtualKey.F5), Is.True);
        h.Handler.IsOnVirtualScreen = false;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Down(WinVirtualKey.F5), Is.True, "a repeat stays away");
            Assert.That(h.Up(WinVirtualKey.F5), Is.True, "the up stays away");
        }
    }

    [Test]
    public void PressedLocallyAfterReturn_Passes()
    {
        var h = new Hooked();
        h.Handler.IsOnVirtualScreen = true;
        h.Down(WinVirtualKey.F5);
        h.Up(WinVirtualKey.F5);
        h.Handler.IsOnVirtualScreen = false;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Down(WinVirtualKey.F5), Is.False);
            Assert.That(h.Up(WinVirtualKey.F5), Is.False);
        }
    }

    [Test]
    public void PressedLocally_HeldThroughAVisitAndTheReturn_ItsRepeatsAndUpPass()
    {
        var h = new Hooked();
        h.PressLocally(WinVirtualKey.F5);
        h.Handler.IsOnVirtualScreen = true;
        Assert.That(h.Down(WinVirtualKey.F5), Is.True, "pre-condition: no repeats while away");
        h.Handler.IsOnVirtualScreen = false;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Down(WinVirtualKey.F5), Is.False, "repeats resume at home");
            Assert.That(h.Up(WinVirtualKey.F5), Is.False);
        }
    }

    [Test]
    public void HeldBeforeTheHookSawItsPress_ItsUpPassesWhileVirtual()
    {
        var h = new Hooked();
        h.OsHeld.Add(WinVirtualKey.F5);
        h.Handler.IsOnVirtualScreen = true;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Down(WinVirtualKey.F5), Is.True, "a repeat stays swallowed");
            Assert.That(h.Up(WinVirtualKey.F5), Is.False, "the up must reach the OS");
        }
    }

    [Test]
    public void PressedDuringTheVisit_HeldIntoASecondVisit_StaysAway()
    {
        var h = new Hooked();
        h.Handler.IsOnVirtualScreen = true;
        h.Down(WinVirtualKey.F5);
        h.Handler.IsOnVirtualScreen = false;
        Assert.That(h.Down(WinVirtualKey.F5), Is.True, "pre-condition");
        h.Handler.IsOnVirtualScreen = true;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Down(WinVirtualKey.F5), Is.True);
            Assert.That(h.Up(WinVirtualKey.F5), Is.True);
        }
    }

    [Test]
    public void LeftCtrlPressedDuringTheVisit_ReleasedAfterReturn_IsSwallowed()
    {
        // deferred as a possible AltGr, so the resolver hands nothing for its press
        var h = new Hooked();
        h.Handler.IsOnVirtualScreen = true;
        Assert.That(h.Down(WinVirtualKey.LControl), Is.True);
        h.Handler.IsOnVirtualScreen = false;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Down(WinVirtualKey.LControl), Is.True);
            Assert.That(h.Up(WinVirtualKey.LControl), Is.True);
        }
    }

    [Test]
    public void AltGrPressedDuringTheVisit_HeldAfterReturn_ItsRepeatsAndUpAreSwallowed()
    {
        var h = new Hooked();
        h.Handler.IsOnVirtualScreen = true;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Down(WinVirtualKey.LControl, NativeMethods.LLKHF_INJECTED), Is.True);
            Assert.That(h.Down(WinVirtualKey.RMenu), Is.True);
        }
        h.Handler.IsOnVirtualScreen = false;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Down(WinVirtualKey.LControl, NativeMethods.LLKHF_INJECTED), Is.True);
            Assert.That(h.Down(WinVirtualKey.RMenu), Is.True);
            Assert.That(h.Up(WinVirtualKey.LControl, NativeMethods.LLKHF_INJECTED), Is.True);
            Assert.That(h.Up(WinVirtualKey.RMenu), Is.True);
        }
    }

    [Test]
    [Platform("Win")] // key resolution needs ToUnicodeEx
    public void DeadKeyPressedDuringTheVisit_HeldAfterReturn_ItsRepeatsAndUpAreSwallowed()
    {
        // VK_OEM_3 is dead_grave on US-International; the hook decides by the key, whatever the resolver made of it
        const int vkOem3 = 0xC0;
        var h = new Hooked();
        h.Handler.IsOnVirtualScreen = true;
        Assert.That(h.Down(vkOem3), Is.True);
        h.Handler.IsOnVirtualScreen = false;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Down(vkOem3), Is.True);
            Assert.That(h.Up(vkOem3), Is.True);
        }
    }

    [Test]
    public void AQuietKeyboardReinstall_ForgetsWhereKeysWent()
    {
        // an up a dead hook lost can no longer swallow a later one
        var h = new Hooked();
        h.Handler.IsOnVirtualScreen = true;
        h.Down(WinVirtualKey.F5);
        h.Handler.IsOnVirtualScreen = false;
        h.Now += 5000;
        h.Click();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Handler.TakeQuietHooks(), Is.EqualTo(WindowsInputHandler.Hooks.Keyboard), "pre-condition");

            Assert.That(h.Up(WinVirtualKey.F5), Is.False);
        }
    }

    private static bool Fire(Hooked h, string mouse) => mouse == nameof(Hooked.Click) ? h.Click() : h.Wheel();
}
