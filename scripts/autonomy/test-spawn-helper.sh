#!/usr/bin/env bash
# Contract test for spawn-helper.sh. It uses a local fake runtime and does not
# contact Armada or a model provider.
set -euo pipefail

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)
SPAWN_HELPER="$SCRIPT_DIR/spawn-helper.sh"
TEST_ROOT=$(mktemp -d "${TMPDIR:-/tmp}/armada-autonomy-test.XXXXXX")
STATE_ROOT="$TEST_ROOT/state"
CAPTURE_FILE="$TEST_ROOT/capture.txt"
PROMPT_FILE="$TEST_ROOT/task.md"
FAKE_RUNTIME="$TEST_ROOT/fake-runtime.sh"

cleanup() {
    AUTONOMY_WORKDIR="$STATE_ROOT" "$SPAWN_HELPER" kill alpha >/dev/null 2>&1 || true
    AUTONOMY_WORKDIR="$STATE_ROOT" "$SPAWN_HELPER" kill beta >/dev/null 2>&1 || true
    AUTONOMY_WORKDIR="$STATE_ROOT" "$SPAWN_HELPER" kill gamma >/dev/null 2>&1 || true
    AUTONOMY_WORKDIR="$STATE_ROOT" "$SPAWN_HELPER" kill claudeprobe >/dev/null 2>&1 || true
    rm -rf "$TEST_ROOT"
}
trap cleanup EXIT INT TERM

fail() {
    echo "FAIL: $*" >&2
    exit 1
}

wait_for_file() {
    local path="$1"
    local attempt=0
    while [ ! -s "$path" ] && [ "$attempt" -lt 50 ]; do
        sleep 0.1
        attempt=$((attempt + 1))
    done
    [ -s "$path" ] || fail "timed out waiting for $path"
}

bash -n "$SPAWN_HELPER"

cat > "$FAKE_RUNTIME" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$1" > "${AUTONOMY_TEST_CAPTURE:?}"
while :; do sleep 1; done
EOF
chmod +x "$FAKE_RUNTIME"

cat > "$PROMPT_FILE" <<'EOF'
Check the repository-wide census.
Report the exact count and the command used.
EOF

run_helper() {
    AUTONOMY_MAX_HELPERS=1 \
    AUTONOMY_HELPER_TIMEOUT_MIN=1 \
    AUTONOMY_RUNTIME=command \
    AUTONOMY_COMMAND="$FAKE_RUNTIME" \
    AUTONOMY_WORKDIR="$STATE_ROOT" \
    AUTONOMY_PARTICIPANT_PREFIX=probe \
    AUTONOMY_TEST_CAPTURE="$CAPTURE_FILE" \
    "$SPAWN_HELPER" "$@"
}

run_helper spawn alpha "$PROMPT_FILE" "$TEST_ROOT" >/dev/null
wait_for_file "$CAPTURE_FILE"

grep -Fq "Check the repository-wide census." "$CAPTURE_FILE" || fail "task prompt was not passed"
grep -Fq "Your participantKey is probe-alpha." "$CAPTURE_FILE" || fail "participant key was not injected"
grep -Fq "Do not edit repositories" "$CAPTURE_FILE" || fail "read-only contract was not injected"
grep -Fq "acknowledge each processed wake with" "$CAPTURE_FILE" || fail "wake acknowledgement was not injected"
grep -Fq "and exit. Do not start a polling loop" "$CAPTURE_FILE" || fail "bounded exit contract was not injected"

list_output=$(run_helper list)
printf '%s' "$list_output" | grep -Fq "alpha" || fail "running helper was not listed"
printf '%s' "$list_output" | grep -Fq "probe-alpha" || fail "participant key was not listed"
printf '%s' "$list_output" | grep -Fq "running: 1/1" || fail "cap usage was not listed"

if run_helper spawn alpha "$PROMPT_FILE" "$TEST_ROOT" >/dev/null 2>&1; then
    fail "duplicate helper name was accepted"
