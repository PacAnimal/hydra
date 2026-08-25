using Hydra.Management;
using System.Net.Sockets;

namespace Tests.Management;

public class ManagementEndpointTests
{
    [Test]
    public void ForConfig_IsStableAndSeparatesInstances()
    {
        var first = ManagementEndpoint.ForConfig(Path.Combine(TestContext.CurrentContext.WorkDirectory, "one", "hydra.conf"));
        var again = ManagementEndpoint.ForConfig(Path.Combine(TestContext.CurrentContext.WorkDirectory, "one", "hydra.conf"));
        var second = ManagementEndpoint.ForConfig(Path.Combine(TestContext.CurrentContext.WorkDirectory, "two", "hydra.conf"));

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(again));
            Assert.That(second.InstanceId, Is.Not.EqualTo(first.InstanceId));
            Assert.That(first.InstanceId, Has.Length.EqualTo(12));
        });
    }

    [Test]
    public void ForConfig_UsesPrivateUnixRuntimeDirectory()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Unix permission test");
        var endpoint = ManagementEndpoint.ForConfig(Path.Combine(TestContext.CurrentContext.WorkDirectory, "hydra.conf"));
        var directory = Path.GetDirectoryName(endpoint.Address)!;
#pragma warning disable CA1416
        var mode = File.GetUnixFileMode(directory);
#pragma warning restore CA1416

        Assert.Multiple(() =>
        {
            Assert.That(endpoint.IsNamedPipe, Is.False);
            Assert.That(mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute), Is.EqualTo(UnixFileMode.None));
        });
    }

    [Test]
    public async Task RemoveStaleUnixSocket_PreservesActiveEndpointAndDeletesStaleOne()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Unix socket test");
        var endpoint = ManagementEndpoint.ForConfig(Path.Combine(TestContext.CurrentContext.WorkDirectory, $"stale-{Guid.NewGuid():N}.conf"));
        if (File.Exists(endpoint.Address)) File.Delete(endpoint.Address);

        using (var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            listener.Bind(new UnixDomainSocketEndPoint(endpoint.Address));
            listener.Listen(1);
            Assert.That(await endpoint.RemoveStaleUnixSocketAsync(CancellationToken.None), Is.False);
        }

        Assert.That(await endpoint.RemoveStaleUnixSocketAsync(CancellationToken.None), Is.True);
        Assert.That(File.Exists(endpoint.Address), Is.False);
    }
}
