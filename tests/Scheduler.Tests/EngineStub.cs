using TheKrystalShip.KGSM.Engine.Contracts.Backups;
using TheKrystalShip.KGSM.Engine.Contracts.Instances;
using TheKrystalShip.KGSM.Engine.Contracts.Invocation;
using TheKrystalShip.KGSM.Engine.Contracts.Operations;
using TheKrystalShip.KGSM.Engine.Contracts.Reports;

namespace TheKrystalShip.Kgsm.Scheduler.Tests;

/// <summary>
/// An engine that records what it was asked to do and answers with a prepared result. It implements
/// exactly the seam the daemon uses, so nothing else is reachable from a test.
/// </summary>
internal sealed class EngineStub : IEngine
{
    /// <summary>Instances to hand back, by id.</summary>
    public Dictionary<string, InstanceConfig> Instances { get; } = new(StringComparer.Ordinal);

    /// <summary>Whether the registry reads at all; false answers the way an unreadable one does.</summary>
    public bool RegistryReadable { get; set; } = true;

    /// <summary>Every backup, prune, update, check and announcement, in the order they were asked for.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>Exit code the next backup returns.</summary>
    public int BackupExitCode { get; set; }

    /// <summary>What that failure says.</summary>
    public string BackupStderr { get; set; } = "";

    /// <summary>Exit code the next prune returns.</summary>
    public int PruneExitCode { get; set; }

    /// <summary>Exit code the next update returns.</summary>
    public int UpdateExitCode { get; set; }

    /// <summary>What that failure says.</summary>
    public string UpdateStderr { get; set; } = "";

    /// <summary>Exit code the next update check returns.</summary>
    public int CheckExitCode { get; set; }

    /// <summary>The version record the engine keeps beside each instance. An instance absent from here has none.</summary>
    public Dictionary<string, InstanceVersion> Versions { get; } = new(StringComparer.Ordinal);

    /// <summary>Records what the engine holds for <paramref name="name"/> after a real check.</summary>
    public void Recorded(string name, string? current, string? latest, bool? updatesAvailable) =>
        Versions[name] = new InstanceVersion
        {
            Current = current,
            Latest = latest,
            Checked = updatesAvailable is not null,
            CheckedAt = updatesAvailable is null ? null : DateTimeOffset.UtcNow,
            UpdatesAvailable = updatesAvailable,
        };

    /// <summary>Blocks inside the backup until released, so a run can be held open mid-window.</summary>
    public SemaphoreSlim? HoldBackup { get; set; }

    IReadOnlyDictionary<string, InstanceConfig>? IEngine.Instances() => RegistryReadable ? Instances : null;

    public IReadOnlyDictionary<string, InstanceVersion> RecordedVersions()
    {
        Calls.Add("versions");
        return Versions;
    }

    public async Task<EngineResult<BackupCreateReport>> CreateBackupAsync(string id, BackupReason reason, CancellationToken ct)
    {
        Calls.Add($"backup:{id}:{reason.ToString().ToLowerInvariant()}");
        if (HoldBackup is { } hold) await hold.WaitAsync(ct);
        return Result<BackupCreateReport>(BackupExitCode, BackupStderr);
    }

    public Task<EngineResult<BackupPruneReport>> PruneBackupsAsync(string id, int keep, CancellationToken ct)
    {
        Calls.Add($"prune:{id}:{keep}");
        return Task.FromResult(Result<BackupPruneReport>(PruneExitCode, "the prune refused"));
    }

    public Task<EngineResult<UpdateReport>> UpdateAsync(string id, CancellationToken ct)
    {
        Calls.Add($"update:{id}");
        return Task.FromResult(Result<UpdateReport>(UpdateExitCode, UpdateStderr));
    }

    public Task<EngineResult<UpdateCheckReport>> CheckUpdateAsync(string id, CancellationToken ct)
    {
        Calls.Add($"check:{id}");
        return Task.FromResult(Result<UpdateCheckReport>(CheckExitCode, "the upstream refused"));
    }

    public Task<EngineResult<ActionReport>> AnnounceAsync(string id, string message, CancellationToken ct)
    {
        Calls.Add($"announce:{id}:{message}");
        return Task.FromResult(Result<ActionReport>(0, ""));
    }

    private static EngineResult<T> Result<T>(int exitCode, string detail) => new()
    {
        ExitCode = exitCode,
        Error = exitCode == 0 ? null : new ErrorReport { Code = ErrorCodes.Failed, Message = detail, ExitCode = exitCode },
    };
}
