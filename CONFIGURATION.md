# KGSM Scheduler — Configuration

## Sources

Configuration comes from two sources, later wins:

1. **`kgsm-scheduler.settings.json`** — installed beside the binary. Declares the daemon's **whole**
   configurable surface, each key with its default. This is the floor.
2. **Environment variables** — override one key each. The systemd unit supplies them via
   `EnvironmentFile=-/etc/kgsm-scheduler/kgsm-scheduler.env`.

An environment variable names a key by spelling that key's **path** through the file with `__`:
`Scheduler__PollIntervalSeconds` sets `Scheduler` → `PollIntervalSeconds`. **A variable naming a
key the file does not declare binds to nothing** — there is no separate list of recognized
variables to fall out of sync with, so if a name is not in that file it does not exist.

For local development, edit `kgsm-scheduler.settings.json` directly. On a host, prefer the env file:
a deploy replaces the settings file, so operator config kept there survives.

## Settings

Every setting is three things that cannot disagree: a key in `kgsm-scheduler.settings.json`, a
property on `SchedulerSettings`, and a field in `deploy/kgsm-scheduler.leaf.json` (what the Control
Panel renders). A test fails the build if any of the three is missing one of the others.

| Key | Env var | Default | Description |
|---|---|---|---|
| `Scheduler:KgsmPath` | `Scheduler__KgsmPath` | `/usr/bin/kgsm` | Path to the KGSM executable, read for each server's schedule. Checked at startup — the daemon refuses to start if nothing is there. |
| `Scheduler:WatchdogSocketPath` | `Scheduler__WatchdogSocketPath` | `/run/kgsm-watchdog/control.sock` | The watchdog's control socket. Restarts are issued through the watchdog, never directly, so this has to match the path the watchdog listens on. |
| `Scheduler:StatusSocketPath` | `Scheduler__StatusSocketPath` | `/run/kgsm-scheduler/status.sock` | Unix socket the schedule snapshot is served on, one NDJSON line per connection. `kgsm-api` reads it here for the `/settings` aggregation. The unit creates the parent directory (`RuntimeDirectory=kgsm-scheduler`). |
| `Scheduler:ControlSocketPath` | `Scheduler__ControlSocketPath` | `/run/kgsm-scheduler/control.sock` | Unix socket the daemon takes instructions on, one NDJSON request and one reply per connection. Separate from the status socket, whose contract is that a client only ever reads. Same parent directory. |
| `Scheduler:PollIntervalSeconds` | `Scheduler__PollIntervalSeconds` | `60` | How often each server's schedule is re-read from KGSM. Bounds how quickly a schedule change takes effect; does not affect the accuracy of a fire already scheduled. Anything below 5 is raised to 5. |
| `Scheduler:GraceWindowMinutes` | `Scheduler__GraceWindowMinutes` | `10` | How late a missed maintenance window may be and still run. Anything later is skipped, so a host that was down does not come back to a burst of catch-up work. Capped at half the window's own period, and floored at one poll interval — see below. |
| `Scheduler:MinimumWindowPeriodMinutes` | `Scheduler__MinimumWindowPeriodMinutes` | `10` | The shortest period this host permits a maintenance window to have. A window asking to run more often is reported as one this host will not fire, with that reason. Anything below 10 is raised to 10 — the grammar's own floor, which a host policy cannot undercut. |
| `Scheduler:AllowDisruptiveTasks` | `Scheduler__AllowDisruptiveTasks` | `true` | Whether maintenance that interrupts the people on a server may run on this host at all. False leaves backups running as normal; anything disruptive is recorded as skipped with that reason, and the windows carrying it are not announced. |
| `Scheduler:UpdateCheckEnabled` | `Scheduler__UpdateCheckEnabled` | `true` | Whether to sweep every server for a newer game build. False means nothing on this host ever asks upstream, so no update is announced — and an `update` task, which dispatches only on a recorded upstream version, is skipped with that reason. |
| `Scheduler:UpdateCheckIntervalMinutes` | `Scheduler__UpdateCheckIntervalMinutes` | `60` | How often the whole roster is swept. Each server asks its own upstream, so the cost is linear in servers. Anything below 5 is raised to 5. |
| `Scheduler:UpdateCheckStaggerSeconds` | `Scheduler__UpdateCheckStaggerSeconds` | `5` | Pause between one server's check and the next. The sweep is serial by design; this spreads the requests out further. Zero checks back to back. |
| `Logging:LogLevel:Default` | `Logging__LogLevel__Default` | `Information` | Minimum severity logged, to the journal. |

Out-of-range numbers are clamped and blank strings fall back to the coded default, so a hand-edited
value degrades to something workable rather than taking the daemon down at startup.

## Grace is relative to the window

`GraceWindowMinutes` is one host-wide number, and a window's period runs from ten minutes to thirty
days. The configured value is therefore **capped at half the window's own period** and **floored at
one poll interval**:

- **The cap.** A grace at or beyond a window's period would leave one occurrence still owed while the
  next is already due — the catch-up burst the grace exists to prevent, arriving one fire at a time
  and never emptying. Half the period is the widest grace under which at most one occurrence is ever
  in flight. A host asking for ten minutes gets ten on a nightly window and five on a ten-minute one.
- **The floor.** A target is reached between ticks and acted on at the next one, so a fire is at best
  one poll late. A grace under that would drop every window on the host as missed, including one that
  had just come due.

## Per-instance configuration

The knobs above are the **daemon's**. What each server does, and when, lives in that server's own
kgsm config and is read fresh on every poll:

| key | what |
|---|---|
| `maintenance_windows` | the windows themselves, packed — `daily@05:00/backup;weekly.sun@04:00/backup,update,restart` |
| `timezone` | the IANA zone an appointment's time of day is read in. Intervals ignore it |
| `backup_retention` | how many prunable archives a `backup` task keeps |
| `announce_lead_minutes` | the lead times a window is announced at, e.g. `15,5,1` |
| `announce_maintenance_message` | what is said, with `{instance}`, `{minutes}` and `{reason}` |
| `announce_maintenance_cancelled_message` | what is said when an announced window does not happen |

The grammar, and what makes a window valid, are `kgsm-lib`'s
(`TheKrystalShip.KGSM.Core.Scheduling`). What *this host* additionally refuses — a period under its
floor, a task it does not run, anything disruptive on a container instance — is in `CLAUDE.md`
under **What this host will fire**.

## Example: env var override

```bash
# /etc/kgsm-scheduler/kgsm-scheduler.env
Scheduler__PollIntervalSeconds=30
Scheduler__GraceWindowMinutes=5
Scheduler__MinimumWindowPeriodMinutes=60
```

`deploy/kgsm-scheduler.env.example` is the annotated version of the same file, with every knob and
its default.

## Example: settings file override

```jsonc
{
  // Only the keys you change; anything omitted keeps its coded default.
  "Scheduler": {
    "PollIntervalSeconds": 30,
    "GraceWindowMinutes": 5
  }
}
```
