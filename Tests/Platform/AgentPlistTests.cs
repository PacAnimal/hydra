using System.Xml.Linq;
using Hydra.Platform.MacOs;

namespace Tests.Platform;

[TestFixture]
public class AgentPlistTests
{
    private const string Exe = "/opt/hydra & co/Hydra";

    [Test]
    public void TheConfigItWasInstalledWithIsPassedToTheAgent()
    {
        var plist = AgentPlist.Generate(Exe, "/opt/hydra & co", "/tmp/logs", "/etc/hydra <x>/hydra.conf");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ProgramArguments(plist), Is.EqualTo([Exe, "--config", "/etc/hydra <x>/hydra.conf"]));
            Assert.That(AgentPlist.ConfigPathOf(plist), Is.EqualTo("/etc/hydra <x>/hydra.conf"));
        }
    }

    // installed without --config, the agent searches beside its binary as it always has
    [Test]
    public void WithoutAConfigTheAgentRunsTheOneBesideItsBinary()
    {
        var plist = AgentPlist.Generate(Exe, "/opt/hydra & co", "/tmp/logs", null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ProgramArguments(plist), Is.EqualTo([Exe]));
            Assert.That(AgentPlist.ConfigPathOf(plist), Is.EqualTo(Path.Combine("/opt/hydra & co", "hydra.conf")));
        }
    }

    private static List<string> ProgramArguments(string plist)
    {
        var key = XDocument.Parse(plist).Descendants("key").Single(k => k.Value == "ProgramArguments");
        return [.. ((XElement)key.NextNode!).Elements("string").Select(e => e.Value)];
    }
}
