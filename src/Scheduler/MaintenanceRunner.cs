using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Core.Scheduling;

namespace TheKrystalShip.Kgsm.Scheduler;

/// <summary>
/// Runs one maintenance window against one instance: exclusive, announced, and abandoning the rest
/// of the window at the first failure.
/// </summary>
/// <remarks>
/// <para>
/// <b>The record belongs to the window that ran.</b> An instance's windows are independent
/// appointments that happen to touch the same server, so an archive that fails is written against
/// the window that asked for it and nowhere else. A window that finds the instance busy is recorded
/// <c>skipped</c> on itself, never <c>failed</c>, and never in another window's fields.
/// </para>
/// <para>
/// <b>A park belongs to the task that needs one, not to the window.</b> Only the update needs a span
/// in which the instance stays stopped, so it is the update that parks — around the engine call and
/// nothing else. Parking at the top of the window instead would hold a live server down through the
/// archive taken before it, and would put the record of the bounce in a place no task's outcome
/// could measure: a release the watchdog refuses leaves the instance down, and only the task that
/// asked for the park is in a position to report that.
/// </para>
/// <para>
/// One park is therefore one bring-up. A restart standing after a task that has already drained and
/// respawned the instance is already delivered, and the tasks settle that between them through
/// <see cref="WindowProgress"/> rather than bouncing a server twice for one window.
/// </para>
/// </remarks>
internal sealed class MaintenanceRunner(
    IInstanceService instances,
    IWatchdogClient watchdog,
    ScheduleRegistry registry,
    MaintenanceTaskCatalog catalog,
    WindowAnnouncer announcer,
    IOptions<SchedulerOptions> options,
    ILogger<MaintenanceRunner> logger)
{
    /// <summary>
    /// Instances with a window in flight. One window per instance at a time: a backup can outlive
    /// several ticks on a large game, and a restart must not land in the middle of one. A window
    /// that comes due while an instance is busy is skipped and recorded, never queued.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _busy = new(StringComparer.Ordinal);

    /// <summary>
    /// The tasks of <paramref name="read"/> that interrupt the people on <paramref name="instance"/>
    /// and can actually run against it.
    /// </summary>
    /// <remarks>
    /// This decides whether the window is announced and what <c>{reason}</c> says. Announcing a
    /// restart that will not happen is the one thing an announcement must never do, so a task the
    /// host policy refuses and a task the instance's runtime puts out of reach are both left out —
    /// both are known before the countdown opens, and a countdown that can only end in a retraction
    /// should never have started.
    /// </remarks>
    public IReadOnlyList<MaintenanceTask> DisruptiveTasks(ReadWindow read, Instance instance)
    {
        if (!read.Valid || !options.Value.AllowDisruptiveTasks) return [];

        // Every disruptive task is issued through the watchdog, which supervises native instances
        // alone. The task's own gate declines it either way; this keeps the server from being told
        // about it first.
        if (instance.Runtime == InstanceRuntime.Container) return [];

        return read.Window.Tasks.Where(t => catalog.Find(t)?.IsDisruptive == true).ToArray();
    }

    /// <summary>
    /// Opens a window run, off the tick so a long backup cannot hold up every other instance's
    /// schedule.
    /// </summary>
    /// <remarks>
    /// The instance's slot is claimed here, synchronously, so two windows of the same instance
    /// coming due on one tick resolve deterministically: the first claims it and the second is
    /// recorded skipped against itself.
    /// </remarks>
    public void Fire(string name, Instance instance, ReadWindow read)
    {
        string windowId = read.Window.Id;

        if (!_busy.TryAdd(name, 0))
        {
            logger.LogInformation(
                "{Instance}: skipping {Window} — a maintenance window is already running", name, windowId);

            const string reason = "a maintenance window was already running on this instance";
            var now = DateTimeOffset.UtcNow;

            Record(name, windowId, new MaintenanceRun(
                now, now, MaintenanceOutcomes.Skipped,
                [.. read.Window.Tasks.Select(t => new MaintenanceTaskRun(t.ToToken(), MaintenanceOutcomes.Skipped, reason))]));

            // Whoever was told this window was coming is told it is not. The countdown ended in
            // nothing happening, which is exactly what a retraction is for.
            _ = Task.Run(() => announcer.RetractAsync(name, instance, windowId, CancellationToken.None),
                CancellationToken.None);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(name, instance, read, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Nothing below is supposed to throw — each task's failure is a recorded outcome —
                // so reaching here means the run itself came apart and the window still owes a
                // record of having done so.
                logger.LogError(ex, "{Instance}: {Window} came apart", name, windowId);

                var now = DateTimeOffset.UtcNow;
                Record(name, windowId, new MaintenanceRun(
                    now, now, MaintenanceOutcomes.Failed,
                    [new MaintenanceTaskRun(windowId, MaintenanceOutcomes.Failed, ex.Message)]));
            }
            finally
            {
                _busy.TryRemove(name, out _);
            }
        }, CancellationToken.None);
    }

    private async Task RunAsync(string name, Instance instance, ReadWindow read, CancellationToken ct)
    {
        string windowId = read.Window.Id;
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var records = new List<MaintenanceTaskRun>(read.Window.Tasks.Count);
        bool aborted = false;
        bool disruptiveHappened = false;

        // One context for the whole window: the tasks are stateless singletons, so this is where
        // what one of them did to the instance is available to the next.
        var context = new MaintenanceContext(name, instance, read.Window, instances, watchdog, new WindowProgress());

        logger.LogInformation("{Instance}: running {Window} ({Tasks})",
            name, windowId, string.Join(", ", read.Window.Tasks.Select(t => t.ToToken())));

        // Canonical order — backup, then update, then restart — is the order kgsm-lib returns the
        // tasks in, whatever order they were written. A backup taken after an update archives the
        // new build instead of the rollback point.
        foreach (MaintenanceTask task in read.Window.Tasks)
        {
            IMaintenanceTask? implementation = catalog.Find(task);
            if (implementation is null)
            {
                // A window naming a task this daemon does not run is refused when it is read, so a
                // valid window never reaches here without one.
                continue;
            }

            if (aborted)
            {
                records.Add(new MaintenanceTaskRun(
                    implementation.Name, MaintenanceOutcomes.Aborted, "a prior task in this window failed"));
                continue;
            }

            if (implementation.IsDisruptive && !options.Value.AllowDisruptiveTasks)
            {
                records.Add(new MaintenanceTaskRun(implementation.Name, MaintenanceOutcomes.Skipped,
                    "disruptive maintenance is not permitted on this host"));
                continue;
            }

            TaskGate gate;
            try
            {
                gate = await implementation.GateAsync(context, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                gate = TaskGate.Fail($"the state could not be re-read before dispatch: {ex.Message}");
            }

            if (gate.Outcome == TaskGateOutcome.Skip)
            {
                logger.LogInformation("{Instance}: {Window}/{Task} — {Reason}",
                    name, windowId, implementation.Name, gate.Message);
                records.Add(new MaintenanceTaskRun(
                    implementation.Name, MaintenanceOutcomes.Skipped, gate.Message));
                continue;
            }

            if (gate.Outcome == TaskGateOutcome.Fail)
            {
                logger.LogWarning("{Instance}: {Window}/{Task} — {Reason}",
                    name, windowId, implementation.Name, gate.Message);
                records.Add(new MaintenanceTaskRun(
                    implementation.Name, MaintenanceOutcomes.Failed, gate.Message));
                aborted = true;
                continue;
            }

            TaskOutcome outcome;
            try
            {
                outcome = await implementation.RunAsync(context, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "{Instance}: {Window}/{Task} failed", name, windowId, implementation.Name);
                outcome = TaskOutcome.Failed(ex.Message);
            }

            records.Add(new MaintenanceTaskRun(implementation.Name, outcome.Outcome, outcome.Message));

            if (outcome.Outcome == MaintenanceOutcomes.Failed)
            {
                aborted = true;
            }
            else if (outcome.Outcome == MaintenanceOutcomes.Ok && implementation.IsDisruptive)
            {
                disruptiveHappened = true;
            }
        }

        var run = new MaintenanceRun(started, DateTimeOffset.UtcNow, Verdict(records), records);
        Record(name, windowId, run);

        logger.LogInformation("{Instance}: {Window} finished — {Outcome}", name, windowId, run.Outcome);

        // The thing the warnings were about either happened, settling the debt, or it did not, and a
        // countdown that ends in nothing is owed the sentence that says so. An interruption the
        // people on the server actually lived through settles it whatever the work behind it came to
        // — telling them afterwards that maintenance was cancelled would be the false sentence.
        if (disruptiveHappened || context.Progress.InstanceCycled)
        {
            announcer.Settle(name, windowId);
        }
        else
        {
            await announcer.RetractAsync(name, instance, windowId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// How the window as a whole came out.
    /// </summary>
    /// <remarks>
    /// A failure anywhere makes the window a failure, because the tasks after it never ran. With
    /// nothing failed, one task that did its work makes the window <c>ok</c>; a window where
    /// nothing applied to the instance is <c>skipped</c>, which is a different sentence from a
    /// window that tried and could not.
    /// </remarks>
    internal static string Verdict(IReadOnlyList<MaintenanceTaskRun> tasks)
    {
        if (tasks.Any(t => t.Outcome == MaintenanceOutcomes.Failed)) return MaintenanceOutcomes.Failed;
        if (tasks.Any(t => t.Outcome == MaintenanceOutcomes.Ok)) return MaintenanceOutcomes.Ok;
        return MaintenanceOutcomes.Skipped;
    }

    private void Record(string name, string windowId, MaintenanceRun run) =>
        registry.UpdateWindow(name, windowId, w => w with { LastRun = run });
}
