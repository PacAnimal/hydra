using System.Collections.Concurrent;
using Hydra.Keyboard;
using Hydra.Mouse;
using Hydra.Platform.Linux;
using Microsoft.Extensions.Logging;
using Tests.Setup;

namespace Tests.Platform;

// The headless master's evdev input against real kernel devices made through uinput: unplugging, plugging in, and
// grabbing what arrives while the input is away. The handler reads a directory of links to the test's own devices, so
// it never sees the machine's real keyboard. Runs where uinput and a devtmpfs /dev/input are (run-tests.sh linux).
[TestFixture]
[Category("Linux")]
public class EvdevHotplugTests
{
    private string _dir = null!;
    private CapturingLog<EvdevInputHandler> _log = null!;
    private EvdevInputHandler _handler = null!;
    private readonly List<VirtualInputDevice> _devices = [];
    private readonly BlockingCollection<object> _events = [];
    // the event loop parks on the first event matching _parkOn, so a test can act while it cannot read
    private readonly Gate _loop = new();
    private Func<object, bool>? _parkOn;
    private TaskCompletionSource _parked = null!;

    [OneTimeSetUp]
    public void RequireUinput()
    {
        if (!VirtualInputDevice.Available)
            Assert.Ignore("Requires Linux with a writable /dev/uinput and a devtmpfs /dev/input (run ./run-tests.sh linux)");
    }

