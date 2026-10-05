using System.Runtime.InteropServices;

namespace Hydra.Platform.MacOs;

// managed conveniences over NativeMethods
internal static class NativeHelpers
{
    // ensure frameworks are loaded before calling into objc_getClass for their classes.
    // NativeLibrary.Load is idempotent — safe to call from multiple constructors.
    internal static void EnsureAppKitLoaded() => NativeLibrary.Load(NativeMethods.AppKit);
    internal static void EnsureApplicationServicesLoaded() => NativeLibrary.Load(NativeMethods.ApplicationServices);

    // creates a CFString/NSString from a managed string (toll-free bridged)
    internal static nint MakeNsString(string s) => NativeMethods.CFStringCreateWithCString(nint.Zero, s, NativeMethods.KCFStringEncodingUtf8);

    // toll-free bridged NSString/CFString → managed string
    internal static unsafe string? CfStringToManaged(nint cfStr)
    {
        if (cfStr == nint.Zero) return null;
        var charCount = NativeMethods.objc_msgSend_long(cfStr, NativeMethods.sel_registerName("length"));
        var bufSize = (nint)(charCount * 4 + 1);
        var buf = Marshal.AllocHGlobal(bufSize);
        try
        {
            return NativeMethods.CFStringGetCString(cfStr, (byte*)buf, bufSize, NativeMethods.KCFStringEncodingUtf8)
                ? Marshal.PtrToStringUTF8(buf) : null;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // polls for up to ~4s using CGEventTapCreate — the reliable live check, unlike AXIsProcessTrusted()
    // which returns a cached value for a running process and won't reflect a live grant.
    internal static bool PollAccessibilityTrusted()
    {
        for (var i = 0; i < 8; i++)
        {
            if (CanCreateEventTap()) return true;
            Thread.Sleep(500);
        }
        return false;
    }

    // opens the system accessibility prompt (System Settings), returns current trust state.
    internal static bool ShowAccessibilityPrompt()
    {
        EnsureAppKitLoaded();
        var cls = NativeMethods.objc_getClass("NSMutableDictionary");
        var dict = NativeMethods.objc_msgSend_noarg(
            NativeMethods.objc_msgSend_noarg(cls, NativeMethods.sel_registerName("alloc")), NativeMethods.sel_registerName("init"));
        var key = MakeNsString("AXTrustedCheckOptionPrompt");
        NativeMethods.objc_msgSend_2arg(dict, NativeMethods.sel_registerName("setObject:forKey:"), NativeMethods.KCFBooleanTrue, key);
        NativeMethods.CFRelease(key);
        var trusted = NativeMethods.AXIsProcessTrustedWithOptions(dict);
        NativeMethods.objc_msgSend_noarg(dict, NativeMethods.sel_registerName("release"));
        return trusted;
    }

    internal static async Task WaitForAccessibilityTrusted(CancellationToken cancel)
    {
        // AXIsProcessTrusted() returns a cached value for a running process and won't update after
        // a live grant. CGEventTapCreate() tests the actual kernel capability and is not cached —
        // it's the reliable detection method used by production macOS accessibility tools.
        // com.apple.accessibility.api distributed notification also doesn't fire for processes that
        // weren't trusted at startup, so polling is the only option.
        while (!cancel.IsCancellationRequested)
        {
            try { await Task.Delay(500, cancel); }
            catch (OperationCanceledException) { return; }
            if (CanCreateEventTap()) return;
        }
    }

    private static bool CanCreateEventTap()
    {
        CGEventTapCallBack probe = (_, _, eventRef, _) => eventRef;
        var tap = NativeMethods.CGEventTapCreate(NativeMethods.KCGHidEventTap, NativeMethods.KCGHeadInsertEventTap,
            NativeMethods.KCGEventTapOptionDefault, NativeMethods.KCGEventMaskForAllEvents, probe, nint.Zero);
        if (tap == nint.Zero) return false;
        NativeMethods.CFRelease(tap);
        GC.KeepAlive(probe);
        return true;
    }

    // allow cursor manipulation from a background thread (private CGS API — matches synergy)
    internal static void EnableBackgroundCursorManipulation()
    {
        var cid = NativeMethods.CGSMainConnectionID();
        var key = MakeNsString("SetsCursorInBackground");
        _ = NativeMethods.CGSSetConnectionProperty(cid, cid, key, NativeMethods.KCFBooleanTrue);
        NativeMethods.CFRelease(key);
    }

    // reads an exported constant (a CFStringRef or similar), not a function
    internal static nint ReadSymbol(string library, string name) => Marshal.ReadIntPtr(SymbolAddress(library, name));

    internal static nint SymbolAddress(string library, string name) => NativeLibrary.GetExport(NativeLibrary.Load(library), name);
}
