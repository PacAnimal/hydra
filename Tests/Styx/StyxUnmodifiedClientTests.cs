using System.Net.WebSockets;
using System.Text;
using Cathedral.Styx;
using Common;
using Hydra.Relay;
using Tests.Setup;

namespace Tests.Styx;

/// <summary>
/// Clients in the field treat a token as opaque, so a v2 token must work through their unchanged code: Hydra's
/// own relay client, and a hand-rolled JSON client, which sees exactly the completion bytes it always did.
/// </summary>
[TestFixture]
public class StyxUnmodifiedClientTests : StyxFixtureBase
{
    [Test]
    public async Task TwoHydraClients_OnAV2Network_ExchangeMessages()
    {
        var root = StyxTestServer.MintToken();
        var key = NetworkConfig.NewEncryptionKey();
        await using var pair = await ClientPair.Start(
            HydraTestClient.Master(Factory, "sender", Blob(root.Narrow(StyxCaveat.Host("sender"), StyxCaveat.Id("s")), key)),
            HydraTestClient.Master(Factory, "receiver", Blob(root.Narrow(StyxCaveat.HostNot("sender"), StyxCaveat.Id("r")), key)));
        var (sender, receiver) = pair;

        sender.Send(["receiver"], TestMessages.Move(42, 99));
        var (source, kind, _) = await receiver.WaitForMessage();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source, Is.EqualTo("sender"));
            Assert.That(kind, Is.EqualTo(MessageKind.MouseMove));
        }
    }

    [Test]
    public async Task AHandRolledJsonClient_SeesTheSameCompletionForV1AndV2()
    {
        var v1 = await RawLogin(await StyxTestServer.GenerateAuthorization(Guid.NewGuid()), "raw-v1");
        var v2 = await RawLogin(StyxTestServer.MintToken(null, StyxCaveat.HostNot("admin")).ToString(), "raw-v2");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(v1, Is.EqualTo("""{"type":3,"invocationId":"1","result":{"authenticated":true,"message":null}}"""));
            Assert.That(v2, Is.EqualTo(v1));
        }
    }

    // one end-to-end key for the whole network, as every member's blob carries
    private string Blob(StyxToken token, string encryptionKey) =>
        new StyxBlob(StyxTestServer.ServerUrl(Factory), encryptionKey, token.ToString()).Write();

    // the completion record for an Authenticate sent over a raw WebSocket, as the firmware sends it
    private async Task<string> RawLogin(string authorization, string hostName)
    {
        _pending.Clear();
        using var cancel = new CancellationTokenSource(Bound);
        var socket = await Factory.Server.CreateWebSocketClient().ConnectAsync(new Uri(Factory.Server.BaseAddress, "relay"), cancel.Token);
        await SendRecord(socket, """{"protocol":"json","version":1}""", cancel.Token);
        Assert.That(await ReadRecord(socket, cancel.Token), Is.EqualTo("{}"));

        await SendRecord(socket, $$"""{"type":1,"invocationId":"1","target":"Authenticate","arguments":[{"authorization":"{{authorization}}","hostName":"{{hostName}}"}]}""",
            cancel.Token);
        while (true)
        {
            var record = await ReadRecord(socket, cancel.Token);
            if (!record.StartsWith("""{"type":3""", StringComparison.Ordinal)) continue;
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancel.Token);
            return record;
        }
    }

    private static Task SendRecord(WebSocket socket, string json, CancellationToken cancel) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json + "\u001e"), WebSocketMessageType.Text, true, cancel);

    private readonly Queue<string> _pending = new();

    private async Task<string> ReadRecord(WebSocket socket, CancellationToken cancel)
    {
        while (_pending.Count == 0)
        {
            var buffer = new byte[8192];
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancel);
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            foreach (var record in Encoding.UTF8.GetString(message.ToArray()).Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
                _pending.Enqueue(record);
        }
        return _pending.Dequeue();
    }
}
