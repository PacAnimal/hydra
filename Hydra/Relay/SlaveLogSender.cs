using Cathedral.Utils;
using Microsoft.Extensions.Logging;

namespace Hydra.Relay;

public sealed class SlaveLogSender(IRelaySender relay, SlaveLogForwarder logForwarder, IWorldState peerState, ILogger<SlaveLogSender> log)
    : SimpleHostedService(log, TimeSpan.FromSeconds(15))
{
    protected override async Task Execute(CancellationToken cancel)
    {
        // wait until connected with at least one master
        while (!relay.IsConnected || (await peerState.GetMasters()).Length == 0)
            await Task.Delay(TimeSpan.FromSeconds(1), cancel);

        // drain until disconnected or no masters
        while (!cancel.IsCancellationRequested)
        {
            await logForwarder.Reader.WaitToReadAsync(cancel);

            var masterConfigs = await peerState.GetMasterConfigs();
            if (!relay.IsConnected || masterConfigs.Count == 0) break;

            if (!logForwarder.Reader.TryRead(out var entry)) continue;

            var targets = masterConfigs
                .Where(kv => entry.Level >= (kv.Value.LogLevel ?? LogLevel.Information))
                .Select(kv => kv.Key)
                .ToArray();

            if (targets.Length == 0) continue;

            var msg = new SlaveLogMessage((int)entry.Level, entry.Category, entry.OriginalMessage, entry.Exception?.ToString());
            var payload = MessageSerializer.Encode(MessageKind.SlaveLog, msg);
            await relay.Send(targets, payload);
        }
    }
}
