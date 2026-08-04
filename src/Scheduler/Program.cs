using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheKrystalShip.KGSM.Extensions;
using TheKrystalShip.Kgsm.Scheduler;

namespace TheKrystalShip.Kgsm.Scheduler;

internal sealed class Program
{
    static async Task<int> Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Configuration
            .AddJsonFile("kgsm-scheduler.settings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables();

        builder.Services.AddSingleton<IOptions<SchedulerOptions>>(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            return Options.Create(new SchedulerOptions
            {
                KgsmPath = cfg["KGSM_SCHEDULER_KGSM_PATH"] ?? "/usr/bin/kgsm",
                WatchdogSocketPath = cfg["KGSM_SCHEDULER_WATCHDOG_SOCKET"] ?? "/run/kgsm-watchdog/control.sock",
                StatusSocketPath = cfg["KGSM_SCHEDULER_STATUS_SOCKET"] ?? "/run/kgsm-scheduler/status.sock",
                PollIntervalSeconds = int.TryParse(cfg["KGSM_SCHEDULER_POLL_INTERVAL"], out var p)
                    ? Math.Max(p, SchedulerOptions.MinPollIntervalSeconds)
                    : 60,
                GraceWindowMinutes = int.TryParse(cfg["KGSM_SCHEDULER_GRACE_WINDOW_MINUTES"], out var g)
                    ? Math.Max(g, 0)
                    : 10,
            });
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddSystemdConsole();

        builder.Services.AddSingleton<ScheduleRegistry>();

        var options = builder.Configuration;
        // The scheduler consumes no events — it reads config from the filesystem
        // (IInstanceService shells out to kgsm) and dispatches through the watchdog client.
        // The registered journal reader is simply never initialized.
        builder.Services.AddKgsmServices(
            options["KGSM_SCHEDULER_KGSM_PATH"] ?? "/usr/bin/kgsm");
        builder.Services.AddKgsmWatchdogClient(
            options["KGSM_SCHEDULER_WATCHDOG_SOCKET"] ?? "/run/kgsm-watchdog/control.sock");

        builder.Services.AddHostedService<SchedulerEngine>();
        builder.Services.AddHostedService<StatusSocketServer>();

        var host = builder.Build();

        var schedulerOptions = host.Services.GetRequiredService<IOptions<SchedulerOptions>>().Value;
        if (string.IsNullOrEmpty(schedulerOptions.KgsmPath) || !File.Exists(schedulerOptions.KgsmPath))
        {
            Console.Error.WriteLine($"[FATAL] kgsm not found at '{schedulerOptions.KgsmPath}'. Set KGSM_SCHEDULER_KGSM_PATH.");
            return 1;
        }

        await host.RunAsync();
        return 0;
    }
}
