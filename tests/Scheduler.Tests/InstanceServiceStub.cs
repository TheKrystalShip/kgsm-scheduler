using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Kgsm.Scheduler.Tests;

/// <summary>
/// An engine that records what it was asked to do and answers with a prepared result. Only the
/// verbs a maintenance window drives are implemented; everything else throws rather than pretending
/// to a result.
/// </summary>
internal sealed class InstanceServiceStub : IInstanceService
{
    /// <summary>Instances to hand back, by id.</summary>
    public Dictionary<string, Instance> Instances { get; } = new(StringComparer.Ordinal);

    /// <summary>Every backup, prune and announcement, in the order they were asked for.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>Exit code the next backup returns.</summary>
    public int BackupExitCode { get; set; }

    /// <summary>What that failure says.</summary>
    public string BackupStderr { get; set; } = "";

    /// <summary>Exit code the next prune returns.</summary>
    public int PruneExitCode { get; set; }

    /// <summary>Blocks inside CreateBackup until released, so a run can be held open mid-window.</summary>
    public SemaphoreSlim? HoldBackup { get; set; }

    public Dictionary<string, Instance> GetAll() => Instances;

    public KgsmResult CreateBackup(string instanceName, string? actor = null, string? origin = null,
        string? reason = null, string? retention = null)
    {
        Calls.Add($"backup:{instanceName}:{reason}:{actor}:{origin}");
        HoldBackup?.Wait();
        return new KgsmResult(BackupExitCode, Stderr: BackupStderr);
    }

    public KgsmResult PruneBackups(string instanceName, int keepN, string? actor = null, string? origin = null)
    {
        Calls.Add($"prune:{instanceName}:{keepN}");
        return new KgsmResult(PruneExitCode, Stderr: "the prune refused");
    }

    public KgsmResult Announce(string instanceName, string message, string? actor = null, string? origin = null)
    {
        Calls.Add($"announce:{instanceName}:{message}");
        return new KgsmResult(0);
    }

    private static T Unused<T>() => throw new NotSupportedException("not exercised by these tests");

    public Dictionary<string, Instance>? GetAllOrNull() => Unused<Dictionary<string, Instance>?>();
    public Instance? GetInstanceInfo(string instanceName) => Unused<Instance?>();
    public InstanceRuntimeStatus? GetInstanceStatus(string instanceName) => Unused<InstanceRuntimeStatus?>();
    public Dictionary<string, Reading<InstanceRuntimeStatus>> GetAllStatuses(bool fast = false) => Unused<Dictionary<string, Reading<InstanceRuntimeStatus>>>();
    public KgsmResult Install(string blueprintName, string? library = null, string? version = null, string? displayName = null, string? actor = null, string? origin = null, int? port = null, bool? start = null, string? id = null) => Unused<KgsmResult>();
    public KgsmResult Uninstall(string instanceName, string? actor = null, string? origin = null) => Unused<KgsmResult>();
    public KgsmResult Move(string instanceName, string library, bool skipSpaceCheck = false, string? actor = null, string? origin = null) => Unused<KgsmResult>();
    public ICollection<string> GetLogs(string instanceName, int maxLines = 10) => Unused<ICollection<string>>();
    public Task<ICollection<string>> GetLogsAsync(string instanceName, int maxLines = 10, CancellationToken cancellationToken = default) => Unused<Task<ICollection<string>>>();
    public KgsmResult GetStatus(string instanceName) => Unused<KgsmResult>();
    public KgsmResult GetInfo(string instanceName) => Unused<KgsmResult>();
    public bool IsActive(string instanceName) => Unused<bool>();
    public KgsmResult Start(string instanceName, string? actor = null, string? origin = null) => Unused<KgsmResult>();
    public KgsmResult Stop(string instanceName, string? actor = null, string? origin = null) => Unused<KgsmResult>();
    public KgsmResult Restart(string instanceName, string? actor = null, string? origin = null) => Unused<KgsmResult>();
    public KgsmResult GetInstalledVersion(string instanceName) => Unused<KgsmResult>();
    public KgsmResult GetLatestVersion(string instanceName) => Unused<KgsmResult>();
    public KgsmResult CheckUpdate(string instanceName, bool emit = false, string? actor = null, string? origin = null) => Unused<KgsmResult>();
    public KgsmResult Update(string instanceName, string? actor = null, string? origin = null) => Unused<KgsmResult>();
    public KgsmResult GetBackups(string instanceName) => Unused<KgsmResult>();
    public List<InstanceBackup> GetBackupsDetailed(string instanceName) => Unused<List<InstanceBackup>>();
    public KgsmResult PinBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => Unused<KgsmResult>();
    public KgsmResult UnpinBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => Unused<KgsmResult>();
    public KgsmResult DeleteBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => Unused<KgsmResult>();
    public KgsmResult RestoreBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => Unused<KgsmResult>();
    public KgsmResult GenerateId(string blueprintName, string? id = null) => Unused<KgsmResult>();
    public KgsmResult Save(string instanceName) => Unused<KgsmResult>();
    public KgsmResult SendInput(string instanceName, string command, string? actor = null, string? origin = null) => Unused<KgsmResult>();
    public KgsmResult Kick(string instanceName, string target, string? actor = null, string? origin = null) => Unused<KgsmResult>();
    public KgsmResult Ban(string instanceName, string target, string? actor = null, string? origin = null) => Unused<KgsmResult>();
    public KgsmResult Unban(string instanceName, string target, string? actor = null, string? origin = null) => Unused<KgsmResult>();
    public KgsmResult FindConfigPath(string instanceName) => Unused<KgsmResult>();
    public KgsmResult GetInstanceConfigValue(string instanceName, string key) => Unused<KgsmResult>();
    public List<InstanceConfigEntry>? GetInstanceConfig(string instanceName, bool settableOnly = false) => Unused<List<InstanceConfigEntry>?>();
    public KgsmResult SetInstanceConfigValue(string instanceName, string key, string value, string? actor = null, string? origin = null) => Unused<KgsmResult>();
    public KgsmResult SetDisplayName(string instanceId, string displayName, string? actor = null, string? origin = null) => Unused<KgsmResult>();
    public InstanceNoteResult SetInstanceNote(string instanceName, string body, string? actor = null, string? origin = null) => Unused<InstanceNoteResult>();
    public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, CancellationToken cancellationToken = default) => Unused<Task<LogSubscription>>();
    public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, LogLevel minimumLogLevel, bool includeRawLines = true, CancellationToken cancellationToken = default) => Unused<Task<LogSubscription>>();}
