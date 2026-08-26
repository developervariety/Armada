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
#   AUTONOMY_LEAD_MODEL       primary provider model (default claude-fable-5)
#   AUTONOMY_LEAD_SUBAGENT_MODEL
#                             OpenCode read-only subagent model
#                             (default opencode/deepseek-v4-flash)
#   AUTONOMY_LEAD_API_BASE_URL
#                             Anthropic-native provider URL
#                             (default https://api.vilao.ai)
#   AUTONOMY_LEAD_API_KEY_FILE
#                             provider key file (default
#                             $HOME/.armada/secrets/autonomy-lead-vilao.key)
#   AUTONOMY_LEAD_TIMEOUT_MIN wall-clock cap per cycle (default 30)
#   AUTONOMY_LEAD_WORKDIR     shared host/container state root (default
#                             $HOME/.armada/autonomy-lead)
#   AUTONOMY_LEAD_REPO        Armada checkout holding the bootstrap prompt
#                             (default: derived from this script's location)
#   AUTONOMY_ARMADA_MCP_URL   Armada MCP URL (default http://127.0.0.1:7891/mcp)
#   AUTONOMY_LEAD_SERVER_LEASE
#                             use Armada's durable cross-runner lease (default 1)
#   AUTONOMY_LEAD_STANDBY_FALLBACK
#                             request legacy fallback while Grok is primary (default 0)
#   AUTONOMY_SKIP_PREFLIGHT   set to 1 to run even when the Admiral looks down
#
# Usage:
#   lead-cycle.sh run          run one cycle now (refuses if one is running)
#   lead-cycle.sh status       report whether a cycle is running, and the last result
#   lead-cycle.sh kill         stop the running cycle
set -euo pipefail

umask 077

LEAD_KEY="${AUTONOMY_LEAD_KEY:-armada-lead}"
RUNTIME="${AUTONOMY_LEAD_RUNTIME:-claude}"
MODEL="${AUTONOMY_LEAD_MODEL:-claude-fable-5}"
SUBAGENT_MODEL="${AUTONOMY_LEAD_SUBAGENT_MODEL:-opencode/deepseek-v4-flash}"
API_BASE_URL="${AUTONOMY_LEAD_API_BASE_URL:-https://api.vilao.ai}"
API_KEY_FILE="${AUTONOMY_LEAD_API_KEY_FILE:-$HOME/.armada/secrets/autonomy-lead-vilao.key}"
TIMEOUT_MIN="${AUTONOMY_LEAD_TIMEOUT_MIN:-30}"
WORKDIR="${AUTONOMY_LEAD_WORKDIR:-$HOME/.armada/autonomy-lead}"
# This script lives in the checkout, so the checkout is two levels up. Deriving
# it beats a hard-coded path: it works on any host and in any clone.
SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)
REPO="${AUTONOMY_LEAD_REPO:-$(CDPATH= cd -- "$SCRIPT_DIR/../.." && pwd -P)}"
MCP_URL="${AUTONOMY_ARMADA_MCP_URL:-http://127.0.0.1:7891/mcp}"
SERVER_LEASE_ENABLED="${AUTONOMY_LEAD_SERVER_LEASE:-1}"
STANDBY_FALLBACK="${AUTONOMY_LEAD_STANDBY_FALLBACK:-0}"
SERVER_CYCLE_ID=""

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
    case "$MODEL" in
        ''|*[!A-Za-z0-9._:\[\]-]*) fail "AUTONOMY_LEAD_MODEL contains unsupported characters" ;;
    esac
    case "$SUBAGENT_MODEL" in
        ''|*[!A-Za-z0-9._:/#\[\]-]*) fail "AUTONOMY_LEAD_SUBAGENT_MODEL contains unsupported characters" ;;
    esac
    case "$API_BASE_URL" in
        http://*|https://*) ;;
        *) fail "AUTONOMY_LEAD_API_BASE_URL must use http or https" ;;
    esac
    case "$API_BASE_URL" in
        *\"*|*\\*|*[[:space:]]*) fail "AUTONOMY_LEAD_API_BASE_URL contains unsupported characters" ;;
    esac
    case "$MCP_URL" in
        http://*|https://*) ;;
        *) fail "AUTONOMY_ARMADA_MCP_URL must use http or https" ;;
    esac
    case "$SERVER_LEASE_ENABLED" in 0|1) ;; *) fail "AUTONOMY_LEAD_SERVER_LEASE must be 0 or 1" ;; esac
    case "$STANDBY_FALLBACK" in 0|1) ;; *) fail "AUTONOMY_LEAD_STANDBY_FALLBACK must be 0 or 1" ;; esac
    if [ "$SERVER_LEASE_ENABLED" = "1" ]; then
        command -v curl >/dev/null 2>&1 || fail "curl is required when the Armada server lease is enabled"
        command -v jq >/dev/null 2>&1 || fail "jq is required when the Armada server lease is enabled"
    fi
    case "$MCP_URL" in
        *\"*|*\\*|*[[:space:]]*) fail "AUTONOMY_ARMADA_MCP_URL contains unsupported characters" ;;
    esac
    [ -f "$REPO/docs/autonomy/lead-bootstrap-prompt.md" ] \
        || fail "bootstrap prompt not found under $REPO"

    case "$(printf '%s' "$RUNTIME" | tr '[:upper:]' '[:lower:]')" in
        claude|opencode)
            [ -f "$API_KEY_FILE" ] || fail "provider key file not found: $API_KEY_FILE"
            [ -s "$API_KEY_FILE" ] || fail "provider key file is empty: $API_KEY_FILE"
            [ -r "$API_KEY_FILE" ] || fail "provider key file is not readable: $API_KEY_FILE"
            local key_mode key_lines
            if key_mode=$(stat -c '%a' "$API_KEY_FILE" 2>/dev/null) \
                || key_mode=$(stat -f '%Lp' "$API_KEY_FILE" 2>/dev/null); then
                case "$key_mode" in
                    *00) ;;
                    *) fail "provider key file must not grant group or other access: $API_KEY_FILE" ;;
                esac
            fi
            key_lines=$(awk 'END { print NR }' "$API_KEY_FILE")
            [ "$key_lines" -eq 1 ] || fail "provider key file must contain exactly one line: $API_KEY_FILE"
            ;;
    esac
}

