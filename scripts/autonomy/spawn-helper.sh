#!/usr/bin/env bash
# Manage bounded, host-side helper sessions for an autonomous Armada lead.
#
# Helpers started here are fresh, read-only, single-task processes. The script
# adds the board contract to every prompt, enforces a concurrency cap, records
# process state, and provides explicit cleanup. It does not configure AgentWake
# and it does not replace captains for repository write work.
#
# Configuration:
#   AUTONOMY_MAX_HELPERS          concurrent helper cap (default 2)
#   AUTONOMY_HELPER_TIMEOUT_MIN   cull age in minutes (default 90)
#   AUTONOMY_RUNTIME              opencode, claude, codex, or command
#                                 (default opencode)
#   AUTONOMY_COMMAND              executable used only with runtime=command
#   AUTONOMY_WORKDIR              process-state root
#                                 (default $HOME/autonomy-helpers)
#   AUTONOMY_PARTICIPANT_PREFIX   participant-key prefix (default helper)
#   AUTONOMY_HELPER_OFFER_SECONDS bounded reassignment window for offer mode
#                                 (default 240)
#   AUTONOMY_ARMADA_MCP_URL        Armada MCP URL for Claude helpers
#                                 (default http://127.0.0.1:7891/mcp)
#   AUTONOMY_CLAUDE_MCP_CONFIG    optional existing Claude MCP config file;
#                                 default is a generated local Armada config
#
# Usage:
#   spawn-helper.sh spawn <name> <prompt-file> [helper-cwd]
#   spawn-helper.sh offer <name> <fallback-prompt-file> <lead-key> [helper-cwd]
#   spawn-helper.sh kill <name>
#   spawn-helper.sh list
#   spawn-helper.sh cull
set -euo pipefail

umask 077

MAX_HELPERS="${AUTONOMY_MAX_HELPERS:-2}"
HELPER_TIMEOUT_MIN="${AUTONOMY_HELPER_TIMEOUT_MIN:-90}"
RUNTIME="${AUTONOMY_RUNTIME:-opencode}"
WORKDIR="${AUTONOMY_WORKDIR:-$HOME/autonomy-helpers}"
PARTICIPANT_PREFIX="${AUTONOMY_PARTICIPANT_PREFIX:-helper}"
HELPER_OFFER_SECONDS="${AUTONOMY_HELPER_OFFER_SECONDS:-240}"
RUN_DIR="$WORKDIR/run"
LOG_DIR="$WORKDIR/logs"
SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)
SCRIPT_PATH="$SCRIPT_DIR/$(basename -- "$0")"

mkdir -p "$RUN_DIR" "$LOG_DIR"

fail() {
    echo "REFUSED: $*" >&2
    exit 2
}

require_positive_integer() {
    local label="$1"
    local value="$2"
    case "$value" in
        ''|*[!0-9]*) fail "$label must be a positive integer" ;;
    esac
    [ "$value" -gt 0 ] || fail "$label must be greater than zero"
}

validate_name() {
    local name="$1"
    case "$name" in
        ''|*[!A-Za-z0-9._-]*|.*|-*) fail "helper name must start with a letter or digit and contain only A-Z, a-z, 0-9, dot, underscore, or dash" ;;
    esac
    [ "${#name}" -le 64 ] || fail "helper name must be 64 characters or fewer"
}

validate_runtime() {
    case "$(printf '%s' "$RUNTIME" | tr '[:upper:]' '[:lower:]')" in
        opencode|claude|codex) ;;
        command)
            [ -n "${AUTONOMY_COMMAND:-}" ] || fail "AUTONOMY_COMMAND is required for runtime=command"
            [ -x "${AUTONOMY_COMMAND:-}" ] || fail "AUTONOMY_COMMAND is not executable: ${AUTONOMY_COMMAND:-}"
            ;;
        *) fail "AUTONOMY_RUNTIME must be opencode, claude, codex, or command" ;;
    esac
}

