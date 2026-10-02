using TheKrystalShip.KGSM.ComponentConfig;

// What the Control Panel shows about this daemon, declared beside the configuration it describes.
// TheKrystalShip.KGSM.ComponentConfig reads this out of the built assembly and writes
// deploy/kgsm-scheduler.leaf.json; deploy.sh installs that into /var/lib/kgsm/leaves/scheduler.json,
// where kgsm-api scans for it. The daemon itself never reads any of this.

[assembly: Leaf(
    id: "scheduler",
    displayName: "Scheduler",
    unit: "kgsm-scheduler.service",
    role: "Runs each server's maintenance windows — backups and restarts, in order, announced and "
        + "exclusive — and sweeps the roster for newer game builds.")]

// What a person may do with this daemon's schedule, through the node's API: the API checks them before
// it relays to the socket, so they are declared here, where they are performed, and checked there.
[assembly: Action("scheduler:windows.read", "See the maintenance schedule and what last ran",
    DeclaredEffect.Read, DeclaredScope.Node)]
[assembly: Action("scheduler:windows.write", "Postpone, skip or run a maintenance window now",
    DeclaredEffect.Execute, DeclaredScope.Node)]

[assembly: ConfigGroup("general", "General", 1)]
[assembly: ConfigGroup("wiring", "Connections", 2)]
[assembly: ConfigGroup("timing", "Timing", 3)]
[assembly: ConfigGroup("policy", "Host policy", 4)]
[assembly: ConfigGroup("updates", "Game updates", 5)]

// Lowest precedence first — the same order the daemon resolves them in.
[assembly: ConfigFloorSource("appsettings", "/opt/kgsm-scheduler/kgsm-scheduler.settings.json")]
[assembly: ConfigFloorSource("systemd-unit", "kgsm-scheduler.service")]
[assembly: ConfigFloorSource("env-file", "/etc/kgsm-scheduler/kgsm-scheduler.env")]

[assembly: ConfigFrameworkNamespace("Logging__",
    "per-category filtering is open-ended: any category name is a valid key")]

[assembly: ConfigFrameworkField("logLevel", "Logging__LogLevel__Default", "Log level",
    Description = "Minimum severity this leaf logs.",
    Group = "general",
    Type = ConfigType.Enum,
    Values = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"])]
