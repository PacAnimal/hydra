using Cathedral.Utils;
using Microsoft.Extensions.Logging;

namespace Hydra.Relay;

public sealed class SlaveLogSender(IRelaySender relay, SlaveLogForwarder logForwarder, ILogger<SlaveLogSender> log)
    : SimpleHostedService(log, TimeSpan.FromSeconds(15))
{
    protected override async Task Execute(CancellationToken cancel)
    {
        // wait until connected with at least one master
        while (!relay.IsConnected || logForwarder.Masters.Length == 0)
            await Task.Delay(TimeSpan.FromSeconds(1), cancel);

        // drain until disconnected or no masters
        while (!cancel.IsCancellationRequested)
        {
            await logForwarder.Reader.WaitToReadAsync(cancel);

            if (!relay.IsConnected || logForwarder.Masters.Length == 0) break;

            // peek → send → consume, so a failed send doesn't drop the entry
            if (!logForwarder.Reader.TryPeek(out var entry)) continue;
            var msg = new SlaveLogMessage((int)entry.Level, entry.Category, entry.OriginalMessage, entry.Exception?.ToString());
            var payload = MessageSerializer.Encode(MessageKind.SlaveLog, msg);
            await relay.Send(logForwarder.Masters, payload);
            logForwarder.Reader.TryRead(out _);
        }
    }
}
