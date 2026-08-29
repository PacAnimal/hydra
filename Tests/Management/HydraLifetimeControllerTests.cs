using Hydra.Management;

namespace Tests.Management;

public class HydraLifetimeControllerTests
{
    [TestCase(false, false, true)]
    [TestCase(true, false, true)]
    [TestCase(true, true, false)]
    public void ShutdownSupportAccountsForTheWindowsServiceSupervisor(
        bool isWindows, bool isSessionChild, bool expected)
    {
        Assert.That(HydraLifetimeController.CanShutdown(isWindows, isSessionChild), Is.EqualTo(expected));
    }
}
