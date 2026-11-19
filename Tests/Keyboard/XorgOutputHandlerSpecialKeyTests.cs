using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Hydra.Keyboard;
using Hydra.Platform.Linux;
using Hydra.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Keyboard;

// an X11 slave presses a special key where the active group has it, lets go of the keycode it pressed, and follows a
// keymap another client changed. Xvfb has no setxkbmap here, so a second group is built with XChangeKeyboardMapping on
// two empty keycodes, set before the handler opens so it does not take them for spares
[TestFixture]
[Category("Linux")]
public sealed partial class XorgOutputHandlerSpecialKeyTests
{
    private nint _display;
    private int _perKeycode;
    private uint _first;
    private uint _second;
    private SpecialKey _key;
    private ulong _keysym;
    private XorgOutputHandler? _output;
    private uint _altR;
    private ulong[] _altRRow = [];

    // a keysym no key of the test server's keymap types, for the keycodes that are not the key's
    private static readonly ulong Other = XorgKeysym.Of('☃');

    [OneTimeSetUp]
    public void RequireX11() => X11Required.OrIgnore();

    [SetUp]
    public unsafe void Open()
    {
        _display = XlibRuntime.OpenDisplay();
        Assert.That(_display, Is.Not.EqualTo(nint.Zero), "pre-condition: an X server");
        // an F key or media key, nothing that syncs a lock or is a modifier
        _key = Enum.GetValues<SpecialKey>()
            .Where(k => XorgKeysym.Injected(k) is >= XorgVirtualKey.F1 and <= XorgVirtualKey.F20 or >= 0x1008FF00 and <= 0x1008FFFF)
            .FirstOrDefault(k => NativeMethods.XKeysymToKeycode(_display, XorgKeysym.Injected(k)) == 0);
        Assume.That(_key, Is.Not.EqualTo(default(SpecialKey)), "pre-condition: an F or media key the keymap lacks");
        _keysym = XorgKeysym.Injected(_key);

        _ = NativeMethods.XDisplayKeycodes(_display, out var min, out var max);
        var map = NativeMethods.XGetKeyboardMapping(_display, (uint)min, max - min + 1, out _perKeycode);
        Assert.That(map, Is.Not.EqualTo(nint.Zero));
        var empty = new List<uint>();
        var keysyms = (ulong*)map;
        for (var i = max - min; i >= 0 && empty.Count < 2; i--)
        {
            var allEmpty = true;
            for (var j = 0; j < _perKeycode; j++) allEmpty &= keysyms[i * _perKeycode + j] == 0;
            if (allEmpty) empty.Add((uint)(min + i));
        }
        _ = NativeMethods.XFree(map);
        Assert.That(empty, Has.Count.EqualTo(2), "pre-condition: two empty keycodes");
        (_first, _second) = (empty[1], empty[0]);
    }

    [TearDown]
    public void Close()
    {
        _output?.Dispose();
        _output = null;
        if (_display == nint.Zero) return;
        if (_first != 0)
        {
            Map(_first);
            Map(_second);
        }
        if (_altR != 0) Map(_altR, _altRRow);
        _altR = 0;
        // every Linux test shares the one Xvfb, whose keyboard starts in its first group
        _ = XkbLockGroup(_display, NativeMethods.XkbUseCoreKbd, 0);
        _ = NativeMethods.XSync(_display, false);
        _ = NativeMethods.XCloseDisplay(_display);
        _display = nint.Zero;
    }

