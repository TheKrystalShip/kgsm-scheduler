using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Kgsm.Scheduler.Tests;

/// <summary>
/// A watchdog that answers with one prepared state, or refuses to answer at all, and records the
/// lifecycle verbs it was told. Everything a test does not exercise throws rather than pretending
/// to a result.
/// </summary>
internal sealed class WatchdogClientStub : IWatchdogClient
{
    private readonly WatchdogInstanceState? _state;
    private readonly bool _reachable;

    private WatchdogClientStub(WatchdogInstanceState? state, bool reachable)
    {
        _state = state;
        _reachable = reachable;
    }

    public static WatchdogClientStub Answering(WatchdogInstanceState? state) => new(state, reachable: true);

    public static WatchdogClientStub Unreachable() => new(null, reachable: false);

    /// <summary>Builds a state the gate reads as "restart this".</summary>
    public static WatchdogInstanceState Running(string name = "factorio-01") => new()
    {
        Name = name,
        Desired = "running",
        Phase = "running",
        Populated = true,
        Restarts = 0,
        Reason = "",
    };

    /// <summary>Builds a state nothing will spawn: held stopped, with no live process.</summary>
    public static WatchdogInstanceState Stopped(string name = "factorio-01") => new()
    {
        Name = name,
        Desired = "stopped",
        Phase = "stopped",
        Populated = false,
        Restarts = 0,
        Reason = "",
    };

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
    public IReadOnlyDictionary<string, WatchdogInstancePresence>? Presence { get; set; }

    public Task<WatchdogInstanceState?> GetStatusAsync(string instanceName, CancellationToken cancellationToken = default)
    {
        StatusCalls++;

        if (!_reachable)
            throw new HttpRequestException("Connection refused (/run/kgsm-watchdog/control.sock)");

        return Task.FromResult(_state);
    }

    public Task<WatchdogActionResult> RestartAsync(
        string instanceName, string origin = "scheduler", CancellationToken cancellationToken = default)
    {
        Restarts.Add(instanceName);
        Calls.Add($"restart:{instanceName}:{origin}");
        return Task.FromResult(new WatchdogActionResult
        {
            Instance = instanceName,
            Ok = !RefuseRestart,
            Message = RefuseRestart ? "the instance is not running" : "restarted",
        });
    }

    public Task<WatchdogActionResult> BeginMaintenanceAsync(
        string instanceName, string origin = "scheduler", CancellationToken cancellationToken = default)
    {
        Calls.Add($"park:{instanceName}:{origin}");
        return Task.FromResult(new WatchdogActionResult
        {
            Instance = instanceName,
            Ok = !RefusePark,
            Message = RefusePark ? "not running — nothing to park" : "parked for maintenance (scheduler)",
        });
    }

    public Task<WatchdogActionResult> EndMaintenanceAsync(
        string instanceName, string origin = "scheduler", CancellationToken cancellationToken = default)
    {
        Calls.Add($"release:{instanceName}:{origin}");
        return Task.FromResult(new WatchdogActionResult
        {
            Instance = instanceName,
            Ok = !RefuseRelease,
            Message = RefuseRelease ? "respawn failed (the node is full)" : "released from maintenance (scheduler)",
        });
    }

    public Task<IReadOnlyDictionary<string, WatchdogInstancePresence>?> GetPlayerPresenceAsync(
        CancellationToken cancellationToken = default) => Task.FromResult(Presence);

    public void Dispose() { }

    private static T Unused<T>() => throw new NotSupportedException("not exercised by these tests");

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Unused<Task<bool>>();
    public Task<WatchdogReadyState?> GetReadyAsync(CancellationToken cancellationToken = default) => Unused<Task<WatchdogReadyState?>>();
    public Task<WatchdogActionResult> StartAsync(string instanceName, string origin = "scheduler", CancellationToken cancellationToken = default) => Unused<Task<WatchdogActionResult>>();
    public Task<WatchdogActionResult> StopAsync(string instanceName, string origin = "scheduler", CancellationToken cancellationToken = default) => Unused<Task<WatchdogActionResult>>();
    public Task<WatchdogActionResult> EnableAsync(string instanceName, CancellationToken cancellationToken = default) => Unused<Task<WatchdogActionResult>>();
    public Task<WatchdogActionResult> DisableAsync(string instanceName, CancellationToken cancellationToken = default) => Unused<Task<WatchdogActionResult>>();
    public Task<IReadOnlyList<string>> GetEnabledNamesAsync(CancellationToken cancellationToken = default) => Unused<Task<IReadOnlyList<string>>>();
    public Task<WatchdogActionResult> ForgetAsync(string instanceName, CancellationToken cancellationToken = default) => Unused<Task<WatchdogActionResult>>();
    public Task<WatchdogActionResult> SetCpuPriorityAsync(string instanceName, string priority, CancellationToken cancellationToken = default) => Unused<Task<WatchdogActionResult>>();
    public Task<IReadOnlyList<WatchdogInstanceState>> ListAsync(CancellationToken cancellationToken = default) => Unused<Task<IReadOnlyList<WatchdogInstanceState>>>();
    public Task<IReadOnlyList<WatchdogRunTimes>> GetRunTimesAsync(CancellationToken cancellationToken = default) => Unused<Task<IReadOnlyList<WatchdogRunTimes>>>();
    public IAsyncEnumerable<string> FollowConsoleAsync(string instanceName, CancellationToken cancellationToken = default) => Unused<IAsyncEnumerable<string>>();
    public Task<IReadOnlyList<string>> GetConsoleTailAsync(string instanceName, int lines, CancellationToken cancellationToken = default) => Unused<Task<IReadOnlyList<string>>>();
    public Task<IReadOnlyList<WatchdogConsoleRun>> GetConsoleRunsAsync(string instanceName, CancellationToken cancellationToken = default) => Unused<Task<IReadOnlyList<WatchdogConsoleRun>>>();
    public Task<IReadOnlyList<string>> GetConsoleRunTailAsync(string instanceName, int lines, int run, CancellationToken cancellationToken = default) => Unused<Task<IReadOnlyList<string>>>();
    public Task<WatchdogConsoleWindow> GetConsoleWindowAsync(string instanceName, int lines, int run, long endOffset, CancellationToken cancellationToken = default) => Unused<Task<WatchdogConsoleWindow>>();
    public Task<WatchdogConsoleDownload?> OpenConsoleDownloadAsync(string instanceName, int run, CancellationToken cancellationToken = default) => Unused<Task<WatchdogConsoleDownload?>>();
    public Task<WatchdogUpnpList?> GetUpnpAsync(string instanceName, CancellationToken cancellationToken = default) => Unused<Task<WatchdogUpnpList?>>();
}
