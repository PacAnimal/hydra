using Tests.Setup;

namespace Tests.Styx;

[TestFixture]
public class ClientPairTests : StyxFixtureBase
{
    // a client left running holds its hub connection and reconnect loop until the fixture's server goes
    [Test]
    public async Task ASetupThatFailsPartwayDisposesBothClients()
    {
        var network = Guid.NewGuid();
        await using var observer = new TestStyxClient();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((await observer.Connect(Factory, await StyxTestServer.GenerateAuthorization(network), "observer")).Authenticated, Is.True);
            Assert.That(await observer.WaitForPeers(), Is.Empty);
        }

        // the receiver's login is refused, so once the sender is up the setup can only fail
        await using var sender = HydraTestClient.Master(Factory, "sender", await StyxTestServer.BuildNetworkConfig(Factory, network));
        await using var receiver = HydraTestClient.Master(Factory, "receiver",
            await StyxTestServer.BuildNetworkConfig(Factory, network, password: "wrong-password"));
        using var giveUp = new CancellationTokenSource();
        var starting = ClientPair.Start(sender, receiver, giveUp.Token);

        Assert.That(await observer.WaitForPeers(15000), Is.EqualTo(["sender"]));
        await giveUp.CancelAsync();

        Assert.That(async () => await starting, Throws.InstanceOf<OperationCanceledException>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sender.Disposed, Is.True, "the client that had connected was left running");
            Assert.That(receiver.Disposed, Is.True, "the client still trying to log in was left running");
            Assert.That(await observer.WaitForPeers(15000), Is.Empty, "the relay still has the connected client");
        }
    }
}
