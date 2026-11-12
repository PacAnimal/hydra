using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Hydra.Platform.Linux;

// Hyprland's request socket ($XDG_RUNTIME_DIR/hypr/$HYPRLAND_INSTANCE_SIGNATURE/.socket.sock): one request per
// connection, the reply runs to EOF. Requests prefixed "j/" answer in JSON. Every call is a short round trip on
// the compositor's main loop, so callers keep them off hot paths (see HyprlandInputHandler's throttle).
internal static class HyprlandIpc
{
    private static readonly string? SocketPath = FindSocket();

    // true when running inside a Hyprland session whose socket is reachable
    internal static bool Available => SocketPath != null;

    private static string? FindSocket()
    {
        var sig = Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE");
        if (string.IsNullOrEmpty(sig)) return null;
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtime)) return null;
        foreach (var dir in new[] { Path.Combine(runtime, "hypr", sig), Path.Combine("/tmp/hypr", sig) })
        {
            var path = Path.Combine(dir, ".socket.sock");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    internal static string Request(string command)
    {
        if (SocketPath == null) throw new InvalidOperationException("Hyprland IPC socket not found");
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.ReceiveTimeout = 1000;
        socket.SendTimeout = 1000;
        socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
        socket.Send(Encoding.UTF8.GetBytes(command));
        var buffer = new byte[8192];
        using var reply = new MemoryStream();
        int n;
        while ((n = socket.Receive(buffer)) > 0) reply.Write(buffer, 0, n);
        return Encoding.UTF8.GetString(reply.GetBuffer(), 0, (int)reply.Length);
    }

    // the cursor in layout coordinates (logical pixels, the same space monitors' x/y live in)
    internal static (double X, double Y)? CursorPos()
    {
        try
        {
            using var doc = JsonDocument.Parse(Request("j/cursorpos"));
            return (doc.RootElement.GetProperty("x").GetDouble(), doc.RootElement.GetProperty("y").GetDouble());
        }
        catch
        {
            return null;
        }
    }

    internal static void MoveCursor(int x, int y) => Command(
        string.Create(CultureInfo.InvariantCulture, $"hl.dispatch(hl.dsp.cursor.move({{ x = {x}, y = {y} }}))"),
        string.Create(CultureInfo.InvariantCulture, $"dispatch movecursor {x} {y}"));

    internal static void SetCursorInvisible(bool invisible) => Command(
        $"hl.config({{ cursor = {{ invisible = {(invisible ? "true" : "false")} }} }})",
        $"keyword cursor:invisible {(invisible ? "true" : "false")}");

    internal static void FocusMonitor(string name) => Command(
        $"hl.dispatch(hl.dsp.focus({{ monitor = \"{name}\" }}))",
        $"dispatch focusmonitor {name}");

    // Hyprland 0.56 replaced its config language with Lua: keyword and dispatch are refused, and commands go through
    // eval as Lua. Older releases have no eval. The first command that gets "ok" settles which form this one speaks.
    private static volatile int _lua = -1;   // -1 unknown, 0 legacy, 1 Lua

    private static void Command(string lua, string legacy)
    {
        try
        {
            if (_lua == 1) { Request($"eval {lua}"); return; }
            if (_lua == 0) { Request(legacy); return; }
            if (Request($"eval {lua}").Trim() == "ok") { _lua = 1; return; }
            if (Request(legacy).Trim() == "ok") _lua = 0;
        }
        catch
        {
            // compositor gone or busy: the next call tries again
        }
    }

    internal static string? GetOptionString(string option)
    {
        try
        {
            using var doc = JsonDocument.Parse(Request($"j/getoption {option}"));
            return doc.RootElement.TryGetProperty("str", out var s) ? s.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    internal sealed record Monitor(string Name, string? Description, int X, int Y, int Width, int Height);

    // enabled monitors, with their size in logical pixels (mode size, rotated by the transform, divided by the scale)
    internal static List<Monitor> Monitors() => ParseMonitors(Request("j/monitors"));

    internal static List<Monitor> ParseMonitors(string json)
    {
        var result = new List<Monitor>();
        using var doc = JsonDocument.Parse(json);
        foreach (var m in doc.RootElement.EnumerateArray())
        {
            if (m.TryGetProperty("disabled", out var d) && d.ValueKind == JsonValueKind.True) continue;
            var width = m.GetProperty("width").GetInt32();
            var height = m.GetProperty("height").GetInt32();
            var scale = m.TryGetProperty("scale", out var s) ? s.GetDouble() : 1.0;
            var transform = m.TryGetProperty("transform", out var t) ? t.GetInt32() : 0;
            if (transform % 2 == 1) (width, height) = (height, width);   // 90/270, flipped or not
            if (scale > 0)
            {
                width = (int)Math.Round(width / scale);
                height = (int)Math.Round(height / scale);
            }
            result.Add(new Monitor(
                m.GetProperty("name").GetString() ?? "",
                m.TryGetProperty("description", out var desc) ? desc.GetString() : null,
                m.GetProperty("x").GetInt32(), m.GetProperty("y").GetInt32(), width, height));
        }
        return result;
    }
}
