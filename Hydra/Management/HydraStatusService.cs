using System.Reflection;
using System.Net.NetworkInformation;
using Hydra.Config;
using Hydra.Relay;
using Hydra.Screen;
using Microsoft.Extensions.DependencyInjection;

namespace Hydra.Management;

internal sealed class HydraStatusService(
    IServiceProvider services,
    IHydraProfile profile,
    IWorldState world,
    IDormancyState dormancy,
    HydraRuntimeInfo runtime,
    TransactionalConfigStore configStore)
{
    internal async Task<HydraStatusSnapshot> GetAsync(CancellationToken cancel)
    {
        var localScreens = new List<ScreenStatus>();
        var detector = services.GetService<IScreenDetector>();
        if (detector != null)
        {
            try
            {
                var snapshot = await detector.Get(cancel).WaitAsync(TimeSpan.FromSeconds(1), cancel);
                localScreens = [.. snapshot.Entries.Select(s => new ScreenStatus(s.Name, profile.Name, s.Width, s.Height, s.MouseScale, s.RelativeMouseScale))];
            }
            catch (TimeoutException) { }
        }

        var peers = new List<PeerStatus>();
        if (profile.Mode == Mode.Master)
        {
            foreach (var peer in await world.GetPeerRuntimeSnapshot())
                peers.Add(new PeerStatus(peer.Name, peer.Platform.ToString(), true,
                    [.. peer.Screens.Select(s => new ScreenStatus(s.Name, peer.Name, s.Width, s.Height, s.MouseScale, s.RelativeMouseScale))]));
        }
        else
        {
            foreach (var master in await world.GetMasters())
                peers.Add(new PeerStatus(master, "Master", true, []));
        }

        var relay = services.GetService<IRelaySender>();
        var transport = relay?.Transport;
        var router = services.GetService<InputRouter>();
        var adapters = GetActiveNetworkAdapters();
        var embeddedPeers = await GetEmbeddedRelayPeers();
        var latency = services.GetService<RelayLatencyService>()?.GetSnapshot() ?? [];
        var config = await configStore.ReadAsync(cancel);
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

        return new HydraStatusSnapshot(
            DateTimeOffset.UtcNow,
            version,
            Environment.ProcessId,
            (long)(DateTimeOffset.UtcNow - runtime.StartedAt).TotalSeconds,
            runtime.ConfigPath,
            config.Revision,
            profile.Name,
            profile.ProfileName,
            profile.Mode,
            profile.ProfileName == null && profile.Hosts.Count == 0,
            relay?.IsConnected == true,
            transport == null ? null : new RelayConnectionStatus(
                transport.InterfaceName,
                transport.InterfaceType,
                transport.LocalAddress,
                transport.LocalPort,
                transport.RelayHost,
                transport.RemoteAddress,
                transport.RemotePort,
                transport.ConnectedAt,
                transport.ConnectionAttempts,
                transport.MessagesSent,
                transport.MessagesReceived,
                transport.BytesSent,
                transport.BytesReceived),
            adapters,
            embeddedPeers,
            [.. latency.Select(item => new PeerLatencyStatus(item.Host, item.LastRttMs, item.AverageRttMs,
                item.P95RttMs, item.JitterMs, item.Samples, item.Lost, item.UpdatedAt))],
            dormancy.IsDormant,
            localScreens,
            peers,
            router == null ? null : await router.GetManagementStatusAsync());
    }

    private async Task<List<EmbeddedRelayPeerStatus>> GetEmbeddedRelayPeers()
    {
        var server = services.GetService<EmbeddedStyxServer>();
        if (server == null) return [];
        var clients = await server.GetClients();
        return [.. clients
            .Select(client =>
            {
                var network = FindInterface(client.LocalIp);
                return new EmbeddedRelayPeerStatus(
                    client.HostName,
                    client.RemoteIp,
                    client.LocalIp,
                    network?.Name ?? "unknown",
                    network == null ? "unknown" : DescribeInterface(network));
            })
            .OrderBy(client => client.HostName, StringComparer.Ordinal)];
    }

    private static NetworkInterface? FindInterface(string address)
    {
        if (!System.Net.IPAddress.TryParse(address, out var parsed)) return null;
        try
        {
            if (parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && parsed.ScopeId > 0)
            {
                var scoped = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(network =>
                    network.GetIPProperties().GetIPv6Properties()?.Index == parsed.ScopeId);
                if (scoped != null) return scoped;
            }
            return NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(network =>
                network.GetIPProperties().UnicastAddresses.Any(unicast =>
                    unicast.Address.Equals(parsed)
                    || unicast.Address.IsIPv4MappedToIPv6 && unicast.Address.MapToIPv4().Equals(parsed)));
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static List<NetworkAdapterStatus> GetActiveNetworkAdapters()
    {
        try
        {
            return [.. NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.OperationalStatus == OperationalStatus.Up
                    && network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(network =>
                {
                    var properties = network.GetIPProperties();
                    var addresses = properties.UnicastAddresses
                        .Select(address => address.Address)
                        .Where(address => !address.IsIPv6LinkLocal)
                        .Select(address => address.ToString())
                        .ToList();
                    var hasGateway = properties.GatewayAddresses.Any(gateway =>
                        !gateway.Address.Equals(System.Net.IPAddress.Any)
                        && !gateway.Address.Equals(System.Net.IPAddress.IPv6Any));
                    var statistics = TryGetStatistics(network);
                    return new NetworkAdapterStatus(network.Name, DescribeInterface(network), addresses, hasGateway,
                        TryGetSpeed(network),
                        statistics?.BytesReceived,
                        statistics?.BytesSent,
                        statistics?.IncomingPacketsWithErrors,
                        statistics?.IncomingPacketsDiscarded,
                        statistics?.OutgoingPacketsWithErrors,
                        statistics == null || OperatingSystem.IsMacOS() ? null : statistics.OutgoingPacketsDiscarded);
                })
                .Where(network => network.Addresses.Count > 0)
                .OrderByDescending(network => network.HasGateway)
                .ThenBy(network => network.Name, StringComparer.Ordinal)];
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private static IPInterfaceStatistics? TryGetStatistics(NetworkInterface network)
    {
        try { return network.GetIPStatistics(); }
        catch (NetworkInformationException) { return null; }
        catch (PlatformNotSupportedException) { return null; }
    }

    private static long TryGetSpeed(NetworkInterface network)
    {
        try { return network.Speed; }
        catch (NetworkInformationException) { return 0; }
        catch (PlatformNotSupportedException) { return 0; }
    }

    private static string DescribeInterface(NetworkInterface network)
    {
        if (network.NetworkInterfaceType == NetworkInterfaceType.Unknown
            && (network.Name.StartsWith("utun", StringComparison.OrdinalIgnoreCase)
                || network.Name.StartsWith("tun", StringComparison.OrdinalIgnoreCase)
                || network.Name.StartsWith("tap", StringComparison.OrdinalIgnoreCase)))
            return "VPN / tunnel";
        return network.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => "Wi-Fi",
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.Ethernet3Megabit
                or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.GigabitEthernet => "Ethernet",
            NetworkInterfaceType.Tunnel => "VPN / tunnel",
            NetworkInterfaceType.Ppp => "PPP",
            _ => network.NetworkInterfaceType.ToString()
        };
    }
}
