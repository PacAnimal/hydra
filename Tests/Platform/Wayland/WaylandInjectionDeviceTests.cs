using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Platform.Linux;
using Hydra.Platform.Linux.Wayland;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Tests.Setup;

namespace Tests.Platform.Wayland;

// which backend a Wayland slave injects through. no portal or compositor, so these run everywhere.
[TestFixture]
public class InjectionBackendTests
{
    [Test]
    public void RemoteDesktop_IsEis_WithoutAskingTheCompositor()
    {
        var asked = false;
        var backend = InjectionBackends.Choose(true, () => asked = true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(backend, Is.EqualTo(InjectionBackend.Eis));
            Assert.That(asked, Is.False, "no Wayland connection is opened where EIS serves");
        }
    }

    [Test]
    public void NoRemoteDesktop_WithVirtualInput_IsVirtualInput() =>
        Assert.That(InjectionBackends.Choose(false, () => true), Is.EqualTo(InjectionBackend.VirtualInput));

    [Test]
    public void Neither_IsNone() =>
        Assert.That(InjectionBackends.Choose(false, () => false), Is.EqualTo(InjectionBackend.None));
}

// the modifiers the wlroots path sends with its keys, as xkbcommon derives them from the keys held on the us keymap
// (pc105 with lv3:ralt_switch for AltGr). needs libxkbcommon, so it self-skips off Linux.
[TestFixture]
[Category("Linux")]
public class XkbModifierStateTests
{
    private const uint LeftShift = 42, RightShift = 54, LeftCtrl = 29, LeftAlt = 56, RightAlt = 100, LeftMeta = 125, KeyA = 30;

    private XkbModifierState _state = null!;

    [OneTimeSetUp]
    public void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Requires Linux with libxkbcommon (run ./run-tests.sh linux)");
    }

    [SetUp]
    public void Create() =>
        _state = new XkbModifierState(XkbKeymapLookup.KeymapTextFromNames(new LinuxInputConfig.XkbNames("us", "pc105", null, "lv3:ralt_switch")));

    [TearDown]
    public void Dispose() => _state.Dispose();

    private uint Depressed => _state.Current.Depressed;

    [Test]
    public void NothingHeld_IsNoModifiers() => Assert.That(_state.Current, Is.EqualTo(new XkbModifiers(0, 0, 0, 0)));

    [TestCase(LeftShift, "Shift")]
    [TestCase(RightShift, "Shift")]
    [TestCase(LeftCtrl, "Control")]
    [TestCase(LeftAlt, "Mod1")]
    [TestCase(LeftMeta, "Mod4")]
    [TestCase(RightAlt, "Mod5")]
    public void HeldModifier_IsItsMask(uint key, string modifier)
    {
        var changed = _state.Update(key, true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(changed, Is.True);
            Assert.That(_state.Mask(modifier), Is.Not.Zero, "pre-condition: the keymap has the modifier");
            Assert.That(Depressed, Is.EqualTo(_state.Mask(modifier)));
        }
    }

    [Test]
    public void ShiftAndAltGr_AreBothHeld()
    {
        _state.Update(LeftShift, true);
        _state.Update(RightAlt, true);
        Assert.That(Depressed, Is.EqualTo(_state.Mask("Shift") | _state.Mask("Mod5")));
    }

    [Test]
    public void CtrlAndSuper_AreBothHeld()
    {
        _state.Update(LeftCtrl, true);
        _state.Update(LeftMeta, true);
        Assert.That(Depressed, Is.EqualTo(_state.Mask("Control") | _state.Mask("Mod4")));
    }

    [Test]
    public void BothShifts_StayShiftedUntilTheLastLetsGo()
    {
        _state.Update(LeftShift, true);
        _state.Update(RightShift, true);
        _state.Update(LeftShift, false);
        Assert.That(Depressed, Is.EqualTo(_state.Mask("Shift")));
        _state.Update(RightShift, false);
        Assert.That(Depressed, Is.Zero);
    }

    [Test]
    public void Letter_ChangesNoModifiers()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_state.Update(KeyA, true), Is.False);
            Assert.That(Depressed, Is.Zero);
        }
    }
}

// the handler's emission through an injection device: the logic both backends share, so each test runs once per
// backend and expects one sequence of both. Translating it into EIS or wlroots requests is each device's own business.
// keys need libxkbcommon for the keymap, so those self-skip off Linux.
[TestFixture]
public class WaylandOutputHandlerEmissionTests
{
    private static readonly ScreenBounds[] Screens = [new(0, 0, 1920, 1080), new(1920, 0, 1280, 1024)];

    private readonly FakeTimeProvider _time = new();
    private FakeWaylandSession _session = null!;
    private readonly List<WaylandOutputHandler> _handlers = [];

    [SetUp]
    public void Create() => _session = new FakeWaylandSession();

    [TearDown]
    public void Dispose()
    {
        foreach (var handler in _handlers) handler.Dispose();
        _handlers.Clear();
        _session.Dispose();
    }

    private static string? KeymapFor(string layout) =>
        OperatingSystem.IsLinux() ? XkbKeymapLookup.KeymapTextFromNames(new LinuxInputConfig.XkbNames(layout, "pc105", null, null)) : null;

    private static readonly string[] Backends = ["EIS", "wlroots virtual input"];

    private sealed record Running(WaylandOutputHandler Handler, RecordingDevice Device);

    private async Task<Running> Started(string backend, bool keymap = false, string layout = "us", uint? bindKey = null, uint group = 0)
    {
        var device = new RecordingDevice(backend, keymap ? KeymapFor(layout) : null, Screens, locked: 0, bindKey, group);
        var handler = await Handler(device);
        return new Running(handler, device);
    }

