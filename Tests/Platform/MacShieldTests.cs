using System.Diagnostics;
using System.Text.RegularExpressions;
using Hydra.Platform.MacOs;

namespace Tests.Platform;

// drives the real hydra-shield from the build output, so an Intel runner exercises its x86_64 slice
[TestFixture]
public class MacShieldTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [OneTimeSetUp]
    public void RequireMacOs()
    {
        if (!OperatingSystem.IsMacOS()) Assert.Ignore("macOS-only: hydra-shield");
    }

    [Test]
    public void Binary_IsUniversal()
    {
        var archs = Tool("lipo", "-archs", MacShieldProcess.BinaryPath).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.That(archs, Is.EquivalentTo(["x86_64", "arm64"]));
    }

    [Test]
    public void Binary_RunsOnMacOs13()
    {
        // swiftc stamps the build machine's own macOS version unless told otherwise, locking out every older mac
        var minimums = Regex.Matches(Tool("vtool", "-show-build", MacShieldProcess.BinaryPath), @"minos (\S+)")
            .Select(m => Version.Parse(m.Groups[1].Value))
            .ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(minimums, Has.Count.EqualTo(2));
            Assert.That(minimums, Has.All.LessThanOrEqualTo(new Version(13, 0)));
        }
    }

    [Test]
    public async Task Shield_EchoesEachState_AndExitsWhenStdinCloses()
    {
        using var shield = Process.Start(new ProcessStartInfo(MacShieldProcess.BinaryPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var stderr = shield.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(Deadline);
        try
        {
            foreach (var state in new[] { "1", "0" })
            {
                await shield.StandardInput.WriteLineAsync(state);
                await shield.StandardInput.FlushAsync(deadline.Token);
                Assert.That(await shield.StandardOutput.ReadLineAsync(deadline.Token), Is.EqualTo(state));
            }

            // the parent going away closes stdin, and the shield must not outlive it
            shield.StandardInput.Close();
            await shield.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            shield.Kill();
            await shield.WaitForExitAsync();
            Assert.Fail($"shield did not answer within {Deadline.TotalSeconds}s: {await stderr}");
        }

        Assert.That(shield.ExitCode, Is.Zero, await stderr);
    }

    private static string Tool(string name, params string[] args)
    {
        using var tool = Process.Start(new ProcessStartInfo("xcrun", [name, .. args]) { RedirectStandardOutput = true })!;
        var output = tool.StandardOutput.ReadToEnd();
        tool.WaitForExit();
        Assert.That(tool.ExitCode, Is.Zero, $"{name} failed");
        return output;
    }
}
