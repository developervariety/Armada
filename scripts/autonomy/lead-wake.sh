#!/usr/bin/env bash
# AgentWake entry point for the autonomous lead.
#
# Armada's AgentWake starts this with the runtime's own flags in argv
# (`--print --continue --setting-sources project,local --strict-mcp-config`)
# and the wake text on stdin. Both are wrong to use directly here:
#
# - `--strict-mcp-config` with no `--mcp-config` gives the started process ZERO
#   Armada tools, so a lead woken that way could not read the board it was woken
#   about. lead-cycle.sh writes the config and passes it.
# - `--continue` resumes whatever session last ran on this host. That session
#   has no participant key and no relation to the lead.
#
# So argv is deliberately ignored. Only stdin carries anything worth keeping.
# Single-flight, the timeout cap, and the prompt contract all stay in
# lead-cycle.sh, which is the one place that owns them.
set -euo pipefail

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)

# Armada's process host writes the payload and then closes stdin, so `cat`
# terminates on its own. The cap is only for a host that dies mid-write, and it
# is applied only where a timeout binary exists; a missing one must not silently
# swallow the wake text, which is the whole reason this shim runs.
WAKE_TEXT=""
if [ ! -t 0 ]; then
    if command -v timeout >/dev/null 2>&1; then
        WAKE_TEXT=$(timeout 10 cat 2>/dev/null || true)
    elif command -v gtimeout >/dev/null 2>&1; then
        WAKE_TEXT=$(gtimeout 10 cat 2>/dev/null || true)
    else
        WAKE_TEXT=$(cat 2>/dev/null || true)
    fi
fi

export AUTONOMY_WAKE_TEXT="$WAKE_TEXT"
exec "$SCRIPT_DIR/lead-cycle.sh" run
