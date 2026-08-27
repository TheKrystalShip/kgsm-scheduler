using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Core.Scheduling;

namespace TheKrystalShip.Kgsm.Scheduler;

/// <summary>
/// Applies the newer game build, behind a watchdog park.
/// </summary>
/// <remarks>
/// <para>
/// <b>The park is this task's own.</b> The engine refuses to update a running instance, so this is
/// the one task that needs a span in which the server stays stopped — and it is held around the
/// engine call alone, never across the archive written before it or the tasks after it. Parked is
/// stopped while still wanted running: crash-restart is suppressed for as long as the park holds,
/// the failure streak and the give-up latch come out of it as they went in, and a stop/start pair
/// from here would say instead that nobody wants the server up and strand it if this daemon died
/// between the two.
/// </para>
/// <para>
/// <b>The release is unconditional</b>, whatever the update did. A window never leaves a server
/// down, and the release is what makes that true rather than the tasks that happen to follow.
/// </para>
/// <para>
/// kgsm takes its own <c>pre-update</c> archive inside the update and abandons the update if that
/// archive fails. That guarantee is the engine's, so nothing here duplicates it.
/// </para>
/// </remarks>
internal sealed class UpdateTask(IOptions<SchedulerOptions> options, ILogger<UpdateTask> logger) : IMaintenanceTask
{
    public string Name => MaintenanceTask.Update.ToToken();

    /// <summary>The server is down for the length of the update, so a window carrying this is announced.</summary>
    public bool IsDisruptive => true;

