using System.Runtime.Versioning;
using System.Xml.Linq;
using Hydra.Platform;
using Hydra.Platform.MacOs;

namespace Tests.Platform;

[TestFixture]
public class ProcessPriorityTests
{
    [Test]
    public void Raise_ReportsAnOutcome_AndNeverThrows()
    {
        // the contract that matters is that it cannot be the reason Hydra fails to start: whatever the
        // platform refuses, Raise() answers with a string for the log rather than an exception.
        var result = ProcessPriority.Raise();

        Assert.That(result, Is.Not.Empty);
    }

    [Test]
    public void GeneratedAgentPlist_AsksLaunchdForPriority()
    {
        // an unprivileged macOS agent cannot nice itself down, so the plist is the only place the
        // priority can come from — if these keys are ever dropped the process silently runs throttled.
        if (OperatingSystem.IsMacOS()) AssertPlistCarriesPriority();
        else Assert.Ignore("macOS-only: AgentCommands is compiled for macOS");
    }

    [SupportedOSPlatform("macos")]
    private static void AssertPlistCarriesPriority()
    {
        var plist = AgentCommands.GeneratePlist("/opt/hydra/Hydra", "/opt/hydra", "/tmp/logs");
        var entries = ReadDict(plist);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries["ProcessType"], Is.EqualTo("Interactive"));
            Assert.That(entries["Nice"], Is.EqualTo(ProcessPriority.UnixNice.ToString()));
        }
    }

    /// <summary>Reads a flat plist &lt;dict&gt; into key → value text, so the assertions don't match on raw XML.</summary>
    private static Dictionary<string, string> ReadDict(string plist)
    {
        var dict = XDocument.Parse(plist).Root?.Element("dict")
            ?? throw new InvalidOperationException("plist has no root dict");

        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        string? key = null;
        foreach (var node in dict.Elements())
        {
            if (node.Name == "key") key = node.Value;
            else if (key != null) { entries[key] = node.Value; key = null; }
        }
        return entries;
    }
}
