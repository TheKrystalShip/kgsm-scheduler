namespace TheKrystalShip.Kgsm.Scheduler;

/// <summary>
/// The scheduler's configuration surface, shaped 1:1 to the <c>Scheduler</c> section of
/// <c>kgsm-scheduler.settings.json</c>. Every knob the daemon has is a property here and a key
/// there; nothing is read by string lookup, so a knob cannot exist in one place and not the other.
/// An environment variable overrides one key by spelling its path with <c>__</c>
/// (<c>Scheduler__PollIntervalSeconds</c>).
/// </summary>
/// <remarks>
/// This type holds what was <em>written</em>, not what the daemon runs on: values arrive
/// unvalidated, exactly as the file or the environment spelled them. <see cref="SchedulerOptions"/>
/// is the validated form — clamping and fallbacks live in
/// <see cref="SchedulerOptions.FromSettings"/>, so binding never fails on a hand-edited value and
/// the daemon starts with something sane instead of not at all.
/// </remarks>
internal sealed class SchedulerSettings
{
    /// <summary>The configuration section this type binds to.</summary>
    public const string Section = "Scheduler";

    /// <summary>Path to the KGSM executable each server's schedule is read from. Checked at startup;
    /// the daemon refuses to run if nothing is there.</summary>
    public string KgsmPath { get; set; } = "/usr/bin/kgsm";

    /// <summary>The watchdog control socket every scheduled restart is issued through. It has to
    /// match the path the watchdog listens on, or nothing scheduled ever fires.</summary>
    public string WatchdogSocketPath { get; set; } = "/run/kgsm-watchdog/control.sock";

    /// <summary>Unix socket the schedule snapshot is served on, one NDJSON line per connection.</summary>
    public string StatusSocketPath { get; set; } = "/run/kgsm-scheduler/status.sock";

    /// <summary>How often each server's schedule is re-read from KGSM (seconds). Raised to
    /// <see cref="SchedulerOptions.MinPollIntervalSeconds"/> if lower.</summary>
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>How late a missed restart may be and still run (minutes). Anything later is skipped.</summary>
    public int GraceWindowMinutes { get; set; } = 10;
}
