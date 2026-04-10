using System.Diagnostics;
using Cathedral.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.MacOs;

// launches the hydra-shield Swift binary (shipped alongside the executable).
// provides two services:
//   1. cursor shielding window — controlled via stdin (CmdHide/CmdShow/CmdDebug)
//      shield echoes the command back on stdout after applying state; C# holds the send lock until the echo arrives
//   2. network state detection — send CmdWifi via stdin to activate; shield reports PfxSsid/PfxWifiAuth on stdout
// always runs on macOS (both master and slave) so network detection is always available.
internal sealed class MacShieldProcess(MacNetworkState networkState, bool needsWifi) : IHostedService, IDisposable
{
    // stdin commands (C# → shield)
    private const string CmdHide = "0"; // pass-through: ignoresMouseEvents = true
    private const string CmdShow = "1"; // absorb: ignoresMouseEvents = false (invisible)
    private const string CmdDebug = "2"; // absorb: ignoresMouseEvents = false (visible red)
    private const string CmdWifi = "wifi"; // activate WiFi/location monitoring (no echo)

    // stdout prefixes (shield → C#)
    private const string PfxSsid = "ssid:";
    private const string PfxWifiAuth = "wifiauth:";

    private readonly string _binaryPath = Path.Combine(AppContext.BaseDirectory, "Resources", "MacShield", "hydra-shield.app", "Contents", "MacOS", "hydra-shield");
    private readonly SemaphoreSlim _sendSemaphore = new(1, 1);
    private volatile TaskCompletionSource<string>? _pendingReply; // completed by ReadOutput when echo arrives
    private Process? _process;
    private TaskCompletionSource? _initialStateTcs;
    private volatile bool _stopping;
    private volatile string _lastState = CmdHide; // last show/hide command; re-applied after unexpected restart

    // assigned after DI builds so post-startup state changes are logged through the normal pipeline
    internal ILogger? Log { get; set; }

    // set after config is resolved — controls visible debug shield + cursor visibility
    internal bool DebugShield { get; set; }

    // fired when network state changes after initial startup — allows NetworkWatcher to react immediately
    internal Action? OnNetworkStateChanged;

    // starts the shield and waits up to `timeout` for the first network state report.
    // called pre-DI so that config resolution has accurate network state on startup.
    internal async Task WaitForInitialState(TimeSpan timeout)
    {
        _initialStateTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        StartProcess();
        await Task.WhenAny(_initialStateTcs.Task, Task.Delay(timeout));
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

    internal Task Show() { _lastState = DebugShield ? CmdDebug : CmdShow; return SendWithReply(_lastState); }
    internal Task Hide() { _lastState = CmdHide; return SendWithReply(CmdHide); }

    public void Dispose()
    {
        Stop();
        _process?.Dispose();
    }

    private void StartProcess()
    {
        if (OperatingSystem.IsMacOS()) EnsureExecutable();
        if (!File.Exists(_binaryPath)) return;

        _process = Process.Start(new ProcessStartInfo
        {
            FileName = _binaryPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });

        if (_process == null) return;

        _ = Hide(); // pass-through on startup

        if (needsWifi)
            _ = SendFireAndForget(CmdWifi); // activate WiFi monitoring + location on demand (no echo expected)
        else
            _initialStateTcs?.TrySetResult(); // no SSID expected — unblock WaitForInitialState immediately

        _ = Task.Run(ReadOutput);
        _ = Task.Run(ReadErrors);
    }

    private void Stop()
    {
        _stopping = true;
        if (_process is null || _process.HasExited) return;
        try
        {
            _process.Kill();
            _process.WaitForExit(2000);
        }
        catch (Exception) { /* already dead */ }
    }

    // sends a command and holds the send lock until the shield echoes the command back
    private async Task SendWithReply(string command)
    {
        if (_process is null || _process.HasExited) return;
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var _ = await _sendSemaphore.WaitForDisposable();
        _pendingReply = tcs;
        try
        {
            _process.StandardInput.WriteLine(command);
            _process.StandardInput.Flush();
        }
        catch (Exception)
        {
            _pendingReply = null;
            return;
        }
        // 5-second safety timeout: if the shield dies without echoing, don't deadlock forever
        await Task.WhenAny(tcs.Task, Task.Delay(5000));
        _pendingReply = null;
    }

    // sends a command without waiting for a reply (e.g. "wifi" activation)
    private async Task SendFireAndForget(string command)
    {
        if (_process is null || _process.HasExited) return;
        using var _ = await _sendSemaphore.WaitForDisposable();
        try
        {
            _process.StandardInput.WriteLine(command);
            _process.StandardInput.Flush();
        }
        catch (Exception) { /* process may have just died */ }
    }

    // logs stderr lines from the shield as warnings
    private async Task ReadErrors()
    {
        var reader = _process?.StandardError;
        if (reader == null) return;
        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line == null) break;
            Log?.LogWarning("shield: {Error}", line);
        }
    }

    // parses stdout lines from the shield and updates MacNetworkState
    private async Task ReadOutput()
    {
        var reader = _process?.StandardOutput;
        if (reader == null) return;
        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line == null)
            {
                // EOF — process exited; unblock any pending SendWithReply
                _pendingReply?.TrySetResult("");
                break;
            }

            if (line is CmdHide or CmdShow or CmdDebug)
            {
                // echo from shield confirming show/hide state was applied
                _pendingReply?.TrySetResult(line);
            }
            else if (line.StartsWith(PfxSsid, StringComparison.Ordinal))
            {
                var ssid = line[PfxSsid.Length..];
                var newSsid = string.IsNullOrEmpty(ssid) ? null : ssid;
                var changed = newSsid != networkState.Ssid;
                networkState.Ssid = newSsid;
                _initialStateTcs?.TrySetResult();
                if (changed && (_initialStateTcs?.Task.IsCompleted ?? true)) OnNetworkStateChanged?.Invoke();
            }
            else if (line.StartsWith(PfxWifiAuth, StringComparison.Ordinal) && int.TryParse(line[PfxWifiAuth.Length..], out var authStatus))
            {
                networkState.WifiAuthStatus = authStatus;
                Log?.LogDebug("Location services auth: {Status}", authStatus switch
                {
                    0 => "notDetermined",
                    1 => "restricted",
                    2 => "denied",
                    3 or 4 => "authorized",
                    _ => authStatus.ToString()
                });
            }
        }

        if (!_stopping)
        {
            // unexpected exit — restart and restore state
            Log?.LogWarning("Shield process exited unexpectedly — restarting");
            await Task.Delay(500);
            StartProcess();
            if (_lastState != CmdHide)
                _ = SendWithReply(_lastState);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private void EnsureExecutable()
    {
        if (!File.Exists(_binaryPath)) return;
        var mode = File.GetUnixFileMode(_binaryPath);
        if ((mode & UnixFileMode.UserExecute) == 0)
            File.SetUnixFileMode(_binaryPath, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }
}
