using Cathedral.Extensions;
using Cathedral.Logging;
using Cathedral.Utils;
using Hydra.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var config = Env.Config;
var builder = Host.CreateDefaultBuilder(args).DisableEventLog();

builder.ConfigureServices((_, services) =>
{
    services.AddSereneConsoleLogging();
});

var app = builder.Build();
var log = app.Services.GetRequiredService<ILogger<Program>>();

log.LogInformation("Hello, World! ({Environment})", config.IsProduction() ? "PRODUCTION" : "DEVELOPMENT");

app.Run();
