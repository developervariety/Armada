#!/usr/bin/env bash
# Run ONE bounded autonomous lead cycle on the admiral host.
#
# The built-in objective scheduler dispatches eligible objectives by itself. It
# does not land work, close incidents, refill campaign lanes, or answer a
# helper. This cycle is that operator layer. It runs once and exits; a timer or
# an AgentWake process wake starts the next one.
#
# Single-flight is the point. A timer firing while a wake-started cycle is
# already running would give one participant key two process owners, which the
# board cannot represent and which duplicates dispatch. The lock below refuses
# the second cycle rather than queueing it: a skipped cycle costs one interval,
# a doubled cycle costs real captain time.
#
# Configuration:
#   AUTONOMY_LEAD_KEY         participant key (default armada-lead). MUST differ
#                             from any interactive operator's key.
#   AUTONOMY_LEAD_RUNTIME     claude, codex, or opencode (default claude)
#   AUTONOMY_LEAD_TIMEOUT_MIN wall-clock cap per cycle (default 45)
#   AUTONOMY_LEAD_WORKDIR     state root (default $HOME/autonomy-lead)
#   AUTONOMY_LEAD_REPO        Armada checkout holding the bootstrap prompt
#                             (default: derived from this script's location)
#   AUTONOMY_ARMADA_MCP_URL   Armada MCP URL (default http://127.0.0.1:7891/mcp)
#
# Usage:
#   lead-cycle.sh run          run one cycle now (refuses if one is running)
#   lead-cycle.sh status       report whether a cycle is running, and the last result
#   lead-cycle.sh kill         stop the running cycle
set -euo pipefail

umask 077

LEAD_KEY="${AUTONOMY_LEAD_KEY:-armada-lead}"
RUNTIME="${AUTONOMY_LEAD_RUNTIME:-claude}"
TIMEOUT_MIN="${AUTONOMY_LEAD_TIMEOUT_MIN:-45}"
WORKDIR="${AUTONOMY_LEAD_WORKDIR:-$HOME/autonomy-lead}"
# This script lives in the checkout, so the checkout is two levels up. Deriving
# it beats a hard-coded path: it works on any host and in any clone.
SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)
REPO="${AUTONOMY_LEAD_REPO:-$(CDPATH= cd -- "$SCRIPT_DIR/../.." && pwd -P)}"
MCP_URL="${AUTONOMY_ARMADA_MCP_URL:-http://127.0.0.1:7891/mcp}"

LOG_DIR="$WORKDIR/logs"
RUN_DIR="$WORKDIR/run"
LOCK_FILE="$RUN_DIR/cycle.lock"
PID_FILE="$RUN_DIR/cycle.pid"
LAST_FILE="$RUN_DIR/last-result"

fail() {
    echo "REFUSED: $*" >&2
    exit 2
}

validate() {
    case "$LEAD_KEY" in
        ''|*[!A-Za-z0-9._:-]*) fail "AUTONOMY_LEAD_KEY must be A-Z a-z 0-9 . _ : or -" ;;
    esac
    case "$TIMEOUT_MIN" in
        ''|*[!0-9]*) fail "AUTONOMY_LEAD_TIMEOUT_MIN must be a positive integer" ;;
    esac
    [ "$TIMEOUT_MIN" -gt 0 ] || fail "AUTONOMY_LEAD_TIMEOUT_MIN must be greater than zero"
    case "$(printf '%s' "$RUNTIME" | tr '[:upper:]' '[:lower:]')" in
        claude|codex|opencode) ;;
        *) fail "AUTONOMY_LEAD_RUNTIME must be claude, codex, or opencode" ;;
    esac
    case "$MCP_URL" in
        http://*|https://*) ;;
        *) fail "AUTONOMY_ARMADA_MCP_URL must use http or https" ;;
    esac
    case "$MCP_URL" in
        *\"*|*\\*|*[[:space:]]*) fail "AUTONOMY_ARMADA_MCP_URL contains unsupported characters" ;;
    esac
    [ -f "$REPO/docs/autonomy/lead-bootstrap-prompt.md" ] \
        || fail "bootstrap prompt not found under $REPO"
}

