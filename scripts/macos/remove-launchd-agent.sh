#!/usr/bin/env bash
set -euo pipefail

PLIST_LABEL="com.armada.admiral"
PLIST_PATH="${HOME}/Library/LaunchAgents/${PLIST_LABEL}.plist"

if [ "$(uname -s)" != "Darwin" ]; then
    echo "ERROR: remove-launchd-agent.sh must be run on macOS." >&2
    exit 1
fi

echo
echo "[remove-launchd-agent] Unloading ${PLIST_LABEL}..."
launchctl bootout "gui/$(id -u)" "${PLIST_PATH}" >/dev/null 2>&1 \
    || launchctl unload -w "${PLIST_PATH}" >/dev/null 2>&1 \
    || true

rm -f "${PLIST_PATH}"

echo
echo "[remove-launchd-agent] Completed."
