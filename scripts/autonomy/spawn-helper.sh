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
#   AUTONOMY_HELPER_CLASS         research (default) or delegate.
#                                 research: reads and reports, writes nothing.
#                                 delegate: may also dispatch voyages and write
#                                 objectives, backlog items and board notes.
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
HELPER_CLASS="${AUTONOMY_HELPER_CLASS:-research}"
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

validate_helper_class() {
    case "$HELPER_CLASS" in
        research|delegate) ;;
        *) fail "AUTONOMY_HELPER_CLASS must be research or delegate" ;;
    esac
}

# A headless helper has nobody to answer a permission prompt, so its class is
# enforced here rather than asserted in prose. Deny wins over allow, so the deny
# list is the real boundary; a research helper cannot write even if its task text
# talks it into trying.
# Claude Code keeps a per-project auto-memory folder for every cwd it runs in. The
# workspace rule allows that folder exactly one line: a pointer to AI-Memory. An unseeded
# folder fills with notes that the next cycle loads as context and that nobody curates,
# so the pointer is written before the runtime starts. An existing MEMORY.md is never
# overwritten. The projects root is overridable for tests; AI_MEMORY_ROOT, when set, names the
# repository's path on this host in the pointer text.
seed_memory_pointer() {
    local cwd="$1"
    local root="${AUTONOMY_CLAUDE_PROJECTS_ROOT:-$HOME/.claude/projects}"
    local memory_repo="${AI_MEMORY_ROOT:-}"
    local key
    key=$(printf '%s' "$cwd" | sed 's#[/.]#-#g')
    local dir="$root/$key/memory"
    mkdir -p "$dir" 2>/dev/null || return 0
    [ -e "$dir/MEMORY.md" ] && return 0
    if [ -n "$memory_repo" ]; then
        printf '%s\n' "Durable memory for every AI tool lives in the AI-Memory repository (on this host: $memory_repo). Write nothing else here." > "$dir/MEMORY.md"
    else
        printf '%s\n' "Durable memory for every AI tool lives in the AI-Memory repository; the operator knows its path on this host. Write nothing else here." > "$dir/MEMORY.md"
    fi
}

prepare_helper_settings() {
    local class="$1"
    local path="$WORKDIR/helper-settings-$class.json"
    local extra_allow="" extra_deny=""

    if [ "$class" = "delegate" ]; then
        extra_allow=''
    else
        extra_deny='
      "mcp__armada__armada_dispatch",
      "mcp__armada__create_objective",
      "mcp__armada__update_objective",
      "mcp__armada__create_backlog_item",
      "mcp__armada__update_backlog_item",
      "mcp__armada__armada_enqueue_merge",
      "mcp__armada__armada_process_merge_entry",
      "mcp__armada__armada_create_incident",
      "mcp__armada__armada_update_incident",
      "mcp__armada__armada_close_incident",
      "mcp__armada__armada_cancel_voyage",
      "mcp__armada__armada_cancel_mission",
      "mcp__armada__armada_restart_mission",
      "mcp__armada__armada_nudge_voyage",
      "mcp__armada__run_check",
      "Write",
      "Edit",'
    fi

    cat > "$path" <<JSON
{
  "permissions": {
    "allow": ["mcp__armada", "Read", "Grep", "Glob", "Bash", "TodoWrite"$extra_allow],
    "deny": [$extra_deny
      "mcp__armada__armada_stop_server",
      "mcp__armada__armada_stop_all",
      "mcp__armada__armada_restore",
      "mcp__armada__armada_backup",
      "mcp__armada__armada_dispatch_hold",
      "mcp__armada__armada_resolve_check",
      "mcp__armada__armada_register_agentwake_session",
      "mcp__armada__armada_objective_scheduler_set",
      "mcp__armada__armada_purge_voyage",
      "mcp__armada__armada_purge_mission",
      "mcp__armada__armada_purge_dock",
      "mcp__armada__armada_purge_merge_queue",
      "mcp__armada__armada_delete_voyages",
      "mcp__armada__armada_delete_missions",
      "mcp__armada__armada_delete_vessel",
      "mcp__armada__armada_delete_captains",
      "mcp__armada__delete_objective",
      "mcp__armada__delete_backlog_item",
      "mcp__armada__approve_deployment",
      "mcp__armada__rollback_deployment",
      "mcp__armada__create_deployment",
      "mcp__armada__create_release",
      "Bash(git push:*)",
      "Bash(git commit:*)",
      "Bash(docker compose:*)",
      "Bash(systemctl:*)"
    ]
  }
}
JSON
    chmod 600 "$path"
    printf '%s\n' "$path"
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
    # An operator-supplied config is theirs to own. It must carry its own
    # X-Armada-Participant header, or the helper receives no directed wakes.
    if [ -n "${AUTONOMY_CLAUDE_MCP_CONFIG:-}" ]; then
        canonical_file "$AUTONOMY_CLAUDE_MCP_CONFIG"
        return
    fi

    local participant_key="${1:-}"
    local mcp_url="${AUTONOMY_ARMADA_MCP_URL:-http://127.0.0.1:7891/mcp}"
    case "$mcp_url" in
        http://*|https://*) ;;
        *) fail "AUTONOMY_ARMADA_MCP_URL must use http or https" ;;
    esac
    case "$mcp_url" in
        *\"*|*\\*|*[[:space:]]*) fail "AUTONOMY_ARMADA_MCP_URL contains unsupported characters" ;;
    esac

    # The participant header identifies this helper to the board, so Armada can
    # return its directed wakes on whatever tool the helper calls next. Without
    # it the helper sees mail only when it reads the board by hand.
    local config_path headers
    if [ -n "$participant_key" ]; then
        case "$participant_key" in
            *[!A-Za-z0-9._:-]*) fail "participant key contains unsupported characters: $participant_key" ;;
        esac
        config_path="$WORKDIR/claude-armada-mcp-$participant_key.json"
        headers=$(printf ',\n      "headers": {\n        "X-Armada-Participant": "%s"\n      }' "$participant_key")
    else
        config_path="$WORKDIR/claude-armada-mcp.json"
        headers=""
    fi

    printf '{\n  "mcpServers": {\n    "armada": {\n      "type": "http",\n      "url": "%s"%s\n    }\n  }\n}\n' \
        "$mcp_url" "$headers" > "$config_path"
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
    local class_line class_limits
    if [ "${HELPER_CLASS:-research}" = "delegate" ]; then
        class_line="You are the bounded DELEGATE helper named $helper_name. You may dispatch
