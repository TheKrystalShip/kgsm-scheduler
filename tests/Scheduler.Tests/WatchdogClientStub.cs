using TheKrystalShip.KGSM.Watchdog.Client;
using TheKrystalShip.KGSM.Watchdog.Model;


namespace TheKrystalShip.Kgsm.Scheduler.Tests;

/// <summary>
/// A watchdog that answers with one prepared state, or refuses to answer at all, and records the
/// lifecycle verbs it was told. Everything a test does not exercise throws rather than pretending
/// to a result.
/// </summary>
internal sealed class WatchdogClientStub : IWatchdogClient
{
    private readonly InstanceState? _state;
    private readonly bool _reachable;

    private WatchdogClientStub(InstanceState? state, bool reachable)
    {
        _state = state;
        _reachable = reachable;
    }

    public static WatchdogClientStub Answering(InstanceState? state) => new(state, reachable: true);

    public static WatchdogClientStub Unreachable() => new(null, reachable: false);

    /// <summary>Builds a state the gate reads as "restart this".</summary>
    public static InstanceState Running(string name = "factorio-01") =>
        new(name, Desired: "running", Enabled: false, Populated: true, Pid: null, CgroupPath: "",
            Phase: "running", Restarts: 0, Reason: "");

    /// <summary>Builds a state nothing will spawn: held stopped, with no live process.</summary>
    public static InstanceState Stopped(string name = "factorio-01") =>
        new(name, Desired: "stopped", Enabled: false, Populated: false, Pid: null, CgroupPath: "",
            Phase: "stopped", Restarts: 0, Reason: "");

    /// <summary>How many times the gate asked for a state — a container never gets that far.</summary>
    public int StatusCalls { get; private set; }

    /// <summary>Every restart this stub was told to perform.</summary>
    public List<string> Restarts { get; } = [];

    /// <summary>
    /// Where every lifecycle verb is recorded, in order. Point it at the engine stub's own list to
    /// read the order of both — which is what a park bracketing an engine call looks like.
    /// </summary>
    public List<string> Calls { get; set; } = [];

    /// <summary>Whether the next restart is refused.</summary>
    public bool RefuseRestart { get; set; }

    /// <summary>Whether the next park is refused, and what it says.</summary>
    public bool RefusePark { get; set; }

    /// <summary>Whether the next release is refused, and what it says.</summary>
    public bool RefuseRelease { get; set; }

    /// <summary>Players the roster reports, or null for an instance the daemon cannot observe.</summary>
    public IReadOnlyDictionary<string, InstancePresence>? Presence { get; set; }

    public Task<InstanceState?> StatusAsync(string instance, CancellationToken cancellationToken = default)
    {
        StatusCalls++;

        if (!_reachable)
            throw new HttpRequestException("Connection refused (/run/kgsm-watchdog/control.sock)");

        return Task.FromResult(_state);
    }

    public Task<ActionResult> RestartAsync(
        string instance, string? origin = null, CancellationToken cancellationToken = default)
    {
        Restarts.Add(instance);
        Calls.Add($"restart:{instance}:{origin}");
        return Task.FromResult(new ActionResult(instance, !RefuseRestart, RefuseRestart ? "the instance is not running" : "restarted"));
    }

    public Task<ActionResult> BeginMaintenanceAsync(
        string instance, string? origin = null, CancellationToken cancellationToken = default)
    {
        Calls.Add($"park:{instance}:{origin}");
        return Task.FromResult(new ActionResult(instance, !RefusePark, RefusePark ? "not running — nothing to park" : "parked for maintenance (scheduler)"));
    }

    public Task<ActionResult> EndMaintenanceAsync(
        string instance, string? origin = null, CancellationToken cancellationToken = default)
    {
        Calls.Add($"release:{instance}:{origin}");
        return Task.FromResult(new ActionResult(instance, !RefuseRelease, RefuseRelease ? "respawn failed (the node is full)" : "released from maintenance (scheduler)"));
    }

    public Task<IReadOnlyDictionary<string, InstancePresence>?> PlayersAsync(
        CancellationToken cancellationToken = default) => Task.FromResult(Presence);

    public void Dispose() { }

    private static T Unused<T>() => throw new NotSupportedException("not exercised by these tests");

    public Task<WatchdogHealth> HealthAsync(CancellationToken cancellationToken = default) => Unused<Task<WatchdogHealth>>();
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Unused<Task<bool>>();
    public Task<IReadOnlyList<InstanceState>> ListAsync(CancellationToken cancellationToken = default) => Unused<Task<IReadOnlyList<InstanceState>>>();
    public Task<IReadOnlyList<InstanceRunTimes>> RunTimesAsync(CancellationToken cancellationToken = default) => Unused<Task<IReadOnlyList<InstanceRunTimes>>>();
    public Task<IReadOnlyList<string>> EnabledAsync(CancellationToken cancellationToken = default) => Unused<Task<IReadOnlyList<string>>>();
    public Task<WatchdogVersionInfo> VersionAsync(CancellationToken cancellationToken = default) => Unused<Task<WatchdogVersionInfo>>();
    public Task<ActionResult> StartAsync(string instance, bool force = false, string? origin = null, CancellationToken cancellationToken = default) => Unused<Task<ActionResult>>();
    public Task<ActionResult> StopAsync(string instance, string? origin = null, CancellationToken cancellationToken = default) => Unused<Task<ActionResult>>();
    public Task<ActionResult> EnableAsync(string instance, CancellationToken cancellationToken = default) => Unused<Task<ActionResult>>();
    public Task<ActionResult> DisableAsync(string instance, CancellationToken cancellationToken = default) => Unused<Task<ActionResult>>();
    public Task<ActionResult> ForgetAsync(string instance, CancellationToken cancellationToken = default) => Unused<Task<ActionResult>>();
    public Task<ActionResult> SetCpuPriorityAsync(string instance, string priority, CancellationToken cancellationToken = default) => Unused<Task<ActionResult>>();
    public Task<IReadOnlyList<ConsoleRun>> ConsoleRunsAsync(string instance, CancellationToken cancellationToken = default) => Unused<Task<IReadOnlyList<ConsoleRun>>>();
    public Task<ConsoleWindow> ConsoleWindowAsync(string instance, int lines, int run = 0, long endOffset = -1, CancellationToken cancellationToken = default) => Unused<Task<ConsoleWindow>>();
    public Task<IReadOnlyList<string>> ConsoleTailAsync(string instance, int lines, int run = 0, CancellationToken cancellationToken = default) => Unused<Task<IReadOnlyList<string>>>();
    public IAsyncEnumerable<string> FollowConsoleAsync(string instance, CancellationToken cancellationToken = default) => Unused<IAsyncEnumerable<string>>();
    public Task<ConsoleDownload?> OpenConsoleDownloadAsync(string instance, int run = 0, CancellationToken cancellationToken = default) => Unused<Task<ConsoleDownload?>>();
    public Task<UpnpListResult?> UpnpAsync(string instance, CancellationToken cancellationToken = default) => Unused<Task<UpnpListResult?>>();
    public Task<MemoryGateState?> MemoryGateAsync(CancellationToken cancellationToken = default) => Unused<Task<MemoryGateState?>>();
}
