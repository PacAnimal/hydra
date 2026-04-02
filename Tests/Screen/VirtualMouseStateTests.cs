using Hydra.Screen;

namespace Tests.Screen;

[TestFixture]
public class VirtualMouseStateTests
{
    private static ScreenRect Screen => new("right", "right", 0, 0, 2560, 1440, IsLocal: false);

    [Test]
    public void IsOnVirtualScreen_InitiallyFalse()
    {
        var state = new VirtualMouseState();
        Assert.That(state.IsOnVirtualScreen, Is.False);
    }

    [Test]
    public void EnterScreen_SetsPosition()
    {
        var state = new VirtualMouseState();
        state.EnterScreen(Screen, [], 2, 720);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.X, Is.EqualTo(2));
            Assert.That(state.Y, Is.EqualTo(720));
            Assert.That(state.IsOnVirtualScreen, Is.True);
        }
    }

    [Test]
    public void ApplyDelta_MovesPosition()
    {
        var state = new VirtualMouseState();
        state.EnterScreen(Screen, [], 2, 720);
        state.ApplyDelta(10, -5);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.X, Is.EqualTo(12).Within(1));
            Assert.That(state.Y, Is.EqualTo(715).Within(1));
        }
    }

    [Test]
    public void ApplyDelta_ClampsToScreenBounds()
    {
        var state = new VirtualMouseState();
        state.EnterScreen(Screen, [], 2, 720);
        state.ApplyDelta(99999, 99999);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.X, Is.LessThanOrEqualTo(Screen.Width - 1));
            Assert.That(state.Y, Is.LessThanOrEqualTo(Screen.Height - 1));
        }
    }

    [Test]
    public void ApplyDelta_ClampsToLeftEdge()
    {
        var state = new VirtualMouseState();
        state.EnterScreen(Screen, [], 2, 720);
        state.ApplyDelta(-99999, 0);
        Assert.That(state.X, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void ApplyDelta_Scale_ReducesMovement()
    {
        var state = new VirtualMouseState();
        state.EnterScreen(Screen, [], 100, 100, scale: 0.5m);
        state.ApplyDelta(100, 0);
        Assert.That(state.X, Is.EqualTo(150).Within(1));
    }

    [Test]
    public void LeaveScreen_ClearsState()
    {
        var state = new VirtualMouseState();
        state.EnterScreen(Screen, [], 2, 720);
        state.LeaveScreen();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.IsOnVirtualScreen, Is.False);
            Assert.That(state.CurrentScreen, Is.Null);
        }
    }

    // -- multi-screen remote layout --

    [Test]
    public void ApplyDelta_MultiScreen_TransitionsToAdjacentScreen()
    {
        // two screens side by side: left (0,0 2560x1440), right (2560,0 2560x1440)
        var left = new ScreenRect("left", "host", 0, 0, 2560, 1440, IsLocal: false);
        var right = new ScreenRect("right", "host", 2560, 0, 2560, 1440, IsLocal: false);
        var all = new List<ScreenRect> { left, right };

        var state = new VirtualMouseState();
        state.EnterScreen(left, all, 2559, 720); // near right edge of left screen

        state.ApplyDelta(5, 0); // cross into right screen

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.CurrentScreen, Is.EqualTo(right));
            Assert.That(state.X, Is.EqualTo(4).Within(1)); // 2559+5 - 2560 = 4
        }
    }

    [Test]
    public void ApplyDelta_DeadZone_ClampsToNearestScreen()
    {
        // T-arrangement: wide bottom (0,0 3840x1440), narrow top (0,-1080 1920x1080)
        // dead zone at right of top (1920-3840, -1080 to 0)
        var bottom = new ScreenRect("bottom", "host", 0, 0, 3840, 1440, IsLocal: false);
        var top = new ScreenRect("top", "host", 0, -1080, 1920, 1080, IsLocal: false);
        var all = new List<ScreenRect> { bottom, top };

        var state = new VirtualMouseState();
        state.EnterScreen(top, all, 1919, 540); // right edge of top screen

        state.ApplyDelta(200, 0); // move into dead zone (right of top, above bottom)

        using (Assert.EnterMultipleScope())
        {
            // should clamp to nearest valid position: right edge of top or top-left of bottom-right area
            // nearest screen is top (right edge) since bottom starts at y=0 and cursor is at y=-540 (global)
            Assert.That(state.X, Is.LessThanOrEqualTo(top.Width - 1));
            Assert.That(state.Y, Is.InRange(0, top.Height - 1));
        }
    }

    [Test]
    public void ApplyDelta_MultiScreen_StaysOnCurrentIfWithinBounds()
    {
        var left = new ScreenRect("left", "host", 0, 0, 2560, 1440, IsLocal: false);
        var right = new ScreenRect("right", "host", 2560, 0, 2560, 1440, IsLocal: false);
        var all = new List<ScreenRect> { left, right };

        var state = new VirtualMouseState();
        state.EnterScreen(left, all, 100, 100);
        state.ApplyDelta(50, 50);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.CurrentScreen, Is.EqualTo(left));
            Assert.That(state.X, Is.EqualTo(150).Within(1));
            Assert.That(state.Y, Is.EqualTo(150).Within(1));
        }
    }

    [Test]
    public void ApplyDelta_MultiScreen_RightToLeft_TransitionsBack()
    {
        // two screens side by side: left (0,0 2560x1440), right (2560,0 2560x1440)
        var left = new ScreenRect("left", "host", 0, 0, 2560, 1440, IsLocal: false);
        var right = new ScreenRect("right", "host", 2560, 0, 2560, 1440, IsLocal: false);
        var all = new List<ScreenRect> { left, right };

        var state = new VirtualMouseState();
        state.EnterScreen(right, all, 1, 720); // near left edge of right screen

        state.ApplyDelta(-5, 0); // cross into left screen

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.CurrentScreen, Is.EqualTo(left));
            Assert.That(state.X, Is.EqualTo(2556).Within(1)); // 2560+1-5 = 2556
        }
    }

    [Test]
    public void ApplyDelta_MultiScreen_TopToBottom_Transitions()
    {
        // two screens stacked: top (0,-1080 1920x1080), bottom (0,0 1920x1080)
        var top = new ScreenRect("top", "host", 0, -1080, 1920, 1080, IsLocal: false);
        var bottom = new ScreenRect("bottom", "host", 0, 0, 1920, 1080, IsLocal: false);
        var all = new List<ScreenRect> { top, bottom };

        var state = new VirtualMouseState();
        state.EnterScreen(top, all, 960, 1079); // near bottom edge of top screen

        state.ApplyDelta(0, 5); // cross into bottom screen

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.CurrentScreen, Is.EqualTo(bottom));
            Assert.That(state.Y, Is.EqualTo(4).Within(1)); // -1080+1079+5 - 0 = 4
        }
    }

    [Test]
    public void ApplyDelta_MultiScreen_BottomToTop_Transitions()
    {
        // two screens stacked: top (0,-1080 1920x1080), bottom (0,0 1920x1080)
        var top = new ScreenRect("top", "host", 0, -1080, 1920, 1080, IsLocal: false);
        var bottom = new ScreenRect("bottom", "host", 0, 0, 1920, 1080, IsLocal: false);
        var all = new List<ScreenRect> { top, bottom };

        var state = new VirtualMouseState();
        state.EnterScreen(bottom, all, 960, 1); // near top edge of bottom screen

        state.ApplyDelta(0, -5); // cross into top screen

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.CurrentScreen, Is.EqualTo(top));
            Assert.That(state.Y, Is.EqualTo(1076).Within(1)); // 0+1-5 - (-1080) = 1076
        }
    }
}
