using System.Diagnostics;
using System.Runtime.Versioning;
using Hydra.FileTransfer;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.MacOs;

[SupportedOSPlatform("macos")]
public sealed class MacFileSelectionDetector : IFileSelectionDetector
{
    private static readonly nint SelSharedWorkspace = NativeMethods.sel_registerName("sharedWorkspace");
    private static readonly nint SelFrontmostApplication = NativeMethods.sel_registerName("frontmostApplication");
    private static readonly nint SelBundleIdentifier = NativeMethods.sel_registerName("bundleIdentifier");
    private static readonly string FinderBundleId = "com.apple.Finder";

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
            if (!IsFinderFrontmost()) return null;
            return RunFinderSelectionScript();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to get Finder selection");
            return null;
        }
    }

    private static bool IsFinderFrontmost()
    {
        using var pool = new ObjcAutoreleasePool();
        var cls = NativeMethods.objc_getClass("NSWorkspace");
        if (cls == nint.Zero) return false;
        var workspace = NativeMethods.objc_msgSend_noarg(cls, SelSharedWorkspace);
        if (workspace == nint.Zero) return false;
        var app = NativeMethods.objc_msgSend_noarg(workspace, SelFrontmostApplication);
        if (app == nint.Zero) return false;
        var bundleId = NativeMethods.CfStringToManaged(NativeMethods.objc_msgSend_noarg(app, SelBundleIdentifier));
        return string.Equals(bundleId, FinderBundleId, StringComparison.Ordinal);
    }

    private static List<string>? RunFinderSelectionScript()
    {
        // one POSIX path per line
        const string script = """
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
        proc.WaitForExit();

        if (proc.ExitCode != 0) return null;

        var paths = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 0)
            .ToList();
        return paths.Count > 0 ? paths : null;
    }
}
