using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Hydra.Platform.Windows;
using Hydra.Relay;

namespace Tests.Platform.Windows;

/// <summary>The P/Invoke declarations whose structs and strings the source generator marshals, called for real.</summary>
[TestFixture]
[SupportedOSPlatform("windows")]
public class WindowsInteropTests
{
    [Test]
    public void ProcessSnapshot_FindsThisProcessByItsExecutableName()
    {
        var exeName = Path.GetFileName(Environment.ProcessPath)!;

        Assert.That(Win32Session.ProcessIdsNamed(exeName), Does.Contain((uint)Environment.ProcessId));
    }

    [Test]
    public void GlobalEvent_CreatedHereCanBeOpenedAndSignalled()
    {
        var name = $"hydra-test-{Guid.NewGuid():N}";
        using var created = Win32Session.CreateGlobalEvent(name, manualReset: true);
        using var opened = Win32Session.OpenGlobalEvent(name);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(opened, Is.Not.Null);
            Assert.That(Win32Session.SignalEvent(opened!), Is.True);
            Assert.That(Win32Session.WaitForEvent(created, 0), Is.True);
        }
    }

    // an interactive session names a real adapter (\\.\DISPLAY1); a service session the disconnected "WinDisc"
    [Test]
    public void MonitorInfo_NamesTheDisplayDevice()
    {
        var monitor = NativeMethods.MonitorFromPoint(new WINPOINT(), NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFOEX { Size = (uint)Unsafe.SizeOf<MONITORINFOEX>() };

        Assert.That(NativeMethods.GetMonitorInfoW(monitor, ref info), Is.True);

        var name = info.DeviceName.ToString();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(name, Is.Not.Empty);
            // ordinal: a culture-aware search finds an ignorable NUL in any string
            Assert.That(name, Does.Not.Contain('\0'));
        }
    }

    // Get-NetIPInterface reads the same table, so it is the oracle for the row layout
    [Test]
    public async Task ConnectedInterfaceMetrics_MatchGetNetIPInterface()
    {
        const string script = "Get-NetIPInterface -ConnectionState Connected | "
            + "ForEach-Object { '{0}|{1}' -f $_.InterfaceIndex,$_.InterfaceMetric }";
        var startInfo = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in (string[])["-NoProfile", "-NonInteractive", "-Command", script]) startInfo.ArgumentList.Add(argument);
        using var powershell = Process.Start(startInfo)!;
        var output = await powershell.StandardOutput.ReadToEndAsync();
        await powershell.WaitForExitAsync();
        var expected = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('|'))
            .Select(parts => new InterfaceMetric(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture)));

        Assert.That(IpInterfaceTable.ConnectedMetrics(), Is.EquivalentTo(expected).And.Not.Empty);
    }
}

/// <summary>Fixed-length UTF-16 buffers from native structs, decoded the way Win32 wrote them.</summary>
[TestFixture]
public class WideCharsTests
{
    [Test]
    public void Decode_StopsAtTheFirstNul()
    {
        ushort[] buffer = ['w', 'i', 'n', 0, 'x', 'y', 0, 0];

        Assert.That(WideChars.Decode(buffer), Is.EqualTo("win"));
    }

    [Test]
    public void Decode_ReadsAFullBufferWithNoNul()
    {
        ushort[] buffer = ['a', 'b'];

        Assert.That(WideChars.Decode(buffer), Is.EqualTo("ab"));
    }
}
