using TheKrystalShip.KGSM.Engine.Contracts.Backups;
using TheKrystalShip.KGSM.Engine.Contracts.Instances;
using TheKrystalShip.KGSM.Engine.Contracts.Libraries;
using TheKrystalShip.KGSM.Engine.Instances;
using TheKrystalShip.KGSM.Engine.Contracts.Invocation;
using TheKrystalShip.KGSM.Engine.Contracts.Operations;
using TheKrystalShip.KGSM.Engine.Reading;

namespace TheKrystalShip.Kgsm.Scheduler;

/// <summary>
/// <see cref="IEngine"/> over the engine core (reads) and the typed invocation of the binary (writes).
/// </summary>
internal sealed class EngineGateway(EngineReader reader, EngineCli cli) : IEngine
{
    private static readonly EngineCaller Caller = new(Provenance.Actor, Provenance.Origin);

    public IReadOnlyDictionary<string, InstanceConfig>? Instances()
    {
        try
        {
            var configs = new Dictionary<string, InstanceConfig>(StringComparer.Ordinal);
            foreach (var instance in reader.Instances())
            {
                if (instance.Config is { } config)
                    configs[instance.Id] = config;
            }

            return configs;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TheKrystalShip.KGSM.Engine.EngineException)
        {
            return null;
        }
    }

    public IReadOnlyDictionary<string, InstanceVersion> RecordedVersions()
    {
        var versions = new Dictionary<string, InstanceVersion>(StringComparer.Ordinal);
        foreach (var instance in reader.Instances())
        {
            if (instance.LibraryState == InstanceLibraryState.Offline) continue;
            versions[instance.Id] = InstanceVersions.Read(instance.Layout);
        }

        return versions;
    }

    public Task<EngineResult<BackupCreateReport>> CreateBackupAsync(string id, BackupReason reason, CancellationToken ct) =>
        cli.CreateBackupAsync(id, reason, caller: Caller, cancellationToken: ct);

    public Task<EngineResult<BackupPruneReport>> PruneBackupsAsync(string id, int keep, CancellationToken ct) =>
        cli.PruneBackupsAsync(id, keep, Caller, ct);

    public Task<EngineResult<UpdateReport>> UpdateAsync(string id, CancellationToken ct) =>
        cli.UpdateAsync(id, Caller, ct);

    public Task<EngineResult<UpdateCheckReport>> CheckUpdateAsync(string id, CancellationToken ct) =>
        cli.CheckUpdateAsync(id, emit: true, Caller, ct);

    public Task<EngineResult<ActionReport>> AnnounceAsync(string id, string message, CancellationToken ct) =>
        cli.AnnounceAsync(id, message, Caller, ct);
}
