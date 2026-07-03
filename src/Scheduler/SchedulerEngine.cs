using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Core.Interfaces;

namespace TheKrystalShip.Kgsm.Scheduler;

internal sealed record SchedulerInstanceStatus(
    string Name,
    string? ScheduledRestart,
    string? RestartTime,
    string? RestartDay,
    string? Timezone,
    DateTimeOffset? NextFireUtc,
    DateTimeOffset? LastRunUtc,
    bool? LastRunOk,
    string? LastRunMessage
);

internal sealed record SchedulerStatusResponse(
    IReadOnlyList<SchedulerInstanceStatus> Instances
);

/// <summary>Minimal mutable per-instance state the engine needs between ticks.</summary>
internal sealed record ScheduleState(
    DateTimeOffset? LastRunUtc,
    bool? LastRunOk,
    string? LastRunMessage);

internal sealed class SchedulerEngine(
    IInstanceService instances,
    IWatchdogClient watchdog,
    SchedulerOptions options,
    ScheduleRegistry registry,
    ILogger<SchedulerEngine> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Scheduler engine started (poll={Poll}s, grace={Grace}min)",
            options.PollIntervalSeconds, options.GraceWindowMinutes);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.PollIntervalSeconds));
        do
        {
            try { await TickAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Scheduler tick error"); }
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var all = instances.GetAll();
        if (all is null) return;

        var now = DateTime.UtcNow;
        var statuses = new List<SchedulerInstanceStatus>(all.Count);

        foreach (var (name, instance) in all)
        {
            var cadence = instance.ScheduledRestart;
            if (string.IsNullOrEmpty(cadence) || cadence == "off")
            {
                statuses.Add(new SchedulerInstanceStatus(name, "off", null, null, null, null, null, null, null));
                continue;
            }

            var tz = ResolveTimezone(instance.Timezone);
            var nextFire = ComputeNextFire(cadence, instance.RestartTime, instance.RestartDay, tz, now);

            var existing = registry.Get(name);
            DateTimeOffset? lastRunUtc = existing?.LastRunUtc;
            bool? lastRunOk = existing?.LastRunOk;
            string? lastRunMsg = existing?.LastRunMessage;

            if (nextFire.HasValue && nextFire.Value <= now)
            {
                var overdue = now - nextFire.Value;
                if (overdue.TotalMinutes > options.GraceWindowMinutes)
                {
                    logger.LogInformation(
                        "{Instance}: skipping missed {Cadence} restart (overdue {Min:F0}min > grace {Grace}min)",
                        name, cadence, overdue.TotalMinutes, options.GraceWindowMinutes);
                }
                else
                {
                    logger.LogInformation("{Instance}: firing scheduled {Cadence} restart", name, cadence);
                    try
                    {
                        var result = await watchdog.RestartAsync(name, "scheduler", ct).ConfigureAwait(false);
                        lastRunUtc = DateTimeOffset.UtcNow;
                        lastRunOk = result.Ok;
                        lastRunMsg = result.Message;
                        logger.LogInformation("{Instance}: restart {Result} — {Msg}",
                            name, result.Ok ? "ok" : "failed", result.Message);
                    }
                    catch (Exception ex)
                    {
                        lastRunUtc = DateTimeOffset.UtcNow;
                        lastRunOk = false;
                        lastRunMsg = ex.Message;
                        logger.LogWarning(ex, "{Instance}: restart failed (watchdog unreachable?)", name);
                    }
                    // Advance next-fire PAST now so we don't re-fire next tick
                    nextFire = ComputeNextFire(cadence, instance.RestartTime, instance.RestartDay, tz,
                        now + TimeSpan.FromMinutes(1));
                }
            }

            statuses.Add(new SchedulerInstanceStatus(
                name, cadence, instance.RestartTime, instance.RestartDay, instance.Timezone,
                nextFire.HasValue ? new DateTimeOffset(nextFire.Value, TimeSpan.Zero) : null,
                lastRunUtc, lastRunOk, lastRunMsg));

            registry.Set(name, new ScheduleState(lastRunUtc, lastRunOk, lastRunMsg));
        }

        registry.Snapshot = new SchedulerStatusResponse(statuses);
    }

    private static TimeZoneInfo ResolveTimezone(string? iana)
    {
        if (string.IsNullOrWhiteSpace(iana)) return TimeZoneInfo.Local;
        try { return TimeZoneInfo.FindSystemTimeZoneById(iana); }
        catch { return TimeZoneInfo.Local; }
    }

    /// <summary>
    /// Computes the NEXT scheduled fire time (in UTC) at or after <paramref name="after"/>.
    /// Returns null for unknown cadences.
    /// </summary>
    private static DateTime? ComputeNextFire(
        string cadence, string? restartTime, string? restartDay,
        TimeZoneInfo tz, DateTime after)
    {
        if (!TryParseTime(restartTime ?? "04:00", out var hour, out var minute))
            return null;

        return cadence switch
        {
            "daily"  => NextDailyFire(hour, minute, tz, after),
            "weekly" => NextWeeklyFire(hour, minute, restartDay, tz, after),
            "6h"     => NextIntervalFire(6, after),
            _        => null,
        };
    }

    private static DateTime NextDailyFire(int hour, int minute, TimeZoneInfo tz, DateTime after)
    {
        var afterLocal = TimeZoneInfo.ConvertTimeFromUtc(after, tz);
        var todayFire = new DateTime(afterLocal.Year, afterLocal.Month, afterLocal.Day,
            hour, minute, 0, DateTimeKind.Unspecified);
        var fireLocal = todayFire > afterLocal ? todayFire : todayFire.AddDays(1);
        return TimeZoneInfo.ConvertTimeToUtc(fireLocal, tz);
    }

    private static DateTime NextWeeklyFire(int hour, int minute, string? day, TimeZoneInfo tz, DateTime after)
    {
        var target = ParseDayOfWeek(day ?? "sun");
        var afterLocal = TimeZoneInfo.ConvertTimeFromUtc(after, tz);
        var todayFire = new DateTime(afterLocal.Year, afterLocal.Month, afterLocal.Day,
            hour, minute, 0, DateTimeKind.Unspecified);

        int daysUntil = ((int)target - (int)afterLocal.DayOfWeek + 7) % 7;
        if (daysUntil == 0 && todayFire <= afterLocal) daysUntil = 7;
        var fireLocal = todayFire.AddDays(daysUntil);
        return TimeZoneInfo.ConvertTimeToUtc(fireLocal, tz);
    }

    private static DateTime NextIntervalFire(int intervalHours, DateTime after)
    {
        // Next boundary: the first whole N-hour mark after `after` (UTC)
        var total = (long)(after - DateTime.UnixEpoch).TotalHours;
        var nextBoundary = (total / intervalHours + 1) * intervalHours;
        return DateTime.UnixEpoch + TimeSpan.FromHours(nextBoundary);
    }

    private static bool TryParseTime(string hhmm, out int hour, out int minute)
    {
        hour = minute = 0;
        var parts = hhmm.Split(':');
        if (parts.Length != 2) return false;
        return int.TryParse(parts[0], out hour) && int.TryParse(parts[1], out minute)
            && hour is >= 0 and <= 23 && minute is >= 0 and <= 59;
    }

    private static DayOfWeek ParseDayOfWeek(string s) => s.ToLowerInvariant() switch
    {
        "mon" => DayOfWeek.Monday,
        "tue" => DayOfWeek.Tuesday,
        "wed" => DayOfWeek.Wednesday,
        "thu" => DayOfWeek.Thursday,
        "fri" => DayOfWeek.Friday,
        "sat" => DayOfWeek.Saturday,
        _     => DayOfWeek.Sunday,
    };
}
