# CLAUDE.md — kgsm-scheduler

## What this is

`kgsm-scheduler` is a **resident leaf daemon** in the KGSM ecosystem. It reads each
instance's **maintenance windows** from kgsm (via `kgsm-lib`) and runs them at their
appointed time — archiving through kgsm and restarting through the watchdog. It also
sweeps the roster on an interval for newer game builds.

It is a **leaf**: it depends only on `kgsm-lib` (which reaches `kgsm` and the
watchdog), never on `kgsm-api` or a sibling leaf. It runs fully standalone,
co-located with a `kgsm`. The API's use of it is optional/additive — it reads the
status socket and degrades gracefully when the daemon is absent.

Maintenance ownership is the scheduler's; the **watchdog** owns autostart +
crash-restart + CPU/mem caps. The per-instance config keys the scheduler reads are
documented in `CONFIGURATION.md`; ecosystem topology is `tks/system-architecture.md`.

## The maintenance window

A **maintenance window** is one appointment plus an ordered set of tasks. An instance
holds a *list* of them, packed into its `maintenance_windows` config value:

```
maintenance_windows="daily@05:00/backup;weekly.sun@04:00/backup,restart"
                     └──── window ────┘ └─────────── window ──────────┘
                     schedule  tasks     schedule       tasks
```

**Dependency is what a window is for; independence is what having several is for.**
Tasks inside one window are ordered and dependent — a failure aborts the rest of it.
Windows are independent appointments that happen to touch the same instance and carry
no state between them. Making window 2 rely on window 1 means merging them.

**A window's id is its schedule expression.** `weekly.sun@04:00` names it for postpone,
skip, run-now and announcement bookkeeping — unique within an instance, stable across
edits to the task set, stored nowhere because it is derived. Editing the schedule
produces a *different* window, which is what makes anything announced about the old one
retractable.

**The grammar, the parser and the clock live in `kgsm-lib`**
(`TheKrystalShip.KGSM.Core.Scheduling`). It is the ecosystem's one implementation and
its one validator, so the API's preview and this daemon's fires cannot disagree about
what an expression means. This repo holds only what is its own: whether *this host* will
fire a window, how far apart its fires are, and how late one may be.

**Tasks run in fixed canonical order — `backup` → `restart` — whatever order they were
written in.** The correct order is a property of what the tasks are, not of how somebody
typed them. **A failed task aborts the rest of the window**; the remainder are recorded
`aborted`. A partially-run window is worse than a skipped one.

**Nothing holds the instance down.** A backup runs against a live server — kgsm records
the state an archive was captured in — and a restart is one atomic watchdog transition.
No task needs a span in which the instance stays stopped, so no window parks one.

## Key files

- `src/Scheduler/Program.cs` — host bootstrap: wires `AddKgsmServices` +
  `AddKgsmWatchdogClient`, registers each task and the hosted services.
- `src/Scheduler/SchedulerEngine.cs` — the wall-clock `BackgroundService`. Polls
  instance config every `PollIntervalSeconds`, holds each window's standing target in
  its timezone, announces the ones approaching, and opens the ones that have come due.
  A grace guard drops fires too overdue to run (host was down) to prevent catch-up storms.
- `src/Scheduler/IMaintenanceTask.cs` — the task contract, the gate's tri-state, the
  outcome vocabulary, and the catalog of what this daemon can run.
- `src/Scheduler/BackupTask.cs` / `RestartTask.cs` — the two tasks.
- `src/Scheduler/RestartGate.cs` — re-asserts the instance's state through
  `IWatchdogClient.GetStatusAsync` in the instant before a restart is dispatched, and
  abandons unless the watchdog measures the instance as running. See **The restart
  gate** below.
- `src/Scheduler/MaintenanceRunner.cs` — one window run: the exclusive slot, the tasks in
  order, the abort, the record. See **The window run** below.
- `src/Scheduler/WindowPlanner.cs` — the pure arithmetic that is this daemon's rather than
  kgsm-lib's: whether this host will fire a window, its period, its grace, its standing plan.
