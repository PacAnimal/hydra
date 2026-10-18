using Hydra.Mouse;
using Hydra.Relay;
using Tests.Setup;

namespace Tests.Screen;

// a scroll goes to the screen being visited, and nowhere while the cursor is home
[TestFixture]
public class ScrollForwardingTests
{
    private static readonly MouseScrollEvent Down = new(0, -120);
    private readonly StartedServices _services = new();

    [TearDown]
    public Task TearDown() => _services.StopAll();

    [Test]
    public async Task WhileVisiting_TheScrollGoesToTheVisitedHost()
    {
        var (platform, relay, _) = await _services.Visiting(TransitionTestHelper.CreateService());

        platform.FireMouseScroll(Down);

        Assert.That(relay.Sent<MouseScrollMessage>(MessageKind.MouseScroll, "remote"), Is.EqualTo([new MouseScrollMessage(0, -120)]));
    }

    [Test]
    public async Task AtHome_TheScrollIsNotForwarded()
    {
        var (platform, relay, _) = await _services.StartOnline(TransitionTestHelper.CreateService());

        platform.FireMouseScroll(Down);

        Assert.That(relay.Sent<MouseScrollMessage>(MessageKind.MouseScroll), Is.Empty);
    }
}
