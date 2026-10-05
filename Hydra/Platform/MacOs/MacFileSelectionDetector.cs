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
        NativeHelpers.EnsureAppKitLoaded();
    }

    public string FileManagerName => "Finder";
    public bool IsFileTransferSupported => true;

    public FileSelectionResult GetSelectedPaths(CancellationToken cancel)
    {
        try
        {
            return RunFinderSelectionScript(cancel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Failed to get Finder selection");
            return FileSelectionResult.Failure;
        }
    }

    private FileSelectionResult RunFinderSelectionScript(CancellationToken cancel)
    {
        // returns "NOT_FOCUSED" when Finder is not the active app; empty string when focused but nothing selected
        const string script = """
            tell application "System Events"
              if frontmost of process "Finder" is false then return "NOT_FOCUSED"
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

        return FromScriptResult(OsaScript.Run(script, OsaScript.FinderTimeout, cancel), _log);
    }

    internal static FileSelectionResult FromScriptResult(OsaScript.Result result, ILogger log)
    {
        if (!result.Success)
        {
            result.LogFailure(log, LogLevel.Warning);
            return FileSelectionResult.Failure;
        }

        if (result.Stdout.Trim() == "NOT_FOCUSED")
            return new FileSelectionResult(false, null);

        var paths = result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 0)
            .ToList();
        return new FileSelectionResult(true, paths.Count > 0 ? paths : null);
    }
}
