# Deploying kgsm-scheduler

```bash
./deploy/setup.sh    # ONCE per host. Asks for sudo. Idempotent, re-runnable.
./deploy/deploy.sh   # every deploy. NO sudo, NO prompts.
```

`setup.sh` provisions the host: chowns `/opt/kgsm-scheduler` to you, seeds
`/etc/kgsm-scheduler/kgsm-scheduler.env` from the annotated `kgsm-scheduler.env.example`, puts the
real unit in **user-owned** `/etc/kgsm-scheduler/systemd/` with
`/etc/systemd/system/kgsm-scheduler.service` symlinked to it, installs a polkit rule scoped to this
project's units, enables the unit, then verifies the grant by making the same unprivileged `systemctl`
calls the deploy will.

`deploy.sh` installs the AOT binary into the prefix and a changed unit into the user-owned directory
as plain file writes, and every `systemctl` verb goes through the polkit grant. It refuses **before
building**, with *"run `deploy/setup.sh`"*, on an unprovisioned host, and verifies the result by asking
the daemon's socket for `/health` — the daemon's own definition of healthy, not just `is-active`.
`deploy-common.sh` holds the paths, units and helpers both scripts share.
