using Hydra.Platform.Windows;
using Hydra.Screen;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Platform.Windows;

[TestFixture]
public class WindowsInputHandlerWarpStateTests
{
    // SetWarpState/GetLastWarp/RecentreToTarget are written from two real threads in
    // production — the dedicated Win32 hook thread (~900/s) and the router's consumer thread
    // (rarely, at a screen entry/exit) — with no synchronisation other than the internal _warpLock.
    // Every write here uses X == Y, so any read observing X != Y is a torn pair: proof the lock let
    // two writes interleave instead of serialising them. This runs in the default (mac/Linux) lane,
    // same as WinKeyResolverTests — the class under test does no P/Invoke until a method that
    // actually calls one is invoked, and none of these four methods do.
    [Test]
    public void ConcurrentWarpStateAccess_NeverObservesATornPair()
    {
        var handler = new WindowsInputHandler(NullLogger<WindowsInputHandler>.Instance, TransitionTestHelper.Profile("home"));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var token = cts.Token;
        var failure = (string?)null;

        var writer = Task.Run(() =>
        {
            var i = 0;
            while (!token.IsCancellationRequested)
                handler.SetWarpState(++i, i);
        });

        var reader = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                Check(handler.GetLastWarp(), nameof(handler.GetLastWarp));
                Check(handler.RecentreToTarget(), nameof(handler.RecentreToTarget));
            }

            void Check(ScreenPoint pair, string source)
            {
                if (pair.X != pair.Y)
                    failure ??= $"{source} returned a torn pair: ({pair.X}, {pair.Y})";
            }
        });

        Task.WaitAll(writer, reader);
        Assert.That(failure, Is.Null, failure);
    }

    // Each case runs the router's exit steps while one of the hook's warps is in flight, then lands
    // the hook's warp: whatever lands last must be the edge, or the cursor is stranded mid-screen.
    [TestCase(3, 0, TestName = "RecentreCursor_RouterExitLandsMidRecentre_CursorEndsAtEdge")]
    [TestCase(2, 1, TestName = "RecentreCursor_RouterRetargetsMidRecentre_CursorEndsAtEdge")]
    [TestCase(1, 2, TestName = "RecentreCursor_RouterRetargetsAfterFlagCheck_CursorEndsAtEdge")]
    public void RecentreCursor_RouterExitDuringRecentre(int stepsDuringFirstWarp, int stepsDuringSecondWarp)
    {
        var handler = new WindowsInputHandler(NullLogger<WindowsInputHandler>.Instance, TransitionTestHelper.Profile("home"));
        const int parkX = 960, parkY = 540;
        var edge = (X: 1919, Y: 300);
        var cursor = (X: -1, Y: -1);
        handler.SetWarpState(parkX, parkY);
        handler.IsOnVirtualScreen = true;

        // InputRouter.ReturnHome, one step at a time
        var routerSteps = new Queue<Action>([
            () => handler.IsOnVirtualScreen = false,
            () => handler.SetWarpState(edge.X, edge.Y),
            () => cursor = edge,
        ]);
        var warps = 0;

        handler.RecentreCursor((x, y) =>
        {
            var steps = ++warps switch { 1 => stepsDuringFirstWarp, 2 => stepsDuringSecondWarp, _ => 0 };
            for (var i = 0; i < steps && routerSteps.Count > 0; i++) routerSteps.Dequeue()();
            cursor = (x, y);
        });
        while (routerSteps.Count > 0) routerSteps.Dequeue()();

        Assert.That(cursor, Is.EqualTo(edge));
    }
}
