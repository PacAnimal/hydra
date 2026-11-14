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

    private Desktop(DesktopKind kind, string name, IReadOnlyDictionary<string, string> env)
    {
        Kind = kind;
        Name = name;
        Env = env;
    }

    public DesktopKind Kind { get; }
    public string Name { get; }
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

    public static async Task<Desktop> Start(DesktopKind kind, string name)
    {
        if (kind == DesktopKind.Evdev)
            return new Desktop(kind, name, new Dictionary<string, string>());

        var result = await Run(Arg(kind), name);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"desktop.sh {Arg(kind)} {name} exited {result.ExitCode}: {result.Stderr}");

        var env = new Dictionary<string, string>();
        foreach (var line in result.Stdout.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = line.IndexOf('=');
            if (eq > 0)
                env[line[..eq]] = line[(eq + 1)..];
        }
        return new Desktop(kind, name, env);
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
            await Run("stop", Name);
    }

    private static async Task<ScriptResult> Run(params string[] args)
    {
        var info = new ProcessStartInfo(Script)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);

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
