# Changelog

All notable changes to `kgsm-scheduler` are documented here.

## [Unreleased]

### Changed — a scheduled backup says a cadence took it

`FireBackupAsync` states `reason: scheduled` on the backup it takes (kgsm-lib 4.40.0). The engine
writes it into the manifest as a fact, and it is what tells a nightly archive apart from one a person
asked for — an unstated reason is recorded as an ad-hoc request, which is what this is not.

The prune that follows is unchanged and now keeps the retention count in *prunable* backups: the
engine skips pinned ones without counting them, so an operator protecting an archive never shrinks
the window this schedule maintains.

### Fixed — a first setup on a host where nothing is installed yet completes

`deploy/setup.sh` enables its unit at boot and starts it only when something exists at the unit's
`ExecStart`. A host that has never deployed this project has an empty prefix, so the unit is enabled
and left stopped, and the summary names the unit that is enabled but not running and says
`deploy/deploy.sh` is what starts it. The fresh-host path is `setup.sh` → `deploy.sh` with nothing
in between.

The grant verification adapts with it, and still makes two real polkit-gated calls: `daemon-reload`,
plus one `manage-units` call on this project's own service — `start` when the service is running
(systemd queues a no-op job), `try-restart` when it is not (documented to do nothing for a unit that
is not running). Both are dispatched as the same `manage-units` action, so a host without the grant is
refused either way and the probe measures the grant rather than the unit.

⚠ Measured in the positive direction only. The deploying user on the development host is in
`wheel`, and two pre-existing polkit rules there grant that group every
`org.freedesktop.systemd1.*` action outright, so no systemctl call by that user can be refused
and the negative path cannot be exercised on it. That `try-restart` consults polkit before it
decides there is nothing to do is systemd's own dispatch order, not something this host can
demonstrate.

## [2.6.0] - 2026-08-16

### Added — a state directory, for the journal this leaf will own

`StateDirectory=kgsm-scheduler` at `StateDirectoryMode=0750`. This daemon is the one leaf that had
neither, because it is the one leaf that persists nothing — its standing targets live in memory and
its schedule is kgsm's config.

That changes with leaf lifecycle events, where every leaf records its own starts, stops and
degradations to its own journal under its own state directory. Declaring the directory now is what
makes that a code change rather than a code change plus a host reprovision, and an empty directory
costs nothing meanwhile.

`0750` with `Group=kgsm` rather than `0700`, because a producer's journal is read by every other
component on the host and a directory cannot be entered without execute on every directory above it.
⚠ A state directory closed to the group hides the journal inside it **silently** — a reader that
cannot traverse in sees no journal rather than a permission error, which is indistinguishable from a
leaf that has recorded nothing.

## [2.5.1] - 2026-08-14

### Added — GPL-3.0-or-later

This project now carries a `LICENSE`. Its package declares `GPL-3.0-or-later` and installs the text
to `/usr/share/licenses/`, so a distributed binary travels with the terms it is under.

### Added — an Arch package, built from the tested binaries

`packaging/PKGBUILD` builds this project into a pacman package. It compiles nothing: CI publishes
first and the recipe places that output, so the packaged bytes are the tested bytes. `pkgver()`
reads `deploy/version.sh`, so the package never restates a version.

The install prefix stays `/opt/<project>` — the same path `deploy.sh` uses — which is what lets the
committed systemd unit ship verbatim instead of being rewritten at packaging time.

Config files are listed in `backup=()`, so an upgrade writes `.pacnew` beside a file you edited
rather than over it. The unit, the sysusers fragment and the leaf descriptor are packaged files, so
the descriptor can never lag the binary it describes. Nothing is enabled by a scriptlet: pacman's
own hooks handle the service account, the state directories and the daemon reload, and enabling a
unit is the administrator's decision.

### Added — one machine-readable version, read rather than restated

`deploy/version.sh` prints this project's version from the single file that declares it, and
`--pkgver` prints the form pacman accepts (a `pkgver` may not contain a hyphen; ordering survives it,
since `vercmp` puts `3.16.0rc3` before `3.16.0`). Packaging asks for a version instead of carrying a
copy that can fall behind the binary.

### Added — the deploy contract is files, not install-time script output

