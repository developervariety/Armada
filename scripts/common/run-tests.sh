#!/usr/bin/env bash
#
# Run the test gate concurrently and report a combined result.
#
# The suites are independent processes: each builds its own temp SQLite database
# under the system temp directory and shares no fixture state, so running them
# together is safe and turns the wall clock into the slowest process rather than
# the sum of all of them. The unit runner is additionally split into shards, one
# process each (see docs/TESTING.md, "Sharded unit runs").
#
# Usage:
#   scripts/common/run-tests.sh                    # unit shards, automated, runtimes, shared
#   scripts/common/run-tests.sh unit               # one runner by name (unit|automated|runtimes|shared)
#   scripts/common/run-tests.sh --shards 4         # unit shard count (default: min(cores/2, 6))
#   scripts/common/run-tests.sh unit --suite "Git Service"
#                                                  # one runner with its own arguments, never sharded
#   ARMADA_TEST_UNIT_SHARDS=1 scripts/common/run-tests.sh
#                                                  # unit as a single process
#   ARMADA_TEST_KEEP_LOGS=1 scripts/common/run-tests.sh
#                                                  # keep the log directory on PASS (for shard weights)
#   ARMADA_TEST_LOG_DIR=<dir> scripts/common/run-tests.sh
#                                                  # write the per-runner logs to <dir> instead of a temp directory
#
# The full gate for a commit runs on a Linux host through scripts/linux/server-gate.sh, which builds the
# commit in a scratch worktree there and runs this script (see docs/TESTING.md, "Gate Host"). Run this
# script directly for quick single-runner or single-suite runs.
#
# "shared" is the Touchstone runner over src/Test.Shared. It prints every skipped
# case with its recorded reason and exits non-zero on any failure, on a stale
# disposition record, or when the selection would execute nothing.
#
# Every runner must exit 0 and print a "Total:" summary line. A unit shard
# passes only when its process exits 0, prints a summary with
# "RESULT: PASS", and the per-shard suite counts add up to the registered total.
# A shard that crashed, printed no summary, or executed nothing fails the gate.
#
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"

FRAMEWORK="${ARMADA_TEST_FRAMEWORK:-net10.0}"
# ARMADA_TEST_LOG_DIR names the directory for the per-runner logs (created when absent); default: a new temp directory.
if [ -n "${ARMADA_TEST_LOG_DIR:-}" ]; then
  LOG_DIR="$ARMADA_TEST_LOG_DIR"
  mkdir -p "$LOG_DIR"
  LOG_DIR_OWNED=0
else
  LOG_DIR="$(mktemp -d)"
  LOG_DIR_OWNED=1
fi

declare -a SUITE_NAMES=(unit automated runtimes shared)
declare -a SUITE_PROJECTS=(
  test/Armada.Test.Unit/Test.Unit.csproj
  test/Armada.Test.Automated/Test.Automated.csproj
  test/Armada.Test.Runtimes/Armada.Test.Runtimes.csproj
  src/Test.Automated/Test.Automated.csproj
)

default_shards() {
  local cores
  cores="$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 2)"
  local shards=$(( cores / 2 ))
  if [ "$shards" -gt 6 ]; then shards=6; fi
  if [ "$shards" -lt 1 ]; then shards=1; fi
  echo "$shards"
}

selected="all"
shards="${ARMADA_TEST_UNIT_SHARDS:-}"
declare -a passthrough=()
while [ "$#" -gt 0 ]; do
  case "$1" in
    --shards)
      if [ "$#" -lt 2 ]; then echo "--shards requires a count" >&2; exit 2; fi
      shards="$2"
      shift 2
      ;;
    *)
      if [ "$selected" = "all" ]; then
        selected="$1"
        shift
        passthrough=("$@")
        break
      fi
      ;;
  esac
done

if [ -z "$shards" ]; then shards="$(default_shards)"; fi
if ! [[ "$shards" =~ ^[1-9][0-9]*$ ]]; then
  echo "Unit shard count must be a positive integer, got '$shards'." >&2
  exit 2
fi

