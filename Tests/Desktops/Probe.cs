using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace Tests.Desktops;

/// <summary>A probe app on a desktop reporting what it receives as JSON lines, and answering commands on stdin.</summary>
public sealed class Probe : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Channel<JsonElement> _lines = Channel.CreateUnbounded<JsonElement>();
    private readonly Queue<string> _recent = new();
    private readonly List<ProbeWatcher> _watchers = [];
    private readonly Task _reader;

    private Probe(Process process)
    {
        _process = process;
        _reader = Task.Run(Read);
    }

    // the probe's ready line
    public JsonElement Ready { get; private set; }

    public static Task<Probe> Input(Desktop d) => Start(d, "input-probe.py");

    public static Task<Probe> Game(Desktop d) => Start(d, "game-probe");

    public static bool Has(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out _);

    // the next line matching, skipping those before it
    public async Task<JsonElement> WaitFor(Func<JsonElement, bool> match)
    {
        using var cts = new CancellationTokenSource(Bound);
        try
        {
            while (true)
            {
                var line = await _lines.Reader.ReadAsync(cts.Token);
                if (match(line))
                    return line;
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ChannelClosedException)
        {
            throw new TimeoutException($"probe line not seen; last lines:\n{string.Join('\n', Recent())}", e);
        }
    }

    // watches for a line matching from now on; the wait it returns ends on one, or bound after its call, whichever is
    // first. the line stays queued for WaitFor
    public Func<Task> Watch(Func<JsonElement, bool> match, TimeSpan bound)
    {
        var watcher = new ProbeWatcher(match, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_watchers)
            _watchers.Add(watcher);
        return async () =>
        {
            await Task.WhenAny(watcher.Seen.Task, Task.Delay(bound));
            lock (_watchers)
                _watchers.Remove(watcher);
        };
    }

    public async Task<JsonElement> Command(object command, string replyKey)
    {
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command));
        await _process.StandardInput.FlushAsync();
        return await WaitFor(e => Has(e, replyKey));
    }

    public List<JsonElement> Drain()
    {
        var drained = new List<JsonElement>();
        while (_lines.Reader.TryRead(out var line))
            drained.Add(line);
        return drained;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                await _process.StandardInput.WriteLineAsync("""{"quit":true}""");
                _process.StandardInput.Close();
            }
        }
        catch (IOException)
        {
            // the probe already went away
        }

        using var cts = new CancellationTokenSource(Bound);
        try
        {
            await _process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _process.Kill(entireProcessTree: true);
        }
        await Settles(_reader);
        _process.Dispose();
    }

    private static async Task<Probe> Start(Desktop d, string file)
    {
        var process = Process.Start(d.Process(file))
            ?? throw new InvalidOperationException($"{file} did not start");
        var probe = new Probe(process);
        try
        {
            probe.Ready = await probe.WaitFor(e => Has(e, "ready"));
            return probe;
        }
        catch
        {
            await probe.DisposeAsync();
            throw;
        }
    }

    private async Task Read()
    {
        // stderr is kept with the recent lines, so a timeout shows why the probe failed
        var errors = Task.Run(async () =>
        {
            while (await _process.StandardError.ReadLineAsync() is { } line)
                Remember("stderr: " + line);
        });
        while (await _process.StandardOutput.ReadLineAsync() is { } line)
        {
            Remember(line);
            try
            {
                using var doc = JsonDocument.Parse(line);
                var report = doc.RootElement.Clone();
                lock (_watchers)
                    foreach (var watcher in _watchers)
                        if (watcher.Match(report))
                            watcher.Seen.TrySetResult();
                _lines.Writer.TryWrite(report);
            }
            catch (JsonException)
            {
                // not a report; kept in the recent lines only
            }
        }
        await errors;
        _lines.Writer.TryComplete();
    }

    private void Remember(string line)
    {
        lock (_recent)
        {
            _recent.Enqueue(line);
            while (_recent.Count > 20)
                _recent.Dequeue();
        }
    }

    private string[] Recent()
    {
        lock (_recent)
            return [.. _recent];
    }

    private sealed record ProbeWatcher(Func<JsonElement, bool> Match, TaskCompletionSource Seen);
}
