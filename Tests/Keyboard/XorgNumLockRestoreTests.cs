using Hydra.Keyboard;
using Hydra.Platform.Linux;
using Hydra.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Keyboard;

// an X11 slave gives back the NumLock it had once a visit that synced it ends
[TestFixture]
[Category("Linux")]
public class XorgNumLockRestoreTests
{
    private nint _display;
    private uint _numLockKeycode;
    private long _now;
    private XorgOutputHandler _output = null!;

    [OneTimeSetUp]
    public void RequireX11() => X11Required.OrIgnore();

    [SetUp]
    public void Open()
    {
        _display = XlibRuntime.OpenDisplay();
        Assert.That(_display, Is.Not.EqualTo(nint.Zero), "pre-condition: an X server");
        _numLockKeycode = NativeMethods.XKeysymToKeycode(_display, XorgVirtualKey.NumLock);
        Assert.That(new[] { _numLockKeycode, NativeMethods.XKeysymToKeycode(_display, XorgVirtualKey.KP_Home) }, Is.All.Not.Zero,
            "pre-condition: the keymap has NumLock and the keypad");
        _output = new XorgOutputHandler(NullLogger<XorgOutputHandler>.Instance) { TickCount = () => _now };
    }

    [TearDown]
    public void Close()
    {
        _output.Dispose();
        // every Linux test shares the one Xvfb, which starts with NumLock off
        SetNumLockLocally(false);
        _ = NativeMethods.XCloseDisplay(_display);
    }

    [Test]
    public void VisitEnd_GivesBackTheNumLockTheSlaveHad()
    {
        SetNumLockLocally(true);
        Tap(SpecialKey.KP_Home);
        Assert.That(NumLockOn(), Is.False, "pre-condition: synced off for the keypad's Home");

        _output.RestoreLocks();

        Assert.That(NumLockOn(), Is.True);
    }

    [Test]
    public void NumLockChangedOnTheSlavesOwnKeyboardBetweenVisits_IsWhatTheNextVisitSyncsFrom()
    {
        SetNumLockLocally(true);
        Tap(SpecialKey.KP_Home);
        _output.RestoreLocks();
        SetNumLockLocally(false);
        _now += 1000;

        _output.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, null, SpecialKey.KP_Home));
        var underNumLock = NumLockOn();
        _output.InjectKey(new KeyEventMessage(KeyEventType.KeyUp, KeyModifiers.None, null, SpecialKey.KP_Home));
        _output.RestoreLocks();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(underNumLock, Is.False, "the keypad's Home went in as Home, not 7");
            Assert.That(NumLockOn(), Is.False, "the slave's own choice stands");
        }
    }

    private void Tap(SpecialKey key)
    {
        _output.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, null, key));
        _output.InjectKey(new KeyEventMessage(KeyEventType.KeyUp, KeyModifiers.None, null, key));
    }

    private bool NumLockOn()
    {
        _ = NativeMethods.XSync(_display, false);
        Assert.That(NativeMethods.XkbGetState(_display, NativeMethods.XkbUseCoreKbd, out var state), Is.Zero);
        return (state.LockedMods & NativeMethods.Mod2Mask) != 0;
    }

    private void SetNumLockLocally(bool on)
    {
        if (NumLockOn() == on) return;
        _ = NativeMethods.XTestFakeKeyEvent(_display, _numLockKeycode, true, 0);
        _ = NativeMethods.XTestFakeKeyEvent(_display, _numLockKeycode, false, 0);
        _ = NativeMethods.XSync(_display, false);
    }
}
