using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Engine.Contracts.Backups;
using TheKrystalShip.KGSM.Engine.Contracts.Scheduling;

namespace TheKrystalShip.Kgsm.Scheduler;

/// <summary>
/// Archives the instance's saved state, then prunes to its retention count.
/// </summary>
/// <remarks>
/// <para>
/// Ungated, and it leaves the instance exactly as it is: kgsm records the state an archive was
/// captured in, so a scheduled backup is valid whatever the server is doing and needs no window in
/// which the server is down.
/// </para>
/// <para>
/// The archive states that a cadence took it. Nothing downstream could otherwise tell a nightly
/// backup from one a person asked for, and the engine will not guess: an unstated reason is
/// recorded as an ad-hoc request, which is what this is not.
/// </para>
/// </remarks>
internal sealed class BackupTask(ILogger<BackupTask> logger) : IMaintenanceTask
{
    public string Name => MaintenanceTask.Backup.ToToken();

    /// <summary>Nobody is interrupted by an archive, so a backup-only window is never announced.</summary>
    public bool IsDisruptive => false;

    public Task<TaskGate> GateAsync(MaintenanceContext ctx, CancellationToken ct) =>
        Task.FromResult(TaskGate.Dispatch);

    public async Task<TaskOutcome> RunAsync(MaintenanceContext ctx, CancellationToken ct)
    {
        logger.LogInformation("{Instance}: creating scheduled backup", ctx.Name);

        var result = await ctx.Engine.CreateBackupAsync(ctx.Name, BackupReason.Scheduled, ct).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            string detail = result.Message;
            logger.LogWarning("{Instance}: scheduled backup failed: {Err}", ctx.Name, detail);
            return TaskOutcome.Failed(detail);
        }

        int retention = ctx.Instance.BackupRetention;
        if (retention <= 0)
        {
            return TaskOutcome.Ok();
        }

        // Only after an archive that actually landed: pruning around a failed one would drop a good
        // backup to make room for something that was never written. The prune keeps that many
        // PRUNABLE archives — pinned ones are skipped and consume no slot, so an operator protecting
        // an archive never shrinks the window this window maintains.
        var prune = await ctx.Engine.PruneBackupsAsync(ctx.Name, retention, ct).ConfigureAwait(false);

        if (prune.IsSuccess)
        {
            return TaskOutcome.Ok();
        }

        // The archive is the job and it landed, so this is not a failed backup. It is also not
        // nothing: a rotation that stops running fills a disk quietly, so it travels on the record.
        string pruneDetail = prune.Message;
        logger.LogWarning("{Instance}: backup prune failed: {Err}", ctx.Name, pruneDetail);
        return TaskOutcome.Ok($"the archive was taken; the prune failed: {pruneDetail}");
    }
}
