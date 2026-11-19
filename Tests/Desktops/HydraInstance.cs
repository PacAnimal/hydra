using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using NUnit.Framework.Interfaces;

namespace Tests.Desktops;

/// <summary>A real Hydra process on a desktop, run from the lane's build, with its log collected.</summary>
public sealed class HydraInstance : IAsyncDisposable
{
    private const string Password = "e2e-secret";

    private readonly Process _process;
    private readonly ConcurrentQueue<string> _log = new();
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
    private readonly Task _reader;
    private readonly Lock _appendLock = new();
    private TaskCompletionSource _appended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private HydraInstance(string name, Process process)
    {
        Name = name;
        _process = process;
        _reader = Task.WhenAll(Task.Run(() => Read(process.StandardOutput)), Task.Run(() => Read(process.StandardError)));
    }

    public string Name { get; }
    public IReadOnlyList<string> Log => [.. _log];

    public static string HydraDll =>
        Environment.GetEnvironmentVariable("HYDRA_DLL") is { Length: > 0 } dll ? dll : "/src/Hydra/bin/Debug/net10.0/Hydra.dll";

    public static Task<HydraInstance> Start(Desktop d, string name, string configJson)
    {
        var dir = Path.Combine("/tmp/hydra-e2e", name);
        Directory.CreateDirectory(dir);
        var config = Path.Combine(dir, "hydra.conf");
        File.WriteAllText(config, configJson);

        var process = Process.Start(d.Process("dotnet", HydraDll, "--config", config))
            ?? throw new InvalidOperationException($"hydra '{name}' did not start");
        // nothing is fed on stdin
        process.StandardInput.Close();
        return Task.FromResult(new HydraInstance(name, process));
    }

    // a master on the left of one slave, serving the relay itself; an evdev master has no local screen
    public static string MasterConfig(string name, string slaveName, int port, bool remoteOnly = false)
    {
        var profile = new JsonObject
        {
            ["profileName"] = "m",
            ["mode"] = "Master",
            ["embeddedStyxServer"] = new JsonObject { ["port"] = port, ["password"] = Password },
            ["hosts"] = new JsonArray(new JsonObject
            {
                ["name"] = name,
                ["neighbours"] = new JsonArray(new JsonObject { ["direction"] = "right", ["name"] = slaveName }),
            }),
        };
        if (remoteOnly)
            profile["remoteOnly"] = true;
        return Root(name, profile);
    }

    public static string SlaveConfig(string name, int port) =>
        Root(name, new JsonObject
        {
            ["profileName"] = "s",
            ["mode"] = "Slave",
            ["embeddedStyx"] = new JsonObject { ["server"] = $"https://127.0.0.1:{port}", ["password"] = Password },
        });

    // the first log line containing fragment, seen already or still to come
    public Task WaitForLog(string fragment) => WaitForLog(fragment, Bound);

    // as above, within bound rather than Bound
    public async Task WaitForLog(string fragment, TimeSpan bound)
    {
        if (Log.Any(l => l.Contains(fragment, StringComparison.Ordinal)))
            return;
        using var cts = new CancellationTokenSource(bound);
        try
        {
            // a line logged after the scan above is still queued in the channel
            while (true)
            {
                var line = await _lines.Reader.ReadAsync(cts.Token);
                if (line.Contains(fragment, StringComparison.Ordinal))
                    return;
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ChannelClosedException)
        {
            throw new TimeoutException($"hydra '{Name}' never logged '{fragment}'; last lines:\n{string.Join('\n', Log.TakeLast(20))}", e);
        }
    }

    // the index in Log of the first line at or after from containing fragment, seen already or still to come
    public Task<int> WaitForLog(string fragment, int from) =>
        WaitForLog(l => l.Contains(fragment, StringComparison.Ordinal), $"'{fragment}'", from);

    // the index in Log of the first line at or after from matching pattern, seen already or still to come
    public Task<int> WaitForLog(Regex pattern, int from) => WaitForLog(pattern.IsMatch, $"/{pattern}/", from);

    private async Task<int> WaitForLog(Func<string, bool> match, string what, int from)
    {
        using var cts = new CancellationTokenSource(Bound);
        try
        {
            while (true)
            {
                // taken before the scan, so a line logged after it completes this task
                Task appended;
                lock (_appendLock)
                    appended = _appended.Task;
                var log = Log;
                for (var i = Math.Max(from, 0); i < log.Count; i++)
                {
                    if (match(log[i]))
                        return i;
                }
                await appended.WaitAsync(cts.Token);
            }
        }
        catch (OperationCanceledException e)
        {
            throw new TimeoutException($"hydra '{Name}' never logged {what} after line {from}; last lines:\n{string.Join('\n', Log.TakeLast(20))}", e);
        }
    }

    // the index in Log of the first line at or after from containing fragment, or -1
    public int IndexOf(string fragment, int from = 0)
    {
        var log = Log;
        for (var i = Math.Max(from, 0); i < log.Count; i++)
        {
            if (log[i].Contains(fragment, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    // the index in Log of the last line containing fragment, or -1
    public int LastIndexOf(string fragment)
    {
        var log = Log;
        for (var i = log.Count - 1; i >= 0; i--)
        {
            if (log[i].Contains(fragment, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // it exited in between
        }
        using var cts = new CancellationTokenSource(Bound);
        try
        {
            await _process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // logged below with the rest
        }
        await Settles(_reader);
        _process.Dispose();

        if (TestContext.CurrentContext.Result.Outcome.Status == TestStatus.Failed)
        {
            await TestContext.Out.WriteLineAsync($"--- hydra '{Name}' log (last 200 lines) ---");
            foreach (var line in Log.TakeLast(200))
                await TestContext.Out.WriteLineAsync(line);
        }
    }

    private static string Root(string name, JsonObject profile) =>
        new JsonObject
        {
            ["name"] = name,
            ["logLevel"] = "Debug",
            // every mouse message sent, so a failed run shows the path the cursor took
            ["debugMouse"] = true,
            ["logFile"] = Path.Combine("/tmp/hydra-e2e", name, "hydra.log"),
            ["autoUpdate"] = false,
            ["profiles"] = new JsonArray(profile),
        }.ToJsonString();

    private async Task Read(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            TaskCompletionSource appended;
            lock (_appendLock)
            {
                _log.Enqueue(line);
                appended = _appended;
                _appended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            appended.TrySetResult();
            _lines.Writer.TryWrite(line);
        }
    }
}