    /// <summary>
    /// Dispatches only on the engine's recorded evidence that a newer build stands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reading is the one the update-check sweep left beside the instance, taken off disk with
    /// no upstream call. Asking upstream here instead would cost a real steamcmd login before every
    /// update window, and the answer it usually gives is "nothing to do" — an update with nothing to
    /// do is a server stopped for nothing.
    /// </para>
    /// <para>
    /// An unrecorded upstream is not evidence that an update is owed, so it declines rather than
    /// stopping a live server on no reading at all. The sweep records one within its own interval;
    /// with the sweep off, nothing on this host ever does, and the skip says so.
    /// </para>
    /// </remarks>
    public async Task<TaskGate> GateAsync(MaintenanceContext ctx, CancellationToken ct)
    {
        // The engine's container update pulls an image rather than a build, and the park every
        // disruptive task runs behind belongs to the watchdog, which supervises native instances
        // alone. Declining leaves the archive written beside it in the same window still firing.
        if (ctx.Instance.Runtime == InstanceRuntime.Container)
            return TaskGate.Skip(
                "not dispatched: this is a container instance, and the watchdog this daemon "
                + "parks through supervises only native ones");

        VersionInfo? version;
        try
        {
            version = await Task.Run(() => Recorded(ctx.Instances, ctx.Name), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Owed and undecidable: the engine holds the only record of what upstream offers, so
            // without it this window cannot tell an up-to-date server from a stale one.
            return TaskGate.Fail($"not dispatched: the engine's recorded version could not be read ({ex.Message})");
        }

        if (version is null)
            return TaskGate.Skip(
                "not dispatched: the engine reported no version for this instance, so nothing here "
                + "knows whether a newer build stands");

        if (version.UpdatesAvailable is null)
            return TaskGate.Skip(options.Value.UpdateCheckEnabled
                ? "not dispatched: nothing has recorded an upstream version for this instance yet"
                : "not dispatched: nothing has recorded an upstream version for this instance, and "
                  + "update checks are disabled on this host");

        if (version.UpdatesAvailable == false)
            return TaskGate.Skip(
                $"not dispatched: no newer build stands — the installed build ({Or(version.Current)}) is "
                + $"the latest the engine recorded{At(version.CheckedAt)}");

        return TaskGate.Dispatch;
    }

    public async Task<TaskOutcome> RunAsync(MaintenanceContext ctx, CancellationToken ct)
    {
        WatchdogActionResult park = await ctx.Watchdog
            .BeginMaintenanceAsync(ctx.Name, Provenance.Leaf, ct)
            .ConfigureAwait(false);

        bool parked = park.Ok;

        if (parked)
        {
            logger.LogInformation("{Instance}: parked for the update — {Msg}", ctx.Name, park.Message);
        }
        else if (await SpawnsNothingAsync(ctx, ct).ConfigureAwait(false))
        {
            // Nothing to park and nothing that will spawn it underneath the update: the instance is
            // already where the engine needs it, and it stays down afterwards because that is what
            // desired-state says. An update of a server nobody wanted running costs no downtime.
            logger.LogInformation("{Instance}: updating without a park — {Msg}", ctx.Name, park.Message);
        }
        else
        {
            // The instance is live or wanted live and the watchdog would not hand it over, so the
            // engine would refuse the update and a supervisor could spawn it mid-write.
            return TaskOutcome.Failed($"the instance could not be parked for the update: {park.Message}");
        }

        TaskOutcome outcome;
        WatchdogActionResult? release = null;

        try
        {
            outcome = await ApplyAsync(ctx, ct).ConfigureAwait(false);
        }
        finally
        {
            if (parked)
            {
                release = await ctx.Watchdog
                    .EndMaintenanceAsync(ctx.Name, Provenance.Leaf, CancellationToken.None)
                    .ConfigureAwait(false);

                logger.LogInformation("{Instance}: release {Result} — {Msg}",
                    ctx.Name, release.Ok ? "ok" : "refused", release.Message);

                // The pair drained the instance and brought it back, which is the bounce a restart
                // in the same window asks for.
                if (release.Ok) ctx.Progress.MarkCycled();
            }
        }

        if (release is null || release.Ok) return outcome;

        // The engine's half may well have worked; the server is down either way, and that is the
        // fact the window owes. The watchdog hands a refused release to its own restart loop, so
        // this is a report rather than an abandonment.
        string left = $"the watchdog did not bring the instance back out of maintenance: {release.Message}";
        return TaskOutcome.Failed(
            outcome.Message is { Length: > 0 } detail ? $"{detail}; {left}" : left);
    }

    private async Task<TaskOutcome> ApplyAsync(MaintenanceContext ctx, CancellationToken ct)
    {
        logger.LogInformation("{Instance}: applying the update", ctx.Name);

        KgsmResult result = await Task
            .Run(() => ctx.Instances.Update(ctx.Name, actor: Provenance.Actor, origin: Provenance.Origin), ct)
            .ConfigureAwait(false);

        if (result.ExitCode == 0)
        {
            logger.LogInformation("{Instance}: update ok", ctx.Name);
            return TaskOutcome.Ok();
        }

        string detail = EngineDetail.Summarize(result.Stderr) ?? "the engine gave no detail";
        logger.LogWarning("{Instance}: update failed: {Err}", ctx.Name, detail);
        return TaskOutcome.Failed(detail);
    }

    /// <summary>
    /// Whether the update may run without a park: the watchdog will start nothing here for as long
    /// as it takes.
    /// </summary>
    /// <remarks>
    /// True for an instance the daemon does not track, and for one it holds as stopped. Everything
    /// else — a park somebody else owns, a crash-restart pending, a process the park lost a race to
    /// — is the watchdog still acting on the instance, and updating a directory a supervisor is
    /// about to spawn out of is the one thing this must not do. An unreachable watchdog measures
    /// nothing and so answers false.
    /// </remarks>
    private static async Task<bool> SpawnsNothingAsync(MaintenanceContext ctx, CancellationToken ct)
    {
        WatchdogInstanceState? state;
        try
        {
            state = await ctx.Watchdog.GetStatusAsync(ctx.Name, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }

        if (state is null) return true;

        return !state.Populated
            && !string.Equals(state.Desired, "running", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The version record the engine keeps beside the instance, answered off disk.
    /// </summary>
    /// <remarks>
    /// The fleet read is the one call kgsm exposes that touches no network: it answers every
    /// instance from what the last real check recorded, which is exactly the reading this gate is
    /// after. An instance whose status could not be read yields nothing rather than a fabricated
    /// "up to date".
    /// </remarks>
    private static VersionInfo? Recorded(IInstanceService instances, string name)
    {
        Dictionary<string, Reading<InstanceRuntimeStatus>> statuses = instances.GetAllStatuses(fast: true);

        return statuses.TryGetValue(name, out Reading<InstanceRuntimeStatus>? reading) && reading.IsMeasured
            ? reading.Value?.Version
            : null;
    }

    private static string Or(string? version) =>
        string.IsNullOrWhiteSpace(version) ? "unknown" : version.Trim();

    private static string At(DateTimeOffset? checkedAt) =>
        checkedAt is { } at ? $" at {at.UtcDateTime:u}" : string.Empty;
}
