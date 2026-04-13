using ByteSizeLib;

namespace Hydra.Platform;

public static class ClipboardUtils
{
    public static readonly long MaxClipboardBytes = (long)ByteSize.FromMebiBytes(16).Bytes;

    public static ulong QuickHash(byte[] data)
    {
        // two hashes with different inputs combined into 64-bit to reduce collision probability
        var hc1 = new HashCode();
        hc1.AddBytes(data);
        var hc2 = new HashCode();
        hc2.Add(data.Length); // prefix with length to differentiate from hc1
        hc2.AddBytes(data);
        return ((ulong)(uint)hc1.ToHashCode() << 32) | (uint)hc2.ToHashCode();
    }
}