- `src/Scheduler/SchedulerStatus.cs` — the records the status socket serves, and the
  outcome vocabulary they are written in.
- `src/Scheduler/AnnouncementPlan.cs` — pure logic for what a server says before a window:
  reading the configured lead times, dropping the ones the window is too frequent to honour,
  picking which mark to speak, and resolving the message.
- `src/Scheduler/WindowAnnouncer.cs` — the countdown itself: speaking a mark, retracting an
  abandoned one, and the measured-empty-roster gate. See **Announcing a window** below.
- `src/Scheduler/PendingAnnouncementStore.cs` — remembers, across a restart of this daemon, which
  marks have already been spoken about which fire of which window.
- `src/Scheduler/UpdateCheckSweep.cs` — the interval `BackgroundService` that asks every
  server whether a newer build exists, via `IInstanceService.CheckUpdate(name, emit: true)`.
  Separate from the engine because the two answer to different clocks: a restart fires at a
  wall-clock time in the server's timezone, a sweep runs on an interval with no meaningful
  time of day. Serial and staggered — each server asks its own upstream, so a parallel sweep
  is N simultaneous steamcmd logins in the same second. Consults the engine's `checked_at`
  first and skips anything checked within **half** the interval, so restarting the daemon
  does not re-ask every upstream (see the doc comment for why half).
- `src/Scheduler/ScheduleRegistry.cs` — thread-safe state and snapshot store shared between
  the engine (writer), the runner (writer), the control socket (writer) and the status
  socket (reader). Every write is a read-modify-write under one lock, because a run that
  finishes between ticks and the tick itself both write the same record.
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

- Window config (`MaintenanceWindows`, `Timezone`, `BackupRetention` on `Instance`) is
  read via `IInstanceService` from `kgsm-lib` — kgsm config is the source of truth, and
  `MaintenanceWindowParser` is the only thing that reads the packed value.
- Restarts are issued via `IWatchdogClient.RestartAsync` — never shell out to
  `kgsm.sh` or open the watchdog socket directly.
- Update availability is **kgsm's fact, not the scheduler's**. The sweep calls
  `check-update --emit` and the engine decides what is worth announcing: it records the
  upstream version beside the instance and emits `instance_update_available` only for one it
  has not announced before. Nothing here compares versions, remembers an answer, or writes an
  audit row.

## The window run

One window run is one exclusive, announced, abort-on-failure sequence against one instance:

1. **Claim the instance's slot.** One window per instance at a time — a backup can outlive
   several ticks on a large game, and a restart must not land in the middle of one. A window
   that finds the slot held is recorded **`skipped` on itself**, with that reason, and the
   countdown it opened is retracted. Never `failed` — nothing failed — and never in another
   window's fields.
2. **Run the tasks in canonical order.** Each is gated immediately before dispatch (below),
   then run.
3. **The first failure aborts the rest.** The remaining tasks are recorded `aborted`.
4. **Release the slot and write the record** — one `lastRun` against the window that ran.

The run happens off the tick, so a long backup on one instance cannot hold up every other
instance's schedule. The slot is claimed synchronously as the tick dispatches, so two windows
of the same instance coming due on one tick resolve deterministically.

**Outcome vocabulary — `ok` · `failed` · `skipped` · `aborted`.** Four words rather than a
boolean, because "did the maintenance work" has four genuinely different answers:

- **`ok`** — it was owed and it happened.
- **`failed`** — it was owed and it did not happen. This is the one a surface raises.
- **`skipped`** — it did not apply to the instance as it stood. The clock came round for a
  server an operator had already stopped, and declining is the correct act, so it is recorded
  with its reason rather than raised.
- **`aborted`** — an earlier task in the same window failed, so this one never got its turn.

The window as a whole is `failed` if any task failed, `ok` if any task did its work, and
`skipped` when nothing applied.

## A task

```csharp
interface IMaintenanceTask
{
    string Name { get; }                  // the grammar token
    bool IsDisruptive { get; }            // drives whether the window is announced
    Task<TaskGate> GateAsync(Instance instance, IWatchdogClient watchdog, CancellationToken ct);
    Task<TaskOutcome> RunAsync(MaintenanceContext ctx, CancellationToken ct);
}
```

