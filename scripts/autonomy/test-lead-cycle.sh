#!/usr/bin/env bash
# Contract test for lead-cycle.sh. It uses a fake runtime and never contacts
# Armada or a model provider.
set -euo pipefail

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd -P)
LEAD_CYCLE="$SCRIPT_DIR/lead-cycle.sh"
REPO_ROOT=$(CDPATH= cd -- "$SCRIPT_DIR/../.." && pwd -P)
TEST_ROOT=$(mktemp -d "${TMPDIR:-/tmp}/armada-lead-test.XXXXXX")
WORKDIR="$TEST_ROOT/state"

cleanup() { rm -rf "$TEST_ROOT"; }
trap cleanup EXIT INT TERM

fail() { echo "FAIL: $*" >&2; exit 1; }

bash -n "$LEAD_CYCLE"

# A fake `claude` that records the prompt and the MCP config path it was given.
mkdir -p "$TEST_ROOT/bin"
cat > "$TEST_ROOT/bin/claude" <<'EOF'
#!/usr/bin/env bash
# Stands in for the real CLI, including its variadic --mcp-config: every
# following non-flag argument is treated as another config path. A launcher that
# passes the prompt positionally therefore fails here exactly as it does live.
set -euo pipefail
args=("$@")
configs=()
collecting=0
for a in "${args[@]}"; do
    if [ "$a" = "--mcp-config" ]; then collecting=1; continue; fi
    if [ "$collecting" = "1" ]; then
        case "$a" in
            --*) collecting=0 ;;
            *) configs+=("$a"); continue ;;
        esac
    fi
done
for c in ${configs[@]+"${configs[@]}"}; do
    [ -f "$c" ] || { echo "Error: MCP config file not found: $c" >&2; exit 1; }
done
[ "${#configs[@]}" -gt 0 ] && printf '%s\n' "${configs[0]}" > "$LEAD_TEST_CONFIG_PATH"
cat > "$LEAD_TEST_PROMPT"
EOF
chmod +x "$TEST_ROOT/bin/claude"

export LEAD_TEST_PROMPT="$TEST_ROOT/prompt.txt"
export LEAD_TEST_CONFIG_PATH="$TEST_ROOT/config-path.txt"

run_lead() {
    PATH="$TEST_ROOT/bin:$PATH" \
    AUTONOMY_LEAD_WORKDIR="$WORKDIR" \
    AUTONOMY_LEAD_REPO="$REPO_ROOT" \
    AUTONOMY_LEAD_KEY="${LEAD_KEY_OVERRIDE:-probe-lead}" \
    AUTONOMY_LEAD_TIMEOUT_MIN=5 \
    "$LEAD_CYCLE" "$@"
}

# --- validation -----------------------------------------------------------
if LEAD_KEY_OVERRIDE='bad key' run_lead run >/dev/null 2>&1; then
    fail "a participant key with a space was accepted"
fi
if AUTONOMY_LEAD_RUNTIME=notareal run_lead run >/dev/null 2>&1; then
    fail "an unknown runtime was accepted"
fi
if AUTONOMY_ARMADA_MCP_URL='ftp://x' run_lead run >/dev/null 2>&1; then
    fail "a non-http MCP URL was accepted"
fi

# --- one successful cycle -------------------------------------------------
run_lead run >/dev/null
[ -s "$LEAD_TEST_PROMPT" ] || fail "the runtime received no prompt"

grep -Fq "Your participantKey is probe-lead." "$LEAD_TEST_PROMPT" \
    || fail "the cycle did not inject its participant key"
grep -Fq "[AUTONOMOUS CYCLE CONTRACT]" "$LEAD_TEST_PROMPT" \
    || fail "the unattended contract was not injected"
grep -Fq "post a handoff, release every claim" "$LEAD_TEST_PROMPT" \
    || fail "the bounded-exit contract was not injected"
grep -Fq "You are the autonomous lead operator" "$LEAD_TEST_PROMPT" \
    || fail "the bootstrap prompt body was not included"
# The doc's front matter explains the file to a human and must not reach the model.
grep -Fq "Use this prompt for one fresh lead cycle" "$LEAD_TEST_PROMPT" \
    && fail "the human-facing doc preamble leaked into the prompt"

