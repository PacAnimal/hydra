using System.Collections.Concurrent;
using Common;
using Common.DTO;
using Common.Interfaces;
using Hydra.FileTransfer;
using Hydra.Relay;
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
public class SlaveSelectionConnectionTests : StyxFixtureBase
{
    private const string Master = "master-pc";
    private static readonly string Key = NetworkConfig.NewEncryptionKey();
    private readonly QueryDuringSecondLogin _relay = new();
    private readonly StartedServices _services = new();
    private readonly List<GatedFileSelectionDetector> _finders = [];
    private readonly List<Gate> _drops = [];
    private readonly List<TestableSlaveRelay> _slaves = [];

    protected override void ConfigureRelay(IServiceCollection services) =>
        services.Configure<HubOptions>(options => options.AddFilter(_relay));

    // a held query or login would keep its slave from stopping
    [TearDown]
    public async Task TearDown()
    {
        foreach (var drop in _drops) drop.Release();
        foreach (var finder in _finders) finder.Release(new FileSelectionResult(true, []));
        _drops.Clear();
        _finders.Clear();
        try
        {
            await _services.StopAll();
        }
        finally
        {
            foreach (var slave in _slaves) slave.Dispose();
            _slaves.Clear();
        }
    }

    private async Task<TestableSlaveRelay> StartSlave(string cfg, GatedFileSelectionDetector finder)
    {
        _finders.Add(finder);
        var slave = new TestableSlaveRelay(Factory, cfg, finder);
        _slaves.Add(slave);
        return await _services.Start(slave);
    }

    [Test]
    public async Task QueryArrivingDuringTheHandshake_IsAnsweredOnThatConnection()
    {
        var finder = new GatedFileSelectionDetector();
        finder.Release(new FileSelectionResult(true, ["/Users/me/report.pdf"]));
        var cfg = await StyxTestServer.BuildNetworkConfig(Factory, Guid.NewGuid(), Key);
        await using var master = HydraTestClient.Master(Factory, Master, cfg);
        await master.StartReady();
        var slave = await StartSlave(cfg, finder);
        await slave.NextAuthentication();
        Assert.That(slave.RequestReconnect(), Is.True);

        await finder.Queried.WaitAsync(Bound);

        Assert.That(await master.WaitForMessage(MessageKind.FileSelectionResponse), Does.Contain("report.pdf"));
    }

    [Test]
    public async Task ConnectionDroppingDuringAQuery_CancelsIt()
    {
        var finder = new GatedFileSelectionDetector();
        var cfg = await StyxTestServer.BuildNetworkConfig(Factory, Guid.NewGuid(), Key);
        var slave = await StartSlave(cfg, finder);
        await slave.NextAuthentication();
        Assert.That(slave.RequestReconnect(), Is.True);
        await finder.Queried.WaitAsync(Bound);
        await slave.NextAuthentication();

        Assert.That(slave.RequestReconnect(), Is.True);

        await finder.Cancelled.WaitAsync(Bound);
    }

    [Test]
    public async Task RelayDroppingTheLoginDuringAQuery_CancelsIt()
    {
        var finder = new GatedFileSelectionDetector();
        var cfg = await StyxTestServer.BuildNetworkConfig(Factory, Guid.NewGuid(), Key);
        var drop = _relay.HoldSecondLogin(NetworkConfig.Parse(cfg).Authorization);
        _drops.Add(drop);
        var slave = await StartSlave(cfg, finder);
        await slave.NextAuthentication();
        Assert.That(slave.RequestReconnect(), Is.True);
        await finder.Queried.WaitAsync(Bound);

        drop.Release();

        await finder.Cancelled.WaitAsync(Bound);
    }

    // the earlier connection's query can no longer answer, so the master's new one must not wait on it
    [Test]
    public async Task QueryFromTheSameMasterWhileAnEarlierConnectionsRuns_IsAnsweredCopyInProgress()
    {
        var finder = new GatedFileSelectionDetector(ignoresCancellation: true);
        var cfg = await StyxTestServer.BuildNetworkConfig(Factory, Guid.NewGuid(), Key);
        var slave = await StartSlave(cfg, finder);
        await slave.NextAuthentication();
        Assert.That(slave.RequestReconnect(), Is.True);
        await finder.Queried.WaitAsync(Bound);
        await slave.NextAuthentication();
        Assert.That(slave.RequestReconnect(), Is.True);
        await finder.Cancelled.WaitAsync(Bound);
        await slave.NextAuthentication();

        await slave.SimulateReceive(Master, MessageKind.FileSelectionQuery, "{}").WaitAsync(Bound);

        var (targets, _, json) = slave.Snapshot().Single(s => s.Kind == MessageKind.FileSelectionResponse);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(targets, Is.EqualTo([Master]));
            Assert.That(json, Does.Contain(FileSelectionResult.InProgressMessage));
        }
    }

    // sends each network's slave a selection query from the master while it logs in for the second time;
    // a held login is then dropped by the relay, once released, instead of completing
    private sealed class QueryDuringSecondLogin : IHubFilter
    {
        private readonly ConcurrentDictionary<string, int> _logins = new();
        private readonly ConcurrentDictionary<string, Gate> _held = new();

        public Gate HoldSecondLogin(string authorization) => _held.GetOrAdd(authorization, _ => new Gate(held: true));

        public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext context, Func<HubInvocationContext, ValueTask<object?>> next)
        {
            if (context.HubMethodName == nameof(IStyxServer.Authenticate) && context.HubMethodArguments[0] is RelayLogin { HostName: not Master } login
                && _logins.AddOrUpdate(login.Authorization, 1, (_, n) => n + 1) == 2)
            {
                var query = await new RelayEncryption(Key).Encrypt(MessageSerializer.Encode(MessageKind.FileSelectionQuery, new FileSelectionQueryMessage()));
                await context.Hub.Clients.Caller.SendAsync(nameof(IStyxClient.Receive), Master, "127.0.0.1", query, context.Context.ConnectionAborted);
                if (_held.TryGetValue(login.Authorization, out var drop))
                {
                    await drop.Passed();
                    context.Context.Abort();
                    throw new HubException("dropped by the relay");
                }
            }
            return await next(context);
        }
    }
}
