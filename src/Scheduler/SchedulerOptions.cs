namespace TheKrystalShip.Kgsm.Scheduler;

internal sealed class SchedulerOptions
{
    /// <summary>The shortest poll interval the engine accepts. Below this the timer it drives is invalid
    /// (a zero or negative period throws), so a hand-edited value is raised to this floor rather than
    /// taking the daemon down at startup.</summary>
    public const int MinPollIntervalSeconds = 5;

    public string KgsmPath { get; init; } = "/usr/bin/kgsm";
    public string WatchdogSocketPath { get; init; } = "/run/kgsm-watchdog/control.sock";
    public string StatusSocketPath { get; init; } = "/run/kgsm-scheduler/status.sock";
    /// <summary>How often to re-scan instance schedule config (seconds). At least <see cref="MinPollIntervalSeconds"/>.</summary>
    public int PollIntervalSeconds { get; init; } = 60;
    /// <summary>
    /// If a scheduled fire is more than this many minutes late (host was down, daemon was stopped),
    /// skip it rather than firing a surprise restart. Prevents catch-up storms.
    /// </summary>
    public int GraceWindowMinutes { get; init; } = 10;
}
