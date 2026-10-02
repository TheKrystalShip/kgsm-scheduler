# The scheduler daemon

`SchedulerEngine` is the wall-clock `BackgroundService`: it polls instance config every
`PollIntervalSeconds`, holds each window's standing target in its timezone, announces the ones
approaching, and opens the ones that have come due. A grace guard drops fires too overdue to run
(host was down) to prevent catch-up storms. `WindowPlanner` is the pure arithmetic that is this
daemon's rather than kgsm-lib's: whether this host will fire a window, its period, its grace, its
standing plan.

- **`ScheduleRegistry` is shared state with several writers** — the engine, the runner and
  `WindowControl` write, `/status` reads — and every write is a read-modify-write under one lock,
  because a run that finishes between ticks and the tick itself both write the same record.
- **`SchedulerSettings` holds what was written, unvalidated**, shaped 1:1 to the `Scheduler` section of
  `kgsm-scheduler.settings.json` and bound in one step. `SchedulerOptions.FromSettings` is the
  validated form the daemon runs on: it clamps out-of-range numbers and falls back on blanks, so a
  hand-edited value degrades instead of taking the daemon down.
- **`UpdateCheckSweep` is a separate interval service from the engine**, because the two answer to
  different clocks: a restart fires at a wall-clock time in the server's timezone, a sweep runs on an
  interval with no meaningful time of day. It asks every server via
  `IInstanceService.CheckUpdate(name, emit: true)`, serial and staggered — each server asks its own
  upstream, so a parallel sweep is N simultaneous steamcmd logins in the same second. It consults the
  engine's `checked_at` first and skips anything checked within **half** the interval, so restarting
  the daemon does not re-ask every upstream (the doc comment says why half). **It is off until a person
  switches it on**: `updateCheckEnabled` is `[Automates]`, so the surface records whoever sets it as its
  author (`ComponentAutomationAuthors`, from the account the node's API relays as
  `Kgsm-Acting-Account`), and each server is checked only while both this daemon's service account and
  that author may read it — asked at every sweep, a refusal recorded on the server as `blocked: …`.

## The window run (`MaintenanceRunner`)

One window run is one exclusive, announced, abort-on-failure sequence against one instance:

1. **Claim the instance's slot.** One window per instance at a time — a backup can outlive several
   ticks on a large game, and a restart must not land in the middle of one. A window that finds the
   slot held is recorded **`skipped` on itself**, with that reason, and the countdown it opened is
   retracted. Never `failed` — nothing failed — and never in another window's fields.
2. **Run the tasks in canonical order.** Each is authorized and then gated immediately before dispatch
   (below), then run.
3. **The first failure aborts the rest.** The remaining tasks are recorded `aborted`.
4. **Release the slot and write the record** — one `lastRun` against the window that ran.

**Every task runs only while two accounts may do it, now** (`AutomationAccess`): this daemon's own
service account, `svc:scheduler@<node>`, and the window's author — `maintenance_windows_author`, which
the engine records with the windows and clears on any windows write that names nobody. Both must hold
every one of the task's `Actions` at the server's install (`instance:<node>/<name>#<nonce>`), evaluated
from the node's replica (`Scheduler:AuthorityReplicaPath`, read and never written). So a window
restarts only what its author could restart by hand, an author who loses access stops their windows at
the next firing, and windows nobody is recorded as writing run nothing. A task refused is recorded
**`blocked`** with the reason — whose access, which action — and the window carries on: a backup nobody
may take says nothing about the restart after it. A replica that cannot be read, a node that has not
named itself and a service account not yet created block the same way; running on a guess is the one
thing an automation must not do. The same question is asked on every poll and carried on the window's
status (`author`, `blocked`), so a window is shown blocked before it is due, and a disruptive task that
would be blocked is never announced.

The run happens off the tick, so a long backup on one instance cannot hold up every other instance's
schedule. The slot is claimed synchronously as the tick dispatches, so two windows of the same
instance coming due on one tick resolve deterministically.

**Outcome vocabulary — `ok` · `failed` · `skipped` · `aborted` · `blocked`.** Five words rather than a
boolean, because "did the maintenance work" has five genuinely different answers:

