using System.Text.Json;
using Hydra.Update;

namespace Tests.Update;

[TestFixture]
public class SelfUpdaterTests
{
    [Test]
    public void NewestEdge_TakesTheHighestPrerelease_NeitherDraftsNorStableReleases()
    {
        using var doc = JsonDocument.Parse("""
            [
              {"tag_name":"v0.2.14","draft":false,"prerelease":false},
              {"tag_name":"v0.2.13","draft":true,"prerelease":true},
              {"tag_name":"v0.2.10","draft":false,"prerelease":false},
              {"tag_name":"v0.2.12","draft":false,"prerelease":true},
              {"tag_name":"not-a-version","draft":false,"prerelease":true},
              {"tag_name":"v0.2.9","draft":false,"prerelease":true}
            ]
            """);

        Assert.That(SelfUpdater.NewestEdge(doc.RootElement) is { } newest ? SelfUpdater.TagVersion(newest) : null, Is.EqualTo(new Version(0, 2, 12)));
    }

    [Test]
    public void NewestEdge_ComparesVersionsNotText()
    {
        using var doc = JsonDocument.Parse("""[{"tag_name":"v0.2.9","prerelease":true},{"tag_name":"v0.2.10","prerelease":true}]""");

        Assert.That(SelfUpdater.NewestEdge(doc.RootElement) is { } newest ? SelfUpdater.TagVersion(newest) : null, Is.EqualTo(new Version(0, 2, 10)));
    }

    [Test]
    public void NewestEdge_IsNull_WhenNothingQualifies()
    {
        using var doc = JsonDocument.Parse("""[{"tag_name":"v0.2.13","draft":true,"prerelease":true},{"tag_name":"latest","prerelease":true},{"tag_name":"v0.2.14"}]""");

        Assert.That(SelfUpdater.NewestEdge(doc.RootElement), Is.Null);
    }
}