mcp_call() {
    local tool="$1"
    local arguments_json="$2"
    local request response json_rpc tool_text
    request=$(jq -cn --arg tool "$tool" --argjson arguments "$arguments_json" \
        '{jsonrpc:"2.0",id:1,method:"tools/call",params:{name:$tool,arguments:$arguments}}')
    response=$(curl -fsS -m 15 \
        -X POST "$MCP_URL" \
        -H 'Content-Type: application/json' \
        -H 'Accept: application/json, text/event-stream' \
        -H "X-Armada-Participant: $LEAD_KEY" \
        --data-binary "$request") || return 1

    if printf '%s' "$response" | jq -e . >/dev/null 2>&1; then
        json_rpc="$response"
    else
        json_rpc=$(printf '%s\n' "$response" \
            | awk '/^data:/ { sub(/^data:[[:space:]]*/, ""); value=$0 } END { print value }')
    fi
    [ -n "$json_rpc" ] || return 1
    printf '%s' "$json_rpc" | jq -e '.error == null' >/dev/null 2>&1 || return 1
    tool_text=$(printf '%s' "$json_rpc" \
        | jq -r '[.result.content[]? | select(.type == "text") | .text][0] // empty')
    [ -n "$tool_text" ] || return 1
    printf '%s\n' "$tool_text"
}