CONFIG=$(cat "$LEAD_TEST_CONFIG_PATH")
[ -f "$CONFIG" ] || fail "the MCP config was not written"
python3 -c 'import json,sys; json.load(open(sys.argv[1]))' "$CONFIG" \
    || fail "the MCP config is not valid JSON"
grep -Fq '"X-Armada-Participant": "probe-lead"' "$CONFIG" \
    || fail "the MCP config did not carry the participant header"

grep -Fq " ok " "$WORKDIR/run/last-result" || fail "a successful cycle was not recorded"
[ ! -e "$WORKDIR/run/cycle.pid" ] || fail "the pidfile outlived the cycle"

# --- single flight --------------------------------------------------------
# A live pidfile must make a second cycle skip rather than run. This is the
# guarantee that stops a timer and a wake starting two leads on one key.
mkdir -p "$WORKDIR/run"
sleep 60 &
SLEEPER=$!
echo "$SLEEPER" > "$WORKDIR/run/cycle.pid"
: > "$LEAD_TEST_PROMPT"
OUTPUT=$(run_lead run)
kill "$SLEEPER" 2>/dev/null || true
printf '%s' "$OUTPUT" | grep -Fq "skipped" || fail "a concurrent cycle was not refused"
[ ! -s "$LEAD_TEST_PROMPT" ] || fail "a concurrent cycle started the runtime anyway"
rm -f "$WORKDIR/run/cycle.pid"

# A stale pidfile must NOT block the next cycle.
echo "999999" > "$WORKDIR/run/cycle.pid"
run_lead run >/dev/null
[ -s "$LEAD_TEST_PROMPT" ] || fail "a stale pidfile blocked a new cycle"

# --- wake text ------------------------------------------------------------
# An AgentWake-started cycle must carry what woke it into the prompt, or the
# lead has to rediscover the reason it was started.
: > "$LEAD_TEST_PROMPT"
AUTONOMY_WAKE_TEXT='[from=helper-x] census finished, needs your call' run_lead run >/dev/null
grep -Fq "[WHAT WOKE YOU]" "$LEAD_TEST_PROMPT" || fail "the wake reason was not injected"
grep -Fq "census finished, needs your call" "$LEAD_TEST_PROMPT" || fail "the wake text was not injected"

# A timer-started cycle has no wake text and must not carry an empty section.
: > "$LEAD_TEST_PROMPT"
run_lead run >/dev/null
grep -Fq "[WHAT WOKE YOU]" "$LEAD_TEST_PROMPT" && fail "an empty wake section was injected"

# The wake shim must ignore the runtime flags AgentWake passes in argv and take
# only stdin, or it would try to parse `--print` as its subcommand.
WAKE_OUT=$(printf 'woken by a mission failure' | \
    PATH="$TEST_ROOT/bin:$PATH" \
    AUTONOMY_LEAD_WORKDIR="$WORKDIR" \
    AUTONOMY_LEAD_REPO="$REPO_ROOT" \
    AUTONOMY_LEAD_KEY=probe-lead \
    AUTONOMY_LEAD_TIMEOUT_MIN=5 \
    "$SCRIPT_DIR/lead-wake.sh" --print --continue --setting-sources project,local --strict-mcp-config 2>&1)
printf '%s' "$WAKE_OUT" | grep -Fq "REFUSED" && fail "the wake shim tried to parse runtime flags: $WAKE_OUT"
grep -Fq "woken by a mission failure" "$LEAD_TEST_PROMPT" || fail "the wake shim did not pass stdin through"

# --- status ---------------------------------------------------------------
# Capture first: `grep -q` closes the pipe on its first match, the upstream takes
# SIGPIPE, and `set -o pipefail` would report that as a failure.
STATUS_OUT=$(run_lead status)
printf '%s\n' "$STATUS_OUT" | grep -Eq "^key: +probe-lead$" || fail "status did not report the key"
printf '%s\n' "$STATUS_OUT" | grep -Eq "^running: +none" || fail "status did not report an idle cycle"

echo "PASS: lead cycle contract"
