using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Hydra.Platform.MacOs;

[SupportedOSPlatform("macos")]
internal static partial class AgentCommands
{
    private const string Label = AgentPlist.Label;
    private const string ShieldLabel = "com.cathedral.hydra.shield";
    private const string PlistFileName = "com.cathedral.hydra.plist";

    [LibraryImport("libc")]
    private static partial uint getuid();

    private static string DomainTarget() => $"gui/{getuid()}";

    internal static void Install(string? configPath)
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

        File.WriteAllText(plistPath, AgentPlist.Generate(exePath, workingDir, logDir, configPath), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        RunLaunchctl("bootstrap", DomainTarget(), plistPath);
        Console.WriteLine("Hydra agent installed and started.");
    }

    internal static void Uninstall()
    {
        var plistPath = PlistPath();

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

    internal static bool IsInstalled() => File.Exists(PlistPath());

    internal static void Start()
    {
        var plistPath = PlistPath();
        if (!File.Exists(plistPath))
            throw new InvalidOperationException("Hydra LaunchAgent is not installed.");

        RunLaunchctl("bootstrap", DomainTarget(), plistPath);
    }

    internal static string InstalledConfigPath() => AgentPlist.ConfigPathOf(File.ReadAllText(PlistPath()));

    private static string PlistPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", PlistFileName);

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
}
