using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Linux;

// PortalClipboardSync's fallback when the portal offers no clipboard: wl-clipboard (wl-paste / wl-copy), which speaks the
// compositor's data-control protocol and so needs no focused window.
//
// Each write is a wl-copy kept in the foreground: it serves the clipboard until another client copies, then exits, so
// while it runs the clipboard is still Hydra's write (and ReadAll answers nothing, as X11 does for its own selection).
// wl-copy offers one MIME type per process, so a write keeps the text when there is any, else the image; the formats
// left out are reported as refused. No change counter: wl-paste --watch could follow the owners, but hashes decide
// where a platform keeps none.
internal sealed class WaylandClipboardSync : IClipboardSync, IDisposable
{
    private const int ReadTimeoutMs = 2000;
    // how long a write waits for wl-copy to take the clipboard before answering
    private const int WriteSettleMs = 50;

    private readonly ILogger _log;
    private readonly Lock _lock = new();
    private Process? _clipboardOwner;
    private Process? _primaryOwner;

    // under another's logger, as PortalClipboardSync's fallback
    internal WaylandClipboardSync(ILogger log) => _log = log;

    internal static bool Available => OnPath("wl-copy") && OnPath("wl-paste") && Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") != null;

    private static bool OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, name)));

    public ClipboardRead ReadAll()
    {
        if (OwnsClipboard()) return ClipboardRead.Empty;

        var listed = Run(["--list-types"]);
        if (listed == null) return ClipboardRead.Empty;   // nothing copied
        var types = Encoding.UTF8.GetString(listed).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

        return WaylandMime.ReadOffered(types, (type, _) => Run(type == WaylandMime.Png ? ["-t", type] : ["-n", "-t", type]), _log);
    }

    // never echo-suppressed: the selection travels as it stands, even when Hydra set it
    public string? GetPrimaryText()
    {
        try
        {
            return Run(["-p", "-n"]) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;
        }
        catch (ClipboardUnreadableException)
        {
            return null;   // a selection that could not be read is none
        }
    }

    public bool SetPrimaryText(string text)
    {
        // taking the selection back with the text it already holds would clear its owner's highlight
        if (GetPrimaryText() == text) return true;
        // a failed copy leaves the old owner serving, so the selection is not lost with it
        if (Copy(["-p"], Encoding.UTF8.GetBytes(text)) is not { } process) return false;
        lock (_lock)
        {
            Stop(_primaryOwner);
            _primaryOwner = process;
        }
        return true;
    }

    public bool CanUseEchoFallback() => OwnsClipboard();

    public ClipboardSnapshot? SetClipboard(ClipboardSnapshot contents)
    {
        // the placeholder carries the hint too, so clipboard managers never keep it
        var sensitive = contents.Concealed || contents.IsClear;
        List<string> args = sensitive ? ["--sensitive"] : [];

        ClipboardSnapshot written;
        byte[] data;
        if (!string.IsNullOrEmpty(contents.Text))
        {
            data = Encoding.UTF8.GetBytes(contents.Text);
            args.AddRange(["-t", WaylandMime.OfferedText[0]]);
            written = contents with { ImagePng = null, Html = null, Rtf = null };
        }
        else if (contents.ImagePng != null)
        {
            data = contents.ImagePng;
            args.AddRange(["-t", WaylandMime.Png]);
            written = contents with { Html = null, Rtf = null };
        }
        else if (contents.Html != null)
        {
            data = Encoding.UTF8.GetBytes(contents.Html);
            args.AddRange(["-t", WaylandMime.Html]);
            written = contents with { Rtf = null };
        }
        else if (contents.Rtf != null)
        {
            data = contents.Rtf;
            args.AddRange(["-t", WaylandMime.Rtf]);
            written = contents;
        }
        else
        {
            // nothing for the clipboard: leave it as it is, and carry only the selection
            if (contents.PrimaryText is { } selection) SetPrimaryText(selection);
            return contents;
        }

        // a failed copy leaves the old owner serving: the clipboard was not replaced, so it must not be emptied either
        if (Copy(args, data) is not { } process) return null;
        lock (_lock)
        {
            Stop(_clipboardOwner);
            _clipboardOwner = process;
        }

        if (contents.PrimaryText is { } primaryText) SetPrimaryText(primaryText);
        return written;
    }

    private bool OwnsClipboard()
    {
        lock (_lock)
            return _clipboardOwner is { HasExited: false };
    }

    // a foreground wl-copy fed the data on stdin; it keeps serving until another client copies. null when it failed
    private Process? Copy(IEnumerable<string> args, byte[] data)
    {
        try
        {
            var psi = new ProcessStartInfo("wl-copy")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--foreground");
            foreach (var a in args) psi.ArgumentList.Add(a);

            var process = Process.Start(psi);
            if (process == null) return null;
            process.StandardInput.BaseStream.Write(data);
            process.StandardInput.Close();
            // it has taken the clipboard once it has read stdin; an early exit means it could not
            if (process.WaitForExit(WriteSettleMs) && process.ExitCode != 0)
            {
                if (_log.IsEnabled(LogLevel.Warning)) _log.LogWarning("wl-copy failed: {Error}", process.StandardError.ReadToEnd().Trim());
                process.Dispose();
                return null;
            }
            return process;
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "wl-copy could not be run");
            return null;
        }
    }

    // wl-paste's output, or null when the clipboard holds nothing (or not that type); throws when it cannot be read
    private static byte[]? Run(IEnumerable<string> args)
    {
        Process? process;
        try
        {
            var psi = new ProcessStartInfo("wl-paste")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            process = Process.Start(psi);
        }
        catch (Exception e)
        {
            throw new ClipboardUnreadableException("wl-paste could not be run", e);
        }
        if (process == null) throw new ClipboardUnreadableException("wl-paste could not be run");

        using (process)
        {
            // read on the thread pool, so the timeout holds against an owner that never finishes; killing wl-paste ends it
            var output = Task.Run(() => WaylandMime.ReadCapped(process.StandardOutput.BaseStream, ClipboardUtils.MaxClipboardBytes));
            var stderr = process.StandardError.ReadToEndAsync();
            byte[]? data;
            try
            {
                if (!output.Wait(ReadTimeoutMs)) throw Unanswered(process);
                data = output.Result;
            }
            catch (AggregateException e)
            {
                Kill(process);
                throw new ClipboardUnreadableException("wl-paste's output could not be read", e.InnerException);
            }
            if (data == null)
            {
                // over the cap: no clipboard could carry it, so that format is left out, as TrimToFit leaves it
                Kill(process);
                return null;
            }
            if (!process.WaitForExit(ReadTimeoutMs)) throw Unanswered(process);
            if (process.ExitCode == 0) return data;
            // "Nothing is copied" / "No selection" / "No suitable type of content copied": empty, not unreadable
            var error = stderr.Result;
            if (error.Contains("Nothing is copied", StringComparison.OrdinalIgnoreCase)
                || error.Contains("No selection", StringComparison.OrdinalIgnoreCase)
                || error.Contains("No suitable type", StringComparison.OrdinalIgnoreCase))
                return null;
            throw new ClipboardUnreadableException($"wl-paste failed: {error.Trim()}");
        }
    }

    private static ClipboardUnreadableException Unanswered(Process process)
    {
        Kill(process);
        return new ClipboardUnreadableException("wl-paste did not answer in time");
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch
        {
            // already gone
        }
    }

    private static void Stop(Process? process)
    {
        if (process == null) return;
        Kill(process);
        process.Dispose();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            Stop(_clipboardOwner);
            Stop(_primaryOwner);
            _clipboardOwner = _primaryOwner = null;
        }
    }
}
