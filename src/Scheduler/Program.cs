using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheKrystalShip.KGSM.ComponentSurface;
using TheKrystalShip.KGSM.ComponentSurface.Http;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Engine.Contracts.Invocation;
using TheKrystalShip.KGSM.Engine.Reading;
using TheKrystalShip.KGSM.Extensions;
using TheKrystalShip.KGSM.Watchdog.Client;
using TheKrystalShip.Kgsm.Scheduler;
using TheKrystalShip.Kgsm.Scheduler.Json;
using TheKrystalShip.KGSM.Lifecycle;
using TheKrystalShip.Auth.Sessions;

namespace TheKrystalShip.Kgsm.Scheduler;

internal sealed class Program
{
    /// <summary>This component's id — what names its descriptor, its runtime directory and its unit.</summary>
    private const string ComponentId = "scheduler";

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
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
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

        // The people the control routes admit. The node relays a person's access token over the socket and
        // it is checked here against the keys of the provider named by Sessions__Issuer and
        // Sessions__Audience; a route admits a token listing the route's action. With no issuer nothing
        // can be verified, so the gated routes answer 503 rather than admit anyone.
        SessionsSettings sessionSettings = builder.Configuration.GetSection(SessionsSettings.Section)
            .Get<SessionsSettings>() ?? new SessionsSettings();
        bool sessionsConfigured = !string.IsNullOrWhiteSpace(sessionSettings.Issuer);
        if (sessionsConfigured)
            builder.Services.AddSessions();

        builder.Services.AddSingleton(sp => new PendingAnnouncementStore(
            options.StateDirectory,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<PendingAnnouncementStore>()));

        // The scheduler consumes no events. It reads instance config through the engine core, acts on the
        // engine by running the kgsm binary, and dispatches through the watchdog client.
        builder.Services.AddSingleton(_ => new EngineReader(new EngineReaderOptions { BinaryPath = options.KgsmPath }));
        builder.Services.AddSingleton(_ => new EngineCli(new EngineCliOptions { BinaryPath = options.KgsmPath }));
        builder.Services.AddSingleton<IEngine, EngineGateway>();
        builder.Services.AddKgsmWatchdogClient(options.WatchdogSocketPath);

        // This daemon's own event journal. It records nothing about game servers — the watchdog owns
        // that — only what this leaf did and whether it can still do it. That matters more here than
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
        builder.Services.AddSingleton<IMaintenanceTask, UpdateTask>();
        builder.Services.AddSingleton<IMaintenanceTask, RestartTask>();
        builder.Services.AddSingleton<MaintenanceTaskCatalog>();
        builder.Services.AddSingleton<WindowAnnouncer>();
        builder.Services.AddSingleton<MaintenanceRunner>();

        builder.Services.AddHostedService<SchedulerEngine>();
        builder.Services.AddHostedService(sp => new UpdateCheckSweep(
            sp.GetRequiredService<IEngine>(),
            sp.GetRequiredService<IOptions<SchedulerOptions>>(),
            sp.GetRequiredService<ScheduleRegistry>(),
            sp.GetRequiredService<ILogger<UpdateCheckSweep>>()));
        builder.Services.AddSingleton<WindowControl>();

        // Server to client only, and only over a unix socket — no TCP anywhere. Nothing off this host
        // has any business asking a leaf what it is scheduled to do, so the socket's filesystem
        // permissions are the entire access boundary rather than one layer of several.
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // A socket file left behind by a killed process would otherwise make the bind fail and take
            // the daemon down over an artefact of the last run.
            if (File.Exists(options.SocketPath))
                File.Delete(options.SocketPath);

            kestrel.ListenUnixSocket(options.SocketPath);

            // What this daemon answers about ITSELF, on a socket of its own. A component owns its
            // configuration, its unit and its journal wherever it runs and only the transport differs;
            // this is a leaf, so the node's API relays over this socket rather than reading the
            // descriptor for it.
            //
            // Its own socket rather than the one above so the API finds it from this component's id
            // alone — every leaf's unit already provisions /run/kgsm-<id>/ — and so that the file's
            // presence is the whole of what says this leaf answers for itself.
            if (File.Exists(options.SurfaceSocketPath))
                File.Delete(options.SurfaceSocketPath);

