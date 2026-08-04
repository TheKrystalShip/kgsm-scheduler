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

    /// <summary>
    /// Validates what configuration supplied and produces the form the daemon runs on. Out-of-range
    /// numbers are clamped and blank strings fall back to the coded default, so a hand-edited value
    /// degrades to something workable instead of taking the daemon down at startup.
    /// </summary>
    public static SchedulerOptions FromSettings(SchedulerSettings s)
    {
        var defaults = new SchedulerOptions();
        return new SchedulerOptions
        {
            KgsmPath = Or(s.KgsmPath, defaults.KgsmPath),
            WatchdogSocketPath = Or(s.WatchdogSocketPath, defaults.WatchdogSocketPath),
            StatusSocketPath = Or(s.StatusSocketPath, defaults.StatusSocketPath),
            PollIntervalSeconds = Math.Max(s.PollIntervalSeconds ?? defaults.PollIntervalSeconds, MinPollIntervalSeconds),
            GraceWindowMinutes = Math.Max(s.GraceWindowMinutes ?? defaults.GraceWindowMinutes, 0),
        };
    }

    private static string Or(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
