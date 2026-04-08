using System.Text;
using Cathedral.Extensions;
using Cathedral.Logging;
using Cathedral.Utils;
using Hydra.Config;
using Hydra.Platform;
using Hydra.Platform.Linux;
using Hydra.Platform.MacOs;
using Hydra.Platform.Windows;
using Hydra.Relay;
using Hydra.Screen;
using Hydra.Update;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// ensure console can display non-ASCII characters (e.g. '€', 'ø') in debug logs
Console.OutputEncoding = Encoding.UTF8;

List<HydraConfig> configs;
string configPath;
try
{
    (configs, configPath) = HydraConfig.LoadAll(Env.Config);
}
catch (FileNotFoundException ex)
{
    Console.Error.WriteLine(ex.Message);
    return;
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine(ex.Message);
    return;
}

// acquire process lock if configured — prevents two instances from running with the same config
ProcessLock? processLock = null;
if (configs[0].LockFile is { } lockFileSetting)
{
    var lockPath = Path.IsPathRooted(lockFileSetting)
        ? lockFileSetting
        : Path.GetFullPath(lockFileSetting, Path.GetDirectoryName(configPath)!);
    try
    {
        processLock = ProcessLock.Acquire(lockPath);
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return;
    }
}

// on macOS, pre-start the shield before DI so network state is available for config resolution.
// the shield (NSApplication) activates WiFi/location on demand when "wifi" is sent via stdin.
MacNetworkState? macNetworkState = null;
MacShieldProcess? macShield = null;
if (OperatingSystem.IsMacOS())
{
    var needsWifi = HydraConfig.HasSsidConditions(configs);
    macNetworkState = new MacNetworkState();
    macShield = new MacShieldProcess(macNetworkState, needsWifi);
    await macShield.WaitForInitialState(TimeSpan.FromSeconds(3));
}

var builder = Host.CreateApplicationBuilder(args).DisableEventLog();
var services = builder.Services;

services.AddEnvironmentConfiguration();
services.AddSereneConsoleLogging(c => c.MinLogLevel = configs[0].LogLevel);

// detect current network/screens and resolve which config to use
var detector = await CreateDetector(macNetworkState, services);
HydraConfig? config;
if (!HydraConfig.HasConditions(configs))
    config = configs[0]; // single unconditional config — no detection needed
else
{
    var activeSsids = HydraConfig.HasSsidConditions(configs) ? await detector.GetActiveSsids() : [];
    var screenCount = HydraConfig.HasScreenCountConditions(configs) ? GetScreenCount() : 1;
    config = HydraConfig.Resolve(configs, new ConditionState(activeSsids, screenCount));
}

// shared services always registered
services.AddSingleton(configs);
services.AddSingleton<ICmdRunner, CmdRunner>();
services.AddSingleton<INetworkDetector>(_ => detector);
services.AddSingleton<IWorldState, WorldState>();

// shield always runs on macOS — handles cursor shielding + network state detection
if (OperatingSystem.IsMacOS() && macShield != null && macNetworkState != null)
{
    if (config != null) macShield.DebugShield = config.DebugShield;
    services.AddSingleton(macNetworkState);
    services.AddSingleton(macShield);
    services.AddHostedService(_ => macShield);
}

// network watcher always runs — logs state on startup, triggers restarts on change
services.AddSingleton(sp => new NetworkWatcher(
    sp.GetRequiredService<INetworkDetector>(),
    GetScreenCount,
    configs,
    config,
    sp.GetRequiredService<ILogger<NetworkWatcher>>()));
services.AddHostedService(sp => sp.GetRequiredService<NetworkWatcher>());

