using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Net.Sockets;

namespace Hydra.Management;

internal sealed partial record ManagementEndpoint(string InstanceId, string Address, bool IsNamedPipe)
{
    internal static ManagementEndpoint ForConfig(string configPath)
    {
        var canonical = Path.GetFullPath(configPath);
        if (OperatingSystem.IsWindows()) canonical = canonical.ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()[..12];
        if (OperatingSystem.IsWindows())
            return new ManagementEndpoint(hash, $"hydra-{hash}", true);

        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(runtime) && Directory.Exists($"/run/user/{GetEffectiveUserId()}"))
            runtime = $"/run/user/{GetEffectiveUserId()}";
        if (string.IsNullOrWhiteSpace(runtime))
            runtime = Path.Combine(Path.GetTempPath(), $"hydra-{GetEffectiveUserId()}");

        var directory = Path.Combine(runtime, "hydra");
        var directoryInfo = Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows() && directoryInfo.LinkTarget != null)
            throw new IOException($"Hydra management directory cannot be a symbolic link: {directory}");
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new ManagementEndpoint(hash, Path.Combine(directory, $"{hash}.sock"), false);
    }

    internal async Task<bool> RemoveStaleUnixSocketAsync(CancellationToken cancel)
    {
        if (IsNamedPipe || !File.Exists(Address)) return true;

        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(250));
        try
        {
            await probe.ConnectAsync(new UnixDomainSocketEndPoint(Address), timeout.Token);
            return false;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            File.Delete(Address);
            return true;
        }
    }

    private static uint GetEffectiveUserId() => geteuid();

    [LibraryImport("libc")]
    private static partial uint geteuid();
}