begin_server_cycle() {
    [ "$SERVER_LEASE_ENABLED" = "1" ] || return 0
    local result acquired reason
    result=$(mcp_call armada_lead_cycle_begin \
        "$(jq -cn --argjson fallback "$STANDBY_FALLBACK" '{standbyFallback:($fallback == 1)}')") \
        || fail "could not acquire the Armada lead-cycle lease"
    acquired=$(printf '%s' "$result" | jq -r '.Acquired // .acquired // false')
    if [ "$acquired" != "true" ]; then
        reason=$(printf '%s' "$result" | jq -r '.RefusalReason // .refusalReason // "cycle refused"')
        echo "skipped: $reason"
        printf '%s skipped server-lease-refused reason=%s\n' \
            "$(date -u +%Y%m%dT%H%M%SZ)" "$reason" > "$LAST_FILE"
        exit 0
    fi
    SERVER_CYCLE_ID=$(printf '%s' "$result" | jq -r '.CycleId // .cycleId // empty')
    [ -n "$SERVER_CYCLE_ID" ] || fail "Armada acquired a lead cycle without returning its ID"
}

close_server_cycle() {
    local runtime_status="$1"
    [ "$SERVER_LEASE_ENABLED" = "1" ] || return "$runtime_status"
    [ -n "$SERVER_CYCLE_ID" ] || return "$runtime_status"

    local current active current_id reason
    if ! current=$(mcp_call armada_lead_cycle_status '{}'); then
        if [ "$runtime_status" -eq 0 ]; then
            echo "lead cycle could not confirm its server-side completion state" >&2
            return 3
        fi
        return "$runtime_status"
    fi
    active=$(printf '%s' "$current" | jq -r '.Active // .active // false')
    current_id=$(printf '%s' "$current" | jq -r '.CycleId // .cycleId // empty')
    if [ "$active" != "true" ] || [ "$current_id" != "$SERVER_CYCLE_ID" ]; then
        SERVER_CYCLE_ID=""
        return "$runtime_status"
    fi

    if [ "$runtime_status" -eq 0 ]; then
        reason="legacy runtime exited without posting its server-side completion handoff"
        mcp_call armada_lead_cycle_fail \
            "$(jq -cn --arg cycleId "$SERVER_CYCLE_ID" --arg reason "$reason" '{cycleId:$cycleId,reason:$reason}')" \
            >/dev/null 2>&1 || true
        SERVER_CYCLE_ID=""
        echo "lead cycle violated its completion contract: $reason" >&2
        return 3
    fi

    reason="legacy runtime exited with status $runtime_status"
    mcp_call armada_lead_cycle_fail \
        "$(jq -cn --arg cycleId "$SERVER_CYCLE_ID" --arg reason "$reason" '{cycleId:$cycleId,reason:$reason}')" \
        >/dev/null 2>&1 || true
    SERVER_CYCLE_ID=""
    return "$runtime_status"
}

