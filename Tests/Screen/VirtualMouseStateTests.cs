using Hydra.Screen;

namespace Tests.Screen;

[TestFixture]
public class VirtualMouseStateTests
{
    private static ScreenRect Screen => new("right", 2560, 0, 2560, 1440, true);

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
        state.EnterScreen(Screen, 2562, 720);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.X, Is.EqualTo(2562));
            Assert.That(state.Y, Is.EqualTo(720));
            Assert.That(state.IsOnVirtualScreen, Is.True);
        }
    }

    [Test]
    public void ApplyDelta_MovesPosition()
    {
        var state = new VirtualMouseState();
        state.EnterScreen(Screen, 2562, 720);
        state.ApplyDelta(10, -5);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.X, Is.EqualTo(2572).Within(1));
            Assert.That(state.Y, Is.EqualTo(715).Within(1));
        }
    }

    [Test]
    public void ApplyDelta_ClampsToScreenBounds()
    {
        var state = new VirtualMouseState();
        state.EnterScreen(Screen, 2562, 720);
        state.ApplyDelta(99999, 99999);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.X, Is.LessThanOrEqualTo(Screen.X + Screen.Width - 1));
            Assert.That(state.Y, Is.LessThanOrEqualTo(Screen.Y + Screen.Height - 1));
        }
    }

    [Test]
    public void ApplyDelta_ClampsToLeftEdge()
    {
        var state = new VirtualMouseState();
        state.EnterScreen(Screen, 2562, 720);
        state.ApplyDelta(-99999, 0);
        Assert.That(state.X, Is.GreaterThanOrEqualTo(Screen.X));
    }

    [Test]
    public void LeaveScreen_ClearsState()
    {
        var state = new VirtualMouseState();
        state.EnterScreen(Screen, 2562, 720);
        state.LeaveScreen();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.IsOnVirtualScreen, Is.False);
            Assert.That(state.CurrentScreen, Is.Null);
        }
    }
}
