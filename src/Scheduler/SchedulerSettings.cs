using TheKrystalShip.KGSM.ComponentConfig;

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
[ConfigSection(Section)]
internal sealed class SchedulerSettings
{
    /// <summary>The configuration section this type binds to.</summary>
    public const string Section = "Scheduler";

    /// <summary>Path to the KGSM executable each server's schedule is read from. Checked at startup;
    /// the daemon refuses to run if nothing is there.</summary>
    /// <panel>Path to the KGSM executable, which the scheduler reads each server's schedule from. It is
    /// checked at startup, and the daemon refuses to run if nothing is there.</panel>
    [ConfigField("kgsmPath", "KGSM executable", Group = "wiring", Type = ConfigType.Path, Risk = ConfigRisk.Wiring)]
    public string KgsmPath { get; set; } = "/usr/bin/kgsm";

    /// <summary>The watchdog control socket every scheduled restart is issued through. It has to
    /// match the path the watchdog listens on, or nothing scheduled ever fires.</summary>
    /// <panel>The watchdog's control socket, which every scheduled restart is issued through. It has to
    /// match the path the watchdog listens on, or nothing scheduled ever fires.</panel>
    [ConfigField("watchdogSocket", "Watchdog control socket", Group = "wiring", Type = ConfigType.Path,
        Risk = ConfigRisk.Wiring)]
    public string WatchdogSocketPath { get; set; } = "/run/kgsm-watchdog/control.sock";

    /// <summary>Unix socket this daemon serves its schedule snapshot and its verbs on, over HTTP.
    /// A read is a <c>GET</c> and an instruction is a <c>POST</c>, so one socket carries both without
    /// either having to wait on the other.</summary>
    /// <panel>Unix socket the scheduler answers on. The Control Panel reads what is scheduled here, and
    /// sends the instructions that defer a window through the same socket.</panel>
    [ConfigField("socket", "Scheduler socket", Group = "wiring", Type = ConfigType.Path,
        Risk = ConfigRisk.Wiring, PairedApiKey = "Api__SchedulerSocketPath")]
    public string SocketPath { get; set; } = "/run/kgsm-scheduler/scheduler.sock";

    /// <summary>Unix socket this daemon answers for ITSELF on — its configuration, its unit, its
    /// journal and the commands it declares.</summary>
    /// <panel>Unix socket the Control Panel reaches this service's own configuration and journal
    /// through. Moving it makes the panel read these settings off disk instead, which still works and
    /// cannot apply a change while the service is up.</panel>
    [ConfigField("surfaceSocket", "Own-surface socket", Group = "wiring", Type = ConfigType.Path,
        Risk = ConfigRisk.Wiring)]
    public string SurfaceSocketPath { get; set; } = "/run/kgsm-scheduler/surface.sock";

    /// <summary>The env file a configuration change made through the panel is written to.</summary>
    /// <panel>Where a setting changed in the Control Panel is written. It has to be a file this
    /// service's unit loads with EnvironmentFile= — the panel checks, and reports the settings as
    /// read-only rather than writing a change nothing would read.</panel>
    [ConfigField("configOverridePath", "Override file", Group = "wiring", Type = ConfigType.Path,
        Risk = ConfigRisk.Wiring)]
    public string ConfigOverridePath { get; set; } = "/var/lib/kgsm-api/leaf-overrides/scheduler.env";

    /// <summary>Directory this daemon keeps state in that must survive a restart of it — which
    /// announcements have already been made about a restart that has not happened yet.</summary>
    /// <panel>Directory the scheduler keeps its own state in. It remembers which servers have already
    /// been told a restart is coming, so a restart of the scheduler does not repeat the warnings or
    /// forget that it promised them.</panel>
    [ConfigField("stateDirectory", "State directory", Group = "wiring", Type = ConfigType.Path,
        Risk = ConfigRisk.Wiring)]
    public string StateDirectory { get; set; } = "/var/lib/kgsm-scheduler";