- **`ok`** — it was owed and it happened.
- **`failed`** — it was owed and it did not happen. This is the one a surface raises.
- **`skipped`** — it did not apply to the instance as it stood. The clock came round for a server an
  operator had already stopped, and declining is the correct act, so it is recorded with its reason
  rather than raised.
- **`aborted`** — an earlier task in the same window failed, so this one never got its turn.
- **`blocked`** — nobody who may do it asked for it: the author or this daemon's account does not hold
  the task's action, or the windows have no author. Somebody has to act on it — saving the windows
  again makes the saver their author.

The window as a whole is `failed` if any task failed, `ok` if any task did its work, `blocked` if a task
was held back and none did its work, and `skipped` when nothing applied.

## A task (`IMaintenanceTask`)

```csharp
interface IMaintenanceTask
{
    string Name { get; }                  // the grammar token
    bool IsDisruptive { get; }            // drives whether the window is announced
    IReadOnlyList<string> Actions { get; } // what running it performs; author and service must hold each
    Task<TaskGate> GateAsync(Instance instance, IWatchdogClient watchdog, CancellationToken ct);
    Task<TaskOutcome> RunAsync(MaintenanceContext ctx, CancellationToken ct);
}
```

**This is the extension point.** A task is one class here, one grammar token in kgsm-lib's parser, one
entry in the API's token set and one toggle in the Control Panel. It adds no cadence, no plan, and no
field on `/status` — a window already reports one row per task it ran.

`IsDisruptive` carries two facts at once, and they are the same fact: a task that interrupts the
people on a server is a task the **watchdog** performs, and the watchdog supervises native instances
alone. That is what makes a container's restart something its **gate** declines rather than something
the window is refused for — the archive written beside it still fires.

**`backup`** — ungated, and it leaves the instance exactly as it is. `CreateBackup(reason: scheduled)`
then `PruneBackups(retention)`, and the prune only after an archive that landed: pruning around a
failed one would drop a good archive to make room for something never written. A prune that refuses
does not fail the backup — the archive is the job and it landed — but it travels on the record,
because a rotation that quietly stops running fills a disk. kgsm records the state an archive was
captured in, so a scheduled backup is valid whatever the instance is doing.

**`update`** — applies the newer game build, behind a watchdog park.

- **It dispatches only on the engine's recorded evidence that a newer build stands.** The reading is
  the one the update-check sweep left beside the instance, answered off disk with no upstream call;
  anything else — an up-to-date server, an upstream nothing has ever recorded — is a skip with that
  reason. Asking upstream here would cost a real steamcmd login before every update window, and an
  update with nothing to do is a server stopped for nothing.
- **The park is held around the engine call alone**, through
  `IWatchdogClient.BeginMaintenanceAsync`/`EndMaintenanceAsync`. Parked is stopped while still wanted
  running: crash-restart is suppressed for as long as the park holds, and the failure streak and the
  give-up latch come out of it as they went in. A stop/start pair from here would say instead that
  nobody wants the server up, and would strand it if this daemon died between the two.
- **The release is unconditional**, whatever the update came to. That is what makes "a window never
  leaves a server down" a property of the task rather than of the tasks that happen to follow it. A
  release the watchdog refuses fails the task, because the server is down and the window owes that
  fact — the watchdog hands the refused respawn to its own restart loop.
- **An instance the watchdog holds stopped is updated without a park** and stays stopped: nothing will
  spawn it mid-write, and a server nobody wants running costs no downtime to update. An instance that
  will not park while the watchdog still holds it live is not updated at all — the engine would refuse
  it and a supervisor could spawn it out of a directory being rewritten.
- kgsm takes its own `pre-update` archive inside the update and abandons the update if that archive
  fails. That guarantee is the engine's, and nothing here duplicates or weakens it.

**`restart`** — `IWatchdogClient.RestartAsync`, one atomic transition the watchdog already owns: it
drains and respawns without incrementing the crash-recovery streak, and never leaves the instance in a
state where desired-state says stopped. A restart in a window whose update already parked is **already
delivered** — the release drained and respawned the instance — so it is recorded `ok` naming what
delivered it, rather than bouncing the server a second time.

## What this host will fire

What leaves a window with nothing to fire **at all** is decided when the window is read, once, and
reported as an invalid window with the reason — never discovered as a failed run every week. An
operator who writes a window this host cannot honour hears about it on the next poll.

