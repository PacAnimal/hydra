using Cathedral.Config;
using Common.DTO;
using MessagePack;
using MessagePack.Resolvers;

namespace Tests.Styx;

// Styx.md documents the encoding of everything crossing the hub, and third parties implement against it.
// New hub methods are free to use Cathedral's richer encodings (enums as name strings, Guids as raw bytes),
// but the types already on the wire must keep encoding the way the document says, so an implementation that
// has shipped keeps working. These tests compare the hub's options against a plain contractless resolver —
// the encoding Styx.md describes — for every type that crosses today.
[TestFixture]
public class StyxWireFormatTests
{
    private static readonly MessagePackSerializerOptions Documented = MessagePackSerializerOptions.Standard
        .WithResolver(ContractlessStandardResolver.Instance)
        .WithSecurity(MessagePackSecurity.UntrustedData);

    private static MessagePackSerializerOptions Hub => SaneMessagePack.InteropOptions;

    private static readonly string[] TwoHosts = ["alpha-box", "beta"];
    private static readonly string[] OneHost = ["alpha-box"];

    private static void AssertEncodesAsDocumented<T>(T value, string what) =>
        Assert.That(MessagePackSerializer.Serialize(value, Hub), Is.EqualTo(MessagePackSerializer.Serialize(value, Documented)),
            $"{what} no longer encodes the way Styx.md documents");

    [Test]
    public void RelayLogin_EncodesAsDocumented() =>
        AssertEncodesAsDocumented(new RelayLogin { Authorization = new string('t', 176), HostName = "alpha-box" }, "RelayLogin");

    [Test]
    public void RelayLoginResponse_EncodesAsDocumented()
    {
        AssertEncodesAsDocumented(new RelayLoginResponse { Authenticated = true }, "accepted RelayLoginResponse");
        AssertEncodesAsDocumented(new RelayLoginResponse { Authenticated = false, Message = "Invalid authorization" }, "refused RelayLoginResponse");
    }

    [Test]
    public void SendArguments_EncodeAsDocumented()
    {
        AssertEncodesAsDocumented(TwoHosts, "Send targetHosts");
        AssertEncodesAsDocumented(new byte[] { 0, 1, 2, 255 }, "Send payload");
    }

    [Test]
    public void ReceiveArguments_EncodeAsDocumented()
    {
        AssertEncodesAsDocumented("alpha-box", "Receive sourceHost");
        AssertEncodesAsDocumented("203.0.113.7", "Receive sourceIp");
        AssertEncodesAsDocumented(new byte[64], "Receive payload");
    }

    [Test]
    public void PeersAndPingAndIpArguments_EncodeAsDocumented()
    {
        AssertEncodesAsDocumented(OneHost, "Peers hostNames");
        AssertEncodesAsDocumented(Array.Empty<string>(), "Peers hostNames when alone");
        AssertEncodesAsDocumented(true, "Ping result");
        AssertEncodesAsDocumented("127.0.0.1", "GetMyIp result");
        AssertEncodesAsDocumented("duplicate hostname", "Kicked reason");
    }

    // Styx.md §3.3: a messagepack client reads PascalCase response members
    [Test]
    public void ResponseMembers_KeepDeclaredCasing()
    {
        var bytes = MessagePackSerializer.Serialize(new RelayLoginResponse { Authenticated = true }, Hub);
        var json = MessagePackSerializer.ConvertToJson(bytes, Hub);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(json, Does.Contain("\"Authenticated\""));
            Assert.That(json, Does.Not.Contain("\"authenticated\""));
        }
    }

    // Styx.md §3.3: payloads are native binary under messagepack, with no base64 expansion
    [Test]
    public void Payloads_AreNativeBinary()
    {
        var bytes = MessagePackSerializer.Serialize(new byte[] { 1, 2, 3 }, Hub);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bytes[0], Is.InRange((byte)0xc4, (byte)0xc6), "payload must use a MessagePack bin type");
            Assert.That(bytes, Has.Length.EqualTo(5), "3 bytes plus a 2-byte bin8 header, not base64");
        }
    }
}
