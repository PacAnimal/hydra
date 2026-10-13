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
    private const int PinX = 100, PinY = 100;
    // the clock reads this when the flag flips; samples are stamped just after it unless a test says otherwise
    private const int FlipTick = 1_000;
    private const uint BeforeFlip = FlipTick - 1, AfterFlip = FlipTick + 1;
    private static readonly Action<int, int> NoWarp = static (_, _) => { };

    private static WindowsInputHandler NewHandler(int flipTick = FlipTick) =>
        new(NullLogger<WindowsInputHandler>.Instance, TransitionTestHelper.Profile("home")) { TickCount = () => flipTick };

    private static void Subscribe(
        WindowsInputHandler handler,
        Action<double, double>? onMouseMove = null,
        Action<double, double>? onMouseDelta = null,
        Action? onMouseButton = null) =>
        handler.Subscribe(onMouseMove ?? ((_, _) => { }), onMouseDelta, _ => { }, _ => onMouseButton?.Invoke(), _ => { });

    private static readonly KBDLLHOOKSTRUCT KeyA = new() { vkCode = WinVirtualKey.A, time = 1 };

    private static MSLLHOOKSTRUCT At(int x, int y, uint time = AfterFlip) => new() { pt = new WINPOINT { x = x, y = y }, time = time };

    private static bool Move(WindowsInputHandler handler, int x, int y, uint time = AfterFlip, Action<int, int>? setCursorPos = null) =>
        handler.HandleMouse(NativeMethods.WM_MOUSEMOVE, At(x, y, time), setCursorPos ?? NoWarp);

    // back on the local screen after a visit parked at the park point; whether and where the router warps is up to the test
    private static WindowsInputHandler Returned(List<(double X, double Y)> reported, int flipTick = FlipTick)
    {
        var handler = NewHandler(flipTick);
        Subscribe(handler, onMouseMove: (x, y) => reported.Add((x, y)));
        handler.SetWarpState(ParkX, ParkY);
        handler.IsOnVirtualScreen = true;
        Move(handler, ParkX + 5, ParkY); // the visit's first sample, re-based
        handler.IsOnVirtualScreen = false;
        return handler;
    }

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
        Move(handler, ParkX + 5, ParkY); // the visit's first sample, re-based

        Assert.That(Move(handler, ParkX + 50, ParkY), Is.True);
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

        Assert.That(Move(handler, EdgeX, EdgeY), Is.True);
    }

    [Test]
    public void MoveComputedAtParkButHandledAfterTheReturnWarp_IsRebasedOntoTheEdge()
    {
        var reported = new List<(double X, double Y)>();
        var warps = new List<(int X, int Y)>();
        var handler = Returned(reported);
        handler.SetWarpState(EdgeX, EdgeY);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Move(handler, ParkX + 3, ParkY, BeforeFlip, (x, y) => warps.Add((x, y))), Is.True);
            Assert.That(warps, Is.EqualTo([(EdgeX, EdgeY)]));
            Assert.That(reported, Is.EqualTo([((double)EdgeX, (double)EdgeY)]), "the router must hear where the cursor was put");
        }
    }

    [Test]
    public void EntryPark_ASampleComputedBeforeItReportsNoDelta()
    {
        var deltas = new List<(double X, double Y)>();
        var handler = NewHandler();
        Subscribe(handler, onMouseDelta: (x, y) => deltas.Add((x, y)));
        handler.SetWarpState(EdgeX, EdgeY);
        handler.IsOnVirtualScreen = true;
        handler.SetWarpState(ParkX, ParkY); // the entry's park

        Move(handler, EdgeX - 2, EdgeY); // computed before the park landed
        Move(handler, ParkX + 3, ParkY);

        Assert.That(deltas, Is.EqualTo([(3.0, 0.0)]));
    }

    [Test]
    public void MidVisitPark_ASampleComputedBeforeItReportsNoDelta()
    {
        var deltas = new List<(double X, double Y)>();
        var handler = NewHandler();
        Subscribe(handler, onMouseDelta: (x, y) => deltas.Add((x, y)));
        handler.SetWarpState(ParkX, ParkY);
        handler.IsOnVirtualScreen = true;
        Move(handler, ParkX + 1, ParkY); // the visit's first sample, re-based
        Move(handler, ParkX + 3, ParkY);
        handler.SetWarpState(PinX, PinY); // parked on a screen adopted mid-visit

        Move(handler, ParkX + 5, ParkY); // computed before that park landed
        Move(handler, PinX + 4, PinY);

        Assert.That(deltas, Is.EqualTo([(3.0, 0.0), (4.0, 0.0)]));
    }

    [Test]
    public void MidVisitPark_AnEchoOfTheOldParkIsSwallowedAndTheRebaseKept()
    {
        var deltas = new List<(double X, double Y)>();
        var warps = new List<(int X, int Y)>();
        var handler = NewHandler();
        Subscribe(handler, onMouseDelta: (x, y) => deltas.Add((x, y)));
        handler.SetWarpState(ParkX, ParkY);
        handler.IsOnVirtualScreen = true;
        Move(handler, ParkX + 1, ParkY); // the visit's first sample, re-based
        handler.SetWarpState(PinX, PinY);

        Move(handler, ParkX, ParkY, setCursorPos: (x, y) => warps.Add((x, y))); // a late echo of the old park
        Move(handler, ParkX + 5, ParkY, setCursorPos: (x, y) => warps.Add((x, y))); // computed before the new park
        Move(handler, PinX + 4, PinY);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deltas, Is.EqualTo([(4.0, 0.0)]));
            Assert.That(warps, Is.EqualTo([(PinX, PinY)]), "only the re-base warps");
        }
    }

    [Test]
    public void EntryWithoutATargetChange_ItsFirstSampleReportsNoDelta()
    {
        // returned by a snap-back to the park point, then re-entered: no warp moves the target, the flip alone marks
        var deltas = new List<(double X, double Y)>();
        var handler = Returned([]);
        Subscribe(handler, onMouseDelta: (x, y) => deltas.Add((x, y)));
        handler.IsOnVirtualScreen = true;

        Move(handler, EdgeX, EdgeY); // measured on the local screen
        Move(handler, ParkX + 2, ParkY);

        Assert.That(deltas, Is.EqualTo([(2.0, 0.0)]));
    }

    [Test]
    public void TwoParksWithNoSampleBetween_TheOldestPointIsStillAnEcho()
    {
        var deltas = new List<(double X, double Y)>();
        var handler = NewHandler();
        Subscribe(handler, onMouseDelta: (x, y) => deltas.Add((x, y)));
        handler.SetWarpState(ParkX, ParkY);
        handler.IsOnVirtualScreen = true;
        Move(handler, ParkX + 1, ParkY); // the visit's first sample, re-based
        handler.SetWarpState(PinX, PinY);
        handler.SetWarpState(EdgeX, EdgeY);

        Move(handler, ParkX, ParkY); // a late echo of the first park
        Move(handler, ParkX, ParkY);
        Move(handler, EdgeX - 3, EdgeY); // the re-base

        Move(handler, EdgeX - 5, EdgeY);
        Assert.That(deltas, Is.EqualTo([(-5.0, 0.0)]));
    }

    [Test]
    public void ReturnThenReEntryBeforeAnySample_AnEchoOfTheReturnEdgeIsSwallowed()
    {
        var deltas = new List<(double X, double Y)>();
        var handler = Returned([]);
        Subscribe(handler, onMouseDelta: (x, y) => deltas.Add((x, y)));
        handler.SetWarpState(EdgeX, EdgeY); // the return
        handler.IsOnVirtualScreen = true;
        handler.SetWarpState(PinX, PinY); // an instant re-entry's park

        Move(handler, EdgeX, EdgeY); // a late echo of the return warp
        Move(handler, EdgeX - 3, EdgeY); // computed between the two warps: the re-base
        Move(handler, PinX + 2, PinY);

        Assert.That(deltas, Is.EqualTo([(2.0, 0.0)]));
    }

    [Test]
    public void ReturnWithoutAWarp_EveryMovePassesThrough()
    {
        var reported = new List<(double X, double Y)>();
        var handler = Returned(reported);

        using (Assert.EnterMultipleScope())
        {
            // exactly the park point too: an app clipping the cursor to the centre must stay usable
            Assert.That(Move(handler, ParkX, ParkY), Is.False);
            Assert.That(Move(handler, ParkX, ParkY), Is.False);
            Assert.That(Move(handler, ParkX, ParkY), Is.False);
            Assert.That(Move(handler, ParkX + 3, ParkY), Is.False);
            Assert.That(reported, Is.EqualTo([((double)ParkX + 3, (double)ParkY)]));
        }
    }

    [Test]
    public void RouterPreemptedBeforeItsWarp_NextMoveIsRebasedOntoTheEdge()
    {
        var reported = new List<(double X, double Y)>();
        var warps = new List<(int X, int Y)>();
        var handler = Returned(reported);
        Move(handler, ParkX + 3, ParkY);
        Move(handler, ParkX + 6, ParkY);
        Move(handler, ParkX + 9, ParkY);
        handler.SetWarpState(EdgeX, EdgeY);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Move(handler, ParkX + 12, ParkY, setCursorPos: (x, y) => warps.Add((x, y))), Is.True);
            Assert.That(warps, Is.EqualTo([(EdgeX, EdgeY)]));
        }
    }

    [Test]
    public void EchoOfTheReturnWarp_DoesNotUseUpTheRebase()
    {
        var handler = Returned([]);
        handler.SetWarpState(EdgeX, EdgeY);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Move(handler, EdgeX, EdgeY), Is.False);
            Assert.That(Move(handler, ParkX + 3, ParkY, BeforeFlip), Is.True);
        }
    }

    [Test]
    public void AfterTheRebase_MovesBackTowardTheParkPassThrough()
    {
        var handler = Returned([]);
        handler.SetWarpState(EdgeX, EdgeY);
        Move(handler, ParkX + 3, ParkY, BeforeFlip);

        using (Assert.EnterMultipleScope())
        {
            for (var x = EdgeX - 100; x > ParkX; x -= 100)
                Assert.That(Move(handler, x, EdgeY), Is.False, $"x={x}");
            Assert.That(Move(handler, ParkX, ParkY), Is.False);
        }
    }

    [Test]
    public void ParkPointEventsAfterTheReturnWarp_TwoAreSwallowedThenOneRebasedThenTheyPass()
    {
        var reported = new List<(double X, double Y)>();
        var warps = new List<(int X, int Y)>();
        var handler = Returned(reported);
        handler.SetWarpState(EdgeX, EdgeY);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Move(handler, ParkX, ParkY), Is.True);
            Assert.That(Move(handler, ParkX, ParkY), Is.True);
            Assert.That(reported, Is.Empty);
            Assert.That(Move(handler, ParkX, ParkY, setCursorPos: (x, y) => warps.Add((x, y))), Is.True);
            Assert.That(warps, Is.EqualTo([(EdgeX, EdgeY)]));
            Assert.That(Move(handler, ParkX, ParkY), Is.False);
        }
    }

    [Test]
    public void ParkPointEventsStampedBeforeTheFlip_AreSwallowedUntilALaterOneArrives()
    {
        var reported = new List<(double X, double Y)>();
        var handler = Returned(reported);
        handler.SetWarpState(EdgeX, EdgeY);
        Move(handler, ParkX + 3, ParkY, BeforeFlip); // the one rebase

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Move(handler, ParkX, ParkY, BeforeFlip), Is.True);
            Assert.That(Move(handler, ParkX, ParkY, FlipTick), Is.True);
            Assert.That(Move(handler, EdgeX - 10, EdgeY), Is.False, "a move stamped after the flip ends the guard");
            Assert.That(Move(handler, ParkX, ParkY, BeforeFlip), Is.False);
            Assert.That(reported, Is.EqualTo([((double)EdgeX, (double)EdgeY), (EdgeX - 10, EdgeY), (ParkX, ParkY)]));
        }
    }

    [Test]
    public void ParkPointEventGuard_HoldsAcrossTheTickWrap()
    {
        var handler = Returned([], flipTick: -1);
        handler.SetWarpState(EdgeX, EdgeY);
        Move(handler, ParkX + 3, ParkY, uint.MaxValue - 1); // the one rebase

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Move(handler, ParkX, ParkY, uint.MaxValue - 1), Is.True, "stamped before the flip");
            Assert.That(Move(handler, ParkX, ParkY, 0), Is.False, "stamped after the flip, past the wrap");
        }
    }

    [Test]
    public void SnapBackToThePark_MovesPassThroughUntilALaterWarpRebasesOne()
    {
        var warps = new List<(int X, int Y)>();
        var handler = Returned([]);
        handler.SetWarpState(ParkX, ParkY); // a disconnect snaps back to the park point itself

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Move(handler, ParkX + 3, ParkY), Is.False);
            Assert.That(Move(handler, ParkX + 6, ParkY), Is.False);
            handler.SetWarpState(PinX, PinY); // a later warp
            Assert.That(Move(handler, PinX + 2, PinY, setCursorPos: (x, y) => warps.Add((x, y))), Is.True);
            Assert.That(warps, Is.EqualTo([(PinX, PinY)]));
            Assert.That(Move(handler, PinX + 4, PinY), Is.False);
        }
    }

    [Test]
    public void RebaseThatRacesAReEntry_ReportsNothing()
    {
        var reported = new List<(double X, double Y)>();
        var handler = Returned(reported);
        handler.SetWarpState(EdgeX, EdgeY);

        var swallowed = Move(handler, ParkX + 3, ParkY, BeforeFlip, (x, _) =>
        {
            if (x != EdgeX) return;
            handler.IsOnVirtualScreen = true;
            handler.SetWarpState(ParkX, ParkY);
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(swallowed, Is.True);
            Assert.That(reported, Is.Empty, "a position reported now would arrive while virtual");
        }
    }

    [Test]
    public void ReEntryBeforeAnySample_KeepsTheOldestPoint()
    {
        // no sample came between the return and the re-entry, so an in-flight one was computed from the park point
        var handler = Returned([]);
        handler.SetWarpState(EdgeX, EdgeY);
        handler.IsOnVirtualScreen = true;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(handler.TakeStraddle(ParkX, ParkY), Is.EqualTo(WindowsInputHandler.Straddle.Echo));
            Assert.That(handler.TakeStraddle(EdgeX - 3, EdgeY), Is.EqualTo(WindowsInputHandler.Straddle.Rebase));
        }
    }

    // a handler on a clock the test advances
    private sealed class Clocked
    {
        public int Now = FlipTick;
        public readonly WindowsInputHandler Handler;
        public readonly List<(double X, double Y)> Deltas = [];
        public readonly List<(double X, double Y)> Moves = [];

        public Clocked()
        {
            Handler = new WindowsInputHandler(NullLogger<WindowsInputHandler>.Instance, TransitionTestHelper.Profile("home")) { TickCount = () => Now };
            Subscribe(Handler, onMouseMove: (x, y) => Moves.Add((x, y)), onMouseDelta: (x, y) => Deltas.Add((x, y)));
        }
    }

    [Test]
    public void EachHookIsReinstalledOnlyAfterFiveSecondsWithoutACallbackOfItsOwn()
    {
        // the keyboard hook has heard nothing since the clock's zero, the mouse hook since the move at FlipTick
        var c = new Clocked();
        Move(c.Handler, EdgeX, EdgeY);

        using (Assert.EnterMultipleScope())
        {
            c.Now = FlipTick + 4_999;
            Assert.That(c.Handler.TakeQuietHooks(), Is.EqualTo(WindowsInputHandler.Hooks.Keyboard), "the mouse moved within five seconds");
            c.Now = FlipTick + 5_000;
            Assert.That(c.Handler.TakeQuietHooks(), Is.EqualTo(WindowsInputHandler.Hooks.Mouse), "the keyboard was just reinstalled");
            c.Now = FlipTick + 9_999;
            Assert.That(c.Handler.TakeQuietHooks(), Is.EqualTo(WindowsInputHandler.Hooks.Keyboard));
            c.Now = FlipTick + 10_000;
            Assert.That(c.Handler.TakeQuietHooks(), Is.EqualTo(WindowsInputHandler.Hooks.Mouse), "still quiet: again");
        }
    }

    [Test]
    public void ReinstallDuringAVisit_TheNextSampleReportsNoDelta()
    {
        // a hook that was gone let the cursor drift off the park point; measuring across that is a bogus jump
        var c = new Clocked();
        c.Handler.SetWarpState(ParkX, ParkY);
        c.Handler.IsOnVirtualScreen = true;
        Move(c.Handler, ParkX + 1, ParkY); // the visit's first sample, re-based
        c.Now += 5_000;
        Assert.That(c.Handler.TakeQuietHooks().HasFlag(WindowsInputHandler.Hooks.Mouse), Is.True, "pre-condition");

        Move(c.Handler, ParkX + 400, ParkY + 300, (uint)c.Now);
        Move(c.Handler, ParkX + 2, ParkY, (uint)c.Now);

        Assert.That(c.Deltas, Is.EqualTo([(2.0, 0.0)]));
    }

    [Test]
    public void ReinstallOnTheLocalScreen_TheNextMoveIsUntouched()
    {
        var c = new Clocked();
        c.Handler.SetWarpState(ParkX, ParkY);
        c.Handler.IsOnVirtualScreen = true;
        c.Handler.IsOnVirtualScreen = false;
        c.Handler.SetWarpState(EdgeX, EdgeY); // the return
        Move(c.Handler, EdgeX - 3, EdgeY, (uint)c.Now); // its re-base
        c.Moves.Clear();
        c.Now += 5_000;
        Assert.That(c.Handler.TakeQuietHooks().HasFlag(WindowsInputHandler.Hooks.Mouse), Is.True, "pre-condition");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Move(c.Handler, ParkX + 7, ParkY, (uint)c.Now), Is.False);
            Assert.That(c.Moves, Is.EqualTo([((double)ParkX + 7, (double)ParkY)]));
        }
    }

    [Test]
    public void LocalMove_PassesThrough()
    {
        Assert.That(Move(NewHandler(), EdgeX, EdgeY), Is.False);
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
