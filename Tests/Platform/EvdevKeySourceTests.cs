using System.Collections.Concurrent;
using Hydra.Platform.Linux;
using Microsoft.Extensions.Logging;
using Tests.Setup;

namespace Tests.Platform;

// The evdev key source a Wayland master reads its hotkeys from, against real kernel devices made through uinput: raw
// key codes, read beside whoever else reads the keyboard rather than grabbed from it. Reads a directory of links to the
// test's own devices, so it never sees the machine's real keyboard. Runs where uinput and a devtmpfs /dev/input are
// (run-tests.sh linux).
[TestFixture]
[Category("Linux")]
public class EvdevKeySourceTests
{
    private string _dir = null!;
    private CapturingLog<EvdevInputHandler> _log = null!;
    private EvdevKeySource _source = null!;
    private readonly List<VirtualInputDevice> _devices = [];
    private readonly BlockingCollection<RawKey> _keys = [];

    private sealed record RawKey(uint Code, bool Down);

    [OneTimeSetUp]
    public void RequireUinput()
    {
        if (!VirtualInputDevice.Available)
            Assert.Ignore("Requires Linux with a writable /dev/uinput and a devtmpfs /dev/input (run ./run-tests.sh linux)");
    }

    [SetUp]
    public void SetUp()
    {
        _dir = TestPaths.FreshFixtureRoot(nameof(EvdevKeySourceTests));
        _log = new CapturingLog<EvdevInputHandler>(LogLevel.Debug);
        _source = new EvdevKeySource(_log, _dir);
        _source.Key += (code, down) => _keys.Add(new RawKey(code, down));
        while (_keys.TryTake(out _)) { }
    }

    [TearDown]
    public void TearDown()
    {
        _source.Dispose();
        foreach (var device in _devices) device.Dispose();
        _devices.Clear();
    }

    private VirtualInputDevice Plug(VirtualInputDevice device)
    {
        _devices.Add(device);
        File.CreateSymbolicLink(Path.Combine(_dir, Path.GetFileName(device.Node)), device.Node);
        return device;
    }

    private RawKey Next()
    {
        if (_keys.TryTake(out var key, TestBound.Bound)) return key;
        throw new TimeoutException("No key arrived");
    }

