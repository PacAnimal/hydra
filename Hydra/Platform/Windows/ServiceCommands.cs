using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;

namespace Hydra.Platform.Windows;

[SupportedOSPlatform("windows")]
internal static class ServiceCommands
{
    private const string ServiceName = "Hydra";

    internal static void Install()
    {
        EnsureElevated("--install");

        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("cannot determine process path");

        // remove the "downloaded from internet" mark so windows doesn't block the service binary
        File.Delete(exePath + ":Zone.Identifier");

        RunSc($"create {ServiceName} binPath= \"\\\"{exePath}\\\" --service\" start= auto obj= LocalSystem");
        RunSc($"description {ServiceName} \"Hydra KVM — seamless mouse and keyboard sharing\"");
        RunSc($"failure {ServiceName} reset= 0 actions= restart/5000/restart/5000/restart/5000");

        // required for SendSAS() to work when called from a service
        Registry.SetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System",
            "SoftwareSASGeneration", 1, RegistryValueKind.DWord);

        RunSc($"start {ServiceName}");
        Console.WriteLine("Hydra service installed and started.");
    }

    internal static void Uninstall()
    {
        EnsureElevated("--uninstall");
        RunSc($"stop {ServiceName}");
        RunSc($"delete {ServiceName}");
        Console.WriteLine("Hydra service removed.");
    }

    private static void EnsureElevated(string arg)
    {
        if (IsElevated()) return;

        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("cannot determine process path");

        try
        {
            Process.Start(new ProcessStartInfo(exePath, arg) { Verb = "runas", UseShellExecute = true })?.WaitForExit();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Elevation failed: {ex.Message}");
        }
        Environment.Exit(0);
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void RunSc(string args)
    {
        using var proc = Process.Start(new ProcessStartInfo("sc.exe", args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("failed to start sc.exe");

        var output = proc.StandardOutput.ReadToEnd();
        var error = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        // stop may fail if already stopped — that's fine
        if (proc.ExitCode != 0 && !args.StartsWith("stop", StringComparison.Ordinal))
            throw new InvalidOperationException($"sc.exe {args} failed (exit {proc.ExitCode}): {output}{error}");
    }
}
