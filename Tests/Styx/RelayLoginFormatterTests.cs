using Common.DTO;
using MessagePack;
using MessagePack.Resolvers;
using Styx.Serialization;

namespace Tests.Styx;

[TestFixture]
public class RelayLoginFormatterTests
{
    private static readonly MessagePackSerializerOptions Options = MessagePackSerializerOptions.Standard
        .WithResolver(CompositeResolver.Create(
            [new RelayLoginFormatter()],
            [ContractlessStandardResolver.Instance]))
        .WithSecurity(MessagePackSecurity.UntrustedData);

    // serializes an arbitrary map the way a third-party client would, then reads it as a RelayLogin
    private static RelayLogin? RoundTrip(Dictionary<string, string> map)
    {
        var bytes = MessagePackSerializer.Serialize(map, Options);
        return MessagePackSerializer.Deserialize<RelayLogin?>(bytes, Options);
    }

    [Test]
    public void Deserialize_PascalCaseKeys_Reads()
    {
        var login = RoundTrip(new Dictionary<string, string> { ["Authorization"] = "token", ["HostName"] = "alpha" });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(login!.Authorization, Is.EqualTo("token"));
            Assert.That(login.HostName, Is.EqualTo("alpha"));
        }
    }

    [Test]
    public void Deserialize_CamelCaseKeys_Reads()
    {
        var login = RoundTrip(new Dictionary<string, string> { ["authorization"] = "token", ["hostName"] = "alpha" });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(login!.Authorization, Is.EqualTo("token"));
            Assert.That(login.HostName, Is.EqualTo("alpha"));
        }
    }

    [Test]
    public void Deserialize_SnakeAndShoutingCase_ReadsWhatItCan()
    {
        // hostname casing varies wildly between languages; only the letters have to match
        var login = RoundTrip(new Dictionary<string, string> { ["AUTHORIZATION"] = "token", ["hostname"] = "alpha" });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(login!.Authorization, Is.EqualTo("token"));
            Assert.That(login.HostName, Is.EqualTo("alpha"));
        }
    }

    [Test]
    public void Deserialize_UnknownKeys_AreIgnored()
    {
        var login = RoundTrip(new Dictionary<string, string>
        {
            ["authorization"] = "token",
            ["hostName"] = "alpha",
            ["somethingElse"] = "ignored",
        });
        Assert.That(login!.Authorization, Is.EqualTo("token"));
    }

    [Test]
    public void Deserialize_MissingKeys_YieldEmptyStrings()
    {
        var login = RoundTrip(new Dictionary<string, string> { ["hostName"] = "alpha" });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(login!.Authorization, Is.Empty);
            Assert.That(login.HostName, Is.EqualTo("alpha"));
        }
    }

    [Test]
    public void Deserialize_Nil_ReturnsNull()
    {
        var bytes = MessagePackSerializer.Serialize<RelayLogin?>(null, Options);
        Assert.That(MessagePackSerializer.Deserialize<RelayLogin?>(bytes, Options), Is.Null);
    }

    [Test]
    public void Serialize_WritesDeclaredCasing()
    {
        var bytes = MessagePackSerializer.Serialize<RelayLogin?>(new RelayLogin { Authorization = "token", HostName = "alpha" }, Options);
        var asMap = MessagePackSerializer.Deserialize<Dictionary<string, string>>(bytes, Options);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(asMap["Authorization"], Is.EqualTo("token"));
            Assert.That(asMap["HostName"], Is.EqualTo("alpha"));
        }
    }

    [Test]
    public void RoundTrip_ThroughFormatter_Survives()
    {
        var original = new RelayLogin { Authorization = "some-base64-token==", HostName = "Alpha-Box" };
        var bytes = MessagePackSerializer.Serialize<RelayLogin?>(original, Options);
        var login = MessagePackSerializer.Deserialize<RelayLogin?>(bytes, Options);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(login!.Authorization, Is.EqualTo(original.Authorization));
            Assert.That(login.HostName, Is.EqualTo(original.HostName));
        }
    }
}