voyages and write objectives, backlog items, incidents and board notes."
        class_limits="Before you dispatch anything: verify the brief's premise against the target tip,
claim the vessel or objective on the board, and confirm the objective names
exactly ONE vessel. Never dispatch onto a vessel another session has claimed, and
never start a second voyage on a vessel that already has an active one. Do not
edit repositories, commit, push, run shared test suites, delete refs, deploy, or
commit durable memory."
    else
        class_line="You are the bounded, READ-ONLY helper named $helper_name."
        class_limits="Do not edit repositories, dispatch voyages, run shared test suites, delete refs,
deploy, or commit durable memory."
    fi

    contract=$(cat <<EOF

[ARMADA HOST HELPER CONTRACT]
$class_line
Your participantKey is $participant_key.
On entry, read the coordination board and heartbeat with that exact key. Drain
UnreadWakes before other work and acknowledge each processed wake with
armada_mark_signal_read. Do only the task above.
$class_limits
Post one addressed outcome note to the lead, release any claim you took, and
exit. Do not start a polling loop or recurring schedule. Do not wait by running a
blocking poll; if you must watch a voyage, use scripts/autonomy/watch-armada.mjs.
Your file sandbox is the helper working directory. If required evidence is
outside it, report the boundary instead of weakening the sandbox or guessing.
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
    seed_memory_pointer "$helper_cwd"
    cd "$helper_cwd"

    case "$(printf '%s' "$runtime" | tr '[:upper:]' '[:lower:]')" in
        opencode) exec opencode run "$prompt" ;;
        claude)
            [ -n "$mcp_config" ] || fail "Claude helper is missing its Armada MCP config"
            # The prompt goes on STDIN. `--mcp-config` is variadic, so a positional
            # prompt after it is consumed as a second config path and the helper
            # dies with "MCP config file not found: <the whole prompt>".
            [ -n "${HELPER_SETTINGS:-}" ] || fail "Claude helper is missing its permission policy"
            exec claude --print --setting-sources project,local --strict-mcp-config \
                --mcp-config "$mcp_config" --settings "$HELPER_SETTINGS" <<<"$prompt"
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
    validate_helper_class
    prune_dead

    local prompt_file helper_cwd participant_key out_log pid alive mcp_config helper_settings
    prompt_file=$(canonical_file "$prompt_arg")
    helper_cwd=$(canonical_directory "$helper_cwd_arg")
    participant_key="$PARTICIPANT_PREFIX-$name"
    out_log="$LOG_DIR/$name.log"
    mcp_config=""
    helper_settings=""
    if [ "$(printf '%s' "$RUNTIME" | tr '[:upper:]' '[:lower:]')" = "claude" ]; then
        mcp_config=$(prepare_claude_mcp_config "$participant_key")
        helper_settings=$(prepare_helper_settings "$HELPER_CLASS")
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
        HELPER_SETTINGS="$helper_settings" \
        AUTONOMY_HELPER_CLASS="$HELPER_CLASS" \
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
