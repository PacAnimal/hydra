using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ByteSizeLib;
using Hydra.FileTransfer;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Windows;

// Win32 file transfer progress dialog.
// all window operations run on a dedicated STA thread; callers post WM_USER+ messages to update it.
[SupportedOSPlatform("windows")]
public sealed class WindowsTransferDialog : IFileTransferDialog, IDisposable
{
    private readonly ILogger<WindowsTransferDialog> _log;
    private nint _hwnd;
    private nint _hwndLabel;
    private nint _hwndProgress;
    private nint _hwndCancel;
    private WndProc? _wndProc;
    private readonly StaMessageLoop _loop;
    private long _totalBytes;
    // only accessed on the STA message-loop thread (set/cancel/dispose all happen in WndProc or during Join)
    private CancellationTokenSource? _autoCloseCts;

    public event Action? CancelRequested;

    // WM_USER+ messages for cross-thread control
    private const uint WmShowPending = NativeMethods.WM_USER + 20;
    private const uint WmShowXfer = NativeMethods.WM_USER + 21;
    private const uint WmSetProgress = NativeMethods.WM_USER + 22; // lParam=GCHandle to ProgressArgs
    private const uint WmShowDone = NativeMethods.WM_USER + 23;
    private const uint WmCloseDialog = NativeMethods.WM_USER + 24;
    private const uint WmShowError = NativeMethods.WM_USER + 25; // lParam=GCHandle to error string
    // lParam for WmShowPending/WmShowError/WmSetProgress carries a GCHandle
    private const int BtnCancelId = 100;
    // base dimensions at 96 DPI (100% scale); actual sizes are scaled to the system DPI at creation time
    private const int BaseDialogW = 320, BaseDialogH = 130;
    private const int BaseControlW = 280, BaseLabelH = 30, BaseProgressH = 20, BaseBtnH = 24, BaseBtnW = 80;
    private const int AutoCloseDelayMs = 2_000; // delay before auto-dismissing a completed transfer dialog

    // scaled at creation time; used in WndProc for repositioning
    private int _dialogW, _dialogH;

    public WindowsTransferDialog(ILogger<WindowsTransferDialog> log)
    {
        _log = log;
        _loop = new StaMessageLoop(
            "HydraTransferDlg",
            init: CreateDialogWindow,
            onExit: () =>
            {
                if (_hwnd != nint.Zero)
                {
                    NativeMethods.DestroyWindow(_hwnd);
                    _hwnd = nint.Zero;
                }
            });
    }

    public void ShowPending(FileTransferInfo info)
    {
        _log.LogDebug("Transfer dialog ShowPending: count={Count} bytes={Bytes} sender={IsSender}",
            info.FileCount, info.TotalBytes, info.IsSender);
        _totalBytes = info.TotalBytes;
        PostWithHandle(WmShowPending, nint.Zero, new ShowPendingArgs(info));
    }

    public void ShowTransferring(FileTransferInfo info)
    {
        _log.LogDebug("Transfer dialog ShowTransferring");
        if (_hwnd == nint.Zero) return;
        _totalBytes = info.TotalBytes;
        NativeMethods.PostMessage(_hwnd, WmShowXfer, nint.Zero, nint.Zero);
    }

    public void UpdateProgress(long bytesTransferred, double bytesPerSecond)
    {
        if (_hwnd == nint.Zero || _totalBytes <= 0) return;
        PostWithHandle(WmSetProgress, nint.Zero, new ProgressArgs(bytesTransferred, bytesPerSecond, _totalBytes));
    }

    public void ShowCompleted()
    {
        _log.LogDebug("Transfer dialog ShowCompleted");
        if (_hwnd == nint.Zero) return;
        NativeMethods.PostMessage(_hwnd, WmShowDone, nint.Zero, nint.Zero);
    }

    public void ShowError(string message)
    {
        _log.LogDebug("Transfer dialog ShowError: {Message}", message);
        PostWithHandle(WmShowError, nint.Zero, message);
    }

    public void Close()
    {
        _log.LogDebug("Transfer dialog Close");
        if (_hwnd == nint.Zero) return;
        NativeMethods.PostMessage(_hwnd, WmCloseDialog, nint.Zero, nint.Zero);
    }

    public void Dispose()
    {
        _loop.Dispose();
        // safe to touch _autoCloseCts now — STA thread is dead
        _autoCloseCts?.Cancel();
        _autoCloseCts?.Dispose();
        _autoCloseCts = null;
    }

    // alloc a GCHandle and post it as lParam; free it if PostMessage fails
    private void PostWithHandle(uint msg, nint wParam, object payload)
    {
        if (_hwnd == nint.Zero) return;
        var handle = GCHandle.Alloc(payload);
        if (!NativeMethods.PostMessage(_hwnd, msg, wParam, GCHandle.ToIntPtr(handle)))
            handle.Free();
    }