known=0
for name in "${SUITE_NAMES[@]}"; do
  if [ "$selected" = "all" ] || [ "$selected" = "$name" ]; then known=1; fi
done
if [ "$known" -eq 0 ]; then
  echo "Unknown suite '$selected'. Use one of: ${SUITE_NAMES[*]}, or omit for all." >&2
  exit 2
fi

# Extra arguments select inside one runner, so they are never split across shards.
# A results manifest is keyed by executable, so a manifest run keeps unit in one process.
if [ "${#passthrough[@]}" -gt 0 ] || [ -n "${ARMADA_TEST_RESULTS_DIRECTORY:-}" ]; then
  shards=1
fi

# ClaudeCodeProviderRoutingTests asserts on the environment a child process would
# inherit, so an ANTHROPIC_* variable exported in the caller's shell makes those
# tests fail for reasons that have nothing to do with the code under test.
run_project() {
  local project="$1"
  local logfile="$2"
  shift 2
  env -u ANTHROPIC_BASE_URL -u ANTHROPIC_AUTH_TOKEN -u ANTHROPIC_API_KEY \
    dotnet run --project "$project" --framework "$FRAMEWORK" --no-build --no-restore -- "$@" > "$logfile" 2>&1
}

start=$(date +%s)

# The projects share Core/Server build outputs. Build in sequence before running
# independent test processes, so parallel compilers cannot overwrite those files.
for i in "${!SUITE_NAMES[@]}"; do
  name="${SUITE_NAMES[$i]}"
  if [ "$selected" != "all" ] && [ "$selected" != "$name" ]; then
    continue
  fi
  if ! dotnet build "${SUITE_PROJECTS[$i]}" --framework "$FRAMEWORK" > "$LOG_DIR/$name-build.log" 2>&1; then
    cat "$LOG_DIR/$name-build.log"
    echo "RESULT: FAIL (build $name)"
    echo "Full output: $LOG_DIR"
    exit 1
  fi
done

declare -a pids=()
declare -a labels=()
declare -a logs=()
declare -a statuses=()

for i in "${!SUITE_NAMES[@]}"; do
  name="${SUITE_NAMES[$i]}"
  if [ "$selected" != "all" ] && [ "$selected" != "$name" ]; then
    continue
  fi
  if [ "$name" = "unit" ] && [ "$shards" -gt 1 ]; then
    for (( s = 1; s <= shards; s++ )); do
      run_project "${SUITE_PROJECTS[$i]}" "$LOG_DIR/unit-shard-$s.log" --shard "$s/$shards" &
      pids+=("$!")
      labels+=("unit-shard-$s")
      logs+=("$LOG_DIR/unit-shard-$s.log")
    done
    continue
  fi
  if [ "${#passthrough[@]}" -gt 0 ]; then
    run_project "${SUITE_PROJECTS[$i]}" "$LOG_DIR/$name.log" "${passthrough[@]}" &
  else
    run_project "${SUITE_PROJECTS[$i]}" "$LOG_DIR/$name.log" &
  fi
  pids+=("$!")
  labels+=("$name")
  logs+=("$LOG_DIR/$name.log")
done

failed=0
for i in "${!pids[@]}"; do
  if wait "${pids[$i]}"; then
    statuses+=(0)
  else
    statuses+=(1)
    failed=1
  fi
done

elapsed=$(( $(date +%s) - start ))

# Read "Total: T  Passed: P  Failed: F  Skipped: S" from a log into four numbers.
summary_numbers() {
  grep -hE "^Total:" "$1" 2>/dev/null | tail -1 \
    | sed -E 's/^Total: ([0-9]+) +Passed: ([0-9]+) +Failed: ([0-9]+)( +Skipped: ([0-9]+))?.*$/\1 \2 \3 \5/'
}

