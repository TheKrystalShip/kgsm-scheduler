using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheKrystalShip.KGSM.Extensions;
using TheKrystalShip.Kgsm.Scheduler;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Lifecycle;

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

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;

        var settings = builder.Configuration.GetSection(SchedulerSettings.Section).Get<SchedulerSettings>()
            ?? new SchedulerSettings();
        var options = SchedulerOptions.FromSettings(settings);

        builder.Services.AddSingleton<IOptions<SchedulerOptions>>(Options.Create(options));

        builder.Logging.ClearProviders();
        builder.Logging.AddSystemdConsole();

        builder.Services.AddSingleton<ScheduleRegistry>();
        builder.Services.AddSingleton(sp => new PendingAnnouncementStore(
            options.StateDirectory,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<PendingAnnouncementStore>()));

        // The scheduler consumes no events — it reads config from the filesystem
        // (IInstanceService shells out to kgsm) and dispatches through the watchdog client.
        // The registered journal reader is simply never initialized.
        builder.Services.AddKgsmServices(options.KgsmPath);
        builder.Services.AddKgsmWatchdogClient(options.WatchdogSocketPath);

        // This daemon's own event journal. It records nothing about game servers — the watchdog owns
        // that — only what this leaf did and whether it can still do it. ⚠ That matters more here than
        // anywhere else in the ecosystem: everything this daemon does is something that was supposed to
        // happen, so a broken scheduler produces no event, no error and no absence anybody notices.
        builder.Services.AddKgsmJournal("kgsm-scheduler", typeof(Program).Assembly);

        builder.Services.AddSingleton(sp => new LeafLifecycle(
            sp.GetRequiredService<IEventJournalWriter>(),
            sp.GetRequiredService<ILogger<LeafLifecycle>>(),
            clock: null,
            startedAt: () => startedAt));

        // Every task this daemon can run, and the pieces the window run is assembled from. A task is
        // stateless, so one instance of each serves the whole host.
        builder.Services.AddSingleton<IMaintenanceTask, BackupTask>();
        builder.Services.AddSingleton<IMaintenanceTask, RestartTask>();
        builder.Services.AddSingleton<MaintenanceTaskCatalog>();
        builder.Services.AddSingleton<WindowAnnouncer>();
        builder.Services.AddSingleton<MaintenanceRunner>();

        builder.Services.AddHostedService<SchedulerEngine>();
        builder.Services.AddHostedService<UpdateCheckSweep>();
        builder.Services.AddHostedService<StatusSocketServer>();
        builder.Services.AddHostedService<ControlSocketServer>();

        var host = builder.Build();

        // The last thing this daemon says. A consumer reading it knows the scheduler went away because
        // somebody stopped it, rather than because it died holding schedules nobody will now run.
        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() =>
            host.Services.GetRequiredService<LeafLifecycle>().MarkStopping(LeafStopReason.Signal));

        if (!File.Exists(options.KgsmPath))
        {
            Console.Error.WriteLine($"[FATAL] kgsm not found at '{options.KgsmPath}'. Set Scheduler__KgsmPath.");
            return 1;
        }

        await host.RunAsync();
        return 0;
    }
}