    [Test]
    public void ASpecialKeyHeldAcrossAGroupChange_IsReleasedOnTheKeycodeItWentDownOn()
    {
        Map(_first, Other);
        Map(_second, Other);
        Open(group: 0);
        // bound on a spare, lower than both keycodes, while no key has it: XKeysymToKeycode then finds it there first,
        // so the key goes down through the group-aware lookup that once picked the key-up's keycode afresh
        Tap();
        var spare = KeycodesOf(_keysym);
        // group 0 has it on the first keycode, group 1 on the second
        Map(_first, _keysym, 0, Other, 0);
        Map(_second, Other, 0, _keysym, 0);

        Inject(KeyEventType.KeyDown);
        var down = WaitHeld(held => held.Contains(_first));
        LockGroup(1);
        Inject(KeyEventType.KeyUp);
        var up = WaitHeld(held => !held.Contains(_first));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(spare, Has.Count.EqualTo(1), "pre-condition: temp-bound first");
            Assert.That(down, Does.Contain(_first), "pre-condition: down on group 0's key");
            Assert.That(up, Has.No.Member(_first));
        }
    }

    // the outside-remap twin of this was dropped: it passed without the held record too, the old lookup finding the
    // first keycode again after the remap, so it pinned nothing. a group change, and the visit end here, tell them apart
    [Test]
    public void AModifierHeldAcrossAGroupChange_IsReleasedAtVisitEnd()
    {
        var shift = NativeMethods.XKeysymToKeycode(_display, XorgVirtualKey.Shift_L);
        Assume.That(shift, Is.Not.Zero, "pre-condition: the keymap has Shift_L");
        // any key with a second group, so the server has one to lock
        Map(_second, Other, 0, Other, 0);
        Open(group: 0);

        _output!.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.Shift, null, SpecialKey.Shift_L));
        var down = WaitHeld(held => held.Contains(shift));
        LockGroup(1);
        _output.EndVisit();
        var after = HeldAfterHandler();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(down, Does.Contain(shift), "pre-condition: down");
            Assert.That(after, Has.No.Member(shift));
        }
    }

    [Test]
    public void ACharacterRepeatedAcrossAGroupChange_IsReleasedByItsOneKeyUp()
    {
        const char ch = 'ж';
        var keysym = XorgKeysym.Of(ch);
        // group 0 types it on the first keycode, group 1 on the second, so a lookup under group 1 finds no key at the
        // first's and would temp-bind the repeat on a spare
        Map(_first, keysym, 0, Other, 0);
        Map(_second, Other, 0, keysym, 0);
        Open(group: 0);

        InjectChar(KeyEventType.KeyDown, ch);
        var down = WaitHeld(held => held.Contains(_first));
        LockGroup(1);
        InjectChar(KeyEventType.KeyDown, ch);
        InjectChar(KeyEventType.KeyUp, ch);
        var up = HeldAfterHandler();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(down, Does.Contain(_first), "pre-condition: down on group 0's key");
            Assert.That(up, Has.No.Member(_first).And.No.Member(_second));
            Assert.That(up.Intersect(KeycodesOf(keysym)), Is.Empty, "no spare left down");
            Assert.That(_output!.Remaps, Is.Zero, "the repeat went on the held keycode, nothing temp-bound");
        }
    }

    // as Print and Sys_Req share a keycode: Sys_Req is no SpecialKey, so two the keymap lacks stand in for them
    [Test]
    public void TwoSpecialKeysOnOneKeycode_TheFirstUpLetsItGo()
    {
        var second = Enum.GetValues<SpecialKey>()
            .Where(k => k != _key && XorgKeysym.Injected(k) is >= XorgVirtualKey.F1 and <= XorgVirtualKey.F20 or >= 0x1008FF00 and <= 0x1008FFFF)
            .FirstOrDefault(k => NativeMethods.XKeysymToKeycode(_display, XorgKeysym.Injected(k)) == 0);
        Assume.That(second, Is.Not.EqualTo(default(SpecialKey)), "pre-condition: a second key the keymap lacks");
        Map(_first, _keysym, XorgKeysym.Injected(second));
        Map(_second, Other);
        Open(group: 0);

        Inject(KeyEventType.KeyDown);
        Inject(KeyEventType.KeyDown, second);
        var bothDown = WaitHeld(held => held.Contains(_first));
        Inject(KeyEventType.KeyUp);
        var oneUp = WaitHeld(held => !held.Contains(_first));
        // pressed by this client, so a stray up of the handler's would show as it let go
        _ = NativeMethods.XTestFakeKeyEvent(_display, _first, true, 0);
        _ = NativeMethods.XSync(_display, false);
        Inject(KeyEventType.KeyUp, second);
        var otherUp = HeldAfterHandler();
        _output!.EndVisit();
        var afterVisit = HeldAfterHandler();
        _ = NativeMethods.XTestFakeKeyEvent(_display, _first, false, 0);
        _ = NativeMethods.XSync(_display, false);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bothDown, Does.Contain(_first), "pre-condition: down");
            Assert.That(oneUp, Has.No.Member(_first), "the first up lets it go");
            Assert.That(otherUp, Does.Contain(_first), "the second up sends nothing");
            Assert.That(afterVisit, Does.Contain(_first), "no stray up at visit end");
            Assert.That(_output.Remaps, Is.Zero);
        }
    }

    // the master re-resolves a held key's repeat with the live modifiers (e, then E once Shift went down) and replays
    // the down's character on the up
    [Test]
    public void ARepeatReResolvedUnderShift_IsReleasedByTheDownsKeyUp()
    {
        var e = NativeMethods.XKeysymToKeycode(_display, 'e');
        Assume.That(e, Is.Not.Zero, "pre-condition: the keymap has e");
        Open(group: 0);

        InjectChar(KeyEventType.KeyDown, 'e');
        var down = WaitHeld(held => held.Contains(e));
        InjectRepeat('E', KeyModifiers.Shift);
        InjectChar(KeyEventType.KeyUp, 'e');
        var up = WaitHeld(held => !held.Contains(e));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(down, Does.Contain(e), "pre-condition: down");
            Assert.That(up, Has.No.Member(e));
            Assert.That(_output!.Remaps, Is.Zero, "pre-condition: E went on e's key");
        }
    }

    [Test]
    public void ARepeatReResolvedToATempBoundCapital_IsReleasedByTheDownsKeyUp()
    {
        // ж and a second level, so the server fills in no Ж: ж goes on the first keycode and Ж is temp-bound
        Map(_first, XorgKeysym.Of('ж'), Other);
        Map(_second, Other);
        Open(group: 0);

        InjectChar(KeyEventType.KeyDown, 'ж');
        var down = WaitHeld(held => held.Contains(_first));
        InjectRepeat('Ж', KeyModifiers.Shift);
        var spare = KeycodesOf(XorgKeysym.Of('Ж'));
        var repeated = WaitHeld(held => spare.Count == 1 && held.Contains(spare[0]));
        InjectChar(KeyEventType.KeyUp, 'ж');
        var up = WaitHeld(held => !held.Contains(_first) && !held.Contains(spare.FirstOrDefault()));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(down, Does.Contain(_first), "pre-condition: down on ж's key");
            Assert.That(spare, Has.Count.EqualTo(1).And.No.Member(_first), "pre-condition: Ж temp-bound on a spare");
            Assert.That(repeated, Does.Contain(spare.FirstOrDefault()), "pre-condition: the repeat went on the spare");
            Assert.That(up, Has.No.Member(_first).And.No.Member(spare.FirstOrDefault()));
        }
    }

    [Test]
    public void ARepeatReResolvedUnderAnotherGroup_IsReleasedByTheDownsKeyUp()
    {
        // one key typing д in group 0 and ф in group 1
        Map(_first, XorgKeysym.Of('д'), 0, XorgKeysym.Of('ф'), 0);
        Map(_second, Other);
        Open(group: 0);

        InjectChar(KeyEventType.KeyDown, 'д');
        var down = WaitHeld(held => held.Contains(_first));
        LockGroup(1);
        InjectRepeat('ф', KeyModifiers.None);
        InjectChar(KeyEventType.KeyUp, 'д');
        var up = WaitHeld(held => !held.Contains(_first));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(down, Does.Contain(_first), "pre-condition: down");
            Assert.That(up, Has.No.Member(_first));
            Assert.That(_output!.Remaps, Is.Zero, "pre-condition: ф went on the same key");
        }
    }

    // us,de under de: <RALT> is [Alt_R],[ISO_Level3_Shift], and a spare keycode is in no modifier map, so Alt_R bound
    // there would modify nothing
    [Test]
    public unsafe void AModifierOnlyAnotherGroupHas_GoesOnItsOwnKey()
    {
        var altR = NativeMethods.XKeysymToKeycode(_display, XorgVirtualKey.Alt_R);
        Assume.That(altR, Is.Not.Zero, "pre-condition: the keymap has Alt_R");
        var map = NativeMethods.XGetKeyboardMapping(_display, altR, 1, out _);
        Assert.That(map, Is.Not.EqualTo(nint.Zero));
        _altRRow = new ReadOnlySpan<ulong>((ulong*)map, _perKeycode).ToArray();
        _ = NativeMethods.XFree(map);
        _altR = altR;
        Map(altR, XorgVirtualKey.Alt_R, 0, XorgVirtualKey.ISO_Level3_Shift, 0);
        Map(_second, Other);
        Open(group: 1);

        _output!.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.Alt, null, SpecialKey.Alt_R));
        var down = WaitHeld(held => held.Contains(altR));
        _output.InjectKey(new KeyEventMessage(KeyEventType.KeyUp, KeyModifiers.None, null, SpecialKey.Alt_R));
        var up = WaitHeld(held => !held.Contains(altR));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(down, Does.Contain(altR));
            Assert.That(up, Has.No.Member(altR));
            Assert.That(_output.Remaps, Is.Zero, "not temp-bound");
        }
    }

    // a key with one group types it under every group, as the server wraps the group into the key's range
    [Test]
    public void OneGroupKeysUnderTheSecondGroup_GoOnTheirOwnKeycodes()
    {
        var keys = new[] { SpecialKey.Shift_L, SpecialKey.Control_L, SpecialKey.Escape };
        var keycodes = keys.Select(k => NativeMethods.XKeysymToKeycode(_display, XorgKeysym.Injected(k))).ToList();
        Assume.That(keycodes, Is.All.Not.Zero, "pre-condition: the keymap has them");
        // any key with a second group, so the server has one to lock
        Map(_second, Other, 0, Other, 0);
        Open(group: 1);

        foreach (var key in keys) Inject(KeyEventType.KeyDown, key);
        var down = WaitHeld(held => keycodes.All(held.Contains));
        foreach (var key in keys) Inject(KeyEventType.KeyUp, key);
        _ = WaitHeld(held => !keycodes.Any(held.Contains));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(down, Is.SupersetOf(keycodes));
            Assert.That(_output!.Remaps, Is.Zero);
        }
    }

    [Test]
    public void ASpecialKeyOnlyAnotherGroupHas_IsTempBound()
    {
        Map(_first, Other);
        Map(_second, Other, 0, _keysym, 0);
        Open(group: 0);

        Inject(KeyEventType.KeyDown);
        var spare = KeycodesOf(_keysym);
        var down = WaitHeld(held => spare.Count == 1 && held.Contains(spare[0]));
        Inject(KeyEventType.KeyUp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(spare, Has.Count.EqualTo(1).And.No.Member(_second), "bound on a spare keycode");
            Assert.That(down, Does.Contain(spare.FirstOrDefault()));
            Assert.That(down, Has.No.Member(_second), "group 1's key types something else under group 0");
            Assert.That(_output!.Remaps, Is.EqualTo(1));
        }
    }

    [Test]
    public void AfterAnotherClientMovesTheKeysym_TheNextKeyGoesOnItsNewKeycode()
    {
        Map(_first, _keysym);
        Map(_second, Other);
        Open(group: 0);
        Inject(KeyEventType.KeyDown);
        var before = WaitHeld(held => held.Contains(_first));
        Inject(KeyEventType.KeyUp);
        _ = WaitHeld(held => !held.Contains(_first));

        Map(_first);
        Map(_second, _keysym);
        _ = NativeMethods.XSync(_display, false);
        Inject(KeyEventType.KeyDown);
        var after = WaitHeld(held => held.Contains(_second));
        Inject(KeyEventType.KeyUp);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(before, Does.Contain(_first), "pre-condition: the old keycode, with Xlib's copy of the keymap loaded");
            Assert.That(after, Does.Contain(_second));
            Assert.That(after, Has.No.Member(_first));
            Assert.That(_output!.Remaps, Is.Zero, "the moved key typed it, nothing temp-bound");
        }
    }

    private void Open(int group)
    {
        LockGroup(group);
        _output = new XorgOutputHandler(NullLogger<XorgOutputHandler>.Instance);
    }

    private void LockGroup(int group)
    {
        Assert.That(XkbLockGroup(_display, NativeMethods.XkbUseCoreKbd, (uint)group), Is.True);
        _ = NativeMethods.XSync(_display, false);
        var stateRead = NativeMethods.XkbGetState(_display, NativeMethods.XkbUseCoreKbd, out var state);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stateRead, Is.Zero);
            Assert.That((int)state.Group, Is.EqualTo(group), "pre-condition: the group locked");
        }
    }

    private void Inject(KeyEventType type) => Inject(type, _key);

    private void Inject(KeyEventType type, SpecialKey key) => _output!.InjectKey(new KeyEventMessage(type, KeyModifiers.None, null, key));

    private void InjectChar(KeyEventType type, char ch) => _output!.InjectKey(new KeyEventMessage(type, KeyModifiers.None, ch, null));

    private void InjectRepeat(char ch, KeyModifiers modifiers) =>
        _output!.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, modifiers, ch, null, IsRepeat: true));

    // for a state with nothing to wait for: the handler only flushes, so give its keys time to reach the server
    private List<uint> HeldAfterHandler()
    {
        Thread.Sleep(200);
        return XorgDisplayHelper.HeldKeycodes(_display);
    }

    private void Tap()
    {
        Inject(KeyEventType.KeyDown);
        Inject(KeyEventType.KeyUp);
    }

    // the core row is group 0's two levels then group 1's; the server makes each non-empty pair a group
    private void Map(uint keycode, params ulong[] syms)
    {
        var row = new ulong[_perKeycode];
        Array.Copy(syms, row, Math.Min(syms.Length, row.Length));
        _ = NativeMethods.XChangeKeyboardMapping(_display, (int)keycode, _perKeycode, row, 1);
        _ = NativeMethods.XSync(_display, false);
    }

    // the handler only flushes, so its keys may reach the server after this connection asks: wait for them, bounded
    private List<uint> WaitHeld(Func<List<uint>, bool> settled)
    {
        var held = XorgDisplayHelper.HeldKeycodes(_display);
        for (var deadline = Environment.TickCount64 + 2000; !settled(held) && Environment.TickCount64 < deadline;)
        {
            Thread.Sleep(10);
            held = XorgDisplayHelper.HeldKeycodes(_display);
        }
        return held;
    }

    // the keycodes whose base level holds the keysym, as the server has it now
    private unsafe List<uint> KeycodesOf(ulong keysym)
    {
        _ = NativeMethods.XDisplayKeycodes(_display, out var min, out var max);
        var map = NativeMethods.XGetKeyboardMapping(_display, (uint)min, max - min + 1, out var perKeycode);
        Assert.That(map, Is.Not.EqualTo(nint.Zero));
        var keysyms = (ulong*)map;
        var found = new List<uint>();
        for (var i = 0; i <= max - min; i++)
            if (keysyms[i * perKeycode] == keysym) found.Add((uint)(min + i));
        _ = NativeMethods.XFree(map);
        return found;
    }

    // <X11/XKBlib.h>; NativeMethods has no binding for it
    [LibraryImport("libX11.so.6")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool XkbLockGroup(nint display, uint deviceSpec, uint group);
}
