# CLAUDE.md — kgsm-scheduler

## What this is

`kgsm-scheduler` is a **resident leaf daemon** in the KGSM ecosystem. It reads
per-instance schedule config from kgsm (via `kgsm-lib`) and issues **atomic restarts
through the watchdog** at wall-clock time (daily / weekly / 6h cadences, in the
instance's configured IANA timezone).

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
- `src/Scheduler/ScheduleRegistry.cs` — thread-safe snapshot store shared between the
  engine (writer) and the status socket (reader).
- `src/Scheduler/StatusSocketServer.cs` — `BackgroundService` that serves the current
  status snapshot as one NDJSON line per connection over a unix socket. Health =
  connect + parse.
- `src/Scheduler/SchedulerOptions.cs` — pure POCO with defaults; bound from
  `IConfiguration` (`KGSM_SCHEDULER_*` keys) via `IOptions<SchedulerOptions>`.
- `kgsm-scheduler.settings.json` — default config values (same keys as env vars). Copied to
  publish output. Env vars win over file values.
- `CONFIGURATION.md` — full reference for all settings, env vars, and defaults.
- `src/Scheduler/Json/SchedulerJsonContext.cs` — source-generated JSON context
  (AOT: all serialization goes through this).
- `systemd/kgsm-scheduler.service` — the service unit.
- `deploy/setup.sh` (once per host, asks for sudo) + `deploy/deploy.sh` (every deploy, no sudo,
  no prompts) — the ecosystem deploy contract; see `tks/scripts/deploy-template/README.md`.

## Build

```bash
dotnet build kgsm-scheduler.slnx
```

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

## Status socket

Default `/run/kgsm-scheduler/status.sock` (`KGSM_SCHEDULER_STATUS_SOCKET`). One NDJSON
line per connect: `{ "instances": [ { name, scheduledRestart, restartTime, restartDay,
timezone, nextFireUtc, lastRunUtc, lastRunOk, lastRunMessage } ] }`. This is what
`kgsm-api` connects to for the `/settings` aggregation.
