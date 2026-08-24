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

Scheduled-restart / auto-backup ownership is the scheduler's; the **watchdog**
owns autostart + crash-restart + CPU/mem caps. The per-instance config keys the
scheduler reads are documented in `CONFIGURATION.md`; ecosystem topology is
`tks/system-architecture.md`.

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
- `src/Scheduler/ControlSocketServer.cs` — the socket the daemon can be *told* something
  on: one NDJSON request in, one reply out. A second socket rather than a second use of
  the first, because the status socket's contract is that a client connects and only
  reads — teaching it to wait for an optional request would put a timeout in front of
  every status read to serve a command that arrives rarely. See **Control socket** below.
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
- `deploy/kgsm-scheduler.service` — the service unit.
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

## Control socket

Default `/run/kgsm-scheduler/control.sock` (`Scheduler__ControlSocketPath`). One NDJSON request
per connection, one reply, then closed:

```
→ {"command":"postpone","instance":"factorio-01","minutes":60}
← {"ok":true,"message":"postponed 60 minute(s)","nextFireUtc":"2026-08-13T05:00:00+00:00"}
```

`postpone` is the only command. `minutes` defaults to 60 and is capped at 720: past that it is a
schedule change, and a schedule change belongs in the instance's own config where it survives a
restart of this daemon.

**It moves the standing target, it does not edit the schedule.** The instance's kgsm config is
untouched, so the fire *after* the postponed one lands exactly where it always would have — which is
what makes this "not tonight" rather than a reschedule, and why it needs nothing from kgsm. The move
is applied under the registry's lock, so a tick landing mid-write cannot overwrite the new target
with the one it read a moment ago. It survives ticks because `Plan()` keeps a standing plan while its
signature matches, and a postponement does not change the signature.

⚠ **The daemon enforces no authorization here, and the shipped command manifest says so** (`gates`
bucket `none`). A unix socket carries no identity; the only restriction is the filesystem permission
on the socket — the same posture as the status socket. A caller that wants a
tier check owes it itself — `kgsm-api` gates its Postpone button at operator before it dials this.

**A postponement does not survive a restart of this daemon.** The standing target lives in the
in-memory registry, so a restart recomputes it from the instance's config and the deferred fire comes
back. That is the honest consequence of not editing the schedule, and it is the right trade for a
verb that means "not for the next hour".

## Version tracking

- **Version source:** `<Version>` in `src/Scheduler/Scheduler.csproj`
- **Packaging reads it via `deploy/version.sh`** — `./deploy/version.sh` prints the declared version, `--pkgver` prints the pacman-safe form. A package never restates a version number; it asks for one.
- Bump the version whenever you make a user-facing change (new feature, bug fix, behaviour change). Patch for fixes, minor for new features, major for breaking changes.
- Update `CHANGELOG.md` under `## [Unreleased]` with a brief entry for every meaningful change.
- A git tag matching the new version should be created on release: `git tag v<version>`.

## Documentation & comments: present-tense canon only

Prose in this repo — every doc, `README`/`CLAUDE.md` section, and in-code comment — describes
**how the thing works right now**, nothing else. History lives in the `CHANGELOG` and git
history; never duplicate it into docs or code.

- **No transitions.** Never "was X, now Y", "used to…", "changed from…", "no longer…", or any
  before/after framing. State the current rule flat: a sentence that only makes sense to a reader
  who knows what the code *used to* do is dead weight, because that "before" no longer exists
  anywhere in the code.
- **Tombstones leave no marker.** When something is removed — dying naturally as part of the work,
  or explicitly asked to be deleted — the removal is silent: no *"removed X"*, no *"X is gone"*,
  no *"deprecated, use Y instead"* pointing at a corpse. The prose reads as if it never was. Code
  kept while the thing that justified it was deleted gets a live present-tense reason to exist —
  or goes too.
- **No residue of the active work.** References only meaningful *during* a piece of work don't
  survive it: *"temporary shim for the rework"*, *"added to satisfy the new requirement"*,
  milestone/phase labels (*"per M2"*, *"the Phase 1 step"*). If a line's justification is the work
  that produced it rather than the system as it now stands, it goes.
- **No volatile numbers.** Counts and versions that drift — how many projects/files/tests/
  partials exist, a dependency's pinned version, a file's line count — never go in prose: they are
  stale the moment anything changes, and nothing fails to remind anyone. Name the authoritative
  source instead (the csproj, the directory, the barrel file). A number belongs in prose only when
  it *is* the contract (a port, a timeout, a cap) or a measured fact that is itself the reason a
  design exists.
- **Edits are replacements, not appends.** When changing an existing feature, rewrite the affected
  doc/comment fresh as if writing it for the first time — never append a correction under the
  stale version, and never leave the stale version standing beside the new. The current revision
  does not converse with prior revisions.

A reader six months from now should learn the system from the doc without knowing what it
replaced. If you catch yourself explaining a change, stop — that sentence belongs in the commit
message. When touching prose that already violates this, rewrite it to present-tense canon in
passing.
