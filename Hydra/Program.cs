using Cathedral.Extensions;
using Cathedral.Logging;
using Hydra.Config;
using Hydra.Platform;
using Hydra.Platform.Linux;
using Hydra.Platform.MacOs;
using Hydra.Platform.Windows;
using Hydra.Screen;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var config = HydraConfig.Load();
var builder = Host.CreateDefaultBuilder(args).DisableEventLog();

builder.ConfigureServices((_, services) =>
{
    services.AddSereneConsoleLogging(c => c.MinLogLevel = config.LogLevel);

    if (OperatingSystem.IsMacOS())
        services.AddSingleton<IPlatformInput, MacInputHandler>();
    else if (OperatingSystem.IsWindows())
        services.AddSingleton<IPlatformInput, WindowsInputHandler>();
    else if (OperatingSystem.IsLinux())
        services.AddSingleton<IPlatformInput, XorgInputHandler>();
    else
        throw new PlatformNotSupportedException($"Unsupported OS: {Environment.OSVersion}");

    services.AddHostedService<ScreenTransitionService>();
});

var app = builder.Build();
app.Run();
