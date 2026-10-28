#!/usr/bin/env bash
# Install the host storage-cleanup scripts and their systemd units. Run as root.
#
# Usage:
#   sudo scripts/linux/storage-cleanup/install-storage-cleanup.sh [--dry-run]
#
# Copies the scripts to /usr/local/lib/armada-storage-cleanup and the units to
# /etc/systemd/system, and reloads systemd. An existing /etc/armada/storage-cleanup.json
# is never overwritten; when none exists the example is copied there for editing.
# The timer is not enabled: review the config and a dry run first (see the printed steps).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LIB_DIR="/usr/local/lib/armada-storage-cleanup"
UNIT_DIR="/etc/systemd/system"
CONFIG_DIR="/etc/armada"
CONFIG_PATH="${CONFIG_DIR}/storage-cleanup.json"
DRY_RUN=0

for arg in "$@"; do
    case "${arg}" in
        --dry-run) DRY_RUN=1 ;;
        -h|--help) sed -n '2,10p' "${BASH_SOURCE[0]}"; exit 0 ;;
        *) echo "ERROR: unknown argument '${arg}'." >&2; exit 2 ;;
    esac
done

run() {
    if [ "${DRY_RUN}" -eq 1 ]; then
        echo "[install-storage-cleanup] would run: $*"
    else
        "$@"
    fi
}

if [ "${DRY_RUN}" -eq 0 ] && [ "$(id -u)" -ne 0 ]; then
    echo "ERROR: run as root (or pass --dry-run)." >&2
    exit 1
fi

SCRIPTS=(cleanup_config.py daily-storage-cleanup.py prune-agent-caches.py
         prune-build-outputs.py prune-deploy-dumps.py prune-docker-storage.py prune-test-databases.py)
UNITS=(armada-storage-cleanup.service armada-storage-cleanup.timer)

run install -d -m 0755 -o root -g root "${LIB_DIR}"
for name in "${SCRIPTS[@]}"; do
    mode=0755
    [ "${name}" = "cleanup_config.py" ] && mode=0644
    run install -m "${mode}" -o root -g root "${SCRIPT_DIR}/${name}" "${LIB_DIR}/${name}"
done
for name in "${UNITS[@]}"; do
    run install -m 0644 -o root -g root "${SCRIPT_DIR}/${name}" "${UNIT_DIR}/${name}"
done

run install -d -m 0755 -o root -g root "${CONFIG_DIR}"
if [ -e "${CONFIG_PATH}" ]; then
    echo "[install-storage-cleanup] Keeping existing config ${CONFIG_PATH}."
else
    run install -m 0640 -o root -g root "${SCRIPT_DIR}/storage-cleanup.example.json" "${CONFIG_PATH}"
    echo "[install-storage-cleanup] Copied the example config to ${CONFIG_PATH}; edit every placeholder path."
fi

if command -v systemctl >/dev/null 2>&1; then
    run systemctl daemon-reload
fi

echo
echo "[install-storage-cleanup] Next steps:"
echo "  1. Edit ${CONFIG_PATH} (see storage-cleanup.example.json for every key)."
echo "  2. Dry run every stage and read the receipts:"
echo "       /usr/bin/python3 ${LIB_DIR}/daily-storage-cleanup.py --config ${CONFIG_PATH}"
echo "  3. Disable any older timer that runs the same cleanup, then enable this one:"
echo "       systemctl enable --now armada-storage-cleanup.timer"
echo "  4. Check the schedule: systemctl list-timers armada-storage-cleanup.timer"
