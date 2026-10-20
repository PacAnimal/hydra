using Hydra.Platform.Windows;
using Hydra.Screen;
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

    private static HookedHandler NewHandler(int flipTick = FlipTick) => new(flipTick);

    private static readonly KBDLLHOOKSTRUCT KeyA = WinHook.Hook(WinVirtualKey.A);

    private static MSLLHOOKSTRUCT At(int x, int y, uint time = AfterFlip) => new() { pt = new WINPOINT { x = x, y = y }, time = time };

    private static bool Move(HookedHandler h, int x, int y, uint time = AfterFlip, Action<int, int>? setCursorPos = null) =>
        h.Move(x, y, time, setCursorPos);

    // back on the local screen after a visit parked at the park point; whether and where the router warps is up to the test
    private static HookedHandler Returned(int flipTick = FlipTick)
    {
        var h = NewHandler(flipTick);
        h.Handler.SetWarpState(ParkX, ParkY);
        h.Handler.IsOnVirtualScreen = true;
        Move(h, ParkX + 5, ParkY); // the visit's first sample, re-based
        h.Handler.IsOnVirtualScreen = false;
        return h;
    }

    [Test]
    public void MoveThatReturnsToLocal_IsSwallowed()
    {
        var h = NewHandler();
        h.OnDelta = () =>
        {
            h.Handler.IsOnVirtualScreen = false;
            h.Handler.SetWarpState(EdgeX, EdgeY);
        };
        h.Handler.SetWarpState(ParkX, ParkY);
        h.Handler.IsOnVirtualScreen = true;
        Move(h, ParkX + 5, ParkY); // the visit's first sample, re-based

        Assert.That(Move(h, ParkX + 50, ParkY), Is.True);
    }

    [Test]
    public void MoveThatEntersARemoteScreen_IsSwallowed()
    {
        var h = NewHandler();
        h.OnMove = () =>
        {
            h.Handler.IsOnVirtualScreen = true;
            h.Handler.SetWarpState(ParkX, ParkY);
        };

        Assert.That(Move(h, EdgeX, EdgeY), Is.True);
    }

    [Test]
    public void MoveComputedAtParkButHandledAfterTheReturnWarp_IsRebasedOntoTheEdge()
    {
        var warps = new List<(int X, int Y)>();
        var h = Returned();
        h.Handler.SetWarpState(EdgeX, EdgeY);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Move(h, ParkX + 3, ParkY, BeforeFlip, (x, y) => warps.Add((x, y))), Is.True);
            Assert.That(warps, Is.EqualTo([(EdgeX, EdgeY)]));
            Assert.That(h.Moves, Is.EqualTo([((double)EdgeX, (double)EdgeY)]), "the router must hear where the cursor was put");
        }
    }

    [Test]
    public void EntryPark_ASampleComputedBeforeItReportsNoDelta()
    {
        var h = NewHandler();
        h.Handler.SetWarpState(EdgeX, EdgeY);
        h.Handler.IsOnVirtualScreen = true;
        h.Handler.SetWarpState(ParkX, ParkY); // the entry's park

        Move(h, EdgeX - 2, EdgeY); // computed before the park landed
        Move(h, ParkX + 3, ParkY);

        Assert.That(h.Deltas, Is.EqualTo([(3.0, 0.0)]));
    }

    [Test]
    public void MidVisitPark_ASampleComputedBeforeItReportsNoDelta()
    {
        var h = NewHandler();
        h.Handler.SetWarpState(ParkX, ParkY);
        h.Handler.IsOnVirtualScreen = true;
        Move(h, ParkX + 1, ParkY); // the visit's first sample, re-based
        Move(h, ParkX + 3, ParkY);
        h.Handler.SetWarpState(PinX, PinY); // parked on a screen adopted mid-visit

        Move(h, ParkX + 5, ParkY); // computed before that park landed
        Move(h, PinX + 4, PinY);

        Assert.That(h.Deltas, Is.EqualTo([(3.0, 0.0), (4.0, 0.0)]));
    }

    [Test]
    public void MidVisitPark_AnEchoOfTheOldParkIsSwallowedAndTheRebaseKept()
    {
        var warps = new List<(int X, int Y)>();
        var h = NewHandler();
        h.Handler.SetWarpState(ParkX, ParkY);
        h.Handler.IsOnVirtualScreen = true;
        Move(h, ParkX + 1, ParkY); // the visit's first sample, re-based
        h.Handler.SetWarpState(PinX, PinY);

        Move(h, ParkX, ParkY, setCursorPos: (x, y) => warps.Add((x, y))); // a late echo of the old park
        Move(h, ParkX + 5, ParkY, setCursorPos: (x, y) => warps.Add((x, y))); // computed before the new park
        Move(h, PinX + 4, PinY);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Deltas, Is.EqualTo([(4.0, 0.0)]));
            Assert.That(warps, Is.EqualTo([(PinX, PinY)]), "only the re-base warps");
        }
    }

    [Test]
    public void EntryWithoutATargetChange_ItsFirstSampleReportsNoDelta()
    {
        // returned by a snap-back to the park point, then re-entered: no warp moves the target, the flip alone marks
        var h = Returned();
        h.Handler.IsOnVirtualScreen = true;

        Move(h, EdgeX, EdgeY); // measured on the local screen
        Move(h, ParkX + 2, ParkY);

        Assert.That(h.Deltas, Is.EqualTo([(2.0, 0.0)]));
    }

    [Test]
    public void TwoParksWithNoSampleBetween_TheOldestPointIsStillAnEcho()
    {
        var h = NewHandler();
        h.Handler.SetWarpState(ParkX, ParkY);
        h.Handler.IsOnVirtualScreen = true;
        Move(h, ParkX + 1, ParkY); // the visit's first sample, re-based
        h.Handler.SetWarpState(PinX, PinY);
        h.Handler.SetWarpState(EdgeX, EdgeY);

        Move(h, ParkX, ParkY); // a late echo of the first park
        Move(h, ParkX, ParkY);
        Move(h, EdgeX - 3, EdgeY); // the re-base

        Move(h, EdgeX - 5, EdgeY);
        Assert.That(h.Deltas, Is.EqualTo([(-5.0, 0.0)]));
    }

    [Test]
    public void ReturnThenReEntryBeforeAnySample_AnEchoOfTheReturnEdgeIsSwallowed()
    {
        var h = Returned();
        h.Handler.SetWarpState(EdgeX, EdgeY); // the return
        h.Handler.IsOnVirtualScreen = true;
        h.Handler.SetWarpState(PinX, PinY); // an instant re-entry's park

        Move(h, EdgeX, EdgeY); // a late echo of the return warp
        Move(h, EdgeX - 3, EdgeY); // computed between the two warps: the re-base
        Move(h, PinX + 2, PinY);

        Assert.That(h.Deltas, Is.EqualTo([(2.0, 0.0)]));
    }

    // a visit parked at the park point, returning to the edge; SetCursorPos for the return has not landed yet
    private static HookedHandler ReturningToTheEdge()
    {
        var h = NewHandler();
        h.Handler.SetWarpState(ParkX, ParkY);
        h.Handler.IsOnVirtualScreen = true;
        Move(h, ParkX + 5, ParkY); // the visit's first sample, re-based
        h.Handler.SetReturnState(new ScreenPoint(EdgeX, EdgeY));
        return h;
    }

    [Test]
    public void ParkEchoHandledBeforeTheReturnWarpLands_IsSwallowed()
    {
        var h = ReturningToTheEdge();

        Assert.That(Move(h, ParkX, ParkY, BeforeFlip), Is.True);
    }

    [Test]
    public void MoveComputedAtParkHandledBeforeTheReturnWarpLands_IsRebasedOntoTheReturnPoint()
    {
        var warps = new List<(int X, int Y)>();
        var h = ReturningToTheEdge();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Move(h, ParkX + 3, ParkY, setCursorPos: (x, y) => warps.Add((x, y))), Is.True);
            Assert.That(warps, Is.EqualTo([(EdgeX, EdgeY)]));
            Assert.That(h.Moves, Is.EqualTo([((double)EdgeX, (double)EdgeY)]));
        }
    }

    [Test]
    public void ReturnWithoutAWarp_EveryMovePassesThrough()
    {
        var h = Returned();

        using (Assert.EnterMultipleScope())
        {
            // exactly the park point too: an app clipping the cursor to the centre must stay usable
            Assert.That(Move(h, ParkX, ParkY), Is.False);
            Assert.That(Move(h, ParkX, ParkY), Is.False);
            Assert.That(Move(h, ParkX, ParkY), Is.False);
            Assert.That(Move(h, ParkX + 3, ParkY), Is.False);
            Assert.That(h.Moves, Is.EqualTo([((double)ParkX + 3, (double)ParkY)]));
        }
    }

    [Test]
    public void RouterPreemptedBeforeItsWarp_NextMoveIsRebasedOntoTheEdge()
    {
        var warps = new List<(int X, int Y)>();
        var h = Returned();
        Move(h, ParkX + 3, ParkY);
        Move(h, ParkX + 6, ParkY);
        Move(h, ParkX + 9, ParkY);
        h.Handler.SetWarpState(EdgeX, EdgeY);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Move(h, ParkX + 12, ParkY, setCursorPos: (x, y) => warps.Add((x, y))), Is.True);
            Assert.That(warps, Is.EqualTo([(EdgeX, EdgeY)]));
        }
    }

    [Test]
    public void EchoOfTheReturnWarp_DoesNotUseUpTheRebase()
    {
        var h = Returned();
        h.Handler.SetWarpState(EdgeX, EdgeY);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Move(h, EdgeX, EdgeY), Is.False);
            Assert.That(Move(h, ParkX + 3, ParkY, BeforeFlip), Is.True);
        }
    }

    [Test]
    public void AfterTheRebase_MovesBackTowardTheParkPassThrough()
    {
        var h = Returned();
        h.Handler.SetWarpState(EdgeX, EdgeY);
        Move(h, ParkX + 3, ParkY, BeforeFlip);

        using (Assert.EnterMultipleScope())
        {
            for (var x = EdgeX - 100; x > ParkX; x -= 100)
                Assert.That(Move(h, x, EdgeY), Is.False, $"x={x}");
            Assert.That(Move(h, ParkX, ParkY), Is.False);
        }
    }

    [Test]
    public void ParkPointEventsAfterTheReturnWarp_TwoAreSwallowedThenOneRebasedThenTheyPass()
    {
        var warps = new List<(int X, int Y)>();
        var h = Returned();
        h.Handler.SetWarpState(EdgeX, EdgeY);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Move(h, ParkX, ParkY), Is.True);
            Assert.That(Move(h, ParkX, ParkY), Is.True);
            Assert.That(h.Moves, Is.Empty);
            Assert.That(Move(h, ParkX, ParkY, setCursorPos: (x, y) => warps.Add((x, y))), Is.True);
            Assert.That(warps, Is.EqualTo([(EdgeX, EdgeY)]));
            Assert.That(Move(h, ParkX, ParkY), Is.False);
        }
    }

    [Test]
    public void ParkPointEventsStampedBeforeTheFlip_AreSwallowedUntilALaterOneArrives()
    {
        var h = Returned();
        h.Handler.SetWarpState(EdgeX, EdgeY);
        Move(h, ParkX + 3, ParkY, BeforeFlip); // the one rebase

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Move(h, ParkX, ParkY, BeforeFlip), Is.True);
            Assert.That(Move(h, ParkX, ParkY, FlipTick), Is.True);
            Assert.That(Move(h, EdgeX - 10, EdgeY), Is.False, "a move stamped after the flip ends the guard");
            Assert.That(Move(h, ParkX, ParkY, BeforeFlip), Is.False);
            Assert.That(h.Moves, Is.EqualTo([((double)EdgeX, (double)EdgeY), (EdgeX - 10, EdgeY), (ParkX, ParkY)]));
        }
    }

    [Test]
    public void ParkPointEventGuard_HoldsAcrossTheTickWrap()
    {
        var h = Returned(flipTick: -1);
        h.Handler.SetWarpState(EdgeX, EdgeY);
        Move(h, ParkX + 3, ParkY, uint.MaxValue - 1); // the one rebase

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Move(h, ParkX, ParkY, uint.MaxValue - 1), Is.True, "stamped before the flip");
            Assert.That(Move(h, ParkX, ParkY, 0), Is.False, "stamped after the flip, past the wrap");
        }
    }

    [Test]
    public void SnapBackToThePark_MovesPassThroughUntilALaterWarpRebasesOne()
    {
        var warps = new List<(int X, int Y)>();
        var h = Returned();
        h.Handler.SetWarpState(ParkX, ParkY); // a disconnect snaps back to the park point itself

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Move(h, ParkX + 3, ParkY), Is.False);
            Assert.That(Move(h, ParkX + 6, ParkY), Is.False);
            h.Handler.SetWarpState(PinX, PinY); // a later warp
            Assert.That(Move(h, PinX + 2, PinY, setCursorPos: (x, y) => warps.Add((x, y))), Is.True);
            Assert.That(warps, Is.EqualTo([(PinX, PinY)]));
            Assert.That(Move(h, PinX + 4, PinY), Is.False);
        }
    }

    [Test]
    public void RebaseThatRacesAReEntry_ReportsNothing()
    {
        var h = Returned();
        h.Handler.SetWarpState(EdgeX, EdgeY);

        var swallowed = Move(h, ParkX + 3, ParkY, BeforeFlip, (x, _) =>
        {
            if (x != EdgeX) return;
            h.Handler.IsOnVirtualScreen = true;
            h.Handler.SetWarpState(ParkX, ParkY);
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(swallowed, Is.True);
            Assert.That(h.Moves, Is.Empty, "a position reported now would arrive while virtual");
        }
    }

    [Test]
    public void ReEntryBeforeAnySample_KeepsTheOldestPoint()
    {
        // no sample came between the return and the re-entry, so an in-flight one was computed from the park point
        var h = Returned();
        h.Handler.SetWarpState(EdgeX, EdgeY);
        h.Handler.IsOnVirtualScreen = true;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Handler.TakeStraddle(ParkX, ParkY), Is.EqualTo(WindowsInputHandler.Straddle.Echo));
            Assert.That(h.Handler.TakeStraddle(EdgeX - 3, EdgeY), Is.EqualTo(WindowsInputHandler.Straddle.Rebase));
        }
    }

    [Test]
    public void EachHookIsReinstalledOnlyAfterFiveSecondsWithoutACallbackOfItsOwn()
    {
        // the keyboard hook has heard nothing since the clock's zero, the mouse hook since the move at FlipTick
        var c = NewHandler();
        Move(c, EdgeX, EdgeY);

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
        var c = NewHandler();
        c.Handler.SetWarpState(ParkX, ParkY);
        c.Handler.IsOnVirtualScreen = true;
        Move(c, ParkX + 1, ParkY); // the visit's first sample, re-based
        c.Now += 5_000;
        Assert.That(c.Handler.TakeQuietHooks().HasFlag(WindowsInputHandler.Hooks.Mouse), Is.True, "pre-condition");

        Move(c, ParkX + 400, ParkY + 300, (uint)c.Now);
        Move(c, ParkX + 2, ParkY, (uint)c.Now);

        Assert.That(c.Deltas, Is.EqualTo([(2.0, 0.0)]));
    }

    [Test]
    public void ReinstallOnTheLocalScreen_TheNextMoveIsUntouched()
    {
        var c = NewHandler();
        c.Handler.SetWarpState(ParkX, ParkY);
        c.Handler.IsOnVirtualScreen = true;
        c.Handler.IsOnVirtualScreen = false;
        c.Handler.SetWarpState(EdgeX, EdgeY); // the return
        Move(c, EdgeX - 3, EdgeY, (uint)c.Now); // its re-base
        c.Moves.Clear();
        c.Now += 5_000;
        Assert.That(c.Handler.TakeQuietHooks().HasFlag(WindowsInputHandler.Hooks.Mouse), Is.True, "pre-condition");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Move(c, ParkX + 7, ParkY, (uint)c.Now), Is.False);
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
        var h = NewHandler();
        h.OnButton = () => h.Handler.IsOnVirtualScreen = !startOnVirtualScreen;
        h.Handler.IsOnVirtualScreen = startOnVirtualScreen;

        Assert.That(h.Handler.HandleMouse(NativeMethods.WM_LBUTTONDOWN, At(EdgeX, EdgeY), HookedHandler.NoWarp), Is.EqualTo(swallowed));
    }

    [TestCase(true, false, TestName = "KeyWhileReturningToLocal_PassesThrough")]
    [TestCase(false, true, TestName = "KeyWhileEnteringARemoteScreen_IsSwallowed")]
    [Platform("Win")] // key resolution needs ToUnicodeEx
    public void KeyWhileCrossingScreens_FollowsTheFinalRead(bool startOnVirtualScreen, bool swallowed)
    {
        var h = NewHandler();
        var keys = 0;
        h.OnKey = () =>
        {
            keys++;
            h.Handler.IsOnVirtualScreen = !startOnVirtualScreen;
        };
        h.Handler.IsOnVirtualScreen = startOnVirtualScreen;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(h.Handler.HandleKey(NativeMethods.WM_KEYDOWN, KeyA), Is.EqualTo(swallowed));
            Assert.That(keys, Is.Positive, "the key must reach the router for the test to mean anything");
        }
    }
}
