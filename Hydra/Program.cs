using Common;
using System.Text;
using Cathedral.Extensions;
using Cathedral.Logging;
using Cathedral.Utils;
using Hydra;
using Hydra.Config;
using Hydra.FileTransfer;
using Hydra.Management;
using Hydra.Platform;
using Hydra.Platform.Linux;
using Hydra.Platform.Linux.Wayland;
using Hydra.Platform.MacOs;
using Hydra.Platform.Windows;
using Hydra.Relay;
using Hydra.Screen;
using Hydra.Tui;
using Hydra.Update;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// ensure console can display non-ASCII characters (e.g. '€', 'ø') in debug logs
Console.OutputEncoding = Encoding.UTF8;

// catch unhandled exceptions on any thread before they silently kill the process
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    Console.Error.WriteLine($"[FATAL] Unhandled exception (terminating={e.IsTerminating}): {e.ExceptionObject}");
    // restore system cursors in case we crash while they're blanked
    if (OperatingSystem.IsWindows())
        WindowsCursorSnapshot.RestoreDefaults();
};
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Console.Error.WriteLine($"[FATAL] Unobserved task exception: {e.Exception}");
    e.SetObserved();
};

var commandLine = HydraArgs.SplitCommand(args);
if (HydraArgs.TuiCommand.EqualsIgnoreCase(commandLine.Command))
{
    Environment.ExitCode = HydraTui.Run(commandLine.Arguments);
    return;
}

if (HydraArgs.PairCommand.EqualsIgnoreCase(commandLine.Command))
{
    Environment.ExitCode = await PairCommand.Run(commandLine.Arguments, Console.Out, Console.Error);
    return;
}

HydraArgs hydraArgs;
try { hydraArgs = HydraArgs.Parse(args); }
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 2;
    return;
}
if (hydraArgs.UnknownArgument(HydraArgs.InstallOption, HydraArgs.UninstallOption, HydraArgs.ServiceOption, HydraArgs.SessionOption) is { } unknown)
{
    Console.Error.WriteLine($"Unknown argument: {unknown}");
    Console.Error.WriteLine(HydraArgs.DaemonUsage);
    Environment.ExitCode = 2;
    return;
}

if (hydraArgs.Has(HydraArgs.InstallOption))
{
    if (OperatingSystem.IsWindows()) ServiceCommands.Install(hydraArgs.InstallConfigPath);
    else if (OperatingSystem.IsMacOS()) AgentCommands.Install(hydraArgs.InstallConfigPath);
    return;
}
if (hydraArgs.Has(HydraArgs.UninstallOption))
{
    if (OperatingSystem.IsWindows()) ServiceCommands.Uninstall();
    else if (OperatingSystem.IsMacOS()) AgentCommands.Uninstall();
    return;
}

// raise our own scheduling priority before doing anything else, so that a machine busy with something
// else still delivers input promptly. Runs before the service branch below so the service host, the
// session child and the plain agent all get it.
var priorityResult = ProcessPriority.Raise();

if (OperatingSystem.IsWindows())
{
    if (hydraArgs.Has(HydraArgs.ServiceOption)) { ServiceHost.Run(args); return; }
    if (hydraArgs.Has(HydraArgs.SessionOption))
        RunMode.IsSessionChild = true;
}

HydraConfigFile configFile;
List<HydraConfig> profiles;
string configPath;
string? lastConfigError = null;
while (true)
{
    try
    {
        var recoveryConfigPath = HydraConfigFile.ResolvePath(hydraArgs.ConfigPath);
        if (await RemoteApplyStore.RestoreExpiredBeforeStartup(recoveryConfigPath))
            Console.Error.WriteLine("Remote configuration was not confirmed; restored the last-known-good config.");
        var loaded = HydraConfigFile.LoadAll(hydraArgs.ConfigPath);
        configFile = loaded.File;
        configPath = loaded.Path;
        profiles = configFile.Profiles;
        break;
    }
    catch (Exception ex) when (HydraConfigFile.IsRetryableStartupFailure(ex))
    {
        // don't hard-exit on a missing/invalid config: under launchd/service KeepAlive that turns into a
        // ~5s relaunch storm that spams the redirect logs forever. Stay alive and retry so a corrected
        // config is picked up automatically. Log the message once (and again only if it changes).
        if (ex.Message != lastConfigError)
        {
            Console.Error.WriteLine(ex.Message);
            lastConfigError = ex.Message;
        }
        await Task.Delay(TimeSpan.FromSeconds(30));
    }
}

