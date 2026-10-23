using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Hydra.Platform.MacOs;

[SupportedOSPlatform("macos")]
internal static partial class AgentCommands
{
    private const string Label = AgentPlist.Label;
    private const string ShieldLabel = "com.cathedral.hydra.shield";
    private const string PlistFileName = Label + ".plist";

    [LibraryImport("libc")]
    private static partial uint getuid();

    private static string DomainTarget() => $"gui/{getuid()}";

    internal static void Install(string? configPath)
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("cannot determine process path");
        var workingDir = Path.GetDirectoryName(exePath)
            ?? throw new InvalidOperationException("cannot determine working directory");

        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "Hydra");
        var plistPath = PlistPath();

        Directory.CreateDirectory(Path.GetDirectoryName(plistPath)!);
        Directory.CreateDirectory(logDir);

        RemoveQuarantine(exePath);
        var shieldPath = MacShieldProcess.AppPath(workingDir);
        if (Directory.Exists(shieldPath)) RemoveQuarantine(shieldPath, recursive: true);
        CodesignApp(exePath);

        // remove any running instance before overwriting the plist
        RunLaunchctl(tolerateFailure: true, "bootout", $"{DomainTarget()}/{Label}");

        File.WriteAllText(plistPath, AgentPlist.Generate(exePath, workingDir, logDir, configPath),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

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

    // signs the executable and the shield beside it, each under its own identifier
    internal static void CodesignApp(string exePath)
    {
        Codesign(exePath, Label);
        var shieldPath = MacShieldProcess.AppPath(Path.GetDirectoryName(exePath)!);
        if (Directory.Exists(shieldPath)) Codesign(shieldPath, ShieldLabel);
    }

    // --requirements sets a permissive designated requirement: any binary with our bundle identifier
    // is trusted, rather than the default which ties the csreq to the specific binary's CDHash.
    // this makes the TCC accessibility entry survive auto-updates — the stored csreq matches
    // any future binary as long as it's signed with the same identifier. failure is non-fatal.
    private static void Codesign(string path, string identifier) =>
        Run("/usr/bin/codesign", "--force", "--sign", "-", "-i", identifier, "--requirements", $"=designated => identifier {identifier}", path);

    // failure is fine — the attribute may not exist
    private static void RemoveQuarantine(string path, bool recursive = false)
    {
        foreach (var attr in new[] { "com.apple.quarantine", "com.apple.provenance" })
            Run("/usr/bin/xattr", recursive ? ["-r", "-d", attr, path] : ["-d", attr, path]);
    }

    private static void RunLaunchctl(params string[] args) => RunLaunchctl(false, args);

    private static void RunLaunchctl(bool tolerateFailure, params string[] args)
    {
        var result = Run("/bin/launchctl", args);
        if (result.ExitCode != 0 && !tolerateFailure)
            throw new InvalidOperationException($"launchctl {string.Join(' ', args)} failed (exit {result.ExitCode}): {result.Output}");
    }

    // runs a tool to its exit, draining both its streams
    private static RunResult Run(string file, params string[] args)
    {
        var startInfo = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var proc = Process.Start(startInfo) ?? throw new InvalidOperationException($"failed to start {file}");
        var error = proc.StandardError.ReadToEndAsync();
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        return new RunResult(proc.ExitCode, output + error.Result);
    }

    private sealed record RunResult(int ExitCode, string Output);
}
