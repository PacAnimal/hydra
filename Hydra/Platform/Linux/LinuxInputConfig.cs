namespace Hydra.Platform.Linux;

// Native input configuration for headless Linux.
//
// Hydra reads raw evdev, so none of the usual stack applies to it automatically: libinput, Xorg and
// console-setup never see these devices. The *configuration* those tools read is still where an
// administrator expects to set this, though, so read it directly rather than inventing Hydra-specific
// settings:
//   * keyboard layout - XKB_DEFAULT_* if set, else /etc/default/keyboard (keyboard-configuration)
//   * pointer speed   - udev hwdb MOUSE_DPI, the same property libinput uses
internal static class LinuxInputConfig
{
    private const string KeyboardFile = "/etc/default/keyboard";

    // libinput normalises pointer motion to 1000 dpi. Match that, so MOUSE_DPI means here what it
    // means everywhere else: a mouse declared at 2000 dpi covers the same physical distance as one
    // declared at 1000, and declaring *less* than the hardware makes the pointer travel further per
    // inch. Speeding a mouse up therefore means declaring a lower dpi, not a higher one.
    private const double ReferenceDpi = 1000.0;

    internal sealed record XkbNames(string Layout, string Model, string? Variant, string? Options);

    internal static XkbNames ResolveXkb()
    {
        var file = ReadKeyboardFile();
        return new XkbNames(
            Pick("XKB_DEFAULT_LAYOUT", file, "XKBLAYOUT") ?? "us",
            Pick("XKB_DEFAULT_MODEL", file, "XKBMODEL") ?? "pc105",
            Pick("XKB_DEFAULT_VARIANT", file, "XKBVARIANT"),
            Pick("XKB_DEFAULT_OPTIONS", file, "XKBOPTIONS"));
    }

    // Delta multiplier for one pointer device. 1.0 when the device has no MOUSE_DPI, so hardware
    // without an hwdb entry behaves exactly as it did before.
    internal static double MouseScale(string devicePath)
    {
        var dpi = MouseDpi(devicePath);
        if (dpi is null || dpi <= 0) return 1.0;
        return Math.Clamp(ReferenceDpi / dpi.Value, 0.1, 10.0);
    }

    internal static int? MouseDpi(string devicePath)
    {
        try
        {
            // udev stores resolved properties per device under /run/udev/data, keyed c<major>:<minor>.
            // Read them from there rather than shelling out to udevadm.
            var name = Path.GetFileName(devicePath);
            var dev = File.ReadAllText($"/sys/class/input/{name}/dev").Trim();   // e.g. "13:69"
            foreach (var line in File.ReadLines($"/run/udev/data/c{dev}"))
            {
                const string prefix = "E:MOUSE_DPI=";
                if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
                return ParseDpi(line[prefix.Length..]);
            }
        }
        catch
        {
            // no hwdb entry, no udev db, or an unreadable device - treat as unset
        }
        return null;
    }

    // "1000@125", or several resolutions with the default starred: "*1000@125 2000@125 3000@125"
    private static int? ParseDpi(string value)
    {
        var tokens = value.Split(" ", StringSplitOptions.RemoveEmptyEntries);
        var chosen = tokens.FirstOrDefault(t => t.StartsWith("*", StringComparison.Ordinal)) ?? tokens.FirstOrDefault();
        if (chosen is null) return null;
        var digits = chosen.TrimStart(Convert.ToChar("*")).Split("@")[0];
        return int.TryParse(digits, out var dpi) ? dpi : null;
    }

    private static string? Pick(string envVar, Dictionary<string, string> file, string fileKey)
    {
        var env = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return file.TryGetValue(fileKey, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
    }

    // Shell-style KEY="value" lines, as written by keyboard-configuration.
    private static Dictionary<string, string> ReadKeyboardFile()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var raw in File.ReadLines(KeyboardFile))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                var eq = line.IndexOf("=", StringComparison.Ordinal);
                if (eq <= 0) continue;
                result[line[..eq].Trim()] = line[(eq + 1)..].Trim().Trim(Convert.ToChar("\""));
            }
        }
        catch
        {
            // absent on non-Debian systems; env vars and defaults cover it
        }
        return result;
    }
}
