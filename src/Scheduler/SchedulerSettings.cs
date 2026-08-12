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

    /// <summary>Unix socket the scheduler takes instructions on, one NDJSON request and one reply per
    /// connection. Separate from the status socket, whose contract is that a client only ever reads.</summary>
    /// <panel>Unix socket the scheduler takes instructions on — postponing a scheduled restart, for
    /// instance. Separate from the status socket, which is read-only.</panel>
    [LeafField("controlSocket", "Control socket", Group = "wiring", Type = LeafType.Path,
        Risk = LeafRisk.Wiring, PairedApiKey = "Api__SchedulerControlSocketPath")]
    public string ControlSocketPath { get; set; } = "/run/kgsm-scheduler/control.sock";

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

    /// <summary>Whether to sweep every server for a newer game build. Off means nothing on this host
    /// ever asks, and no update is announced.</summary>
    /// <panel>Whether to check each server for a newer game build. Turning this off means nothing on
    /// this host ever asks upstream, so no update notification is raised — servers keep running
    /// exactly as they are.</panel>
    [LeafField("updateCheckEnabled", "Check for game updates", Group = "updates", Type = LeafType.Bool)]
    public bool? UpdateCheckEnabled { get; set; }

    /// <summary>How often the whole roster is swept for updates (minutes). Raised to
    /// <see cref="SchedulerOptions.MinUpdateCheckIntervalMinutes"/> if lower.</summary>
    /// <panel>How often every server is checked for a newer build. A game release is not a fast-moving
    /// fact and each check costs a real request to the game's upstream, so hourly is generous.</panel>
    [LeafField("updateCheckIntervalMin", "Update check interval", Group = "updates",
        Min = SchedulerOptions.MinUpdateCheckIntervalMinutes, Unit = "min")]
    public int? UpdateCheckIntervalMinutes { get; set; }

    /// <summary>Pause between one server's update check and the next, within a sweep (seconds).</summary>
    /// <panel>How long to wait between checking one server and the next. Each server asks its own
    /// upstream, so this spreads the requests out instead of sending them all in the same second.</panel>
    [LeafField("updateCheckStaggerSec", "Update check stagger", Group = "updates", Min = 0, Unit = "s")]
    public int? UpdateCheckStaggerSeconds { get; set; }
}
