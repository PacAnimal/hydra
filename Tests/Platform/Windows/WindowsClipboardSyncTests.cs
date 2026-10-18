using System.Diagnostics;
using System.Runtime.Versioning;
using Hydra.Platform;
using Hydra.Platform.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Platform.Windows;

// the real clipboard, which is one per session
[TestFixture]
[NonParallelizable]
[SupportedOSPlatform("windows")]
public class WindowsClipboardSyncTests : CopiedPrimaryContract
{
    protected override IClipboardSync Create() => new WindowsClipboardSync(NullLogger<WindowsClipboardSync>.Instance);

    [TestCase("ExcludeClipboardContentFromMonitorProcessing", 1u)]
    [TestCase("CanIncludeInClipboardHistory", 0u)]
    public void PasswordManagersCopy_IsNotThePrimary(string format, uint value)
    {
        HydraWritesThen(() => WriteForeignCopy("secret", format, value));

        Assert.That(Hydra.GetPrimaryText(), Is.EqualTo("relayed selection"));
    }

    [Test]
    public void CopyAllowedIntoHistory_IsThePrimary()
    {
        HydraWritesThen(() => WriteForeignCopy("copied", "CanIncludeInClipboardHistory", 1));

        Assert.That(Hydra.GetPrimaryText(), Is.EqualTo("copied"));
    }

    [Test]
    public void FilesCopiedInExplorer_AreLeftOnTheClipboard()
    {
        var file = Path.GetTempFileName();
        try
        {
            HydraWritesThen(() => RunInOtherProcess("$f = New-Object System.Collections.Specialized.StringCollection; " +
                                                    $"[void]$f.Add('{file}'); [System.Windows.Forms.Clipboard]::SetFileDropList($f)"));

            var applied = ClipboardUtils.ApplyPreservingFiles(Hydra, new ClipboardSnapshot("pushed", null, null), NullLogger.Instance, "test");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(applied, Is.Null);
                Assert.That(Hydra.HasFileClipboard(), Is.True);
                Assert.That(Hydra.GetText(), Is.Null);
            }
        }
        finally
        {
            File.Delete(file);
        }
    }

    // another app's copy, made by another process: its text, and a marker format holding a DWORD beside it
    private static void WriteForeignCopy(string text, string markerFormat, uint value) =>
        RunInOtherProcess("$d = New-Object System.Windows.Forms.DataObject; " +
                          $"$d.SetText('{text}'); $d.SetData('{markerFormat}', (New-Object System.IO.MemoryStream(,[BitConverter]::GetBytes([uint32]{value})))); " +
                          "[System.Windows.Forms.Clipboard]::SetDataObject($d, $true)");

    // a clipboard script run by Windows Forms in another process, as another app's copy would be
    private static void RunInOtherProcess(string clipboardScript)
    {
        var script = "Add-Type -AssemblyName System.Windows.Forms; " + clipboardScript;
        using var process = Process.Start(new ProcessStartInfo("powershell", ["-NoProfile", "-STA", "-Command", script]) { RedirectStandardError = true })!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.That(process.ExitCode, Is.Zero, error);
    }
}