            kestrel.ListenUnixSocket(options.SurfaceSocketPath);
        });

        // The descriptor this build generated, the host's deploy floors beneath it, the overrides in
        // force, this unit's journal, and the bounce that makes a change take effect. All of it is the
        // shared component library, which is also what the generator that writes the descriptor lives
        // beside.
        builder.Services.AddComponentSurface(new ComponentSurfaceOptions(
            ComponentSurfacePaths.Descriptor(ComponentId),
            options.ConfigOverridePath,
            ComponentSurfacePaths.Commands(ComponentId)));

        WebApplication host = builder.Build();

        if (sessionsConfigured)
        {
            host.UseAuthentication();
            host.UseAuthorization();
        }
        else
        {
            host.Use(async (context, next) =>
            {
                if (context.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>() is not null)
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsync(
                        "no sign-in provider is configured (Sessions__Issuer), so nobody can be admitted\n");
                    return;
                }

                await next();
            });
        }

        // The last thing this daemon says. A consumer reading it knows the scheduler went away because
        // somebody stopped it, rather than because it died holding schedules nobody will now run.
        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() =>
            host.Services.GetRequiredService<LeafLifecycle>().MarkStopping(LeafStopReason.Signal));

        // A socket only exists once the host is listening, so the mode is set here rather than before
        // Run — which would be an ENOENT on a file that is not there yet. 0660 lets a process in the
        // socket's group ask without the schedule being readable by everyone on the host.
        host.Lifetime.ApplicationStarted.Register(() =>
        {
            foreach (string socket in (string[])[options.SocketPath, options.SurfaceSocketPath])
            {
                try
                {
                    if (OperatingSystem.IsLinux() && File.Exists(socket))
                        File.SetUnixFileMode(socket, SocketMode);
                }
                catch (Exception ex)
                {
                    host.Logger.LogWarning(ex, "could not set mode on {Socket}", socket);
                }
            }
        });

        // Liveness. Deliberately not an alias for /status: this answers "the process is up and
        // serving", and a scheduler that is up while unable to read a single instance's config must
        // still be able to say so on /status rather than failing this and looking dead.
        host.MapGet("/health", () => Results.Text("ok\n"));

        // What is scheduled on this host and what last ran — every instance, every window.
        host.MapGet("/status", (ScheduleRegistry registry) =>
            Results.Json(registry.Snapshot, SchedulerJsonContext.Default.SchedulerStatusResponse));

        // The three things this daemon can be told. Each is a route rather than a verb inside one
        // request body: the method already says whether something is being read or changed, and a
        // second dispatch layer under it would be one more place a verb can be spelled wrong.
        //
        // The node relays the person's access token, and each route admits a token listing
        // scheduler:windows.write.
        host.MapPost("/windows/postpone", (HttpRequest request, WindowControl control, CancellationToken ct) =>
            Told(request, ct, body => control.Postpone(body))).RequireAction(SchedulerActionIds.WindowsWrite);

        host.MapPost("/windows/skip", (HttpRequest request, WindowControl control, CancellationToken ct) =>
            Told(request, ct, body => control.Skip(body))).RequireAction(SchedulerActionIds.WindowsWrite);

        host.MapPost("/windows/run-now", (HttpRequest request, WindowControl control, CancellationToken ct) =>
            Told(request, ct, body => control.RunNow(body))).RequireAction(SchedulerActionIds.WindowsWrite);

        // What this daemon answers about itself, at the routes every component serves them at. No gate:
        // the socket's filesystem permissions are the boundary, and nothing else is on it.
        host.MapGroup("/component").MapComponentSurface();

        if (!File.Exists(options.KgsmPath))
        {
            Console.Error.WriteLine($"[FATAL] kgsm not found at '{options.KgsmPath}'. Set Scheduler__KgsmPath.");
            return 1;
        }

        await host.RunAsync();
        return 0;
    }

    /// <summary>
    /// Permission bits every socket this daemon binds is given: readable and writable by the owner and
    /// by anything in its group, and by nothing else on the host.
    /// </summary>
    private const UnixFileMode SocketMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;

    /// <summary>
    /// Reads one instruction and answers with what came of it.
    /// </summary>
    /// <remarks>
    /// <b>A refusal is an answer, not a failure.</b> An instruction naming a window this host does not
    /// have was read perfectly well and declined, so it comes back as an answer carrying its own reason
    /// — and a non-200 keeps its single meaning of "this daemon could not read what you sent". The
    /// caller is about to tell a person what happened to their evening, and "the scheduler said no" has
    /// to stay distinguishable from "the scheduler could not be reached".
    /// </remarks>
    private static async Task<IResult> Told(
        HttpRequest request, CancellationToken ct, Func<ControlRequest, ControlResponse> verb)
    {
        ControlRequest? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync(
                request.Body, SchedulerJsonContext.Default.ControlRequest, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Refused("malformed request");
        }

        return body is null
            ? Refused("empty request")
            : Results.Json(verb(body), SchedulerJsonContext.Default.ControlResponse);

        static IResult Refused(string why) => Results.Json(
            new ControlResponse(false, why),
            SchedulerJsonContext.Default.ControlResponse,
            statusCode: StatusCodes.Status400BadRequest);
    }
}
