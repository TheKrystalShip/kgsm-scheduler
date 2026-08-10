using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheKrystalShip.KGSM.Core.Interfaces;

namespace TheKrystalShip.Kgsm.Scheduler;

/// <summary>
/// Asks every server, on a cadence, whether a newer game build exists.
///
/// This holds no answer and makes no judgement. It calls <c>check-update --emit</c> and the engine
/// does the rest: kgsm fetches the upstream version, records it beside the instance, and emits
/// <c>instance_update_available</c> for a version it has not announced before. What is kept here is
/// only the operational fact of having asked — when, and whether the ask succeeded — which is what
/// lets a surface say when this host last looked instead of implying it looks continuously.
///
/// Separate from <see cref="SchedulerEngine"/> rather than another branch of its tick, because the
/// two answer to different clocks. A scheduled restart fires at a wall-clock time in the server's own
/// timezone; a sweep runs on an interval and has no meaningful time of day. Folding an hourly
/// interval into a per-minute wall-clock poll would mean carrying a "have I run this hour" flag
/// through a loop shaped for something else.
/// </summary>
internal sealed class UpdateCheckSweep(
    IInstanceService instances,
    IOptions<SchedulerOptions> options,
    ScheduleRegistry registry,
    ILogger<UpdateCheckSweep> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Value.UpdateCheckEnabled)
        {
            // Said once, plainly. Silence here would be indistinguishable from a sweep that runs and
            // never finds anything, and someone would eventually debug the wrong thing.
            logger.LogInformation(
                "Update checks are disabled — nothing on this host will ask upstream for a newer build");
            return;
        }

        var interval = TimeSpan.FromMinutes(options.Value.UpdateCheckIntervalMinutes);
        logger.LogInformation(
            "Update check sweep started (every {Interval}min, {Stagger}s between servers)",
            options.Value.UpdateCheckIntervalMinutes, options.Value.UpdateCheckStaggerSeconds);

        using var timer = new PeriodicTimer(interval);
        do
        {
            try { await SweepAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Update check sweep error"); }
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Walks the roster one server at a time. Serial on purpose: each server asks its own upstream,
    /// so running them together means as many simultaneous steamcmd logins (or registry probes) as
    /// there are servers, all in the same second, against hosts that have every reason to throttle
    /// that. A sweep is not urgent — it is allowed to take minutes.
    /// </summary>
    private async Task SweepAsync(CancellationToken ct)
    {
        var all = instances.GetAll();
        if (all is null || all.Count == 0) return;

        var recorded = RecordedCheckTimes();
        var now = DateTimeOffset.UtcNow;
        var stagger = TimeSpan.FromSeconds(options.Value.UpdateCheckStaggerSeconds);
        bool first = true;

        foreach (var name in all.Keys)
        {
            if (ct.IsCancellationRequested) return;

            if (WasCheckedRecently(recorded.TryGetValue(name, out var last) ? last : null, now,
                    options.Value.UpdateCheckIntervalMinutes))
            {
                logger.LogDebug("{Instance}: skipping update check — checked recently", name);
                continue;
            }

            if (!first && stagger > TimeSpan.Zero)
                await Task.Delay(stagger, ct).ConfigureAwait(false);
            first = false;

            await CheckAsync(name, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// When each server's upstream was last really fetched, according to the engine's own record.
    /// This is a fast status read — it answers off disk and touches no network. An unreadable roster
    /// yields nothing, which makes every server due: asking again costs a request, whereas assuming
    /// freshness that cannot be confirmed would let a stale answer stand indefinitely.
    /// </summary>
    private Dictionary<string, DateTimeOffset> RecordedCheckTimes()
    {
        var times = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        try
        {
            foreach (var (name, reading) in instances.GetAllStatuses(fast: true))
            {
                if (reading.Value?.Version.CheckedAt is { } at)
                    times[name] = at;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read recorded update-check times; checking every server");
        }
        return times;
    }

    /// <summary>
    /// Whether a server's recorded check is recent enough to leave alone this sweep. The cadence
    /// belongs to the interval, not to the daemon's uptime: without this, restarting the scheduler
    /// would re-ask every upstream immediately, so a few deploys in a row become a burst of logins
    /// against Steam for an answer taken minutes ago.
    ///
    /// The window is HALF the interval rather than the whole of it. A sweep staggers, so a server
    /// checked late in the previous sweep is fractionally younger than one interval when the next
    /// sweep starts — measured against the full interval it would skip, then be a full two intervals
    /// stale by the sweep after that.
    /// </summary>
    internal static bool WasCheckedRecently(
        DateTimeOffset? last, DateTimeOffset now, int intervalMinutes)
    {
        if (last is not { } at) return false;

        var window = TimeSpan.FromMinutes(intervalMinutes) / 2;
        var age = now - at;

        // A record stamped in the future is not evidence of anything (a clock moved, a file was
        // copied in) and is treated as no record at all rather than as indefinitely fresh.
        return age >= TimeSpan.Zero && age < window;
    }

    /// <summary>
    /// One server's check. A failure is recorded and the sweep moves on — the next sweep tries again,
    /// so there is nothing to gain from retrying tightly against an upstream that just refused.
    /// </summary>
    private async Task CheckAsync(string name, CancellationToken ct)
    {
        try
        {
            var result = await Task
                .Run(() => instances.CheckUpdate(name, emit: true), ct)
                .ConfigureAwait(false);

            bool ok = result.ExitCode == 0;
            if (!ok)
            {
                logger.LogWarning("{Instance}: update check failed: {Err}",
                    name, string.IsNullOrWhiteSpace(result.Stderr) ? "no detail" : result.Stderr.Trim());
            }

            registry.Update(name, s => s with
            {
                LastUpdateCheckUtc = DateTimeOffset.UtcNow,
                LastUpdateCheckOk = ok,
                LastUpdateCheckMessage = ok ? null : Summarize(result.Stderr),
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Instance}: update check failed", name);
            registry.Update(name, s => s with
            {
                LastUpdateCheckUtc = DateTimeOffset.UtcNow,
                LastUpdateCheckOk = false,
                LastUpdateCheckMessage = ex.Message,
            });
        }
    }

    /// <summary>
    /// The failure as one line for the status snapshot. kgsm writes several lines of context on a
    /// failed check; the snapshot is served as one NDJSON line per connection, so the last line —
    /// the one naming what actually went wrong — is what travels.
    /// </summary>
    internal static string? Summarize(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return null;

        string? last = stderr
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        return string.IsNullOrWhiteSpace(last) ? null : last;
    }
}
