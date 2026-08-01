# Changelog

All notable changes to `kgsm-scheduler` are documented here.

## [Unreleased]

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
