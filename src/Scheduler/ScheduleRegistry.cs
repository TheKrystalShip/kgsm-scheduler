using TheKrystalShip.KGSM.Core.Scheduling;

namespace TheKrystalShip.Kgsm.Scheduler;

/// <summary>
/// One window as this daemon holds it between ticks: what it is, the timezone its appointment is
/// read in, its standing target, and the last run it produced.
/// </summary>
/// <remarks>
/// The window and the timezone travel with the state so that anything moving the target — the tick,
/// or an instruction on the control socket — computes the fire after it from the same window the
/// tick planned, rather than re-reading the id through a second parse.
/// </remarks>
internal sealed record WindowState(
    MaintenanceWindow Window,
    TimeZoneInfo Timezone,
    WindowPlan Plan,
    MaintenanceRun? LastRun);

/// <summary>
/// Per-instance state the engine carries between ticks.
/// </summary>
/// <remarks>
/// The windows are keyed by their id, which is their schedule expression. A window whose schedule
/// is edited is a different window and takes a different key, so nothing about the old one is
/// carried into the new — including what it last did.
/// </remarks>
internal sealed record ScheduleState
{
    /// <summary>Every window this instance currently declares, by id.</summary>
    public IReadOnlyDictionary<string, WindowState> Windows { get; init; } =
        new Dictionary<string, WindowState>(StringComparer.Ordinal);

    /// <summary>When the update sweep last tried this instance.</summary>
    public DateTimeOffset? LastUpdateCheckUtc { get; init; }

    /// <summary>Whether that attempt succeeded.</summary>
    public bool? LastUpdateCheckOk { get; init; }

    /// <summary>What went wrong with it, in one line.</summary>
    public string? LastUpdateCheckMessage { get; init; }
}

/// <summary>
/// Thread-safe snapshot store shared between <see cref="SchedulerEngine"/> (writer)
/// and <see cref="StatusSocketServer"/> (reader).
/// </summary>
internal sealed class ScheduleRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, ScheduleState> _states = new(StringComparer.Ordinal);
    private SchedulerStatusResponse _snapshot = new([]);

    public SchedulerStatusResponse Snapshot
    {
        get { lock (_lock) return _snapshot; }
        set { lock (_lock) _snapshot = value; }
    }

    public ScheduleState? Get(string name)
    {
        lock (_lock) return _states.GetValueOrDefault(name);
    }

    public void Set(string name, ScheduleState state)
    {
        lock (_lock) _states[name] = state;
    }

    /// <summary>
    /// Applies <paramref name="mutate"/> to an instance's state under the lock and returns the
    /// result. Maintenance runs happen off the tick and finish whenever they finish, so the tick
    /// and a completing run both write this record — each must merge into what the other left
    /// rather than overwrite it with a stale copy.
    /// </summary>
    public ScheduleState Update(string name, Func<ScheduleState, ScheduleState> mutate)
    {
        lock (_lock)
        {
            var updated = mutate(_states.GetValueOrDefault(name) ?? new ScheduleState());
            _states[name] = updated;
            return updated;
        }
    }

    /// <summary>
    /// Applies <paramref name="mutate"/> to one window of one instance, leaving every other window
    /// alone. A window this instance does not declare is left absent — nothing here invents one.
    /// </summary>
    /// <returns>The window as it now stands, or null when the instance does not declare it.</returns>
    public WindowState? UpdateWindow(string name, string windowId, Func<WindowState, WindowState> mutate)
    {
        lock (_lock)
        {
            var state = _states.GetValueOrDefault(name);
            if (state is null || !state.Windows.TryGetValue(windowId, out var window)) return null;

            var updated = mutate(window);
            var windows = new Dictionary<string, WindowState>(state.Windows, StringComparer.Ordinal)
            {
                [windowId] = updated,
            };

            _states[name] = state with { Windows = windows };
            return updated;
        }
    }
}