    private async Task<WaylandOutputHandler> Handler(IInjectionDevice device, CapturingLog<WaylandOutputHandler>? log = null)
    {
        var handler = new WaylandOutputHandler(_session, log ?? new CapturingLog<WaylandOutputHandler>(), _time);
        _handlers.Add(handler);
        await _session.Asked(_handlers.Count);
        _session.Succeed(_handlers.Count, device);
        await handler.Acquiring;
        Assert.That(handler.HasDevice, Is.True, "pre-condition: the handler took the device");
        return handler;
    }

    private static void Type(WaylandOutputHandler handler, string text)
    {
        foreach (var ch in text)
        {
            handler.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, ch, null));
            handler.InjectKey(new KeyEventMessage(KeyEventType.KeyUp, KeyModifiers.None, ch, null));
        }
    }

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("Requires Linux with libxkbcommon (run ./run-tests.sh linux)");
    }

    [TestCaseSource(nameof(Backends))]
    public async Task Move_IsAnAbsoluteMoveInsideTheScreens(string backend)
    {
        var (handler, device) = await Started(backend);
        handler.MoveMouse(100, 200);
        handler.MoveMouse(5000, 2000);
        // the device's first move goes through a neighbouring pixel (see WaylandOutputHandler)
        Assert.That(device.Sent, Is.EqualTo(["start", "abs 101,200", "frame", "abs 100,200", "frame", "abs 3199,1023", "frame"]));
    }

    [TestCaseSource(nameof(Backends))]
    public async Task RelativeMove_IsARelativeMove(string backend)
    {
        var (handler, device) = await Started(backend);
        handler.MoveMouseRelative(3, -4);
        Assert.That(device.Sent, Is.EqualTo(["start", "rel 3,-4", "frame"]));
    }

    [TestCaseSource(nameof(Backends))]
    public async Task Click_IsItsEvdevButtonDownThenUp(string backend)
    {
        var (handler, device) = await Started(backend);
        handler.InjectMouseButton(new MouseButtonMessage(MouseButton.Left, true));
        handler.InjectMouseButton(new MouseButtonMessage(MouseButton.Left, true));
        handler.InjectMouseButton(new MouseButtonMessage(MouseButton.Left, false));
        Assert.That(device.Sent, Is.EqualTo(["start", "button 272 down", "frame", "button 272 up", "frame"]), "a second down is dropped");
    }

    [TestCaseSource(nameof(Backends))]
    public async Task ScrollUp_IsNegativeY(string backend)
    {
        var (handler, device) = await Started(backend);
        handler.InjectMouseScroll(new MouseScrollMessage(0, MouseScrollEvent.Notch));
        Assert.That(device.Sent, Is.EqualTo(["start", "scroll 0,-120", "frame"]));
    }

    [TestCaseSource(nameof(Backends))]
    public async Task EndVisit_LetsGoOfAHeldButtonAndStops(string backend)
    {
        var (handler, device) = await Started(backend);
        handler.InjectMouseButton(new MouseButtonMessage(MouseButton.Right, true));
        handler.EndVisit();
        Assert.That(device.Sent, Is.EqualTo(["start", "button 273 down", "frame", "button 273 up", "frame", "stop"]));
    }

    [TestCaseSource(nameof(Backends))]
    public async Task ShiftedCharacter_PressesShiftAroundItsKey(string backend)
    {
        RequireLinux();
        var (handler, device) = await Started(backend, keymap: true);
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, '@', null));
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyUp, KeyModifiers.None, '@', null));
        Assert.That(device.Sent, Is.EqualTo(
            ["start", "key 42 down", "frame", "key 3 down", "frame", "key 42 up", "frame", "key 3 up", "frame"]));
    }

    [TestCaseSource(nameof(Backends))]
    public async Task HeldShift_IsKeptForAShiftedCharacter(string backend)
    {
        RequireLinux();
        var (handler, device) = await Started(backend, keymap: true);
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.Shift, null, SpecialKey.Shift_L));
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.Shift, 'A', null));
        Assert.That(device.Sent, Is.EqualTo(["start", "key 42 down", "frame", "key 30 down", "frame"]));
    }

    [TestCaseSource(nameof(Backends))]
    public async Task CapsLockOn_IsTappedOnToMatchTheMaster(string backend)
    {
        RequireLinux();
        var (handler, device) = await Started(backend, keymap: true);
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.CapsLock, 'A', null));
        Assert.That(device.Sent, Is.EqualTo(["start", "key 58 down", "frame", "key 58 up", "frame", "key 30 down", "frame"]));
    }

    [TestCaseSource(nameof(Backends))]
    public async Task Eacute_OnALayoutWithDeadKeys_IsTheDeadKeyThenE(string backend)
    {
        // de: dead_acute on KEY_EQUAL (13), e on KEY_E (18)
        RequireLinux();
        var (handler, device) = await Started(backend, keymap: true, layout: "de");
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, 'é', null));
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyUp, KeyModifiers.None, 'é', null));
        Assert.That(device.Sent, Is.EqualTo(
            ["start", "key 13 down", "frame", "key 13 up", "frame", "key 18 down", "frame", "key 18 up", "frame"]));
    }

    [Test]
    public async Task ACharacterNeitherTypedNorComposed_OnEis_IsDropped()
    {
        RequireLinux();
        var (handler, device) = await Started("EIS", keymap: true, layout: "de");
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, '☃', null));
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyUp, KeyModifiers.None, '☃', null));
        Assert.That(device.Sent, Is.Empty);
    }

    [Test]
    public async Task ACharacterNeitherTypedNorComposed_OnADeviceThatBindsIt_IsTheBoundKey()
    {
        RequireLinux();
        var (handler, device) = await Started("wlroots virtual input", keymap: true, layout: "de", bindKey: 222);
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.Shift, '☃', null));
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyUp, KeyModifiers.Shift, '☃', null));
        Assert.That(device.Sent, Is.EqualTo(["bind 0x1002603", "start", "key 222 down", "frame", "key 222 up", "frame"]));
    }

    [TestCaseSource(nameof(Backends))]
    public async Task ALostDevice_IsLetGoOf(string backend)
    {
        var (handler, device) = await Started(backend);
        device.Lose();
        handler.MoveMouse(10, 10);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.HasDevice, Is.False);
            Assert.That(device.Sent, Is.Empty);
        }
    }

    // us,ru: д on KEY_L (38) in group 1, l on it in group 0
    private const uint KeyL = 38;

    [Test]
    public async Task UsRu_OnEisUnderGroup0_ACyrillicLetterIsRefusedWithALog()
    {
        RequireLinux();
        var log = new CapturingLog<WaylandOutputHandler>();
        var device = new RecordingDevice("EIS", KeymapFor("us,ru"), Screens, locked: 0, group: 0);
        var handler = await Handler(device, log);
        Type(handler, "дд");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(device.Sent, Is.Empty, "under group 0 its key types l");
            Assert.That(log.Entries.Count(e => e.Message.Contains("is on a layout the compositor has not selected")), Is.EqualTo(1));
        }
    }

    [Test]
    public async Task UsRu_OnEisUnderGroup0_ALatinLetterIsTyped()
    {
        RequireLinux();
        var (handler, device) = await Started("EIS", keymap: true, layout: "us,ru", group: 0);
        Type(handler, "l");
        Assert.That(device.Sent, Is.EqualTo(["start", $"key {KeyL} down", "frame", $"key {KeyL} up", "frame"]));
    }

    [Test]
    public async Task UsRu_OnEisUnderGroup1_ACyrillicLetterIsTypedOnItsKey()
    {
        RequireLinux();
        var (handler, device) = await Started("EIS", keymap: true, layout: "us,ru", group: 1);
        Type(handler, "д");
        Assert.That(device.Sent, Is.EqualTo(["start", $"key {KeyL} down", "frame", $"key {KeyL} up", "frame"]));
    }

    [Test]
    public async Task Ru_OnEis_ACyrillicLetterIsTypedOnItsKey()
    {
        RequireLinux();
        var (handler, device) = await Started("EIS", keymap: true, layout: "ru");
        Type(handler, "д");
        Assert.That(device.Sent, Is.EqualTo(["start", $"key {KeyL} down", "frame", $"key {KeyL} up", "frame"]));
    }

    [Test]
    public async Task UsRu_OnVirtualInput_EachLetterGoesInUnderItsGroupAndTheStartingOneComesBack()
    {
        RequireLinux();
        var input = new RecordingVirtualInput();
        using var device = new VirtualInjectionDevice(XkbKeymap.FromNames(new LinuxInputConfig.XkbNames("us,ru", "pc105", null, null)), () => input);
        var handler = await Handler(device);
        Type(handler, "Привет, world");

        // each key down but Shift's, as the key and the group the last modifiers sent before it selected
        var downs = new List<string>();
        var group = "0";
        foreach (var sent in input.Sent)
        {
            if (sent.StartsWith("mods ", StringComparison.Ordinal)) group = sent.Split(',')[^1];
            else if (sent.StartsWith("key ", StringComparison.Ordinal) && sent.EndsWith(" down", StringComparison.Ordinal) && sent != "key 42 down")
                downs.Add($"{sent.Split(' ')[1]}@{group}");
        }
        using (Assert.EnterMultipleScope())
        {
            // П р и в е т on G H B D T N, then ru's , (Shift+slash) and the space stay on group 1, w o r l d on us
            Assert.That(downs, Is.EqualTo(["34@1", "35@1", "48@1", "32@1", "20@1", "49@1",
                "53@1", "57@1", "17@0", "24@0", "19@0", "38@0", "32@0"]));
            Assert.That(group, Is.EqualTo("0"), "the starting group is back");
            Assert.That(device.Group, Is.Zero);
        }
    }

    private const uint KeyC = 46, LeftShift = 42;

    private static VirtualInjectionDevice UsRuVirtual(RecordingVirtualInput input) =>
        new(XkbKeymap.FromNames(new LinuxInputConfig.XkbNames("us,ru", "pc105", null, null)), () => input);

    // each key sent, as "38 down@1": the key, its direction and the group the last modifiers before it carried
    private static List<string> KeysWithGroup(RecordingVirtualInput input)
    {
        var keys = new List<string>();
        var group = "0";
        foreach (var sent in input.Sent)
        {
            if (sent.StartsWith("mods ", StringComparison.Ordinal)) group = sent.Split(',')[^1];
            else if (sent.StartsWith("key ", StringComparison.Ordinal)) keys.Add($"{sent[4..]}@{group}");
        }
        return keys;
    }

    private static string? LastGroup(RecordingVirtualInput input) =>
        input.Sent.LastOrDefault(s => s.StartsWith("mods ", StringComparison.Ordinal))?.Split(',')[^1];

    [Test]
    public async Task UsRu_OnEisUnderGroup1_CtrlC_PressesKeyCWithCtrl()
    {
        // the master's Ctrl comes as its own key; under ru KEY_C types с, which apps match as Ctrl+C
        RequireLinux();
        var (handler, device) = await Started("EIS", keymap: true, layout: "us,ru", group: 1);
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.Control, 'c', null));
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyUp, KeyModifiers.Control, 'c', null));
        Assert.That(device.Sent, Is.EqualTo(["start", $"key {KeyC} down", "frame", $"key {KeyC} up", "frame"]));
    }

    [Test]
    public async Task UsRu_OnEisUnderGroup1_ALatinLetterIsRefused()
    {
        // under group 1 KEY_L types д
        RequireLinux();
        var (handler, device) = await Started("EIS", keymap: true, layout: "us,ru", group: 1);
        Type(handler, "l");
        Assert.That(device.Sent, Is.Empty);
    }

    [Test]
    public async Task Ru_OnEis_ALatinLetterTypesNothing()
    {
        RequireLinux();
        var (handler, device) = await Started("EIS", keymap: true, layout: "ru");
        Assert.DoesNotThrow(() => Type(handler, "l"));
        Assert.That(device.Sent, Is.Empty);
    }

    [Test]
    public async Task UsRu_OnVirtualInput_CtrlC_SelectsNoGroup_AndCtrlCyrillicGoesInUnderGroup1()
    {
        RequireLinux();
        var input = new RecordingVirtualInput();
        using var device = UsRuVirtual(input);
        var handler = await Handler(device);
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.Control, 'c', null));
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyUp, KeyModifiers.Control, 'c', null));
        var afterC = input.Sent;
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.Control, 'д', null));
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyUp, KeyModifiers.Control, 'д', null));
        var afterД = LastGroup(input);
        handler.EndVisit();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterC.Where(s => s.StartsWith("mods ", StringComparison.Ordinal)), Is.Empty, "c is on the group already in effect");
            Assert.That(KeysWithGroup(input), Is.EqualTo([$"{KeyC} down@0", $"{KeyC} up@0", $"{KeyL} down@1", $"{KeyL} up@1"]));
            Assert.That(afterД, Is.EqualTo("1"), "the selection stays until a key needs another");
            Assert.That(LastGroup(input), Is.EqualTo("0"), "the visit's end gives the starting group back");
        }
    }

    [Test]
    public async Task UsRu_OnVirtualInput_ReleaseAllWhileACyrillicKeyIsHeld_GivesGroup0Back()
    {
        RequireLinux();
        var input = new RecordingVirtualInput();
        using var device = UsRuVirtual(input);
        var handler = await Handler(device);
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, 'д', null));
        handler.EndVisit();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(KeysWithGroup(input), Is.EqualTo([$"{KeyL} down@1", $"{KeyL} up@1"]));
            Assert.That(LastGroup(input), Is.EqualTo("0"));
        }
    }

    [Test]
    public async Task UsRu_OnVirtualInput_ACyrillicKeyRepeat_StaysOnGroup1()
    {
        RequireLinux();
        var input = new RecordingVirtualInput();
        using var device = UsRuVirtual(input);
        var handler = await Handler(device);
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, 'д', null));
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, 'д', null));
        var beforeUp = LastGroup(input);
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyUp, KeyModifiers.None, 'д', null));
        var afterUp = LastGroup(input);
        handler.EndVisit();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(KeysWithGroup(input), Is.EqualTo([$"{KeyL} down@1", $"{KeyL} up@1", $"{KeyL} down@1", $"{KeyL} up@1"]));
            Assert.That(beforeUp, Is.EqualTo("1"), "the repeat stays on group 1");
            Assert.That(afterUp, Is.EqualTo("1"), "the up keeps the selection");
            Assert.That(LastGroup(input), Is.EqualTo("0"), "the visit's end gives group 0 back");
            Assert.That(input.Sent.Count(s => s.StartsWith("mods ", StringComparison.Ordinal)), Is.EqualTo(2), "one switch there, one back");
        }
    }

    [Test]
    public async Task UsRu_OnVirtualInput_ShiftedCyrillicCapital()
    {
        RequireLinux();
        var input = new RecordingVirtualInput();
        using var device = UsRuVirtual(input);
        var handler = await Handler(device);
        Type(handler, "Д");
        handler.EndVisit();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(KeysWithGroup(input), Is.EqualTo(
                [$"{LeftShift} down@1", $"{KeyL} down@1", $"{LeftShift} up@1", $"{KeyL} up@1"]));
            Assert.That(LastGroup(input), Is.EqualTo("0"));
        }
    }

    private const uint KeyComma = 51, KeyDot = 52, KeySlash = 53, KeySemicolon = 39, Key1 = 2, KeySpace = 57, KeyW = 17, KeyE = 18;
    private const uint KeyEqual = 13, KeyCapsLock = 58;

    private static VirtualInjectionDevice VirtualOn(RecordingVirtualInput input, string layout) =>
        new(XkbKeymap.FromNames(new LinuxInputConfig.XkbNames(layout, "pc105", null, null)), () => input);

    private static void Shortcut(WaylandOutputHandler handler, char ch)
    {
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.Control, ch, null));
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyUp, KeyModifiers.Control, ch, null));
    }

    private static int GroupSwitches(RecordingVirtualInput input) => input.Sent.Count(s => s.StartsWith("mods ", StringComparison.Ordinal));

    private static string[] Taps(params uint[] keys) => [.. keys.SelectMany(k => new[] { $"key {k} down", "frame", $"key {k} up", "frame" })];

    [Test]
    public async Task UsRu_OnEisUnderGroup1_PunctuationShortcuts_AreTheUsKeysWithNoShift()
    {
        // ru has , on Shift+slash, ; on Shift+4 and / on its own key (Shift+backslash, or LSGT on some xkeyboard-config
        // versions); a shortcut takes the Latin group's key
        RequireLinux();
        var (handler, device) = await Started("EIS", keymap: true, layout: "us,ru", group: 1);
        foreach (var ch in ",./;") Shortcut(handler, ch);
        Assert.That(device.Sent, Is.EqualTo((string[])["start", .. Taps(KeyComma, KeyDot, KeySlash, KeySemicolon)]));
    }

    [Test]
    public async Task RuUs_OnVirtualInput_PunctuationShortcuts_GoInUnderTheLatinGroupWithNoShift()
    {
        RequireLinux();
        var input = new RecordingVirtualInput();
        using var device = VirtualOn(input, "ru,us");
        var handler = await Handler(device);
        foreach (var ch in ",./;") Shortcut(handler, ch);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(KeysWithGroup(input), Is.EqualTo(new[] { KeyComma, KeyDot, KeySlash, KeySemicolon }
                .SelectMany(k => new[] { $"{k} down@1", $"{k} up@1" })));
            Assert.That(GroupSwitches(input), Is.EqualTo(1), "the Latin group is selected once");
        }
    }

    [Test]
    public async Task RuUs_OnVirtualInput_CtrlC_SelectsTheLatinGroup()
    {
        RequireLinux();
        var input = new RecordingVirtualInput();
        using var device = VirtualOn(input, "ru,us");
        var handler = await Handler(device);
        Shortcut(handler, 'c');
        Assert.That(KeysWithGroup(input), Is.EqualTo([$"{KeyC} down@1", $"{KeyC} up@1"]));
    }

    [Test]
    public async Task RuUs_OnVirtualInput_CyrillicTypesUnselected_LatinSelectsGroup1_AndTheVisitsEndGivesGroup0Back()
    {
        RequireLinux();
        var input = new RecordingVirtualInput();
        using var device = VirtualOn(input, "ru,us");
        var handler = await Handler(device);
        Type(handler, "дд");
        var afterCyrillic = GroupSwitches(input);
        Type(handler, "ll");
        handler.EndVisit();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterCyrillic, Is.Zero, "ru is the device's own group");
            Assert.That(KeysWithGroup(input), Is.EqualTo(
                [$"{KeyL} down@0", $"{KeyL} up@0", $"{KeyL} down@0", $"{KeyL} up@0", $"{KeyL} down@1", $"{KeyL} up@1", $"{KeyL} down@1", $"{KeyL} up@1"]));
            Assert.That(LastGroup(input), Is.EqualTo("0"));
            Assert.That(device.Group, Is.Zero);
        }
    }

    [Test]
    public async Task UsRu_OnEisUnderGroup1_CapsLock_ShiftsCyrillicAsItShiftsLatin()
    {
        // the master's CapsLock is tapped on first; then д needs Shift to undo it and Д none
        RequireLinux();
        var (handler, device) = await Started("EIS", keymap: true, layout: "us,ru", group: 1);
        foreach (var ch in "дД")
        {
            handler.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.CapsLock, ch, null));
            handler.InjectKey(new KeyEventMessage(KeyEventType.KeyUp, KeyModifiers.CapsLock, ch, null));
        }
        Assert.That(device.Sent, Is.EqualTo((string[])["start", $"key {KeyCapsLock} down", "frame", $"key {KeyCapsLock} up", "frame",
            $"key {LeftShift} down", "frame", $"key {KeyL} down", "frame", $"key {LeftShift} up", "frame", $"key {KeyL} up", "frame",
            .. Taps(KeyL)]));
    }

    [Test]
    public async Task UsRu_OnVirtualInput_CapsLock_ShiftsCyrillicAsItShiftsLatin()
    {
        RequireLinux();
        var input = new RecordingVirtualInput();
        using var device = UsRuVirtual(input);
        var handler = await Handler(device);
        foreach (var ch in "дД")
        {
            handler.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.CapsLock, ch, null));
            handler.InjectKey(new KeyEventMessage(KeyEventType.KeyUp, KeyModifiers.CapsLock, ch, null));
        }
        Assert.That(KeysWithGroup(input), Is.EqualTo([$"{KeyCapsLock} down@0", $"{KeyCapsLock} up@0",
            $"{LeftShift} down@1", $"{KeyL} down@1", $"{LeftShift} up@1", $"{KeyL} up@1", $"{KeyL} down@1", $"{KeyL} up@1"]));
    }

    [Test]
    public async Task UsRu_OnVirtualInput_OverlappingCyrillicThenLatin_EachKeyGoesInUnderItsGroup()
    {
        // д and l are both KEY_L: the second down selects its own group before it, the ups follow the last selection
        RequireLinux();
        var input = new RecordingVirtualInput();
        using var device = UsRuVirtual(input);
        var handler = await Handler(device);
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, 'д', null));
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, 'l', null));
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyUp, KeyModifiers.None, 'д', null));
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyUp, KeyModifiers.None, 'l', null));
        handler.EndVisit();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(KeysWithGroup(input), Is.EqualTo([$"{KeyL} down@1", $"{KeyL} down@0", $"{KeyL} up@0", $"{KeyL} up@0"]));
            Assert.That(LastGroup(input), Is.EqualTo("0"));
        }
    }

    [Test]
    public async Task UsRu_OnVirtualInput_OverlappingLatinThenCyrillic_EachKeyGoesInUnderItsGroup()
    {
        RequireLinux();
        var input = new RecordingVirtualInput();
        using var device = UsRuVirtual(input);
        var handler = await Handler(device);
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, 'l', null));
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyDown, KeyModifiers.None, 'д', null));
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyUp, KeyModifiers.None, 'l', null));
        handler.InjectKey(new KeyEventMessage(KeyEventType.KeyUp, KeyModifiers.None, 'д', null));
        var beforeEnd = LastGroup(input);
        handler.EndVisit();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(KeysWithGroup(input), Is.EqualTo([$"{KeyL} down@0", $"{KeyL} down@1", $"{KeyL} up@1", $"{KeyL} up@1"]));
            Assert.That(beforeEnd, Is.EqualTo("1"));
            Assert.That(LastGroup(input), Is.EqualTo("0"), "the visit's end gives group 0 back");
        }
    }

    [Test]
    public async Task UsRu_OnEisUnderGroup1_Punctuation_IsRusOwnKeys()
    {
        // ru: <AB10> { period, comma }; digits and space are as on us
        RequireLinux();
        var (handler, device) = await Started("EIS", keymap: true, layout: "us,ru", group: 1);
        Type(handler, ",.1 ");
        Assert.That(device.Sent, Is.EqualTo((string[])["start",
            $"key {LeftShift} down", "frame", $"key {KeySlash} down", "frame", $"key {LeftShift} up", "frame", $"key {KeySlash} up", "frame",
            .. Taps(KeySlash, Key1, KeySpace)]));
    }

    [Test]
    public async Task UsRu_OnVirtualInput_ANewDeviceAfterALoss_StartsOnItsOwnGroup()
    {
        RequireLinux();
        var input = new RecordingVirtualInput();
        using var device = UsRuVirtual(input);
        var handler = await Handler(device);
        Type(handler, "д");
        Assert.That(LastGroup(input), Is.EqualTo("1"), "pre-condition: group 1 selected");
        input.Lose();

        var next = new RecordingVirtualInput();
        using var replacement = UsRuVirtual(next);
        handler.MoveMouse(1, 1);
        await _session.Asked(2);
        _session.Succeed(2, replacement);
        await handler.Acquiring;
        Type(handler, "l");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(KeysWithGroup(next), Is.EqualTo([$"{KeyL} down@0", $"{KeyL} up@0"]));
            Assert.That(GroupSwitches(next), Is.Zero, "no selection carried over");
        }
    }

    [Test]
    public async Task UsRu_OnVirtualInput_ABoundCharacterUnderGroup1_KeepsTheSelection()
    {
        RequireLinux();
        var input = new RecordingVirtualInput();
        using var device = UsRuVirtual(input);
        using var lookup = new XkbKeymapLookup(device.Keymap!);
        var spare = lookup.SpareKey() ?? throw new AssertionException("pre-condition: us,ru has a spare key");
        var handler = await Handler(device);
        Type(handler, "д☃д");
        var beforeLatin = LastGroup(input);
        Type(handler, "l");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(KeysWithGroup(input), Is.EqualTo([$"{KeyL} down@1", $"{KeyL} up@1", $"{spare} down@1", $"{spare} up@1",
                $"{KeyL} down@1", $"{KeyL} up@1", $"{KeyL} down@0", $"{KeyL} up@0"]));
            Assert.That(beforeLatin, Is.EqualTo("1"), "the bound keymap's modifiers keep group 1");
            Assert.That(input.Keymaps[^1], Is.EqualTo(input.Keymaps[0]), "the compositor's keymap is back");
        }
    }

    [Test]
    public async Task DeRu_OnEisUnderGroup1_AnAccentedLatinCharacter_IsNotComposedWithGroup0sDeadKeys()
    {
        // de composes é from dead_acute (KEY_EQUAL) and e, both on group 0; under ru KEY_EQUAL types = and KEY_E у
        RequireLinux();
        var (handler, device) = await Started("EIS", keymap: true, layout: "de,ru", group: 1);
        Type(handler, "é");
        Assert.That(device.Sent, Is.Empty);
    }

    [Test]
    public async Task DeRu_OnEisUnderGroup0_AnAccentedLatinCharacter_IsComposed()
    {
        RequireLinux();
        var (handler, device) = await Started("EIS", keymap: true, layout: "de,ru", group: 0);
        Type(handler, "é");
        Assert.That(device.Sent, Is.EqualTo((string[])["start", .. Taps(KeyEqual, KeyE)]));
    }

    [Test]
    public async Task UsRu_OnVirtualInput_ACyrillicWord_SwitchesOnce_AndBackOnlyForLatinOrTheVisitsEnd()
    {
        RequireLinux();
        var input = new RecordingVirtualInput();
        using var device = UsRuVirtual(input);
        var handler = await Handler(device);
        Type(handler, "привет");
        var afterWord = GroupSwitches(input);
        Type(handler, "w");
        var afterLatin = GroupSwitches(input);
        Type(handler, "д");
        handler.EndVisit();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterWord, Is.EqualTo(1), "one switch to group 1 for the word");
            Assert.That(afterLatin, Is.EqualTo(2), "back to group 0 for the Latin letter");
            Assert.That(GroupSwitches(input), Is.EqualTo(4), "group 1 for д, group 0 at the visit's end");
            Assert.That(LastGroup(input), Is.EqualTo("0"));
            Assert.That(KeysWithGroup(input).Where(k => k.StartsWith($"{KeyW} ", StringComparison.Ordinal)), Is.All.EndsWith("@0"));
        }
    }

    [Test]
    public async Task UsRu_OnEisUnderGroup0_TheFirstRefusalIsInformation_TheRestDebug()
    {
        RequireLinux();
        var log = new CapturingLog<WaylandOutputHandler>();
        var device = new RecordingDevice("EIS", KeymapFor("us,ru"), Screens, locked: 0, group: 0);
        var handler = await Handler(device, log);
        Type(handler, "дж");
        var refusals = log.Entries.Where(e => e.Message.Contains("is on a layout the compositor has not selected")).ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(refusals.Select(e => e.Level), Is.EqualTo((LogLevel[])[LogLevel.Information, LogLevel.Debug]));
            Assert.That(refusals[0].Message, Does.Contain("switch the slave's layout"));
        }
    }

    private const uint KeyQ = 16, KeyY = 21, KeyV = 47;

    [Test]
    public async Task UsDe_OnEisUnderGroup1_CtrlZ_IsTheDeKeyForZ()
    {
        // de is QWERTZ: z is on AD06, KEY_Y
        RequireLinux();
        var (handler, device) = await Started("EIS", keymap: true, layout: "us,de", group: 1);
        Shortcut(handler, 'z');
        Assert.That(device.Sent, Is.EqualTo((string[])["start", .. Taps(KeyY)]));
    }

    [Test]
    public async Task UsFr_OnEisUnderGroup1_CtrlA_IsTheAzertyKeyForA()
    {
        // fr is AZERTY: a is on AD01, KEY_Q
        RequireLinux();
        var (handler, device) = await Started("EIS", keymap: true, layout: "us,fr", group: 1);
        Shortcut(handler, 'a');
        Assert.That(device.Sent, Is.EqualTo((string[])["start", .. Taps(KeyQ)]));
    }

    [Test]
    public async Task UsDe_OnVirtualInput_UnderTheSelectedDeGroup_CtrlZ_StaysOnIt()
    {
        // only de types ö (AC10, KEY_SEMICOLON), which selects group 1; Ctrl+z then takes de's z key there
        RequireLinux();
        var input = new RecordingVirtualInput();
        using var device = VirtualOn(input, "us,de");
        var handler = await Handler(device);
        Type(handler, "ö");
        Shortcut(handler, 'z');
        using (Assert.EnterMultipleScope())
        {
            Assert.That(KeysWithGroup(input), Is.EqualTo([$"{KeySemicolon} down@1", $"{KeySemicolon} up@1", $"{KeyY} down@1", $"{KeyY} up@1"]));
            Assert.That(GroupSwitches(input), Is.EqualTo(1), "no switch back for the shortcut");
        }
    }

    [Test]
    public async Task UsRu_OnVirtualInput_AfterACyrillicLetter_CtrlComma_GoesBackToGroup0WithNoShift()
    {
        // under ru , is Shift+slash; the shortcut takes us's comma key instead
        RequireLinux();
        var input = new RecordingVirtualInput();
        using var device = UsRuVirtual(input);
        var handler = await Handler(device);
        Type(handler, "д");
        Shortcut(handler, ',');
        using (Assert.EnterMultipleScope())
        {
            Assert.That(KeysWithGroup(input), Is.EqualTo([$"{KeyL} down@1", $"{KeyL} up@1", $"{KeyComma} down@0", $"{KeyComma} up@0"]));
            Assert.That(LastGroup(input), Is.EqualTo("0"));
        }
    }

    [Test]
    public async Task Ru_OnEis_CtrlC_TakesTheUsReferenceKey()
    {
        // ru alone has no Latin group: a shortcut ru does not type takes the us reference keymap's key, while ru's own
        // exact / comes first; which key that is varies with xkeyboard-config (Shift+backslash, or LSGT with no Shift),
        // so it is read from the keymap, and it is never us's KEY_SLASH
        RequireLinux();
        var (handler, device) = await Started("EIS", keymap: true, layout: "ru");
        using var lookup = new XkbKeymapLookup(device.Keymap!);
        var slash = lookup.Find(XkbKeymapLookup.CharToKeysym('/')) ?? throw new AssertionException("pre-condition: ru types /");
        Assert.That(slash.AltGr, Is.False, "pre-condition: ru types / with at most Shift");
        foreach (var ch in "cv/") Shortcut(handler, ch);
        var shortcuts = device.Sent.ToList();
        Type(handler, "c");
        string[] slashStrokes = slash.Shift
            ? [$"key {LeftShift} down", "frame", $"key {slash.EvdevKey} down", "frame", $"key {LeftShift} up", "frame", $"key {slash.EvdevKey} up", "frame"]
            : Taps(slash.EvdevKey);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(slash.EvdevKey, Is.Not.EqualTo(KeySlash), "ru's own key, not the us reference key");
            Assert.That(shortcuts, Is.EqualTo((string[])["start", .. Taps(KeyC, KeyV), .. slashStrokes]));
            Assert.That(device.Sent, Is.EqualTo(shortcuts), "a plain c is no shortcut and ru types none");
        }
    }

    [Test]
    public async Task Ru_OnEis_PunctuationShortcuts_AreRusOwnExactKeys()
    {
        // ru types . on KEY_SLASH and , on Shift+KEY_SLASH; apps see ru's keysyms, so the exact key is the one they match
        RequireLinux();
        var (handler, device) = await Started("EIS", keymap: true, layout: "ru");
        foreach (var ch in ".,") Shortcut(handler, ch);
        Assert.That(device.Sent, Is.EqualTo((string[])["start", .. Taps(KeySlash),
            $"key {LeftShift} down", "frame", $"key {KeySlash} down", "frame", $"key {LeftShift} up", "frame", $"key {KeySlash} up", "frame"]));
    }

    [Test]
    public async Task Ru_OnVirtualInput_CtrlC_TakesTheBoundSpareKey()
    {
        // under ru KEY_C types с; the device binds an exact c instead
        RequireLinux();
        var input = new RecordingVirtualInput();
        using var device = VirtualOn(input, "ru");
        using var lookup = new XkbKeymapLookup(device.Keymap!);
        var spare = lookup.SpareKey() ?? throw new AssertionException("pre-condition: ru has a spare key");
        var handler = await Handler(device);
        Shortcut(handler, 'c');
        using (Assert.EnterMultipleScope())
        {
            Assert.That(input.Sent.Where(s => s.StartsWith("key ", StringComparison.Ordinal)), Is.EqualTo([$"key {spare} down", $"key {spare} up"]));
            Assert.That(input.Keymaps, Has.Count.GreaterThanOrEqualTo(2), "a keymap binding c is uploaded");
        }
        using var bound = new XkbKeymapLookup(input.Keymaps[1]);
        Assert.That(bound.Find(XkbKeymapLookup.CharToKeysym('c'))?.EvdevKey, Is.EqualTo(spare));
    }
}

