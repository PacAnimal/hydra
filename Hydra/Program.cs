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

// catch unhandled exceptions on any thread before they silently kill the process
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Console.Error.WriteLine($"[FATAL] Unhandled exception (terminating={e.IsTerminating}): {e.ExceptionObject}");
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Console.Error.WriteLine($"[FATAL] Unobserved task exception: {e.Exception}");
    e.SetObserved();
};

if (OperatingSystem.IsWindows())
{
    if (args.Contains("--install-service")) { ServiceCommands.Install(); return; }
    if (args.Contains("--uninstall-service")) { ServiceCommands.Uninstall(); return; }
    if (args.Contains("--service")) { ServiceHost.Run(args); return; }
    if (args.Contains("--session")) RunMode.IsSessionChild = true;
}

HydraConfigFile configFile;
List<HydraConfig> profiles;
string configPath;
try
{
    (configFile, configPath) = HydraConfigFile.LoadAll(Env.Config);
    profiles = configFile.Profiles;
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
if (configFile.LockFile is { } lockFileSetting)
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
    var needsWifi = HydraConfig.HasSsidConditions(profiles);
    macNetworkState = new MacNetworkState();
    macShield = new MacShieldProcess(macNetworkState, needsWifi);
    await macShield.WaitForInitialState(TimeSpan.FromSeconds(3));
}

var builder = Host.CreateApplicationBuilder(args).DisableEventLog();
var services = builder.Services;

services.AddEnvironmentConfiguration();

// detect current network/screens and resolve which profile to use
var detector = await CreateDetector(macNetworkState, services);
HydraConfig? config;
if (configFile.Profile != null)
    config = HydraConfig.Resolve(profiles, new ConditionState([], 1), configFile.Profile);
else if (!HydraConfig.HasConditions(profiles))
    config = profiles[0]; // single unconditional profile — no detection needed
else
{
    var activeSsids = HydraConfig.HasSsidConditions(profiles) ? await detector.GetActiveSsids() : [];
    var screenCount = HydraConfig.HasScreenCountConditions(profiles) ? GetScreenCount() : 1;
    config = HydraConfig.Resolve(profiles, new ConditionState(activeSsids, screenCount));
}

var profile = new HydraProfile(configFile, config);
services.AddSingleton<IHydraProfile>(profile);

services.AddSereneConsoleLogging(c => c.MinLogLevel = profile.LogLevel);

if (!RunMode.IsSessionChild && configFile.LogFile is { } logFileSetting)
{
    var logPath = Path.IsPathRooted(logFileSetting)
        ? logFileSetting
        : Path.GetFullPath(logFileSetting, Path.GetDirectoryName(configPath)!);
    services.AddSereneFileLogging(logPath, c => c.MinLogLevel = profile.LogLevel);
}

var startupLog = await services.CreateLogger<HydraProfile>();
startupLog.LogInformation("Active profile: {ProfileName}", profile.ProfileName ?? "<none>");

// shared services always registered
services.AddSingleton(profiles);
services.AddSingleton<ICmdRunner, CmdRunner>();
services.AddSingleton<INetworkDetector>(_ => detector);
services.AddSingleton<IWorldState, WorldState>();

// shield always runs on macOS — handles cursor shielding + network state detection
if (OperatingSystem.IsMacOS() && macShield != null && macNetworkState != null)
{
    macShield.DebugShield = profile.DebugShield;
    services.AddSingleton(macNetworkState);
    services.AddSingleton(macShield);
    services.AddHostedService(_ => macShield);
}

// network watcher always runs — logs state on startup, triggers restarts on change
services.AddSingleton(sp => new NetworkWatcher(
    sp.GetRequiredService<INetworkDetector>(),
    GetScreenCount,
    profiles,
    config,
    configFile.Profile,
    sp.GetRequiredService<ILogger<NetworkWatcher>>()));
services.AddHostedService(sp => sp.GetRequiredService<NetworkWatcher>());

if (config != null)
{
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

    if (profile.Mode == Mode.Master)
    {
        if (OperatingSystem.IsMacOS())
            services.AddSingleton<IPlatformInput, MacInputHandler>();
        else if (OperatingSystem.IsWindows())
            services.AddSingleton<IPlatformInput>(sp =>
                new WindowsInputHandler(sp.GetRequiredService<ILogger<WindowsInputHandler>>(), profile.DebugShield));
        else if (linuxConsoleMode)
        {
            if (!profile.RemoteOnly)
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
    else if (profile.Mode == Mode.Slave)
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
        services.AddSereneCustomLogging(e => forwarder.ForwardAsync(e).AsTask(), c => c.MinLogLevel = LogLevel.Debug);
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

    if (OperatingSystem.IsMacOS())
        services.AddSingleton<IClipboardSync, MacClipboardSync>();
    else if (OperatingSystem.IsWindows())
        services.AddSingleton<IClipboardSync, WindowsClipboardSync>();
    else if (OperatingSystem.IsLinux() && !linuxConsoleMode)
        services.AddSingleton<IClipboardSync, XorgClipboardSync>();
    else
        services.AddSingleton<IClipboardSync, NullClipboardSync>();
    // use a shared, user-accessible path when running as service child (process token is SYSTEM,
    // so Path.GetTempPath() resolves to SYSTEM's temp which Explorer can't access for paste)
    var tempBasePath = OperatingSystem.IsWindows() && RunMode.IsSessionChild
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Hydra", "clipboard-files")
        : null;
    services.AddSingleton(sp => new TempFileManager(sp.GetRequiredService<ILogger<TempFileManager>>(), tempBasePath));

    if (!RunMode.IsSessionChild)
        services.AddHostedService<SelfUpdater>();

    if (profile.NetworkConfig != null)
    {
        if (profile.Mode == Mode.Slave)
            services.AddHostedService<IRelaySender, SlaveRelayConnection>();
        else
            services.AddHostedService<IRelaySender, MasterRelayConnection>();
    }
    else
        services.AddSingleton<IRelaySender, NullRelaySender>();
}

if (OperatingSystem.IsWindows() && RunMode.IsSessionChild)
    services.AddHostedService<SessionChildLifetime>();

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
if (HydraConfig.HasScreenCountConditions(profiles))
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
