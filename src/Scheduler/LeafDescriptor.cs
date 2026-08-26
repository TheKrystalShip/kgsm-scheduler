using TheKrystalShip.KGSM.LeafConfig;

// What the Control Panel shows about this daemon, declared beside the configuration it describes.
// TheKrystalShip.KGSM.LeafConfig reads this out of the built assembly and writes
// deploy/kgsm-scheduler.leaf.json; deploy.sh installs that into /var/lib/kgsm/leaves/scheduler.json,
// where kgsm-api scans for it. The daemon itself never reads any of this.

[assembly: Leaf(
    id: "scheduler",
    displayName: "Scheduler",
    unit: "kgsm-scheduler.service",
    role: "Runs each server's maintenance windows — backups and restarts, in order, announced and "
        + "exclusive — and sweeps the roster for newer game builds.")]

[assembly: LeafGroup("general", "General", 1)]
[assembly: LeafGroup("wiring", "Connections", 2)]
[assembly: LeafGroup("timing", "Timing", 3)]
[assembly: LeafGroup("policy", "Host policy", 4)]
[assembly: LeafGroup("updates", "Game updates", 5)]

// Lowest precedence first — the same order the daemon resolves them in.
[assembly: LeafFloorSource("appsettings", "/opt/kgsm-scheduler/kgsm-scheduler.settings.json")]
[assembly: LeafFloorSource("systemd-unit", "/etc/kgsm-scheduler/systemd/kgsm-scheduler.service")]
[assembly: LeafFloorSource("env-file", "/etc/kgsm-scheduler/kgsm-scheduler.env")]

[assembly: LeafFrameworkNamespace("Logging__",
    "per-category filtering is open-ended: any category name is a valid key")]

[assembly: LeafFrameworkField("logLevel", "Logging__LogLevel__Default", "Log level",
    Description = "Minimum severity this leaf logs.",
    Group = "general",
    Type = LeafType.Enum,
    Values = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"])]
