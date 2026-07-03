#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(dirname "$SCRIPT_DIR")"
DEST=/opt/kgsm-scheduler
UNIT_NAME=kgsm-scheduler

cd "$REPO_DIR"

echo ">> publishing kgsm-scheduler (AOT)"
dotnet publish src/Scheduler/Scheduler.csproj -c Release -r linux-x64 -o /tmp/kgsm-scheduler-publish

SUDO="${SUDO:-sudo}"
echo ">> stopping $UNIT_NAME"
$SUDO systemctl stop "$UNIT_NAME" || true

echo ">> syncing to $DEST"
$SUDO rsync -a --delete /tmp/kgsm-scheduler-publish/ "$DEST/"
$SUDO chmod +x "$DEST/kgsm-scheduler"

echo ">> enabling and starting $UNIT_NAME"
$SUDO systemctl daemon-reload
$SUDO systemctl enable --now "$UNIT_NAME"

echo ">> $UNIT_NAME status"
$SUDO systemctl status "$UNIT_NAME" --no-pager -l | head -10
