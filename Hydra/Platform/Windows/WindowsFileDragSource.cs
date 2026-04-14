using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using Hydra.FileTransfer;
using Hydra.Screen;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Windows;

// detects dragged files on Windows by registering an OLE IDropTarget on thin transparent
// strips along active transition edges. strips are rebuilt each time the screen layout changes.
// left/right strips take priority over top/bottom at corners to avoid shared-strip issues.
[SupportedOSPlatform("windows")]
public sealed class WindowsFileDragSource : IFileDragSource, IDisposable
{
    private readonly ILogger<WindowsFileDragSource> _log;
    private readonly EdgeDropTarget _dropTarget = new();
    private readonly List<nint> _edgeHwnds = [];
    private readonly StaMessageLoop _loop;
    private WndProc? _wndProc;
    private List<ActiveEdgeRange> _pendingRanges = [];
    private readonly Lock _pendingLock = new();

    private const string EdgeClassName = "HydraDragEdge";
    private const uint WmRecreateStrips = NativeMethods.WM_USER + 1;
    private const int EdgePx = 16;

    public WindowsFileDragSource(ILogger<WindowsFileDragSource> log)
    {
        _log = log;
        _loop = new StaMessageLoop(
            "HydraDragDetect",
            init: () =>
            {
                // OLE must be initialized on each thread that uses drag-and-drop
                _ = NativeMethods.OleInitialize(nint.Zero);
                RegisterWindowClass();
            },
            onThreadMessage: msg =>
            {
                if (msg.message == WmRecreateStrips) { RecreateEdgeStrips(); return true; }
                return false;
            },
            onExit: () =>
            {
                lock (_edgeHwnds)
                {
                    foreach (var hwnd in _edgeHwnds)
                    {
                        var hr = NativeMethods.RevokeDragDrop(hwnd);
                        _log.LogDebug("RevokeDragDrop hwnd={Hwnd} hr={Hr:X}", hwnd, hr);
                        NativeMethods.DestroyWindow(hwnd);
                    }
                    _edgeHwnds.Clear();
                }
                NativeMethods.OleUninitialize();
            });
    }

    public List<string>? GetDraggedPaths()
    {
        var paths = _dropTarget.GetCurrentPaths();
        _log.LogDebug("GetDraggedPaths: {Count} path(s)", paths?.Count ?? 0);
        return paths;
    }

    public void UpdateActiveEdges(List<ActiveEdgeRange> ranges)
    {
        lock (_pendingLock) _pendingRanges = ranges;
        // use PostThreadMessage so the strip rebuild is handled even before any strip windows exist
        if (_loop.ThreadId != 0)
            NativeMethods.PostThreadMessage(_loop.ThreadId, WmRecreateStrips, nint.Zero, nint.Zero);
    }

    public void Dispose() => _loop.Dispose();

