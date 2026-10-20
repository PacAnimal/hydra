namespace Hydra.Platform.MacOs;

// the current keyboard layout's UCKeyboardLayout data, held open until disposed
internal readonly struct KeyboardLayout : IDisposable
{
    private readonly nint _source;

    // the layout data for UCKeyTranslate, or zero when the layout has none
    internal nint Data { get; }

    private KeyboardLayout(nint source)
    {
        _source = source;
        if (source == nint.Zero) return;
        var layoutData = NativeMethods.TISGetInputSourceProperty(source, NativeMethods.KTISPropertyUnicodeKeyLayoutData);
        if (layoutData != nint.Zero) Data = NativeMethods.CFDataGetBytePtr(layoutData);
    }

    // the active layout, or the Latin-capable one macOS keeps alongside it
    internal static KeyboardLayout Open(bool asciiCapable = false) => new(asciiCapable
        ? NativeMethods.TISCopyCurrentASCIICapableKeyboardLayoutInputSource()
        : NativeMethods.TISCopyCurrentKeyboardLayoutInputSource());

    public void Dispose()
    {
        if (_source != nint.Zero) NativeMethods.CFRelease(_source);
    }
}
