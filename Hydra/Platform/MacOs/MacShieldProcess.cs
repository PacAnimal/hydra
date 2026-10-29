using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Cathedral.Utils;
using Hydra.FileTransfer;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.MacOs;

// launches the hydra-shield Swift binary (shipped alongside the executable) and talks to it over a ShieldChannel, whose
// header describes the messages. provides: the cursor shielding window, network state detection, the file transfer
// progress panel (IFileTransferDialog) and on-screen notices (IOsdNotification).
// always runs on macOS (both master and slave) so network detection is always available.
internal sealed class MacShieldProcess : IHostedService, IDisposable, IFileTransferDialog, IOsdNotification
{
    private const string StateHide = "hide"; // pass-through: ignoresMouseEvents = true
    private const string StateShow = "show"; // absorb: ignoresMouseEvents = false (invisible)
    private const string StateDebug = "debug"; // absorb: ignoresMouseEvents = false (visible red)

    private readonly MacNetworkState _networkState;
    private readonly bool _needsWifi;
    // starts one shield, null when there is none to start; tests start a fake one
    private readonly Func<ShieldInstance?> _launch;

    public MacShieldProcess(MacNetworkState networkState, bool needsWifi) : this(networkState, needsWifi, Launch) { }

    internal MacShieldProcess(MacNetworkState networkState, bool needsWifi, Func<ShieldInstance?> launch)
    {
        _networkState = networkState;
        _needsWifi = needsWifi;
        _launch = launch;
    }

    // IFileTransferDialog
    public event Action? CancelRequested;

    // the shield's app bundle in an install directory
    internal static string AppPath(string installDir) => Path.Combine(installDir, "Resources", "MacShield", "hydra-shield.app");

    internal static readonly string BinaryPath = Path.Combine(AppPath(AppContext.BaseDirectory), "Contents", "MacOS", "hydra-shield");
    private ShieldInstance? _process;
    private volatile ShieldChannel? _channel;
    private TaskCompletionSource? _initialStateTcs;
    private volatile TaskCompletionSource? _authSettledTcs; // completed when location auth leaves notDetermined
    private readonly Toggle _stopping = new();
    private readonly Lock _processLock = new();
    private volatile string _lastState = StateHide; // last show/hide state; re-applied after unexpected restart
    // how long to wait for the shield's ack, and for a send
    internal TimeSpan SendReplyTimeout { get; init; } = TimeSpan.FromSeconds(5);
    private const int RestartInitialDelayMs = 500;    // initial backoff before restarting a crashed shield
    private const int RestartMaxDelayMs = 30_000;     // backoff cap

    // how long a shield stays up after acking its state before its death no longer counts as a crash-loop
    internal static readonly TimeSpan HealthyUptime = TimeSpan.FromSeconds(30);

    // reset once a (re)started shield has stayed up HealthyUptime past acking its state, so one that acks and dies
    // still backs off
    private readonly DoublingBackoff _restartDelayMs = new(RestartInitialDelayMs, RestartMaxDelayMs);
    private readonly Lock _backoffLock = new();
    // the channel whose shield last acked its first state, and when, under _backoffLock
    private ShieldChannel? _ackedChannel;
    private long _ackedAt;

    // what HealthyUptime is measured on; tests advance a fake one
    internal TimeProvider Clock { get; init; } = TimeProvider.System;

    // how many times the shield has been started, for tests
    internal int Starts => Volatile.Read(ref _starts);
    private int _starts;

    // how many started shields have acked their state, for tests
    internal int StatesAcked => Volatile.Read(ref _statesAcked);
    private int _statesAcked;

    // the wait before a restart; tests record it rather than wait
    internal Func<int, Task> RestartDelay { get; init; } = Task.Delay;

    // assigned after DI builds so post-startup state changes are logged through the normal pipeline
    internal ILogger? Log { get; set; }

    // set after config is resolved — controls visible debug shield + cursor visibility
    internal bool DebugShield { get; set; }

