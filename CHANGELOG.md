# Changelog

All notable changes to `kgsm-scheduler` are documented here.

## [Unreleased]

### Changed — kgsm-lib 2.0.0 (the socket event transport is gone)
- **Pinned to `TheKrystalShip.KGSM.Lib` 2.0.0**, which removes `UnixSocketClient`,
  `KgsmEventTransport` and `KgsmOptions.SocketPath`/`EventTransport`. The scheduler consumes no events, but was
  still calling the socket overload with `/dev/null` as a path it documented as "required but unused".
  It now takes the one-argument overload and constructs no event transport at all. No behaviour change.

### Fixed — scheduled fires actually happen
- **A schedule now fires.** The engine recomputed "the next fire after now" on every tick and then
  asked whether that was already due. It never was: a next-fire time is by construction after the
  instant it was computed from, so the comparison could not come out true and no scheduled restart
  had ever run. The computed target is now held across ticks and later ticks are compared against
  it. The target is re-derived when the cadence, time, day or timezone changes, so an edited
  schedule takes effect on the next tick.

### Changed — backups run on their own cadence
- **`backup_schedule` / `backup_time` / `backup_day` replace `auto_backup_on_restart`.** A backup
  is taken against the instance as it is, running or not — kgsm records the state each archive was
  captured in — so it no longer needs a restart window to happen in. The scheduled backup is a
  `CreateBackup` + prune, with no stop and no start, and it runs whether or not the instance has a
  restart schedule at all. The two schedules share only the timezone.
- **One operation per instance at a time.** A scheduled operation runs off the tick, so a large
  game's backup cannot hold up every other instance's schedule, and a fire that arrives while that
  instance is still busy is skipped and recorded rather than queued.
- The status socket carries `backupSchedule`, `backupTime`, `backupDay` and `nextBackupUtc`
  alongside the restart schedule.

### Added — the Control Panel can configure this daemon
- **`deploy/kgsm-scheduler.leaf.json` declares every setting the scheduler reads** — all five
  `KGSM_SCHEDULER_*` keys plus the standard logging level, grouped for display, each with its type,
  default, bounds, unit and risk. `deploy.sh` installs it into `/var/lib/kgsm/leaves/`, where
  kgsm-api scans for it and renders this daemon's configuration page. Nothing in kgsm-api needs to
  know about the scheduler for that to work.
- **A coverage test project (`tests/Scheduler.Tests`) fails the build if the descriptor and the code
  disagree.** It scans the daemon's own source, so a setting added without a descriptor entry fails
  here, and a descriptor entry naming a key the scheduler does not read fails here too.
- The KGSM path and both sockets are marked `wiring`; the status socket names
  `KGSM_API_SCHEDULER_SOCKET` as the API setting that has to move with it.

### Fixed — an out-of-range poll interval no longer takes the daemon down
- **`KGSM_SCHEDULER_POLL_INTERVAL` below 5 seconds is raised to 5**, and a negative grace window to
  zero. A zero or negative interval built an invalid timer and the daemon failed at startup, which
  is a harsh outcome for a typo in an env file.

### Changed — headless deploys (`setup.sh` once, `deploy.sh` forever after)
- **`deploy/setup.sh` provisions the host once** (asks for sudo; idempotent): chowns
  `/opt/kgsm-scheduler` to the deploying user, seeds the env file, puts the real unit in
  `/etc/kgsm-scheduler/systemd/` with `/etc/systemd/system/kgsm-scheduler.service` symlinked to it,
  installs a polkit grant scoped to this project's units, enables the unit, and verifies the grant
  with the same unprivileged `systemctl` calls `deploy.sh` makes.
- **`deploy/deploy.sh` runs with no `sudo` and no prompts**, and refuses up-front (before building)
  with "run `deploy/setup.sh`" when the host is not provisioned. Post-deploy health is a real
  connect-and-read of the status socket, not just `systemctl is-active`.
- `deploy/deploy-common.sh` carries the project block plus the shared helpers, sourced by both entry
  points so they cannot drift. Canonical template and contract:
  `tks/scripts/deploy-template/README.md`.
- `artifacts/` (the deploy publish output) is now gitignored, matching every other repo.

### Added
- Auto-backup on scheduled restart: when `auto_backup_on_restart=true`, each scheduled
  restart now runs Stop → CreateBackup → PruneBackups(retention) → Start instead of a
  bare atomic watchdog RestartAsync. Backup failure is logged as a warning but does NOT
  prevent the instance from starting again.
- `SchedulerInstanceStatus.lastBackupUtc`, `lastBackupOk`, `lastBackupMessage` in the
  status socket output — lets kgsm-api surface the last backup result in the Settings tab.

## [1.0.0]

- Initial resident scheduler leaf: wall-clock scheduled restarts (daily / weekly / 6h)
  in each instance's IANA timezone, issued via the watchdog; NDJSON status socket.
