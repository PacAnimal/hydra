using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Hydra.Relay;

internal static partial class RelayAddressPreference
{
    private static readonly TimeSpan PreferenceQueryTimeout = TimeSpan.FromSeconds(2);

    internal static async Task<IReadOnlyList<IPAddress>> OrderAsync(
        IReadOnlyList<IPAddress> addresses,
        int port,
        CancellationToken cancellationToken)
    {
        var preferences = await GetInterfacePreferencesAsync(cancellationToken);
        if (preferences.Count == 0) return addresses;

        return OrderByInterfacePreference(addresses, address => FindRouteInterface(address, port), preferences);
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

    private static async Task<IReadOnlyDictionary<string, int>> GetInterfacePreferencesAsync(
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

            if (OperatingSystem.IsWindows())
            {
                const string script = "Get-NetIPInterface -ConnectionState Connected | "
                    + "Sort-Object InterfaceMetric,InterfaceIndex | "
                    + "ForEach-Object { '{0}|{1}' -f $_.InterfaceIndex,$_.InterfaceMetric }";
                var output = await RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], queryCancellation.Token);
                return output == null ? EmptyPreferences() : ParseWindowsInterfaceMetrics(output);
            }
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

    private static string? FindRouteInterface(IPAddress address, int port)
    {
        try
        {
            if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId > 0)
                return FindInterfaceByIndex((int)address.ScopeId, AddressFamily.InterNetworkV6)?.Name;

            using var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(address, port));
            return socket.LocalEndPoint is IPEndPoint local ? FindInterfaceByAddress(local.Address)?.Name : null;
        }
        catch (Exception exception) when (exception is SocketException or NetworkInformationException)
        {
            return null;
        }
    }

    private static NetworkInterface? FindInterfaceByAddress(IPAddress address)
    {
        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(network =>
            network.GetIPProperties().UnicastAddresses.Any(unicast =>
            {
                var candidate = unicast.Address.IsIPv4MappedToIPv6 ? unicast.Address.MapToIPv4() : unicast.Address;
                return candidate.Equals(normalized);
            }));
    }

    private static NetworkInterface? FindInterfaceByIndex(int index, AddressFamily family) =>
        NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(network =>
        {
            var properties = network.GetIPProperties();
            return family == AddressFamily.InterNetwork
                ? properties.GetIPv4Properties()?.Index == index
                : properties.GetIPv6Properties()?.Index == index;
        });

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

    internal static IReadOnlyDictionary<string, int> ParseWindowsInterfaceMetrics(string output) =>
        ParseWindowsInterfaceMetrics(output, index => FindInterfaceByIndex(index, AddressFamily.InterNetwork)?.Name
            ?? FindInterfaceByIndex(index, AddressFamily.InterNetworkV6)?.Name);

    internal static IReadOnlyDictionary<string, int> ParseWindowsInterfaceMetrics(
        string output,
        Func<int, string?> interfaceResolver)
    {
        var metrics = new Dictionary<int, int>();
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length == 2
                && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var index)
                && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var metric))
                metrics[index] = metric;
        }

        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (index, metric) in metrics)
        {
            var interfaceName = interfaceResolver(index);
            if (interfaceName != null && (!result.TryGetValue(interfaceName, out var current) || metric < current))
                result[interfaceName] = metric;
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

        using var process = new Process { StartInfo = startInfo };
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
            catch (InvalidOperationException)
            {
                // The process exited between HasExited and Kill.
            }
            throw;
        }
    }

    private static IReadOnlyDictionary<string, int> EmptyPreferences() =>
        new Dictionary<string, int>(StringComparer.Ordinal);

    [GeneratedRegex(@"^\((\d+)\)\s")]
    private static partial Regex MacOrderLine();

    [GeneratedRegex(@"Device:\s*([^)]*)\)")]
    private static partial Regex MacDeviceLine();

    [GeneratedRegex(@"(?:^|\s)dev\s+(\S+)")]
    private static partial Regex LinuxDevice();

    [GeneratedRegex(@"(?:^|\s)metric\s+(\d+)")]
    private static partial Regex LinuxMetric();
}
