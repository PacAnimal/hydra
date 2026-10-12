using Hydra.Platform.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Platform.Windows;

// The router can cross screens while a hook callback is still running. The crossing tests flip the flag
// from inside the callback the router would run, the way that crossing lands, and check whether the
// sample is swallowed or passed through to the local desktop.
[TestFixture]
public class WindowsInputHandlerHookTests
{
    private const int ParkX = 960, ParkY = 540;
    private const int EdgeX = 1919, EdgeY = 300;
    private static readonly Action<int, int> NoWarp = static (_, _) => { };

    private static WindowsInputHandler NewHandler() =>
        new(NullLogger<WindowsInputHandler>.Instance, TransitionTestHelper.Profile("home"));

    private static void Subscribe(
        WindowsInputHandler handler,
        Action<double, double>? onMouseMove = null,
        Action<double, double>? onMouseDelta = null,
        Action? onMouseButton = null) =>
        handler.Subscribe(onMouseMove ?? ((_, _) => { }), onMouseDelta, _ => { }, _ => onMouseButton?.Invoke(), _ => { });

    private static readonly KBDLLHOOKSTRUCT KeyA = new() { vkCode = WinVirtualKey.A, time = 1 };

    private static MSLLHOOKSTRUCT At(int x, int y) => new() { pt = new WINPOINT { x = x, y = y } };

    [Test]
    public void MoveThatReturnsToLocal_IsSwallowed()
    {
        var handler = NewHandler();
        Subscribe(handler, onMouseDelta: (_, _) =>
        {
            handler.IsOnVirtualScreen = false;
            handler.SetWarpState(EdgeX, EdgeY);
        });
        handler.SetWarpState(ParkX, ParkY);
        handler.IsOnVirtualScreen = true;
        handler.HandleMouse(NativeMethods.WM_MOUSEMOVE, At(ParkX + 5, ParkY), NoWarp); // resyncs the stale delta reference

        Assert.That(handler.HandleMouse(NativeMethods.WM_MOUSEMOVE, At(ParkX + 50, ParkY), NoWarp), Is.True);
    }

    [Test]
    public void MoveThatEntersARemoteScreen_IsSwallowed()
    {
        var handler = NewHandler();
        Subscribe(handler, onMouseMove: (_, _) =>
        {
            handler.IsOnVirtualScreen = true;
            handler.SetWarpState(ParkX, ParkY);
        });

        Assert.That(handler.HandleMouse(NativeMethods.WM_MOUSEMOVE, At(EdgeX, EdgeY), NoWarp), Is.True);
    }

    [Test]
    public void LocalMove_PassesThrough()
    {
        Assert.That(NewHandler().HandleMouse(NativeMethods.WM_MOUSEMOVE, At(EdgeX, EdgeY), NoWarp), Is.False);
    }

    // the router forwards buttons and keys by its state when it processes them, so the hook must agree with the final read
    [TestCase(true, false, TestName = "ButtonWhileReturningToLocal_PassesThrough")]
    [TestCase(false, true, TestName = "ButtonWhileEnteringARemoteScreen_IsSwallowed")]
    public void ButtonWhileCrossingScreens_FollowsTheFinalRead(bool startOnVirtualScreen, bool swallowed)
    {
        var handler = NewHandler();
        Subscribe(handler, onMouseButton: () => handler.IsOnVirtualScreen = !startOnVirtualScreen);
        handler.IsOnVirtualScreen = startOnVirtualScreen;

        Assert.That(handler.HandleMouse(NativeMethods.WM_LBUTTONDOWN, At(EdgeX, EdgeY), NoWarp), Is.EqualTo(swallowed));
    }

    [TestCase(true, false, TestName = "KeyWhileReturningToLocal_PassesThrough")]
    [TestCase(false, true, TestName = "KeyWhileEnteringARemoteScreen_IsSwallowed")]
    [Platform("Win")] // key resolution needs ToUnicodeEx
    public void KeyWhileCrossingScreens_FollowsTheFinalRead(bool startOnVirtualScreen, bool swallowed)
    {
        var handler = NewHandler();
        var keys = 0;
        handler.Subscribe((_, _) => { }, null, _ =>
        {
            keys++;
            handler.IsOnVirtualScreen = !startOnVirtualScreen;
        }, _ => { }, _ => { });
        handler.IsOnVirtualScreen = startOnVirtualScreen;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.HandleKey(NativeMethods.WM_KEYDOWN, KeyA), Is.EqualTo(swallowed));
            Assert.That(keys, Is.Positive, "the key must reach the router for the test to mean anything");
        }
    }
}
