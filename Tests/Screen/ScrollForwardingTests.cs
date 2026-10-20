using Hydra.Mouse;
using Hydra.Relay;
using Tests.Setup;

namespace Tests.Screen;

// a scroll goes to the screen being visited, and nowhere while the cursor is home
[TestFixture]
public class ScrollForwardingTests : ServicesFixture
{
    private static readonly MouseScrollEvent Down = new(0, -120);

    [Test]
    public async Task WhileVisiting_TheScrollGoesToTheVisitedHost()
    {
        var (platform, relay, _) = await Services.Visiting(TransitionTestHelper.CreateService());

        platform.FireMouseScroll(Down);

        Assert.That(relay.Sent.Of<MouseScrollMessage>(MessageKind.MouseScroll, "remote"), Is.EqualTo([new MouseScrollMessage(0, -120)]));
    }

    [Test]
    public async Task AtHome_TheScrollIsNotForwarded()
    {
        var (platform, relay, _) = await Services.StartOnline(TransitionTestHelper.CreateService());

        platform.FireMouseScroll(Down);

        Assert.That(relay.Sent.Of<MouseScrollMessage>(MessageKind.MouseScroll), Is.Empty);
    }
}
