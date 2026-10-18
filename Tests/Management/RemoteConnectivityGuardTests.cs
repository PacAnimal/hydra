using Hydra.Management;
using static Tests.Setup.ConfigJson;

namespace Tests.Management;

public class RemoteConnectivityGuardTests
{
    [Test]
    public void ChangingTheDuplicateTheLoaderUses_IsRisky()
    {
        var current = AsFile("""[{"mode":"Slave","networkConfig":"shadowed","NetworkConfig":"effective"}]""");
        var candidate = AsFile("""[{"mode":"Slave","networkConfig":"shadowed","NetworkConfig":"different"}]""");

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