- The expression does not parse. kgsm-lib's own error travels, because it names the offending text.
- The window comes round more often than `MinimumWindowPeriodMinutes` permits.
- The window names a task this daemon does not run. That is a fact about the host rather than about
  one server, so it is stated once, here.

What one *particular instance* cannot run is a different question, and the task's own gate answers it
in the instant before dispatch. A container instance is the live case: every disruptive task is issued
through the watchdog, which supervises native instances alone, so a container's `restart` is declined
with that reason while the `backup` written beside it in the same window still fires. It is declined
rather than failed because nothing was owed — the restart was never going to happen — and because
failing would abort the rest of the window.

This daemon is the runtime backstop, not the only check. The API refuses an impossible window when
somebody writes it; every other writer — the CLI, the assistant, a script — reaches kgsm directly, and
this is what stands behind them.

**Validity is per window.** An invalid one disables itself and leaves the instance's other windows
firing. It appears in the snapshot as `valid: false` with its `error` and a null `nextFireUtc` —
never absent, and the two together are what distinguish it from a window that is simply not due.

It also degrades the leaf's **`config`** component, alongside `watchdog` and `kgsm`. This leaf fails
more quietly than any other in the ecosystem: everything it does is something that was supposed to
happen, so maintenance that never runs produces no event and no absence anybody notices. Degrading is
what makes "there is maintenance here that is never going to happen" a fact the leaf's own health
states.

**A host policy is not a misconfiguration.** With `AllowDisruptiveTasks` off, a window carrying a
restart still fires and still takes its backup; the restart is recorded `skipped` with the policy as
its reason, and the window is not announced — there would be nothing true to announce. The same holds
for a container's restart: both are known before the countdown would open, and a countdown that can
only end in a retraction never starts.

## The restart gate (`RestartGate`)

A due restart is dispatched only after the instance's state is re-read from the watchdog
(`IWatchdogClient.GetStatusAsync`) in that instant. The clock decides *when*; only the watchdog knows
what the instance is doing by then, and restarting the wrong thing is worse than not restarting.

Two watchdog behaviours are what make the re-assert load-bearing. `StartAsync` is an operator override
that clears the give-up latch and the failure streak, so dispatching into an instance the supervisor
has given up on wipes its crash history on a timer. And a stop of an instance the daemon does not
track succeeds as a no-op, so an ungated restart of a deliberately stopped server runs straight into
the start half and spawns it.

The gate dispatches on exactly one reading — phase `running` with a populated cgroup — and abandons on
everything else, recording which kind of abandonment it was:

- **`failed` — the restart was owed and did not happen.** The watchdog did not answer, so this
  instance's state is unknown — never read as "not running", because nothing measured it. This aborts
  the rest of the window.
- **`skipped` — the restart does not apply.** The instance is not supervised (so it is not running),
  the watchdog has given up on it, it is mid-way through a phase of its own, or it is a container and
  the watchdog supervises native instances alone. Declining is the correct outcome, so it is recorded
  with its reason rather than raised, and the window carries on.

## Announcing a window (`AnnouncementPlan`, `WindowAnnouncer`, `PendingAnnouncementStore`)

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

**A lead at or above a window's own period is dropped, and the drop is reported.** The smallest due
mark is the only true one of several — but that holds because marks come due in descending order, and
they only do so while the period exceeds the largest lead. On a ten-minute window with leads `15,5,1`,
the first tick after a fire already has 15 due, so the server would be told *"in 15 minutes"* nine
minutes before it happens, every time. Such a lead is dropped and the daemon says so once per
configuration, rather than silently honouring fewer leads than an operator wrote.

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

**A server with nobody on it is not announced to — but only when the watchdog can actually see its
players.** `GetPlayerPresenceAsync` reporting `IsDetected` with an empty roster is a measured absence.
An unreachable daemon, an untracked instance, or one whose players cannot be observed at all are each
announced to anyway: "no players detected" and "detection unavailable" are different facts, and
reading the second as the first silences a server full of people.

**Delivered means the engine wrote to the console, never that a person read it.**

## The socket

