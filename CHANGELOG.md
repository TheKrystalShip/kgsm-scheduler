# Changelog

All notable changes to `kgsm-scheduler` are documented here.

## [Unreleased]

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
