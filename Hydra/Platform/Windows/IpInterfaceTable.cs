using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Hydra.Relay;

namespace Hydra.Platform.Windows;

/// <summary>The IP Helper interface table: what Get-NetIPInterface reads, without spawning PowerShell to read it.</summary>
[SupportedOSPlatform("windows")]
internal static partial class IpInterfaceTable
{
    private const string Iphlpapi = "iphlpapi.dll";
    private const ushort AfUnspec = 0;

    // MIB_IPINTERFACE_TABLE (netioapi.h): a ULONG count, then rows aligned to their 8-byte LUID
    private const int RowsOffset = 8;

    // MIB_IPINTERFACE_ROW, only the fields read here
    private const int RowSize = 168;
    private const int InterfaceIndexOffset = 16;
    private const int MetricOffset = 148;
    private const int ConnectedOffset = 156;

    // both families, so an interface may appear twice with different metrics
    internal static unsafe IReadOnlyList<InterfaceMetric> ConnectedMetrics()
    {
        var error = GetIpInterfaceTable(AfUnspec, out var table);
        if (error != 0) throw new Win32Exception((int)error);
        try
        {
            var count = *(uint*)table;
            var metrics = new List<InterfaceMetric>((int)count);
            for (var i = 0; i < count; i++)
            {
                var row = (byte*)table + RowsOffset + (i * RowSize);
                if (row[ConnectedOffset] != 0)
                    metrics.Add(new InterfaceMetric((int)*(uint*)(row + InterfaceIndexOffset), (int)*(uint*)(row + MetricOffset)));
            }

            return metrics;
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    [LibraryImport(Iphlpapi)]
    private static partial uint GetIpInterfaceTable(ushort family, out nint table);

    [LibraryImport(Iphlpapi)]
    private static partial void FreeMibTable(nint memory);
}
