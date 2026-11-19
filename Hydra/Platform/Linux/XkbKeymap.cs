using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Linux;

// one compiled xkb keymap and the context it was compiled in, shared by whatever resolves, looks up or tracks keys on
// it; the creator disposes it after every user, which only borrows it
internal sealed class XkbKeymap : IDisposable
{
    private readonly nint _ctx;
    private int _disposed;

    private XkbKeymap(Func<nint, nint> newKeymap, string source)
    {
        _ctx = EvdevNativeMethods.xkb_context_new(0);
        if (_ctx == 0) throw new InvalidOperationException("Failed to create xkb context.");
        Handle = newKeymap(_ctx);
        if (Handle != 0) return;
        EvdevNativeMethods.xkb_context_unref(_ctx);
        throw new InvalidOperationException($"Failed to create xkb keymap from {source}.");
    }

    // the xkb_keymap; valid until Dispose
    internal nint Handle { get; }

    // from a keymap in XKB_KEYMAP_FORMAT_TEXT_V1, as an EIS server or wl_keyboard hands it over
    internal static XkbKeymap FromText(string keymapText) =>
        new(ctx => EvdevNativeMethods.xkb_keymap_new_from_string(ctx, keymapText, EvdevNativeMethods.XKB_KEYMAP_FORMAT_TEXT_V1, 0), "keymap text");

    internal static XkbKeymap FromNames(LinuxInputConfig.XkbNames xkb) =>
        new(ctx => NewKeymapFromNames(ctx, xkb), $"layout '{xkb.Layout}'");

    // the layout the system names (LinuxInputConfig.ResolveXkb), without its options when they do not compile here
    internal static XkbKeymap FromSystemLayout(ILogger log)
    {
        var xkb = LinuxInputConfig.ResolveXkb();
        XkbKeymap keymap;
        try
        {
            keymap = FromNames(xkb);
        }
        catch (InvalidOperationException) when (xkb.Options != null)
        {
            // an option only the desktop's own xkb files define (Omarchy's compose option) fails to compile here
            if (log.IsEnabled(LogLevel.Warning)) log.LogWarning("Keyboard options '{Options}' did not compile; using the layout without them", xkb.Options);
            xkb = xkb with { Options = null };
            keymap = FromNames(xkb);
        }
        if (log.IsEnabled(LogLevel.Information))
            log.LogInformation("Keyboard layout: {Layout} model: {Model}{Variant}", xkb.Layout, xkb.Model, xkb.Variant is null ? "" : $" variant: {xkb.Variant}");
        return keymap;
    }

    // the keymap as XKB_KEYMAP_FORMAT_TEXT_V1 text
    internal string Text()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var text = EvdevNativeMethods.xkb_keymap_get_as_string(Handle, EvdevNativeMethods.XKB_KEYMAP_FORMAT_TEXT_V1);
        if (text == 0) throw new InvalidOperationException("Failed to serialize xkb keymap.");
        try
        {
            return Marshal.PtrToStringUTF8(text) ?? throw new InvalidOperationException("Failed to serialize xkb keymap.");
        }
        finally
        {
            EvdevNativeMethods.Free(text);
        }
    }

    // the bit a named modifier (Shift, Control, Mod1, Mod4, Mod5, Lock, ...) sets in a mask; 0 when the keymap lacks it
    internal uint ModMask(string name)
    {
        var index = EvdevNativeMethods.xkb_keymap_mod_get_index(Handle, name);
        return index is EvdevNativeMethods.XKB_MOD_INVALID or >= 32 ? 0 : 1u << (int)index;
    }

    // compiles a keymap from RMLVO names; 0 on failure. the caller owns the keymap.
    private static nint NewKeymapFromNames(nint ctx, LinuxInputConfig.XkbNames xkb)
    {
        // allocate native strings for rule names; freed in the finally below.
        // Model comes from the system config too: a 105-key board is the common case but not a
        // universal one, and getting it wrong misplaces the keys either side of Enter.
        var names = new XkbRuleNames
        {
            Rules = Marshal.StringToCoTaskMemUTF8("evdev"),
            Model = Marshal.StringToCoTaskMemUTF8(xkb.Model),
            Layout = Marshal.StringToCoTaskMemUTF8(xkb.Layout),
            Variant = xkb.Variant is null ? 0 : Marshal.StringToCoTaskMemUTF8(xkb.Variant),
            Options = xkb.Options is null ? 0 : Marshal.StringToCoTaskMemUTF8(xkb.Options),
        };

        try
        {
            return EvdevNativeMethods.xkb_keymap_new_from_names(ctx, ref names, 0);
        }
        finally
        {
            Marshal.FreeCoTaskMem(names.Rules);
            Marshal.FreeCoTaskMem(names.Model);
            Marshal.FreeCoTaskMem(names.Layout);
            if (names.Variant != 0) Marshal.FreeCoTaskMem(names.Variant);
            if (names.Options != 0) Marshal.FreeCoTaskMem(names.Options);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        EvdevNativeMethods.xkb_keymap_unref(Handle);
        EvdevNativeMethods.xkb_context_unref(_ctx);
    }
}
