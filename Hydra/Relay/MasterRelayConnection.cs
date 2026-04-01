using Hydra.Config;
using Microsoft.Extensions.Logging;

namespace Hydra.Relay;

public class MasterRelayConnection(HydraConfig config, ILogger<RelayConnection> log)
    : RelayConnection(config, log)
{
    protected override Task OnReceive(string sourceHost, MessageKind kind, string json)
    {
        // masters ignore establishment messages — they are not slaves
        if (kind is MessageKind.MasterConfig)
            return Task.CompletedTask;

        return base.OnReceive(sourceHost, kind, json);
    }
}
