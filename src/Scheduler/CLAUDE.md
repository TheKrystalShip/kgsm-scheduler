# The scheduler daemon

The window model, the run, the tasks, the gate, announcing, the socket and its verbs are canon in
`../../../kgsm-docs/reference/maintenance-windows.md`, `systems/operations/maintenance-windows.md` and
`systems/operations/announcements.md`. This file is what to know when editing the code.

## Engine and shared state

`SchedulerEngine` is the wall-clock `BackgroundService`: it polls instance config every
`PollIntervalSeconds`, holds each window's standing target in its timezone, announces the ones
approaching, and opens the ones that have come due. A grace guard drops fires too overdue to run, so a
host that was down does not return to catch-up work. `WindowPlanner` is the pure arithmetic that is this
daemon's rather than kgsm-lib's: whether this host will fire a window, its period, its grace, its
standing plan. The grammar, parser and clock are kgsm-lib's (`TheKrystalShip.KGSM.Core.Scheduling`).

- **`ScheduleRegistry` has several writers** (the engine, the runner and `WindowControl` write; `/status`
  reads). Every write is a read-modify-write under one lock, because a run that finishes between ticks and
  the tick itself both write the same record.
- **`SchedulerSettings` holds what was written, unvalidated**, shaped 1:1 to the `Scheduler` section of
  `kgsm-scheduler.settings.json`. `SchedulerOptions.FromSettings` is the validated form the daemon runs
  on: it clamps out-of-range numbers and falls back on blanks, so a hand-edited value degrades instead of
  taking the daemon down.
- **`UpdateCheckSweep` is a separate interval service**, because a restart fires at a wall-clock time in
  the server's timezone while a sweep runs on an interval with no time of day. It asks every server via
  `IInstanceService.CheckUpdate(name, emit: true)`, serial and staggered, because each server asks its
  own upstream and a parallel sweep is N simultaneous steamcmd logins. It skips anything the engine's
  `checked_at` shows checked within half the interval, so restarting the daemon does not re-ask every
  upstream. It is off until a person switches it on (`updateCheckEnabled`). It only reads, as
  this daemon on its own host, so no person's access is consulted when it runs.
- **Update availability is the engine's fact.** The sweep records nothing and compares no versions;
  `lastUpdateCheckUtc` on the status is the sweep's own attempt, not the engine's `checked_at`.

## The run (`MaintenanceRunner`)

The run happens off the tick, and the instance's slot is claimed synchronously as the tick dispatches, so
two windows of one instance coming due together resolve deterministically. The author is checked when
the windows are saved, by whoever saves them (the token had to list every action the windows run); the
engine records the author with the windows (`maintenance_windows_author`). At run time the daemon acts as
itself and only requires that an author is recorded, so a role removed from the author stops nothing
already saved. Windows with no author run nothing: every task is `blocked`, the status says why, and no
countdown opens. Outcomes are `ok`, `failed`, `skipped`, `aborted` and `blocked`; the window as a
whole is `failed` if any task failed, `ok` if any task did its work, `blocked` if a task was held back and
none did its work, and `skipped` when nothing applied.

## A task (`IMaintenanceTask`)

```csharp
interface IMaintenanceTask
{
    string Name { get; }                  // the grammar token
    bool IsDisruptive { get; }            // drives whether the window is announced
    Task<TaskGate> GateAsync(Instance instance, IWatchdogClient watchdog, CancellationToken ct);
    Task<TaskOutcome> RunAsync(MaintenanceContext ctx, CancellationToken ct);
}
```

**This is the extension point.** A task is one class here, one grammar token in kgsm-lib's parser, one
entry in the API's token set and one toggle in the Control Panel. It adds no cadence, no plan and no
field on `/status`.

`IsDisruptive` carries two facts that are the same fact: a task that interrupts the people on a server is
a task the watchdog performs, and the watchdog supervises native instances alone. That is what makes a
container's restart something its gate declines rather than something the window is refused for.

- `backup`: `CreateBackup(reason: scheduled)` then `PruneBackups(retention)`, the prune only after an
  archive that landed. A refused prune does not fail the backup but travels on the record.
- `update`: the park runs through `IWatchdogClient.BeginMaintenanceAsync`/`EndMaintenanceAsync` around the
  engine call alone, and the release is unconditional. Task semantics: `systems/operations/maintenance-windows.md`.
- `restart`: `IWatchdogClient.RestartAsync`, gated by `RestartGate` on one reading (phase `running` with a
  populated cgroup).

## Announcing

`AnnouncementPlan`, `WindowAnnouncer` and `PendingAnnouncementStore` implement
`systems/operations/announcements.md`. The text is the instance's `announce_maintenance_message` with
`{minutes}`, `{reason}` and `{instance}` resolved; the engine then substitutes that into the game's own
broadcast template, so a message containing `{message}` needs no special handling here. The bookkeeping is
keyed `(instance, window)`, and `pending-announcements.json` in `Scheduler__StateDirectory` records which
marks were spoken about which fire; deleting it costs only the memory of what was already announced. The
players check reads `GetPlayerPresenceAsync` and silences only on a measured absence (`IsDetected` with an
empty roster).

## The socket and the verbs

`/run/kgsm-scheduler/scheduler.sock` (`Scheduler__SocketPath`) serves `GET /health`, `GET /status`
(`SchedulerStatus`) and one `POST` route per verb under `/windows/` (`WindowControl`): contract in
`reference/maintenance-windows.md`. The snapshot is rebuilt by the engine's tick, so an outcome written
between ticks appears at the next one. A verb moves a standing target under the registry's lock and edits
no schedule; the plan survives ticks because it is kept while its signature matches, and moving a target
does not change the signature. A moved target is a different fire from the one anybody was warned about,
so the engine retracts the warnings given and announces afresh. The verbs are `scheduler:windows.write`:
the node relays the person's access token as a bearer and each route admits a token listing it
(`TheKrystalShip.Auth.Sessions`, `Sessions__Issuer`/`Sessions__Audience`); with no issuer they answer `503`.

## Its own surface

`/run/kgsm-scheduler/surface.sock` (`Scheduler__SurfaceSocketPath`) serves what this component answers
about itself under `/component`: its descriptor, the overrides in force, its journal, its unit and its
declared commands. A change made in the Control Panel is written to `Scheduler__ConfigOverridePath`, which
the unit loads with `EnvironmentFile=`. All of it is `TheKrystalShip.KGSM.ComponentSurface`
(`systems/components/surfaces.md`).
