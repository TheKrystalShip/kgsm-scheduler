namespace TheKrystalShip.Kgsm.Scheduler;

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
    /// result. Scheduled operations run off the tick and finish whenever they finish, so the tick
    /// and a completing operation both write this record — each must merge into what the other
    /// left rather than overwrite it with a stale copy.
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
}
