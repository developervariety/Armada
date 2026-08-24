#!/usr/bin/env bash
# Install the autonomous lead-cycle timer on the admiral host.
#
# The units are templates. The real checkout path is a host fact, not a
# repository fact, so it is substituted here rather than committed.
#
#   install.sh install   write units, reload, enable and start the timer
#   install.sh remove    stop, disable, and delete the units
#   install.sh status    show timer and last-run state
set -euo pipefail

UNIT_DIR="${AUTONOMY_UNIT_DIR:-/etc/systemd/system}"
SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)
CHECKOUT="${AUTONOMY_LEAD_REPO:-$(CDPATH= cd -- "$SCRIPT_DIR/../../.." && pwd -P)}"

fail() { echo "REFUSED: $*" >&2; exit 2; }

[ -f "$SCRIPT_DIR/armada-lead-cycle.service" ] || fail "unit templates not found"
[ -x "$CHECKOUT/scripts/autonomy/lead-cycle.sh" ] \
    || fail "lead-cycle.sh not found or not executable under $CHECKOUT"

case "${1:-}" in
    install)
        for unit in armada-lead-cycle.service armada-lead-cycle.timer; do
            sed "s|__ARMADA_CHECKOUT__|$CHECKOUT|g" "$SCRIPT_DIR/$unit" \
                > "$UNIT_DIR/$unit"
            chmod 644 "$UNIT_DIR/$unit"
        done
        systemctl daemon-reload
        # Enable the TIMER only. The service is oneshot and is started by the
        # timer or run by hand; enabling it would fire a cycle on every boot.
        systemctl enable --now armada-lead-cycle.timer
        systemctl list-timers armada-lead-cycle.timer --no-pager
        ;;
    remove)
        systemctl disable --now armada-lead-cycle.timer 2>/dev/null || true
        rm -f "$UNIT_DIR/armada-lead-cycle.service" "$UNIT_DIR/armada-lead-cycle.timer"
        systemctl daemon-reload
        echo "removed"
        ;;
    status)
        systemctl status armada-lead-cycle.timer --no-pager || true
        echo "--- last cycle ---"
        "$CHECKOUT/scripts/autonomy/lead-cycle.sh" status
        ;;
    *) fail "usage: install.sh install|remove|status" ;;
esac
