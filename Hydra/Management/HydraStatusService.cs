using System.Reflection;
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
        var router = services.GetService<InputRouter>();
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
            dormancy.IsDormant,
            localScreens,
            peers,
            router == null ? null : await router.GetManagementStatusAsync());
    }
}
