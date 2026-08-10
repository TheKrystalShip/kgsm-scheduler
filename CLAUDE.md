# CLAUDE.md — kgsm-scheduler

## What this is

`kgsm-scheduler` is a **resident leaf daemon** in the KGSM ecosystem. It reads
per-instance schedule config from kgsm (via `kgsm-lib`) and issues **atomic restarts
through the watchdog** at wall-clock time (daily / weekly / 6h cadences, in the
instance's configured IANA timezone). It also takes scheduled backups, and sweeps the
roster on an interval for newer game builds.

It is a **leaf**: it depends only on `kgsm-lib` (which reaches `kgsm` and the
watchdog), never on `kgsm-api` or a sibling leaf. It runs fully standalone,
co-located with a `kgsm`. The API's use of it is optional/additive — it reads the
status socket and degrades gracefully when the daemon is absent.

Scheduled-restart / auto-backup ownership is the scheduler's; the **watchdog** still
owns autostart + crash-restart + CPU/mem caps. See `tks/server-settings-plan.md` and
`system-architecture.md`.

## Key files

- `src/Scheduler/Program.cs` — host bootstrap: wires `AddKgsmServices` +
  `AddKgsmWatchdogClient`, registers the two hosted services.
- `src/Scheduler/SchedulerEngine.cs` — the wall-clock `BackgroundService`. Polls
  instance config every `PollIntervalSeconds`, computes each instance's next fire in
  its timezone, and fires `IWatchdogClient.RestartAsync(name, "scheduler", ct)` when
  due. A `GraceWindowMinutes` guard skips fires that are too overdue (host was down)
  to prevent catch-up storms. Also holds `SchedulerInstanceStatus` /
  `SchedulerStatusResponse` / `ScheduleState` records.
- `src/Scheduler/UpdateCheckSweep.cs` — the interval `BackgroundService` that asks every
  server whether a newer build exists, via `IInstanceService.CheckUpdate(name, emit: true)`.
  Separate from the engine because the two answer to different clocks: a restart fires at a
  wall-clock time in the server's timezone, a sweep runs on an interval with no meaningful
  time of day. Serial and staggered — each server asks its own upstream, so a parallel sweep
  is N simultaneous steamcmd logins in the same second. Consults the engine's `checked_at`
  first and skips anything checked within **half** the interval, so restarting the daemon
  does not re-ask every upstream (see the doc comment for why half).
- `src/Scheduler/ScheduleRegistry.cs` — thread-safe snapshot store shared between the
  engine (writer) and the status socket (reader).
- `src/Scheduler/StatusSocketServer.cs` — `BackgroundService` that serves the current
  status snapshot as one NDJSON line per connection over a unix socket. Health =
  connect + parse.
- `src/Scheduler/SchedulerSettings.cs` — the configuration surface, shaped 1:1 to the
  `Scheduler` section of `kgsm-scheduler.settings.json` and bound in one step. Holds
  what was *written*, unvalidated.
- `src/Scheduler/SchedulerOptions.cs` — the validated form the daemon runs on.
  `FromSettings` clamps out-of-range numbers and falls back on blanks, so a hand-edited
  value degrades instead of taking the daemon down. Injected as `IOptions<SchedulerOptions>`.
- `kgsm-scheduler.settings.json` — declares the whole configurable surface with defaults.
  Copied to publish output, installed beside the binary. An environment variable overrides
  one key by spelling its path with `__` (`Scheduler__PollIntervalSeconds`); a variable
  naming a key this file does not declare binds to nothing.
- `deploy/kgsm-scheduler.env.example` — the annotated operator env file; `setup.sh` seeds
  `/etc/kgsm-scheduler/kgsm-scheduler.env` from it.
- `CONFIGURATION.md` — full reference for all settings, env vars, and defaults.
- `src/Scheduler/Json/SchedulerJsonContext.cs` — source-generated JSON context
  (AOT: all serialization goes through this).
- `systemd/kgsm-scheduler.service` — the service unit.
- `deploy/setup.sh` + `deploy/deploy.sh` + `deploy/deploy-common.sh` — see **Deploying** below.

## Build

```bash
dotnet build kgsm-scheduler.slnx
```

## Deploying

```bash
./deploy/setup.sh    # ONCE per host. Asks for sudo. Idempotent, re-runnable.
./deploy/deploy.sh   # every deploy. NO sudo, NO prompts.
```

`setup.sh` provisions the host: chowns `/opt/kgsm-scheduler` to you, seeds the env file, puts the
real unit in **user-owned** `/etc/kgsm-scheduler/systemd/` with
`/etc/systemd/system/kgsm-scheduler.service` symlinked to it, installs a polkit rule scoped to this
project's units, enables the unit, then verifies the grant by making the same unprivileged
`systemctl` calls the deploy will.

That is what makes `deploy.sh` **need no privilege at all**: the prefix is yours so installing the
AOT binary is a plain file write, a changed unit is a plain file write into the user-owned
directory, and every `systemctl` verb goes through the polkit grant. It refuses **before building**,
with *"run `deploy/setup.sh`"*, on an unprovisioned host, and verifies the result by connecting to
the status socket and reading a line — the daemon's own definition of healthy, not just
`is-active`. If some *other* operation seems to need root, stop and ask; don't reintroduce `sudo`.

`deploy-common.sh` holds the paths/units/helpers both scripts share. The three files are
self-contained, so a standalone clone deploys with no other repo checked out; every `kgsm-*` repo
carries this same pattern.

## AOT publish (must be 0 ILC warnings)

```bash
dotnet publish src/Scheduler/Scheduler.csproj -c Release -r linux-x64   # expect 0 IL2026/IL3050/ILC warnings
```

This is a **Native AOT** project (`PublishAot=true`). Constraints: no reflection, no
`Activator.CreateInstance`, no `dynamic`; every serialized type must be registered in
`SchedulerJsonContext`. `TimeZoneInfo.FindSystemTimeZoneById` is AOT-safe on Linux
(reads `/usr/share/zoneinfo`; `InvariantGlobalization=true` does not break it).

## Interop

- Schedule config (`ScheduledRestart`, `RestartTime`, `RestartDay`, `Timezone` on
  `Instance`) is read via `IInstanceService` from `kgsm-lib` — kgsm config is the
  source of truth.
- Restarts are issued via `IWatchdogClient.RestartAsync` — never shell out to
  `kgsm.sh` or open the watchdog socket directly.
- Update availability is **kgsm's fact, not the scheduler's**. The sweep calls
  `check-update --emit` and the engine decides what is worth announcing: it records the
  upstream version beside the instance and emits `instance_update_available` only for one it
  has not announced before. Nothing here compares versions, remembers an answer, or writes an
  audit row.

## Status socket

Default `/run/kgsm-scheduler/status.sock` (`Scheduler__StatusSocketPath`). One NDJSON
line per connect: `{ "instances": [ { name, scheduledRestart, restartTime, restartDay,
timezone, nextFireUtc, lastRunUtc, lastRunOk, lastRunMessage, lastBackupUtc, lastBackupOk,
lastBackupMessage, backupSchedule, backupTime, backupDay, nextBackupUtc, lastUpdateCheckUtc,
lastUpdateCheckOk, lastUpdateCheckMessage } ] }`. This is what `kgsm-api` connects to for
the `/settings` aggregation.

The snapshot is rebuilt by the engine's tick, so an outcome written by an operation that
finishes between ticks appears at the next one — up to `PollIntervalSeconds` later.

⚠ `lastUpdateCheckUtc` is **the sweep's own attempt**, not when the upstream was last
fetched. A server skipped as recently-checked is null here while the engine holds a real
`checked_at` for it, and a failed attempt has a time here with no new `checked_at` there. A
surface answering *"when was this last checked for updates"* wants the engine's `checked_at`
from the status read; these three fields answer *"is the sweep working, and what failed"*.
