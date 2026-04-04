using System.Diagnostics;

namespace Hydra.Platform.MacOs;

// launches the hydra-shield Swift binary (shipped alongside the executable),
// which places a topmost window over the cursor park position to absorb hover effects.
// controlled via stdin: "0" = hide, "1" = show (invisible), "2" = show (visible red, debug).
internal sealed class MacShieldProcess : IDisposable
{
    private readonly string _binaryPath;
    private Process? _process;

    internal MacShieldProcess()
    {
        _binaryPath = Path.Combine(AppContext.BaseDirectory, "Resources", "MacShield", "hydra-shield");
        if (OperatingSystem.IsMacOS()) EnsureExecutable();
    }

    internal void Start()
    {
        _process = Process.Start(new ProcessStartInfo
        {
            FileName = _binaryPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
        });
        Hide(); // confirm pass-through on startup
    }

    internal void Show() => Send(Debugger.IsAttached ? "2" : "1");
    internal void Hide() => Send("0");

    internal void Stop()
    {
        if (_process is null || _process.HasExited) return;
        try
        {
            _process.Kill();
            _process.WaitForExit(2000);
        }
        catch (Exception) { /* already dead */ }
    }

    public void Dispose()
    {
        Stop();
        _process?.Dispose();
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

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private void EnsureExecutable()
    {
        if (!File.Exists(_binaryPath)) return;
        var mode = File.GetUnixFileMode(_binaryPath);
        if ((mode & UnixFileMode.UserExecute) == 0)
            File.SetUnixFileMode(_binaryPath, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }
}