    // fired when network state changes after initial startup — allows NetworkWatcher to react immediately
    internal Action? OnNetworkStateChanged;

    // starts the shield and waits up to `timeout` for the first network state report.
    // called pre-DI so that config resolution has accurate network state on startup.
    // if location auth is still notDetermined at timeout, extends the wait to handle the
    // first-run case where macOS needs time to prompt/grant location permission.
    internal async Task WaitForInitialState(TimeSpan timeout)
    {
        _initialStateTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _authSettledTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        StartProcess();
        await Task.WhenAny(_initialStateTcs.Task, Task.Delay(timeout));

        // if ssid arrived, we're done
        if (_initialStateTcs.Task.IsCompleted) return;

        // if auth is still undetermined, wait for it to settle (location permission prompt)
        if (_networkState.WifiAuthStatus is null or 0)
            await Task.WhenAny(_initialStateTcs.Task, _authSettledTcs.Task, Task.Delay(TimeSpan.FromSeconds(8)));

        // if auth is now authorized, give ssid a moment to arrive
        if (!_initialStateTcs.Task.IsCompleted && _networkState.WifiAuthStatus is 3 or 4)
            await Task.WhenAny(_initialStateTcs.Task, Task.Delay(TimeSpan.FromSeconds(2)));

        _authSettledTcs = null;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // no-op if already started by WaitForInitialState
        if (_process != null) return Task.CompletedTask;
        StartProcess();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Stop();
        return Task.CompletedTask;
    }

    internal Task Show()
    {
        var target = DebugShield ? StateDebug : StateShow;
        if (_lastState != target && Log?.IsEnabled(LogLevel.Debug) == true) Log.LogDebug("Shield → {State} (show/absorb)", target);
        _lastState = target;
        return SendState(target);
    }

    internal Task Hide()
    {
        if (_lastState != StateHide) Log?.LogDebug("Shield → hide (pass-through)");
        _lastState = StateHide;
        return SendState(StateHide);
    }

    // the IPv4 ARP table as the shield reads it, under its own Local Network grant, after a datagram from it to each gateway,
    // which is what has macOS ask for that grant; null when it did not answer or could not read the table
    internal async Task<IReadOnlyList<Neighbour>?> Neighbours(IReadOnlyList<IPAddress> gateways, CancellationToken cancel)
    {
        if (_channel is not { } channel) return null;
        var fields = new Dictionary<string, object?> { ["gateways"] = gateways.Select(g => g.ToString()).ToArray() };
        return await channel.SendAndAwaitReply("neighbours", fields, SendReplyTimeout, cancel) is { } reply ? ParseNeighbours(reply) : null;
    }

    // null without entries: the shield leaves them out when the table could not be read
    internal static List<Neighbour>? ParseNeighbours(ShieldMessage reply)
    {
        if (reply["entries"] is not object?[] entries) return null;
        var neighbours = new List<Neighbour>();
        foreach (var entry in entries)
        {
            if (entry is not IReadOnlyDictionary<string, object?> fields) continue;
            if (fields.GetValueOrDefault("ip") is not string ip || !IPAddress.TryParse(ip, out var address)) continue;
            if (fields.GetValueOrDefault("mac") is byte[] { Length: 6 } mac) neighbours.Add(new Neighbour(address, new PhysicalAddress(mac)));
        }
        return neighbours;
    }

    // -- IFileTransferDialog --

    // initializes the panel with file info and shows it immediately in transferring mode
    public void ShowTransferring(FileTransferInfo info) => Post(new ShieldMessage("transferBegin", new Dictionary<string, object?>
    {
        ["totalBytes"] = info.TotalBytes,
        ["fileCount"] = info.FileCount,
        ["isSender"] = info.IsSender,
        ["names"] = info.FileNames,
    }));

    public void SetCurrentFile(string fileName) => Post(new ShieldMessage("transferFile", new Dictionary<string, object?> { ["name"] = fileName }));

    public void UpdateProgress(long bytesTransferred, double bytesPerSecond)
        => Post(new ShieldMessage("transferProgress", new Dictionary<string, object?> { ["bytes"] = bytesTransferred, ["speed"] = bytesPerSecond }));

