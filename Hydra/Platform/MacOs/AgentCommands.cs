using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;

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
        RunLaunchctl(tolerateFailure: true, "bootout", $"{DomainTarget()}/{Label}");

        File.WriteAllText(plistPath, GeneratePlist(exePath, workingDir, logDir), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        RunLaunchctl("bootstrap", DomainTarget(), plistPath);
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

        RunLaunchctl(tolerateFailure: true, "bootout", $"{DomainTarget()}/{Label}");
        File.Delete(plistPath);
        Console.WriteLine("Hydra agent removed.");
    }

    internal static void Stop()
    {
        // Keep the plist so macOS can load it again at the next login. This only unloads the
        // current user-session job; --uninstall is the permanent opt-out from auto-start.
        RunLaunchctl(tolerateFailure: true, "bootout", $"{DomainTarget()}/{Label}");
    }

    internal static bool IsInstalled()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return File.Exists(Path.Combine(home, "Library", "LaunchAgents", PlistFileName));
    }

    internal static void Start()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var plistPath = Path.Combine(home, "Library", "LaunchAgents", PlistFileName);
        if (!File.Exists(plistPath))
            throw new InvalidOperationException("Hydra LaunchAgent is not installed.");

        RunLaunchctl("bootstrap", DomainTarget(), plistPath);
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

    private static void RunLaunchctl(params string[] args) => RunLaunchctl(false, args);

    private static void RunLaunchctl(bool tolerateFailure, params string[] args)
    {
        var startInfo = new ProcessStartInfo("/bin/launchctl")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var proc = Process.Start(startInfo)
            ?? throw new InvalidOperationException("failed to start launchctl");

        var output = proc.StandardOutput.ReadToEnd();
        var error = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0 && !tolerateFailure)
            throw new InvalidOperationException(
                $"launchctl {string.Join(' ', args)} failed (exit {proc.ExitCode}): {output}{error}");
    }

    internal static string GeneratePlist(string exePath, string workingDir, string logDir)
    {
        var exe = SecurityElement.Escape(exePath);
        var wd = SecurityElement.Escape(workingDir);
        var stdout = SecurityElement.Escape(Path.Combine(logDir, "hydra.stdout.log"));
        var stderr = SecurityElement.Escape(Path.Combine(logDir, "hydra.stderr.log"));

        // ProcessType and Nice are the whole reason this agent can keep up on a loaded machine. Without
        // ProcessType launchd classifies us as a background job and throttles our CPU and I/O; Nice is
        // privileged and launchd is the only one in a position to apply it on our behalf — the process
        // itself runs as the user and cannot. ProcessPriority.Raise() then only matches what we hold.
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
                <key>ProcessType</key>
                <string>Interactive</string>
                <key>Nice</key>
                <integer>{ProcessPriority.UnixNice}</integer>
            </dict>
            </plist>
            """;
    }
}
