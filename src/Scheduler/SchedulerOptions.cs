namespace TheKrystalShip.Kgsm.Scheduler;

internal sealed class SchedulerOptions
{
    public string KgsmPath { get; init; } =
        Environment.GetEnvironmentVariable("KGSM_SCHEDULER_KGSM_PATH") ?? "/usr/bin/kgsm";

    public string KgsmSocketPath { get; init; } =
        Environment.GetEnvironmentVariable("KGSM_SCHEDULER_KGSM_SOCKET") ?? "/run/kgsm/events.sock";

    public string WatchdogSocketPath { get; init; } =
        Environment.GetEnvironmentVariable("KGSM_SCHEDULER_WATCHDOG_SOCKET") ?? "/run/kgsm-watchdog/control.sock";

    public string StatusSocketPath { get; init; } =
        Environment.GetEnvironmentVariable("KGSM_SCHEDULER_STATUS_SOCKET") ?? "/run/kgsm-scheduler/status.sock";

    /// <summary>How often to re-scan instance schedule config (seconds).</summary>
    public int PollIntervalSeconds { get; init; } =
        int.TryParse(Environment.GetEnvironmentVariable("KGSM_SCHEDULER_POLL_INTERVAL"), out var v) ? v : 60;

    /// <summary>
    /// If a scheduled fire is more than this many minutes late (host was down, daemon was stopped),
    /// skip it rather than firing a surprise restart. Prevents catch-up storms.
    /// </summary>
    public int GraceWindowMinutes { get; init; } =
        int.TryParse(Environment.GetEnvironmentVariable("KGSM_SCHEDULER_GRACE_WINDOW_MINUTES"), out var g) ? g : 10;
}