prepare_claude_mcp_config() {
    if [ -n "${AUTONOMY_CLAUDE_MCP_CONFIG:-}" ]; then
        canonical_file "$AUTONOMY_CLAUDE_MCP_CONFIG"
        return
    fi

    local mcp_url="${AUTONOMY_ARMADA_MCP_URL:-http://127.0.0.1:7891/mcp}"
    case "$mcp_url" in
        http://*|https://*) ;;
        *) fail "AUTONOMY_ARMADA_MCP_URL must use http or https" ;;
    esac
    case "$mcp_url" in
        *\"*|*\\*|*[[:space:]]*) fail "AUTONOMY_ARMADA_MCP_URL contains unsupported characters" ;;
    esac

    local config_path="$WORKDIR/claude-armada-mcp.json"
    printf '{\n  "mcpServers": {\n    "armada": {\n      "type": "http",\n      "url": "%s"\n    }\n  }\n}\n' "$mcp_url" > "$config_path"
    chmod 600 "$config_path"
    printf '%s\n' "$config_path"
}

canonical_file() {
    local path="$1"
    [ -f "$path" ] || fail "prompt file not found: $path"
    local directory
    directory=$(CDPATH= cd -- "$(dirname -- "$path")" && pwd -P)
    printf '%s/%s\n' "$directory" "$(basename -- "$path")"
}

canonical_directory() {
    local path="$1"
    [ -d "$path" ] || fail "helper working directory not found: $path"
    CDPATH= cd -- "$path" && pwd -P
}

is_alive() {
    local pid="$1"
    case "$pid" in ''|*[!0-9]*) return 1 ;; esac
    kill -0 "$pid" 2>/dev/null
}

remove_state() {
    local name="$1"
    rm -f "$RUN_DIR/$name.pid" "$RUN_DIR/$name.start" "$RUN_DIR/$name.key" \
        "$RUN_DIR/$name.mode" "$RUN_DIR/$name.lead"
}

