using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;

namespace Hydra.Platform.MacOs;

[SupportedOSPlatform("macos")]
internal static class AgentCommands
{
    private const string Label = "com.cathedral.hydra";
    private const string PlistFileName = "com.cathedral.hydra.plist";

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

        // strip quarantine and re-sign so launchd and the shield helper aren't blocked by gatekeeper
        RemoveQuarantine(exePath);
        Codesign(exePath);
        var shieldPath = Path.Combine(workingDir, "Resources", "MacShield", "hydra-shield.app");
        if (Directory.Exists(shieldPath))
        {
            RemoveQuarantine(shieldPath, recursive: true);
            Codesign(shieldPath);
        }

        // unload any existing agent before overwriting the plist
        if (File.Exists(plistPath))
            RunLaunchctl($"unload -w \"{plistPath}\"", tolerateFailure: true);

        File.WriteAllText(plistPath, GeneratePlist(exePath, workingDir, logDir), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        RunLaunchctl($"load -w \"{plistPath}\"");
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

        RunLaunchctl($"unload -w \"{plistPath}\"", tolerateFailure: true);
        File.Delete(plistPath);
        Console.WriteLine("Hydra agent removed.");
    }

    internal static void Codesign(string path)
    {
        using var proc = Process.Start(new ProcessStartInfo("codesign", $"--force --deep --sign - \"{path}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        proc?.WaitForExit(); // failure is non-fatal — app may still run without a local signature
    }

    private static void RemoveQuarantine(string path, bool recursive = false)
    {
        var args = recursive ? $"-dr com.apple.quarantine \"{path}\"" : $"-d com.apple.quarantine \"{path}\"";
        using var proc = Process.Start(new ProcessStartInfo("xattr", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        proc?.WaitForExit(); // failure is fine — attribute may not exist
    }

    private static void RunLaunchctl(string args, bool tolerateFailure = false)
    {
        using var proc = Process.Start(new ProcessStartInfo("launchctl", args)
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
