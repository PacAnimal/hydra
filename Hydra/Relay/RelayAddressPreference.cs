using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Hydra.Platform.Windows;

namespace Hydra.Relay;

internal static partial class RelayAddressPreference
{
    private static readonly TimeSpan PreferenceQueryTimeout = TimeSpan.FromSeconds(2);

    internal static async Task<IReadOnlyList<IPAddress>> OrderAsync(
        IReadOnlyList<IPAddress> addresses,
        int port,
        CancellationToken cancellationToken) =>
        await OrderAsync(addresses, address => FindRouteInterface(address, port), InterfacePreferenceCache.Shared.GetAsync, cancellationToken);

    internal static async Task<IReadOnlyList<IPAddress>> OrderAsync(
        IReadOnlyList<IPAddress> addresses,
        Func<IPAddress, string?> interfaceResolver,
        Func<CancellationToken, Task<IReadOnlyDictionary<string, int>>> preferencesQuery,
        CancellationToken cancellationToken)
    {
        var routes = addresses.Distinct().ToDictionary(address => address, interfaceResolver);
        // one route has nothing to be preferred over, and the query spawns a process on macOS and Linux
        if (routes.Values.Distinct().Count() <= 1) return addresses;

        var preferences = await preferencesQuery(cancellationToken);
        if (preferences.Count == 0) return addresses;

        return OrderByInterfacePreference(addresses, address => routes[address], preferences);
    }

    internal static IReadOnlyList<IPAddress> OrderByInterfacePreference(
        IReadOnlyList<IPAddress> addresses,
        Func<IPAddress, string?> interfaceResolver,
        IReadOnlyDictionary<string, int> preferences) =>
        [.. addresses
            .Select((address, index) => new
            {
                Address = address,
                OriginalIndex = index,
                Preference = GetPreference(address, interfaceResolver, preferences)
            })
            .OrderBy(candidate => candidate.Preference)
            .ThenBy(candidate => candidate.OriginalIndex)
            .Select(candidate => candidate.Address)];

    private static int GetPreference(
        IPAddress address,
        Func<IPAddress, string?> interfaceResolver,
        IReadOnlyDictionary<string, int> preferences)
    {
        var interfaceName = interfaceResolver(address);
        return interfaceName != null && preferences.TryGetValue(interfaceName, out var preference)
            ? preference
            : int.MaxValue;
    }

    internal static async Task<IReadOnlyDictionary<string, int>> QueryInterfacePreferencesAsync(
        CancellationToken cancellationToken)
    {
        using var queryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        queryCancellation.CancelAfter(PreferenceQueryTimeout);
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var output = await RunAsync("/usr/sbin/networksetup", ["-listnetworkserviceorder"], queryCancellation.Token);
                return output == null ? EmptyPreferences() : ParseMacServiceOrder(output);
            }

            if (OperatingSystem.IsLinux())
            {
                var ip = File.Exists("/usr/sbin/ip") ? "/usr/sbin/ip" : File.Exists("/sbin/ip") ? "/sbin/ip" : "ip";
                var ipv4 = await RunAsync(ip, ["-o", "route", "show", "default"], queryCancellation.Token);
                var ipv6 = await RunAsync(ip, ["-o", "-6", "route", "show", "default"], queryCancellation.Token);
                return ParseLinuxDefaultRoutes(string.Join('\n', ipv4, ipv6));
            }

            if (OperatingSystem.IsWindows()) return PreferencesFromMetrics(IpInterfaceTable.ConnectedMetrics());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return EmptyPreferences();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Preference discovery is advisory. DNS ordering and the per-address connection fallback remain
            // available if a platform command is missing, restricted, or returns an unexpected result.
        }

        return EmptyPreferences();
    }

    internal static string? FindRouteInterface(IPAddress address, int port)
    {
        try
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(address, port));
            return socket.LocalEndPoint is IPEndPoint local ? NetworkInterfaceLookup.FindByAddress(local.Address)?.Name : null;
        }
        catch (Exception exception) when (exception is SocketException or NetworkInformationException)
        {
            return null;
        }
    }

    internal static IReadOnlyDictionary<string, int> ParseMacServiceOrder(string output)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        int? order = null;
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var orderMatch = MacOrderLine().Match(line);
            if (orderMatch.Success)
            {
                order = int.Parse(orderMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                continue;
            }

            if (order == null) continue;
            var deviceMatch = MacDeviceLine().Match(line);
            if (!deviceMatch.Success) continue;
            var device = deviceMatch.Groups[1].Value.Trim();
            if (device.Length > 0) result.TryAdd(device, order.Value);
            order = null;
        }

        return result;
    }

    internal static IReadOnlyDictionary<string, int> ParseLinuxDefaultRoutes(string output)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = 0;
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var deviceMatch = LinuxDevice().Match(line);
            if (!deviceMatch.Success) continue;
            var metricMatch = LinuxMetric().Match(line);
            var metric = metricMatch.Success
                ? int.Parse(metricMatch.Groups[1].Value, CultureInfo.InvariantCulture)
                : 0;
            var preference = (int)Math.Min(((long)metric * 1000) + Math.Min(order++, 999), int.MaxValue - 1L);
            var device = deviceMatch.Groups[1].Value;
            if (!result.TryGetValue(device, out var current) || preference < current)
                result[device] = preference;
        }

        return result;
    }

    internal static IReadOnlyDictionary<string, int> PreferencesFromMetrics(IEnumerable<InterfaceMetric> metrics) =>
        PreferencesFromMetrics(metrics, index => NetworkInterfaceLookup.FindByIndex(index, AddressFamily.InterNetwork)?.Name
            ?? NetworkInterfaceLookup.FindByIndex(index, AddressFamily.InterNetworkV6)?.Name);

    internal static IReadOnlyDictionary<string, int> PreferencesFromMetrics(
        IEnumerable<InterfaceMetric> metrics,
        Func<int, string?> interfaceResolver)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var metric in metrics)
        {
            var interfaceName = interfaceResolver(metric.Index);
            if (interfaceName != null && (!result.TryGetValue(interfaceName, out var current) || metric.Metric < current))
                result[interfaceName] = metric.Metric;
        }

        return result;
    }

    private static async Task<string?> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process();
        process.StartInfo = startInfo;
        if (!process.Start()) return null;
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await stderr;
            return process.ExitCode == 0 ? await stdout : null;
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Best-effort cleanup — the process may have exited between HasExited and Kill, or
                // Kill itself may fail for other reasons. Either way this must not replace the
                // cancellation being propagated below with an unrelated exception from the cleanup.
            }
            throw;
        }
    }

    private static Dictionary<string, int> EmptyPreferences() =>
        new(StringComparer.Ordinal);

    [GeneratedRegex(@"^\((\d+)\)\s")]
    private static partial Regex MacOrderLine();

    [GeneratedRegex(@"Device:\s*([^)]*)\)")]
    private static partial Regex MacDeviceLine();

    [GeneratedRegex(@"(?:^|\s)dev\s+(\S+)")]
    private static partial Regex LinuxDevice();

    [GeneratedRegex(@"(?:^|\s)metric\s+(\d+)")]
    private static partial Regex LinuxMetric();
}
