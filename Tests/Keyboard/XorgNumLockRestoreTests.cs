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
    private XorgOutputHandler? _output;

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
        _output?.Dispose();
        _output = null;
        if (_display == nint.Zero) return;
        // every Linux test shares the one Xvfb, which starts with NumLock off
        if (_numLockKeycode != 0) SetNumLockLocally(false);
        _ = NativeMethods.XCloseDisplay(_display);
        _display = nint.Zero;
    }

    [Test]
    public void VisitEnd_GivesBackTheNumLockTheSlaveHad()
    {
        SetNumLockLocally(true);
        Tap(SpecialKey.KP_Home);
        Assert.That(NumLockOn(), Is.False, "pre-condition: synced off for the keypad's Home");

        _output!.EndVisit();

        Assert.That(NumLockOn(), Is.True);
    }

    [Test]
    public void NumLockChangedOnTheSlavesOwnKeyboardBetweenVisits_IsWhatTheNextVisitSyncsFrom()
    {
        SetNumLockLocally(true);
        Tap(SpecialKey.KP_Home);
        _output!.EndVisit();
        SetNumLockLocally(false);
        _now += 1000;

        _output.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, null, SpecialKey.KP_Home));
        var underNumLock = NumLockOn();
        _output.InjectKey(new KeyEventMessage(KeyEventType.KeyUp, KeyModifiers.None, null, SpecialKey.KP_Home));
        _output.EndVisit();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(underNumLock, Is.False, "the keypad's Home went in as Home, not 7");
            Assert.That(NumLockOn(), Is.False, "the slave's own choice stands");
        }
    }

    private void Tap(SpecialKey key)
    {
        _output!.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, null, key));
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

// an X11 slave types a character its layout lacks on a spare keycode, and the binding outlives the key: a client may
// fetch the keymap only after the key event, and must find the character there
[TestFixture]
[Category("Linux")]
public class XorgTempBindingTests
{
    private nint _display;
    private XorgOutputHandler? _output;

    [OneTimeSetUp]
    public void RequireX11() => X11Required.OrIgnore();

    [SetUp]
    public void Open()
    {
        _display = XlibRuntime.OpenDisplay();
        Assert.That(_display, Is.Not.EqualTo(nint.Zero), "pre-condition: an X server");
        _output = new XorgOutputHandler(NullLogger<XorgOutputHandler>.Instance);
        Assert.That(_output.SpareKeycodes, Is.GreaterThanOrEqualTo(3), "pre-condition: spare keycodes");
    }

    [TearDown]
    public void Close()
    {
        _output?.Dispose();
        _output = null;
        if (_display == nint.Zero) return;
        _ = NativeMethods.XCloseDisplay(_display);
        _display = nint.Zero;
    }

    [Test]
    public void ACharacterTypedTwice_ReusesOneKeycode_AndRemapsOnce()
    {
        Tap('ж');
        var first = KeycodesOf('ж');
        Tap('ж');

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Has.Count.EqualTo(1), "pre-condition: bound on one keycode");
            Assert.That(KeycodesOf('ж'), Is.EqualTo(first));
            Assert.That(_output!.Remaps, Is.EqualTo(1));
        }
    }

    [Test]
    public void AfterKeyUp_TheBindingIsStillInTheServersKeymap()
    {
        Tap('ж');

        Assert.That(KeycodesOf('ж'), Has.Count.EqualTo(1));
    }

    [Test]
    public void EverySpareKeycodeBound_ANewCharacterEvictsTheLeastRecentlyUsed_NeverAHeldOne()
    {
        const char held = '\u4E00';  // CJK, so none of the run is in the test server's layout
        _output!.InjectKey(Char(KeyEventType.KeyDown, held));
        var fillers = Enumerable.Range(1, _output.SpareKeycodes - 1).Select(i => (char)(held + i)).ToList();
        foreach (var ch in fillers) Tap(ch);
        var lruKeycodes = KeycodesOf(fillers[0]);
        var next = (char)(held + _output.SpareKeycodes);

        Tap(next);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lruKeycodes, Has.Count.EqualTo(1), "pre-condition: the oldest released one was bound");
            Assert.That(KeycodesOf(next), Is.EqualTo(lruKeycodes), "took the least recently used binding's keycode");
            Assert.That(KeycodesOf(fillers[0]), Is.Empty);
            Assert.That(KeycodesOf(held), Has.Count.EqualTo(1), "the held one, older still, kept its binding");
            Assert.That(KeycodesOf(fillers[1]), Has.Count.EqualTo(1));
        }
        _output.InjectKey(Char(KeyEventType.KeyUp, held));
    }

    [Test]
    public void Dispose_UnbindsEveryTempKeycode()
    {
        var chars = new[] { 'ж', 'щ', 'Ї' };
        foreach (var ch in chars) Tap(ch);
        Assert.That(chars.Select(KeycodesOf), Has.All.Count.EqualTo(1), "pre-condition: all bound");

        _output!.Dispose();

        Assert.That(chars.Select(KeycodesOf), Has.All.Empty);
    }

    [Test]
    public void ACapitalTempBoundOnce_WithShift_UsesItsOwnKey()
    {
        const uint aKey = 38;  // the A key, on Xvfb's evdev rules
        // 'A' with no Shift is not on the A key's level the slave's modifiers give, so it goes in temp-bound
        Tap('A');
        var spare = KeycodesOf('A');

        _output!.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.Shift, 'A', null));
        // the handler only flushes, so the press may reach the server after this connection asks: wait for it, bounded
        var held = XorgDisplayHelper.HeldKeycodes(_display);
        for (var deadline = Environment.TickCount64 + 2000; !held.Contains(aKey) && Environment.TickCount64 < deadline;)
        {
            Thread.Sleep(10);
            held = XorgDisplayHelper.HeldKeycodes(_display);
        }
        _output.InjectKey(new KeyEventMessage(KeyEventType.KeyUp, KeyModifiers.Shift, 'A', null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(spare, Has.Count.EqualTo(1).And.No.Member(aKey), "pre-condition: bound on a spare keycode");
            Assert.That(held, Does.Contain(aKey));
            Assert.That(held, Has.No.Member(spare.FirstOrDefault()));
            Assert.That(_output.Remaps, Is.EqualTo(1));
        }
    }

    private static KeyEventMessage Char(KeyEventType type, char ch) => new(type, KeyModifiers.None, ch, null);

    private void Tap(char ch)
    {
        _output!.InjectKey(Char(KeyEventType.KeyDown, ch));
        _output.InjectKey(Char(KeyEventType.KeyUp, ch));
    }

    // the keycodes whose base level holds the character, as the server has it now
    private unsafe List<uint> KeycodesOf(char ch)
    {
        _ = NativeMethods.XDisplayKeycodes(_display, out var min, out var max);
        var map = NativeMethods.XGetKeyboardMapping(_display, (uint)min, max - min + 1, out var perKeycode);
        Assert.That(map, Is.Not.EqualTo(nint.Zero));
        var keysyms = (ulong*)map;
        var keysym = XorgKeysym.Of(ch);
        var found = new List<uint>();
        for (var i = 0; i <= max - min; i++)
            if (keysyms[i * perKeycode] == keysym) found.Add((uint)(min + i));
        _ = NativeMethods.XFree(map);
        return found;
    }
}
