using System.Text.Json;
using Cathedral.Config;
using Hydra.Relay;

namespace Tests.Setup;

/// <summary>
/// Records what a fake sender sent and lets a test await a kind of message that the sender produces on its own
/// thread. The record is read through <see cref="Snapshot"/>, since the sender may still be appending to it.
/// </summary>
public sealed class SentSignals
{
    private readonly List<SentMessage> _sent = [];
    private readonly List<AwaitedSend> _awaited = [];

    public void Record(string[] targetHosts, byte[] payload)
    {
        var decoded = MessageSerializer.Decode(payload);
        TaskCompletionSource[] reached;
        lock (_awaited)
        {
            _sent.Add(new SentMessage(targetHosts, decoded.Kind, decoded.Json));
            reached = [.. _awaited.Where(a => a.Kind == decoded.Kind).Select(a => a.Signal)];
            _awaited.RemoveAll(a => a.Kind == decoded.Kind);
        }
        foreach (var signal in reached) signal.TrySetResult();
    }

    // a copy of everything sent so far
    public List<SentMessage> Snapshot()
    {
        lock (_awaited) return [.. _sent];
    }

    // the messages of one kind sent so far, decoded, optionally only those sent to a host
    public List<T> Sent<T>(MessageKind kind, string? host = null) =>
        [.. Snapshot()
            .Where(s => s.Kind == kind && (host is null || s.Targets.Contains(host)))
            .Select(s => JsonSerializer.Deserialize<T>(s.Json, SaneJson.Options)!)];

    // everything sent so far, cleared in the same step so nothing sent in between is lost
    public List<SentMessage> TakeAll()
    {
        lock (_awaited)
        {
            List<SentMessage> taken = [.. _sent];
            _sent.Clear();
            return taken;
        }
    }

    public void Clear()
    {
        lock (_awaited) _sent.Clear();
    }

    // completes once a message of this kind has been sent
    public Task WaitFor(MessageKind kind)
    {
        lock (_awaited)
        {
            if (_sent.Any(s => s.Kind == kind)) return Task.CompletedTask;
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _awaited.Add(new AwaitedSend(kind, signal));
            return signal.Task.WaitAsync(TestBound.Bound);
        }
    }

    private sealed record AwaitedSend(MessageKind Kind, TaskCompletionSource Signal);
}

public sealed record SentMessage(string[] Targets, MessageKind Kind, string Json);
