using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Hydra.FileTransfer;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Windows;

// wraps IProgressDialog (shell32) — the same COM progress dialog Windows Explorer uses for file copies.
[SupportedOSPlatform("windows")]
public sealed class WindowsProgressDialog(ILogger<WindowsProgressDialog> log) : IFileTransferDialog, IDisposable
{
    private readonly ILogger<WindowsProgressDialog> _log = log;
    private readonly Lock _lock = new();
    private IProgressDialog? _dlg;
    private long _totalBytes;
    private CancellationTokenSource? _pollCts;

    private const uint ProgdlgAutotime = 0x00000002; // show estimated time remaining
    private const uint ProgdlgNominimize = 0x00000008; // no minimize button
    private const uint PdtimerReset = 1;          // reset elapsed time for AUTOTIME estimate

    public event Action? CancelRequested;

    public void ShowPending(FileTransferInfo info) => ShowTransferring(info);

    public void ShowTransferring(FileTransferInfo info)
    {
        _totalBytes = info.TotalBytes;
        lock (_lock)
        {
            StopLocked();
            var dlg = (IProgressDialog)new ProgressDialogCom();
            var verb = info.IsSender ? "Sending" : "Receiving";
            var count = info.FileCount;
            dlg.StartProgressDialog(nint.Zero, null, ProgdlgAutotime | ProgdlgNominimize, nint.Zero);
            dlg.SetTitle("Hydra File Transfer");
            dlg.SetLine(1, $"{verb} {count} {(count == 1 ? "file" : "files")} ({FormatBytes(info.TotalBytes)})", false, nint.Zero);
            dlg.SetLine(2, BuildFileNames(info.FileNames), false, nint.Zero);
            dlg.SetProgress64(0, (ulong)info.TotalBytes);
            dlg.Timer(PdtimerReset, nint.Zero);
            _dlg = dlg;
        }
        StartCancelPoll();
    }

    public void UpdateProgress(long bytesTransferred, double bytesPerSecond)
    {
        IProgressDialog? dlg;
        long total;
        lock (_lock) { dlg = _dlg; total = _totalBytes; }
        if (dlg == null || total <= 0) return;
        try
        {
            dlg.SetProgress64((ulong)bytesTransferred, (ulong)total);
            var speed = bytesPerSecond > 0 ? $"  ·  {FormatSpeed(bytesPerSecond)}" : "";
            dlg.SetLine(2, $"{FormatBytes(bytesTransferred)} / {FormatBytes(total)}{speed}", false, nint.Zero);
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException)
        {
            _log.LogDebug(ex, "UpdateProgress: dialog already released");
        }
    }

    public void ShowCompleted()
    {
        IProgressDialog? dlg;
        lock (_lock) dlg = _dlg;
        if (dlg == null) return;
        try
        {
            dlg.SetProgress64((ulong)_totalBytes, (ulong)_totalBytes);
            dlg.SetLine(2, "Transfer complete", false, nint.Zero);
        }
        catch { /* dialog may have closed */ }
        // stop after a brief pause so the user sees 100%
        _ = Task.Delay(1_500).ContinueWith(_ => Close());
    }

    public void ShowError(string message)
    {
        lock (_lock) StopLocked();
        _log.LogDebug("Transfer error dialog: {Message}", message);
        _ = Task.Run(() =>
            NativeMethods.MessageBoxW(nint.Zero, message, "Hydra — Transfer Failed",
                NativeMethods.MB_OK | NativeMethods.MB_ICONERROR));
    }

    public void Close()
    {
        lock (_lock) StopLocked();
    }

    public void Dispose()
    {
        lock (_lock) StopLocked();
    }

    // caller must hold _lock
    private void StopLocked()
    {
        _pollCts?.Cancel();
        _pollCts = null;
        if (_dlg == null) return;
        var dlg = _dlg;
        _dlg = null;
        try { dlg.StopProgressDialog(); }
        catch (Exception ex) { _log.LogDebug(ex, "StopProgressDialog threw"); }
        try { Marshal.ReleaseComObject(dlg); } catch { /* already released */ }
    }

    private void StartCancelPoll()
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            _pollCts?.Cancel();
            _pollCts = cts = new CancellationTokenSource();
        }
        _ = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try { await Task.Delay(100, cts.Token); }
                catch (OperationCanceledException) { return; }
                IProgressDialog? dlg;
                lock (_lock) dlg = _dlg;
                if (dlg == null) return;
                try
                {
                    if (dlg.HasUserCancelled())
                    {
                        try { CancelRequested?.Invoke(); } catch { /* handler errors are callers' problem */ }
                        return;
                    }
                }
                catch (Exception ex) when (ex is COMException or InvalidComObjectException) { return; }
            }
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
