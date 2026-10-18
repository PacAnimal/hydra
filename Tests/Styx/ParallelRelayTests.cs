using Styx;
using Tests.Setup;

namespace Tests.Styx;

/// <summary>
/// <c>/relay/parallel</c> is the same relay as <c>/relay</c>: the same login and one set of peers, whichever path each of
/// them connected on.
/// </summary>
[TestFixture]
public class ParallelRelayTests : StyxFixtureBase
{
    private const string Parallel = Constants.ParallelRelayPath;
    private const string Ordered = Constants.RelayPath;

    [TestCase(Ordered, Parallel)]
    [TestCase(Parallel, Ordered)]
    [TestCase(Parallel, Parallel)]
    public async Task PeersOnEitherPath_SeeEachOther_AndExchangeSends(string firstPath, string secondPath)
    {
        await using var peers = await ConnectedPeers(name => name == "first" ? firstPath : secondPath, "first", "second");
        var (first, second) = (peers["first"], peers["second"]);

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

        Assert.That(await holder.WaitForKick(), Is.EqualTo(RelayReasons.KickedDuplicateHostname));
    }
}
