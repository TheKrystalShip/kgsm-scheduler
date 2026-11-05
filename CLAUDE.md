# CLAUDE.md — kgsm-scheduler

`kgsm-scheduler` is a resident leaf daemon: it reads each instance's maintenance windows from kgsm (through
the engine core), runs them at their appointed time (archiving and updating through the `kgsm` binary, restarting and parking
through the watchdog), and sweeps the roster for newer game builds. It depends only on the engine packages and
degrades honestly without the watchdog. Role, contracts and peers: `../kgsm-docs/components/kgsm-scheduler.md`.

- The window model, grammar, run, tasks, gates and announcing:
  `../kgsm-docs/systems/operations/maintenance-windows.md`, `reference/maintenance-windows.md`,
  `systems/operations/announcements.md`.
- The daemon's code-level rules: `src/Scheduler/CLAUDE.md`. Deploying: `deploy/CLAUDE.md`.
- Every setting and per-instance config key: `CONFIGURATION.md`.

## Interop

- Window config (`MaintenanceWindows`, `Timezone`, `BackupRetention` on `Instance`) is read via
  `EngineReader` (behind this daemon's `IEngine` seam); `MaintenanceWindowParser` (engine contracts) is the only thing that reads the packed value.
- Restarts go through `IWatchdogClient.RestartAsync`, the update's park through
  `BeginMaintenanceAsync`/`EndMaintenanceAsync`. Engine writes run the `kgsm` binary through `EngineCli` as `system:scheduler`; never open the watchdog socket by hand.
- Update availability is kgsm's fact. The sweep calls `check-update --emit` and the engine decides what is
  worth announcing (`server.update.available`, once per upstream version). Nothing here compares versions,
  remembers an answer or writes an audit row. The `update` task reads the recorded evidence back through
  the fast fleet status read, which touches no network.

## Build and AOT publish

```bash
dotnet build kgsm-scheduler.slnx
dotnet publish src/Scheduler/Scheduler.csproj -c Release -r linux-x64   # expect 0 IL2026/IL3050/ILC warnings
```

Native AOT (`PublishAot=true`): no reflection, no `Activator.CreateInstance`, no `dynamic`; every serialized
type is registered in `src/Scheduler/Json/SchedulerJsonContext.cs`. `TimeZoneInfo.FindSystemTimeZoneById`
is AOT-safe on Linux (it reads `/usr/share/zoneinfo`; `InvariantGlobalization=true` does not break it).

`src/Scheduler/kgsm-scheduler.settings.json` declares the whole configurable surface with defaults and is
installed beside the binary. An environment variable overrides one key by spelling its path with `__`
(`Scheduler__PollIntervalSeconds`); a variable naming an undeclared key binds to nothing.

## Version tracking

- The version is `<Version>` in `src/Scheduler/Scheduler.csproj`; `./deploy/version.sh` prints it (`--pkgver`
  for the pacman form).
- Bump for every user-facing change (patch for fixes, minor for features, major for breaking changes), with
  a `CHANGELOG.md` entry under `## [Unreleased]`, and tag it `git tag -a v<version> -m "kgsm-scheduler v<version>"`.

## Read before changing

- `../kgsm-docs/systems/operations/maintenance-windows.md`, `reference/maintenance-windows.md`.
- `../kgsm-docs/systems/authorization/`: who a task runs as.
- `../kgsm-docs/reference/leaf-config-descriptor.md`, `logging-convention.md`.

Docs and comments describe the system as it is now: present tense, no history, no emoji, no volatile
numbers (workspace `../CLAUDE.md`).
