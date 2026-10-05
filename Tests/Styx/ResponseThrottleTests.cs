using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Common.DTO;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Styx;
using Tests.Setup;

namespace Tests.Styx;

// every endpoint a stranger can call answers no sooner than its throttle, whatever the answer
[TestFixture]
public class ResponseThrottleTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private ThrottleClock _clock = null!;
    private WebApplicationFactory<global::Styx.Program> _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _clock = new ThrottleClock();
        _factory = StyxTestServer.Create(configure: services => services.AddSingleton(new ResponseThrottle(_clock)));
    }

    [TearDown]
    public async Task TearDown() => await _factory.DisposeAsync();

    [TestCase(true)]
    [TestCase(false)]
    public async Task Authenticate(bool valid)
    {
        await using var hub = new HubConnectionBuilder()
            .WithUrl($"{_factory.Server.BaseAddress}relay", options => options.UseTestServer(_factory.Server))
            .Build();
        await hub.StartAsync();
        var authorization = valid ? await StyxTestServer.GenerateAuthorization(Guid.NewGuid()) : "not-an-authorization";

        var answer = hub.InvokeAsync<RelayLoginResponse>("Authenticate", new RelayLogin { Authorization = authorization, HostName = "throttled" });

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

    // holds the answer one tick short of the throttle, then lets it through
    private async Task<T> ReleaseThrottle<T>(Task<T> answer, int seconds)
    {
        var due = await _clock.Started.Task.WaitAsync(Bound);
        Assert.That(due, Is.EqualTo(TimeSpan.FromSeconds(seconds)));

        _clock.Advance(due - TimeSpan.FromTicks(1));
        Assert.That(answer.IsCompleted, Is.False, "already answered one tick short of the throttle");

        _clock.Advance(TimeSpan.FromTicks(1));
        return await answer.WaitAsync(Bound);
    }

    // says when the throttle has started waiting, and for how long, so the test advances only a wait that exists
    private sealed class ThrottleClock : FakeTimeProvider
    {
        public TaskCompletionSource<TimeSpan> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Started.TrySetResult(dueTime);
            return timer;
        }
    }
}
