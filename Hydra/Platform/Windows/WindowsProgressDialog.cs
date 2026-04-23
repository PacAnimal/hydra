using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Hydra.FileTransfer;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Windows;

// wraps IProgressDialog (shell32) — the same COM progress dialog Windows Explorer uses for file copies.
// IProgressDialog is apartment-threaded (STA). all COM calls are dispatched to the dedicated STA thread
// via PostThreadMessage so they're serviced by its active message pump.
[SupportedOSPlatform("windows")]
public sealed class WindowsProgressDialog : IFileTransferDialog, IDisposable
{
    private readonly ILogger<WindowsProgressDialog> _log;
    private readonly Lock _lock = new();
    private readonly StaMessageLoop _loop;
    private IProgressDialog? _dlg;  // owned exclusively by the STA thread
    private long _totalBytes;
    private long _lastProgressTick;
    private CancellationTokenSource? _pollCts;

    // custom thread message: lParam = GCHandle<Action>
    private const uint WmRunAction = NativeMethods.WM_USER + 50;
    private const uint ProgdlgAutotime = 0x00000002; // auto time-remaining estimate
    private const uint ProgdlgNominimize = 0x00000008;
    private const uint PdtimerReset = 1;
    private const long ProgressThrottleMs = 100; // cap UI updates at ~10 Hz

    public event Action? CancelRequested;

    public WindowsProgressDialog(ILogger<WindowsProgressDialog> log)
    {
        _log = log;
        _loop = new StaMessageLoop(
            "HydraProgressDlg",
            init: () => { _ = NativeMethods.OleInitialize(nint.Zero); },
            onThreadMessage: HandleThreadMessage,
            onExit: () =>
            {
                StopOnSta();
                NativeMethods.OleUninitialize();
            });
    }

    private bool HandleThreadMessage(MSG msg)
    {
        if (msg.message != WmRunAction) return false;
        var handle = GCHandle.FromIntPtr(msg.lParam);
        try { ((Action?)handle.Target)?.Invoke(); }
        catch (Exception ex) { _log.LogDebug(ex, "STA action threw"); }
        finally { handle.Free(); }
        return true;
    }

    private void PostToSta(Action action)
    {
        var handle = GCHandle.Alloc(action);
        if (!NativeMethods.PostThreadMessage(_loop.ThreadId, WmRunAction, nint.Zero, GCHandle.ToIntPtr(handle)))
            handle.Free();
    }

    public void ShowTransferring(FileTransferInfo info)
    {
        _totalBytes = info.TotalBytes;
        _lastProgressTick = 0;
        PostToSta(() => StartDialogOnSta(info));
        StartCancelPoll();
    }

