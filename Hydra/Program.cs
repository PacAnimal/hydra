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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// ensure console can display non-ASCII characters (e.g. '€', 'ø') in debug logs
Console.OutputEncoding = Encoding.UTF8;

HydraConfig config;
try
{
    config = HydraConfig.Load(Env.Config);
}
catch (FileNotFoundException ex)
{
    Console.Error.WriteLine(ex.Message);
    return;
}
var builder = Host.CreateDefaultBuilder(args).DisableEventLog();

builder.ConfigureServices((_, services) =>
{
    services.AddEnvironmentConfiguration();
    services.AddSereneConsoleLogging(c => c.MinLogLevel = config.LogLevel);
    services.AddSingleton(config);
    services.AddSingleton<IWorldState, WorldState>();

    if (config.Mode == Mode.Master)
    {
        if (OperatingSystem.IsMacOS())
            services.AddSingleton<IPlatformInput, MacInputHandler>();
        else if (OperatingSystem.IsWindows())
            services.AddSingleton<IPlatformInput, WindowsInputHandler>();
        else if (OperatingSystem.IsLinux())
            services.AddSingleton<IPlatformInput, XorgInputHandler>();
        else
            throw new PlatformNotSupportedException($"Unsupported OS: {Environment.OSVersion}");

        services.AddHostedService<ScreenTransitionService>();
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

        // forwarder buffers log entries; SlaveLogSender drains them to masters
        var forwarder = new SlaveLogForwarder();
        services.AddSingleton(forwarder);
        services.AddSereneCustomLogging(e => forwarder.ForwardAsync(e).AsTask(), c => c.MinLogLevel = config.LogLevel);
        services.AddHostedService<SlaveLogSender>();
    }

    if (OperatingSystem.IsMacOS())
        services.AddHostedService<IScreenDetector, MacScreenDetector>();
    else if (OperatingSystem.IsWindows())
        services.AddHostedService<IScreenDetector, WindowsScreenDetector>();
    else if (OperatingSystem.IsLinux())
        services.AddHostedService<IScreenDetector, XorgScreenDetector>();
    else
        throw new PlatformNotSupportedException($"Unsupported OS: {Environment.OSVersion}");

    if (config.NetworkConfig != null)
    {
        if (config.Mode == Mode.Slave)
            services.AddHostedService<IRelaySender, SlaveRelayConnection>();
        else
            services.AddHostedService<IRelaySender, MasterRelayConnection>();
    }
    else
        services.AddSingleton<IRelaySender, NullRelaySender>();
});

var app = builder.Build();

app.Run();