    public void ShowCompleted() => Post(new ShieldMessage("transferDone"));

    public void ShowError(string message) => Post(new ShieldMessage("transferError", new Dictionary<string, object?> { ["message"] = message }));

    public void Close() => Post(new ShieldMessage("transferClose"));

    // -- IOsdNotification --

    public void Show(string message) => Post(new ShieldMessage("osd", new Dictionary<string, object?> { ["text"] = message }));

    public void Dispose()
    {
        Stop();
        _process?.Dispose();
    }

    private void StartProcess()
    {
        // start under the process lock and re-check _stopping so a restart racing Stop() can't
        // resurrect the shield after shutdown (which would orphan an event-absorbing overlay window)
        ShieldInstance? process;
        lock (_processLock)
        {
            if (_stopping) return;
            Retire(_process);
            _process = process = _launch();
            if (process != null) Interlocked.Increment(ref _starts);
        }

        if (process == null) return;
        // a send given up mid-frame ends the channel; killing the shield ends its pipes too, so the read loop restarts it
        var channel = new ShieldChannel(process.Output, process.Input, new ForwardingLog(this)) { OnBroken = process.Kill };
        _channel = channel;

        // apply the current show/hide state as a single ordered message. on first start _lastState is
        // StateHide (pass-through); after an unexpected restart it still holds the pre-crash state so the
        // shield resumes absorbing. sending it here rather than as a separate restore avoids a Hide/Show race.
        _ = ApplyState(channel);

        if (_needsWifi)
            Post(new ShieldMessage("wifi")); // activate WiFi monitoring + location on demand (no ack expected)
        else
            _initialStateTcs?.TrySetResult(); // no SSID expected — unblock WaitForInitialState immediately

        _ = Task.Run(() => ReadOutput(channel));
        if (process.Errors is { } errors) _ = Task.Run(() => ReadErrors(errors));
    }

    // the real shield binary, null when it is not installed
    private static ShieldInstance? Launch()
    {
        if (OperatingSystem.IsMacOS()) EnsureExecutable();
        if (!File.Exists(BinaryPath)) return null;
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = BinaryPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        return process == null ? null : new ShieldInstance.OfProcess(process);
    }

    // a shield whose channel ended may still be running, holding its overlay window; it goes before the next starts
    private static void Retire(ShieldInstance? old)
    {
        if (old == null) return;
        old.Kill();
        old.Dispose();
    }

    private bool IsStopping()
    {
        lock (_processLock) return _stopping;
    }

    private void Stop()
    {
        // set _stopping and capture the process under the same lock StartProcess uses, so a concurrent
        // (re)start either bails on _stopping or is captured here and killed — never left orphaned
        ShieldInstance? proc;
        lock (_processLock)
        {
            _stopping.TrySet();
            proc = _process;
        }
        proc?.Kill();
    }

    // sends a shield state and waits for its ack; a dead shield or a missing ack gives up after SendReplyTimeout
    private async Task SendState(string state)
    {
        if (_channel is not { } channel) return;
        await SendState(channel, state);
    }

    private async ValueTask<bool> SendState(ShieldChannel channel, string state)
    {
        var acked = await channel.SendAndAwaitAck("shield", new Dictionary<string, object?> { ["state"] = state }, SendReplyTimeout, CancellationToken.None);
        if (!acked && Log?.IsEnabled(LogLevel.Debug) == true) Log.LogDebug("Shield did not ack {State}", state);
        return acked;
    }

    // a (re)started shield's state; a Show or Hide made while it started may have gone to the old channel, so the
    // current state follows until the shield has acked it. its first ack starts the uptime that, once it reaches
    // HealthyUptime, resets the restart backoff
    private async Task ApplyState(ShieldChannel channel)
    {
        var state = _lastState;
        var acked = false;
        while (await SendState(channel, state))
        {
            if (!acked)
            {
                acked = true;
                lock (_backoffLock)
                {
                    _ackedChannel = channel;
                    _ackedAt = Clock.GetTimestamp();
                }
                Interlocked.Increment(ref _statesAcked);
            }
            if (_lastState == state) break;
            state = _lastState;
        }
    }

