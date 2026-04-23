using System.Runtime.Versioning;
using Hydra.FileTransfer;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.MacOs;

// resolves the paste destination by querying Finder via osascript — same approach as MacFileSelectionDetector.
// returns the front Finder window's folder, or the Desktop if Finder is active but no folder window is open.
// returns null (→ error) if a non-Finder app is frontmost.
[SupportedOSPlatform("macos")]
public sealed class MacDropTargetResolver(ILogger<MacDropTargetResolver> log) : IDropTargetResolver
{
    private readonly ILogger<MacDropTargetResolver> _log = log;

    public string? GetPasteDirectory()
    {
        try
        {
            return RunScript();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "GetPasteDirectory failed");
            return null;
        }
    }

    public void MoveToDestination(string tempDir, string destDir) => FileUtils.MoveTo(tempDir, destDir);

    private string? RunScript()
    {
        // returns NOT_FINDER if Finder is not frontmost; a POSIX path otherwise
        const string script = """
            tell application "System Events"
              if frontmost of process "Finder" is false then return "NOT_FINDER"
            end tell
            tell application "Finder"
              if (count of Finder windows) is 0 then return POSIX path of (desktop as alias)
              try
                return POSIX path of (target of front Finder window as alias)
              on error
                return POSIX path of (desktop as alias)
              end try
            end tell
            """;

        var result = OsaScript.Run(script);
        if (!result.Success)
        {
            _log.LogDebug("osascript exited {Code}: {Stderr}", result.ExitCode, result.Stderr.Trim());
            return null;
        }

        var stdout = result.Stdout.Trim();
        _log.LogDebug("Finder paste target: {Path}", stdout);
        if (stdout == "NOT_FINDER" || stdout.Length == 0) return null;
        return stdout;
    }
}
