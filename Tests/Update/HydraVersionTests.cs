using System.Reflection;
using Hydra.Update;

namespace Tests.Update;

[TestFixture]
public class HydraVersionTests
{
    [TestCase("0.1.42+e039a5ea6837a0dd3474bb5782a93b8f8087945a", "0.1.42")]
    [TestCase("0.1.42", "0.1.42")]
    [TestCase(null, "0.0.0")]
    public void Release_DropsTheBuildMetadata(string? informational, string expected)
    {
        Assert.That(HydraVersion.Release(informational), Is.EqualTo(expected));
    }

    // SelfUpdater parses it against a release tag, so it must stay a plain version; and it must be Hydra's own,
    // which under the test runner differs from the host process's, so reading the wrong assembly fails here
    [Test]
    public void Current_IsHydrasOwnParseableVersion()
    {
        var host = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Version.TryParse(HydraVersion.Current, out _), Is.True);
            Assert.That(HydraVersion.Current, Is.EqualTo(typeof(HydraVersion).Assembly.GetName().Version!.ToString(3)));
            Assert.That(HydraVersion.Release(host), Is.Not.EqualTo(HydraVersion.Current), "the host's version must differ for this test to discriminate");
        }
    }
}
