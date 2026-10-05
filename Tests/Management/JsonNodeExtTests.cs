using System.Text.Json.Nodes;
using Hydra.Management;

namespace Tests.Management;

public class JsonNodeExtTests
{
    [Test]
    public void GetIgnoreCase_FindsAKeyInAnyCase()
    {
        var node = JsonNode.Parse("""{"HideCursor":true}""")!.AsObject();

        Assert.That(node.GetIgnoreCase("hideCursor")!.GetValue<bool>(), Is.True);
    }

    [Test]
    public void GetIgnoreCase_ReturnsTheLastDuplicate_LikeTheLoader()
    {
        var node = JsonNode.Parse("""{"hideCursor":false,"HideCursor":true}""")!.AsObject();

        Assert.That(node.GetIgnoreCase("hidecursor")!.GetValue<bool>(), Is.True);
    }

    [Test]
    public void GetIgnoreCase_ReturnsNullWhenAbsent() =>
        Assert.That(JsonNode.Parse("{}")!.AsObject().GetIgnoreCase("x"), Is.Null);

    [Test]
    public void SetIgnoreCase_ReplacesInPlaceKeepingTheExistingSpelling()
    {
        var node = JsonNode.Parse("""{"first":1,"HideCursor":true,"last":2}""")!.AsObject();

        node.SetIgnoreCase("hideCursor", false);

        Assert.That(node.ToJsonString(), Is.EqualTo("""{"first":1,"HideCursor":false,"last":2}"""));
    }

    [Test]
    public void SetIgnoreCase_AddsTheGivenSpellingWhenAbsent()
    {
        var node = JsonNode.Parse("{}")!.AsObject();

        node.SetIgnoreCase("hideCursor", true);

        Assert.That(node.ToJsonString(), Is.EqualTo("""{"hideCursor":true}"""));
    }

    [Test]
    public void SetIgnoreCase_DropsShadowedDuplicatesAndKeepsTheOneTheLoaderReads()
    {
        var node = JsonNode.Parse("""{"hideCursor":false,"HideCursor":true}""")!.AsObject();

        node.SetIgnoreCase("hidecursor", false);

        Assert.That(node.ToJsonString(), Is.EqualTo("""{"HideCursor":false}"""));
    }

    [Test]
    public void RemoveIgnoreCase_RemovesEverySpelling()
    {
        var node = JsonNode.Parse("""{"hideCursor":false,"keep":1,"HideCursor":true}""")!.AsObject();

        node.RemoveIgnoreCase("HIDECURSOR");

        Assert.That(node.ToJsonString(), Is.EqualTo("""{"keep":1}"""));
    }
}
