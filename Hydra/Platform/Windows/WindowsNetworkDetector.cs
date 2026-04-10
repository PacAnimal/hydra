using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Hydra.Platform.Windows;

internal sealed partial class WindowsNetworkDetector : INetworkDetector
{
    public Task<List<string>> GetActiveSsids(CancellationToken cancel = default) =>
        Task.FromResult(GetWifiSsids());

    private static List<string> GetWifiSsids()
    {
        var results = new List<string>();

        if (WlanOpenHandle(2, nint.Zero, out _, out var handle) != 0)
            return results;

        try
        {
            if (WlanEnumInterfaces(handle, nint.Zero, out var ifaceListPtr) != 0)
                return results;

            try
            {
                var count = Marshal.ReadInt32(ifaceListPtr); // dwNumberOfItems at offset 0
                var itemStart = ifaceListPtr + 8; // items start after dwNumberOfItems(4) + dwIndex(4)

                for (var i = 0; i < count; i++)
                {
                    var ifacePtr = itemStart + i * WlanInterfaceInfoSize;
                    var ssid = QuerySsid(handle, ifacePtr);
                    if (ssid != null)
                        results.Add(ssid);
                }
            }
            finally
            {
                WlanFreeMemory(ifaceListPtr);
            }
        }
        finally
        {
            _ = WlanCloseHandle(handle, nint.Zero);
        }

        return results;
    }

    private static string? QuerySsid(nint handle, nint ifaceInfoPtr)
    {
        // GUID is at the start of WLAN_INTERFACE_INFO
        var guid = Marshal.PtrToStructure<Guid>(ifaceInfoPtr);

        const uint wlanIntfOpcodeCurrentConnection = 7;
        if (WlanQueryInterface(handle, ref guid, wlanIntfOpcodeCurrentConnection, nint.Zero,
                out _, out var dataPtr, out _) != 0)
            return null;

        try
        {
            // WLAN_CONNECTION_ATTRIBUTES layout:
            // isState (4) + wlanConnectionMode (4) + strProfileName (512) + wlanAssociationAttributes
            // wlanAssociationAttributes: dot11Ssid is first field
            // dot11Ssid: uSSIDLength (4) + ucSSID[32]
            var assocOffset = 4 + 4 + 512; // isState + mode + profileName
            var ssidLenOffset = assocOffset;
            var ssidDataOffset = assocOffset + 4;

            var ssidLen = Marshal.ReadInt32(dataPtr, ssidLenOffset);
            if (ssidLen <= 0 || ssidLen > 32) return null;

            var ssidBytes = new byte[ssidLen];
            Marshal.Copy(dataPtr + ssidDataOffset, ssidBytes, 0, ssidLen);
            return Encoding.UTF8.GetString(ssidBytes);
        }
        finally
        {
            WlanFreeMemory(dataPtr);
        }
    }

    // WLAN_INTERFACE_INFO size: GUID(16) + strInterfaceDescription(512) + isState(4) = 532 bytes
    private const int WlanInterfaceInfoSize = 532;

    [LibraryImport("wlanapi.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint WlanOpenHandle(uint dwClientVersion, nint pReserved, out uint pdwNegotiatedVersion, out nint phClientHandle);

    [LibraryImport("wlanapi.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint WlanCloseHandle(nint hClientHandle, nint pReserved);

    [LibraryImport("wlanapi.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint WlanEnumInterfaces(nint hClientHandle, nint pReserved, out nint ppInterfaceList);

    [LibraryImport("wlanapi.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint WlanQueryInterface(nint hClientHandle, ref Guid pInterfaceGuid, uint wlanIntfOpcode,
        nint pReserved, out uint pdwDataSize, out nint ppData, out uint pWlanOpcodeValueType);

    [LibraryImport("wlanapi.dll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial void WlanFreeMemory(nint pMemory);
}
