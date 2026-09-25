# CLAUDE.md — kgsm-scheduler

## What this is

`kgsm-scheduler` is a **resident leaf daemon** in the KGSM ecosystem. It reads each instance's
**maintenance windows** from kgsm (via `kgsm-lib`) and runs them at their appointed time — archiving
and updating through kgsm, restarting and parking through the watchdog. It also sweeps the roster on an
interval for newer game builds, which is what records the upstream version an update window then acts
on.

It is a **leaf**: it depends only on `kgsm-lib` (which reaches `kgsm` and the watchdog), never on
`kgsm-api` or a sibling leaf. It runs fully standalone, co-located with a `kgsm`. The API's use of it
is optional/additive — it reads this daemon's socket and degrades gracefully when the daemon is absent.

Maintenance ownership is the scheduler's; the **watchdog** owns autostart + crash-restart + CPU/mem
caps. The per-instance config keys the scheduler reads, and every setting and env var, are in
`CONFIGURATION.md`; ecosystem topology is `tks/system-architecture.md`.

**The daemon's rules — the window run, the tasks, the restart gate, announcing, the socket and its
verbs — are `src/Scheduler/CLAUDE.md`.** Deploying is `deploy/CLAUDE.md`.

## The maintenance window

A **maintenance window** is one appointment plus an ordered set of tasks. An instance holds a *list* of
them, packed into its `maintenance_windows` config value:

```
maintenance_windows="daily@05:00/backup;weekly.sun@04:00/backup,restart"
                     └──── window ────┘ └─────────── window ──────────┘
                     schedule  tasks     schedule       tasks
```

**Dependency is what a window is for; independence is what having several is for.** Tasks inside one
window are ordered and dependent — a failure aborts the rest of it. Windows are independent
appointments that happen to touch the same instance and carry no state between them. Making window 2
rely on window 1 means merging them.

**A window's id is its schedule expression.** `weekly.sun@04:00` names it for postpone, skip, run-now
and announcement bookkeeping — unique within an instance, stable across edits to the task set, stored
nowhere because it is derived. Editing the schedule produces a *different* window, which is what makes
anything announced about the old one retractable.

**The grammar, the parser and the clock live in `kgsm-lib`** (`TheKrystalShip.KGSM.Core.Scheduling`).
It is the ecosystem's one implementation and its one validator, so the API's preview and this daemon's
fires cannot disagree about what an expression means. This repo holds only what is its own: whether
*this host* will fire a window, how far apart its fires are, and how late one may be.

**Tasks run in fixed canonical order — `backup` → `update` → `restart` — whatever order they were
written in.** The correct order is a property of what the tasks are, not of how somebody typed them: a
backup taken after an update archives the new build instead of the rollback point. **A failed task
aborts the rest of the window**; the remainder are recorded `aborted`. A partially-run window is worse
than a skipped one.

**A park belongs to the task that needs one.** The engine refuses to update a running instance, so
`update` is the one task that needs a span in which the server stays stopped — and it holds that span
around the engine call alone. The archive before it runs against a live server, because kgsm records
the state an archive was captured in. **One park is one bring-up:** the release drains and respawns the
instance, which is the whole of what a restart is, so a `restart` standing after it is already
delivered rather than bounced a second time.

## Interop

- Window config (`MaintenanceWindows`, `Timezone`, `BackupRetention` on `Instance`) is read via
  `IInstanceService` from `kgsm-lib` — kgsm config is the source of truth, and
  `MaintenanceWindowParser` is the only thing that reads the packed value.
- Restarts are issued via `IWatchdogClient.RestartAsync`, and the update's park through
  `BeginMaintenanceAsync`/`EndMaintenanceAsync` — never shell out to `kgsm.sh` or open the watchdog
  socket directly.
- Update availability is **kgsm's fact, not the scheduler's**. The sweep calls `check-update --emit`
  and the engine decides what is worth announcing: it records the upstream version beside the instance
  and emits `instance_update_available` only for one it has not announced before. Nothing here
  compares versions, remembers an answer, or writes an audit row. The `update` task reads that same
  record back — through the fast fleet status read, which touches no network — and is the only thing
  that acts on it.

## Build and AOT publish

```bash
dotnet build kgsm-scheduler.slnx
dotnet publish src/Scheduler/Scheduler.csproj -c Release -r linux-x64   # expect 0 IL2026/IL3050/ILC warnings
```

This is a **Native AOT** project (`PublishAot=true`). Constraints: no reflection, no
`Activator.CreateInstance`, no `dynamic`; every serialized type must be registered in
`src/Scheduler/Json/SchedulerJsonContext.cs`. `TimeZoneInfo.FindSystemTimeZoneById` is AOT-safe on
Linux (reads `/usr/share/zoneinfo`; `InvariantGlobalization=true` does not break it).

`src/Scheduler/kgsm-scheduler.settings.json` declares the whole configurable surface with defaults and
is installed beside the binary. An environment variable overrides one key by spelling its path with
`__` (`Scheduler__PollIntervalSeconds`); a variable naming a key this file does not declare binds to
nothing.

## Version tracking

- **Version source:** `<Version>` in `src/Scheduler/Scheduler.csproj`.
- **Packaging reads it via `deploy/version.sh`** — `./deploy/version.sh` prints the declared version,
  `--pkgver` prints the pacman-safe form. A package never restates a version number; it asks for one.
- Bump the version whenever you make a user-facing change (patch for fixes, minor for features, major
  for breaking changes), with a `CHANGELOG.md` entry under `## [Unreleased]`.
