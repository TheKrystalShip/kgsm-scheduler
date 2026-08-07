using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    string? LastRunMessage,
    DateTimeOffset? LastBackupUtc = null,
    bool? LastBackupOk = null,
    string? LastBackupMessage = null,
    string? BackupSchedule = null,
    string? BackupTime = null,
    string? BackupDay = null,
    DateTimeOffset? NextBackupUtc = null
);

internal sealed record SchedulerStatusResponse(
    IReadOnlyList<SchedulerInstanceStatus> Instances
);

/// <summary>
/// A standing schedule: the configuration it was derived from, and the instant it next fires.
/// The target is held across ticks rather than recomputed from the present each time, because
/// "next fire after now" is always in the future — comparing it to now could never say "due".
/// The signature is what makes an edited schedule take effect: when the cadence, time, day or
/// timezone changes the stored target is discarded and a new one computed.
/// </summary>
internal sealed record SchedulePlan(string Signature, DateTime? NextUtc);

/// <summary>Per-instance state the engine carries between ticks.</summary>
internal sealed record ScheduleState(
    DateTimeOffset? LastRunUtc = null,
    bool? LastRunOk = null,
    string? LastRunMessage = null,
    DateTimeOffset? LastBackupUtc = null,
    bool? LastBackupOk = null,
    string? LastBackupMessage = null,
    SchedulePlan? Restart = null,
    SchedulePlan? Backup = null);

