using TheKrystalShip.KGSM.Engine.Contracts.Backups;
using TheKrystalShip.KGSM.Engine.Contracts.Instances;
using TheKrystalShip.KGSM.Engine.Contracts.Invocation;
using TheKrystalShip.KGSM.Engine.Contracts.Operations;

namespace TheKrystalShip.Kgsm.Scheduler;

/// <summary>
/// What this daemon asks of the engine: the roster and its recorded version evidence, read in-process,
/// and the five acts it performs, run through the <c>kgsm</c> binary as <see cref="Provenance.Actor"/>.
/// </summary>
/// <remarks>
/// The seam is this daemon's own, so a test substitutes the engine without a binary or a data home.
/// </remarks>
internal interface IEngine
{
    /// <summary>
    /// Every registered instance whose config reads, keyed by id, or null when the registry cannot be
    /// read at all, which is not the same as a host with no instances.
    /// </summary>
    IReadOnlyDictionary<string, InstanceConfig>? Instances();

    /// <summary>
    /// What each instance's last update check recorded, answered off disk: no network is touched. An
    /// instance whose status could not be read is absent rather than reported up to date.
    /// </summary>
    IReadOnlyDictionary<string, InstanceVersion> RecordedVersions();

    Task<EngineResult<BackupCreateReport>> CreateBackupAsync(string id, BackupReason reason, CancellationToken ct);

    /// <summary>Deletes the prunable backups beyond the newest <paramref name="keep"/>.</summary>
    Task<EngineResult<BackupPruneReport>> PruneBackupsAsync(string id, int keep, CancellationToken ct);

    Task<EngineResult<UpdateReport>> UpdateAsync(string id, CancellationToken ct);

    /// <summary>Asks upstream whether a newer build exists, recording the answer and announcing it once.</summary>
    Task<EngineResult<UpdateCheckReport>> CheckUpdateAsync(string id, CancellationToken ct);

    Task<EngineResult<ActionReport>> AnnounceAsync(string id, string message, CancellationToken ct);
}