    private void StartDialogOnSta(FileTransferInfo info)
    {
        StopOnSta();
        try
        {
            // ReSharper disable once SuspiciousTypeConversion.Global
            var dlg = (IProgressDialog)new ProgressDialogCom();
            dlg.StartProgressDialog(nint.Zero, null, ProgdlgAutotime | ProgdlgNominimize, nint.Zero);
            dlg.SetTitle("Hydra File Transfer");
            dlg.SetLine(1, BuildFileNames(info.FileNames), false, nint.Zero);
            dlg.SetLine(2, "", false, nint.Zero);
            dlg.SetProgress64(0, (ulong)info.TotalBytes);
            dlg.Timer(PdtimerReset, nint.Zero);
            _dlg = dlg;
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to create IProgressDialog"); }
    }

    // only called on the STA thread
    private void StopOnSta()
    {
        if (_dlg == null) return;
        var dlg = _dlg;
        _dlg = null;
        try { dlg.StopProgressDialog(); } catch (Exception ex) { _log.LogDebug(ex, "StopProgressDialog threw"); }
        try { Marshal.ReleaseComObject(dlg); } catch { /* already released */ }
    }

    public void SetCurrentFile(string fileName)
    {
        PostToSta(() =>
        {
            if (_dlg == null) return;
            _dlg.SetLine(1, fileName, false, nint.Zero);
        });
    }

    public void UpdateProgress(long bytesTransferred, double bytesPerSecond)
    {
        var total = _totalBytes;
        // throttle to avoid flooding the STA message queue
        var now = Environment.TickCount64;
        if (now - _lastProgressTick < ProgressThrottleMs) return;
        _lastProgressTick = now;
        PostToSta(() =>
        {
            if (_dlg == null) return;
            try
            {
                var speed = bytesPerSecond > 0 ? $"  ·  {FormatSpeed(bytesPerSecond)}" : "";
                if (total > 0)
                {
                    _dlg.SetProgress64((ulong)bytesTransferred, (ulong)total);
                    _dlg.SetLine(2, $"{FormatBytes(bytesTransferred)} / {FormatBytes(total)}{speed}", false, nint.Zero);
                }
                else
                {
                    // total unknown (receiving without prior FileTransferStart) — show bytes/speed only
                    _dlg.SetLine(2, $"{FormatBytes(bytesTransferred)}{speed}", false, nint.Zero);
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidComObjectException)
            {
                _log.LogDebug(ex, "UpdateProgress: dialog already released");
            }
        });
    }

    public void ShowCompleted()
    {
        PostToSta(() =>
        {
            if (_dlg == null) return;
            try
            {
                _dlg.SetProgress64((ulong)_totalBytes, (ulong)_totalBytes);
                _dlg.SetLine(2, "Transfer complete", false, nint.Zero);
            }
            catch { /* dialog may have closed */ }
        });
        _ = Task.Delay(1_500).ContinueWith(_ => Close());
    }

    public void ShowError(string message)
    {
        PostToSta(StopOnSta);
        _log.LogDebug("Transfer error: {Message}", message);
        _ = Task.Run(() => NativeMethods.MessageBoxW(nint.Zero, message, "Hydra — Transfer Failed",
            NativeMethods.MB_OK | NativeMethods.MB_ICONERROR));
    }

    public void Close()
    {
        CancellationTokenSource? cts;
        lock (_lock) { cts = _pollCts; _pollCts = null; }
        cts?.Cancel();
        PostToSta(StopOnSta);
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_lock) { cts = _pollCts; _pollCts = null; }
        cts?.Cancel();
        // StopOnSta runs before the loop exits (WM_QUIT is queued after it by loop.Dispose)
        PostToSta(StopOnSta);
        _loop.Dispose();
    }

    private void StartCancelPoll()
    {
        CancellationTokenSource newCts;
        lock (_lock)
        {
            _pollCts?.Cancel();
            _pollCts = newCts = new CancellationTokenSource();
        }
        _ = Task.Run(async () =>
        {
            try
            {
                while (!newCts.Token.IsCancellationRequested)
                {
                    try { await Task.Delay(150, newCts.Token); }
                    catch (OperationCanceledException) { return; }
                    // poll HasUserCancelled on the STA thread (it owns _dlg)
                    PostToSta(() =>
                    {
                        try
                        {
                            if (_dlg?.HasUserCancelled() != true) return;
                            lock (_lock) { _pollCts?.Cancel(); _pollCts = null; }
                            try { CancelRequested?.Invoke(); }
                            catch { /* ignored */ }
                        }
                        catch { /* ignored */ }
                    });
                }
            }
            catch (Exception ex) { _log.LogDebug(ex, "Cancel poll loop exited unexpectedly"); }
        }, CancellationToken.None);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1_000_000_000.0:0.#} GB",
        >= 1_000_000 => $"{bytes / 1_000_000.0:0.#} MB",
        >= 1_000 => $"{bytes / 1_000.0:0.#} KB",
        _ => $"{bytes} B",
    };

    private static string FormatSpeed(double bps) => bps switch
    {
        >= 1_000_000_000 => $"{bps / 1_000_000_000.0:0.#} GB/s",
        >= 1_000_000 => $"{bps / 1_000_000.0:0.#} MB/s",
        >= 1_000 => $"{bps / 1_000.0:0.#} KB/s",
        _ => $"{bps:0.#} B/s",
    };

    private static string BuildFileNames(string[] names)
    {
        var text = string.Join(", ", names.Take(3));
        if (names.Length > 3) text += $" +{names.Length - 3} more";
        return text;
    }
}

// IProgressDialog COM interface (shell32) — vtable order matches shlobj.h
[ComImport]
[Guid("EBBC7C04-315E-11d2-B62F-006097DF5BD4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IProgressDialog
{
    void StartProgressDialog(nint hwndParent, [MarshalAs(UnmanagedType.IUnknown)] object? punkEnableModless, uint dwFlags, nint pvReserved);
    void StopProgressDialog();
    void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pwzTitle);
    void SetAnimation(nint hInstAnimation, uint idAnimation);
    [PreserveSig]
    [return: MarshalAs(UnmanagedType.Bool)]
    bool HasUserCancelled();
    void SetProgress(uint dwCompleted, uint dwTotal);
    void SetProgress64(ulong ullCompleted, ulong ullTotal);
    void SetLine(uint dwLineNum, [MarshalAs(UnmanagedType.LPWStr)] string pwzString, [MarshalAs(UnmanagedType.Bool)] bool fCompactPath, nint pvReserved);
    void SetCancelMsg([MarshalAs(UnmanagedType.LPWStr)] string pwzCancelMsg, nint pvReserved);
    void Timer(uint dwTimerAction, nint pvReserved);
}

// CLSID_ProgressDialog
[ComImport]
[Guid("F8383852-FCD3-11d1-A6B9-006097DF5BD4")]
internal class ProgressDialogCom { }
