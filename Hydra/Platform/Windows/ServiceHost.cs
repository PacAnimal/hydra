using System.Runtime.Versioning;
using Cathedral.Logging;
using Cathedral.Utils;
using Hydra.Config;
using Hydra.Update;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hydra.Platform.Windows;

[SupportedOSPlatform("windows")]
internal static class ServiceHost
{
    internal static void Run(string[] args)
    {
        HydraConfigFile configFile;
        List<HydraConfig> profiles;
        try
        {
            (configFile, _) = HydraConfigFile.LoadAll(Env.Config);
            profiles = configFile.Profiles;
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return;
        }

        var config = HydraConfig.HasConditions(profiles) ? null : profiles[0];
        var profile = new HydraProfile(configFile, config);

        var builder = Host.CreateApplicationBuilder(args).DisableEventLog();
        var services = builder.Services;

        services.AddWindowsService(options => options.ServiceName = "Hydra");
        services.AddSereneConsoleLogging(c => c.MinLogLevel = profile.LogLevel);
        services.AddSingleton<IHydraProfile>(profile);
        services.AddSingleton<SessionWatchdog>();
        services.AddHostedService(sp => sp.GetRequiredService<SessionWatchdog>());
        services.AddHostedService<SasService>();
        services.AddSingleton<SelfUpdater>();
        services.AddHostedService(sp => sp.GetRequiredService<SelfUpdater>());

        var app = builder.Build();

        // wire updater to stop child before swapping binary
        var watchdog = app.Services.GetRequiredService<SessionWatchdog>();
        var updater = app.Services.GetRequiredService<SelfUpdater>();
        updater.StopChild = () => watchdog.StopChildAsync();

        app.Run();
    }
}