    [SetUp]
    public void SetUp()
    {
        _dir = TestPaths.FreshFixtureRoot(nameof(EvdevHotplugTests));
        _log = new CapturingLog<EvdevInputHandler>(LogLevel.Debug);
        _handler = new EvdevInputHandler(_log) { InputDir = _dir };
        while (_events.TryTake(out _)) { }
        _parkOn = null;
        _parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [TearDown]
    public async Task TearDown()
    {
        _loop.Release();
        await _handler.DisposeAsync();
        foreach (var device in _devices) device.Dispose();
        _devices.Clear();
    }

    private VirtualInputDevice Plug(VirtualInputDevice device, string? linkName = null)
    {
        _devices.Add(device);
        Link(device, linkName);
        return device;
    }

    private void Link(VirtualInputDevice device, string? linkName = null)
    {
        var link = Path.Combine(_dir, linkName ?? Path.GetFileName(device.Node));
        if (File.Exists(link) || Directory.Exists(link)) File.Delete(link);
        File.CreateSymbolicLink(link, device.Node);
    }

    // started, and grabbed so that what the test types never reaches a desktop on the machine running it
    private async Task Start()
    {
        await _handler.StartEventTap((_, _) => { }, (_, _) => { }, Record, e => Record(e), e => Record(e));
        _handler.IsOnVirtualScreen = true;
    }

    private void Record(object e)
    {
        _events.Add(e);
        if (_parkOn?.Invoke(e) != true) return;
        _parkOn = null;
        _parked.TrySetResult();
        _loop.Passed().Wait(TestBound.Bound);
    }

    // Unplugs the device while the event loop is parked inside the event matching parkOn. The kernel queues ups for
    // what the device held, but marks it dead in the same unplug, so a loop that cannot read until then never sees
    // them: anything released after this is Hydra's own doing.
    private async Task UnplugWhileParked(VirtualInputDevice device, Func<object, bool> parkOn, Action press)
    {
        _loop.Hold();
        _parkOn = parkOn;
        press();
        await _parked.Task.WaitAsync(TestBound.Bound);
        device.Unplug();   // synchronous: the device is dead once this returns
        _loop.Release();
        await Removed(device.Node);
    }

    // waits for the next event of that type matching, skipping any before it
    private void Next<T>(Func<T, bool> match)
    {
        while (_events.TryTake(out var e, TestBound.Bound))
            if (e is T t && match(t)) return;
        throw new TimeoutException($"No matching {typeof(T).Name} arrived");
    }

    // by the link's name: "Input device added: <dir>/eventN (keyboard)", "Input device removed: <dir>/eventN"
    private Task<CapturedEntry> Added(string node) => Logged($"Input device added: {Path.Combine(_dir, Path.GetFileName(node))} (");
    private Task<CapturedEntry> Removed(string node) => Logged($"Input device removed: {Path.Combine(_dir, Path.GetFileName(node))}", whole: true);

    private Task<CapturedEntry> Logged(string text, bool whole = false) =>
        _log.WaitFor(e => whole ? e.Message == text : e.Message.StartsWith(text, StringComparison.Ordinal));

    // the events before the first matching one, waiting for it
    private List<object> Before<T>(Func<T, bool> match)
    {
        var skipped = new List<object>();
        while (_events.TryTake(out var e, TestBound.Bound))
        {
            if (e is T t && match(t)) return skipped;
            skipped.Add(e);
        }
        throw new TimeoutException($"No matching {typeof(T).Name} arrived");
    }

    private static bool IsShift(KeyEvent e) => e.Key == SpecialKey.Shift_L;

    [Test]
    public async Task HeldKey_IsReleased_WhenItsKeyboardIsUnplugged()
    {
        var keyboard = Plug(VirtualInputDevice.Keyboard("hydra-test-keyboard"));
        var other = Plug(VirtualInputDevice.Keyboard("hydra-test-keyboard-2"));
        await Start();

        await UnplugWhileParked(keyboard, e => e is KeyEvent { Key: SpecialKey.Shift_L, Type: KeyEventType.KeyDown },
            () => keyboard.Key(UinputNativeMethods.KEY_LEFTSHIFT, down: true));

        Next<KeyEvent>(e => IsShift(e) && e.Type == KeyEventType.KeyDown);
        Next<KeyEvent>(e => IsShift(e) && e.Type == KeyEventType.KeyUp);

        // a key typed on the other keyboard after the drop: no second release of Shift came before it
        other.Key(EvdevNativeMethods.KEY_A, down: true);
        other.Key(EvdevNativeMethods.KEY_A, down: false);
        Assert.That(Before<KeyEvent>(e => e is { Character: 'a', Type: KeyEventType.KeyDown }).OfType<KeyEvent>().Where(IsShift), Is.Empty,
            "the key is released once");
    }

    [Test]
    public async Task HeldButton_IsReleased_WhenItsMouseIsUnplugged()
    {
        var mouse = Plug(VirtualInputDevice.Mouse("hydra-test-mouse"));
        await Start();

        await UnplugWhileParked(mouse, e => e is MouseButtonEvent { Button: MouseButton.Left, IsPressed: true },
            () => mouse.Key(EvdevNativeMethods.BTN_LEFT, down: true));

        Next<MouseButtonEvent>(e => e is { Button: MouseButton.Left, IsPressed: true });
        Next<MouseButtonEvent>(e => e is { Button: MouseButton.Left, IsPressed: false });
    }

    [Test]
    public async Task KeyboardPluggedInAfterStart_IsRead()
    {
        Plug(VirtualInputDevice.Mouse("hydra-test-mouse"));
        await Start();

        var keyboard = Plug(VirtualInputDevice.Keyboard("hydra-test-keyboard"));
        await Added(keyboard.Node);

        keyboard.Key(EvdevNativeMethods.KEY_A, down: true);
        keyboard.Key(EvdevNativeMethods.KEY_A, down: false);
        Next<KeyEvent>(e => e is { Character: 'a', Type: KeyEventType.KeyDown });
    }

    [Test]
    public async Task RemainingDevice_KeepsBeingRead_AfterAnotherIsUnplugged()
    {
        var first = Plug(VirtualInputDevice.Keyboard("hydra-test-keyboard-1"));
        var second = Plug(VirtualInputDevice.Keyboard("hydra-test-keyboard-2"));
        await Start();

        first.Unplug();
        await Removed(first.Node);

        second.Key(EvdevNativeMethods.KEY_A, down: true);
        second.Key(EvdevNativeMethods.KEY_A, down: false);
        Next<KeyEvent>(e => e is { Character: 'a', Type: KeyEventType.KeyDown });
    }

    [Test]
    public async Task StartWithNoKeyboardOrMouse_WaitsForOne()
    {
        Plug(VirtualInputDevice.Other("hydra-test-power"));
        await Start();
        await _log.WaitFor(e => e.Message.Contains("waiting for one to be plugged in"));

        var keyboard = Plug(VirtualInputDevice.Keyboard("hydra-test-keyboard"));
        await Added(keyboard.Node);
    }

    [Test]
    public async Task NodesThatCannotBeOpened_FailTheStart()
    {
        File.CreateSymbolicLink(Path.Combine(_dir, "event0"), Path.Combine(_dir, "missing"));

        await Assert.ThatAsync(Start, Throws.InvalidOperationException);
    }

    // the kernel hands a freed event number to the next device at once, so a name once taken by a device that is
    // neither keyboard nor pointer must not be skipped when a keyboard comes back under it
    [Test]
    public async Task ReusedNodeName_IsProbedAgain()
    {
        const string name = "event9000";
        Plug(VirtualInputDevice.Other("hydra-test-power"), name);
        await Start();

        var keyboard = Plug(VirtualInputDevice.Keyboard("hydra-test-keyboard"), name);
        await Logged($"Input device added: {Path.Combine(_dir, name)} (");

        keyboard.Key(EvdevNativeMethods.KEY_A, down: true);
        keyboard.Key(EvdevNativeMethods.KEY_A, down: false);
        Next<KeyEvent>(e => e is { Character: 'a', Type: KeyEventType.KeyDown });
    }

    // opening a node wakes its device, so one that is neither keyboard nor mouse is probed once, not on every scan
    [Test]
    public async Task NonInputNode_IsProbedOnce()
    {
        var power = Plug(VirtualInputDevice.Other("hydra-test-power"));
        await Start();

        // a device plugged in makes the loop scan the directory again
        var keyboard = Plug(VirtualInputDevice.Keyboard("hydra-test-keyboard"));
        await Added(keyboard.Node);

        var probed = $"Neither keyboard nor mouse: {Path.Combine(_dir, Path.GetFileName(power.Node))}";
        Assert.That(_log.Messages.Count(m => m == probed), Is.EqualTo(1));
    }

    [Test]
    public async Task DevicePluggedInWhileAway_IsGrabbed_AndReleasedOnReturn()
    {
        Plug(VirtualInputDevice.Keyboard("hydra-test-keyboard-1"));
        await Start();

        var keyboard = Plug(VirtualInputDevice.Keyboard("hydra-test-keyboard-2"));
        await Added(keyboard.Node);
        Assert.That(GrabbedElsewhere(keyboard.Node), Is.True, "grabbed on arrival while the input is away");

        _handler.IsOnVirtualScreen = false;
        Assert.That(GrabbedElsewhere(keyboard.Node), Is.False, "released on return");
    }

    // whether another process holds EVIOCGRAB on the node: our own grab fails with EBUSY if so
    private static bool GrabbedElsewhere(string node)
    {
        var fd = EvdevNativeMethods.Open(node, EvdevNativeMethods.O_RDONLY | EvdevNativeMethods.O_NONBLOCK | EvdevNativeMethods.O_CLOEXEC);
        Assert.That(fd, Is.GreaterThanOrEqualTo(0), $"{node} opens");
        try
        {
            if (EvdevNativeMethods.ioctl_grab(fd, EvdevNativeMethods.EVIOCGRAB, 1) < 0) return true;
            _ = EvdevNativeMethods.ioctl_grab(fd, EvdevNativeMethods.EVIOCGRAB, 0);
            return false;
        }
        finally
        {
            _ = EvdevNativeMethods.Close(fd);
        }
    }
}
