using System.Collections.Concurrent;
using Common;
using Common.DTO;
using Common.Interfaces;
using Hydra.FileTransfer;
using Hydra.Relay;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Tests.Setup;

namespace Tests.Relay;

/// <summary>
/// The relay delivers traffic while a slave is still logging in, so a selection query can arrive before the
/// login completes and must be answered on that connection; one still running when its connection drops is
/// abandoned rather than answered into the next.
/// </summary>
[TestFixture]
public class SlaveSelectionConnectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private const string Master = "master-pc";
    private static readonly string Key = StyxTestServer.GenerateEncryptionKey();
    private static readonly QueryDuringSecondLogin Relay = new();
    private static WebApplicationFactory<global::Styx.Program>? _styx;

    [OneTimeSetUp]
    public static void OneTimeSetUp()
    {
        _styx = StyxTestServer.Create(configure: services =>
            services.Configure<HubOptions>(options => options.AddFilter(Relay)));
        _ = _styx.Server;
    }

    [OneTimeTearDown]
    public static async Task OneTimeTearDown()
    {
        if (_styx != null) await _styx.DisposeAsync();
    }

    [Test]
    public async Task QueryArrivingDuringTheHandshake_IsAnsweredOnThatConnection()
    {
        var finder = new GatedFileSelectionDetector();
        finder.Release(new FileSelectionResult(true, ["/Users/me/report.pdf"]));
        var cfg = await StyxTestServer.BuildNetworkConfig(_styx!, Guid.NewGuid(), Key);
        await using var master = HydraTestClient.Master(_styx!, Master, cfg);
        await master.StartReady();
        using var slave = new TestableSlaveRelay(_styx!, cfg, finder);
        await slave.StartAsync(CancellationToken.None);
        try
        {
            await slave.NextAuthentication(Timeout);
            Assert.That(slave.RequestReconnect(), Is.True);

            await finder.Queried.WaitAsync(Timeout);

            Assert.That(await NextFrom(master, MessageKind.FileSelectionResponse), Does.Contain("report.pdf"));
        }
        finally
        {
            await Stop(slave);
        }
    }

    [Test]
    public async Task ConnectionDroppingDuringAQuery_CancelsIt()
    {
        var finder = new GatedFileSelectionDetector();
        var cfg = await StyxTestServer.BuildNetworkConfig(_styx!, Guid.NewGuid(), Key);
        using var slave = new TestableSlaveRelay(_styx!, cfg, finder);
        await slave.StartAsync(CancellationToken.None);
        try
        {
            await slave.NextAuthentication(Timeout);
            Assert.That(slave.RequestReconnect(), Is.True);
            await finder.Queried.WaitAsync(Timeout);
            await slave.NextAuthentication(Timeout);

            Assert.That(slave.RequestReconnect(), Is.True);

            await finder.Cancelled.WaitAsync(Timeout);
        }
        finally
        {
            finder.Release(new FileSelectionResult(true, []));
            await Stop(slave);
        }
    }

    [Test]
    public async Task RelayDroppingTheLoginDuringAQuery_CancelsIt()
    {
        var finder = new GatedFileSelectionDetector();
        var cfg = await StyxTestServer.BuildNetworkConfig(_styx!, Guid.NewGuid(), Key);
        var drop = Relay.HoldSecondLogin(NetworkConfig.Parse(cfg).Authorization);
        using var slave = new TestableSlaveRelay(_styx!, cfg, finder);
        await slave.StartAsync(CancellationToken.None);
        try
        {
            await slave.NextAuthentication(Timeout);
            Assert.That(slave.RequestReconnect(), Is.True);
            await finder.Queried.WaitAsync(Timeout);

            drop.SetResult();

            await finder.Cancelled.WaitAsync(Timeout);
        }
        finally
        {
            drop.TrySetResult();
            finder.Release(new FileSelectionResult(true, []));
            await Stop(slave);
        }
    }

    // the earlier connection's query can no longer answer, so the master's new one must not wait on it
    [Test]
    public async Task QueryFromTheSameMasterWhileAnEarlierConnectionsRuns_IsAnsweredCopyInProgress()
    {
        var finder = new GatedFileSelectionDetector(ignoresCancellation: true);
        var cfg = await StyxTestServer.BuildNetworkConfig(_styx!, Guid.NewGuid(), Key);
        using var slave = new TestableSlaveRelay(_styx!, cfg, finder);
        await slave.StartAsync(CancellationToken.None);
        try
        {
            await slave.NextAuthentication(Timeout);
            Assert.That(slave.RequestReconnect(), Is.True);
            await finder.Queried.WaitAsync(Timeout);
            await slave.NextAuthentication(Timeout);
            Assert.That(slave.RequestReconnect(), Is.True);
            await finder.Cancelled.WaitAsync(Timeout);
            await slave.NextAuthentication(Timeout);

            await slave.SimulateReceive(Master, MessageKind.FileSelectionQuery, "{}").WaitAsync(Timeout);

            var (targets, _, json) = slave.Snapshot().Single(s => s.Kind == MessageKind.FileSelectionResponse);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(targets, Is.EqualTo([Master]));
                Assert.That(json, Does.Contain(FileSelectionResult.InProgressMessage));
            }
        }
        finally
        {
            finder.Release(new FileSelectionResult(true, []));
            await Stop(slave);
        }
    }

    private static async Task<string> NextFrom(HydraTestClient master, MessageKind kind)
    {
        while (true)
        {
            var (_, received, json) = await master.WaitForNextMessage((int)Timeout.TotalMilliseconds);
            if (received == kind) return json;
        }
    }

    private static async Task Stop(TestableSlaveRelay slave)
    {
        using var giveUp = new CancellationTokenSource(Timeout);
        await slave.StopAsync(giveUp.Token);
    }

    // sends each network's slave a selection query from the master while it logs in for the second time;
    // a held login is then dropped by the relay, once released, instead of completing
    private sealed class QueryDuringSecondLogin : IHubFilter
    {
        private readonly ConcurrentDictionary<string, int> _logins = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _held = new();

        public TaskCompletionSource HoldSecondLogin(string authorization) =>
            _held.GetOrAdd(authorization, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext context, Func<HubInvocationContext, ValueTask<object?>> next)
        {
            if (context.HubMethodName == nameof(IStyxServer.Authenticate) && context.HubMethodArguments[0] is RelayLogin { HostName: not Master } login
                && _logins.AddOrUpdate(login.Authorization, 1, (_, n) => n + 1) == 2)
            {
                var query = await new RelayEncryption(Key).Encrypt(MessageSerializer.Encode(MessageKind.FileSelectionQuery, new FileSelectionQueryMessage()));
                await context.Hub.Clients.Caller.SendAsync(nameof(IStyxClient.Receive), Master, "127.0.0.1", query, context.Context.ConnectionAborted);
                if (_held.TryGetValue(login.Authorization, out var drop))
                {
                    await drop.Task;
                    context.Context.Abort();
                    throw new HubException("dropped by the relay");
                }
            }
            return await next(context);
        }
    }
}
