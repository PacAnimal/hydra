using System.Runtime.InteropServices;
using System.Text;
using Hydra.Platform;
using Hydra.Platform.Linux.Wayland;

namespace Tests.Platform.Wayland;

[TestFixture]
public unsafe class WaylandClientTests
{
    private static readonly nint SeatTable = 0x5EA7;
    private static readonly nint OutputTable = 0x0D7;

    private static WaylandProtocols Built() => WaylandProtocols.Build(name => name switch
    {
        "wl_seat" => SeatTable,
        "wl_output" => OutputTable,
        _ => 0,
    });

    private static WlInterface* Table(WaylandProtocols protocols, string name) => (WlInterface*)protocols[name];

    private static string Read(nint utf8) => Marshal.PtrToStringUTF8(utf8)!;

    private static List<string> Names(WlMessage* messages, int count)
    {
        var names = new List<string>();
        for (var i = 0; i < count; i++) names.Add(Read(messages[i].name));
        return names;
    }

    // -- wl_argument --

    [Test]
    public void Argument_IsEightBytes()
    {
        Assert.That(sizeof(WlArgument), Is.EqualTo(8));
    }

    [Test]
    public void Argument_PacksEachKindAtOffsetZero()
    {
        var args = stackalloc WlArgument[5];
        args[0] = WaylandArgs.UInt(0xDEADBEEF);
        args[1] = WaylandArgs.Int(-7);
        args[2] = WaylandArgs.Fixed(1.5);
        args[3] = WaylandArgs.Object(0x1234_5678);
        args[4] = WaylandArgs.Fd(42);

        var raw = (uint*)args;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(raw[0], Is.EqualTo(0xDEADBEEF));
            Assert.That((int)raw[2], Is.EqualTo(-7));
            Assert.That((int)raw[4], Is.EqualTo(384));
            Assert.That(((nint*)args)[3], Is.EqualTo((nint)0x1234_5678));
            Assert.That((int)raw[8], Is.EqualTo(42));
        }
    }

    [Test]
    public void NewId_IsZeroed()
    {
        Assert.That(WaylandArgs.NewId().o, Is.EqualTo(nint.Zero));
    }

    [TestCase("usun", 4)]
    [TestCase("?on", 2)]
    [TestCase("2?o?on", 3)]
    [TestCase("2s", 1)]
    [TestCase("", 0)]
    public void Count_IgnoresSinceAndNullable(string signature, int expected)
    {
        Assert.That(WaylandArgs.Count(signature), Is.EqualTo(expected));
    }

    // -- wl_fixed_t --

    [TestCase(0.0, 0)]
    [TestCase(1.0, 256)]
    [TestCase(-1.0, -256)]
    [TestCase(0.5, 128)]
    [TestCase(-2.25, -576)]
    [TestCase(1.0 / 256, 1)]
    public void Fixed_Is24Dot8(double value, int expected)
    {
        Assert.That(WaylandArgs.ToFixed(value), Is.EqualTo(expected));
    }

    [Test]
    public void Fixed_RoundTrips()
    {
        foreach (var value in new[] { 0.0, 3.5, -12.75, 1000.125 })
            Assert.That(WaylandArgs.FromFixed(WaylandArgs.ToFixed(value)), Is.EqualTo(value));
    }

    [Test]
    public void Fixed_SaturatesAndDropsNaN()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(WaylandArgs.ToFixed(1e12), Is.EqualTo(int.MaxValue));
            Assert.That(WaylandArgs.ToFixed(-1e12), Is.EqualTo(int.MinValue));
            Assert.That(WaylandArgs.ToFixed(double.NaN), Is.Zero);
        }
    }

    // -- interface tables --

    [Test]
    public void Structs_HaveLibwaylandLayout()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sizeof(WlMessage), Is.EqualTo(24));
            Assert.That(sizeof(WlInterface), Is.EqualTo(40));
            Assert.That(Marshal.OffsetOf<WlInterface>(nameof(WlInterface.methods)), Is.EqualTo((nint)16));
            Assert.That(Marshal.OffsetOf<WlInterface>(nameof(WlInterface.event_count)), Is.EqualTo((nint)24));
            Assert.That(Marshal.OffsetOf<WlInterface>(nameof(WlInterface.events)), Is.EqualTo((nint)32));
        }
    }

    [Test]
    public void VirtualPointer_RequestsFollowTheXml()
    {
        var table = Table(Built(), WaylandProtocols.VirtualPointer);
        var messages = (WlMessage*)table->methods;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Read(table->name), Is.EqualTo("zwlr_virtual_pointer_v1"));
            Assert.That(table->version, Is.EqualTo(2));
            Assert.That(table->event_count, Is.Zero);
            Assert.That(table->events, Is.EqualTo(nint.Zero));
            Assert.That(Names(messages, table->method_count), Is.EqualTo(
                ["motion", "motion_absolute", "button", "axis", "frame", "axis_source", "axis_stop", "axis_discrete", "destroy"]));
            Assert.That(Read(messages[WaylandProtocols.PointerMotion].signature), Is.EqualTo("uff"));
            Assert.That(Read(messages[WaylandProtocols.PointerMotionAbsolute].signature), Is.EqualTo("uuuuu"));
            Assert.That(Read(messages[WaylandProtocols.PointerButton].signature), Is.EqualTo("uuu"));
            Assert.That(Read(messages[WaylandProtocols.PointerAxis].signature), Is.EqualTo("uuf"));
            Assert.That(Read(messages[WaylandProtocols.PointerFrame].signature), Is.Empty);
            Assert.That(Read(messages[WaylandProtocols.PointerAxisSource].signature), Is.EqualTo("u"));
            Assert.That(Read(messages[WaylandProtocols.PointerAxisStop].signature), Is.EqualTo("uu"));
            Assert.That(Read(messages[WaylandProtocols.PointerAxisDiscrete].signature), Is.EqualTo("uufi"));
            Assert.That(Read(messages[WaylandProtocols.PointerDestroy].signature), Is.Empty);
        }
    }

    [Test]
    public void VirtualPointerManager_CreatesPointersOnTheSeat()
    {
        var protocols = Built();
        var table = Table(protocols, WaylandProtocols.VirtualPointerManager);
        var messages = (WlMessage*)table->methods;
        var create = (nint*)messages[WaylandProtocols.PointerManagerCreate].types;
        var withOutput = (nint*)messages[WaylandProtocols.PointerManagerCreateWithOutput].types;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(table->version, Is.EqualTo(2));
            Assert.That(Names(messages, table->method_count),
                Is.EqualTo(["create_virtual_pointer", "destroy", "create_virtual_pointer_with_output"]));
            Assert.That(Read(messages[WaylandProtocols.PointerManagerCreate].signature), Is.EqualTo("?on"));
            Assert.That(Read(messages[WaylandProtocols.PointerManagerDestroy].signature), Is.Empty);
            Assert.That(Read(messages[WaylandProtocols.PointerManagerCreateWithOutput].signature), Is.EqualTo("2?o?on"));
            Assert.That(create[0], Is.EqualTo(SeatTable));
            Assert.That(create[1], Is.EqualTo(protocols[WaylandProtocols.VirtualPointer]));
            Assert.That(withOutput[0], Is.EqualTo(SeatTable));
            Assert.That(withOutput[1], Is.EqualTo(OutputTable));
            Assert.That(withOutput[2], Is.EqualTo(protocols[WaylandProtocols.VirtualPointer]));
        }
    }

    [Test]
    public void VirtualKeyboard_RequestsFollowTheXml()
    {
        var table = Table(Built(), WaylandProtocols.VirtualKeyboard);
        var messages = (WlMessage*)table->methods;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Read(table->name), Is.EqualTo("zwp_virtual_keyboard_v1"));
            Assert.That(table->version, Is.EqualTo(1));
            Assert.That(table->event_count, Is.Zero);
            Assert.That(Names(messages, table->method_count), Is.EqualTo(["keymap", "key", "modifiers", "destroy"]));
            Assert.That(Read(messages[WaylandProtocols.KeyboardKeymap].signature), Is.EqualTo("uhu"));
            Assert.That(Read(messages[WaylandProtocols.KeyboardKey].signature), Is.EqualTo("uuu"));
            Assert.That(Read(messages[WaylandProtocols.KeyboardModifiers].signature), Is.EqualTo("uuuu"));
            Assert.That(Read(messages[WaylandProtocols.KeyboardDestroy].signature), Is.Empty);
        }
    }

    [Test]
    public void VirtualKeyboardManager_CreatesKeyboardsOnTheSeat()
    {
        var protocols = Built();
        var table = Table(protocols, WaylandProtocols.VirtualKeyboardManager);
        var messages = (WlMessage*)table->methods;
        var types = (nint*)messages[WaylandProtocols.KeyboardManagerCreate].types;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(table->version, Is.EqualTo(1));
            Assert.That(Names(messages, table->method_count), Is.EqualTo(["create_virtual_keyboard"]));
            Assert.That(Read(messages[0].signature), Is.EqualTo("on"));
            Assert.That(types[0], Is.EqualTo(SeatTable));
            Assert.That(types[1], Is.EqualTo(protocols[WaylandProtocols.VirtualKeyboard]));
        }
    }

    [Test]
    public void XdgOutputManager_GetsAnXdgOutputForAWlOutput()
    {
        var protocols = Built();
        var table = Table(protocols, WaylandProtocols.XdgOutputManager);
        var messages = (WlMessage*)table->methods;
        var types = (nint*)messages[WaylandProtocols.XdgOutputManagerGet].types;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(table->version, Is.EqualTo(3));
            Assert.That(Names(messages, table->method_count), Is.EqualTo(["destroy", "get_xdg_output"]));
            Assert.That(Read(messages[WaylandProtocols.XdgOutputManagerDestroy].signature), Is.Empty);
            Assert.That(Read(messages[WaylandProtocols.XdgOutputManagerGet].signature), Is.EqualTo("no"));
            Assert.That(types[0], Is.EqualTo(protocols[WaylandProtocols.XdgOutput]));
            Assert.That(types[1], Is.EqualTo(OutputTable));
        }
    }

    [Test]
    public void XdgOutput_EventsFollowTheXml()
    {
        var table = Table(Built(), WaylandProtocols.XdgOutput);
        var events = (WlMessage*)table->events;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(table->version, Is.EqualTo(3));
            Assert.That(Names((WlMessage*)table->methods, table->method_count), Is.EqualTo(["destroy"]));
            Assert.That(Names(events, table->event_count),
                Is.EqualTo(["logical_position", "logical_size", "done", "name", "description"]));
            Assert.That(Read(events[WaylandProtocols.XdgOutputLogicalPosition].signature), Is.EqualTo("ii"));
            Assert.That(Read(events[WaylandProtocols.XdgOutputLogicalSize].signature), Is.EqualTo("ii"));
            Assert.That(Read(events[WaylandProtocols.XdgOutputDone].signature), Is.Empty);
            Assert.That(Read(events[WaylandProtocols.XdgOutputName].signature), Is.EqualTo("2s"));
            Assert.That(Read(events[WaylandProtocols.XdgOutputDescription].signature), Is.EqualTo("2s"));
        }
    }

    [Test]
    public void EveryMessage_HasATypeSlotPerArgument()
    {
        foreach (var spec in WaylandProtocols.Specs)
        {
            foreach (var message in spec.Requests.Concat(spec.Events))
                Assert.That(message.Types, Has.Count.EqualTo(WaylandArgs.Count(message.Signature)), $"{spec.Name}.{message.Name}");
        }
    }

    [Test]
    public void AMissingCoreInterface_FailsTheBuild()
    {
        Assert.That(() => WaylandProtocols.Build(_ => 0), Throws.InvalidOperationException);
    }

    // -- absolute motion --

    private static DetectedScreen Screen(int x, int y, int width, int height) => new(x, y, width, height, null, null, null);

    [Test]
    public void Normalise_IsRelativeToTheBoundingBox()
    {
        var outputs = new[] { Screen(0, 0, 1920, 1080), Screen(1920, -200, 2560, 1440) };

        Assert.That(VirtualInput.Normalise(2000, 100, outputs), Is.EqualTo(new AbsoluteMotion(2000, 300, 4480, 1440)));
    }

    [Test]
    public void Normalise_AcceptsAnOriginAwayFromZero()
    {
        var outputs = new[] { Screen(-1280, 0, 1280, 1024), Screen(0, 0, 1920, 1080) };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(VirtualInput.Normalise(-1280, 0, outputs), Is.EqualTo(new AbsoluteMotion(0, 0, 3200, 1080)));
            Assert.That(VirtualInput.Normalise(0, 0, outputs), Is.EqualTo(new AbsoluteMotion(1280, 0, 3200, 1080)));
        }
    }

    [Test]
    public void Normalise_ClampsInsideTheBox()
    {
        var outputs = new[] { Screen(0, 0, 1920, 1080) };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(VirtualInput.Normalise(-50, -50, outputs), Is.EqualTo(new AbsoluteMotion(0, 0, 1920, 1080)));
            Assert.That(VirtualInput.Normalise(5000, 5000, outputs), Is.EqualTo(new AbsoluteMotion(1919, 1079, 1920, 1080)));
        }
    }

    [Test]
    public void Normalise_WithoutOutputs_IsNull()
    {
        Assert.That(VirtualInput.Normalise(0, 0, []), Is.Null);
    }

    // -- keymap --

    [Test]
    public void Keymap_IsNulTerminatedUtf8()
    {
        var bytes = VirtualInput.KeymapBytes("xkb_keymap { æ };");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bytes[^1], Is.Zero);
            Assert.That(Encoding.UTF8.GetString(bytes, 0, bytes.Length - 1), Is.EqualTo("xkb_keymap { æ };"));
            Assert.That(bytes, Has.Length.EqualTo(Encoding.UTF8.GetByteCount("xkb_keymap { æ };") + 1));
        }
    }
}
