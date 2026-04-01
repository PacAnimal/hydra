using System.Threading.Channels;
using Cathedral.Logging;

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
    };

    private readonly Channel<LogEntry> _channel = Channel.CreateBounded<LogEntry>(ChannelOptions);
    private readonly HashSet<string> _masters = [];
    private readonly Lock _mastersLock = new();

    public ChannelReader<LogEntry> Reader => _channel.Reader;

    public ValueTask ForwardAsync(LogEntry entry) => _channel.Writer.WriteAsync(entry);

    public void AddMaster(string host) { lock (_mastersLock) _masters.Add(host); }

    public string[] Masters { get { lock (_mastersLock) return [.. _masters]; } }
}