    /// <summary>How often each server's schedule is re-read from KGSM (seconds). Raised to
    /// <see cref="SchedulerOptions.MinPollIntervalSeconds"/> if lower.</summary>
    /// <panel>How often each server's schedule is re-read from KGSM. This bounds how quickly a schedule
    /// change takes effect; it does not affect the accuracy of a fire that is already scheduled.</panel>
    [ConfigField("pollIntervalSec", "Schedule re-scan interval", Group = "timing",
        Min = SchedulerOptions.MinPollIntervalSeconds, Unit = "s")]
    public int? PollIntervalSeconds { get; set; }

    /// <summary>How late a missed maintenance window may be and still run (minutes). Capped at half
    /// the window's own period and floored at one poll.</summary>
    /// <panel>How late a missed maintenance window may be and still run. Anything later is skipped, so a
    /// host that was down does not come back to a burst of catch-up work. A window that comes round more
    /// often than twice this gets half its own period instead, so a frequent window can never have two
    /// occurrences owed at once.</panel>
    [ConfigField("graceWindowMin", "Missed-fire grace window", Group = "timing", Min = 0, Unit = "min")]
    public int? GraceWindowMinutes { get; set; }

    /// <summary>The shortest period this host permits a maintenance window to have (minutes). A window
    /// that comes round more often is reported as one this host will not fire.</summary>
    /// <panel>How often maintenance is allowed to happen at all on this host. A window asking to run more
    /// frequently than this is reported as one that will not fire, rather than firing anyway — the floor
    /// is the host's answer, not the server's.</panel>
    [ConfigField("minWindowPeriodMin", "Minimum window period", Group = "policy",
        Min = SchedulerOptions.MinWindowPeriodMinutes, Unit = "min")]
    public int? MinimumWindowPeriodMinutes { get; set; }

    /// <summary>Whether maintenance that interrupts the people on a server may run on this host at
    /// all.</summary>
    /// <panel>Whether maintenance that interrupts the people on a server — a restart, or an update — may
    /// run on this host at all. Off leaves backups running as normal; anything disruptive is recorded as
    /// skipped and the windows carrying it are not announced, since there would be nothing true to
    /// announce.</panel>
    [ConfigField("allowDisruptiveTasks", "Allow disruptive maintenance", Group = "policy",
        Type = ConfigType.Bool)]
    public bool? AllowDisruptiveTasks { get; set; }

    /// <summary>Whether to sweep every server for a newer game build. Off means nothing on this host
    /// ever asks, and no update is announced.</summary>
    /// <panel>Whether to check each server for a newer game build. Turning this off means nothing on
    /// this host ever asks upstream, so no update notification is raised — servers keep running
    /// exactly as they are.</panel>
    [ConfigField("updateCheckEnabled", "Check for game updates", Group = "updates", Type = ConfigType.Bool)]
    public bool? UpdateCheckEnabled { get; set; }

    /// <summary>How often the whole roster is swept for updates (minutes). Raised to
    /// <see cref="SchedulerOptions.MinUpdateCheckIntervalMinutes"/> if lower.</summary>
    /// <panel>How often every server is checked for a newer build. A game release is not a fast-moving
    /// fact and each check costs a real request to the game's upstream, so hourly is generous.</panel>
    [ConfigField("updateCheckIntervalMin", "Update check interval", Group = "updates",
        Min = SchedulerOptions.MinUpdateCheckIntervalMinutes, Unit = "min")]
    public int? UpdateCheckIntervalMinutes { get; set; }

    /// <summary>Pause between one server's update check and the next, within a sweep (seconds).</summary>
    /// <panel>How long to wait between checking one server and the next. Each server asks its own
    /// upstream, so this spreads the requests out instead of sending them all in the same second.</panel>
    [ConfigField("updateCheckStaggerSec", "Update check stagger", Group = "updates", Min = 0, Unit = "s")]
    public int? UpdateCheckStaggerSeconds { get; set; }
}
