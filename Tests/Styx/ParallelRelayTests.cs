using Tests.Setup;

namespace Tests.Styx;

/// <summary>
/// <c>/relay/parallel</c> is the same relay as <c>/relay</c>: the same login, the same refusal of an unauthenticated
/// caller, and one set of peers, whichever path each of them connected on.
/// </summary>
[TestFixture]
public class ParallelRelayTests : StyxFixtureBase
{
    private const string Parallel = global::Styx.Constants.ParallelRelayPath;
    private const string Ordered = global::Styx.Constants.RelayPath;

    [Test]
    public async Task Ping_WithoutAuth_ReturnsPong()
    {
        await using var client = new TestStyxClient();
        await client.ConnectRaw(Factory, Parallel);
        Assert.That(await client.Server!.Ping(), Is.True);
    }

    [Test]
    public async Task Send_WithoutAuth_FailsWithException()
    {
        await using var client = new TestStyxClient();
        await client.ConnectRaw(Factory, Parallel);
        Exception? ex = null;
        try { await client.Server!.Send(["any-host"], [1, 2, 3]); } catch (Exception e) { ex = e; }
        Assert.That(ex, Is.Not.Null);
    }

    [TestCase(Ordered, Parallel)]
    [TestCase(Parallel, Ordered)]
    [TestCase(Parallel, Parallel)]
    public async Task PeersOnEitherPath_SeeEachOther_AndExchangeSends(string firstPath, string secondPath)
    {
        var token = StyxTestServer.MintToken().ToString();
        await using var first = new TestStyxClient();
        await using var second = new TestStyxClient();
        await first.Connect(Factory, token, "first", firstPath);
        await second.Connect(Factory, token, "second", secondPath);

        await WaitForPeers(first, ["second"]);
        await WaitForPeers(second, ["first"]);

        await first.Server!.Send(["second"], [1]);
        await second.Server!.Send(["first"], [2]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await second.NextReceived(), Is.EqualTo(new byte[] { 1 }));
            Assert.That(await first.NextReceived(), Is.EqualTo(new byte[] { 2 }));
        }
    }

    [TestCase(Ordered, Parallel)]
    [TestCase(Parallel, Ordered)]
    public async Task ADuplicateLoginOnTheOtherPath_KicksTheOldHolder(string holderPath, string newcomerPath)
    {
        var token = StyxTestServer.MintToken().ToString();
        await using var holder = new TestStyxClient();
        await using var newcomer = new TestStyxClient();
        await holder.Connect(Factory, token, "twin", holderPath);
        await newcomer.Connect(Factory, token, "twin", newcomerPath);

        Assert.That(await holder.WaitForKick(), Is.EqualTo("duplicate hostname"));
    }

    // the first snapshot that matches, since the relay may announce intermediate memberships first
    private static async Task WaitForPeers(TestStyxClient client, string[] expected)
    {
        while (true)
        {
            if ((await client.WaitForPeers()).Order().SequenceEqual(expected.Order())) return;
        }
    }
}