internal sealed class SchedulerEngine(
    IInstanceService instances,
    IWatchdogClient watchdog,
    IOptions<SchedulerOptions> options,
    ScheduleRegistry registry,
    ILogger<SchedulerEngine> logger) : BackgroundService
{
    /// <summary>
    /// Instances with an operation in flight. One operation per instance at a time: a backup can
    /// outlive several ticks on a large game, and a restart must not land in the middle of one.
    /// A fire that arrives while an instance is busy is skipped and recorded, never queued.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _busy = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Scheduler engine started (poll={Poll}s, grace={Grace}min)",
            options.Value.PollIntervalSeconds, options.Value.GraceWindowMinutes);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds));
        do
        {
            try { await TickAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Scheduler tick error"); }
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    private Task TickAsync(CancellationToken ct)
    {
        var all = instances.GetAll();
        if (all is null) return Task.CompletedTask;

        var now = DateTime.UtcNow;
        var statuses = new List<SchedulerInstanceStatus>(all.Count);

        foreach (var (name, instance) in all)
        {
            var tz = ScheduleClock.ResolveTimezone(instance.Timezone);
            var state = registry.Get(name) ?? new ScheduleState();

            var restart = Plan(state.Restart,
                instance.ScheduledRestart, instance.RestartTime, instance.RestartDay, tz, now);
            var backup = Plan(state.Backup,
                instance.BackupSchedule, instance.BackupTime, instance.BackupDay, tz, now);

            if (IsDue(restart, now))
            {
                if (!TooOverdue(name, "restart", restart, now))
                    Begin(name, "restart", ct2 => FireRestartAsync(name, ct2));

                restart = restart with
                {
                    NextUtc = ScheduleClock.ComputeNextFire(
                        instance.ScheduledRestart, instance.RestartTime, instance.RestartDay, tz, now),
                };
            }

            if (IsDue(backup, now))
            {
                if (!TooOverdue(name, "backup", backup, now))
                {
                    int retention = instance.BackupRetention ?? 5;
                    Begin(name, "backup", ct2 => FireBackupAsync(name, retention, ct2));
                }

                backup = backup with
                {
                    NextUtc = ScheduleClock.ComputeNextFire(
                        instance.BackupSchedule, instance.BackupTime, instance.BackupDay, tz, now),
                };
            }

            // Merge rather than overwrite: an operation started on an earlier tick may still be
            // running and will write its own outcome into the same record when it finishes.
            var current = registry.Update(name, s => s with { Restart = restart, Backup = backup });

            statuses.Add(new SchedulerInstanceStatus(
                name,
                ScheduleClock.IsActive(instance.ScheduledRestart) ? instance.ScheduledRestart : "off",
                instance.RestartTime, instance.RestartDay, instance.Timezone,
                AsOffset(restart.NextUtc),
                current.LastRunUtc, current.LastRunOk, current.LastRunMessage,
                current.LastBackupUtc, current.LastBackupOk, current.LastBackupMessage,
                ScheduleClock.IsActive(instance.BackupSchedule) ? instance.BackupSchedule : "off",
                instance.BackupTime, instance.BackupDay,
                AsOffset(backup.NextUtc)));
        }

        registry.Snapshot = new SchedulerStatusResponse(statuses);
        return Task.CompletedTask;
    }

    private static DateTimeOffset? AsOffset(DateTime? utc) =>
        utc.HasValue ? new DateTimeOffset(utc.Value, TimeSpan.Zero) : null;

    /// <summary>
    /// Returns the plan to use this tick: the standing one while its configuration is unchanged,
    /// otherwise a fresh target computed from now.
    /// </summary>
    internal static SchedulePlan Plan(
        SchedulePlan? existing, string? cadence, string? time, string? day, TimeZoneInfo tz, DateTime now)
    {
        string signature = string.Join('|', cadence ?? "", time ?? "", day ?? "", tz.Id);
        if (existing is not null && existing.Signature == signature)
            return existing;

        return new SchedulePlan(signature, ScheduleClock.ComputeNextFire(cadence, time, day, tz, now));
    }

    internal static bool IsDue(SchedulePlan plan, DateTime now) =>
        plan.NextUtc is { } target && now >= target;

    /// <summary>
    /// Whether a due fire is too late to run. A host that was asleep or down must not wake up to a
    /// burst of catch-up work, so anything overdue beyond the grace window is dropped, not deferred.
    /// </summary>
    private bool TooOverdue(string name, string what, SchedulePlan plan, DateTime now)
    {
        if (plan.NextUtc is not { } target) return false;

        var overdue = now - target;
        if (overdue.TotalMinutes <= options.Value.GraceWindowMinutes) return false;

        logger.LogInformation(
            "{Instance}: skipping missed scheduled {What} (overdue {Min:F0}min > grace {Grace}min)",
            name, what, overdue.TotalMinutes, options.Value.GraceWindowMinutes);
        return true;
    }

    /// <summary>
    /// Runs a scheduled operation off the tick so a long backup cannot hold up every other
    /// instance's schedule. The instance's own slot is held for the duration.
    /// </summary>
    private void Begin(string name, string what, Func<CancellationToken, Task> operation)
    {
        if (!_busy.TryAdd(name, 0))
        {
            logger.LogInformation(
                "{Instance}: skipping scheduled {What} — an operation is already running", name, what);
            registry.Update(name, s => s with
            {
                LastRunUtc = DateTimeOffset.UtcNow,
                LastRunOk = false,
                LastRunMessage = $"scheduled {what} skipped: an operation was already running",
            });
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await operation(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "{Instance}: scheduled {What} failed", name, what);
                registry.Update(name, s => s with
                {
                    LastRunUtc = DateTimeOffset.UtcNow,
                    LastRunOk = false,
                    LastRunMessage = ex.Message,
                });
            }
            finally
            {
                _busy.TryRemove(name, out _);
            }
        }, CancellationToken.None);
    }

    private async Task FireRestartAsync(string name, CancellationToken ct)
    {
        logger.LogInformation("{Instance}: firing scheduled restart", name);

        var result = await watchdog.RestartAsync(name, "scheduler", ct).ConfigureAwait(false);
        logger.LogInformation("{Instance}: restart {Result} — {Msg}",
            name, result.Ok ? "ok" : "failed", result.Message);

        registry.Update(name, s => s with
        {
            LastRunUtc = DateTimeOffset.UtcNow,
            LastRunOk = result.Ok,
            LastRunMessage = result.Message,
        });
    }

    /// <summary>
    /// Takes a scheduled backup and prunes to the retention count. The instance is left exactly as
    /// it is: kgsm records what state the archive was captured in, so a backup no longer needs a
    /// stopped server — or a restart window to happen in.
    /// </summary>
    private async Task FireBackupAsync(string name, int retention, CancellationToken ct)
    {
        logger.LogInformation("{Instance}: creating scheduled backup", name);

        var result = await Task
            .Run(() => instances.CreateBackup(name, actor: "system:scheduler", origin: "system"), ct)
            .ConfigureAwait(false);

        bool ok = result.ExitCode == 0;
        if (!ok)
        {
            logger.LogWarning("{Instance}: scheduled backup failed: {Err}", name, result.Stderr);
        }
        else if (retention > 0)
        {
            // Only after a backup that actually landed: pruning around a failed one would drop a
            // good archive to make room for something that was never written.
            var prune = await Task
                .Run(() => instances.PruneBackups(name, retention, actor: "system:scheduler", origin: "system"), ct)
                .ConfigureAwait(false);

            if (prune.ExitCode != 0)
                logger.LogWarning("{Instance}: backup prune failed: {Err}", name, prune.Stderr);
        }

        registry.Update(name, s => s with
        {
            LastBackupUtc = DateTimeOffset.UtcNow,
            LastBackupOk = ok,
            LastBackupMessage = ok ? null : result.Stderr,
        });
    }
}