`deploy/polkit/48-kgsm-scheduler-deploy.rules.in` carries the headless-deploy grant as reviewable content, and
`setup.sh` renders the deploying user and unit list into it instead of embedding the rule in a
heredoc — what a host is granted can now be read without running anything.

`deploy/sysusers.d/kgsm-scheduler.conf` declares the `kgsm` service account so a packaged install provisions it
declaratively rather than relying on an account that happens to exist.

`deploy/kgsm-scheduler.requires.json` states every host command, peer service and kernel feature this project
needs — each with its Arch package name, a probe that proves it works, and, for anything optional,
what is lost without it.

### Changed — the committed unit names the service account, not a developer

`User=`/`Group=` read `kgsm`, the account `sysusers.d` declares. `render_unit()` still substitutes
the deploying user at install time, so a dev-host deploy is unchanged.

### Changed — the unit lives in `deploy/`

`kgsm-scheduler.service` moves from `systemd/` to `deploy/`, so `render_unit()` uses the same path
every other repo does. The unit also declares `Group=`, which it previously omitted.

### Added — a control socket, and one thing to say on it

`postpone` pushes a server's next scheduled restart back without touching its schedule. The instance's
kgsm config is untouched, so the fire *after* the postponed one lands exactly where it always would
have — which is what makes it "not tonight" rather than a reschedule, and why it needs nothing from
kgsm. Defaults to an hour, capped at twelve: past that it is a schedule change, and a schedule change
belongs in the instance's own config where it survives a restart of this daemon.

**A second socket rather than a second use of the status one.** That socket's contract is that a
client connects and only ever reads, and everything reading it depends on that; teaching it to wait
for an optional request first would put a timeout in front of every status read to serve a command
that arrives rarely.

The move is applied under the registry's lock, so a tick landing mid-write cannot overwrite the new
target with the one it read a moment ago, and it survives ticks because a postponement does not change
the plan's signature — which is what `Plan()` keys a standing target on.

⚠ **The daemon enforces no authorization here, and the shipped command manifest says so** (`gates`
bucket `none`). A unix socket carries no identity; the only restriction is the filesystem permission
on it, the same posture the status socket has always had. A caller wanting a tier check owes it
itself.

⚠ **A postponement does not survive a restart of this daemon** — the standing target is in memory, so
a restart recomputes it from config and the deferred fire returns. The honest consequence of not
editing the schedule.

The daemon now ships a command manifest (`deploy/kgsm-scheduler.commands.json`), installed into
`/var/lib/kgsm/leaves/commands/` on every deploy, so what it can be told is documented without
`kgsm-api` learning a thing about it.

### Added — the scheduler sweeps the roster for newer game builds

A second cadence beside the wall-clock schedules: every `UpdateCheckIntervalMinutes` (hourly by
default) each server is asked whether a newer build exists, via `check-update --emit`. The scheduler
holds no answer and makes no judgement — kgsm fetches the upstream version, records it beside the
instance and emits `instance_update_available` for a version it has not announced before. Update
availability becomes a fact every surface reads from the journal instead of something each one polls
for.

Its own `BackgroundService` rather than another branch of the engine's tick, because the two answer
to different clocks: a scheduled restart fires at a wall-clock time in the server's timezone, while a
sweep runs on an interval and has no meaningful time of day.

**Serial and staggered.** Each server asks its own upstream, so running the roster together means as
many simultaneous steamcmd logins as there are servers, in the same second, against hosts with every
reason to throttle that. The roster is walked one at a time with `UpdateCheckStaggerSeconds` between.
A failed check is logged and the sweep moves on; the next sweep tries again.

**The cadence belongs to the interval, not to the daemon's uptime.** A `PeriodicTimer` fires
immediately on start, so a sweep that consulted nothing would re-ask every upstream on every restart
— a few deploys in a row becoming a burst of logins for an answer taken minutes ago. The sweep reads
the engine's own `checked_at` record first (a fast status read, no network) and leaves alone anything
checked within half the interval. Half rather than the whole: a sweep staggers, so the server checked
last is fractionally younger than one interval when the next sweep begins, and measuring against the
full interval would skip it and let it go two intervals stale.