    // sends without waiting on the caller; the channel keeps concurrent sends whole and in order. a send that
    // outlasts the timeout breaks the channel, which kills the shield so it restarts
    private void Post(ShieldMessage message)
    {
        if (_channel is { } channel) _ = SendQuietly(channel, message);
    }

    private async Task SendQuietly(ShieldChannel channel, ShieldMessage message)
    {
        try
        {
            using var timeout = new CancellationTokenSource(SendReplyTimeout);
            await channel.Send(message, timeout.Token);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // the shield has just died or stalled; the read loop restarts it
        }
    }

    // logs stderr lines from the shield as warnings
    private async Task ReadErrors(TextReader reader)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line == null) break;
                Log?.LogWarning("shield: {Error}", line);
            }
        }
        catch (Exception ex) { Log?.LogDebug(ex, "Shield stderr reader exited"); }
    }

    // dispatches messages from the shield and updates MacNetworkState; restarts the shield when its stdout ends,
    // whatever ended it, unless stopping
    private async Task ReadOutput(ShieldChannel channel)
    {
        try
        {
            await foreach (var message in channel.ReadAll(CancellationToken.None))
            {
                try
                {
                    Dispatch(message);
                }
                catch (Exception ex)
                {
                    Log?.LogWarning(ex, "Shield message {Kind} could not be handled", message.Kind);
                }
            }
        }
        catch (Exception ex) { Log?.LogWarning(ex, "Shield stdout reader failed"); }

        if (IsStopping()) return;
        // unexpected exit — restart with exponential backoff to avoid rapid crash-loops.
        // StartProcess re-applies _lastState, so the shield resumes its pre-crash absorb state.
        int delayMs;
        lock (_backoffLock)
        {
            if (_ackedChannel == channel && Clock.GetElapsedTime(_ackedAt) >= HealthyUptime) _restartDelayMs.Reset();
            delayMs = _restartDelayMs.Next();
        }
        Log?.LogWarning("Shield process exited unexpectedly — restarting in {Delay}ms", delayMs);
        try
        {
            await RestartDelay(delayMs);
            StartProcess();
        }
        catch (Exception ex) { Log?.LogError(ex, "Shield restart failed"); }
    }

    private void Dispatch(ShieldMessage message)
    {
        switch (message.Kind)
        {
            case "ssid":
                var newSsid = message.GetString("name") is { Length: > 0 } ssid ? ssid : null;
                var changed = newSsid != _networkState.Ssid;
                _networkState.Ssid = newSsid;
                _initialStateTcs?.TrySetResult();
                if (changed && (_initialStateTcs?.Task.IsCompleted ?? true)) OnNetworkStateChanged?.Invoke();
                break;
            case "wifiAuth" when message.GetLong("status") is { } status:
                var authStatus = (int)status;
                _networkState.WifiAuthStatus = authStatus;
                if (Log?.IsEnabled(LogLevel.Debug) == true)
                    Log.LogDebug("Location services auth: {Status}", MacNetworkState.AuthLabel(authStatus));
                if (authStatus != 0) _authSettledTcs?.TrySetResult();
                break;
            case "transferCancel":
                CancelRequested?.Invoke();
                break;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static void EnsureExecutable()
    {
        if (!File.Exists(BinaryPath)) return;
        var mode = File.GetUnixFileMode(BinaryPath);
        if ((mode & UnixFileMode.UserExecute) == 0)
            File.SetUnixFileMode(BinaryPath, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    // the channel is made before DI assigns Log, so it logs through whatever Log is at the time
    private sealed class ForwardingLog(MacShieldProcess owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner.Log?.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => owner.Log?.IsEnabled(logLevel) == true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => owner.Log?.Log(logLevel, eventId, state, exception, formatter);
    }
}
