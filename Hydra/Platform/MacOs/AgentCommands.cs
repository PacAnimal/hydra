using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.MacOs;

[SupportedOSPlatform("macos")]
internal static partial class AgentCommands
{
    private const string Label = "com.cathedral.hydra";
    private const string ShieldLabel = "com.cathedral.hydra.shield";
    private const string PlistFileName = "com.cathedral.hydra.plist";

    [LibraryImport("libc")]
    private static partial uint getuid();

    private static string DomainTarget() => $"gui/{getuid()}";

    internal static void Install()
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("cannot determine process path");
        var workingDir = Path.GetDirectoryName(exePath)
            ?? throw new InvalidOperationException("cannot determine working directory");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var agentsDir = Path.Combine(home, "Library", "LaunchAgents");
        var logDir = Path.Combine(home, "Library", "Logs", "Hydra");
        var plistPath = Path.Combine(agentsDir, PlistFileName);

        Directory.CreateDirectory(agentsDir);
        Directory.CreateDirectory(logDir);

        RemoveQuarantine(exePath);
        Codesign(exePath, Label);
        var shieldPath = Path.Combine(workingDir, "Resources", "MacShield", "hydra-shield.app");
        if (Directory.Exists(shieldPath))
        {
            RemoveQuarantine(shieldPath, recursive: true);
            Codesign(shieldPath, ShieldLabel);
        }

        // remove any running instance before overwriting the plist
        RunLaunchctl($"bootout {DomainTarget()}/{Label}", tolerateFailure: true);

        File.WriteAllText(plistPath, GeneratePlist(exePath, workingDir, logDir), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        RunLaunchctl($"bootstrap {DomainTarget()} \"{plistPath}\"");
        Console.WriteLine("Hydra agent installed and started.");
    }

    internal static void Uninstall()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var plistPath = Path.Combine(home, "Library", "LaunchAgents", PlistFileName);

        if (!File.Exists(plistPath))
        {
            Console.WriteLine("Hydra agent is not installed.");
            return;
        }

        RunLaunchctl($"bootout {DomainTarget()}/{Label}", tolerateFailure: true);
        File.Delete(plistPath);
        Console.WriteLine("Hydra agent removed.");
    }

    internal static void Codesign(string path, string identifier)
    {
        // --requirements sets a permissive designated requirement: any binary with our bundle identifier
        // is trusted, rather than the default which ties the csreq to the specific binary's CDHash.
        // this makes the TCC accessibility entry survive auto-updates — the stored csreq matches
        // any future binary as long as it's signed with the same identifier.
        var psi = new ProcessStartInfo("/usr/bin/codesign")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--force");
        psi.ArgumentList.Add("--sign");
        psi.ArgumentList.Add("-");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(identifier);
        psi.ArgumentList.Add("--requirements");
        psi.ArgumentList.Add($"=designated => identifier {identifier}");
        psi.ArgumentList.Add(path);
        using var proc = Process.Start(psi);
        proc?.WaitForExit(); // failure is non-fatal
    }

    // stale TCC entry: the app appears enabled in System Settings but the stored csreq was bound
    // to a previous binary hash so AXIsProcessTrusted still returns false. tccutil reset is a
    // no-op even with sudo on macOS 14+, and the system-level TCC DB is SIP-protected so sqlite3
    // as root won't work either. only System Settings (via Apple's private tcc.manager entitlement)
    // can remove the entry. we open Settings and log a clear one-time instruction; once the user
    // removes and re-grants, the permissive designated requirement we sign with means future
    // auto-updates will never break the entry again.
    internal static void ResetTccAccessibility(ILogger log)
    {
        log.LogWarning("Accessibility permission has a stale code-signature requirement (csreq mismatch " +
                       "after update). The entry in System Settings still shows Hydra as enabled, but " +
                       "macOS is verifying against an old binary hash. Fix: open System Settings → " +
                       "Privacy & Security → Accessibility, click the − button next to Hydra to remove " +
                       "it, then Hydra will automatically re-prompt you to add it back. The new entry " +
                       "will use a build-independent identifier requirement, so future updates will not " +
                       "require this step again.");
    }

    private static void RemoveQuarantine(string path, bool recursive = false)
    {
        foreach (var attr in new[] { "com.apple.quarantine", "com.apple.provenance" })
        {
            var psi = new ProcessStartInfo("/usr/bin/xattr")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (recursive) psi.ArgumentList.Add("-r");
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(attr);
            psi.ArgumentList.Add(path);
            using var proc = Process.Start(psi);
            proc?.WaitForExit(); // failure is fine — attribute may not exist
        }
    }

    private static void RunLaunchctl(string args, bool tolerateFailure = false)
    {
        using var proc = Process.Start(new ProcessStartInfo("/bin/launchctl", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("failed to start launchctl");

        var output = proc.StandardOutput.ReadToEnd();
        var error = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0 && !tolerateFailure)
            throw new InvalidOperationException($"launchctl {args} failed (exit {proc.ExitCode}): {output}{error}");
    }

    private static string GeneratePlist(string exePath, string workingDir, string logDir)
    {
        var exe = SecurityElement.Escape(exePath);
        var wd = SecurityElement.Escape(workingDir);
        var stdout = SecurityElement.Escape(Path.Combine(logDir, "hydra.stdout.log"));
        var stderr = SecurityElement.Escape(Path.Combine(logDir, "hydra.stderr.log"));

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{Label}</string>
                <key>ProgramArguments</key>
                <array>
                    <string>{exe}</string>
                </array>
                <key>RunAtLoad</key>
                <true/>
                <key>KeepAlive</key>
                <true/>
                <key>StandardOutPath</key>
                <string>{stdout}</string>
                <key>StandardErrorPath</key>
                <string>{stderr}</string>
                <key>WorkingDirectory</key>
                <string>{wd}</string>
                <key>ThrottleInterval</key>
                <integer>5</integer>
            </dict>
            </plist>
            """;
    }
}
