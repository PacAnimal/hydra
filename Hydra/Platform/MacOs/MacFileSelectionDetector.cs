using System.Diagnostics;
using System.Runtime.Versioning;
using Hydra.FileTransfer;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.MacOs;

[SupportedOSPlatform("macos")]
public sealed class MacFileSelectionDetector : IFileSelectionDetector
{
    private readonly ILogger<MacFileSelectionDetector> _log;

    public MacFileSelectionDetector(ILogger<MacFileSelectionDetector> log)
    {
        _log = log;
        NativeMethods.EnsureAppKitLoaded();
    }

    public List<string>? GetSelectedPaths()
    {
        try
        {
            return RunFinderSelectionScript();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to get Finder selection");
            return null;
        }
    }

    private List<string>? RunFinderSelectionScript()
    {
        // bail immediately if Finder is not the active app
        const string script = """
            tell application "System Events"
              if frontmost of process "Finder" is false then return ""
            end tell
            tell application "Finder"
              set sel to selection
              set output to ""
              repeat with f in sel
                set output to output & POSIX path of (f as alias) & linefeed
              end repeat
              return output
            end tell
            """;

        using var proc = Process.Start(new ProcessStartInfo("osascript", ["-e", script])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (proc == null) return null;

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            _log.LogWarning("osascript exited {Code}: {Stderr}", proc.ExitCode, stderr.Trim());
            return null;
        }

        var paths = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 0)
            .ToList();
        return paths.Count > 0 ? paths : null;
    }
}