echo "================================================================================"
unit_total=0; unit_passed=0; unit_failed=0; unit_skipped=0
unit_shard_suites=0; unit_registered=""; unit_shards_seen=0
for i in "${!labels[@]}"; do
  label="${labels[$i]}"
  log="${logs[$i]}"
  line="$(grep -hE "^Total:" "$log" 2>/dev/null | tail -1)"
  if [ -z "$line" ]; then
    printf '%-14s (no summary -- see %s)\n' "$label" "$log"
    failed=1
    continue
  fi
  if [ "${statuses[$i]}" -ne 0 ]; then
    failed=1
  fi
  printf '%-14s %s\n' "$label" "$line"
  case "$label" in
    unit-shard-*)
      unit_shards_seen=$(( unit_shards_seen + 1 ))
      # Runner contract cases start inner runners that print their own RESULT lines; the shard's own
      # result is the last one.
      if [ "$(grep -hE "^RESULT: " "$log" | tail -1)" != "RESULT: PASS" ]; then
        failed=1
      fi
      read -r t p f sk <<< "$(summary_numbers "$log")"
      unit_total=$(( unit_total + ${t:-0} ))
      unit_passed=$(( unit_passed + ${p:-0} ))
      unit_failed=$(( unit_failed + ${f:-0} ))
      unit_skipped=$(( unit_skipped + ${sk:-0} ))
      # The shard's own selection line is printed before any suite runs; runner contract cases print
      # inner shard lines later, so the first line is the shard's.
      shard_line="$(grep -hE "^Shard [0-9]+/[0-9]+: [0-9]+ of [0-9]+ suites$" "$log" | head -1)"
      if [ -z "$shard_line" ]; then
        echo "  $label printed no shard selection line"
        failed=1
      else
        count="$(echo "$shard_line" | sed -E 's/^Shard [0-9]+\/[0-9]+: ([0-9]+) of ([0-9]+) suites$/\1/')"
        registered="$(echo "$shard_line" | sed -E 's/^Shard [0-9]+\/[0-9]+: ([0-9]+) of ([0-9]+) suites$/\2/')"
        unit_shard_suites=$(( unit_shard_suites + count ))
        if [ -n "$unit_registered" ] && [ "$unit_registered" != "$registered" ]; then
          echo "  unit shards disagree on the registered suite count ($unit_registered vs $registered)"
          failed=1
        fi
        unit_registered="$registered"
      fi
      ;;
  esac
done
if [ "$unit_shards_seen" -gt 0 ] || { [ "$shards" -gt 1 ] && { [ "$selected" = "all" ] || [ "$selected" = "unit" ]; }; }; then
  printf '%-14s Total: %s  Passed: %s  Failed: %s  Skipped: %s  (%s of %s shards summarised)\n' \
    "unit" "$unit_total" "$unit_passed" "$unit_failed" "$unit_skipped" "$unit_shards_seen" "$shards"
  if [ "$unit_shards_seen" -ne "$shards" ]; then
    failed=1
  fi
  if [ -z "$unit_registered" ] || [ "$unit_shard_suites" -ne "$unit_registered" ]; then
    echo "  unit shard suite counts sum to $unit_shard_suites, registered suites: ${unit_registered:-unknown}"
    failed=1
  fi
fi
echo "--------------------------------------------------------------------------------"
echo "Wall clock: ${elapsed}s"

if [ "$failed" -ne 0 ]; then
  echo "RESULT: FAIL"
  echo
  for i in "${!labels[@]}"; do
    label="${labels[$i]}"
    log="${logs[$i]}"
    if [ "${statuses[$i]}" -ne 0 ]; then
      echo "  [$label] exited non-zero"
    fi
    grep -hE "^\s+FAIL| FAIL +[0-9]+ms$" "$log" 2>/dev/null | sed "s/^/  [$label] /"
    grep -hE "^RESULT: FAIL \(discovery\)" "$log" 2>/dev/null | sed "s/^/  [$label] /"
  done
  echo
  echo "Full output: $LOG_DIR"
  exit 1
fi

echo "RESULT: PASS"
# A log directory the caller named is never deleted.
if [ -n "${ARMADA_TEST_KEEP_LOGS:-}" ] || [ "$LOG_DIR_OWNED" -eq 0 ]; then
  echo "Full output: $LOG_DIR"
else
  rm -rf "$LOG_DIR"
fi
