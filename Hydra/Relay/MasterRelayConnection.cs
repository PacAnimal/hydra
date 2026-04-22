using Hydra.Config;
using Microsoft.Extensions.Logging;

namespace Hydra.Relay;

public class MasterRelayConnection(IHydraProfile profile, ILogger<RelayConnection> log, IWorldState peerState)
    : RelayConnection(profile, log, peerState)
{
    protected override Task OnReceive(string sourceHost, MessageKind kind, ReadOnlyMemory<byte> body)
    {
        // masters ignore establishment messages — they are not slaves
        if (kind is MessageKind.MasterConfig)
            return Task.CompletedTask;

        return base.OnReceive(sourceHost, kind, body);
    }
}
