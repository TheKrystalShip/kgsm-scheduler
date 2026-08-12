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
        // ContentRootPath is pinned to the binary's own directory rather than left to default to the
        // process working directory. The unit starts the daemon with no WorkingDirectory, so that
        // default is "/", and the builder installs its own appsettings.json providers with
        // reloadOnChange:true — which hangs a RECURSIVE FileSystemWatcher off the content root.
        // Rooted at "/", that watch walks the entire filesystem and takes an inotify watch per
        // directory (~190k here), exhausting the per-user fs.inotify.max_user_watches budget that
        // the game servers on this host draw from; a game that cannot get a watch fails to boot.
        // AppContext.BaseDirectory is the one directory that is correct no matter where the process
        // was started from — the same reason the settings file below is named absolutely.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });

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
        builder.Services.AddHostedService<UpdateCheckSweep>();
        builder.Services.AddHostedService<StatusSocketServer>();
        builder.Services.AddHostedService<ControlSocketServer>();

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
