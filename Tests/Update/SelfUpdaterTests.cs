using System.Text.Json;
using Hydra.Update;

namespace Tests.Update;

[TestFixture]
public class SelfUpdaterTests
{
    [Test]
    public void Newest_TakesTheHighestVersion_PrereleasesIncluded_DraftsNot()
    {
        using var doc = JsonDocument.Parse("""
            [
              {"tag_name":"v0.2.13","draft":true,"prerelease":false},
              {"tag_name":"v0.2.10","draft":false,"prerelease":false},
              {"tag_name":"v0.2.12","draft":false,"prerelease":true},
              {"tag_name":"not-a-version","draft":false,"prerelease":true},
              {"tag_name":"v0.2.9","draft":false,"prerelease":true}
            ]
            """);

        Assert.That(SelfUpdater.Newest(doc.RootElement) is { } newest ? SelfUpdater.TagVersion(newest) : null, Is.EqualTo(new Version(0, 2, 12)));
    }

    [Test]
    public void Newest_ComparesVersionsNotText()
    {
        using var doc = JsonDocument.Parse("""[{"tag_name":"v0.2.9"},{"tag_name":"v0.2.10"}]""");

        Assert.That(SelfUpdater.Newest(doc.RootElement) is { } newest ? SelfUpdater.TagVersion(newest) : null, Is.EqualTo(new Version(0, 2, 10)));
    }

    [Test]
    public void Newest_IsNull_WhenNothingQualifies()
    {
        using var doc = JsonDocument.Parse("""[{"tag_name":"v0.2.13","draft":true},{"tag_name":"latest"}]""");

        Assert.That(SelfUpdater.Newest(doc.RootElement), Is.Null);
    }
}
