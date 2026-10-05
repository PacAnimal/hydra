using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Hydra.Relay;
using Microsoft.Extensions.Time.Testing;

namespace Tests.Relay;

[TestFixture]
public class RelaySocketConnectorTests
{
    [Test]
    public void OrderByInterfacePreference_UsesConfiguredOrder_AndPreservesTies()
    {
        var wifiFirst = IPAddress.Parse("192.168.1.21");
        var unknown = IPAddress.Parse("10.0.0.10");
        var ethernetFirst = IPAddress.Parse("192.168.1.129");
        var ethernetSecond = IPAddress.Parse("192.168.1.130");
        var interfaces = new Dictionary<IPAddress, string>
        {
            [wifiFirst] = "en0",
            [ethernetFirst] = "en7",
            [ethernetSecond] = "en7"
        };

        var ordered = RelayAddressPreference.OrderByInterfacePreference(
            [wifiFirst, unknown, ethernetFirst, ethernetSecond],
            address => interfaces.GetValueOrDefault(address),
            new Dictionary<string, int> { ["en7"] = 1, ["en0"] = 7 });

        Assert.That(ordered, Is.EqualTo([ethernetFirst, ethernetSecond, wifiFirst, unknown]));
    }

    // asking the OS spawns a process on macOS and Linux, and one route has no rival
    [Test]
    public async Task OrderAsync_AddressesOnOneRoute_KeepTheirOrderWithoutAskingThePreferences()
    {
        var queries = 0;

        var ordered = await RelayAddressPreference.OrderAsync([IPAddress.IPv6Loopback, IPAddress.Loopback], _ => "lo0",
            async _ =>
            {
                queries++;
                await Task.CompletedTask;
                return new Dictionary<string, int>();
            },
            CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ordered, Is.EqualTo([IPAddress.IPv6Loopback, IPAddress.Loopback]));
            Assert.That(queries, Is.Zero);
        }
    }

    [Test]
    public async Task OrderAsync_AddressesOnDifferentRoutes_FollowThePreferences()
    {
        var wifi = IPAddress.Parse("192.168.1.21");
        var ethernet = IPAddress.Parse("192.168.1.129");
        var interfaces = new Dictionary<IPAddress, string> { [wifi] = "en0", [ethernet] = "en7" };

        var ordered = await RelayAddressPreference.OrderAsync([wifi, ethernet], interfaces.GetValueOrDefault,
            async _ =>
            {
                await Task.CompletedTask;
                return new Dictionary<string, int> { ["en7"] = 1, ["en0"] = 7 };
            },
            CancellationToken.None);

        Assert.That(ordered, Is.EqualTo([ethernet, wifi]));
    }

    [Test]
    public void ParseMacServiceOrder_MapsEnabledDevicesOnly()
    {
        const string output = """
            An asterisk (*) denotes that a network service is disabled.
            (1) USB Ethernet
            (Hardware Port: USB Ethernet, Device: en7)
            (*) Wi-Fi
            (Hardware Port: Wi-Fi, Device: en0)
            (3) Thunderbolt Bridge
            (Hardware Port: Thunderbolt Bridge, Device: bridge0)
            """;

        var preferences = RelayAddressPreference.ParseMacServiceOrder(output);

        Assert.That(preferences, Is.EqualTo(new Dictionary<string, int> { ["en7"] = 1, ["bridge0"] = 3 }));
    }

    [Test]
    public void ParseLinuxDefaultRoutes_UsesLowestMetric_ThenRouteOrder()
    {
        const string output = """
            default via 192.168.1.1 dev wlan0 proto dhcp metric 600
            default via 192.168.1.1 dev eth0 proto dhcp metric 100
            default via fe80::1 dev eth0 proto ra metric 1024
            """;

        var preferences = RelayAddressPreference.ParseLinuxDefaultRoutes(output);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preferences["eth0"], Is.LessThan(preferences["wlan0"]));
            Assert.That(preferences, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public void PreferencesFromMetrics_UsesLowestMetricPerInterface()
    {
        InterfaceMetric[] metrics = [new(12, 25), new(7, 5), new(8, 50)];
        var names = new Dictionary<int, string> { [12] = "Wi-Fi", [7] = "Ethernet", [8] = "Ethernet" };

        var preferences = RelayAddressPreference.PreferencesFromMetrics(metrics, index => names.GetValueOrDefault(index));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preferences["Ethernet"], Is.EqualTo(5));
            Assert.That(preferences["Wi-Fi"], Is.EqualTo(25));
            Assert.That(preferences, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public void PreferencesFromMetrics_RealInterfaceResolution_SkipsUnsupportedFamilyWithoutThrowing()
    {
        // Exercises the real NetworkInterfaceLookup.FindByIndex (no fake resolver): some local interfaces commonly
        // don't support one address family (IPv6 disabled, an IPv4-less tunnel adapter, etc.), and
        // that must be skipped per-interface rather than aborting the whole lookup. Real indices vary
        // per machine, so this only asserts it never throws — the regression it guards against is a
        // NetworkInformationException escaping and discarding every interface's preference, not a
        // specific mapping.
        InterfaceMetric[] metrics = [new(1, 10), new(2, 20), new(3, 30), new(999999, 40)];

        Assert.DoesNotThrow(() => RelayAddressPreference.PreferencesFromMetrics(metrics));
    }

    [Test]
    public async Task OrderAsync_TwoDialsOfOneAttempt_ShareOneQuery()
    {
        var query = new CountingPreferences();
        var cache = new InterfacePreferenceCache(query.Run, new FakeTimeProvider());

        var negotiate = await RelayAddressPreference.OrderAsync([Wifi, Ethernet], Interfaces.GetValueOrDefault, cache.GetAsync, CancellationToken.None);
        var webSocket = await RelayAddressPreference.OrderAsync([Wifi, Ethernet], Interfaces.GetValueOrDefault, cache.GetAsync, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(negotiate, Is.EqualTo([Ethernet, Wifi]));
            Assert.That(webSocket, Is.EqualTo([Ethernet, Wifi]));
            Assert.That(query.Runs, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task InterfacePreferenceCache_NetworkChange_QueriesAfresh()
    {
        var query = new CountingPreferences();
        var cache = new InterfacePreferenceCache(query.Run, new FakeTimeProvider());

        await cache.GetAsync(CancellationToken.None);
        cache.Invalidate();
        await cache.GetAsync(CancellationToken.None);

        Assert.That(query.Runs, Is.EqualTo(2));
    }

    // a host without change notifications (Linux without netlink) must still dial, relying on the lifetime alone
    [Test]
    public async Task InterfacePreferenceCache_UnavailableNetworkChangeEvents_FallsBackToTheLifetime()
    {
        var query = new CountingPreferences();
        var time = new FakeTimeProvider();
        var cache = InterfacePreferenceCache.Create(query.Run, time, _ => throw new NetworkInformationException());

        await cache.GetAsync(CancellationToken.None);
        time.Advance(InterfacePreferenceCache.Lifetime);
        await cache.GetAsync(CancellationToken.None);

        Assert.That(query.Runs, Is.EqualTo(2));
    }

    [Test]
    public async Task InterfacePreferenceCache_NetworkChangeEvent_Invalidates()
    {
        var query = new CountingPreferences();
        Action? changed = null;
        var cache = InterfacePreferenceCache.Create(query.Run, new FakeTimeProvider(), invalidate => changed = invalidate);

        await cache.GetAsync(CancellationToken.None);
        changed!();
        await cache.GetAsync(CancellationToken.None);

        Assert.That(query.Runs, Is.EqualTo(2));
    }

    // a metric or service-order edit need not change any address, so no event reports it
    [Test]
    public async Task InterfacePreferenceCache_OutlivedAnswer_QueriesAfresh()
    {
        var query = new CountingPreferences();
        var time = new FakeTimeProvider();
        var cache = new InterfacePreferenceCache(query.Run, time);

        await cache.GetAsync(CancellationToken.None);
        time.Advance(InterfacePreferenceCache.Lifetime - TimeSpan.FromSeconds(1));
        await cache.GetAsync(CancellationToken.None);
        var withinLifetime = query.Runs;
        time.Advance(TimeSpan.FromSeconds(1));
        await cache.GetAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(withinLifetime, Is.EqualTo(1));
            Assert.That(query.Runs, Is.EqualTo(2));
        }
    }

    // an empty answer is a failed or timed-out query, not a machine without preferences
    [Test]
    public async Task InterfacePreferenceCache_EmptyAnswer_IsAskedAgain()
    {
        var query = new CountingPreferences { Answer = new Dictionary<string, int>() };
        var cache = new InterfacePreferenceCache(query.Run, new FakeTimeProvider());

        await cache.GetAsync(CancellationToken.None);
        await cache.GetAsync(CancellationToken.None);

        Assert.That(query.Runs, Is.EqualTo(2));
    }

    // the query is shared, so one dial giving up must not fail the dial waiting beside it
    [Test]
    public async Task InterfacePreferenceCache_CancelledCaller_LeavesTheSharedQueryRunning()
    {
        var release = new TaskCompletionSource<IReadOnlyDictionary<string, int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        var cache = new InterfacePreferenceCache(_ =>
        {
            runs++;
            return release.Task;
        }, new FakeTimeProvider());
        using var giveUp = new CancellationTokenSource();

        var abandoned = cache.GetAsync(giveUp.Token);
        var waiting = cache.GetAsync(CancellationToken.None);
        await giveUp.CancelAsync();
        release.SetResult(new Dictionary<string, int> { ["en7"] = 1 });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(async () => await abandoned, Throws.InstanceOf<OperationCanceledException>());
            Assert.That(await waiting, Does.ContainKey("en7"));
            Assert.That(runs, Is.EqualTo(1));
        }
    }

    [Test]
    public void FindRouteInterface_Loopback_IsTheLoopbackInterface() =>
        Assert.That(RelayAddressPreference.FindRouteInterface(IPAddress.Loopback, 9),
            Is.EqualTo(NetworkInterfaceLookup.FindByAddress(IPAddress.Loopback)?.Name));

    // a scoped link-local destination routes out of the interface its scope names
    [Test]
    public void FindRouteInterface_ScopedLinkLocal_IsTheScopedInterface()
    {
        var linkLocal = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses
                .Where(unicast => unicast.Address.IsIPv6LinkLocal && unicast.Address.ScopeId > 0)
                .Select(unicast => new { network.Name, unicast.Address }))
            .FirstOrDefault();
        if (linkLocal == null) Assert.Ignore("no interface here has a scoped IPv6 link-local address");

        Assert.That(RelayAddressPreference.FindRouteInterface(linkLocal.Address, 9), Is.EqualTo(linkLocal.Name));
    }

    [Test]
    public async Task ConnectAsync_FirstAddressFails_FallsBackToNextAddress()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepting = listener.AcceptSocketAsync();

        using var connected = await RelaySocketConnector.ConnectAsync(
            [IPAddress.IPv6Loopback, IPAddress.Loopback],
            port,
            TimeSpan.FromMilliseconds(250),
            CancellationToken.None);
        using var accepted = await accepting.WaitAsync(TimeSpan.FromSeconds(1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(connected.Connected, Is.True);
            Assert.That(((IPEndPoint)connected.RemoteEndPoint!).Address, Is.EqualTo(IPAddress.Loopback));
        }
    }

    [Test]
    public void ConnectAsync_NoResolvedAddresses_ReportsHostNotFound()
    {
        var error = Assert.ThrowsAsync<SocketException>(async () =>
            await RelaySocketConnector.ConnectAsync(
                [],
                51600,
                TimeSpan.FromMilliseconds(50),
                CancellationToken.None));

        Assert.That(error!.SocketErrorCode, Is.EqualTo(SocketError.HostNotFound));
    }

    private static readonly IPAddress Wifi = IPAddress.Parse("192.168.1.21");
    private static readonly IPAddress Ethernet = IPAddress.Parse("192.168.1.129");
    private static readonly Dictionary<IPAddress, string> Interfaces = new() { [Wifi] = "en0", [Ethernet] = "en7" };

    private sealed class CountingPreferences
    {
        public int Runs { get; private set; }
        public IReadOnlyDictionary<string, int> Answer { get; init; } = new Dictionary<string, int> { ["en7"] = 1, ["en0"] = 7 };

        public async Task<IReadOnlyDictionary<string, int>> Run(CancellationToken _)
        {
            Runs++;
            await Task.CompletedTask;
            return Answer;
        }
    }
}