prune_dead() {
    local file name pid
    for file in "$RUN_DIR"/*.pid; do
        [ -e "$file" ] || continue
        name=$(basename -- "$file" .pid)
        pid=$(cat "$file" 2>/dev/null || true)
        is_alive "$pid" || remove_state "$name"
    done
}

alive_count() {
    local count=0 file pid
    for file in "$RUN_DIR"/*.pid; do
        [ -e "$file" ] || continue
        pid=$(cat "$file" 2>/dev/null || true)
        if is_alive "$pid"; then count=$((count + 1)); fi
    done
    echo "$count"
}

run_helper() {
    local runtime="$1"
    local prompt_file="$2"
    local helper_cwd="$3"
    local prompt participant_key helper_name helper_mode lead_key offer_seconds mcp_config contract

    prompt=$(cat "$prompt_file")
    participant_key="${HELPER_PARTICIPANT_KEY:?missing HELPER_PARTICIPANT_KEY}"
    helper_name="${HELPER_NAME:?missing HELPER_NAME}"
    helper_mode="${HELPER_MODE:-task}"
    lead_key="${HELPER_LEAD_PARTICIPANT_KEY:-}"
    offer_seconds="${HELPER_OFFER_SECONDS:-240}"
    mcp_config="${HELPER_MCP_CONFIG:-}"
    contract=$(cat <<EOF

[ARMADA HOST HELPER CONTRACT]
You are the bounded, read-only helper named $helper_name.
Your participantKey is $participant_key.
On entry, read the coordination board and heartbeat with that exact key. Drain
UnreadWakes before other work and acknowledge each processed wake with
armada_mark_signal_read. Do only the task above. Do not edit repositories,
dispatch voyages, run shared test suites, delete refs, deploy, or commit durable
memory. Post one addressed outcome note to the lead, release any claim you took,
and exit. Do not start a polling loop or recurring schedule. Your file sandbox
is the helper working directory. If required evidence is outside it, report the
boundary instead of weakening the sandbox or guessing.
EOF
)

    if [ "$helper_mode" = "offer" ]; then
        contract="$contract$(cat <<EOF

[ARMADA HELPER OFFER]
Your lead participantKey is $lead_key. Immediately post one availability note
addressed to that key. State your fallback task from the prompt above. Give the
lead a bounded $offer_seconds-second reassignment window. During that window,
wait no more than 25 seconds between heartbeat or board-read checks. Handle a
directed Wake before the fallback and acknowledge it. If the lead assigns work,
do only that work. If the window expires without an assignment, do the fallback.
If the lead tells you to stand down, post the outcome and exit. This bounded
offer window is the only polling allowed by this contract.
EOF
)"
    fi

    prompt="$prompt$contract"
    cd "$helper_cwd"

    case "$(printf '%s' "$runtime" | tr '[:upper:]' '[:lower:]')" in
        opencode) exec opencode run "$prompt" ;;
        claude)
            [ -n "$mcp_config" ] || fail "Claude helper is missing its Armada MCP config"
            exec claude --print --setting-sources project,local --strict-mcp-config --mcp-config "$mcp_config" "$prompt"
            ;;
        codex) exec codex exec "$prompt" ;;
        command)
            local command_path="${AUTONOMY_COMMAND:-}"
            [ -n "$command_path" ] || fail "AUTONOMY_COMMAND is required for runtime=command"
            [ -x "$command_path" ] || fail "AUTONOMY_COMMAND is not executable: $command_path"
            exec "$command_path" "$prompt"
            ;;
        *) fail "AUTONOMY_RUNTIME must be opencode, claude, codex, or command" ;;
    esac
}

do_spawn() {
    local helper_mode="${1:-task}"
    if [ "$#" -gt 0 ]; then shift; fi
    local name="${1:-}"
    local prompt_arg="${2:-}"
    local lead_key=""
    local helper_cwd_arg
    if [ "$helper_mode" = "offer" ]; then
        lead_key="${3:-}"
        helper_cwd_arg="${4:-$WORKDIR}"
    else
        helper_cwd_arg="${3:-$WORKDIR}"
    fi
    validate_name "$name"
    [ -n "$prompt_arg" ] || fail "usage: $0 spawn <name> <prompt-file> [helper-cwd]"

    if [ "$helper_mode" = "offer" ]; then
        validate_name "$lead_key"
        require_positive_integer AUTONOMY_HELPER_OFFER_SECONDS "$HELPER_OFFER_SECONDS"
    fi

    require_positive_integer AUTONOMY_MAX_HELPERS "$MAX_HELPERS"
    require_positive_integer AUTONOMY_HELPER_TIMEOUT_MIN "$HELPER_TIMEOUT_MIN"
    validate_runtime
    prune_dead

    local prompt_file helper_cwd participant_key out_log pid alive mcp_config
    prompt_file=$(canonical_file "$prompt_arg")
    helper_cwd=$(canonical_directory "$helper_cwd_arg")
    participant_key="$PARTICIPANT_PREFIX-$name"
    out_log="$LOG_DIR/$name.log"
    mcp_config=""
    if [ "$(printf '%s' "$RUNTIME" | tr '[:upper:]' '[:lower:]')" = "claude" ]; then
        mcp_config=$(prepare_claude_mcp_config)
    fi

    if [ -e "$RUN_DIR/$name.pid" ]; then
        pid=$(cat "$RUN_DIR/$name.pid" 2>/dev/null || true)
        if is_alive "$pid"; then fail "helper '$name' is already running"; fi
        remove_state "$name"
    fi

    alive=$(alive_count)
    [ "$alive" -lt "$MAX_HELPERS" ] || fail "$alive running helpers meets cap $MAX_HELPERS; kill or cull first"

    nohup env \
        HELPER_PARTICIPANT_KEY="$participant_key" \
        HELPER_NAME="$name" \
        HELPER_MODE="$helper_mode" \
        HELPER_LEAD_PARTICIPANT_KEY="$lead_key" \
        HELPER_OFFER_SECONDS="$HELPER_OFFER_SECONDS" \
        HELPER_MCP_CONFIG="$mcp_config" \
        AUTONOMY_COMMAND="${AUTONOMY_COMMAND:-}" \
        "$SCRIPT_PATH" __run "$RUNTIME" "$prompt_file" "$helper_cwd" \
        >>"$out_log" 2>&1 &
    pid=$!

    echo "$pid" > "$RUN_DIR/$name.pid"
    date -u +%s > "$RUN_DIR/$name.start"
    echo "$participant_key" > "$RUN_DIR/$name.key"
    echo "$helper_mode" > "$RUN_DIR/$name.mode"
    echo "$lead_key" > "$RUN_DIR/$name.lead"
    echo "spawned name=$name pid=$pid mode=$helper_mode key=$participant_key lead=${lead_key:--} log=$out_log"
}

do_kill() {
    local name="${1:-}"
    validate_name "$name"
    local pid_file="$RUN_DIR/$name.pid"
    [ -e "$pid_file" ] || fail "no such helper: $name"

    local pid
    pid=$(cat "$pid_file" 2>/dev/null || true)
    if is_alive "$pid"; then
        pkill -TERM -P "$pid" 2>/dev/null || true
        kill -TERM "$pid" 2>/dev/null || true
        local attempt=0
        while is_alive "$pid" && [ "$attempt" -lt 20 ]; do
            sleep 0.1
            attempt=$((attempt + 1))
        done
        if is_alive "$pid"; then
            pkill -KILL -P "$pid" 2>/dev/null || true
            kill -KILL "$pid" 2>/dev/null || true
        fi
    fi

    remove_state "$name"
    echo "stopped name=$name pid=$pid"
}

do_list() {
    local file pid name state start now elapsed key mode lead
    now=$(date -u +%s)
    printf '%-20s %-8s %-10s %-8s %-10s %-24s %s\n' NAME PID STATE MODE ELAPSED PARTICIPANT_KEY LEAD_KEY
    for file in "$RUN_DIR"/*.pid; do
        [ -e "$file" ] || continue
        name=$(basename -- "$file" .pid)
        pid=$(cat "$file" 2>/dev/null || true)
        state=exited
        if is_alive "$pid"; then state=running; fi
        elapsed=-
        if [ -e "$RUN_DIR/$name.start" ]; then
            start=$(cat "$RUN_DIR/$name.start" 2>/dev/null || true)
            case "$start" in ''|*[!0-9]*) ;; *) elapsed="$(( (now - start) / 60 ))min" ;; esac
        fi
        key=$(cat "$RUN_DIR/$name.key" 2>/dev/null || true)
        mode=$(cat "$RUN_DIR/$name.mode" 2>/dev/null || true)
        lead=$(cat "$RUN_DIR/$name.lead" 2>/dev/null || true)
        printf '%-20s %-8s %-10s %-8s %-10s %-24s %s\n' "$name" "$pid" "$state" "${mode:-task}" "$elapsed" "$key" "${lead:--}"
    done
    echo "running: $(alive_count)/$MAX_HELPERS"
}

do_cull() {
    require_positive_integer AUTONOMY_HELPER_TIMEOUT_MIN "$HELPER_TIMEOUT_MIN"
    local file pid name start now age
    now=$(date -u +%s)
    for file in "$RUN_DIR"/*.pid; do
        [ -e "$file" ] || continue
        name=$(basename -- "$file" .pid)
        pid=$(cat "$file" 2>/dev/null || true)
        if ! is_alive "$pid"; then
            remove_state "$name"
            echo "reaped name=$name"
            continue
        fi
        start=$(cat "$RUN_DIR/$name.start" 2>/dev/null || true)
        case "$start" in ''|*[!0-9]*) continue ;; esac
        age=$((now - start))
        if [ "$age" -gt $((HELPER_TIMEOUT_MIN * 60)) ]; then
            echo "culling name=$name age=$((age / 60))min timeout=${HELPER_TIMEOUT_MIN}min"
            do_kill "$name"
        fi
    done
}

COMMAND="${1:-list}"
if [ "$#" -gt 0 ]; then shift; fi

case "$COMMAND" in
    __run) run_helper "${1:-}" "${2:-}" "${3:-}" ;;
    spawn) do_spawn task "$@" ;;
    offer) do_spawn offer "$@" ;;
    kill) do_kill "${1:-}" ;;
    list) do_list ;;
    cull) do_cull ;;
    *) fail "usage: $0 {spawn <name> <prompt-file> [cwd] | offer <name> <fallback-prompt-file> <lead-key> [cwd] | kill <name> | list | cull}" ;;
esac
