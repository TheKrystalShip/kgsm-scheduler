using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Extensions;
using TheKrystalShip.Kgsm.Scheduler;

var options = new SchedulerOptions();

if (string.IsNullOrEmpty(options.KgsmPath) || !File.Exists(options.KgsmPath))
{
    Console.Error.WriteLine($"[FATAL] kgsm not found at '{options.KgsmPath}'. Set KGSM_SCHEDULER_KGSM_PATH.");
    return 1;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSystemdConsole();

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ScheduleRegistry>();

// kgsm-lib: instance service (reads schedule config from kgsm) + watchdog client (issues restarts)
builder.Services.AddKgsmServices(options.KgsmPath, options.KgsmSocketPath);
builder.Services.AddKgsmWatchdogClient(options.WatchdogSocketPath);

builder.Services.AddHostedService<SchedulerEngine>();
builder.Services.AddHostedService<StatusSocketServer>();

var host = builder.Build();
await host.RunAsync();
return 0;