**This is the extension point.** A task is one class here, one grammar token in kgsm-lib's
parser, one entry in the API's token set and one toggle in the Control Panel. It adds no
cadence, no plan, and no field on the status socket — a window already reports one row per
task it ran.

`IsDisruptive` carries two facts at once, and they are the same fact: a task that interrupts
the people on a server is a task the **watchdog** performs, and the watchdog supervises native
instances alone. That is what makes a container's restart something its **gate** declines rather
than something the window is refused for — the archive written beside it still fires.

**`backup`** — ungated, and it leaves the instance exactly as it is. `CreateBackup(reason:
scheduled)` then `PruneBackups(retention)`, and the prune only after an archive that landed:
pruning around a failed one would drop a good archive to make room for something never
written. A prune that refuses does not fail the backup — the archive is the job and it landed
— but it travels on the record, because a rotation that quietly stops running fills a disk.

**`restart`** — `IWatchdogClient.RestartAsync`, one atomic transition the watchdog already
owns: it drains and respawns without incrementing the crash-recovery streak, and never leaves
the instance in a state where desired-state says stopped.

## What this host will fire

What leaves a window with nothing to fire **at all** is decided when the window is read, once,
and reported as an invalid window with the reason — never discovered as a failed run every week.
An operator who writes a window this host cannot honour hears about it on the next poll.

- The expression does not parse. kgsm-lib's own error travels, because it names the offending
  text.
- The window comes round more often than `MinimumWindowPeriodMinutes` permits.
- The window names a task this daemon does not run. That is a fact about the host rather than
  about one server, so it is stated once, here.

What one *particular instance* cannot run is a different question, and the task's own gate answers
it in the instant before dispatch. A container instance is the live case: every disruptive task is
issued through the watchdog, which supervises native instances alone, so a container's `restart` is
declined with that reason while the `backup` written beside it in the same window still fires. It
is declined rather than failed because nothing was owed — the restart was never going to happen —
and because failing would abort the rest of the window.

⚠ This daemon is the runtime backstop, not the only check. The API refuses an impossible window
when somebody writes it; every other writer — the CLI, the assistant, a script — reaches kgsm
directly, and this is what stands behind them.

**Validity is per window.** An invalid one disables itself and leaves the instance's other
windows firing. It appears in the snapshot as `valid: false` with its `error` and a null
`nextFireUtc` — never absent, and the two together are what distinguish it from a window that
is simply not due.

⚠ It also degrades the leaf's **`config`** component, alongside `watchdog` and `kgsm`. This
leaf fails more quietly than any other in the ecosystem: everything it does is something that
was supposed to happen, so maintenance that never runs produces no event and no absence anybody
notices. Degrading is what makes "there is maintenance here that is never going to happen" a
fact the leaf's own health states.

**A host policy is not a misconfiguration.** With `AllowDisruptiveTasks` off, a window carrying
a restart still fires and still takes its backup; the restart is recorded `skipped` with the
policy as its reason, and the window is not announced — there would be nothing true to announce.
The same holds for a container's restart: both are known before the countdown would open, and a
countdown that can only end in a retraction never starts.

## The restart gate

A due restart is dispatched only after the instance's state is re-read from the watchdog in that
instant. The clock decides *when*; only the watchdog knows what the instance is doing by then, and
restarting the wrong thing is worse than not restarting.

Two watchdog behaviours are what make the re-assert load-bearing. `StartAsync` is an operator
override that clears the give-up latch and the failure streak, so dispatching into an instance the
supervisor has given up on wipes its crash history on a timer. And a stop of an instance the daemon
does not track succeeds as a no-op, so an ungated restart of a deliberately stopped server runs
straight into the start half and spawns it.

The gate dispatches on exactly one reading — phase `running` with a populated cgroup — and abandons
on everything else, recording which kind of abandonment it was:

