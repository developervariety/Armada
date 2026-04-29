#!/usr/bin/env bash
set -euo pipefail

UNIT_NAME="armada.service"
UNIT_DIR="${XDG_CONFIG_HOME:-${HOME}/.config}/systemd/user"
UNIT_PATH="${UNIT_DIR}/${UNIT_NAME}"

if ! command -v systemctl >/dev/null 2>&1; then
    echo "ERROR: systemctl is required to remove the systemd user service." >&2
    exit 1
fi

echo
echo "[remove-systemd-user] Disabling ${UNIT_NAME}..."
systemctl --user disable --now "${UNIT_NAME}" >/dev/null 2>&1 || true

rm -f "${UNIT_PATH}"
systemctl --user daemon-reload
systemctl --user reset-failed >/dev/null 2>&1 || true

echo
echo "[remove-systemd-user] Completed."