cleanup_cycle() {
    rm -f "$PID_FILE"
    if [ "$SERVER_LEASE_ENABLED" = "1" ] && [ -n "$SERVER_CYCLE_ID" ]; then
        local reason="legacy launcher exited before the lead cycle closed"
        mcp_call armada_lead_cycle_fail \
            "$(jq -cn --arg cycleId "$SERVER_CYCLE_ID" --arg reason "$reason" '{cycleId:$cycleId,reason:$reason}')" \
            >/dev/null 2>&1 || true
        SERVER_CYCLE_ID=""
    fi
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

prepare_settings() {
    local path="$WORKDIR/lead-settings.json"
    # An unattended run has nobody to answer a permission prompt, so the policy
    # has to be stated up front. Allow the Armada surface and ordinary file and
    # shell work; deny what must stay an owner action even when the lead judges
    # it useful. Deny wins over allow, so the list below is the real boundary.
    #
    # The denials are not arbitrary. Fleet-destructive and purge tools cannot be
    # undone; deployment and release tools reach outside the repository;
    # armada_resolve_check could manufacture a green gate; armada_dispatch_hold
    # is fleet-wide and would freeze every peer session; and the AgentWake
    # registration tool would let a cycle re-point the autonomy at itself.
    cat > "$path" <<'JSON'
{
  "permissions": {
    "allow": [
      "mcp__armada",
      "Read", "Grep", "Glob", "Write", "Edit", "TodoWrite", "Task",
      "Bash"
    ],
    "deny": [
      "mcp__armada__armada_stop_server",
      "mcp__armada__armada_stop_all",
      "mcp__armada__armada_restore",
      "mcp__armada__armada_backup",
      "mcp__armada__armada_dispatch_hold",
      "mcp__armada__armada_resolve_check",
      "mcp__armada__armada_register_agentwake_session",
      "mcp__armada__armada_objective_scheduler_set",
      "mcp__armada__armada_delete_captain",
      "mcp__armada__armada_delete_captains",
      "mcp__armada__armada_delete_dock",
      "mcp__armada__armada_delete_docks",
      "mcp__armada__armada_delete_event",
      "mcp__armada__armada_delete_events",
      "mcp__armada__armada_delete_fleet",
      "mcp__armada__armada_delete_fleets",
      "mcp__armada__armada_delete_incident",
      "mcp__armada__armada_delete_merge",
      "mcp__armada__armada_delete_missions",
      "mcp__armada__armada_delete_signals",
      "mcp__armada__armada_delete_vessel",
      "mcp__armada__armada_delete_vessels",
      "mcp__armada__armada_delete_voyages",
      "mcp__armada__armada_purge_dock",
      "mcp__armada__armada_purge_merge_entries",
      "mcp__armada__armada_purge_merge_entry",
      "mcp__armada__armada_purge_merge_queue",
      "mcp__armada__armada_purge_mission",
      "mcp__armada__armada_purge_voyage",
      "mcp__armada__delete_objective",
      "mcp__armada__delete_backlog_item",
      "mcp__armada__delete_playbook",
      "mcp__armada__delete_runbook",
      "mcp__armada__delete_runbook_execution",
      "mcp__armada__delete_environment",
      "mcp__armada__delete_persona",
      "mcp__armada__delete_pipeline",
      "mcp__armada__delete_workflow_profile",
      "mcp__armada__create_deployment",
      "mcp__armada__update_deployment",
      "mcp__armada__approve_deployment",
      "mcp__armada__rollback_deployment",
      "mcp__armada__verify_deployment",
      "mcp__armada__create_release",
      "mcp__armada__update_release",
      "Bash(git push --force:*)",
      "Bash(git push -f:*)",
      "Bash(docker compose:*)",
      "Bash(systemctl:*)",
      "Bash(rm -rf /:*)"
    ]
  }
}
JSON
    chmod 600 "$path"
    printf '%s\n' "$path"
}

prepare_opencode_config() {
    local path="$WORKDIR/lead-opencode.json"
    local primary_model="vilao/$MODEL"
    local key_reference="{file:$API_KEY_FILE}"

    # This overlay belongs to the lead process only. The global OpenCode config
    # is also used by captains, so putting the lead model, participant header, or
    # permission policy there would silently turn every OpenCode captain into the
    # lead. OPENCODE_CONFIG merges this file over the global provider credentials
    # for this process and its child sessions only.
    jq -n \
        --arg apiBaseUrl "$API_BASE_URL" \
        --arg apiKey "$key_reference" \
        --arg model "$MODEL" \
        --arg primaryModel "$primary_model" \
        --arg subagentModel "$SUBAGENT_MODEL" \
        --arg mcpUrl "$MCP_URL" \
        --arg leadKey "$LEAD_KEY" \
        '{
          "$schema": "https://opencode.ai/config.json",
          model: $primaryModel,
          small_model: $subagentModel,
          subagent_depth: 1,
          provider: {
            vilao: {
              npm: "@ai-sdk/anthropic",
              name: "Vilao",
              options: {
                baseURL: $apiBaseUrl,
                apiKey: $apiKey,
                setCacheKey: true
              },
              models: {
                ($model): {
                  name: "Vilao Claude Fable 5"
                }
              }
            }
          },
          mcp: {
            armada: {
              type: "remote",
              url: $mcpUrl,
              enabled: true,
              oauth: false,
              headers: {
                "X-Armada-Participant": $leadKey
              }
            }
          },
          permission: {
            question: "deny",
            armada_armada_stop_server: "deny",
            armada_armada_stop_all: "deny",
            armada_armada_restore: "deny",
            armada_armada_backup: "deny",
            armada_armada_dispatch_hold: "deny",
            armada_armada_resolve_check: "deny",
            armada_armada_register_agentwake_session: "deny",
            armada_armada_objective_scheduler_set: "deny",
            armada_armada_delete_captain: "deny",
            armada_armada_delete_captains: "deny",
            armada_armada_delete_dock: "deny",
            armada_armada_delete_docks: "deny",
            armada_armada_delete_event: "deny",
            armada_armada_delete_events: "deny",
            armada_armada_delete_fleet: "deny",
            armada_armada_delete_fleets: "deny",
            armada_armada_delete_incident: "deny",
            armada_armada_delete_merge: "deny",
            armada_armada_delete_missions: "deny",
            armada_armada_delete_signals: "deny",
            armada_armada_delete_vessel: "deny",
            armada_armada_delete_vessels: "deny",
            armada_armada_delete_voyages: "deny",
            armada_armada_purge_dock: "deny",
            armada_armada_purge_merge_entries: "deny",
            armada_armada_purge_merge_entry: "deny",
            armada_armada_purge_merge_queue: "deny",
            armada_armada_purge_mission: "deny",
            armada_armada_purge_voyage: "deny",
            armada_delete_objective: "deny",
            armada_delete_backlog_item: "deny",
            armada_delete_playbook: "deny",
            armada_delete_runbook: "deny",
            armada_delete_runbook_execution: "deny",
            armada_delete_environment: "deny",
            armada_delete_persona: "deny",
            armada_delete_pipeline: "deny",
            armada_delete_workflow_profile: "deny",
            armada_create_deployment: "deny",
            armada_update_deployment: "deny",
            armada_approve_deployment: "deny",
            armada_rollback_deployment: "deny",
            armada_verify_deployment: "deny",
            armada_create_release: "deny",
            armada_update_release: "deny",
            bash: {
              "*": "allow",
              "git push --force*": "deny",
              "git push -f *": "deny",
              "docker compose *": "deny",
              "systemctl *": "deny",
              "rm -rf /*": "deny"
            }
          },
          agent: {
            build: {
              model: $primaryModel
            },
            explore: {
              model: $subagentModel
            },
            general: {
              description: "Read-only Armada fleet and repository investigator. Return evidence to the lead and never change state.",
              model: $subagentModel,
              tools: {
                write: false,
                edit: false,
                patch: false,
                bash: false
              },
              permission: {
                question: "deny",
                armada_armada_coordination_post: "deny",
                armada_armada_coordination_claim: "deny",
                armada_armada_mark_signal_read: "deny",
                armada_armada_update_objective: "deny",
                armada_update_objective: "deny",
                armada_create_objective: "deny",
                armada_armada_mark_objective_auto_dispatchable: "deny",
                armada_armada_enqueue_merge: "deny",
                armada_armada_process_merge_entry: "deny",
                armada_armada_close_incident: "deny",
                armada_armada_update_incident: "deny",
                armada_armada_cancel_mission: "deny",
                armada_armada_cancel_voyage: "deny",
                armada_armada_dispatch: "deny",
                armada_armada_nudge_voyage: "deny",
                armada_armada_signal: "deny"
              }
            },
            title: { model: $subagentModel },
            summary: { model: $subagentModel },
            compaction: { model: $subagentModel }
          }
        }' > "$path"
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

This launch already acquired Armada lead cycle ${SERVER_CYCLE_ID:-without-server-lease}.
When a cycle ID starts with lcy_, call armada_lead_cycle_heartbeat during long
work. When your pass is done, release your claims, stop helpers, and make
armada_lead_cycle_complete with that cycle ID and your handoff text your final
Armada action: that tool posts the handoff to the board itself, once. Do not
post the handoff with armada_coordination_post, do not re-post on a failed
call, and never pass roomKey. If you must stop early, call armada_lead_cycle_fail with a
clear reason. The launcher marks a successful process as failed if it leaves its
server lease open.

Nobody is watching this cycle. That changes three things:
- You cannot ask a question and wait. When a decision belongs to the owner,
  post it to the board as a named OWNER DECISION and continue with other work.
- Run ONE bounded pass, hand off through armada_lead_cycle_complete, release every claim, stop every helper
  you started, and exit. Do not start a polling loop; the next cycle is started
  for you.
- Prefer work that is reversible and provable. Do not enable AgentWake process
  delivery, do not force-push, do not deploy, and do not merge a PR.

You have about $TIMEOUT_MIN minutes of wall clock. Reserve the last three for the
handoff note and cleanup. If a voyage is still running when your time is nearly
gone, say so plainly in the handoff and leave it for the next cycle rather than
waiting on it.

Do not wait by running a blocking poll. To watch a voyage, start
$REPO/scripts/autonomy/watch-armada.mjs and read its lines. A blocking shell loop
inside one tool call produces no visible progress, cannot see a directed board
note while it runs, and has ended turns mid-work.
EOF

    if [ "$(printf '%s' "$RUNTIME" | tr '[:upper:]' '[:lower:]')" = "opencode" ]; then
        cat <<EOF

[COST-AWARE DELEGATION]
You are the primary lead on $MODEL. Use the OpenCode explore subagent for
repository searches and the general subagent for read-only fleet inspection.
Those child agents run on $SUBAGENT_MODEL. Give each child one bounded,
independent question and ask for exact evidence. You must make all decisions and
all state changes yourself. Never delegate a write, dispatch, merge, incident
change, signal acknowledgement, coordination claim, or board post. Parallel
read-only work is useful; parallel control of Armada is forbidden. Combine
related reads into one child task. Each return to the primary is another Vilao
request, so do not split one investigation into many small child tasks.
EOF
    fi

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

    # A tick that lands while the Admiral is rebuilding has nothing to talk to. It
    # would burn a cycle failing at its first tool call, and its log would read as
    # a broken lead rather than a redeploy in progress.
    #
    # This matters more than it looks: the alternative was to STOP the timer for
    # every deploy and start it again afterwards. That is a manual step, and it was
    # missed -- the lead sat idle for an hour because whoever redeployed did not
    # restart it. Skipping cleanly here is what lets the timer be left alone.
    if [ "${AUTONOMY_SKIP_PREFLIGHT:-0}" != "1" ]; then
        if ! curl -s -o /dev/null -m 10 \
            -X POST "$MCP_URL" \
            -H 'Content-Type: application/json' \
            -H 'Accept: application/json, text/event-stream' \
            --data-binary '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}' 2>/dev/null
        then
            echo "skipped: the Admiral is not answering at $MCP_URL (redeploy or outage)"
            mkdir -p "$RUN_DIR"
            printf '%s skipped admiral-unreachable\n' "$(date -u +%Y%m%dT%H%M%SZ)" > "$LAST_FILE"
            exit 0
        fi
    fi

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

    begin_server_cycle
    trap cleanup_cycle EXIT

    # GNU coreutils on the admiral host; gtimeout where Homebrew supplies it.
    # A cycle with no cap could run until the provider gives up, so refuse to
    # pretend: run uncapped only after saying so out loud.
    local timeout_bin=""
    if command -v timeout >/dev/null 2>&1; then timeout_bin="timeout"
    elif command -v gtimeout >/dev/null 2>&1; then timeout_bin="gtimeout"
    else echo "warning: no timeout binary; running this cycle UNCAPPED" >&2
    fi

    local stamp log raw prompt_file mcp_config settings_file opencode_config runtime status
    stamp=$(date -u +%Y%m%dT%H%M%SZ)
    log="$LOG_DIR/cycle-$stamp.log"
    raw="$LOG_DIR/cycle-$stamp.jsonl"
    prompt_file="$RUN_DIR/prompt-$stamp.md"
    mcp_config=$(prepare_mcp_config)
    settings_file=$(prepare_settings)
    opencode_config=""
    runtime=$(printf '%s' "$RUNTIME" | tr '[:upper:]' '[:lower:]')

    build_prompt > "$prompt_file"

    # Run from the checkout, whoever started us. The systemd unit sets
    # WorkingDirectory, but a wake or a hand-run does not, and the runtime loads its
    # project rules relative to the working directory -- a cycle started from $HOME
    # silently gets no repository instructions.
    seed_memory_pointer "$REPO"
    cd "$REPO" || fail "cannot enter the Armada checkout at $REPO"
    echo $$ > "$PID_FILE"

    echo "lead cycle $stamp starting key=$LEAD_KEY runtime=$runtime timeout=${TIMEOUT_MIN}m"

    local -a cap=()
    [ -n "$timeout_bin" ] && cap=("$timeout_bin" --signal=TERM --kill-after=60 "${TIMEOUT_MIN}m")

    set +e
    case "$runtime" in
        claude)
            # The prompt goes on STDIN, not argv. `--mcp-config` is variadic, so a
            # positional prompt after it is swallowed as a second config path and
            # the run dies with "MCP config file not found: <the whole prompt>".
            # stdin also sidesteps the argv length limit for a long brief.
            #
            # Capture the whole event stream, not just the final message. `--print`
            # alone emits one closing paragraph: an eight-minute cycle that did real
            # work once left a 73-byte log claiming it had nothing to report. The raw
            # .jsonl is written incrementally, so it survives a killed run and the
            # digest below can still be rendered from a partial stream.
            local api_key
            api_key=$(tr -d '\r\n' < "$API_KEY_FILE")
            ${cap[@]+"${cap[@]}"} env -u ANTHROPIC_AUTH_TOKEN \
                ANTHROPIC_BASE_URL="$API_BASE_URL" \
                ANTHROPIC_API_KEY="$api_key" \
                claude --print \
                --output-format stream-json --verbose \
                --model "$MODEL" \
                --setting-sources project,local \
                --strict-mcp-config --mcp-config "$mcp_config" \
                --settings "$settings_file" \
                --add-dir "$WORKDIR" \
                < "$prompt_file" > "$raw" 2>&1
            ;;
        codex)
            ${cap[@]+"${cap[@]}"} codex exec - < "$prompt_file" > "$log" 2>&1
            ;;
        opencode)
            opencode_config=$(prepare_opencode_config)
            ${cap[@]+"${cap[@]}"} env OPENCODE_CONFIG="$opencode_config" \
                opencode run --model "vilao/$MODEL" --agent build --format json \
                "$(cat "$prompt_file")" > "$raw" 2>&1
            ;;
    esac
    status=$?
    set -e

    set +e
    close_server_cycle "$status"
    status=$?
    set -e

    if [ -s "$raw" ]; then
        if node "$SCRIPT_DIR/render-cycle-log.mjs" "$raw" > "$log" 2>/dev/null; then
            :
        else
            # Never lose the run because the renderer failed. The raw stream is the
            # source of truth; fall back to it verbatim.
            echo "(render failed; raw stream follows)" > "$log"
            cat "$raw" >> "$log"
        fi
    fi

    # 124 is timeout's own code. It means the cap was reached, which is a bounded
    # outcome and not a crash; say which it was so a reader does not guess.
    if [ "$status" -eq 124 ]; then
        printf '%s timeout after %sm log=%s raw=%s\n' "$stamp" "$TIMEOUT_MIN" "$log" "$raw" > "$LAST_FILE"
        echo "lead cycle $stamp hit its ${TIMEOUT_MIN}m cap; log=$log"
    elif [ "$status" -ne 0 ]; then
        printf '%s failed exit=%s log=%s raw=%s\n' "$stamp" "$status" "$log" "$raw" > "$LAST_FILE"
        echo "lead cycle $stamp failed with exit $status; log=$log"
    else
        printf '%s ok log=%s raw=%s\n' "$stamp" "$log" "$raw" > "$LAST_FILE"
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
    echo "model:   $MODEL"
    if [ "$(printf '%s' "$RUNTIME" | tr '[:upper:]' '[:lower:]')" = "opencode" ]; then
        echo "reader:  $SUBAGENT_MODEL"
    fi
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
