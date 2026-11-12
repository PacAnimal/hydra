using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Linux;

// The Wayland clipboard through wl-clipboard (wl-paste / wl-copy), which speak the compositor's data-control protocol
// and so need no focused window. XWayland's clipboard only reaches Wayland's when the compositor bridges it, which a
// Hyprland master cannot count on.
//
// Each write is a wl-copy kept in the foreground: it serves the clipboard until another client copies, then exits, so
// while it runs the clipboard is still Hydra's write (and ReadAll answers nothing, as X11 does for its own selection).
// wl-copy offers one MIME type per process, so a write keeps the text when there is any, else the image; the formats
// left out are reported as refused.
internal sealed class WaylandClipboardSync(ILogger<WaylandClipboardSync> log) : IClipboardSync, IDisposable
{
    private const int ReadTimeoutMs = 2000;
    // how long a write waits for wl-copy to take the clipboard before answering
    private const int WriteSettleMs = 50;

    // KDE's mark of a password manager's copy, which wl-copy --sensitive offers
    private const string PasswordHint = "x-kde-passwordManagerHint";

    private static readonly string[] TextTypes = ["text/plain;charset=utf-8", "UTF8_STRING", "text/plain", "TEXT", "STRING"];

    private readonly Lock _lock = new();
    private Process? _clipboardOwner;
    private Process? _primaryOwner;

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

        var textType = TextTypes.FirstOrDefault(types.Contains);
        var text = textType != null ? Run(["-n", "-t", textType]) : null;
        var image = types.Contains("image/png") ? Run(["-t", "image/png"]) : null;
        var html = types.Contains("text/html") ? Run(["-n", "-t", "text/html"]) : null;
        var concealed = types.Contains(PasswordHint) && Run(["-n", "-t", PasswordHint]) is { } hint && Encoding.UTF8.GetString(hint).Trim() == "secret";

        return new ClipboardRead(
            text != null ? Encoding.UTF8.GetString(text) : null,
            image is { Length: > 0 } ? image : null,
            html != null ? Encoding.UTF8.GetString(html) : null,
            null,
            concealed);
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
        var process = Copy(["-p"], Encoding.UTF8.GetBytes(text));
        lock (_lock)
        {
            Stop(_primaryOwner);
            _primaryOwner = process;
        }
        return process != null;
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
            args.AddRange(["-t", "text/plain;charset=utf-8"]);
            written = contents with { ImagePng = null, Html = null, Rtf = null };
        }
        else if (contents.ImagePng != null)
        {
            data = contents.ImagePng;
            args.AddRange(["-t", "image/png"]);
            written = contents with { Html = null, Rtf = null };
        }
        else if (contents.Html != null)
        {
            data = Encoding.UTF8.GetBytes(contents.Html);
            args.AddRange(["-t", "text/html"]);
            written = contents with { Rtf = null };
        }
        else
        {
            // nothing for the clipboard: leave it as it is, and carry only the selection
            if (contents.PrimaryText is { } selection) SetPrimaryText(selection);
            return contents;
        }

        var process = Copy(args, data);
        lock (_lock)
        {
            Stop(_clipboardOwner);
            _clipboardOwner = process;
        }
        if (process == null) return null;

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
                if (log.IsEnabled(LogLevel.Warning)) log.LogWarning("wl-copy failed: {Error}", process.StandardError.ReadToEnd().Trim());
                process.Dispose();
                return null;
            }
            return process;
        }
        catch (Exception e)
        {
            log.LogWarning(e, "wl-copy could not be run");
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
            using var output = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
            var stderr = process.StandardError.ReadToEndAsync();
            if (!copy.Wait(ReadTimeoutMs) || !process.WaitForExit(ReadTimeoutMs))
            {
                try { process.Kill(); } catch { /* already gone */ }
                throw new ClipboardUnreadableException("wl-paste did not answer in time");
            }
            if (process.ExitCode == 0) return output.ToArray();
            // "Nothing is copied" / "No selection" / "No suitable type of content copied": empty, not unreadable
            var error = stderr.Result;
            if (error.Contains("Nothing is copied", StringComparison.OrdinalIgnoreCase)
                || error.Contains("No selection", StringComparison.OrdinalIgnoreCase)
                || error.Contains("No suitable type", StringComparison.OrdinalIgnoreCase))
                return null;
            throw new ClipboardUnreadableException($"wl-paste failed: {error.Trim()}");
        }
    }

    private static void Stop(Process? process)
    {
        if (process == null) return;
        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch
        {
            // already gone
        }
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
