using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hydra.Relay;
using Tests.Setup;

namespace Tests.Desktops;

// hydra logs a push or pull answer as it takes it, just before writing it, and nothing once written: each test waits for
// that line, then pastes until the app reads what was copied, bounded
[Category("Desktops")]
[TestFixtureSource(typeof(DesktopPairs), nameof(DesktopPairs.All))]
public sealed class ClipboardTests(DesktopKind master, DesktopKind slave) : DesktopPair(master, slave)
{
    protected override string? Unsupported() =>
        EvdevMaster ? "an evdev master has no desktop, so no app to copy from or paste into" : null;

    [Test]
    public async Task Text_MasterToSlave()
    {
        var text = "m-" + Guid.NewGuid();
        await CopyOnMaster(new { copy = text }, "copied", text);

        var from = Slave.Log.Count;
        await CrossRight();
        await Slave.WaitForLog(PushOf(text), from);

        Assert.That(await PasteUntil(SlaveProbe, text), Is.EqualTo(text));
    }

    [Test]
    public async Task Text_SlaveToMaster()
    {
        var text = "s-" + Guid.NewGuid();
        await CrossRight();
        // a click on hs first, so the slave's app has the focus: kwin takes a selection only from a focused app
        await ClickOn(SlaveProbe);
        await CopyOnSlave(new { copy = text }, "copied", text);

        var from = Master.Log.Count;
        await ReturnLeft();
        await Master.WaitForLog($"Clipboard pull response from hs: text={text.Length},", from);

        Assert.That(await PasteUntil(MasterProbe!, text), Is.EqualTo(text));
    }

    [Test]
    public async Task Image_MasterToSlave()
    {
        var png = TestImages.Png;
        await CopyOnMaster(new { copy_png_base64 = Convert.ToBase64String(png) }, "copied_png", null);

        var from = Slave.Log.Count;
        await CrossRight();
        await Slave.WaitForLog("Clipboard push from hm:", from);

        // gtk re-encodes what it serves, so the pixels are compared, not the bytes
        var expected = Pixels.Of(png);
        var pasted = await PastePngUntil(SlaveProbe, expected);
        Assert.That(pasted, Is.EqualTo(expected));
    }

    [Test]
    public async Task Large_Text_Streams()
    {
        // past the stream threshold, so it goes over in chunks
        var text = LargeText(1536 * 1024);
        Assert.That(Encoding.UTF8.GetByteCount(text), Is.GreaterThan(ClipboardStreamSender.Threshold));
        // gtk 4.22.5 hangs serving an x11 INCR transfer (and its main loop stays frozen), so an x11 master's copy is xclip's
        await using var owner = X11Master ? await XclipOwner.Copy(MasterDesktop, text) : null;
        if (owner == null)
            await CopyOnMaster(new { copy = text }, "copied", text);

        var from = Slave.Log.Count;
        await CrossRight();
        await Slave.WaitForLog(" from hm begun:", from);
        await Slave.WaitForLog(PushOf(text), from);

        var pasted = await PasteUntil(SlaveProbe, text);
        Assert.That(pasted, Has.Length.EqualTo(text.Length));
        Assert.That(pasted, Is.EqualTo(text));
    }

    // a click on the master's app first, so it has the focus: kwin takes a selection only from a focused app, and keeps
    // the last one when an unfocused app copies; text is what was copied, null for a png
    private async Task CopyOnMaster(object command, string reply, string? text)
    {
        await ClickOn(MasterProbe!);
        await Copy(MasterProbe!, MasterKind, MasterDesktop, command, reply, text);
    }

    private Task CopyOnSlave(object command, string reply, string? text) =>
        Copy(SlaveProbe, SlaveKind, SlaveDesktop, command, reply, text);

