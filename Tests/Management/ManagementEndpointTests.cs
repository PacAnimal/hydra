using System.Runtime.Versioning;
using Hydra.Management;
using System.Net.Sockets;
using Tests.Setup;

namespace Tests.Management;

public class ManagementEndpointTests
{
    [Test]
    public void ForConfig_IsStableAndSeparatesInstances()
    {
        var first = ManagementEndpoint.ForConfig(Path.Combine(TestContext.CurrentContext.WorkDirectory, "one", "hydra.conf"));
        var again = ManagementEndpoint.ForConfig(Path.Combine(TestContext.CurrentContext.WorkDirectory, "one", "hydra.conf"));
        var second = ManagementEndpoint.ForConfig(Path.Combine(TestContext.CurrentContext.WorkDirectory, "two", "hydra.conf"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo(again));
            Assert.That(second.InstanceId, Is.Not.EqualTo(first.InstanceId));
            Assert.That(first.InstanceId, Has.Length.EqualTo(12));
        }
    }

    // the id names the socket or pipe, so a TUI and a daemon of different versions must agree on it
    [Test]
    public void ForConfig_InstanceIdIsTheConfigPathsSha256Prefix()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Windows upper-cases the path first");

        var endpoint = ManagementEndpoint.ForConfig("/etc/hydra/hydra.conf");

        Assert.That(endpoint.InstanceId, Is.EqualTo("569f987a0058"));
    }

    [Test]
    public void ForConfig_NormalizesWindowsPathCasing()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows path identity test");
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Hydra", "hydra.conf");

        var mixedCase = ManagementEndpoint.ForConfig(path);
        var upperCase = ManagementEndpoint.ForConfig(path.ToUpperInvariant());

        Assert.That(mixedCase.InstanceId, Is.EqualTo(upperCase.InstanceId));
    }

    [Test]
    [UnsupportedOSPlatform("windows")]
    public void ForConfig_UsesPrivateUnixRuntimeDirectory()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Unix permission test");
        var endpoint = ManagementEndpoint.ForConfig(Path.Combine(TestContext.CurrentContext.WorkDirectory, "hydra.conf"));
        var directory = Path.GetDirectoryName(endpoint.Address)!;
        var mode = File.GetUnixFileMode(directory);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(endpoint.IsNamedPipe, Is.False);
            Assert.That(mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute), Is.EqualTo(UnixFileMode.None));
        }
    }

    [Test]
    public async Task RemoveStaleUnixSocket_PreservesActiveEndpointAndDeletesStaleOne()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Unix socket test");
        var endpoint = ManagementEndpoint.ForConfig(Path.Combine(TestPaths.FreshFixtureRoot(nameof(ManagementEndpointTests)), "stale.conf"));
        if (File.Exists(endpoint.Address)) File.Delete(endpoint.Address);

        using (var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            listener.Bind(new UnixDomainSocketEndPoint(endpoint.Address));
            listener.Listen(1);
            Assert.That(await endpoint.RemoveStaleUnixSocketAsync(CancellationToken.None), Is.False);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await endpoint.RemoveStaleUnixSocketAsync(CancellationToken.None), Is.True);
            Assert.That(File.Exists(endpoint.Address), Is.False);
        }
    }
}
