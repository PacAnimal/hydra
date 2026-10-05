using Hydra.Management;

namespace Tests.Management;

public class RemoteConnectivityGuardTests
{
    [Test]
    public void ChangingTheDuplicateTheLoaderUses_IsRisky()
    {
        const string current = """{"profiles":[{"mode":"Slave","networkConfig":"shadowed","NetworkConfig":"effective"}]}""";
        const string candidate = """{"profiles":[{"mode":"Slave","networkConfig":"shadowed","NetworkConfig":"different"}]}""";

        Assert.That(RemoteConnectivityGuard.FindRiskyChanges(current, candidate), Is.EquivalentTo(["profile 1 networkConfig"]));
    }

    [Test]
    public void ChangingOnlyTheCaseOfAKey_IsNotRisky()
    {
        const string current = """{"profiles":[{"mode":"Slave","networkConfig":"same"}]}""";
        const string candidate = """{"Profiles":[{"Mode":"Slave","NetworkConfig":"same"}]}""";

        Assert.That(RemoteConnectivityGuard.FindRiskyChanges(current, candidate), Is.Empty);
    }
}
