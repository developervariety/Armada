#!/usr/bin/env bash
# =====================================================================
# factory-reset.sh -- wipe Armada back to a factory-fresh state (Linux).
#
# Stops the Armada Admiral (systemd --user unit armada.service, plus any
# stray Armada.Server process), then deletes the database and all runtime
# state under ~/.armada so the next start comes up empty as if freshly
# deployed. By default it KEEPS the deployed server bin and dashboard so
# the deployment still runs; pass --all to remove those too.
#
# Flags:
#   -y | --yes | --force   skip the confirmation prompt
#   --all                  also delete bin and dashboard (whole ~/.armada)
#
# NOTE: this regenerates the API key on next start; MCP clients and the CLI
# that stored the old key must be reconfigured.
# =====================================================================
set -euo pipefail

ARMADA_DIR="${HOME}/.armada"
UNIT_NAME="armada.service"
FORCE=0
WIPE_ALL=0

for arg in "$@"; do
    case "$arg" in
        -y|--yes|--force) FORCE=1 ;;
        --all) WIPE_ALL=1 ;;
        *) echo "[factory-reset] Unknown argument: $arg" >&2; exit 2 ;;
    esac
done

if [ ! -d "$ARMADA_DIR" ]; then
    echo "[factory-reset] Nothing to reset: $ARMADA_DIR does not exist."
    exit 0
fi

if [ "$FORCE" -ne 1 ]; then
    echo
    echo "WARNING: this permanently DELETES the Armada database and runtime state under:"
    echo "    $ARMADA_DIR"
    echo "  - armada.db: all fleets, vessels, captains, missions, voyages, jobs, and more"
    echo "  - docks (git worktrees), repos (clones), logs, settings.json"
    if [ "$WIPE_ALL" -eq 1 ]; then
        echo "  - --all: ALSO the deployed server bin and dashboard"
    fi
    echo
    printf 'Type YES to continue: '
    read -r CONFIRM
    if [ "$CONFIRM" != "YES" ]; then
        echo "[factory-reset] Aborted."
        exit 1
    fi
fi

echo
echo "[factory-reset] Stopping the Armada Admiral..."
if command -v systemctl >/dev/null 2>&1; then
    systemctl --user stop "$UNIT_NAME" 2>/dev/null || true
fi
pkill -f 'Armada\.Server' 2>/dev/null || true
sleep 1

echo "[factory-reset] Deleting database and runtime state..."
rm -f "$ARMADA_DIR"/armada.db "$ARMADA_DIR"/armada.db-shm "$ARMADA_DIR"/armada.db-wal \
      "$ARMADA_DIR"/crash.log "$ARMADA_DIR"/settings.json
rm -rf "$ARMADA_DIR"/docks "$ARMADA_DIR"/repos "$ARMADA_DIR"/logs

if [ "$WIPE_ALL" -eq 1 ]; then
    echo "[factory-reset] Removing deployed server bin and dashboard..."
    rm -rf "$ARMADA_DIR"/bin "$ARMADA_DIR"/dashboard
fi

echo
echo "[factory-reset] Done. Armada state wiped at $ARMADA_DIR."
if [ "$WIPE_ALL" -eq 1 ]; then
    echo "Reinstall to redeploy: ./scripts/linux/install-systemd-user.sh"
else
    echo "Start it factory-fresh: ./scripts/linux/update-systemd-user.sh (or: systemctl --user start $UNIT_NAME)"
fi
exit 0
