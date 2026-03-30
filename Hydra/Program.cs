using Cathedral.Extensions;
using Cathedral.Logging;
using Hydra.Platform;
using Hydra.Platform.MacOs;
using Hydra.Screen;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateDefaultBuilder(args).DisableEventLog();

builder.ConfigureServices((_, services) =>
{
    services.AddSereneConsoleLogging();
    services.AddSingleton<IPlatformInput, MacInputHandler>();
    services.AddHostedService<ScreenTransitionService>();
});

var app = builder.Build();
app.Run();
