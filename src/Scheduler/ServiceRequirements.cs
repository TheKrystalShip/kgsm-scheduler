using TheKrystalShip.KGSM;
using TheKrystalShip.KGSM.ComponentConfig;

// What this daemon does to the engine as its own service account. Nothing here is done for a person in
// front of it: every call is a window or a sweep coming round on the clock, and each runs only where both
// this account and the person who wrote the window, or switched the sweep on, may do it.

[assembly: Requires(KgsmActions.ServerRead, DeclaredScope.Instance,
    "Read each server's maintenance windows, run state and update evidence on every poll")]
[assembly: Requires(KgsmActions.ServerBackupsCreate, DeclaredScope.Instance,
    "Take the backups a maintenance window asks for")]
[assembly: Requires(KgsmActions.ServerBackupsManage, DeclaredScope.Instance,
    "Prune old backups to the retention a server keeps, after a scheduled backup lands")]
[assembly: Requires(KgsmActions.ServerUpdate, DeclaredScope.Instance,
    "Apply a newer game build in a maintenance window")]
[assembly: Requires(KgsmActions.ServerRestart, DeclaredScope.Instance,
    "Restart a server in a maintenance window")]
[assembly: Requires(KgsmActions.ServerAnnounce, DeclaredScope.Instance,
    "Warn the players on a server before a maintenance window interrupts them")]
