using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Platform.Linux;
using Hydra.Platform.Linux.Wayland;
using Hydra.Relay;
using Hydra.Screen;
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

    private async Task<Running> Started(string backend, bool keymap = false, string layout = "us", uint? bindKey = null)
    {
        var device = new RecordingDevice(backend, keymap ? KeymapFor(layout) : null, Screens, locked: 0, bindKey);
        var handler = new WaylandOutputHandler(_session, new CapturingLog<WaylandOutputHandler>(), _time);
        _handlers.Add(handler);
        await _session.Asked(_handlers.Count);
        _session.Succeed(_handlers.Count, device);
        await handler.Acquiring;
        Assert.That(handler.HasDevice, Is.True, "pre-condition: the handler took the device");
        return new Running(handler, device);
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