// VirtualInjectionDevice's keymap swap for a keysym no key types, against recorded virtual devices: the us keymap
// compiled with xkbcommon, so it self-skips off Linux
[TestFixture]
[Category("Linux")]
public class VirtualInjectionDeviceSwapTests
{
    private const uint Snowman = 0x1002603, Comet = 0x1002604, LeftShift = 42, KeyA = 30;

    private RecordingVirtualInput _input = null!;
    private VirtualInjectionDevice _device = null!;
    private uint _spare;

    [OneTimeSetUp]
    public void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Ignore("Requires Linux with libxkbcommon (run ./run-tests.sh linux)");
    }

    [SetUp]
    public void Create()
    {
        _input = new RecordingVirtualInput();
        var keymap = XkbKeymap.FromNames(new LinuxInputConfig.XkbNames("us", "pc105", null, null));
        _device = new VirtualInjectionDevice(keymap, () => _input);
        using var lookup = new XkbKeymapLookup(_device.Keymap!);
        _spare = lookup.SpareKey() ?? throw new AssertionException("pre-condition: us has a spare key");
    }

    [TearDown]
    public void Dispose()
    {
        _device.Dispose();
        _input.Dispose();
    }

    [Test]
    public void Bind_UploadsAKeymapTypingTheKeysymOnTheSpareKey()
    {
        var key = _device.BindKeysym(Snowman);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(key, Is.EqualTo(_spare));
            Assert.That(_input.Sent, Is.EqualTo(["keymap 0", "keymap 1", "mods 0,0,0,0"]));
        }
        using var bound = new XkbKeymapLookup(_input.Keymaps[1]);
        Assert.That(bound.Find(Snowman), Is.EqualTo(new XkbKeyStroke(_spare, false, false)));
    }

    [Test]
    public void TheSameKeysymAgain_IsNotUploadedAgain()
    {
        TypeBound(Snowman);
        TypeBound(Snowman);
        Assert.That(_input.Sent, Is.EqualTo(["keymap 0", "keymap 1", "mods 0,0,0,0",
            $"key {_spare} down", $"key {_spare} up", $"key {_spare} down", $"key {_spare} up"]));
    }

    [Test]
    public void AnotherKey_PutsTheCompositorsKeymapBackFirst()
    {
        TypeBound(Snowman);
        _device.Key(KeyA, true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_input.Sent, Is.EqualTo(["keymap 0", "keymap 1", "mods 0,0,0,0",
            $"key {_spare} down", $"key {_spare} up", "keymap 2", "mods 0,0,0,0", $"key {KeyA} down"]));
            Assert.That(_input.Keymaps[2], Is.EqualTo(_input.Keymaps[0]));
        }
    }

    [Test]
    public void AnotherKeysym_IsBoundOnANewKeymap()
    {
        TypeBound(Snowman);
        Assert.That(_device.BindKeysym(Comet), Is.EqualTo(_spare));
        using var bound = new XkbKeymapLookup(_input.Keymaps[^1]);
        Assert.That(bound.Find(Comet), Is.EqualTo(new XkbKeyStroke(_spare, false, false)));
    }

    [Test]
    public void AnotherKeysym_WhileTheSpareKeyIsHeld_IsRefused()
    {
        _device.BindKeysym(Snowman);
        _device.Key(_spare, true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_device.BindKeysym(Comet), Is.Null, "the held key would type something else");
            Assert.That(_input.Keymaps, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public void AHeldShift_IsSentAgainAfterEachUpload()
    {
        _device.Key(LeftShift, true);
        TypeBound(Snowman);
        _device.Key(KeyA, true);
        Assert.That(_input.Sent, Is.EqualTo(["keymap 0", $"key {LeftShift} down", "mods 1,0,0,0",
            "keymap 1", "mods 1,0,0,0", $"key {_spare} down", $"key {_spare} up",
            "keymap 2", "mods 1,0,0,0", $"key {KeyA} down"]));
    }

    [Test]
    public void StopEmulating_PutsTheCompositorsKeymapBack()
    {
        TypeBound(Snowman);
        _device.StopEmulating();
        _device.StopEmulating();
        Assert.That(_input.Sent.TakeLast(2), Is.EqualTo(["keymap 2", "mods 0,0,0,0"]));
    }

    [Test]
    public void ALostConnection_BindsNothing()
    {
        _input.Lose();
        Assert.That(_device.BindKeysym(Snowman), Is.Null);
    }

    private void TypeBound(uint keysym)
    {
        Assert.That(_device.BindKeysym(keysym), Is.EqualTo(_spare));
        _device.Key(_spare, true);
        _device.Key(_spare, false);
    }
}
