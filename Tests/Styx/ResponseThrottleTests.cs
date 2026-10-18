using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Common.DTO;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Styx;
using Tests.Setup;

namespace Tests.Styx;

// every endpoint a stranger can call answers no sooner than its throttle, whatever the answer
[TestFixture]
public class ResponseThrottleTests
{
    private StartedTimers _clock = null!;
    private WebApplicationFactory<global::Styx.Program> _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _clock = new StartedTimers();
        _factory = StyxTestServer.Create(configure: services => services.AddSingleton(new ResponseThrottle(_clock)));
    }

    [TearDown]
    public async Task TearDown() => await _factory.DisposeAsync();

    [TestCase(true)]
    [TestCase(false)]
    public async Task Authenticate(bool valid)
    {
        await using var client = new TestStyxClient();
        await client.ConnectRaw(_factory);
        var authorization = valid ? await StyxTestServer.GenerateAuthorization(Guid.NewGuid()) : "not-an-authorization";

        var answer = client.Server!.Authenticate(new RelayLogin { Authorization = authorization, HostName = "throttled" });

        Assert.That((await ReleaseThrottle(answer, Constants.AuthThrottleSeconds)).Authenticated, Is.EqualTo(valid));
    }

    [TestCase(StyxTestServer.TestPassword, HttpStatusCode.OK)]
    [TestCase("wrong-password", HttpStatusCode.Unauthorized)]
    public async Task NetworkConfig(string password, HttpStatusCode expected)
    {
        using var http = _factory.CreateClient();

        var answer = http.PostAsJsonAsync("/api/network-config", new { Password = password });

        Assert.That((await ReleaseThrottle(answer, Constants.NetworkConfigThrottleSeconds)).StatusCode, Is.EqualTo(expected));
    }

    [Test]
    public async Task Status()
    {
        using var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-an-authorization");

        var answer = http.GetAsync("/api/status");

        Assert.That((await ReleaseThrottle(answer, Constants.StatusThrottleSeconds)).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // the holder of a valid token is no stranger: the clock never moves, so waiting on the throttle would hang here
    [TestCase(true)]
    [TestCase(false)]
    public async Task Status_WithAValidToken_IsAnsweredAtOnce(bool v1)
    {
        using var http = _factory.CreateClient();
        var token = v1 ? await StyxTestServer.GenerateAuthorization(Guid.NewGuid()) : StyxTestServer.MintToken().ToString();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var answer = await http.GetAsync("/api/status").WaitAsync(Bound);

        Assert.That(answer.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    // holds the answer one tick short of the throttle, then lets it through
    private async Task<T> ReleaseThrottle<T>(Task<T> answer, int seconds)
    {
        var due = await _clock.Started.Next((int)Bound.TotalMilliseconds, "the throttle");
        Assert.That(due, Is.EqualTo(TimeSpan.FromSeconds(seconds)));

        _clock.Advance(due - TimeSpan.FromTicks(1));
        Assert.That(answer.IsCompleted, Is.False, "already answered one tick short of the throttle");

        _clock.Advance(TimeSpan.FromTicks(1));
        return await answer.WaitAsync(Bound);
    }
}
