using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Lifecycle;

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
    DateTimeOffset? NextBackupUtc = null,
    DateTimeOffset? LastUpdateCheckUtc = null,
    bool? LastUpdateCheckOk = null,
    string? LastUpdateCheckMessage = null
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
    SchedulePlan? Backup = null,
    DateTimeOffset? LastUpdateCheckUtc = null,
    bool? LastUpdateCheckOk = null,
    string? LastUpdateCheckMessage = null);

internal sealed class SchedulerEngine(
    IInstanceService instances,
    IWatchdogClient watchdog,
    LeafLifecycle lifecycle,
    IOptions<SchedulerOptions> options,
    ScheduleRegistry registry,
    PendingAnnouncementStore pending,
    ILogger<SchedulerEngine> logger) : BackgroundService
{
    /// <summary>
    /// Instances with an operation in flight. One operation per instance at a time: a backup can
    /// outlive several ticks on a large game, and a restart must not land in the middle of one.
    /// A fire that arrives while an instance is busy is skipped and recorded, never queued.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _busy = new(StringComparer.Ordinal);

    /// <summary>Who this daemon acts as in the audit trail. The <c>system:</c> form is what a
    /// consumer reads as an autonomous leaf rather than as a person on the local host.</summary>
    private const string ProvenanceActor = "system:scheduler";

    /// <summary>The surface a human drove. A scheduled restart has none.</summary>
    private const string ProvenanceOrigin = "system";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("Scheduler engine started (poll={Poll}s, grace={Grace}min)",
            options.Value.PollIntervalSeconds, options.Value.GraceWindowMinutes);

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
    /// Reports whether the watchdog is reachable, on every poll rather than when a restart is due.
    /// </summary>
    /// <remarks>
    /// ⚠ By the time a schedule comes due somebody is already waiting for a restart that will not
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
                "the watchdog is not answering; every scheduled restart will fail, and the only "
                + "evidence would otherwise be a server that never went down");
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var all = instances.GetAll();

        if (all is null)
        {
            // Not an empty host. The engine could not be read at all, which is indistinguishable from
            // "nobody has configured a schedule" everywhere else, including this daemon's own status
            // socket.
            lifecycle.MarkDegraded(
                SchedulerComponents.Kgsm,
                "could not read the instance list from kgsm; this daemon knows of no schedules, which "
                + "looks exactly like a host that has none");

            return;
        }

        lifecycle.MarkRecovered(SchedulerComponents.Kgsm);

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

            await AnnounceUpcomingAsync(name, instance, restart, now, ct).ConfigureAwait(false);

            if (IsDue(restart, now))
            {
                if (!TooOverdue(name, "restart", restart, now))
                {
                    var runtime = instance.Runtime;
                    Begin(name, "restart", ct2 => FireRestartAsync(name, instance, runtime, ct2));
                }
                else
                {
                    // Too late to run, so the warning stands against nothing. Whoever was told is
                    // told it is off, for the same reason an abandoned dispatch retracts.
                    await RetractAsync(name, instance, ct).ConfigureAwait(false);
                }

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
                AsOffset(backup.NextUtc),
                current.LastUpdateCheckUtc, current.LastUpdateCheckOk, current.LastUpdateCheckMessage));
        }

        registry.Snapshot = new SchedulerStatusResponse(statuses);
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

    /// <summary>
    /// Fires a due restart, once the instance's state at this instant says the restart applies.
    /// </summary>
    /// <remarks>
    /// The state is re-read here rather than carried from the tick that scheduled the fire: the
    /// operation runs off the tick, and what the instance is doing when the clock comes round is the
    /// only thing that decides whether restarting it is the right act. <see cref="RestartGate"/>
    /// holds what a verdict is made of; every skip is recorded so the reason reaches the status
    /// socket.
    /// </remarks>
    /// <summary>
    /// Tells the people on a server that a restart is coming, at each lead time it declares.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs on the tick rather than off it: an announcement is one console write, and doing it
    /// inline keeps the spoken marks and the persisted record of them in the same sequence.
    /// </para>
    /// <para>
    /// Every reason to say nothing is a normal outcome, not a failure — the game declares no
    /// broadcast command, the instance sets no lead times, nobody is connected, or the restart is
    /// still further off than the largest lead.
    /// </para>
    /// </remarks>
    private async Task AnnounceUpcomingAsync(
        string name, Instance instance, SchedulePlan plan, DateTime now, CancellationToken ct)
    {
        var existing = pending.Get(name);

        if (plan.NextUtc is not DateTime target || !AnnouncementPlan.CanAnnounce(instance))
        {
            // A window standing against a schedule that has been turned off, or a game that can no
            // longer carry the message, is owed its retraction like any other abandonment.
            if (existing is not null)
            {
                await RetractAsync(name, instance, ct).ConfigureAwait(false);
            }

            return;
        }

        var leads = AnnouncementPlan.ParseLeadMinutes(instance.AnnounceLeadMinutes);
        double minutesUntil = (target - now).TotalMinutes;

        if (minutesUntil > leads[0])
        {
            return;
        }

        // A target that moved — a postponement, or an edited schedule — is a different restart from
        // the one anybody was told about. The warnings already given are void, and saying so is what
        // keeps the earlier countdown from ending in an unexplained silence: told "one minute", then
        // nothing, then a fresh countdown an hour later with no word about the first.
        IReadOnlyList<int> announced = [];

        if (existing is not null)
        {
            if (existing.FireAtUtc == new DateTimeOffset(target, TimeSpan.Zero))
            {
                announced = existing.AnnouncedLeads;
            }
            else
            {
                await RetractAsync(name, instance, ct).ConfigureAwait(false);
            }
        }

        int? mark = AnnouncementPlan.NextMark(leads, announced, minutesUntil, out var due);
        if (mark is null)
        {
            return;
        }

        var spent = announced.Concat(due).Distinct().ToArray();

        // Whether anybody is listening is the watchdog's answer and only its. An instance it cannot
        // observe is announced to anyway: "no players detected" and "detection is unavailable" are
        // different facts, and reading the second as the first silences a server full of people.
        if (await NobodyIsConnectedAsync(name, ct).ConfigureAwait(false))
        {
            logger.LogDebug("{Instance}: skipping the {Mark}-minute announcement — nobody is connected", name, mark);
            pending.Set(name, new PendingAnnouncement(new DateTimeOffset(target, TimeSpan.Zero), spent));
            return;
        }

        string message = AnnouncementPlan.Resolve(instance.AnnounceRestartMessage!, instance, mark);
        var result = instances.Announce(name, message, actor: ProvenanceActor, origin: ProvenanceOrigin);

        if (result.ExitCode != 0)
        {
            // The restart is the job; a warning that could not be delivered does not stop it. Never
            // silent, though — an announcement path that fails quietly is indistinguishable from one
            // nobody wired up.
            logger.LogWarning("{Instance}: could not announce the restart: {Error}", name, result.Stderr);
        }
        else
        {
            logger.LogInformation("{Instance}: announced the restart, {Mark} minute(s) out", name, mark);
        }

        // The mark is spent either way. A send that failed will fail again on the next tick, and
        // retrying it would turn one undeliverable warning into one per tick until the restart.
        pending.Set(name, new PendingAnnouncement(new DateTimeOffset(target, TimeSpan.Zero), spent));
    }

    /// <summary>
    /// Tells a server the restart it was warned about is not happening, and forgets the warning.
    /// </summary>
    /// <remarks>
    /// A warning followed by silence is worse than no warning: players log off for a restart that
    /// never comes, and nothing tells them otherwise. Called wherever an announced restart is
    /// abandoned — the gate declining it, a schedule turned off mid-window, a fire too overdue to run.
    /// No-op for an instance that was never told anything.
    /// </remarks>
    private async Task RetractAsync(string name, Instance instance, CancellationToken ct)
    {
        if (pending.Get(name) is null)
        {
            return;
        }

        pending.Clear(name);

        string? template = instance.AnnounceRestartCancelledMessage;
        if (string.IsNullOrWhiteSpace(template) || !BroadcastCommand.IsSupported(instance.BroadcastCommand))
        {
            return;
        }

        if (await NobodyIsConnectedAsync(name, ct).ConfigureAwait(false))
        {
            return;
        }

        string message = AnnouncementPlan.Resolve(template, instance, minutes: null);
        var result = instances.Announce(name, message, actor: ProvenanceActor, origin: ProvenanceOrigin);

        if (result.ExitCode != 0)
        {
            logger.LogWarning("{Instance}: could not announce the cancellation: {Error}", name, result.Stderr);
        }
        else
        {
            logger.LogInformation("{Instance}: announced that the restart is cancelled", name);
        }
    }

    /// <summary>
    /// Whether the watchdog can see this instance's players and reports none connected.
    /// </summary>
    /// <remarks>
    /// ⚠ Returns <see langword="false"/> whenever the answer is not a measured empty roster — an
    /// unreachable daemon, an instance it does not track, or one whose players it cannot observe at
    /// all. Every one of those means "announce anyway": an unobservable server is not an empty one,
    /// and treating it as empty is how a full server gets restarted without warning.
    /// </remarks>
    private async Task<bool> NobodyIsConnectedAsync(string name, CancellationToken ct)
    {
        try
        {
            var presence = await watchdog.GetPlayerPresenceAsync(ct).ConfigureAwait(false);

            return presence is not null
                && presence.TryGetValue(name, out var instancePresence)
                && instancePresence.IsDetected
                && instancePresence.Players.Count == 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task FireRestartAsync(
        string name, Instance instance, InstanceRuntime? runtime, CancellationToken ct)
    {
        var gate = await RestartGate.EvaluateAsync(watchdog, name, runtime, ct).ConfigureAwait(false);
        if (gate.Outcome != RestartGateOutcome.Dispatch)
        {
            logger.LogInformation("{Instance}: {Reason}", name, gate.Message);
            registry.Update(name, s => s with
            {
                LastRunUtc = DateTimeOffset.UtcNow,
                LastRunOk = gate.LastRunOk,
                LastRunMessage = gate.Message,
            });

            // Whoever was told this restart was coming is told it is not. The gate declining is the
            // commonest way an announced restart is abandoned — the server was stopped, or the
            // watchdog gave up on it, during the countdown.
            await RetractAsync(name, instance, ct).ConfigureAwait(false);
            return;
        }

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

        // The restart the warnings were about has happened, so the debt is settled and the next
        // window starts from nothing said.
        pending.Clear(name);
    }

    /// <summary>
    /// Takes a scheduled backup and prunes to the retention count. The instance is left exactly as
    /// it is: kgsm records what state the archive was captured in, so a backup no longer needs a
    /// stopped server — or a restart window to happen in.
    /// </summary>
    /// <remarks>
    /// The backup states that a cadence took it. Nothing downstream could otherwise tell a nightly
    /// archive from one a person asked for, and the engine will not guess: an unstated reason is
    /// recorded as an ad-hoc request, which is what this is not.
    /// <para>
    /// The prune keeps <paramref name="retention"/> <b>prunable</b> backups. Pinned ones are skipped
    /// and do not consume a slot, so an operator protecting an archive never shrinks the window this
    /// schedule maintains.
    /// </para>
    /// </remarks>
    private async Task FireBackupAsync(string name, int retention, CancellationToken ct)
    {
        logger.LogInformation("{Instance}: creating scheduled backup", name);

        var result = await Task
            .Run(() => instances.CreateBackup(name, actor: "system:scheduler", origin: "system",
                reason: BackupReason.Scheduled), ct)
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
