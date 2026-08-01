#!/usr/bin/env bash
#
# deploy.sh — build + deploy the kgsm-scheduler leaf. Fully headless: no sudo, no prompts.
#
#   ./deploy/deploy.sh
#
# Assumes deploy/setup.sh has provisioned this host (prefix owned by you, the unit symlinked out
# of a directory you own, polkit grant in place). If it has not, this script says so and stops
# before building. Publishes the Native-AOT binary as YOU — a single self-contained native
# binary, NO .NET runtime needed on the host.
#
# Deploy is verified against the status socket (connect + read a status line), which is the
# daemon's documented health signal — not merely "systemd launched it".
#
# Knobs: RID, KGSM_SCHEDULER_STATUS_SOCKET, HEALTH_TRIES.
#
set -euo pipefail

source "$(dirname "${BASH_SOURCE[0]}")/deploy-common.sh"

PROJECT_CSPROJ="$REPO_DIR/src/Scheduler/Scheduler.csproj"
RID="${RID:-linux-x64}"

STOPPED=0
on_err() {
    err "deploy failed (line $1)."
    if [[ "$STOPPED" -eq 1 ]]; then
        err "the service was stopped for the swap and may be down — bringing it back up ..."
        if systemctl start "$SERVICE"; then
            err "restarted ${SERVICE} (running the PREVIOUS build)."
        else
            err "could NOT restart ${SERVICE}. Check: systemctl status ${SERVICE}"
        fi
    fi
    exit 1
}
trap 'on_err "$LINENO"' ERR

# ── Preflight ─────────────────────────────────────────────────────────────────
refuse_root
require_setup
[[ -f "$PROJECT_CSPROJ" ]] || { err "project not found: $PROJECT_CSPROJ"; exit 1; }

# ── 1. Build (Native-AOT, as the invoking user) ────────────────────────────────
log "publishing Native-AOT (${RID}) → ${PUBLISH_DIR}"
rm -rf "$PUBLISH_DIR"
dotnet publish "$PROJECT_CSPROJ" -c Release -r "$RID" -o "$PUBLISH_DIR"

# ── 2. Refresh the unit if it changed (we own the file; systemd reads it via the symlink) ──
install_units_unprivileged

# ── 2b. Publish the leaf config descriptor ────────────────────────────────────
# Before the swap, so the surface kgsm-api reads never lags the binary that implements it.
install_leaf_descriptor

# ── 3. The swap ────────────────────────────────────────────────────────────────
log "stopping ${SERVICE}"
sysctl_do stop "$SERVICE" || true
STOPPED=1

log "syncing publish tree → ${PREFIX}"
rsync -a --delete --exclude='*.pdb' --exclude='*.xml' "$PUBLISH_DIR/" "$PREFIX/"

if [[ "$UNIT_CHANGED" -eq 1 ]]; then
    log "reloading systemd"
    sysctl_do daemon-reload
fi

log "starting ${SERVICE}"
sysctl_do start "$SERVICE"
STOPPED=0

# ── 4. Verify (connect to the status socket and read a line) ───────────────────
log "waiting for ${SERVICE} to serve its status socket at ${SCHED_SOCK} ..."
if wait_health; then
    log "kgsm-scheduler is up and serving status ✓"
    systemctl --no-pager --lines=0 status "$SERVICE" 2>/dev/null | head -n 4 || true
else
    err "service started but ${SCHED_SOCK} did not serve a status line within ${HEALTH_TRIES}s."
    err "recent logs:"
    journalctl -u "$SERVICE" -n 30 --no-pager || true
    exit 1
fi