if (config != null)
{
    services.AddSingleton(config);

    // console mode: no X display available — use evdev input and null screen detector
    var linuxConsoleMode = OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("DISPLAY") == null;

    // screen detector must be registered before any service that awaits IScreenDetector.Get() at startup
    if (OperatingSystem.IsMacOS())
        services.AddHostedService<IScreenDetector, MacScreenDetector>();
    else if (OperatingSystem.IsWindows())
        services.AddHostedService<IScreenDetector, WindowsScreenDetector>();
    else if (linuxConsoleMode)
        services.AddHostedService<IScreenDetector, NullScreenDetector>();
    else if (OperatingSystem.IsLinux())
        services.AddHostedService<IScreenDetector, XorgScreenDetector>();
    else
        throw new PlatformNotSupportedException($"Unsupported OS: {Environment.OSVersion}");

    if (config.Mode == Mode.Master)
    {
        if (OperatingSystem.IsMacOS())
            services.AddSingleton<IPlatformInput, MacInputHandler>();
        else if (OperatingSystem.IsWindows())
            services.AddSingleton<IPlatformInput, WindowsInputHandler>();
        else if (linuxConsoleMode)
        {
            if (!config.RemoteOnly)
            {
                Console.Error.WriteLine("No display server available (DISPLAY not set). Set remoteOnly: true in hydra.conf for console operation.");
                return;
            }
            services.AddSingleton<IPlatformInput, EvdevInputHandler>();
        }
        else if (OperatingSystem.IsLinux())
            services.AddSingleton<IPlatformInput, XorgInputHandler>();
        else
            throw new PlatformNotSupportedException($"Unsupported OS: {Environment.OSVersion}");

        services.AddHostedService<InputRouter>();
    }
    else if (config.Mode == Mode.Slave)
    {
        if (OperatingSystem.IsMacOS())
            services.AddSingleton<IPlatformOutput, MacOutputHandler>();
        else if (OperatingSystem.IsWindows())
            services.AddSingleton<IPlatformOutput, WindowsOutputHandler>();
        else if (OperatingSystem.IsLinux())
            services.AddSingleton<IPlatformOutput, XorgOutputHandler>();
        else
            throw new PlatformNotSupportedException($"Unsupported OS: {Environment.OSVersion}");

        services.AddSingleton<ICursorVisibility>(sp => (ICursorVisibility)sp.GetRequiredService<IPlatformOutput>());
        services.AddSingleton<SlaveCursorHider>();

        // forwarder buffers log entries; SlaveLogSender drains them to masters
        var forwarder = new SlaveLogForwarder();
        services.AddSingleton(forwarder);
        services.AddSereneCustomLogging(e => forwarder.ForwardAsync(e).AsTask(), c => c.MinLogLevel = config.LogLevel);
        services.AddHostedService<SlaveLogSender>();

        services.AddHostedService<IScreensaverSuppressor, ScreensaverSuppressor>();
    }

    if (OperatingSystem.IsMacOS())
        services.AddSingleton<IScreenSaverSync, MacScreenSaverSync>();
    else if (OperatingSystem.IsWindows())
        services.AddSingleton<IScreenSaverSync, WindowsScreenSaverSync>();
    else if (linuxConsoleMode)
        services.AddSingleton<IScreenSaverSync, NullScreenSaverSync>();
    else if (OperatingSystem.IsLinux())
        services.AddSingleton<IScreenSaverSync, XorgScreenSaverSync>();
    else
        services.AddSingleton<IScreenSaverSync, NullScreenSaverSync>();

    services.AddHostedService<SelfUpdater>();

    if (config.NetworkConfig != null)
    {
        if (config.Mode == Mode.Slave)
            services.AddHostedService<IRelaySender, SlaveRelayConnection>();
        else
            services.AddHostedService<IRelaySender, MasterRelayConnection>();
    }
    else
        services.AddSingleton<IRelaySender, NullRelaySender>();
}

var app = builder.Build();

if (macShield != null)
{
    var shieldLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Shield");
    macShield.Log = shieldLog;
    shieldLog.LogInformation("auth={Auth} ssid={Ssid}",
        macNetworkState!.WifiAuthStatus switch { 0 => "notDetermined", 1 => "restricted", 2 => "denied", 3 or 4 => "authorized", _ => "none" },
        macNetworkState.Ssid ?? "(none)");

    // wire shield state changes to immediate network re-check
    macShield.OnNetworkStateChanged = () => app.Services.GetRequiredService<NetworkWatcher>().TriggerCheck();
}

// wire screen changes to condition re-check when screenCount conditions are configured
if (HydraConfig.HasScreenCountConditions(configs))
{
    var screenDetector = app.Services.GetService<IScreenDetector>();
    if (screenDetector != null)
    {
        var watcher = app.Services.GetRequiredService<NetworkWatcher>();
        screenDetector.ScreensChanged += _ => { watcher.TriggerCheck(); return Task.CompletedTask; };
    }
}

app.Run();
processLock?.Dispose();

// creates the platform-specific network detector for use before DI is set up
static async Task<INetworkDetector> CreateDetector(MacNetworkState? macNetworkState, IServiceCollection logServices)
{
    if (OperatingSystem.IsMacOS()) return new MacNetworkDetector(macNetworkState);
    var cmdRunner = new CmdRunner(await logServices.CreateLogger<CmdRunner>());
    if (OperatingSystem.IsWindows()) return new WindowsNetworkDetector();
    if (OperatingSystem.IsLinux()) return new LinuxNetworkDetector(cmdRunner, await logServices.CreateLogger<LinuxNetworkDetector>());
    throw new PlatformNotSupportedException($"Unsupported OS: {Environment.OSVersion}");
}

// returns the current number of connected screens
static int GetScreenCount()
{
    if (OperatingSystem.IsMacOS()) return MacDisplayHelper.GetAllScreens().Count;
    if (OperatingSystem.IsWindows()) return WindowsDisplayHelper.GetAllScreens().Count;
    if (OperatingSystem.IsLinux())
    {
        var display = Hydra.Platform.Linux.NativeMethods.XOpenDisplay(null);
        if (display == nint.Zero) return 1;
        try
        {
            var root = Hydra.Platform.Linux.NativeMethods.XDefaultRootWindow(display);
            return XorgDisplayHelper.GetAllScreens(display, root).Count;
        }
        finally
        {
            _ = Hydra.Platform.Linux.NativeMethods.XCloseDisplay(display);
        }
    }
    return 1;
}