    private void RegisterWindowClass()
    {
        _wndProc = WndProcImpl;
        var hInstance = NativeMethods.GetModuleHandleW(nint.Zero);
        var className = Marshal.StringToHGlobalUni(EdgeClassName);
        try
        {
            var wc = new NativeMethods.WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInstance,
                lpszClassName = className,
            };
            var atom = NativeMethods.RegisterClassExW(in wc);
            _log.LogDebug("RegisterClassExW HydraDragEdge: atom={Atom}", atom);
        }
        finally { Marshal.FreeHGlobal(className); }
    }

    private void RecreateEdgeStrips()
    {
        List<ActiveEdgeRange> ranges;
        lock (_pendingLock) ranges = _pendingRanges;

        lock (_edgeHwnds)
        {
            foreach (var hwnd in _edgeHwnds)
            {
                var hr = NativeMethods.RevokeDragDrop(hwnd);
                _log.LogDebug("RecreateEdgeStrips RevokeDragDrop hwnd={Hwnd} hr={Hr:X}", hwnd, hr);
                NativeMethods.DestroyWindow(hwnd);
            }
            _edgeHwnds.Clear();
        }

        var hInstance = NativeMethods.GetModuleHandleW(nint.Zero);
        var className = Marshal.StringToHGlobalUni(EdgeClassName);
        try
        {
            foreach (var rect in ComputeStripRects(ranges))
                CreateStrip(hInstance, className, rect.X, rect.Y, rect.W, rect.H);
        }
        finally { Marshal.FreeHGlobal(className); }

        int count;
        lock (_edgeHwnds) count = _edgeHwnds.Count;
        _log.LogDebug("Edge strips rebuilt: {Count} strip(s)", count);
    }

    // converts active edge ranges into strip window rects.
    // left/right strips take full pixel range; top/bottom strips are inset at corners
    // where a left/right strip exists on the same screen.
    private static IEnumerable<StripRect> ComputeStripRects(List<ActiveEdgeRange> ranges)
    {
        // collect left/right ranges per screen for corner trimming
        var leftRightByScreen = ranges
            .Where(r => r.Direction is Direction.Left or Direction.Right)
            .ToLookup(r => r.Screen.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var range in ranges)
        {
            var s = range.Screen;
            int x, y, w, h;

            if (range.Direction is Direction.Left or Direction.Right)
            {
                // left/right: strip runs vertically along pixel range of screen height
                x = range.Direction == Direction.Left ? s.X : s.X + s.Width - EdgePx;
                y = s.Y + range.PixelStart;
                w = EdgePx;
                h = range.PixelEnd - range.PixelStart;
            }
            else
            {
                // top/bottom: strip runs horizontally along pixel range of screen width.
                // inset by EdgePx at corners where a left/right strip covers that same corner.
                var lrRanges = leftRightByScreen[s.Name].ToList();
                var start = range.PixelStart;
                var end = range.PixelEnd;

                // for a top strip: inset if the vertical strip reaches the top (PixelStart < EdgePx).
                // for a bottom strip: inset if it reaches the bottom (PixelEnd > s.Height - EdgePx).
                bool ReachesCorner(ActiveEdgeRange r) => range.Direction == Direction.Up
                    ? r.PixelStart < EdgePx
                    : r.PixelEnd > s.Height - EdgePx;

                if (lrRanges.Any(r => r.Direction == Direction.Left && ReachesCorner(r)))
                    start = Math.Max(start, EdgePx);

                if (lrRanges.Any(r => r.Direction == Direction.Right && ReachesCorner(r)))
                    end = Math.Min(end, s.Width - EdgePx);

                if (start >= end) continue;

                x = s.X + start;
                y = range.Direction == Direction.Up ? s.Y : s.Y + s.Height - EdgePx;
                w = end - start;
                h = EdgePx;
            }

            if (w > 0 && h > 0)
                yield return new StripRect(x, y, w, h);
        }
    }

    private void CreateStrip(nint hInstance, nint className, int x, int y, int w, int h)
    {
        var hwnd = NativeMethods.CreateWindowExW(
            NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_NOACTIVATE,
            className, nint.Zero,
            NativeMethods.WS_POPUP,
            x, y, w, h,
            nint.Zero, nint.Zero, hInstance, nint.Zero);

        if (hwnd == nint.Zero)
        {
            _log.LogDebug("CreateWindowExW failed for edge strip at ({X},{Y}) {W}x{H}", x, y, w, h);
            return;
        }

        // bAlpha=1: essentially invisible (~0.4% opacity) but allows OLE drag hit-testing
        NativeMethods.SetLayeredWindowAttributes(hwnd, 0, 1, NativeMethods.LWA_ALPHA);
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, w, h, NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);

        var dropTargetPtr = Marshal.GetComInterfaceForObject<EdgeDropTarget, IDropTargetInterface>(_dropTarget);
        var hr = NativeMethods.RegisterDragDrop(hwnd, dropTargetPtr);
        Marshal.Release(dropTargetPtr);

        _log.LogDebug("Edge strip ({X},{Y}) {W}x{H} hwnd={Hwnd} RegisterDragDrop hr={Hr:X}", x, y, w, h, hwnd, hr);
        lock (_edgeHwnds) _edgeHwnds.Add(hwnd);
    }

    private nint WndProcImpl(nint hWnd, uint msg, nint wParam, nint lParam)
        => NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);

    private record StripRect(int X, int Y, int W, int H);

    // -- COM IDropTarget interface definition (used to produce the CCW) --

    [ComVisible(true)]
    [Guid("00000122-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDropTargetInterface
    {
        [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] IDataObject pDataObj, uint grfKeyState, POINTL pt, ref uint pdwEffect);
        [PreserveSig] int DragOver(uint grfKeyState, POINTL pt, ref uint pdwEffect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] IDataObject pDataObj, uint grfKeyState, POINTL pt, ref uint pdwEffect);
    }

    // -- IDropTarget implementation --

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    internal sealed class EdgeDropTarget : IDropTargetInterface
    {
        private readonly Lock _lock = new();
        private List<string>? _paths;

        public List<string>? GetCurrentPaths()
        {
            lock (_lock) return _paths == null ? null : [.. _paths];
        }

        public int DragEnter(IDataObject pDataObj, uint grfKeyState, POINTL pt, ref uint pdwEffect)
        {
            lock (_lock) _paths = ExtractCfHDrop(pDataObj);
            pdwEffect = 0; // DROPEFFECT_NONE — observe only, let source handle the drop
            return 0; // S_OK
        }

        public int DragOver(uint grfKeyState, POINTL pt, ref uint pdwEffect)
        {
            pdwEffect = 0;
            return 0;
        }

        public int DragLeave()
        {
            lock (_lock) _paths = null;
            return 0;
        }

        public int Drop(IDataObject pDataObj, uint grfKeyState, POINTL pt, ref uint pdwEffect)
        {
            // should not fire — we return DROPEFFECT_NONE in DragEnter/DragOver
            pdwEffect = 0;
            return 0;
        }

        private static List<string>? ExtractCfHDrop(IDataObject dataObj)
        {
            var fmt = new FORMATETC
            {
                cfFormat = (short)NativeMethods.CF_HDROP,
                dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                tymed = TYMED.TYMED_HGLOBAL,
            };

            STGMEDIUM medium;
            try { dataObj.GetData(ref fmt, out medium); }
            catch { return null; } // data object doesn't support CF_HDROP

            if ((medium.tymed & TYMED.TYMED_HGLOBAL) == 0)
            {
                NativeMethods.ReleaseStgMedium(ref medium);
                return null;
            }

            var hDrop = medium.unionmember;
            try
            {
                return ReadHDrop(hDrop);
            }
            finally
            {
                NativeMethods.ReleaseStgMedium(ref medium);
            }
        }

        private static unsafe List<string>? ReadHDrop(nint hDrop)
        {
            var count = NativeMethods.DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
            if (count == 0) return null;

            var paths = new List<string>((int)count);
            for (uint i = 0; i < count; i++)
            {
                // first call with null buffer to get required length
                var len = NativeMethods.DragQueryFileW(hDrop, i, null, 0);
                if (len == 0) continue;
                var buf = new char[len + 1];
                fixed (char* p = buf)
                    _ = NativeMethods.DragQueryFileW(hDrop, i, p, len + 1);
                paths.Add(new string(buf, 0, (int)len));
            }
            return paths.Count > 0 ? paths : null;
        }
    }
}