is_alive() {
    local pid="$1"
    case "$pid" in ''|*[!0-9]*) return 1 ;; esac
    kill -0 "$pid" 2>/dev/null
}

prepare_mcp_config() {
    local path="$WORKDIR/lead-mcp.json"
    # The participant header is what lets Armada return directed wakes on ANY
    # tool result, so the cycle sees mail without polling the board for it.
    printf '{\n  "mcpServers": {\n    "armada": {\n      "type": "http",\n      "url": "%s",\n      "headers": {\n        "X-Armada-Participant": "%s"\n      }\n    }\n  }\n}\n' \
        "$MCP_URL" "$LEAD_KEY" > "$path"
    chmod 600 "$path"
    printf '%s\n' "$path"
}

build_prompt() {
    # The bootstrap doc is the contract. Everything below it is the only
    # per-cycle state: which key to use, and that this is an unattended run.
    sed -n '/^---$/,$p' "$REPO/docs/autonomy/lead-bootstrap-prompt.md" | tail -n +2
    cat <<EOF

[AUTONOMOUS CYCLE CONTRACT]
Your participantKey is $LEAD_KEY. Use that exact key for every heartbeat, board
read, claim, and addressed note. Do not use an interactive operator's key.

Nobody is watching this cycle. That changes three things:
- You cannot ask a question and wait. When a decision belongs to the owner,
  post it to the board as a named OWNER DECISION and continue with other work.
- Run ONE bounded pass, post a handoff, release every claim, stop every helper
  you started, and exit. Do not start a polling loop; the next cycle is started
  for you.
- Prefer work that is reversible and provable. Do not enable AgentWake process
  delivery, do not force-push, do not deploy, and do not merge a PR.

You have about $TIMEOUT_MIN minutes of wall clock. Reserve the last five for the
handoff note and cleanup. If a voyage is still running when your time is nearly
gone, say so plainly in the handoff and leave it for the next cycle rather than
waiting on it.

Do not wait by running a blocking poll. To watch a voyage, start
$REPO/scripts/autonomy/watch-armada.mjs and read its lines. A blocking shell loop
inside one tool call produces no visible progress, cannot see a directed board
note while it runs, and has ended turns mid-work.
EOF

    if [ -n "${AUTONOMY_WAKE_TEXT:-}" ]; then
        cat <<EOF

[WHAT WOKE YOU]
Armada started this cycle because of the event below. Treat it as the first item
of directed work, then continue with the normal pass. The full record is on the
board and in your UnreadWakes; the text here is a summary and may be truncated.

$AUTONOMY_WAKE_TEXT
EOF
    fi
}

