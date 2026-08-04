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

        // The settings file lives beside the binary, which is not necessarily the working directory
        // the unit starts us in, so it is named absolutely. Environment variables are registered
        // after it and therefore win: configuration resolves by source order, and appending the file
        // to the sources the builder already installed puts it ahead of the builder's own
        // environment provider. Without re-registering, the file would outrank every Scheduler__*
        // and Logging__* variable and an override would read as applied while changing nothing.
        builder.Configuration
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "kgsm-scheduler.settings.json"),
                optional: true, reloadOnChange: false)
            .AddEnvironmentVariables();

        var settings = builder.Configuration.GetSection(SchedulerSettings.Section).Get<SchedulerSettings>()
            ?? new SchedulerSettings();
        var options = SchedulerOptions.FromSettings(settings);

        builder.Services.AddSingleton<IOptions<SchedulerOptions>>(Options.Create(options));

        builder.Logging.ClearProviders();
        builder.Logging.AddSystemdConsole();

        builder.Services.AddSingleton<ScheduleRegistry>();

        // The scheduler consumes no events — it reads config from the filesystem
        // (IInstanceService shells out to kgsm) and dispatches through the watchdog client.
        // The registered journal reader is simply never initialized.
        builder.Services.AddKgsmServices(options.KgsmPath);
        builder.Services.AddKgsmWatchdogClient(options.WatchdogSocketPath);

        builder.Services.AddHostedService<SchedulerEngine>();
        builder.Services.AddHostedService<StatusSocketServer>();

        var host = builder.Build();

        if (!File.Exists(options.KgsmPath))
        {
            Console.Error.WriteLine($"[FATAL] kgsm not found at '{options.KgsmPath}'. Set Scheduler__KgsmPath.");
            return 1;
        }

        await host.RunAsync();
        return 0;
    }
}
