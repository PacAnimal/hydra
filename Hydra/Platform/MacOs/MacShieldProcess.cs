using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.MacOs;

// launches the hydra-shield Swift binary (shipped alongside the executable).
// provides two services:
//   1. cursor shielding window — controlled via stdin ("0" hide, "1" show, "2" debug)
//   2. network state detection — shield reports "ssid:Name" and "wired:0/1" on stdout
// always runs on macOS (both master and slave) so network detection is always available.
internal sealed class MacShieldProcess(MacNetworkState networkState) : IHostedService, IDisposable
{
    private readonly string _binaryPath = Path.Combine(AppContext.BaseDirectory, "Resources", "MacShield", "hydra-shield.app", "Contents", "MacOS", "hydra-shield");
    private Process? _process;
    private TaskCompletionSource? _initialStateTcs;
    private bool _receivedSsid;
    private bool _receivedWired;

    // assigned after DI builds so post-startup state changes are logged through the normal pipeline
    internal ILogger? Log { get; set; }

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

    internal void Show() => Send(Debugger.IsAttached ? "2" : "1");
    internal void Hide() => Send("0");

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
        });

        if (_process == null) return;

        Hide(); // pass-through on startup
        _ = Task.Run(ReadOutput);
    }

    private void Stop()
    {
        if (_process is null || _process.HasExited) return;
        try
        {
            _process.Kill();
            _process.WaitForExit(2000);
        }
        catch (Exception) { /* already dead */ }
    }

    private void Send(string command)
    {
        if (_process is null || _process.HasExited) return;
        try
        {
            _process.StandardInput.WriteLine(command);
            _process.StandardInput.Flush();
        }
        catch (Exception) { /* process may have just died */ }
    }

    // parses stdout lines from the shield and updates MacNetworkState
    private async Task ReadOutput()
    {
        var reader = _process?.StandardOutput;
        if (reader == null) return;
        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line == null) break; // EOF — process exited

            if (line.StartsWith("ssid:", StringComparison.Ordinal))
            {
                var ssid = line["ssid:".Length..];
                var newSsid = string.IsNullOrEmpty(ssid) ? null : ssid;
                var changed = newSsid != networkState.Ssid;
                networkState.Ssid = newSsid;
                _receivedSsid = true;
                if (_receivedWired) _initialStateTcs?.TrySetResult();
                if (changed && (_initialStateTcs?.Task.IsCompleted ?? true)) OnNetworkStateChanged?.Invoke();
            }
            else if (line.StartsWith("wired:", StringComparison.Ordinal))
            {
                var wired = line["wired:".Length..] == "1";
                var changed = wired != networkState.Wired;
                networkState.Wired = wired;
                _receivedWired = true;
                if (_receivedSsid) _initialStateTcs?.TrySetResult();
                if (changed && (_initialStateTcs?.Task.IsCompleted ?? true)) OnNetworkStateChanged?.Invoke();
            }
            else if (line.StartsWith("wifiauth:", StringComparison.Ordinal) && int.TryParse(line["wifiauth:".Length..], out var authStatus))
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