    private void AssertNext(uint code, bool down)
    {
        var key = Next();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(key.Code, Is.EqualTo(code), "code");
            Assert.That(key.Down, Is.EqualTo(down), "down");
        }
    }

    [Test]
    public void KeyPressAndRelease_ArriveRaw()
    {
        var keyboard = Plug(VirtualInputDevice.Keyboard("hydra-test-keyboard"));
        Assert.That(_source.Start(), Is.True);

        keyboard.Key(EvdevNativeMethods.KEY_A, down: true);
        keyboard.Key(EvdevNativeMethods.KEY_A, down: false);

        AssertNext(EvdevNativeMethods.KEY_A, true);
        AssertNext(EvdevNativeMethods.KEY_A, false);
    }

    [Test]
    public void Keyboard_IsNotGrabbed()
    {
        var keyboard = Plug(VirtualInputDevice.Keyboard("hydra-test-keyboard"));
        Assert.That(_source.Start(), Is.True);

        // a key read through it proves the source has the device open by now
        keyboard.Key(EvdevNativeMethods.KEY_A, down: true);
        AssertNext(EvdevNativeMethods.KEY_A, true);

        var fd = EvdevNativeMethods.Open(keyboard.Node, EvdevNativeMethods.O_RDONLY | EvdevNativeMethods.O_NONBLOCK | EvdevNativeMethods.O_CLOEXEC);
        Assert.That(fd, Is.GreaterThanOrEqualTo(0), $"{keyboard.Node} opens");
        try
        {
            Assert.That(EvdevNativeMethods.ioctl_grab(fd, EvdevNativeMethods.EVIOCGRAB, 1), Is.GreaterThanOrEqualTo(0),
                "nobody else holds the grab");
            _ = EvdevNativeMethods.ioctl_grab(fd, EvdevNativeMethods.EVIOCGRAB, 0);
        }
        finally
        {
            _ = EvdevNativeMethods.Close(fd);
        }
    }

    [Test]
    public void PointerOnlyDevice_IsNotOpened()
    {
        var mouse = Plug(VirtualInputDevice.Mouse("hydra-test-mouse"));
        var keyboard = Plug(VirtualInputDevice.Keyboard("hydra-test-keyboard"));
        Assert.That(_source.Start(), Is.True);

        mouse.Button(EvdevNativeMethods.BTN_LEFT, down: true);
        mouse.Button(EvdevNativeMethods.BTN_LEFT, down: false);
        mouse.Move(5, 5);
        // a sentinel after it: nothing of the mouse's came before
        keyboard.Key(EvdevNativeMethods.KEY_A, down: true);

        using (Assert.EnterMultipleScope())
        {
            AssertNext(EvdevNativeMethods.KEY_A, true);
            Assert.That(_log.Messages, Does.Contain($"Neither keyboard nor mouse: {Path.Combine(_dir, Path.GetFileName(mouse.Node))}"));
        }
    }

    // a keyboard+mouse receiver is opened as a keyboard, but only its keys are hotkey material
    [Test]
    public void ComboReceiver_ButtonsRaiseNothing_KeysArrive()
    {
        var combo = Plug(VirtualInputDevice.Combo("hydra-test-combo"));
        Assert.That(_source.Start(), Is.True);

        combo.Button(EvdevNativeMethods.BTN_LEFT, down: true);
        combo.Button(EvdevNativeMethods.BTN_LEFT, down: false);
        // a sentinel after it: no button came before
        combo.Key(EvdevNativeMethods.KEY_A, down: true);

        AssertNext(EvdevNativeMethods.KEY_A, true);
    }

    // the session can start before any keyboard exists: the watch runs on, and takes one plugged in later
    [Test]
    public async Task NoKeyboardYet_StartIsTrue_AndOnePluggedInLaterDeliversKeys()
    {
        Plug(VirtualInputDevice.Mouse("hydra-test-mouse"));
        Plug(VirtualInputDevice.Other("hydra-test-power"));
        Assert.That(_source.Start(), Is.True);

        var keyboard = Plug(VirtualInputDevice.Keyboard("hydra-test-keyboard"));
        await _log.WaitFor(e => e.Message.StartsWith($"Input device added: {Path.Combine(_dir, Path.GetFileName(keyboard.Node))} (", StringComparison.Ordinal));

        keyboard.Key(EvdevNativeMethods.KEY_A, down: true);
        AssertNext(EvdevNativeMethods.KEY_A, true);
    }

    // a node the user may not open (EACCES) stands for a /dev/input without the input group; root opens anything
    [Test]
    public void UnreadableNodes_StartIsFalse()
    {
        if (Environment.IsPrivilegedProcess) Assert.Ignore("root opens anything");
        var node = Path.Combine(_dir, "event0");
        File.WriteAllText(node, "");
        UnixMode.Set(node, UnixFileMode.None);

        Assert.That(_source.Start(), Is.False);
    }

    // a user outside the input group is often granted a gamepad but no keyboard: one node refused with no keyboard
    // opened is unreadable keyboards, not a keyboard yet to be plugged in
    [Test]
    public void ReadableNonKeyboardAndUnreadableNode_StartIsFalse()
    {
        if (Environment.IsPrivilegedProcess) Assert.Ignore("root opens anything");
        Plug(VirtualInputDevice.Other("hydra-test-power"));
        var node = Path.Combine(_dir, "event9999");
        File.WriteAllText(node, "");
        UnixMode.Set(node, UnixFileMode.None);

        Assert.That(_source.Start(), Is.False);
    }
}
