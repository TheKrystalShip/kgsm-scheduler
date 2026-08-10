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
| `Scheduler:PollIntervalSeconds` | `Scheduler__PollIntervalSeconds` | `60` | How often each server's schedule is re-read from KGSM. Bounds how quickly a schedule change takes effect; does not affect the accuracy of a fire already scheduled. Anything below 5 is raised to 5. |
| `Scheduler:GraceWindowMinutes` | `Scheduler__GraceWindowMinutes` | `10` | How late a missed restart may be and still run. Anything later is skipped, so a host that was down does not come back to a burst of catch-up restarts. Zero always runs missed restarts. |
| `Scheduler:UpdateCheckEnabled` | `Scheduler__UpdateCheckEnabled` | `true` | Whether to sweep every server for a newer game build. False means nothing on this host ever asks upstream, so no update is announced. |
| `Scheduler:UpdateCheckIntervalMinutes` | `Scheduler__UpdateCheckIntervalMinutes` | `60` | How often the whole roster is swept. Each server asks its own upstream, so the cost is linear in servers. Anything below 5 is raised to 5. |
| `Scheduler:UpdateCheckStaggerSeconds` | `Scheduler__UpdateCheckStaggerSeconds` | `5` | Pause between one server's check and the next. The sweep is serial by design; this spreads the requests out further. Zero checks back to back. |
| `Logging:LogLevel:Default` | `Logging__LogLevel__Default` | `Information` | Minimum severity logged, to the journal. |

Out-of-range numbers are clamped and blank strings fall back to the coded default, so a hand-edited
value degrades to something workable rather than taking the daemon down at startup.

## Example: env var override

```bash
# /etc/kgsm-scheduler/kgsm-scheduler.env
Scheduler__PollIntervalSeconds=30
Scheduler__GraceWindowMinutes=5
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
