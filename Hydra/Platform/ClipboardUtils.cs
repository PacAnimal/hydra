namespace Hydra.Platform;

public static class ClipboardUtils
{
    public static uint QuickHash(byte[] data)
    {
        var hc = new HashCode();
        hc.AddBytes(data);
        return (uint)hc.ToHashCode();
    }
}