cmd_run() {
    validate
    mkdir -p "$LOG_DIR" "$RUN_DIR"

    if [ -e "$PID_FILE" ]; then
        local existing
        existing=$(cat "$PID_FILE" 2>/dev/null || true)
        if is_alive "$existing"; then
            echo "skipped: cycle already running (pid $existing)"
            exit 0
        fi
        rm -f "$PID_FILE"
    fi

    # flock closes the race between the pidfile check above and a timer or wake
    # firing in the same instant. Without -n it would QUEUE a second cycle rather
    # than refuse it, which is the exact failure being prevented. The admiral host
    # has flock; where it is absent (macOS, for the contract test) the pidfile
    # check above still holds, so the guarantee degrades rather than disappears.
    if command -v flock >/dev/null 2>&1; then
        exec 9>"$LOCK_FILE"
        if ! flock -n 9; then
            echo "skipped: another cycle holds the lock"
            exit 0
        fi
    fi

    # GNU coreutils on the admiral host; gtimeout where Homebrew supplies it.
    # A cycle with no cap could run until the provider gives up, so refuse to
    # pretend: run uncapped only after saying so out loud.
    local timeout_bin=""
    if command -v timeout >/dev/null 2>&1; then timeout_bin="timeout"
    elif command -v gtimeout >/dev/null 2>&1; then timeout_bin="gtimeout"
    else echo "warning: no timeout binary; running this cycle UNCAPPED" >&2
    fi

    local stamp log prompt_file mcp_config runtime status
    stamp=$(date -u +%Y%m%dT%H%M%SZ)
    log="$LOG_DIR/cycle-$stamp.log"
    prompt_file="$RUN_DIR/prompt-$stamp.md"
    mcp_config=$(prepare_mcp_config)
    runtime=$(printf '%s' "$RUNTIME" | tr '[:upper:]' '[:lower:]')

    build_prompt > "$prompt_file"
    echo $$ > "$PID_FILE"
    trap 'rm -f "$PID_FILE"' EXIT

    echo "lead cycle $stamp starting key=$LEAD_KEY runtime=$runtime timeout=${TIMEOUT_MIN}m"

    local -a cap=()
    [ -n "$timeout_bin" ] && cap=("$timeout_bin" --signal=TERM --kill-after=60 "${TIMEOUT_MIN}m")

    set +e
    case "$runtime" in
        claude)
            ${cap[@]+"${cap[@]}"} claude --print --setting-sources project,local \
                --strict-mcp-config --mcp-config "$mcp_config" \
                "$(cat "$prompt_file")" > "$log" 2>&1
            ;;
        codex)
            ${cap[@]+"${cap[@]}"} codex exec "$(cat "$prompt_file")" > "$log" 2>&1
            ;;
        opencode)
            ${cap[@]+"${cap[@]}"} opencode run "$(cat "$prompt_file")" > "$log" 2>&1
            ;;
    esac
    status=$?
    set -e

    # 124 is timeout's own code. It means the cap was reached, which is a bounded
    # outcome and not a crash; say which it was so a reader does not guess.
    if [ "$status" -eq 124 ]; then
        printf '%s timeout after %sm log=%s\n' "$stamp" "$TIMEOUT_MIN" "$log" > "$LAST_FILE"
        echo "lead cycle $stamp hit its ${TIMEOUT_MIN}m cap; log=$log"
    elif [ "$status" -ne 0 ]; then
        printf '%s failed exit=%s log=%s\n' "$stamp" "$status" "$log" > "$LAST_FILE"
        echo "lead cycle $stamp failed with exit $status; log=$log"
    else
        printf '%s ok log=%s\n' "$stamp" "$log" > "$LAST_FILE"
        echo "lead cycle $stamp completed; log=$log"
    fi

    exit "$status"
}

cmd_status() {
    mkdir -p "$RUN_DIR"
    local pid="none"
    if [ -e "$PID_FILE" ]; then
        pid=$(cat "$PID_FILE" 2>/dev/null || true)
        is_alive "$pid" || pid="none (stale pidfile)"
    fi
    echo "key:     $LEAD_KEY"
    echo "runtime: $RUNTIME"
    echo "running: $pid"
    echo -n "last:    "
    cat "$LAST_FILE" 2>/dev/null || echo "no cycle has run"
}

cmd_kill() {
    [ -e "$PID_FILE" ] || fail "no cycle is running"
    local pid
    pid=$(cat "$PID_FILE" 2>/dev/null || true)
    is_alive "$pid" || { rm -f "$PID_FILE"; fail "no cycle is running (stale pidfile removed)"; }
    kill "$pid" 2>/dev/null || true
    sleep 2
    is_alive "$pid" && kill -9 "$pid" 2>/dev/null || true
    rm -f "$PID_FILE"
    echo "stopped cycle pid $pid"
}

case "${1:-}" in
    run) cmd_run ;;
    status) cmd_status ;;
    kill) cmd_kill ;;
    *) fail "usage: lead-cycle.sh run|status|kill" ;;
esac