- **`failed` — the restart was owed and did not happen.** The watchdog did not answer, so this
  instance's state is unknown — never read as "not running", because nothing measured it. This
  aborts the rest of the window.
- **`skipped` — the restart does not apply.** The instance is not supervised (so it is not running),
  the watchdog has given up on it, it is mid-way through a phase of its own, or it is a container
  and the watchdog supervises native instances alone. Declining is the correct outcome, so it is
  recorded with its reason rather than raised, and the window carries on.

Backups are ungated: kgsm records the state an archive was captured in, so a scheduled backup is
valid whatever the instance is doing.

## Announcing a window

At each lead time an instance declares (`announce_lead_minutes`, e.g. `15,5,1`), the engine tells the
people on that server that maintenance is coming, through the game's own console via
`IInstanceService.Announce`. The text is the instance's `announce_maintenance_message` with
`{minutes}`, `{reason}` and `{instance}` resolved; the engine then substitutes *that* into the game's
own broadcast template. Two substitutions, different placeholders, different owners — which is why a
message containing `{message}` needs no special handling here.

**The window is announced, not the task.** `{reason}` is resolved from the window's disruptive tasks
that this host permits: `restart` alone reads *"restarting"*, a window carrying `update` reads
*"updating and restarting"*, because an update implies the restart that makes it the running build.
**A window with nothing disruptive left in it is never announced** — there is no true sentence to say
about a nightly archive that interrupts nobody.

**The bookkeeping is keyed `(instance, window)`.** One instance can have two windows counting down at
once, and each is announced about, and retracted, on its own.

⚠ **A lead at or above a window's own period is dropped, and the drop is reported.** The smallest
due mark is the only true one of several — but that holds because marks come due in descending
order, and they only do so while the period exceeds the largest lead. On a ten-minute window with
leads `15,5,1`, the first tick after a fire already has 15 due, so the server would be told
*"in 15 minutes"* nine minutes before it happens, every time. Such a lead is dropped and the daemon
says so once per configuration, rather than silently honouring fewer leads than an operator wrote.

**Announcing is opt-in and every reason to stay quiet is normal.** No lead times, no
`broadcast_command` for the game, or no message each mean the maintenance happens exactly as it
otherwise would, unannounced. None is an error, and none blocks the window.

**Several marks that fall due at once speak once, as the smallest.** A daemon that was down arrives to
find 15, 5 and 1 all passed. The smallest is the only true statement of the three, so it is the one
spoken and the rest are spent without being said — a queue would count the fire upward.

**A mark is spent whether or not its announcement was delivered.** A send that failed will fail again
next tick, and retrying would turn one undeliverable warning into one per tick until the fire.

**What was said survives a restart of this daemon.** `pending-announcements.json` in
`Scheduler__StateDirectory` records which marks were spoken about which fire of which window. An entry
is a *debt* — it exists only while something has been said about a fire that has not happened — and
never a schedule: the schedule is re-derived from the instance's config every tick, so deleting the
file costs nothing but the memory of what was already announced.

**An announced window that does not happen is retracted**, with
`announce_maintenance_cancelled_message`. That covers the gate declining every disruptive task, the
window being deleted mid-countdown, the fire being too overdue to run, the instance being busy with
another window, and the target moving under a postponement, a skip or an edited schedule. A warning
followed by silence is worse than no warning: players leave for a restart that never comes and nothing
tells them otherwise.

⚠ **A server with nobody on it is not announced to — but only when the watchdog can actually see its
players.** `GetPlayerPresenceAsync` reporting `IsDetected` with an empty roster is a measured
absence. An unreachable daemon, an untracked instance, or one whose players cannot be observed at all
are each announced to anyway: "no players detected" and "detection unavailable" are different facts,
and reading the second as the first silences a server full of people.

⚠ **Delivered means the engine wrote to the console, never that a person read it.**

## Status socket

Default `/run/kgsm-scheduler/status.sock` (`Scheduler__StatusSocketPath`). One NDJSON line per
connect. This is what `kgsm-api` connects to for its own aggregation:

