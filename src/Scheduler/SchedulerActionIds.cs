namespace TheKrystalShip.Kgsm.Scheduler;

/// <summary>The ids of the actions this daemon declares.</summary>
internal static class SchedulerActionIds
{
    /// <summary>See the maintenance schedule and what last ran.</summary>
    public const string WindowsRead = "scheduler:windows.read";

    /// <summary>Postpone, skip or run a maintenance window now.</summary>
    public const string WindowsWrite = "scheduler:windows.write";
}
