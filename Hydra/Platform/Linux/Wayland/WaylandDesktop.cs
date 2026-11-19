using System.Text;
using Microsoft.Extensions.Logging;

namespace Hydra.Platform.Linux.Wayland;

internal static class WaylandDesktop
{
    private const UnixFileMode Readable =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    // a Wayland login session, which Hydra drives through the desktop portals rather than X11
    internal static bool IsSession =>
        OperatingSystem.IsLinux()
        && Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland"
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    // Hyprland sets its instance signature in every session it starts
    internal static bool IsHyprland =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE"));

    // The portal knows a host app only by a desktop file named for its app id: without one, Register fails ("App info
    // not found") and GlobalShortcuts refuses the app outright. So one is kept in the user's applications directory,
    // hidden from menus, rewritten when the executable it names has moved. A failure is logged and the run goes on.
    // True when the file was written now (created or rewritten), so the portal may not have indexed it yet.
    internal static bool EnsureDesktopEntry(string appId, ILogger log) =>
        Environment.ProcessPath is { } exe && EnsureDesktopEntry(Path.Combine(ApplicationsDir(), appId + ".desktop"), exe, log);

    internal static bool EnsureDesktopEntry(string path, string exe, ILogger log)
    {
        try
        {
            var existing = File.Exists(path) ? File.ReadAllText(path) : null;
            if (!NeedsWrite(existing, exe)) return false;
            Write(path, DesktopEntry(exe));
            if (log.IsEnabled(LogLevel.Information))
                log.LogInformation(LogEvents.FilePaths, "{Action} {Path} so the desktop portal knows Hydra (needed for its permissions and hotkeys)",
                    existing is null ? "Wrote" : "Updated", path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (log.IsEnabled(LogLevel.Warning))
                log.LogWarning(LogEvents.FilePaths, "Cannot write the desktop entry {Path}; the portal may refuse hotkeys at home: {Error}", path, e.Message);
            return false;
        }
    }

    // $XDG_DATA_HOME/applications, the data home defaulting to ~/.local/share
    private static string ApplicationsDir()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(dataHome) || !Path.IsPathRooted(dataHome))
            dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(dataHome, "applications");
    }

    internal static string DesktopEntry(string exe) =>
        $"""
         [Desktop Entry]
         Type=Application
         Name=Hydra
         Comment=Software KVM; written by Hydra so the desktop portal knows it
         Exec={QuoteExec(exe)}
         NoDisplay=true

         """;

    // missing, or naming another executable than exe
    internal static bool NeedsWrite(string? existing, string exe) =>
        existing is null || ExecOf(existing) != QuoteExec(exe);

    // the Exec value of the [Desktop Entry] group; null when it has none
    internal static string? ExecOf(string entry)
    {
        var inEntry = false;
        foreach (var raw in entry.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) inEntry = line == "[Desktop Entry]";
            else if (inEntry && line.StartsWith("Exec=", StringComparison.Ordinal)) return line["Exec=".Length..];
        }
        return null;
    }

    // the desktop entry spec's Exec quoting: a path with a reserved character is double-quoted, with ", `, $ and \
    // escaped by a backslash, and every backslash then doubled again by the string's own escape rule
    internal static string QuoteExec(string exe)
    {
        if (!exe.Any(c => c is ' ' or '\t' or '"' or '\'' or '\\' or '>' or '<' or '~' or '|' or '&' or ';' or '$' or '*' or '?' or '#' or '(' or ')' or '`'))
            return exe;
        var quoted = new StringBuilder("\"");
        foreach (var c in exe)
        {
            if (c is '"' or '`' or '$' or '\\') quoted.Append('\\');
            quoted.Append(c);
        }
        quoted.Append('"');
        return quoted.ToString().Replace("\\", "\\\\", StringComparison.Ordinal);
    }

    // a temp file beside it, moved over it: a reader never sees half a file
    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".hydra-tmp";
        File.WriteAllText(temp, content);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, Readable);
        File.Move(temp, path, overwrite: true);
    }
}
