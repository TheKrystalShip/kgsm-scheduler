namespace TheKrystalShip.Kgsm.Scheduler;

internal sealed class SchedulerOptions
{
    public string KgsmPath { get; init; } = "/usr/bin/kgsm";
    public string WatchdogSocketPath { get; init; } = "/run/kgsm-watchdog/control.sock";
    public string StatusSocketPath { get; init; } = "/run/kgsm-scheduler/status.sock";
    /// <summary>How often to re-scan instance schedule config (seconds).</summary>
    public int PollIntervalSeconds { get; init; } = 60;
    /// <summary>
    /// If a scheduled fire is more than this many minutes late (host was down, daemon was stopped),
    /// skip it rather than firing a surprise restart. Prevents catch-up storms.
    /// </summary>
    public int GraceWindowMinutes { get; init; } = 10;
}
