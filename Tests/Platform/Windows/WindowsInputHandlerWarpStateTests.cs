using Hydra.Platform.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Setup;

namespace Tests.Platform.Windows;

[TestFixture]
public class WindowsInputHandlerWarpStateTests
{
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
