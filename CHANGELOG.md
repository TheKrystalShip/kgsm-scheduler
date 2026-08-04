# Changelog

All notable changes to `kgsm-scheduler` are documented here.

## [Unreleased]

### Added — the env template is held to the settings file
- **A test fails the build when `deploy/kgsm-scheduler.env.example` names a key
  `kgsm-scheduler.settings.json` does not declare.** The env file overrides the settings file one
  key at a time, so a variable naming an undeclared key binds to nothing — it reads as configuration
  and is inert. The template is the one copy of that file in version control, so it is the copy that
  can be checked. Commented lines count too, since a commented key is what someone uncomments;
  systemd's own directives quoted in the prose (`EnvironmentFile=`, `Delegate=`) do not, because they
  configure the unit rather than the leaf.

### Changed
- **`pairedApiKey` names the Control Panel API's renamed setting.** kgsm-api's environment
  variables are now spelled `Api__<Property>`, and this value is what the API resolves to warn that
  a change here has moved this leaf out of its reach. Naming the old key would have made that check
  silently find nothing and report the change as clean.

### Fixed — a knob written blank no longer takes the daemon down
- **Both numbers in the settings type are nullable, so "written blank" means unset.** Binding a blank
  value to a non-nullable `int` throws, which made a single stray `Scheduler__PollIntervalSeconds=`
  line in an env file a startup crash; a null one binds to `0`, silently discarding the coded default.
  Null now means unset and the coded default applies. A value that is present but is not a number
  still fails loudly, which is the point of typing it.

### Changed — configuration is typed, and the settings file declares all of it

**This deploy renames every environment variable the scheduler reads.** A host carrying the old
names loses those overrides silently and falls back to the settings file, so update
`/etc/kgsm-scheduler/kgsm-scheduler.env` in the same step. The Control Panel needs no change: the
descriptor's `key` values are untouched, so a stored override keeps working.

| Was | Now |
|---|---|
| `KGSM_SCHEDULER_KGSM_PATH` | `Scheduler__KgsmPath` |
| `KGSM_SCHEDULER_WATCHDOG_SOCKET` | `Scheduler__WatchdogSocketPath` |
| `KGSM_SCHEDULER_STATUS_SOCKET` | `Scheduler__StatusSocketPath` |
| `KGSM_SCHEDULER_POLL_INTERVAL` | `Scheduler__PollIntervalSeconds` |
| `KGSM_SCHEDULER_GRACE_WINDOW_MINUTES` | `Scheduler__GraceWindowMinutes` |

- **`kgsm-scheduler.settings.json` declares the whole configurable surface**, hierarchically, each
  key with its default. An environment variable overrides one key of it by spelling that key's path
  with `__`. There is no longer a separate set of variable names that only the code knows: a name
  not in that file binds to nothing, which is what makes the descriptor checkable against something
  real rather than against a regex over the source.
- **`SchedulerSettings` binds the file in one step**, and nothing reads configuration by string
  lookup. `SchedulerOptions.FromSettings` is the validating step between what was written and what
  the daemon runs on — it clamps and falls back, so a hand-edited value degrades rather than taking
  the daemon down.
- **The startup `kgsm` check reads the same bound options the daemon runs on**, instead of
  re-reading configuration by a second path that could disagree with the first.
- **The settings file is read from beside the binary**, by absolute path, so the working directory
  the unit happens to start in cannot decide whether the daemon is configured.
- **`KGSM_SCHEDULER_KGSM_SOCKET` is gone from the host env file.** Nothing has read it since the
  socket event transport was removed; it sat there looking like configuration.

### Fixed — the Control Panel can attribute a value again
- **`floorSources` lists the settings file first.** The list is lowest-precedence-first, and the
  settings file is the base the environment overrides. Listed last, the Control Panel resolves a
  knob to the file's default and reports it as the deployed value — showing a blank where the unit
  sets a real path. A test now pins the ordering.

### Added
- **`deploy/kgsm-scheduler.env.example`** — the annotated operator env file, every knob with its
  default. `setup.sh` seeds `/etc/kgsm-scheduler/kgsm-scheduler.env` from it on a fresh host.
- **The descriptor coverage test pins a chain of three**, in both directions at every link: a
  property on `SchedulerSettings`, a key in the settings file, a field in the descriptor. Adding a
  knob to one and forgetting the others fails the build.

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
