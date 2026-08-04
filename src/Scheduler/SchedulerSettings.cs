using TheKrystalShip.KGSM.LeafConfig;

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
/// <see cref="SchedulerOptions.FromSettings"/>, so the daemon starts with something sane rather
/// than not at all.
/// <para>
/// Both numbers are <b>nullable</b>, and null means "not written" — the coded default in
/// <see cref="SchedulerOptions"/> applies. Two binder behaviours make this load-bearing rather than
/// stylistic: a blank value (<c>Scheduler__PollIntervalSeconds=</c>, a single stray line in an env
/// file) binds to a non-nullable <see cref="int"/> by throwing, taking the daemon down at startup;
/// and a JSON null binds to <c>0</c>, silently discarding the default a property initializer here
/// would have carried. Nullable turns both into "unset". A value that is present but is not a
/// number still fails loudly, which is the point of typing it at all.
/// </para>
/// </remarks>
[LeafSection(Section)]
internal sealed class SchedulerSettings
{
    /// <summary>The configuration section this type binds to.</summary>
    public const string Section = "Scheduler";

    /// <summary>Path to the KGSM executable each server's schedule is read from. Checked at startup;
    /// the daemon refuses to run if nothing is there.</summary>
    /// <panel>Path to the KGSM executable, which the scheduler reads each server's schedule from. It is
    /// checked at startup, and the daemon refuses to run if nothing is there.</panel>
    [LeafField("kgsmPath", "KGSM executable", Group = "wiring", Type = LeafType.Path, Risk = LeafRisk.Wiring)]
    public string KgsmPath { get; set; } = "/usr/bin/kgsm";

    /// <summary>The watchdog control socket every scheduled restart is issued through. It has to
    /// match the path the watchdog listens on, or nothing scheduled ever fires.</summary>
    /// <panel>The watchdog's control socket, which every scheduled restart is issued through. It has to
    /// match the path the watchdog listens on, or nothing scheduled ever fires.</panel>
    [LeafField("watchdogSocket", "Watchdog control socket", Group = "wiring", Type = LeafType.Path,
        Risk = LeafRisk.Wiring)]
    public string WatchdogSocketPath { get; set; } = "/run/kgsm-watchdog/control.sock";

    /// <summary>Unix socket the schedule snapshot is served on, one NDJSON line per connection.</summary>
    /// <panel>Unix socket the scheduler serves its schedule snapshot on. The Control Panel reads it here
    /// to show what is scheduled and when it last ran.</panel>
    [LeafField("statusSocket", "Status socket", Group = "wiring", Type = LeafType.Path,
        Risk = LeafRisk.Wiring, PairedApiKey = "Api__SchedulerSocketPath")]
    public string StatusSocketPath { get; set; } = "/run/kgsm-scheduler/status.sock";

    /// <summary>How often each server's schedule is re-read from KGSM (seconds). Raised to
    /// <see cref="SchedulerOptions.MinPollIntervalSeconds"/> if lower.</summary>
    /// <panel>How often each server's schedule is re-read from KGSM. This bounds how quickly a schedule
    /// change takes effect; it does not affect the accuracy of a fire that is already scheduled.</panel>
    [LeafField("pollIntervalSec", "Schedule re-scan interval", Group = "timing",
        Min = SchedulerOptions.MinPollIntervalSeconds, Unit = "s")]
    public int? PollIntervalSeconds { get; set; }

    /// <summary>How late a missed restart may be and still run (minutes). Anything later is skipped.</summary>
    /// <panel>How late a missed restart may be and still run. Anything later is skipped, so a host that
    /// was down does not come back to a burst of catch-up restarts. Zero always runs missed
    /// restarts.</panel>
    [LeafField("graceWindowMin", "Missed-fire grace window", Group = "timing", Min = 0, Unit = "min")]
    public int? GraceWindowMinutes { get; set; }
}
