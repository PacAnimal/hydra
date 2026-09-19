using Hydra.Config;

namespace Hydra.Management;

// Backs `hydra tui --demo`: a design-preview / screenshot mode that renders the real TUI
// against fabricated data instead of a live daemon, so the UI can be shown or captured
// without ever touching a real network, a real config, or a real machine's identity. Every
// address below is drawn from RFC 5737's reserved documentation ranges (192.0.2.0/24,
// 198.51.100.0/24) — safe to publish, never a route anyone can actually reach.
internal sealed class MockManagementClient : IManagementClient
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;
    internal const string DemoConfigJson = /*lang=json,strict*/ """
        {
          "name": "desktop",
          "profiles": [{
            "profileName": "Home Office",
            "mode": "Master",
            "embeddedStyxServer": { "port": 5000, "password": "[hidden by Hydra TUI]" },
            "hosts": [
              { "name": "desktop", "neighbours": [{ "direction": "right", "name": "laptop" }] },
              { "name": "laptop" }
            ]
          }]
        }
        """;

    public Task<ServerHello> HelloAsync(CancellationToken cancel = default) =>
        Task.FromResult(new ServerHello(ManagementProtocol.Version, "0.0.0-demo", "demo", Environment.ProcessId));

    public Task<HydraStatusSnapshot> GetStatusAsync(CancellationToken cancel = default)
    {
        // fixed offset so "uptime"/"connected for" read as an established, already-busy session
        // rather than a process that just started, without the numbers drifting out of proportion
        // with each other the longer the demo happens to stay open
        const long baseUptimeSeconds = 4117;
        var elapsed = (long)(DateTimeOffset.UtcNow - StartedAt).TotalSeconds;
        var uptime = baseUptimeSeconds + elapsed;
        var connectedAt = StartedAt.AddSeconds(-baseUptimeSeconds);
        // traffic counters creep upward the longer the demo runs, so an interactive session
        // still feels alive rather than frozen on one static snapshot
        var drift = elapsed * 3;

        return Task.FromResult(new HydraStatusSnapshot(
            DateTimeOffset.UtcNow,
            "0.0.0-demo",
            Environment.ProcessId,
            uptime,
            "~/hydra.conf",
            "demo0000",
            "desktop",
            "Home Office",
            Mode.Master,
            IsIdle: false,
            RelayConnected: true,
            new RelayConnectionStatus(
                "en0", "Wi-Fi", "192.0.2.10", 51830,
                "relay.example.com", "192.0.2.1", 5000,
                connectedAt, 2, 5_180 + drift, 7_040 + drift * 2, 2_600_000 + drift * 400, 9_100_000 + drift * 1300),
            [
                new NetworkAdapterStatus("en0", "Wi-Fi", ["192.0.2.10", "2001:db8:1::10"], true, 866_000_000,
                    4_100_000_000 + drift * 9_000, 312_000_000 + drift * 600, 0, 0, 0, 0),
                new NetworkAdapterStatus("utun4", "VPN / tunnel", ["198.51.100.4"], false, 0,
                    2_900_000_000 + drift * 3_000, 2_700_000_000 + drift * 3_000, 0, 0, 0, 0),
            ],
            [
                new EmbeddedRelayPeerStatus("desktop", "127.0.0.1", "127.0.0.1", "lo0", "Loopback"),
                new EmbeddedRelayPeerStatus("laptop", "192.0.2.20", "192.0.2.10", "en0", "Wi-Fi"),
            ],
            [new PeerLatencyStatus("laptop", 2.1, 6.8, 24.0, 1.9, 340 + uptime, 0, DateTimeOffset.UtcNow)],
            Dormant: false,
            [new ScreenStatus("desktop", "desktop", 3840, 2160, 1.0m, null)],
            [
                new PeerStatus("laptop", "MacOS", Connected: true,
                    [new ScreenStatus("laptop", "laptop", 2560, 1600, 1.0m, 1.0m)]),
            ],
            new RouterStatus(IsRemote: false, ActiveHost: null, ActiveScreen: null,
                LockedToScreen: false, ConfinedToScreen: false, RelativeMouse: false)));
    }

    public Task<ManagementLogPage> GetLogsAsync(long after, CancellationToken cancel = default)
    {
        var now = DateTimeOffset.UtcNow;
        List<ManagementLogEntry> entries =
        [
            new(1, now.AddSeconds(-42), "Information", "Hydra.Relay.RelayConnection", "Connected to Styx relay"),
            new(2, now.AddSeconds(-41), "Information", "Hydra.Relay.RelayConnection", "Authenticated on relay as desktop"),
            new(3, now.AddSeconds(-38), "Information", "Hydra.Relay.RelayConnection", "Peers online: laptop"),
            new(4, now.AddSeconds(-20), "Information", "Hydra.Screen.InputRouter", "Entering remote screen 'laptop'"),
            new(5, now.AddSeconds(-19), "Information", "Hydra.Screen.InputRouter", "Leaving remote screen 'laptop'"),
            new(6, now.AddSeconds(-5), "Debug", "Hydra.Relay.ActivityTracker", "Resetting local idle timer"),
        ];
        var page = entries.Where(entry => entry.Cursor > after).ToList();
        return Task.FromResult(new ManagementLogPage(entries.Count, 0, page));
    }

    public Task<ConfigDocument> GetConfigAsync(CancellationToken cancel = default) =>
        Task.FromResult(new ConfigDocument("~/hydra.conf", "demo0000", DemoConfigJson));

    public Task<ConfigValidation> ValidateConfigAsync(string json, CancellationToken cancel = default) =>
        Task.FromResult(new ConfigValidation(true));

    public Task<ConfigDocument> SaveConfigAsync(SaveConfigRequest request, CancellationToken cancel = default) =>
        Task.FromResult(new ConfigDocument("~/hydra.conf", "demo0001", request.Json));

    public Task<CommandResult> ReconnectRelayAsync(CancellationToken cancel = default) =>
        Task.FromResult(new CommandResult(true, "Relay reconnect requested. (demo mode — nothing actually happened)"));

    public Task<CommandResult> RestartHydraAsync(CancellationToken cancel = default) =>
        Task.FromResult(new CommandResult(true, "Hydra restart requested. (demo mode — nothing actually happened)"));

    public Task<CommandResult> ShutdownHydraAsync(CancellationToken cancel = default) =>
        Task.FromResult(new CommandResult(true, "Hydra shutdown requested. (demo mode — nothing actually happened)"));

    public Task<RemotePairResult> PairRemoteAsync(RemotePairRequest request, CancellationToken cancel = default) =>
        Task.FromResult(new RemotePairResult(true, $"Paired with {request.Host}. (demo mode)"));

    public Task<RemoteConfigDocument> GetRemoteConfigAsync(string host, CancellationToken cancel = default) =>
        Task.FromResult(new RemoteConfigDocument(host, "demo0000", DemoConfigJson, null));

    public Task<ConfigValidation> ValidateRemoteConfigAsync(RemoteValidateRequest request, CancellationToken cancel = default) =>
        Task.FromResult(new ConfigValidation(true));

    public Task<RemoteApplyAccepted> ApplyRemoteConfigAsync(RemoteApplyRequest request, CancellationToken cancel = default) =>
        Task.FromResult(new RemoteApplyAccepted(Guid.NewGuid(), "demo0001", DateTimeOffset.UtcNow.AddSeconds(90),
            "Candidate saved. (demo mode — nothing actually happened)"));

    public Task<CommandResult> ConfirmRemoteConfigAsync(RemoteConfirmRequest request, CancellationToken cancel = default) =>
        Task.FromResult(new CommandResult(true, "Remote configuration confirmed. (demo mode)"));
}