    private void CreateDialogWindow()
    {
        _wndProc = WndProcImpl;
        var hInstance = NativeMethods.GetModuleHandleW(nint.Zero);
        var className = Marshal.StringToHGlobalUni("HydraTransferDlg");
        try
        {
            // ensure common controls (progress bar class) are registered
            var icc = new NativeMethods.INITCOMMONCONTROLSEX
            {
                dwSize = (uint)Marshal.SizeOf<NativeMethods.INITCOMMONCONTROLSEX>(),
                dwICC = NativeMethods.ICC_PROGRESS_CLASS,
            };
            NativeMethods.InitCommonControlsEx(in icc);

            var wc = new NativeMethods.WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInstance,
                lpszClassName = className,
                hbrBackground = NativeMethods.COLOR_BTNFACE + 1,
            };
            NativeMethods.RegisterClassExW(in wc);

            // scale base dimensions to the system DPI so the dialog is the right physical size
            var dpi = NativeMethods.GetDpiForSystem();
            var s = dpi / 96.0;
            int Scale(int v) => (int)Math.Round(v * s);

            _dialogW = Scale(BaseDialogW);
            _dialogH = Scale(BaseDialogH);
            var controlW = Scale(BaseControlW);
            var labelH = Scale(BaseLabelH);
            var progressH = Scale(BaseProgressH);
            var btnH = Scale(BaseBtnH);
            var btnW = Scale(BaseBtnW);

            _hwnd = NativeMethods.CreateWindowExW(
                NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TOOLWINDOW,
                className, nint.Zero,
                NativeMethods.WS_OVERLAPPED | NativeMethods.WS_CAPTION | NativeMethods.WS_SYSMENU,
                CenteredX(), CenteredY(), _dialogW, _dialogH,
                nint.Zero, nint.Zero, hInstance, nint.Zero);

            if (_hwnd == nint.Zero)
            {
                _log.LogWarning("Failed to create transfer dialog window");
                return;
            }

            NativeMethods.SetWindowTextW(_hwnd, "Hydra File Transfer");
            _log.LogDebug("Transfer dialog window created hwnd={Hwnd} dpi={Dpi}", _hwnd, dpi);

            // label child (file info)
            var labelClass = Marshal.StringToHGlobalUni("STATIC");
            _hwndLabel = NativeMethods.CreateWindowExW(0, labelClass, nint.Zero,
                NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.SS_LEFT,
                Scale(10), Scale(8), controlW, labelH,
                _hwnd, nint.Zero, hInstance, nint.Zero);
            Marshal.FreeHGlobal(labelClass);

            // progress bar child
            var pbClass = Marshal.StringToHGlobalUni("msctls_progress32");
            _hwndProgress = NativeMethods.CreateWindowExW(0, pbClass, nint.Zero,
                NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.PBS_SMOOTH,
                Scale(10), Scale(44), controlW, progressH,
                _hwnd, nint.Zero, hInstance, nint.Zero);
            Marshal.FreeHGlobal(pbClass);

            // set progress range 0-100
            NativeMethods.PostMessage(_hwndProgress, NativeMethods.PBM_SETRANGE32, 0, 100);

            // cancel button
            var btnClass = Marshal.StringToHGlobalUni("BUTTON");
            _hwndCancel = NativeMethods.CreateWindowExW(0, btnClass, nint.Zero,
                NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.BS_PUSHBUTTON,
                (_dialogW - btnW) / 2, Scale(72), btnW, btnH,
                _hwnd, BtnCancelId, hInstance, nint.Zero);
            Marshal.FreeHGlobal(btnClass);

            SetLabelText("Preparing…");
            SetCancelText("Cancel");
        }
        finally
        {
            Marshal.FreeHGlobal(className);
        }
    }

    private nint WndProcImpl(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_COMMAND:
                // low word of wParam = control ID
                if ((wParam & 0xFFFF) == BtnCancelId && ((wParam >> 16) & 0xFFFF) == NativeMethods.BN_CLICKED)
                {
                    _log.LogDebug("Transfer dialog cancel clicked");
                    try { CancelRequested?.Invoke(); } catch (Exception ex) { _log.LogWarning(ex, "CancelRequested handler threw"); }
                    HideDialog();
                }
                return 0;

            case NativeMethods.WM_CLOSE:
                _log.LogDebug("Transfer dialog closed by user (WM_CLOSE)");
                try { CancelRequested?.Invoke(); } catch (Exception ex) { _log.LogWarning(ex, "CancelRequested handler threw"); }
                HideDialog();
                return 0;

            case WmShowPending:
                {
                    // cancel any pending auto-close from a previous completed transfer
                    _autoCloseCts?.Cancel();
                    NativeMethods.SetWindowTextW(_hwnd, "Hydra File Transfer");
                    var handle = GCHandle.FromIntPtr(lParam);
                    try
                    {
                        if (handle.Target is ShowPendingArgs args)
                        {
                            var verb = args.Info.IsSender ? "Sending" : "Receiving";
                            var names = string.Join(", ", args.Info.FileNames.Take(3));
                            if (args.Info.FileNames.Length > 3) names += ", …";
                            var sizeStr = FormatBytes(args.Info.TotalBytes);
                            SetLabelText($"{verb} {args.Info.FileCount} file(s): {names} ({sizeStr})");
                            SetProgressValue(0);
                        }
                    }
                    finally { handle.Free(); }

                    // reposition to avoid obscuring active window; show above center
                    NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST,
                        CenteredX(), CenteredY(), 0, 0,
                        NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);
                    SetCancelText("Cancel");
                    return 0;
                }

            case WmShowXfer:
                // cancel any pending auto-close from a previous completed transfer
                _autoCloseCts?.Cancel();
                SetLabelText("Transferring…");
                EnsureTopmostVisible();
                return 0;

            case WmSetProgress:
                {
                    var handle = GCHandle.FromIntPtr(lParam);
                    try
                    {
                        if (handle.Target is ProgressArgs p)
                        {
                            var pct = (int)Math.Min(100, p.TotalBytes > 0 ? p.BytesTransferred * 100 / p.TotalBytes : 0);
                            SetProgressValue(pct);
                            var transferred = FormatBytes(p.BytesTransferred);
                            var total = FormatBytes(p.TotalBytes);
                            var speed = p.BytesPerSecond > 0 ? $"  ·  {FormatSpeed(p.BytesPerSecond)}" : "";
                            SetLabelText($"{transferred} / {total}{speed}");
                        }
                    }
                    finally { handle.Free(); }
                    return 0;
                }

            case WmShowDone:
                SetProgressValue(100);
                SetLabelText("Transfer complete!");
                SetCancelText("Close");
                // auto-close after 2 seconds; cancellable if a new transfer starts before then
                _autoCloseCts?.Cancel();
                _autoCloseCts?.Dispose();
                _autoCloseCts = new CancellationTokenSource();
                var autoCloseToken = _autoCloseCts.Token;
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(AutoCloseDelayMs, autoCloseToken); }
                    catch (OperationCanceledException) { return; }
                    NativeMethods.PostMessage(_hwnd, WmCloseDialog, nint.Zero, nint.Zero);
                });
                return 0;

            case WmShowError:
                {
                    var handle = GCHandle.FromIntPtr(lParam);
                    string errorText = "Unknown error";
                    try { errorText = handle.Target as string ?? errorText; }
                    finally { handle.Free(); }
                    NativeMethods.SetWindowTextW(_hwnd, "Hydra — Transfer Failed");
                    SetLabelText($"Error: {errorText}");
                    SetCancelText("Close");
                    EnsureTopmostVisible();
                    return 0;
                }

            case WmCloseDialog:
                HideDialog();
                return 0;
        }
        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void EnsureTopmostVisible() => NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);

    private void HideDialog()
    {
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        _log.LogDebug("Transfer dialog hidden");
    }

    private void SetLabelText(string text)
    {
        if (_hwndLabel != nint.Zero)
            NativeMethods.SetWindowTextW(_hwndLabel, text);
    }

    private void SetCancelText(string text)
    {
        if (_hwndCancel != nint.Zero)
            NativeMethods.SetWindowTextW(_hwndCancel, text);
    }

    private void SetProgressValue(int pct)
    {
        if (_hwndProgress != nint.Zero)
            NativeMethods.PostMessage(_hwndProgress, NativeMethods.PBM_SETPOS, pct, nint.Zero);
    }

    private int CenteredX() => (NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN) - _dialogW) / 2;
    private static int CenteredY() => NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN) / 4;

    private static string FormatBytes(long bytes) => ByteSize.FromBytes(bytes).ToString("#.# B");

    private static string FormatSpeed(double bps) => ByteSize.FromBytes(bps).ToString("#.# B") + "/s";

    private sealed class ShowPendingArgs(FileTransferInfo info)
    {
        public FileTransferInfo Info { get; } = info;
    }

    private sealed class ProgressArgs(long bytesTransferred, double bytesPerSecond, long totalBytes)
    {
        public long BytesTransferred { get; } = bytesTransferred;
        public double BytesPerSecond { get; } = bytesPerSecond;
        public long TotalBytes { get; } = totalBytes;
    }
}
