using System.Diagnostics;

namespace Tests.Desktops;

public enum DesktopKind
{
    Openbox,
    Gnome,
    Kde,
    KdeX11,
    Evdev,
    Hyprland,
}

/// <summary>One headless desktop started by desktop.sh, and the environment its clients run in.</summary>
public sealed class Desktop : IAsyncDisposable
{
    private const string Script = "desktop.sh";

    private Desktop(DesktopKind kind, string name, string? layout, string? options, IReadOnlyDictionary<string, string> env)
    {
        Kind = kind;
        Name = name;
        Layout = layout;
        Options = options;
        Env = env;
    }

    public DesktopKind Kind { get; }
    public string Name { get; }
    // the xkb layout list it started with, as in "us,ru"; null is the image's default, us
    public string? Layout { get; }
    // the xkb option list it started with, as in "grp:switch"; null is the image's default
    public string? Options { get; }
    public IReadOnlyDictionary<string, string> Env { get; }
    public bool IsWayland => Kind == DesktopKind.Hyprland || Env.ContainsKey("WAYLAND_DISPLAY");

    public static string Arg(DesktopKind kind) => kind switch
    {
        DesktopKind.Openbox => "openbox",
        DesktopKind.Gnome => "gnome",
        DesktopKind.Kde => "kde",
        DesktopKind.KdeX11 => "kde-x11",
        DesktopKind.Evdev => "evdev",
        DesktopKind.Hyprland => "hyprland",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    // whether the lane lists this kind in HYDRA_DESKTOP_KINDS
    public static bool Available(DesktopKind kind)
    {
        var kinds = Environment.GetEnvironmentVariable("HYDRA_DESKTOP_KINDS") ?? "";
        return kinds.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(Arg(kind));
    }

    public static async Task<Desktop> Start(DesktopKind kind, string name, string? layout = null, string? options = null)
    {
        if (kind == DesktopKind.Evdev)
        {
            if (layout != null)
                throw new ArgumentException("evdev has no desktop to load a layout into", nameof(layout));
            if (options != null)
                throw new ArgumentException("evdev has no desktop to load xkb options into", nameof(options));
            return new Desktop(kind, name, null, null, new Dictionary<string, string>());
        }

        var result = await Run(layout, options, Arg(kind), name);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"desktop.sh {Arg(kind)} {name} exited {result.ExitCode}: {result.Stderr}");

        var env = new Dictionary<string, string>();
        foreach (var line in result.Stdout.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = line.IndexOf('=');
            if (eq > 0)
                env[line[..eq]] = line[(eq + 1)..];
        }
        return new Desktop(kind, name, layout, options, env);
    }

    // locks the active xkb layout group, 0 being the first layout in Layout
    public async Task Group(int index)
    {
        if (Kind == DesktopKind.Evdev)
            throw new InvalidOperationException("evdev has no desktop to switch the layout group of");
        var result = await Run(null, null, "group", Name, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"desktop.sh group {Name} {index} exited {result.ExitCode}: {result.Stderr}");
    }

    // a start info for a client of this desktop: its env, software rendering, all streams redirected
    public ProcessStartInfo Process(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        foreach (var (key, value) in Env)
            info.Environment[key] = value;
        info.Environment["GSK_RENDERER"] = "cairo";
        info.Environment["LIBGL_ALWAYS_SOFTWARE"] = "1";
        return info;
    }

    public async ValueTask DisposeAsync()
    {
        if (Kind != DesktopKind.Evdev)
            await Run(null, null, "stop", Name);
    }

    private static async Task<ScriptResult> Run(string? layout, string? options, params string[] args)
    {
        var info = new ProcessStartInfo(Script)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        // never inherited, so a layout-less start is the default one whatever the runner's env holds
        info.Environment.Remove("HYDRA_XKB_LAYOUT");
        info.Environment.Remove("HYDRA_XKB_OPTIONS");
        if (layout != null)
            info.Environment["HYDRA_XKB_LAYOUT"] = layout;
        if (options != null)
            info.Environment["HYDRA_XKB_OPTIONS"] = options;

        using var process = System.Diagnostics.Process.Start(info)
            ?? throw new InvalidOperationException("desktop.sh did not start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        // a desktop takes a while to come up; desktop.sh bounds itself, this guards a hung script
        using var cts = new CancellationTokenSource(Bound * 4);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"desktop.sh {string.Join(' ', args)} did not finish");
        }
        return new ScriptResult(process.ExitCode, await stdout, await stderr);
    }

    private sealed record ScriptResult(int ExitCode, string Stdout, string Stderr);
}
