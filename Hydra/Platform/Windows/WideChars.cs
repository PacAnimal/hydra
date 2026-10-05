using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Hydra.Platform.Windows;

// NUL-terminated UTF-16 buffers inside native structs; ushort, since a char field is not blittable
[InlineArray(32)]
internal struct WideChars32
{
    private ushort _first;

    public override readonly string ToString() => WideChars.Decode(this);
}

[InlineArray(260)]
internal struct WideChars260
{
    private ushort _first;

    public override readonly string ToString() => WideChars.Decode(this);
}

internal static class WideChars
{
    internal static string Decode(ReadOnlySpan<ushort> buffer)
    {
        var chars = MemoryMarshal.Cast<ushort, char>(buffer);
        var end = chars.IndexOf('\0');
        return (end < 0 ? chars : chars[..end]).ToString();
    }
}
