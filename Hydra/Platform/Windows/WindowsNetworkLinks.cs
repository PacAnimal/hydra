using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Hydra.Platform.Windows;

[SupportedOSPlatform("windows")]
internal sealed partial class WindowsNetworkLinks : INetworkLinkSource
{
    private const ushort AfInet = 2;
    private const uint ErrorNotFound = 1168;

    // MIB_IF_ROW2 (netioapi.h), only the fields read here
    private const int IfRowSize = 1352;
    private const int IfIndexOffset = 8;
    private const int IfTypeOffset = 1128;
    private const int IfPhysicalMediumOffset = 1140;
    private const int IfFlagsOffset = 1152;
    private const int IfOperStatusOffset = 1156;
    private const uint IfOperStatusUp = 1;
    private const uint IfTypeEthernetCsmacd = 6;
    private const byte HardwareInterfaceFlag = 0x1;
    private const byte FilterInterfaceFlag = 0x2;
    private const uint PhysicalMediumUnspecified = 0;
    private const uint PhysicalMedium8023 = 14;

    // MIB_IPNET_ROW2, only the fields read here: a SOCKADDR_INET (sin_family, sin_port, sin_addr), then the hardware address
    private const int NetRowSize = 88;
    private const int NetAddressOffset = 4;
    private const int NetMacOffset = 40;
    private const int NetMacLengthOffset = 72;
    private const int NetStateOffset = 76;
    // NlnsUnreachable, NlnsIncomplete: no hardware address yet
    private const uint FirstResolvedState = 2;

    // from the OS's own table rather than .NET's list, which leaves out the NIC under a Hyper-V external switch: no IP
    // stack is bound to it, its vEthernet holds the address
    public bool HasWiredLink(IReadOnlyList<NetworkInterface> live) => WiredLinks().Count > 0;

    // the interface index of each Ethernet adapter that is up and has the hardware flag, which Hyper-V's vEthernet, VPN
    // and other virtual adapters lack; Wi-Fi has its own type
    internal static unsafe List<int> WiredLinks()
    {
        var error = GetIfTable2(out var table);
        if (error != 0) throw new Win32Exception((int)error);
        try
        {
            var count = *(uint*)table;
            var wired = new List<int>();
            for (var i = 0; i < count; i++)
            {
                var row = (byte*)table + IpInterfaceTable.RowsOffset + (i * IfRowSize);
                var physicalMedium = *(uint*)(row + IfPhysicalMediumOffset);
                // a lightweight filter stacked on a NIC has a row of its own, mirroring the NIC's
                var flags = row[IfFlagsOffset];
                if (*(uint*)(row + IfTypeOffset) == IfTypeEthernetCsmacd && (flags & HardwareInterfaceFlag) != 0 && (flags & FilterInterfaceFlag) == 0
                    && physicalMedium is PhysicalMediumUnspecified or PhysicalMedium8023 && *(uint*)(row + IfOperStatusOffset) == IfOperStatusUp)
                    wired.Add((int)*(uint*)(row + IfIndexOffset));
            }
            return wired;
        }
        finally
        {
            IpInterfaceTable.FreeMibTable(table);
        }
    }

    public async ValueTask<IReadOnlyList<Neighbour>> Neighbours(IReadOnlyList<IPAddress> gateways, CancellationToken cancel)
    {
        await ValueTask.CompletedTask;
        return ReadNeighbours();
    }

    public void Resolve(IReadOnlyList<IPAddress> gateways)
    {
        foreach (var gateway in gateways) NetworkLinkReader.Poke(gateway);
    }

    internal static unsafe List<Neighbour> ReadNeighbours()
    {
        var error = GetIpNetTable2(AfInet, out var table);
        if (error == ErrorNotFound) return [];
        if (error != 0) throw new Win32Exception((int)error);
        try
        {
            var count = *(uint*)table;
            var neighbours = new List<Neighbour>((int)count);
            for (var i = 0; i < count; i++)
            {
                var row = (byte*)table + IpInterfaceTable.RowsOffset + (i * NetRowSize);
                if (*(uint*)(row + NetStateOffset) < FirstResolvedState || *(uint*)(row + NetMacLengthOffset) != 6) continue;
                var address = new IPAddress(new ReadOnlySpan<byte>(row + NetAddressOffset, 4));
                neighbours.Add(new Neighbour(address, new PhysicalAddress(new ReadOnlySpan<byte>(row + NetMacOffset, 6).ToArray())));
            }
            return neighbours;
        }
        finally
        {
            IpInterfaceTable.FreeMibTable(table);
        }
    }

    [LibraryImport(IpInterfaceTable.Iphlpapi)]
    private static partial uint GetIfTable2(out nint table);

    [LibraryImport(IpInterfaceTable.Iphlpapi)]
    private static partial uint GetIpNetTable2(ushort family, out nint table);
}
