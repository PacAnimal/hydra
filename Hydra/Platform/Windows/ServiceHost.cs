using System.Runtime.Versioning;
using Cathedral.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hydra.Platform.Windows;

[SupportedOSPlatform("windows")]
internal static class ServiceHost
{
    internal static void Run(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args).DisableEventLog();
        builder.Services.AddWindowsService(options => options.ServiceName = "Hydra");
        builder.Services.AddSereneConsoleLogging();
        builder.Services.AddHostedService<SessionWatchdog>();
        builder.Services.AddHostedService<SasService>();
        builder.Build().Run();
    }
}
