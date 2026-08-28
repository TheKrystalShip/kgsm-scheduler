using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Scheduling;
using TheKrystalShip.KGSM.Lifecycle;

namespace TheKrystalShip.Kgsm.Scheduler;

/// <summary>
/// The wall clock. Every poll it re-reads each instance's maintenance windows, holds each one's
/// standing appointment, announces the ones approaching, and opens the ones that have come due.
/// </summary>
internal sealed class SchedulerEngine(
    IInstanceService instances,
    IWatchdogClient watchdog,
    LeafLifecycle lifecycle,
    IOptions<SchedulerOptions> options,
    ScheduleRegistry registry,
    MaintenanceTaskCatalog catalog,
    MaintenanceRunner runner,
    WindowAnnouncer announcer,
    ILogger<SchedulerEngine> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Scheduler engine started (poll={Poll}s, grace={Grace}min)",
            options.Value.PollIntervalSeconds, options.Value.GraceWindowMinutes);

        if (!options.Value.AllowDisruptiveTasks)
        {
            // Said once, plainly. A host that silently declines every restart looks exactly like one
            // whose windows are misconfigured, and somebody would eventually debug the wrong thing.
            logger.LogInformation(
                "Disruptive maintenance is not permitted on this host — restarts are recorded as "
                + "skipped and the windows carrying them are not announced");
        }

        // The loop is running, so this daemon is doing the thing it exists to do. Whether it can
        // reach what it dispatches through is a separate question, reported per tick below.
        lifecycle.MarkReady(
            $"polling every {options.Value.PollIntervalSeconds}s "
            + $"(grace {options.Value.GraceWindowMinutes}min)");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds));
        do
        {
            try { await TickAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Scheduler tick error"); }

            await ReportWatchdogAsync(ct).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Reports whether the watchdog is reachable, on every poll rather than when a window is due.
    /// </summary>
    /// <remarks>
    /// By the time a window comes due somebody is already waiting for a restart that will not
    /// happen. Probing on the tick is what turns the most silent failure in this ecosystem into one
    /// that announces itself. The probe is a request on a unix socket, which is why it is affordable
    /// at this cadence.
    /// </remarks>
    private async Task ReportWatchdogAsync(CancellationToken ct)
    {
        bool reachable;

        try
        {
            reachable = await watchdog.IsReadyAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            reachable = false;
        }

        if (reachable)
        {
            lifecycle.MarkRecovered(SchedulerComponents.Watchdog);
        }
        else
        {
            lifecycle.MarkDegraded(
                SchedulerComponents.Watchdog,
                "the watchdog is not answering; every disruptive maintenance task will fail, and the "
                + "only evidence would otherwise be a server that never went down");
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var all = instances.GetAll();

        if (all is null)
        {
            // Not an empty host. The engine could not be read at all, which is indistinguishable from
            // "nobody has configured a window" everywhere else, including this daemon's own status
            // socket.
            lifecycle.MarkDegraded(
                SchedulerComponents.Kgsm,
                "could not read the instance list from kgsm; this daemon knows of no windows, which "
                + "looks exactly like a host that has none");

            return;
        }

        lifecycle.MarkRecovered(SchedulerComponents.Kgsm);

        var now = DateTime.UtcNow;
        var statuses = new List<SchedulerInstanceStatus>(all.Count);
        var unreadable = new List<string>();

        foreach (var (name, instance) in all)
        {
            var tz = ScheduleClock.ResolveTimezone(instance.Timezone);

            ReadWindow[] reads = [.. MaintenanceWindowParser
                .Parse(instance.MaintenanceWindows)
                .Select(w => WindowPlanner.Read(
                    w, catalog, options.Value.MinimumWindowPeriodMinutes, tz, now))];

            foreach (var read in reads.Where(r => !r.Valid))
            {
                unreadable.Add($"{name}: '{read.Window.Expression}' — {read.Error}");
            }

            // Merge rather than overwrite: a window opened on an earlier tick may still be running
            // and will write its own record into the same state when it finishes.
            var planned = registry.Update(name, s =>
            {
                var windows = new Dictionary<string, WindowState>(StringComparer.Ordinal);

                foreach (var read in reads)
                {
                    var existing = s.Windows.GetValueOrDefault(read.Window.Id);
                    windows[read.Window.Id] = new WindowState(
                        read.Window, tz,
                        WindowPlanner.Plan(existing?.Plan, read, tz, now),
                        existing?.LastRun);
                }

                return s with { Windows = windows };
            });

            foreach (var read in reads)
            {
                var plan = planned.Windows[read.Window.Id].Plan;
                var disruptive = runner.DisruptiveTasks(read, instance);

                await announcer
                    .AnnounceUpcomingAsync(name, instance, read, plan, disruptive, now, ct)
                    .ConfigureAwait(false);

                if (!WindowPlanner.IsDue(plan, now)) continue;

                if (TooOverdue(name, read, plan, now))
                {
                    // Too late to run, so the warning stands against nothing. Whoever was told is
                    // told it is off, for the same reason an abandoned run retracts.
                    await announcer.RetractAsync(name, instance, read.Window.Id, ct).ConfigureAwait(false);
                }
                else
                {
                    runner.Fire(name, instance, read);
                }

                // The occurrence is spent either way, so the target moves on to the next one.
                registry.UpdateWindow(name, read.Window.Id, w => w with
                {
                    Plan = w.Plan with { NextUtc = ScheduleClock.NextFire(read.Window, tz, now) },
                });
            }

            var current = registry.Get(name) ?? planned;

            statuses.Add(new SchedulerInstanceStatus(
                name,
                instance.Timezone,
                [.. reads.Select(read => Describe(read, current.Windows.GetValueOrDefault(read.Window.Id)))],
                current.LastUpdateCheckUtc,
                current.LastUpdateCheckOk,
                current.LastUpdateCheckMessage));
        }

        ReportWindows(unreadable);

        registry.Snapshot = new SchedulerStatusResponse(statuses);
    }

    private static SchedulerWindowStatus Describe(ReadWindow read, WindowState? state) =>
        new(read.Window.Id,
            WindowPlanner.KindToken(read.Window),
            [.. read.Window.Tasks.Select(t => t.ToToken())],
            read.Valid,
            read.Error,
            AsOffset(state?.Plan.NextUtc),
            state?.LastRun);

    private static DateTimeOffset? AsOffset(DateTime? utc) =>
        utc.HasValue ? new DateTimeOffset(utc.Value, TimeSpan.Zero) : null;

    /// <summary>
    /// Says, in the leaf's own health, that this host holds windows it will not fire.
    /// </summary>
    /// <remarks>
    /// A window is reported invalid on the status socket per instance, which is the detail. This is
    /// the fact a surface can see without reading it: something on this host asked for maintenance
    /// that is not going to happen.
    /// </remarks>
    private void ReportWindows(IReadOnlyList<string> unreadable)
    {
        if (unreadable.Count == 0)
        {
            lifecycle.MarkRecovered(SchedulerComponents.Config);
            return;
        }

        lifecycle.MarkDegraded(
            SchedulerComponents.Config,
            "maintenance this host will not run, because the window asking for it could not be read "
            + $"or is not permitted here — {string.Join("; ", unreadable)}");
    }

    /// <summary>
    /// Whether a due fire is too late to run. A host that was asleep or down must not wake up to a
    /// burst of catch-up work, so anything overdue beyond the window's grace is dropped, not
    /// deferred.
    /// </summary>
    private bool TooOverdue(string name, ReadWindow read, WindowPlan plan, DateTime now)
    {
        if (plan.NextUtc is not { } target) return false;

        double grace = WindowPlanner.Grace(
            options.Value.GraceWindowMinutes, read.Period, options.Value.PollIntervalSeconds);
        var overdue = now - target;
        if (overdue.TotalMinutes <= grace) return false;

        logger.LogInformation(
            "{Instance}: skipping missed {Window} (overdue {Min:F0}min > grace {Grace:F0}min)",
            name, read.Window.Id, overdue.TotalMinutes, grace);
        return true;
    }
}
