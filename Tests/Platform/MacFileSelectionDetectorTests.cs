using System.Runtime.Versioning;
using Hydra.FileTransfer;
using Hydra.Platform.MacOs;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Platform;

[TestFixture]
[SupportedOSPlatform("macos")]
public class MacFileSelectionDetectorTests
{
    [OneTimeSetUp]
    public void RequireMacOs()
    {
        if (!OperatingSystem.IsMacOS()) Assert.Ignore("macOS-only: Finder");
    }

    [Test]
    public void CancelledQuery_ThrowsRatherThanReportingAFailure()
    {
        var detector = new MacFileSelectionDetector(NullLogger<MacFileSelectionDetector>.Instance);

        Assert.Catch<OperationCanceledException>(() => detector.GetSelectedPaths(new CancellationToken(canceled: true)));
    }

    [Test]
    public void TimedOutScript_IsAFailureRatherThanNotFocused()
    {
        var result = FromScript(new OsaScript.Result(false, string.Empty, string.Empty, -1, TimedOut: true));

        Assert.That(result.Failed, Is.True);
    }

    [Test]
    public void ScriptExitingNonZero_IsAFailureRatherThanNotFocused()
    {
        var result = FromScript(new OsaScript.Result(false, string.Empty, "Finder got an error", 1));

        Assert.That(result.Failed, Is.True);
    }

    [Test]
    public void NotFocused_IsNotAFailure()
    {
        var result = FromScript(new OsaScript.Result(true, "NOT_FOCUSED\n", string.Empty, 0));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.FileManagerFocused, Is.False);
            Assert.That(result.Failed, Is.False);
        }
    }

    [Test]
    public void Selection_ReturnsEachPath()
    {
        var result = FromScript(new OsaScript.Result(true, "/Users/me/a.txt\n/Users/me/b dir/\n", string.Empty, 0));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.FileManagerFocused, Is.True);
            Assert.That(result.Failed, Is.False);
            Assert.That(result.Paths, Is.EqualTo(["/Users/me/a.txt", "/Users/me/b dir/"]));
        }
    }

    private static FileSelectionResult FromScript(OsaScript.Result script) =>
        MacFileSelectionDetector.FromScriptResult(script, NullLogger.Instance);
}
