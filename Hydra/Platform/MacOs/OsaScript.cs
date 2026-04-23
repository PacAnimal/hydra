using System.Diagnostics;
using System.Runtime.Versioning;

namespace Hydra.Platform.MacOs;

[SupportedOSPlatform("macos")]
internal static class OsaScript
{
    public readonly record struct Result(bool Success, string Stdout, string Stderr, int ExitCode);

    public static Result Run(string script)
    {
        using var proc = Process.Start(new ProcessStartInfo("osascript", ["-e", script])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (proc == null) return new Result(false, string.Empty, string.Empty, -1);

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return new Result(proc.ExitCode == 0, stdout, stderr, proc.ExitCode);
    }
}
