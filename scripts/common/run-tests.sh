#!/usr/bin/env bash
#
# Run the three test suites concurrently and report a combined result.
#
# The suites are independent processes: each builds its own temp SQLite database
# under the system temp directory and shares no fixture state, so running them
# together is safe and turns the wall clock into the slowest suite rather than
# the sum of all three.
#
# Usage:
#   scripts/run-tests.sh            # all three, concurrently
#   scripts/run-tests.sh unit       # one suite by name (unit|automated|runtimes)
#
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"

FRAMEWORK="${ARMADA_TEST_FRAMEWORK:-net10.0}"
LOG_DIR="$(mktemp -d)"

declare -a SUITE_NAMES=(unit automated runtimes)
declare -a SUITE_PROJECTS=(
  test/Armada.Test.Unit
  test/Armada.Test.Automated
  test/Armada.Test.Runtimes
)

# ClaudeCodeProviderRoutingTests asserts on the environment a child process would
# inherit, so an ANTHROPIC_* variable exported in the caller's shell makes those
# tests fail for reasons that have nothing to do with the code under test.
run_suite() {
  local project="$1"
  local logfile="$2"
  env -u ANTHROPIC_BASE_URL -u ANTHROPIC_AUTH_TOKEN -u ANTHROPIC_API_KEY \
    dotnet run --project "$project" --framework "$FRAMEWORK" > "$logfile" 2>&1
}

selected="${1:-all}"
declare -a pids=()
declare -a running=()

start=$(date +%s)

for i in "${!SUITE_NAMES[@]}"; do
  name="${SUITE_NAMES[$i]}"
  if [ "$selected" != "all" ] && [ "$selected" != "$name" ]; then
    continue
  fi
  run_suite "${SUITE_PROJECTS[$i]}" "$LOG_DIR/$name.log" &
  pids+=("$!")
  running+=("$name")
done

if [ "${#pids[@]}" -eq 0 ]; then
  echo "Unknown suite '$selected'. Use one of: ${SUITE_NAMES[*]}, or omit for all." >&2
  exit 2
fi

failed=0
for i in "${!pids[@]}"; do
  if ! wait "${pids[$i]}"; then
    failed=1
  fi
done

elapsed=$(( $(date +%s) - start ))

echo "================================================================================"
for name in "${running[@]}"; do
  printf '%-10s ' "$name"
  grep -hE "^Total:" "$LOG_DIR/$name.log" 2>/dev/null | tail -1 || echo "(no summary -- see $LOG_DIR/$name.log)"
done
echo "--------------------------------------------------------------------------------"
echo "Wall clock: ${elapsed}s"

if [ "$failed" -ne 0 ]; then
  echo "RESULT: FAIL"
  echo
  for name in "${running[@]}"; do
    grep -hE "^\s+FAIL" "$LOG_DIR/$name.log" 2>/dev/null | sed "s/^/  [$name] /"
  done
  echo
  echo "Full output: $LOG_DIR"
  exit 1
fi

echo "RESULT: PASS"
rm -rf "$LOG_DIR"