    // the probe answers once it has asked for the selection, before the display server has taken its copy; a user's copy
    // is done before they move, so wait until the display server serves it (mutter has no data-control for wl-paste, so
    // gnome, and kde alike, ask the fake portal backend, which hears the compositor's owner changes whether or not hydra
    // listens yet)
    private static async Task Copy(Probe probe, DesktopKind kind, Desktop desktop, object command, string reply,
        string? text)
    {
        var before = kind is DesktopKind.Gnome or DesktopKind.Kde
            ? (await Selection(desktop))?.Serial
              ?? throw new AssertionException($"{desktop.Name}'s fake portal backend did not answer Control.Selection")
            : 0;
        var reader = ReaderFor(kind);
        var typesBefore = reader != null && text != null ? await Output(desktop, reader.File, reader.Types, Bound) : null;
        await probe.Command(command, reply);
        if (kind is DesktopKind.Gnome or DesktopKind.Kde)
            await OwnerTaken(desktop, before, text);
        else if (reader != null)
            await Served(desktop, reader, typesBefore, text);
    }

    // how a desktop's clipboard is read from outside it: the type listing, and the text
    private sealed record ClipboardReader(string File, string[] Types, string[] Text);

    private static ClipboardReader? ReaderFor(DesktopKind kind) => kind switch
    {
        DesktopKind.Openbox or DesktopKind.KdeX11 => new ClipboardReader("xclip",
            ["-o", "-selection", "clipboard", "-t", "TARGETS"], ["-o", "-selection", "clipboard"]),
        DesktopKind.Hyprland => new ClipboardReader("wl-paste", ["--list-types"], ["--no-newline"]),
        _ => null,
    };

    private static readonly string[] TextTypes = ["text/plain;charset=utf-8", "text/plain", "UTF8_STRING", "STRING"];

    private static bool ListsText(string? types) =>
        types != null && types.Split('\n', StringSplitOptions.TrimEntries).Any(TextTypes.Contains);

    // a full read of a large copy pulls it all through the app, so text is read only once the listing has gained a text
    // type, or (text listed already before the copy) at most this often
    private static readonly TimeSpan FullReadEvery = TimeSpan.FromMilliseconds(500);

    // polls until the display server serves text, or for a png lists image/png, bounded; a reader is never killed before
    // the bound, as gtk can wedge when the far end of a large transfer goes away
    private static async Task Served(Desktop desktop, ClipboardReader reader, string? typesBefore, string? text)
    {
        var bound = Stopwatch.StartNew();
        var readText = text != null && ListsText(typesBefore);
        Stopwatch? sinceRead = null;
        string? last = null;
        while (bound.Elapsed < Bound)
        {
            if (text == null)
            {
                last = await Output(desktop, reader.File, reader.Types, Remaining(bound));
                if (last != null && last.Contains("image/png"))
                    return;
            }
            else if (!readText)
            {
                readText = ListsText(await Output(desktop, reader.File, reader.Types, Remaining(bound)));
            }
            if (text != null && readText && (sinceRead == null || sinceRead.Elapsed >= FullReadEvery))
            {
                sinceRead = Stopwatch.StartNew();
                last = await Output(desktop, reader.File, reader.Text, Remaining(bound));
                if (last == text)
                    return;
            }
            await Task.Delay(50);
        }
        var wanted = text != null ? $"{text.Length} chars starting '{Prefix(text)}'" : "image/png";
        var got = last != null ? $"{last.Length} chars starting '{Prefix(last)}'" : "nothing";
        Assert.Fail($"{desktop.Name} never served the copy over {Bound} through {reader.File}: wanted {wanted}, got {got}");
    }

    private static TimeSpan Remaining(Stopwatch bound) =>
        Bound - bound.Elapsed > TimeSpan.FromSeconds(1) ? Bound - bound.Elapsed : TimeSpan.FromSeconds(1);

    private static string Prefix(string s) => s.Length <= 40 ? s : s[..40];