// acquire process lock if configured — prevents two instances from running with the same config
ProcessLock? processLock = null;
if (configFile.LockFile is { } lockFileSetting)
{
    try
    {
        processLock = ProcessLock.Acquire(HydraConfigFile.Beside(configPath, lockFileSetting));
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
var detector = await CreateDetector(macNetworkState, macShield, services);
HydraConfig? config;
if (configFile.Profile != null)
    config = HydraConfig.Resolve(profiles, new ConditionState([], 1), configFile.Profile);
else if (!HydraConfig.HasConditions(profiles))
    config = profiles[0]; // single unconditional profile — no detection needed
else
{
    var activeSsids = (HydraConfig.HasSsidConditions(profiles) ? await detector.GetActiveSsids() : []) ?? [];
    var screenCount = HydraConfig.HasScreenCountConditions(profiles) ? GetScreenCount() : 1;
    var isPluggedIn = HydraConfig.HasPluggedInConditions(profiles) ? await detector.GetIsPluggedIn() : null;
    var links = HydraConfig.HasLinkConditions(profiles) ? await ReadNetworkLinks(detector, HydraConfig.HasGatewayMacConditions(profiles)) : null;
    config = HydraConfig.Resolve(profiles, new ConditionState(activeSsids, screenCount, isPluggedIn, links));
}

// derive network config blob from embeddedStyx (explicit) or embeddedStyxServer (auto-localhost)
string? embeddedNetworkConfig = null;
if (config?.EmbeddedStyx != null)
    embeddedNetworkConfig = await NetworkConfig.ComputeEmbeddedBlob(EmbeddedRelayTls.WithHttps(config.EmbeddedStyx.Server), config.EmbeddedStyx.Password);
else if (config?.EmbeddedStyxServer != null)
    embeddedNetworkConfig = await NetworkConfig.ComputeEmbeddedBlob($"https://localhost:{config.EmbeddedStyxServer.Port}", config.EmbeddedStyxServer.Password);

var profile = new HydraProfile(configFile, config, embeddedNetworkConfig);
services.AddSingleton<IHydraProfile>(profile);

var managementLogs = new ManagementLogBuffer();
services.AddSingleton(managementLogs);
services.AddSingleton<ILoggerProvider>(managementLogs);

services.AddSereneConsoleLogging(c => c.MinLogLevel = profile.LogLevel);

// the session child is where the input hooks and the relay actually run, so in service mode its log is
// the only one that records a dropped connection or a hook failure. sessionLogFile is usually unset, so
// fall back to a sibling of logFile rather than silently writing no file log at all.
var logFileSetting = RunMode.IsSessionChild
    ? configFile.SessionLogFile ?? (configFile.LogFile is { } mainLog ? Path.ChangeExtension(mainLog, ".session.log") : null)
    : configFile.LogFile;
if (logFileSetting is { } logFile)
{
    var logPath = HydraConfigFile.Beside(configPath, logFile);
    if (configFile.LogTruncate && File.Exists(logPath))
        new FileStream(logPath, FileMode.Truncate).Dispose();
    services.AddSereneFileLogging(logPath, c => configFile.ConfigureFileLog(c, profile.LogLevel));
}

var startupLog = await services.CreateLogger<HydraProfile>();
if (startupLog.IsEnabled(LogLevel.Information))
{
    startupLog.LogInformation("Active profile: {ProfileName}", profile.ProfileName ?? "<none>");
    startupLog.LogInformation("Process priority: {Priority}", priorityResult);
    if (profile.SharingOff) startupLog.LogInformation("Clipboard sync and file transfer: off");
}

foreach (var hotkeyError in profile.Hotkeys.Errors)
    startupLog.LogWarning("Hotkey config: {Error}", hotkeyError);
if (startupLog.IsEnabled(LogLevel.Information))
    foreach (var disabled in profile.Hotkeys.Disabled)
        startupLog.LogInformation("Hotkey config: {Action} is disabled", disabled);

if (config?.EmbeddedStyxServer != null && startupLog.IsEnabled(LogLevel.Information))
{
    startupLog.LogInformation("Embedded Styx relay on port {Port}", config.EmbeddedStyxServer.Port);
    startupLog.LogInformation("Remote hosts can connect with: embeddedStyx: {{\"server\": \"https://<your-ip>:{Port}\", \"password\": \"<password>\"}}", config.EmbeddedStyxServer.Port);
}

// Recovery must start before platform/input services: a candidate can block while waiting for permissions
// or fail before the relay is ready, but its rollback deadline must still keep running.
var runtimeInfo = new HydraRuntimeInfo(configPath, DateTimeOffset.UtcNow);
services.AddSingleton(runtimeInfo);
services.AddSingleton<TransactionalConfigStore>();
services.AddSingleton<HydraLifetimeController>();
services.AddSingleton<IHydraLifetimeController>(sp => sp.GetRequiredService<HydraLifetimeController>());
services.AddHostedSingleton<RemoteApplyStore>();

// shared services always registered
services.AddSingleton(profiles);
services.AddSingleton<ICmdRunner, CmdRunner>();
services.AddSingleton<INetworkDetector>(_ => detector);
services.AddSingleton<IWorldState, WorldState>();
services.AddHostedSingleton<DormancyState>();
services.AddSingleton<IDormancyState>(sp => sp.GetRequiredService<DormancyState>());
services.AddLazyResolvers(); // enables Lazy<T> injection — used to break circular deps (e.g. ActivityTracker ↔ IRelaySender)

// shield always runs on macOS — handles cursor shielding + network state detection
if (OperatingSystem.IsMacOS() && macShield != null && macNetworkState != null)
{
    macShield.DebugShield = profile.DebugShield;
    services.AddSingleton(macNetworkState);
    services.AddHostedSingleton(macShield);
}

// network watcher always runs — logs state on startup, triggers restarts on change
services.AddHostedSingleton(sp => new NetworkWatcher(
    sp.GetRequiredService<INetworkDetector>(),
    GetScreenCount,
    profiles,
    config,
    configFile.Profile,
    sp.GetRequiredService<IDormancyState>(),
    sp.GetRequiredService<ILogger<NetworkWatcher>>()));

if (config != null)
{
    // Wayland: input, injection, monitors and the clipboard through the desktop portals, on one session for the run
    var wayland = WaylandDesktop.IsSession;
    var hasXDisplay = Environment.GetEnvironmentVariable("DISPLAY") != null;

    // console mode: no X display available — use evdev input and null screen detector
    var linuxConsoleMode = OperatingSystem.IsLinux() && !wayland && !hasXDisplay;

    // disposed with the host, after everything that uses it
    if (wayland)
        services.AddSingleton<IWaylandSession, WaylandSession>();

    // screen detector must be registered before any service that awaits IScreenDetector.Get() at startup
    if (OperatingSystem.IsMacOS())
        services.AddHostedService<IScreenDetector, MacScreenDetector>();
    else if (OperatingSystem.IsWindows())
        services.AddHostedService<IScreenDetector, WindowsScreenDetector>();
    else if (linuxConsoleMode)
        services.AddHostedService<IScreenDetector, NullScreenDetector>();
    else if (wayland)
        services.AddHostedService<IScreenDetector, WaylandScreenDetector>();
    else if (OperatingSystem.IsLinux())
        services.AddHostedService<IScreenDetector, XorgScreenDetector>();
    else
        throw new PlatformNotSupportedException($"Unsupported OS: {Environment.OSVersion}");

    if (profile.Mode == Mode.Master)
    {
        if (OperatingSystem.IsMacOS())
        {
            services.AddSingleton<IPlatformInput, MacInputHandler>();
            services.AddSingleton<IWindowFocus, MacWindowFocus>();
        }
        else if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IPlatformInput, WindowsInputHandler>();
            // the input handler, so a window is focused behind the shield's teardown on its own thread
            services.AddSingleton(sp => (IWindowFocus)sp.GetRequiredService<IPlatformInput>());
        }
        else if (linuxConsoleMode)
        {
            if (!profile.RemoteOnly)
            {
                Console.Error.WriteLine("No display server available (DISPLAY not set). Set remoteOnly: true in hydra.conf for console operation.");
                return;
            }
            services.AddSingleton<IPlatformInput, EvdevInputHandler>();
        }
        else if (wayland)
        {
            if (profile.RemoteOnly)
            {
                Console.Error.WriteLine("remoteOnly is not supported on a Wayland master: the desktop portal captures input only when the pointer crosses a screen edge. Remove remoteOnly from hydra.conf, or run Hydra in an X11 session.");
                return;
            }
            services.AddSingleton<IPlatformInput>(sp => new WaylandInputHandler(sp.GetRequiredService<IWaylandSession>(), sp.GetRequiredService<IHydraProfile>(),
                sp.GetRequiredService<ILogger<WaylandInputHandler>>(), WaylandDesktop.IsHyprland, sp.GetService<ICaptureClipboard>(),
                // xdg-desktop-portal-hyprland binds no shortcut by itself, so the hotkeys at home are read from the keyboards
                // unless readKeyboardsForHotkeys is off
                WaylandDesktop.IsHyprland && profile.ReadKeyboardsForHotkeys ? new EvdevKeySource(sp.GetRequiredService<ILogger<EvdevInputHandler>>()) : null,
                WaylandDesktop.IsHyprland && !profile.ReadKeyboardsForHotkeys));
            services.AddSingleton<IWindowFocus, NullWindowFocus>();
        }
        else if (OperatingSystem.IsLinux())
        {
            services.AddSingleton<IPlatformInput, XorgInputHandler>();
            services.AddSingleton<IWindowFocus, XorgWindowFocus>();
        }
        else
            throw new PlatformNotSupportedException($"Unsupported OS: {Environment.OSVersion}");

        services.AddHostedService<ICursorHider, CursorHiderService>();
        services.AddHostedSingleton<InputRouter>();
    }
    else if (profile.Mode == Mode.Slave)
    {
        if (OperatingSystem.IsMacOS())
        {
            services.AddCoalescedOutput<MacOutputHandler>();
            services.AddSingleton<IWindowFocus, MacWindowFocus>();
        }
        else if (OperatingSystem.IsWindows())
        {
            // guarded again: the platform analyzer does not follow the branch into a lambda
            services.AddCoalescedOutput<WindowsOutputHandler>(handler => { if (OperatingSystem.IsWindows()) handler.Initialize(); });
            // the output handler, so a window is focused on the input desktop's thread
            services.AddSingleton<IWindowFocus>(sp => OperatingSystem.IsWindows() ? sp.GetRequiredService<WindowsOutputHandler>() : new NullWindowFocus());
        }
        else if (wayland)
        {
            services.AddCoalescedOutput<WaylandOutputHandler>();
            services.AddSingleton<IWindowFocus, NullWindowFocus>();
        }
        else if (OperatingSystem.IsLinux())
        {
            services.AddCoalescedOutput<XorgOutputHandler>();
            services.AddSingleton<IWindowFocus, XorgWindowFocus>();
        }
        else
            throw new PlatformNotSupportedException($"Unsupported OS: {Environment.OSVersion}");

        services.AddSingleton<IPlatformInput, SlavePlatformInput>();

        // real event tap for local keyboard/mouse activity tracking (events pass through — nothing is consumed)
        if (OperatingSystem.IsMacOS())
            services.AddSingleton<ILocalEventTap, MacInputHandler>();
        else if (OperatingSystem.IsWindows())
            services.AddSingleton<ILocalEventTap, WindowsInputHandler>();
        else if (!linuxConsoleMode && !wayland)
            services.AddSingleton<ILocalEventTap, XorgInputHandler>();
        else
            services.AddSingleton<ILocalEventTap>(sp => sp.GetRequiredService<IPlatformInput>()); // no-op on console and Wayland

        services.AddHostedService<ICursorHider, CursorHiderService>();
        services.AddHostedService<SlaveLocalInputWatcher>();

        // forwarder buffers log entries; SlaveLogSender drains them to masters
        var forwarder = new SlaveLogForwarder();
        services.AddSingleton(forwarder);
        services.AddSereneCustomLogging(e => forwarder.Forward(e).AsTask(), c => c.MinLogLevel = LogLevel.Debug);
        services.AddHostedService<SlaveLogSender>();

    }

    if (OperatingSystem.IsMacOS())
        services.AddHostedService<IScreenSaverSync, MacScreenSaverSync>();
    else if (OperatingSystem.IsWindows())
        services.AddHostedService<IScreenSaverSync, WindowsScreenSaverSync>();
    else if (linuxConsoleMode || wayland)
        services.AddSingleton<IScreenSaverSync, NullScreenSaverSync>();
    else if (OperatingSystem.IsLinux())
        services.AddHostedService<IScreenSaverSync, XorgScreenSaverSync>();
    else
        services.AddSingleton<IScreenSaverSync, NullScreenSaverSync>();

    // clipboardSync Off never reads or writes the clipboard
    if (profile.SharingOff)
        services.AddSingleton<IClipboardSync, NullClipboardSync>();
    else if (OperatingSystem.IsMacOS())
        services.AddSingleton<IClipboardSync, MacClipboardSync>();
    else if (OperatingSystem.IsWindows())
        services.AddSingleton<IClipboardSync, WindowsClipboardSync>();
    else if (wayland)
    {
        // wl-clipboard when the portal has no clipboard; the master's input handler claims it for each visit
        services.AddSingleton<PortalClipboardSync>();
        services.AddSingleton<IClipboardSync>(sp => sp.GetRequiredService<PortalClipboardSync>());
        services.AddSingleton<ICaptureClipboard>(sp => sp.GetRequiredService<PortalClipboardSync>());
    }
    else if (OperatingSystem.IsLinux() && hasXDisplay)
        services.AddSingleton<IClipboardSync, XorgClipboardSync>();
    else
        services.AddSingleton<IClipboardSync, NullClipboardSync>();

    if (!RunMode.IsSessionChild)
        services.AddHostedService<SelfUpdater>();

    // file selection detector: reads selected files from Finder/Explorer for copy hotkey
    if (OperatingSystem.IsMacOS())
        services.AddSingleton<IFileSelectionDetector, MacFileSelectionDetector>();
    else if (OperatingSystem.IsWindows())
        services.AddSingleton<IFileSelectionDetector, WindowsFileSelectionDetector>();
    else
        services.AddSingleton<IFileSelectionDetector, NullFileSelectionDetector>();

    // file transfer: dialog and drop target resolver depend on platform; service is shared master/slave
    if (OperatingSystem.IsMacOS())
    {
        // macShield implements IFileTransferDialog and IOsdNotification (already registered as singleton above)
        services.AddSingleton<IFileTransferDialog>(sp => sp.GetRequiredService<MacShieldProcess>());
        services.AddSingleton<IOsdNotification>(sp => sp.GetRequiredService<MacShieldProcess>());
        services.AddSingleton<IDropTargetResolver, MacDropTargetResolver>();
    }
    else if (OperatingSystem.IsWindows())
    {
        services.AddSingleton<IFileTransferDialog, WindowsProgressDialog>();
        services.AddSingleton<IOsdNotification, WindowsOsdNotification>();
        services.AddSingleton<IDropTargetResolver, WindowsDropTargetResolver>();
    }
    else
    {
        services.AddSingleton<IFileTransferDialog, NullFileTransferDialog>();
        services.AddSingleton<IOsdNotification, NullOsdNotification>();
        services.AddSingleton<IDropTargetResolver, NullDropTargetResolver>();
    }
    services.AddSingleton<FileTransferService>();

    // embedded Styx must be registered before the relay connection so it starts first
    if (config.EmbeddedStyxServer != null)
    {
        services.AddSingleton(config.EmbeddedStyxServer);
        services.AddHostedSingleton<EmbeddedStyxServer>();
        // the client dials it only when no embeddedStyx names another relay (see embeddedNetworkConfig)
        if (config.EmbeddedStyx == null)
            services.AddSingleton<IRelayReady>(sp => sp.GetRequiredService<EmbeddedStyxServer>());
    }

    if (profile.Mode == Mode.Slave)
        services.AddHostedService<IRelaySender, SlaveRelayConnection>();
    else
        services.AddHostedService<IRelaySender, MasterRelayConnection>();

    if (profile.AllowSystemSleep)
    {
        services.AddSingleton<SystemSleepCoordinator>();
        if (OperatingSystem.IsMacOS())
            services.AddHostedService<MacSystemSleepMonitor>();
        else if (OperatingSystem.IsWindows())
            services.AddHostedService<WindowsSystemSleepMonitor>();
        else if (OperatingSystem.IsLinux())
            services.AddHostedService<LinuxSystemSleepMonitor>();
    }

    services.AddHostedSingleton<RelayLatencyService>();
    services.AddHostedSingleton<RemoteManagementService>();
    services.AddSingleton<IActivityTracker, ActivityTracker>();
}

services.AddSingleton<RemoteManagementStore>();
if (OperatingSystem.IsWindows() && RunMode.IsSessionChild)
    services.AddHostedService<SessionChildLifetime>();

services.AddSingleton<HydraStatusService>();
services.AddHostedService<ManagementServer>();

var app = builder.Build();

if (macShield != null)
{
    var shieldLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Shield");
    macShield.Log = shieldLog;
    if (shieldLog.IsEnabled(LogLevel.Information))
        shieldLog.LogInformation("auth={Auth} ssid={Ssid}",
            MacNetworkState.AuthLabel(macNetworkState!.WifiAuthStatus),
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
        screenDetector.ScreensChanged += _ => watcher.TriggerCheck();
    }
}

app.Run();
processLock?.Dispose();

// creates the platform-specific network detector for use before DI is set up
static async Task<INetworkDetector> CreateDetector(MacNetworkState? macNetworkState, MacShieldProcess? macShield, IServiceCollection logServices)
{
    if (OperatingSystem.IsMacOS()) return new MacNetworkDetector(macNetworkState, macShield);
    var cmdRunner = new CmdRunner(await logServices.CreateLogger<CmdRunner>());
    if (OperatingSystem.IsWindows()) return new WindowsNetworkDetector();
    if (OperatingSystem.IsLinux()) return new LinuxNetworkDetector(cmdRunner, await logServices.CreateLogger<LinuxNetworkDetector>());
    throw new PlatformNotSupportedException($"Unsupported OS: {Environment.OSVersion}");
}

// unknown when the platform could not be asked: the link conditions then match nothing, and NetworkWatcher retries
static async Task<NetworkLinks?> ReadNetworkLinks(INetworkDetector detector, bool gatewayMacs)
{
    try
    {
        return await detector.GetNetworkLinks(gatewayMacs);
    }
    catch (Exception e) when (e is not OperationCanceledException)
    {
        Console.Error.WriteLine($"Network link detection failed: {e.Message}");
        return null;
    }
}

// returns the current number of connected screens; on Wayland the compositor's wl_outputs, counted over a short-lived
// Wayland connection since the profile is picked from this count before DI, with XWayland's count (1 without it) when
// the compositor cannot be asked
static int GetScreenCount()
{
    if (OperatingSystem.IsMacOS()) return MacDisplayHelper.GetAllScreens().Count;
    if (OperatingSystem.IsWindows()) return WindowsDisplayHelper.GetAllScreens().Count;
    if (OperatingSystem.IsLinux())
    {
        if (WaylandDesktop.IsSession && CountWaylandOutputs() is { } outputs) return outputs;
        var display = XlibRuntime.TryOpenDisplay();
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

// the compositor's outputs with a logical size; null when it cannot be reached or reports none
static int? CountWaylandOutputs()
{
    try
    {
        using var client = WaylandClient.Connect(NullLogger.Instance);
        using var outputs = new WaylandOutputs(client, NullLogger.Instance);
        var count = outputs.Screens.Count;
        return count > 0 ? count : null;
    }
    catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException or DllNotFoundException or EntryPointNotFoundException)
    {
        return null;
    }
}
