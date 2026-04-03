using Cathedral.Logging;
using System.Threading.Channels;

namespace Hydra.Relay;

/// <summary>
/// Buffers slave log entries for forwarding to masters.
/// Capacity is 1000; oldest entries are dropped when full.
/// </summary>
public sealed class SlaveLogForwarder
{
    private static readonly BoundedChannelOptions ChannelOptions = new(1000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        AllowSynchronousContinuations = false,
        SingleReader = true,
    };

    private readonly Channel<LogEntry> _channel = Channel.CreateBounded<LogEntry>(ChannelOptions);

    public ChannelReader<LogEntry> Reader => _channel.Reader;

    public ValueTask ForwardAsync(LogEntry entry) => _channel.Writer.WriteAsync(entry);
}
