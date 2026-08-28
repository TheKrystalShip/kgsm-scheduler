using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Scheduling;

namespace TheKrystalShip.Kgsm.Scheduler;

/// <summary>
/// Tells the people on a server that maintenance is coming, at each lead time the instance
/// declares — and tells them when it is not.
/// </summary>
/// <remarks>
/// <para>
/// Runs on the tick rather than off it: an announcement is one console write, and doing it inline
/// keeps the spoken marks and the persisted record of them in the same sequence.
/// </para>
/// <para>
/// Every reason to say nothing is a normal outcome, not a failure — the game declares no broadcast
/// command, the instance sets no lead times, the window disturbs nobody, nobody is connected, or
/// the fire is still further off than the largest lead.
/// </para>
/// </remarks>
internal sealed class WindowAnnouncer(
    IInstanceService instances,
    IWatchdogClient watchdog,
    PendingAnnouncementStore pending,
    ILogger<WindowAnnouncer> logger)
{
    /// <summary>
    /// Lead sets already reported as narrowed, so the report is made once per configuration rather
    /// than once per poll.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _reportedDrops = new(StringComparer.Ordinal);

    /// <summary>
    /// Announces one window's approach, if there is anything true to say about it.
    /// </summary>
    /// <param name="name">The instance's id.</param>
    /// <param name="instance">Its configuration.</param>
    /// <param name="read">The window, as this host will act on it.</param>
    /// <param name="plan">Its standing target.</param>
    /// <param name="disruptive">
    /// The window's tasks that both interrupt people and are permitted to run here. Empty means
    /// nothing to warn about, and the countdown is retracted rather than opened.
    /// </param>
    /// <param name="now">The instant of this tick.</param>
    /// <param name="ct">Cancels the request.</param>
    public async Task AnnounceUpcomingAsync(
        string name,
        Instance instance,
        ReadWindow read,
        WindowPlan plan,
        IReadOnlyCollection<MaintenanceTask> disruptive,
        DateTime now,
        CancellationToken ct)
    {
        string windowId = read.Window.Id;
        PendingAnnouncement? existing = pending.Get(name, windowId);
        string? reason = AnnouncementPlan.Reason(disruptive);

        if (plan.NextUtc is not DateTime target || reason is null || !AnnouncementPlan.CanAnnounce(instance))
        {
            // A countdown standing against a window that has been deleted, emptied of everything
            // disruptive, or a game that can no longer carry the message, is owed its retraction
            // like any other abandonment.
            if (existing is not null)
            {
                await RetractAsync(name, instance, windowId, ct).ConfigureAwait(false);
            }

            return;
        }

        IReadOnlyList<int> leads = AnnouncementPlan.ApplicableLeads(
            AnnouncementPlan.ParseLeadMinutes(instance.AnnounceLeadMinutes), read.Period, out var dropped);

        ReportDropped(name, windowId, read, dropped);

        if (leads.Count == 0)
        {
            if (existing is not null)
            {
                await RetractAsync(name, instance, windowId, ct).ConfigureAwait(false);
            }

            return;
        }

        double minutesUntil = (target - now).TotalMinutes;

        if (minutesUntil > leads[0])
        {
            return;
        }

        // A target that moved — a postponement, a skipped occurrence, or an edited window — is a
        // different fire from the one anybody was told about. The warnings already given are void,
        // and saying so is what keeps the earlier countdown from ending in an unexplained silence:
        // told "one minute", then nothing, then a fresh countdown an hour later with no word about
        // the first.
        IReadOnlyList<int> announced = [];

        if (existing is not null)
        {
            if (existing.FireAtUtc == new DateTimeOffset(target, TimeSpan.Zero))
            {
                announced = existing.AnnouncedLeads;
            }
            else
            {
                await RetractAsync(name, instance, windowId, ct).ConfigureAwait(false);
            }
        }

        int? mark = AnnouncementPlan.NextMark(leads, announced, minutesUntil, out var due);
        if (mark is null)
        {
            return;
        }

        int[] spent = announced.Concat(due).Distinct().ToArray();

        // Whether anybody is listening is the watchdog's answer and only its. An instance it cannot
        // observe is announced to anyway: "no players detected" and "detection is unavailable" are
        // different facts, and reading the second as the first silences a server full of people.
        if (await NobodyIsConnectedAsync(name, ct).ConfigureAwait(false))
        {
            logger.LogDebug("{Instance}: skipping the {Mark}-minute announcement for {Window} — nobody is connected",
                name, mark, windowId);
            pending.Set(name, windowId, new PendingAnnouncement(new DateTimeOffset(target, TimeSpan.Zero), spent));
            return;
        }

        string message = AnnouncementPlan.Resolve(
            instance.AnnounceMaintenanceMessage!, instance, mark, reason);
        KgsmResult result = instances.Announce(
            name, message, actor: Provenance.Actor, origin: Provenance.Origin);

        if (result.ExitCode != 0)
        {
            // The maintenance is the job; a warning that could not be delivered does not stop it.
            // Never silent, though — an announcement path that fails quietly is indistinguishable
            // from one nobody wired up.
            logger.LogWarning("{Instance}: could not announce {Window}: {Error}", name, windowId, result.Stderr);
        }
        else
        {
            logger.LogInformation("{Instance}: announced {Window}, {Mark} minute(s) out", name, windowId, mark);
        }

        // The mark is spent either way. A send that failed will fail again on the next tick, and
        // retrying it would turn one undeliverable warning into one per tick until the fire.
        pending.Set(name, windowId, new PendingAnnouncement(new DateTimeOffset(target, TimeSpan.Zero), spent));
    }

    /// <summary>
    /// Tells a server the maintenance it was warned about is not happening, and forgets the warning.
    /// </summary>
    /// <remarks>
    /// A warning followed by silence is worse than no warning: players log off for a restart that
    /// never comes, and nothing tells them otherwise. Called wherever an announced fire is
    /// abandoned — a gate declining it, a window deleted mid-countdown, a fire too overdue to run,
    /// an occurrence skipped. No-op for a window that was never announced.
    /// </remarks>
    public async Task RetractAsync(string name, Instance instance, string windowId, CancellationToken ct)
    {
        if (pending.Get(name, windowId) is null)
        {
            return;
        }

        pending.Clear(name, windowId);

        string? template = instance.AnnounceMaintenanceCancelledMessage;
        if (string.IsNullOrWhiteSpace(template) || !BroadcastCommand.IsSupported(instance.BroadcastCommand))
        {
            return;
        }

        if (await NobodyIsConnectedAsync(name, ct).ConfigureAwait(false))
        {
            return;
        }

        string message = AnnouncementPlan.Resolve(template, instance, minutes: null);
        KgsmResult result = instances.Announce(
            name, message, actor: Provenance.Actor, origin: Provenance.Origin);

        if (result.ExitCode != 0)
        {
            logger.LogWarning("{Instance}: could not announce the cancellation of {Window}: {Error}",
                name, windowId, result.Stderr);
        }
        else
        {
            logger.LogInformation("{Instance}: announced that {Window} is cancelled", name, windowId);
        }
    }

    /// <summary>
    /// Settles a window's debt: what was announced has now happened, so the next countdown starts
    /// from nothing said.
    /// </summary>
    public void Settle(string name, string windowId) => pending.Clear(name, windowId);

    /// <summary>
    /// Whether the watchdog can see this instance's players and reports none connected.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> whenever the answer is not a measured empty roster — an
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

    /// <summary>
    /// Says once, per configuration, which of an instance's lead times a window is too frequent to
    /// honour. Silently honouring fewer leads than an operator wrote is the failure mode this
    /// exists against.
    /// </summary>
    private void ReportDropped(string name, string windowId, ReadWindow read, IReadOnlyList<int> dropped)
    {
        if (dropped.Count == 0) return;

        string key = $"{name}|{windowId}|{string.Join(',', dropped)}";
        if (!_reportedDrops.TryAdd(key, 0)) return;

        logger.LogWarning(
            "{Instance}: {Window} fires every {Period:F0} minute(s), so the {Leads}-minute lead time(s) "
            + "are not announced — a warning at or beyond a window's own period would be spoken before "
            + "the fire it describes",
            name, windowId, read.Period.TotalMinutes, string.Join(", ", dropped));
    }
}