    // the stdout of a run, or null if it failed, printed nothing or outlasted timeout: not served yet
    private static async Task<string?> Output(Desktop desktop, string file, string[] args, TimeSpan timeout)
    {
        using var process = Process.Start(desktop.Process(file, args))
            ?? throw new InvalidOperationException($"{file} did not start");
        process.StandardInput.Close();
        _ = process.StandardError.ReadToEndAsync();
        using var timeoutSource = new CancellationTokenSource(timeout);
        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token);
            return process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return null;
        }
    }

    // polls the fake portal backend until a change after serial before is another client's, offering the copy's type
    private static async Task OwnerTaken(Desktop desktop, uint before, string? text)
    {
        string[] wanted = text != null ? ["text/plain;charset=utf-8", "text/plain"] : ["image/png"];
        var bound = Stopwatch.StartNew();
        PortalSelection? last;
        do
        {
            last = await Selection(desktop);
            if (last is { SessionOwns: false } && last.Serial > before && last.MimeTypes.Intersect(wanted).Any())
                return;
            await Task.Delay(50);
        } while (bound.Elapsed < Bound);
        var got = last != null
            ? $"serial {last.Serial}, session owns: {last.SessionOwns}, types [{string.Join(", ", last.MimeTypes)}]"
            : "no answer";
        Assert.Fail($"{desktop.Name}'s compositor never reported another client's copy after serial {before} over {Bound}: " +
                    $"wanted one of [{string.Join(", ", wanted)}], got {got}");
    }

    // the latest owner change the fake portal backend heard of, or null if gdbus failed or its answer did not parse
    private static async Task<PortalSelection?> Selection(Desktop desktop)
    {
        var output = await Output(desktop, "gdbus", ["call", "--session",
            "--dest", "org.freedesktop.impl.portal.desktop.hydratest", "--object-path", "/org/hydratest/Control",
            "--method", "org.hydratest.Control.Selection"], TimeSpan.FromSeconds(2));
        var match = output != null ? SelectionReply.Match(output) : null;
        if (match is not { Success: true })
            return null;
        var types = Regex.Matches(match.Groups["types"].Value, @"'((?:[^'\\]|\\.)*)'").Select(m => m.Groups[1].Value);
        return new PortalSelection(uint.Parse(match.Groups["serial"].Value), match.Groups["owns"].Value == "true",
            [.. types]);
    }

    // gdbus's text for (ubas): (uint32 3, false, ['text/plain', 'text/plain;charset=utf-8']), an empty list as @as []
    private static readonly Regex SelectionReply = new(
        @"^\(\s*(?:uint32\s+)?(?<serial>\d+)\s*,\s*(?<owns>true|false)\s*,\s*(?:@as\s+)?\[(?<types>.*)\]\s*,?\s*\)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private sealed record PortalSelection(uint Serial, bool SessionOwns, string[] MimeTypes);

    // the slave's debug line as it takes a push of text
    private static string PushOf(string text) => $"Clipboard push from hm: text={text.Length},";

    // xclip holding the clipboard of an x11 desktop, serving text until disposed
    private sealed class XclipOwner : IAsyncDisposable
    {
        private readonly Process _process;

        private XclipOwner(Process process) => _process = process;

        // once xclip owns the selection: with -quiet it stays in the foreground and says so on stderr
        public static async Task<XclipOwner> Copy(Desktop desktop, string text)
        {
            var process = Process.Start(desktop.Process("xclip", "-quiet", "-selection", "clipboard", "-i"))
                ?? throw new InvalidOperationException("xclip did not start");
            var owner = new XclipOwner(process);
            try
            {
                _ = process.StandardOutput.ReadToEndAsync();
                await process.StandardInput.WriteAsync(text);
                process.StandardInput.Close();
                using var bound = new CancellationTokenSource(Bound);
                while (await process.StandardError.ReadLineAsync(bound.Token) is { } line)
                {
                    if (line.StartsWith("Waiting for selection requests", StringComparison.Ordinal))
                    {
                        _ = process.StandardError.ReadToEndAsync();
                        return owner;
                    }
                }
                throw new InvalidOperationException($"xclip exited without owning the clipboard (exit {(process.HasExited ? process.ExitCode : -1)})");
            }
            catch
            {
                await owner.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
                using var bound = new CancellationTokenSource(Bound);
                await _process.WaitForExitAsync(bound.Token);
            }
            finally
            {
                _process.Dispose();
            }
        }
    }

    private static string LargeText(int length)
    {
        var builder = new StringBuilder(length);
        for (var line = 0; builder.Length < length; line++)
            builder.Append("line ").Append(line).Append(" of the large clipboard\n");
        return builder.ToString(0, length);
    }

    // pastes until the app reads expected, bounded; the last read
    private static async Task<string> PasteUntil(Probe probe, string expected)
    {
        var bound = Stopwatch.StartNew();
        string pasted;
        do
        {
            var reply = await probe.Command(new { paste = true }, "pasted");
            pasted = reply.GetProperty("pasted").GetString() ?? "";
        } while (pasted != expected && bound.Elapsed < Bound);
        return pasted;
    }

    private static async Task<string> PastePngUntil(Probe probe, string expected)
    {
        var bound = Stopwatch.StartNew();
        string pixels;
        do
        {
            var reply = await probe.Command(new { paste_png = true }, "pasted_png_base64");
            var base64 = reply.GetProperty("pasted_png_base64");
            pixels = base64.ValueKind == JsonValueKind.String ? Pixels.Of(Convert.FromBase64String(base64.GetString()!)) : "";
        } while (pixels != expected && bound.Elapsed < Bound);
        return pixels;
    }

    // a png's size and pixels as rgba hex: 8-bit rgb or rgba, not interlaced, which is what gtk and the test image are
    private static class Pixels
    {
        public static string Of(byte[] png)
        {
            var width = 0;
            var height = 0;
            var colour = 0;
            using var idat = new MemoryStream();
            for (var at = 8; at < png.Length;)
            {
                var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
                var type = Encoding.ASCII.GetString(png, at + 4, 4);
                var data = png.AsSpan(at + 8, length);
                if (type == "IHDR")
                {
                    width = BinaryPrimitives.ReadInt32BigEndian(data);
                    height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    if (data[8] != 8 || data[12] != 0)
                        throw new NotSupportedException($"png bit depth {data[8]}, interlace {data[12]}");
                    colour = data[9];
                }
                else if (type == "IDAT")
                {
                    idat.Write(data);
                }
                at += 12 + length;
            }
            var channels = colour switch
            {
                2 => 3,
                6 => 4,
                _ => throw new NotSupportedException($"png colour type {colour}"),
            };

            using var raw = new MemoryStream();
            idat.Position = 0;
            using (var zlib = new ZLibStream(idat, CompressionMode.Decompress))
                zlib.CopyTo(raw);
            var rows = raw.ToArray();

            var stride = width * channels;
            var previous = new byte[stride];
            var builder = new StringBuilder($"{width}x{height}:");
            for (var y = 0; y < height; y++)
            {
                var filter = rows[y * (stride + 1)];
                var row = rows.AsSpan(y * (stride + 1) + 1, stride).ToArray();
                Unfilter(filter, row, previous, channels);
                for (var x = 0; x < width; x++)
                {
                    var p = x * channels;
                    builder.Append($"{row[p]:x2}{row[p + 1]:x2}{row[p + 2]:x2}{(channels == 4 ? row[p + 3] : 255):x2}");
                }
                previous = row;
            }
            return builder.ToString();
        }

        private static void Unfilter(byte filter, byte[] row, byte[] up, int bpp)
        {
            for (var i = 0; i < row.Length; i++)
            {
                int left = i >= bpp ? row[i - bpp] : 0;
                int above = up[i];
                int corner = i >= bpp ? up[i - bpp] : 0;
                row[i] = (byte)(row[i] + filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => above,
                    3 => (left + above) / 2,
                    4 => Paeth(left, above, corner),
                    _ => throw new NotSupportedException($"png filter {filter}"),
                });
            }
        }

        private static int Paeth(int a, int b, int c)
        {
            var p = a + b - c;
            var pa = Math.Abs(p - a);
            var pb = Math.Abs(p - b);
            var pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
    }
}