```json
{ "instances": [ {
  "name": "factorio-01",
  "timezone": "Europe/Madrid",
  "windows": [ {
    "id": "weekly.sun@04:00",
    "kind": "appointment",
    "tasks": ["backup", "restart"],
    "valid": true,
    "error": null,
    "nextFireUtc": "2026-08-30T02:00:00+00:00",
    "lastRun": {
      "startedUtc": "2026-08-23T02:00:00+00:00",
      "finishedUtc": "2026-08-23T02:07:41+00:00",
      "outcome": "failed",
      "tasks": [
        { "name": "backup",  "outcome": "failed",  "message": "no space left on device" },
        { "name": "restart", "outcome": "aborted", "message": "a prior task in this window failed" }
      ]
    }
  } ],
  "lastUpdateCheckUtc": "2026-08-26T22:11:03+00:00",
  "lastUpdateCheckOk": true,
  "lastUpdateCheckMessage": null
} ] }
```

`kind` is `appointment` or `interval`. `lastRun` is null for a window that has not run since this
daemon started — the record lives in memory, not on disk.

The snapshot is rebuilt by the engine's tick, so an outcome written by a run that finishes between
ticks appears at the next one — up to `PollIntervalSeconds` later.

⚠ `lastUpdateCheckUtc` is **the sweep's own attempt**, not when the upstream was last
fetched. A server skipped as recently-checked is null here while the engine holds a real
`checked_at` for it, and a failed attempt has a time here with no new `checked_at` there. A
surface answering *"when was this last checked for updates"* wants the engine's `checked_at`
from the status read; these three fields answer *"is the sweep working, and what failed"*.

## Control socket

Default `/run/kgsm-scheduler/control.sock` (`Scheduler__ControlSocketPath`). One NDJSON request
per connection, one reply, then closed:

```
→ {"command":"postpone","instance":"factorio-01","window":"daily@04:00","minutes":60}
← {"ok":true,"message":"postponed 60 minute(s)","nextFireUtc":"2026-08-13T05:00:00+00:00"}
```

| verb | arguments | what it does |
|---|---|---|
| `postpone` | `instance`, `window`, `minutes` (1–720, default 60) | pushes this window's next run back |
| `skip` | `instance`, `window` | drops this occurrence; the one after it is unaffected |
| `run-now` | `instance`, `window` | brings this window forward to the next poll |

`minutes` is capped at 720: past that it is a schedule change, and a schedule change belongs in the
instance's own config where it survives a restart of this daemon.

**A verb names its window.** One instance holds several appointments, and moving the wrong one is
worse than refusing — so an instruction naming no window is refused with the ids it could have named.

**Every verb moves a standing target; none edits a schedule.** The instance's kgsm config is
untouched, so the fire *after* the one acted on lands exactly where it always would have — which is
what makes these "not tonight" and "just this once" rather than reschedules, and why they need
nothing from kgsm. The move is applied under the registry's lock, so a tick landing mid-write cannot
overwrite the new target with the one it read a moment ago. It survives ticks because the plan is
kept while its signature matches, and moving a target does not change the signature.

`run-now` moves the target to the present rather than starting a run itself, so the window goes
through exactly the sequence a scheduled one does — the same exclusive slot, the same gates, the same
record. A second path into a run would be a second set of rules about when one is allowed to happen.

**None of it survives a restart of this daemon.** The standing target lives in the in-memory
registry, so a restart recomputes it from the instance's config and the deferred fire comes back.
That is the honest consequence of not editing the schedule, and it is the right trade for verbs that
mean "not for the next hour".

A moved target is a different fire from the one anybody was warned about: the engine retracts the
warnings already given and announces the new countdown from scratch. What persists across a restart
of this daemon is only what was *said*, never the schedule — so the deferred fire coming back also
brings back an unannounced countdown, announced afresh.

⚠ **The daemon enforces no authorization here, and the shipped command manifest says so** (`gates`
bucket `none`). A unix socket carries no identity; the only restriction is the filesystem permission
on the socket — the same posture as the status socket. A caller that wants a tier check owes it
itself — `kgsm-api` gates its buttons at operator before it dials this.

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
