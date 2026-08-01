# KGSM Scheduler — Configuration

## Sources

Configuration is loaded from two sources, in order of precedence (last wins):

1. **`kgsm-scheduler.settings.json`** — lives next to the binary. Provides documented defaults.
2. **Environment variables** — same key names as the JSON file. Override the file values.

The systemd unit sets env vars via `EnvironmentFile=-/etc/kgsm-scheduler/kgsm-scheduler.env`.
For local development, edit `kgsm-scheduler.settings.json` directly.

## Settings

Every setting has three representations: a JSON key, an env var of the same name, and a
typed property on `SchedulerOptions`. Defaults are listed under the JSON column.

| JSON key / Env var | Default | Description |
|---|---|---|
| `KGSM_SCHEDULER_KGSM_PATH` | `/usr/bin/kgsm` | Path to the `kgsm` binary. Validated at startup — the daemon refuses to start if this file does not exist. Set the env var or edit the JSON to point at a non-standard install. |
| `KGSM_SCHEDULER_WATCHDOG_SOCKET` | `/run/kgsm-watchdog/control.sock` | kgsm-watchdog control unix socket. Scheduler issues atomic restarts through the watchdog — never directly. Must match the watchdog's listen path. |
| `KGSM_SCHEDULER_STATUS_SOCKET` | `/run/kgsm-scheduler/status.sock` | Status unix socket the scheduler exposes. Serves one NDJSON line per connection with the current schedule snapshot. `kgsm-api` connects here for the `/settings` aggregation. The systemd unit creates the parent directory (`RuntimeDirectory=kgsm-scheduler`). |
| `KGSM_SCHEDULER_POLL_INTERVAL` | `60` | How often (seconds) to re-scan instance schedule config from kgsm. Lower = more responsive to config changes, higher = less I/O. Anything below 5 is raised to 5. |
| `KGSM_SCHEDULER_GRACE_WINDOW_MINUTES` | `10` | Grace window in minutes. If a scheduled fire is more than this many minutes late (host was down, daemon was stopped), skip it rather than firing a surprise restart. Prevents catch-up storms on boot. Set to 0 to always fire missed restarts. |

## Example: env var override

```bash
# /etc/kgsm-scheduler/kgsm-scheduler.env
KGSM_SCHEDULER_POLL_INTERVAL=30
KGSM_SCHEDULER_GRACE_WINDOW_MINUTES=5
```

## Example: kgsm-scheduler.settings.json override

```jsonc
{
  // Only override what you need; missing keys use the coded defaults.
  "KGSM_SCHEDULER_POLL_INTERVAL": "30",
  "KGSM_SCHEDULER_GRACE_WINDOW_MINUTES": "5"
}
```