`lastUpdateCheckUtc` / `lastUpdateCheckOk` / `lastUpdateCheckMessage` join the status snapshot. These
are the **sweep's own** record — when this daemon last attempted a check and what went wrong — which
is the part the engine cannot report. When the upstream was really fetched is the engine's
`checked_at`, on the status read; a server skipped as fresh has a null here and a real `checked_at`
there, and the two must not be conflated.

### Fixed — an update-available announcement is attributed to the leaf, not to a person

The sweep's `check-update --emit` carried no provenance, so the `instance_update_available` event it
produced fell back to the OS user this daemon runs as: an unattended hourly sweep appeared in the
audit log as `heisen` having asked. It stamps `actor: "system:scheduler"`, `origin: "system"` — the
same pair the scheduled backups use.

### Fixed — a scheduled backup is attributed to the leaf, not to a person

The backup and prune calls stamped `actor: "scheduler"`, and a bare actor with no provider prefix is
the engine's OS-user fallback: kgsm-api parses it into a **human** on the local host, so the audit
trail read as though someone named `scheduler` had taken the archive. They now stamp
`actor: "system:scheduler"`, the same `provider:name` form the watchdog uses, which resolves to the
autonomous leaf — and lets a surface identify the row as scheduler-sourced.

`origin` goes with it: `"scheduler"` is not one of the five surfaces the audit vocabulary knows
(`ui|assistant|discord|system|api`), so it was normalized away and the row carried no origin at all.
An autonomous leaf action is `system`.

### Changed — kgsm-lib 4.5.0

Up from 2.0.0. `IInstanceService.CheckUpdate` is what the sweep is built on: `emit` runs the engine's
recording check, and `actor`/`origin` stamp the announcement it produces. `VersionInfo.CheckedAt` is
the engine's own record of when an upstream was last fetched, which is what lets the sweep skip a
server it would otherwise re-ask on every restart.

The engine event journal is now queried directly through the library
(`IEventJournalHistory`), which retires kgsm-monitor's event index — nothing here read that index, so
this repo only follows the pin.

Two breaking changes in the library reach this code. `IEventService.RegisterRawHandler` and
`IEventSource.EventReceived` carry an `EventPosition` alongside the envelope, because an event's
journal position is now its identity: it is unique by construction, so two identical events emitted
within one second are no longer collapsed the way a content hash collapsed them.
`IInstanceService` gained the player-moderation verbs (`Kick`/`Ban`/`Unban`) back in 2.1.0, which
this repo skipped over. Nothing here implements the interface, so the bump is the whole change.

### Fixed
- **The daemon no longer watches the entire filesystem.** The host builder's content root defaulted to
  the process working directory, which under this unit is `/`, and the builder's own `appsettings.json`
  providers watch that root *recursively* for reload — one inotify watch per directory, ~190k of the
  524k per-user budget, held for the daemon's lifetime. That budget is shared with every game server on
  the host, and a game that cannot register a watch fails to boot. The content root is now pinned to
  `AppContext.BaseDirectory`, and the unit sets `WorkingDirectory=` so the working directory is the
  install prefix rather than `/`.

### Changed — the leaf config descriptor is generated, not written
- **`deploy/kgsm-scheduler.leaf.json` is now written by `TheKrystalShip.KGSM.LeafConfig` on every build**, from
  `[LeafField]` attributes and `<panel>` doc tags on `SchedulerSettings`. A knob lives in two places —
  the property and the settings-file key — instead of three, and the descriptor cannot describe a
  variable this leaf does not read: the `env` name is derived from the property's position under its
  bound section, and the default from the settings file itself. **Edit the settings class, not the
  JSON.**
- **A field's operator-facing prose comes from a `<panel>` tag**, falling back to `<summary>` with a
  build message naming the field. The two are separate because they answer different questions: the
  summary tells a developer what the value means to the code, the panel tells whoever runs the host
  what changing it does.
- **`LeafDescriptorTests` is gone.** Every check it made — settings coverage in both directions, the
  field vocabulary, group and `dependsOn` references, enum values and defaults, bounds, floor-source
  order — now runs in the generator, at the point the file is produced rather than after, and in one
  implementation shared by every leaf instead of a copy per repo.
- The package is **build-only** and declares no dependencies: the attributes arrive as source and the
  generator reads this assembly's metadata in its own process, so nothing reaches the published
  output and this leaf gains no reflection.

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