fi
if run_helper spawn second "$PROMPT_FILE" "$TEST_ROOT" >/dev/null 2>&1; then
    fail "concurrency cap was not enforced"
fi
if run_helper spawn ../bad "$PROMPT_FILE" "$TEST_ROOT" >/dev/null 2>&1; then
    fail "unsafe helper name was accepted"
fi

run_helper kill alpha >/dev/null
[ ! -e "$STATE_ROOT/run/alpha.pid" ] || fail "kill left process state"

: > "$CAPTURE_FILE"
run_helper offer beta "$PROMPT_FILE" lead-owner "$TEST_ROOT" >/dev/null
wait_for_file "$CAPTURE_FILE"
grep -Fq "Your lead participantKey is lead-owner." "$CAPTURE_FILE" || fail "offer lead key was not injected"
grep -Fq "bounded 240-second reassignment window" "$CAPTURE_FILE" || fail "bounded offer window was not injected"
offer_list=$(run_helper list)
printf '%s' "$offer_list" | grep -Fq "offer" || fail "offer mode was not listed"
printf '%s' "$offer_list" | grep -Fq "lead-owner" || fail "offer lead key was not listed"
run_helper kill beta >/dev/null

: > "$CAPTURE_FILE"
run_helper spawn gamma "$PROMPT_FILE" "$TEST_ROOT" >/dev/null
wait_for_file "$CAPTURE_FILE"
echo 0 > "$STATE_ROOT/run/gamma.start"
run_helper cull >/dev/null
[ ! -e "$STATE_ROOT/run/gamma.pid" ] || fail "cull left expired process state"

if AUTONOMY_RUNTIME=unknown AUTONOMY_WORKDIR="$STATE_ROOT" "$SPAWN_HELPER" spawn badruntime "$PROMPT_FILE" "$TEST_ROOT" >/dev/null 2>&1; then
    fail "unknown runtime was accepted"
fi

cat > "$TEST_ROOT/claude" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$@" > "${AUTONOMY_TEST_CAPTURE:?}"
while :; do sleep 1; done
EOF
chmod +x "$TEST_ROOT/claude"
: > "$CAPTURE_FILE"
PATH="$TEST_ROOT:$PATH" \
AUTONOMY_MAX_HELPERS=1 \
AUTONOMY_RUNTIME=claude \
AUTONOMY_WORKDIR="$STATE_ROOT" \
AUTONOMY_PARTICIPANT_PREFIX=probe \
AUTONOMY_TEST_CAPTURE="$CAPTURE_FILE" \
"$SPAWN_HELPER" spawn claudeprobe "$PROMPT_FILE" "$TEST_ROOT" >/dev/null
wait_for_file "$CAPTURE_FILE"
grep -Fxq -- "--strict-mcp-config" "$CAPTURE_FILE" || fail "Claude strict MCP flag was not passed"
grep -Fxq -- "--mcp-config" "$CAPTURE_FILE" || fail "Claude explicit MCP config flag was not passed"
CLAUDE_MCP_CONFIG="$STATE_ROOT/claude-armada-mcp-probe-claudeprobe.json"
grep -Fq '"mcpServers"' "$CLAUDE_MCP_CONFIG" || fail "Claude Armada MCP config was not generated"
grep -Fq 'http://127.0.0.1:7891/mcp' "$CLAUDE_MCP_CONFIG" || fail "Claude Armada MCP URL was not generated"
# Without this header the helper is anonymous to the board, so Armada cannot
# return its directed wakes on an ordinary tool call.
grep -Fq '"X-Armada-Participant": "probe-claudeprobe"' "$CLAUDE_MCP_CONFIG" \
    || fail "Claude Armada MCP config did not carry the participant header"
python3 -c 'import json,sys; json.load(open(sys.argv[1]))' "$CLAUDE_MCP_CONFIG" \
    || fail "Claude Armada MCP config is not valid JSON"
AUTONOMY_WORKDIR="$STATE_ROOT" "$SPAWN_HELPER" kill claudeprobe >/dev/null

echo "PASS: bounded helper lifecycle"
