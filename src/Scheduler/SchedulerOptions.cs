namespace TheKrystalShip.Kgsm.Scheduler;

internal sealed class SchedulerOptions
{
    /// <summary>The shortest poll interval the engine accepts. Below this the timer it drives is invalid
    /// (a zero or negative period throws), so a hand-edited value is raised to this floor rather than
    /// taking the daemon down at startup.</summary>
    public const int MinPollIntervalSeconds = 5;

    /// <summary>
    /// The shortest update-check interval the sweep accepts. Every check is a real request to a game's
    /// upstream and the roster is walked one server at a time, so a sweep set tighter than this could
    /// still be running when the next one is due — and the floor also keeps a hand-edited zero from
    /// throwing the timer it drives.
    /// </summary>
    public const int MinUpdateCheckIntervalMinutes = 5;

    /// <summary>
    /// The shortest window period this daemon accepts as a host floor. It is the same floor the
    /// grammar itself enforces, so a host cannot set a policy that permits something the parser
    /// refuses; a host that wants maintenance to be rarer than this raises its own number.
    /// </summary>
    public const int MinWindowPeriodMinutes = 10;

    public string KgsmPath { get; init; } = "/usr/bin/kgsm";
    public string WatchdogSocketPath { get; init; } = "/run/kgsm-watchdog/control.sock";
    public string StatusSocketPath { get; init; } = "/run/kgsm-scheduler/status.sock";
    public string ControlSocketPath { get; init; } = "/run/kgsm-scheduler/control.sock";
    public string StateDirectory { get; init; } = "/var/lib/kgsm-scheduler";
    /// <summary>How often to re-scan instance schedule config (seconds). At least <see cref="MinPollIntervalSeconds"/>.</summary>
    public int PollIntervalSeconds { get; init; } = 60;
    /// <summary>
    /// If a scheduled fire is more than this many minutes late (host was down, daemon was stopped),
    /// skip it rather than firing a surprise restart. Prevents catch-up storms.
    /// </summary>
    public int GraceWindowMinutes { get; init; } = 10;

    /// <summary>
    /// The shortest period this host permits a maintenance window to have (minutes). A window that
    /// comes round more often is reported as one this host will not fire. At least
    /// <see cref="MinWindowPeriodMinutes"/>.
    /// </summary>
    public int MinimumWindowPeriodMinutes { get; init; } = MinWindowPeriodMinutes;

    /// <summary>
    /// Whether maintenance that interrupts the people on a server may run here at all. False leaves
    /// backups running and records every disruptive task as skipped with that reason.
    /// </summary>
    public bool AllowDisruptiveTasks { get; init; } = true;

    /// <summary>Whether to sweep the roster for newer game builds at all.</summary>
    public bool UpdateCheckEnabled { get; init; } = true;

    /// <summary>
    /// How often the whole roster is swept (minutes). Hourly by default: a game release is not a
    /// fast-moving fact, and the cost of a sweep is linear in the number of servers because each one
    /// asks its own upstream. At least <see cref="MinUpdateCheckIntervalMinutes"/>.
    /// </summary>
    public int UpdateCheckIntervalMinutes { get; init; } = 60;

    /// <summary>
    /// Pause between one server's check and the next. The sweep is serial by design — firing every
    /// check at once means N simultaneous steamcmd logins in the same second — and this spreads what
    /// remains out further.
    /// </summary>
    public int UpdateCheckStaggerSeconds { get; init; } = 5;

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
            ControlSocketPath = Or(s.ControlSocketPath, defaults.ControlSocketPath),
            StateDirectory = Or(s.StateDirectory, defaults.StateDirectory),
            PollIntervalSeconds = Math.Max(s.PollIntervalSeconds ?? defaults.PollIntervalSeconds, MinPollIntervalSeconds),
            GraceWindowMinutes = Math.Max(s.GraceWindowMinutes ?? defaults.GraceWindowMinutes, 0),
            MinimumWindowPeriodMinutes = Math.Max(
                s.MinimumWindowPeriodMinutes ?? defaults.MinimumWindowPeriodMinutes,
                MinWindowPeriodMinutes),
            AllowDisruptiveTasks = s.AllowDisruptiveTasks ?? defaults.AllowDisruptiveTasks,
            UpdateCheckEnabled = s.UpdateCheckEnabled ?? defaults.UpdateCheckEnabled,
            UpdateCheckIntervalMinutes = Math.Max(
                s.UpdateCheckIntervalMinutes ?? defaults.UpdateCheckIntervalMinutes,
                MinUpdateCheckIntervalMinutes),
            UpdateCheckStaggerSeconds = Math.Max(
                s.UpdateCheckStaggerSeconds ?? defaults.UpdateCheckStaggerSeconds, 0),
        };
    }

    private static string Or(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