Default `/run/kgsm-scheduler/scheduler.sock` (`Scheduler__SocketPath`), serving HTTP: `GET /health`
for liveness, `GET /status` for the snapshot (`SchedulerStatus`), and one `POST` route per verb. A read
is a `GET` and an instruction is a `POST`, so one socket carries both and neither waits on the other.

`GET /status` is what `kgsm-api` reads for its own aggregation:

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

`lastUpdateCheckUtc` is **the sweep's own attempt**, not when the upstream was last fetched. A server
skipped as recently-checked is null here while the engine holds a real `checked_at` for it, and a
failed attempt has a time here with no new `checked_at` there. A surface answering *"when was this last
checked for updates"* wants the engine's `checked_at` from the status read; these three fields answer
*"is the sweep working, and what failed"*.

## What it can be told (`WindowControl`)

Each verb is its own route under `/windows/`, carrying the instance and window it acts on:

```
→ POST /windows/postpone  {"instance":"factorio-01","window":"daily@04:00","minutes":60}
← 200                     {"ok":true,"message":"postponed 60 minute(s)","nextFireUtc":"2026-08-13T05:00:00+00:00"}
```

| route | body | what it does |
|---|---|---|
| `POST /windows/postpone` | `instance`, `window`, `minutes` (1–720, default 60) | pushes this window's next run back |
| `POST /windows/skip` | `instance`, `window` | drops this occurrence; the one after it is unaffected |
| `POST /windows/run-now` | `instance`, `window` | brings this window forward to the next poll |

**A refusal is an answer.** An instruction naming a window this host does not have was read perfectly
well and declined, so it comes back 200 with `ok:false` and its own reason. A non-2xx keeps its single
meaning of "this daemon could not read what you sent" — a 400 for a body that is not a request, a 404
for a verb that is not a route.

`minutes` is capped at 720: past that it is a schedule change, and a schedule change belongs in the
instance's own config where it survives a restart of this daemon.

**A verb names its window.** One instance holds several appointments, and moving the wrong one is worse
than refusing — so an instruction naming no window is refused with the ids it could have named.

**Every verb moves a standing target; none edits a schedule.** The instance's kgsm config is untouched,
so the fire *after* the one acted on lands exactly where it always would have — which is what makes
these "not tonight" and "just this once" rather than reschedules, and why they need nothing from kgsm.
The move is applied under the registry's lock, so a tick landing mid-write cannot overwrite the new
target with the one it read a moment ago. It survives ticks because the plan is kept while its
signature matches, and moving a target does not change the signature.

`run-now` moves the target to the present rather than starting a run itself, so the window goes through
exactly the sequence a scheduled one does — the same exclusive slot, the same gates, the same record. A
second path into a run would be a second set of rules about when one is allowed to happen.

**None of it survives a restart of this daemon.** The standing target lives in the in-memory registry,
so a restart recomputes it from the instance's config and the deferred fire comes back. That is the
honest consequence of not editing the schedule, and it is the right trade for verbs that mean "not for
the next hour".

A moved target is a different fire from the one anybody was warned about: the engine retracts the
warnings already given and announces the new countdown from scratch. What persists across a restart of
this daemon is only what was *said*, never the schedule — so the deferred fire coming back also brings
back an unannounced countdown, announced afresh.

**The verbs are `scheduler:windows.write`, checked by the caller.** A unix socket carries no identity;
the only restriction on it is its filesystem permission. The shipped command manifest names the action
each verb is performed under, and `kgsm-api` checks the person for it before it dials this — the action
is declared here, where it is performed, and checked there. A window run early with `run-now` is still
the window, gated by its author like any other firing.

## Its own surface

Default `/run/kgsm-scheduler/surface.sock` (`Scheduler__SurfaceSocketPath`), a socket of its own so the
node's API finds it from this component's id alone. It serves what this component answers about itself
— its descriptor and the deploy floors under it, the overrides in force, its journal, its unit and the
commands it declares — at the routes every component serves them at, under `/component`. A change made
in the Control Panel is written to `Scheduler__ConfigOverridePath`, a file this unit already loads with
`EnvironmentFile=`. All of it is `TheKrystalShip.KGSM.ComponentSurface`, which lives beside the
generator that writes the descriptor it reads, so a leaf and an anchor answer the same questions the
same way.
