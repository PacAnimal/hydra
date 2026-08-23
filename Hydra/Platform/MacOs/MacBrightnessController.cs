using System.Runtime.InteropServices;

namespace Hydra.Platform.MacOs;

// macOS brightness routing: use DisplayServices when the OS owns a display's brightness, then
// fall back to DDC/CI for a generic external monitor. Both are private APIs, resolved lazily so
// unsupported macOS versions retain Hydra's legacy media-key fallback.
internal sealed class MacBrightnessController
{
    private const string DisplayServices = "/System/Library/PrivateFrameworks/DisplayServices.framework/DisplayServices";
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const int KernSuccess = 0;
    private const uint DdcDisplayAddress = 0x37;
    private const uint DdcHostAddress = 0x51;
    private const byte BrightnessVcpCode = 0x10;
    private const double Step = 0.05;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetBrightnessDelegate(uint displayId, out float brightness);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetBrightnessDelegate(uint displayId, float brightness);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte CanChangeBrightnessDelegate(uint displayId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int BrightnessChangedDelegate(uint displayId, double brightness);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint CreateWithServiceDelegate(nint allocator, uint service);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int I2cDelegate(nint avService, uint address, uint senderAddress, byte* data, uint length);

    private readonly object _gate = new();
    private readonly MacBetterDisplayController _betterDisplay = new();
    private readonly nint _displayServicesHandle = LoadLibrary(DisplayServices);
    private readonly nint _ioKitHandle = LoadLibrary(IOKit);
    private readonly GetBrightnessDelegate? _getDisplayBrightness;
    private readonly SetBrightnessDelegate? _setDisplayBrightness;
    private readonly CanChangeBrightnessDelegate? _canChangeDisplayBrightness;
    private readonly BrightnessChangedDelegate? _displayBrightnessChanged;
    private readonly CreateWithServiceDelegate? _createWithService;
    private readonly I2cDelegate? _readI2c;
    private readonly I2cDelegate? _writeI2c;
    private bool _isAvailable;

    internal MacBrightnessController()
    {
        _getDisplayBrightness = LoadDelegate<GetBrightnessDelegate>(_displayServicesHandle, "DisplayServicesGetBrightness");
        _setDisplayBrightness = LoadDelegate<SetBrightnessDelegate>(_displayServicesHandle, "DisplayServicesSetBrightness");
        _canChangeDisplayBrightness = LoadDelegate<CanChangeBrightnessDelegate>(_displayServicesHandle, "DisplayServicesCanChangeBrightness");
        _displayBrightnessChanged = LoadDelegate<BrightnessChangedDelegate>(_displayServicesHandle, "DisplayServicesBrightnessChanged");
        _createWithService = LoadDelegate<CreateWithServiceDelegate>(_ioKitHandle, "IOAVServiceCreateWithService");
        _readI2c = LoadDelegate<I2cDelegate>(_ioKitHandle, "IOAVServiceReadI2C");
        _writeI2c = LoadDelegate<I2cDelegate>(_ioKitHandle, "IOAVServiceWriteI2C");
    }

    internal bool IsAvailable => _isAvailable;

    internal bool TryAdjustMainDisplay(bool increase, out float normalizedBrightness)
    {
        normalizedBrightness = 0;
        lock (_gate)
        {
            if (_betterDisplay.TryAdjustMainDisplayBrightness(increase))
            {
                _isAvailable = true;
                return true;
            }

            var displayId = NativeMethods.CGMainDisplayID();
            if (TryAdjustDisplayServices(displayId, increase, out normalizedBrightness)
                || TryAdjustDdc(displayId, increase, out normalizedBrightness))
            {
                _isAvailable = true;
                return true;
            }
            return false;
        }
    }

    private bool TryAdjustDisplayServices(uint displayId, bool increase, out float normalizedBrightness)
    {
        normalizedBrightness = 0;
        if (_getDisplayBrightness is null || _setDisplayBrightness is null) return false;
        // Apple panels can be controllable even if this optional capability symbol is unavailable.
        if (_canChangeDisplayBrightness is not null && _canChangeDisplayBrightness(displayId) == 0) return false;
        if (_getDisplayBrightness(displayId, out var current) != KernSuccess) return false;

        var next = Math.Clamp(current + (increase ? (float)Step : -(float)Step), 0f, 1f);
        if (_setDisplayBrightness(displayId, next) != KernSuccess) return false;
        _ = _displayBrightnessChanged?.Invoke(displayId, next);
        normalizedBrightness = next;
        return true;
    }

    private unsafe bool TryAdjustDdc(uint displayId, bool increase, out float normalizedBrightness)
    {
        normalizedBrightness = 0;
        if (_createWithService is null || _readI2c is null || _writeI2c is null) return false;
        // A built-in display is handled by DisplayServices; generic external monitors use DDC/CI.
        if (NativeMethods.CGDisplayIsBuiltin(displayId) != 0) return false;

        var matching = NativeMethods.IOServiceMatching("DCPAVServiceProxy");
        if (matching == nint.Zero || NativeMethods.IOServiceGetMatchingServices(0, matching, out var iterator) != KernSuccess)
            return false;

        try
        {
            uint service;
            while ((service = NativeMethods.IOIteratorNext(iterator)) != 0)
            {
                try
                {
                    var avService = _createWithService(nint.Zero, service);
                    if (avService == nint.Zero) continue;
                    try
                    {
                        if (!TryReadBrightness(avService, out var current, out var max) || max == 0) continue;
                        var delta = Math.Max(1, (int)Math.Round(max * Step));
                        var next = (ushort)Math.Clamp(current + (increase ? delta : -delta), 0, max);
                        if (TryWriteBrightness(avService, next))
                        {
                            normalizedBrightness = next / (float)max;
                            return true;
                        }
                    }
                    finally { NativeMethods.CFRelease(avService); }
                }
                finally { NativeMethods.IOObjectRelease(service); }
            }
        }
        finally { NativeMethods.IOObjectRelease(iterator); }

        return false;
    }

    private unsafe bool TryReadBrightness(nint avService, out ushort current, out ushort max)
    {
        current = max = 0;
        var request = CreateGetBrightnessRequest();
        fixed (byte* requestPtr = request)
        {
            if (_writeI2c!(avService, DdcDisplayAddress, DdcHostAddress, requestPtr, (uint)request.Length) != KernSuccess)
                return false;
        }

        Thread.Sleep(50);
        var reply = new byte[11];
        fixed (byte* replyPtr = reply)
        {
            if (_readI2c!(avService, DdcDisplayAddress, DdcHostAddress, replyPtr, (uint)reply.Length) != KernSuccess)
                return false;
        }
        return TryParseBrightnessReply(reply, out current, out max);
    }

    private unsafe bool TryWriteBrightness(nint avService, ushort value)
    {
        var request = CreateSetBrightnessRequest(value);
        fixed (byte* requestPtr = request)
            return _writeI2c!(avService, DdcDisplayAddress, DdcHostAddress, requestPtr, (uint)request.Length) == KernSuccess;
    }

    internal static byte[] CreateGetBrightnessRequest()
    {
        var request = new byte[] { 0x82, 0x01, BrightnessVcpCode, 0 };
        request[^1] = CalculateChecksum(request);
        return request;
    }

    internal static byte[] CreateSetBrightnessRequest(ushort value)
    {
        var request = new byte[] { 0x84, 0x03, BrightnessVcpCode, (byte)(value >> 8), (byte)value, 0 };
        request[^1] = CalculateChecksum(request);
        return request;
    }

    internal static bool TryParseBrightnessReply(ReadOnlySpan<byte> reply, out ushort current, out ushort max)
    {
        current = max = 0;
        if (reply.Length < 10 || reply[2] != 0x02 || reply[4] != BrightnessVcpCode) return false;
        max = (ushort)((reply[6] << 8) | reply[7]);
        current = (ushort)((reply[8] << 8) | reply[9]);
        return max > 0 && current <= max;
    }

    private static byte CalculateChecksum(ReadOnlySpan<byte> packet)
    {
        byte checksum = 0x6E ^ (byte)DdcHostAddress;
        foreach (var value in packet[..^1]) checksum ^= value;
        return checksum;
    }

    private static nint LoadLibrary(string path)
    {
        try { return NativeLibrary.Load(path); }
        catch { return nint.Zero; }
    }

    private static T? LoadDelegate<T>(nint handle, string symbol) where T : Delegate
    {
        if (handle == nint.Zero) return null;
        try { return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(handle, symbol)); }
        catch { return null; }
    }
}
