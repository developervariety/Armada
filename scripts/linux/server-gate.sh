#!/usr/bin/env bash
#
# Run the full test gate for one commit on a Linux gate host over ssh.
#
# Git-heavy suites spend most of their time starting processes, and process start-up is far slower on
# macOS than on Linux, so the gate runs on a Linux host. This script tests a COMMIT, never a working tree:
# it pushes the commit to a scratch bare repository on the host, checks it out detached in a scratch
# worktree next to it, builds, and runs scripts/common/run-tests.sh (sharded unit, automated, runtimes and
# shared, concurrently). Nothing outside the scratch directory is read or written on the host, so a shared
# checkout or a running service is never touched.
#
# Usage:
#   scripts/linux/server-gate.sh <ref> [--ssh-host <alias>] [--scratch-dir <path>] [--shards <n>]
#                                [--host-lock <path>] [--lock-held] [--min-available-mb <n>] [--min-free-disk-mb <n>]
#
#   <ref>            Local commit to test (a branch, tag or SHA). It must be committed; the script refuses
#                    to run while tracked files have uncommitted changes.
#   --ssh-host       ssh alias of the gate host. Default: $ARMADA_GATE_SSH_HOST.
#   --scratch-dir    Absolute path of the scratch directory on the host. Default: $ARMADA_GATE_SCRATCH_DIR.
#                    Created when absent. Holds repo.git, worktree/, logs/, tmp/ and a lock file.
#   --shards         Unit shard count passed to run-tests.sh. Default: run-tests.sh's own default.
#   --host-lock      Host-wide lock directory on the host. Every gate on the host takes it, so only one gate
#                    builds and tests at a time. Default: $ARMADA_GATE_HOST_LOCK, else /tmp/armada-gate-host.lock.
#   --lock-held      The caller already holds the host lock for this run; do not take or release it.
#   --min-available-mb
#                    Wait after taking the host lock until the host reports at least this much available
#                    memory. Default: $ARMADA_GATE_MIN_AVAILABLE_MB, else 8000. 0 disables the wait.
#   --min-free-disk-mb
#                    Refuse before copying source, and recheck under the host lock before building, when
#                    scratch storage is below this floor. Default: $ARMADA_GATE_MIN_FREE_DISK_MB, else 8000.
#                    This reserves headroom; unrelated writers can still consume space after admission.
#
# Example:
#   ARMADA_GATE_SSH_HOST=<server-host> ARMADA_GATE_SCRATCH_DIR=<scratch-dir> scripts/linux/server-gate.sh HEAD
#
# The host needs git, bash and the .NET SDK. The build and every runner log stay on the host under
# <scratch-dir>/logs/<time>-<sha>/; the combined summary is printed here. The exit code is 0 only when the
# build succeeded and run-tests.sh printed RESULT: PASS. Only one gate runs per scratch directory at a time.
#
set -euo pipefail

usage() {
  sed -n '12,35p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
}

ref=""
ssh_host="${ARMADA_GATE_SSH_HOST:-}"
scratch_dir="${ARMADA_GATE_SCRATCH_DIR:-}"
shards=""
host_lock="${ARMADA_GATE_HOST_LOCK:-/tmp/armada-gate-host.lock}"
lock_held="0"
min_available_mb="${ARMADA_GATE_MIN_AVAILABLE_MB:-8000}"
min_free_disk_mb="${ARMADA_GATE_MIN_FREE_DISK_MB:-8000}"

while [ "$#" -gt 0 ]; do
  case "$1" in
    --ssh-host)
      if [ "$#" -lt 2 ]; then echo "--ssh-host requires an alias" >&2; exit 2; fi
      ssh_host="$2"; shift 2 ;;
    --scratch-dir)
      if [ "$#" -lt 2 ]; then echo "--scratch-dir requires a path" >&2; exit 2; fi
      scratch_dir="$2"; shift 2 ;;
    --shards)
      if [ "$#" -lt 2 ]; then echo "--shards requires a count" >&2; exit 2; fi
      shards="$2"; shift 2 ;;
    --host-lock)
      if [ "$#" -lt 2 ]; then echo "--host-lock requires a path" >&2; exit 2; fi
      host_lock="$2"; shift 2 ;;
    --lock-held)
      lock_held="1"; shift ;;
    --min-available-mb)
      if [ "$#" -lt 2 ]; then echo "--min-available-mb requires a number" >&2; exit 2; fi
      min_available_mb="$2"; shift 2 ;;
    --min-free-disk-mb)
      if [ "$#" -lt 2 ]; then echo "--min-free-disk-mb requires a number" >&2; exit 2; fi
      min_free_disk_mb="$2"; shift 2 ;;
    -h|--help)
      usage; exit 0 ;;
    -*)
      echo "Unknown option '$1'." >&2; usage >&2; exit 2 ;;
    *)
      if [ -n "$ref" ]; then echo "Only one ref may be given (got '$ref' and '$1')." >&2; exit 2; fi
      ref="$1"; shift ;;
  esac
done

if [ -z "$ref" ]; then echo "A ref to test is required." >&2; usage >&2; exit 2; fi
if [ -z "$ssh_host" ]; then echo "No gate host: pass --ssh-host or set ARMADA_GATE_SSH_HOST." >&2; exit 2; fi
if [ -z "$scratch_dir" ]; then echo "No scratch directory: pass --scratch-dir or set ARMADA_GATE_SCRATCH_DIR." >&2; exit 2; fi
case "$scratch_dir" in
  /*) ;;
  *) echo "The scratch directory must be an absolute path on the host, got '$scratch_dir'." >&2; exit 2 ;;
esac
scratch_dir="${scratch_dir%/}"
if [ -z "$scratch_dir" ]; then echo "The scratch directory cannot be the root directory." >&2; exit 2; fi
if [ -n "$shards" ] && ! [[ "$shards" =~ ^[1-9][0-9]*$ ]]; then
  echo "Shard count must be a positive integer, got '$shards'." >&2; exit 2
fi

case "$host_lock" in
  /*) ;;
  *) echo "The host lock must be an absolute path on the host, got '$host_lock'." >&2; exit 2 ;;
esac
if ! [[ "$min_available_mb" =~ ^[0-9]+$ ]]; then
  echo "Minimum available memory must be a whole number of MB, got '$min_available_mb'." >&2; exit 2
fi

if ! [[ "$min_free_disk_mb" =~ ^[1-9][0-9]{0,8}$ ]]; then
  echo "Minimum free disk must be a positive whole number of MB, got '$min_free_disk_mb'." >&2; exit 2
fi

# Sent as a trusted script body to both remote phases, so the admission grammar has one owner.
disk_admission_script=$(cat <<'DISK_ADMISSION'
check_disk_admission() {
  local scratch="$1" minimum_mb="$2" probe="$1" report available_kb available_mb
  while [ ! -e "$probe" ]; do probe="$(dirname "$probe")"; done
  if ! report="$(LC_ALL=C df -Pk "$probe" 2>/dev/null)"; then
    echo "RESULT: FAIL (disk admission: cannot read free space for $probe)"; return 1
  fi
  if ! available_kb="$(printf '%s\n' "$report" | awk 'NR==2 { if ($4 ~ /^[0-9]+$/) print $4; else exit 1 } END { if (NR != 2) exit 1 }')" \
      || ! [[ "$available_kb" =~ ^[0-9]{1,15}$ ]]; then
    echo "RESULT: FAIL (disk admission: invalid free-space reading for $probe)"; return 1
  fi
  available_mb=$((10#$available_kb / 1024))
  echo "Disk admission: $available_mb MB available at $probe (minimum $minimum_mb MB)"
  if [ "$available_mb" -lt "$minimum_mb" ]; then
    echo "RESULT: FAIL (disk admission: insufficient scratch storage at $probe)"; return 1
  fi
}
check_disk_admission "$1" "$2"
DISK_ADMISSION
)

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO_ROOT"

# The gate result must describe a commit. Uncommitted edits to tracked files would not be pushed, so a
# pass would describe code nobody is looking at.
git update-index -q --refresh >/dev/null 2>&1 || true
if ! git diff --quiet HEAD -- || ! git diff --cached --quiet --; then
  echo "Refusing to run: tracked files have uncommitted changes. Commit them first; the gate tests a commit." >&2
  git status --short --untracked-files=no >&2
  exit 2
fi

if ! sha="$(git rev-parse --verify --quiet "${ref}^{commit}")"; then
  echo "'$ref' does not name a commit." >&2
  exit 2
fi
short_sha="${sha:0:12}"
gate_ref="refs/gate/${sha}"

quoted_scratch="$(printf '%q' "$scratch_dir")"

echo "Gate: $short_sha ($(git log -1 --format=%s "$sha"))"
echo "Host: $ssh_host  Scratch: $scratch_dir"

# Refuse before mkdir, bare initialization or source transfer, even when scratch does not exist yet.
preflight_args="$(printf '%q ' "$disk_admission_script" -- "$scratch_dir" "$min_free_disk_mb")"
ssh "$ssh_host" "bash -c $preflight_args"

# Create the scratch bare repository when absent. Nothing else on the host is created or changed.
ssh "$ssh_host" "mkdir -p $quoted_scratch && { [ -d $quoted_scratch/repo.git ] || git init -q --bare $quoted_scratch/repo.git; }"

# Push the commit under a gate-only ref, so no branch on the host moves.
git push -q "${ssh_host}:${scratch_dir}/repo.git" "${sha}:${gate_ref}"

remote_args="$(printf '%q ' "$scratch_dir" "$sha" "$shards" "$host_lock" "$lock_held" "$min_available_mb" "$ref" "$min_free_disk_mb" "$disk_admission_script")"

set +e
ssh "$ssh_host" "bash -s -- $remote_args" <<'REMOTE'
set -uo pipefail
scratch="$1"
sha="$2"
shards="$3"
host_lock="$4"
lock_held="$5"
min_available_mb="$6"
gate_label="$7"
min_free_disk_mb="$8"
disk_admission_script="$9"
repo="$scratch/repo.git"
worktree="$scratch/worktree"
logs="$scratch/logs/$(date -u +%Y%m%dT%H%M%SZ)-${sha:0:12}"
mkdir -p "$logs"

# Build servers outlive the gate. Node reuse would keep MSBuild workers alive after the gate exits, and
# any child that inherited the lock descriptor would hold the lock and refuse every later gate, so reuse is
# off, the build and test commands run with the lock descriptor closed, and build servers are shut down.
export MSBUILDDISABLENODEREUSE=1
export DOTNET_CLI_USE_MSBUILD_SERVER=0
# The compiler server holds several GB between builds; each gate compiles in its own process instead.
export UseSharedCompilation=false

# The host /tmp can be memory-backed, and the builds write a great deal of temporary data there. Build temp
# files go to a folder in the scratch directory on disk, which the exit trap removes. The test runners keep
# the fast system temp directory for their test databases: each runner makes its own root there, puts every
# temp path of the run and its children under it, and removes it on exit (TestDataDirectory).
export ARMADA_TEST_TEMP_ROOT="${TMPDIR:-/tmp}"
gate_tmp="$scratch/tmp"

# Prefer the SDK the Microsoft install script puts in ~/.dotnet over a distribution package. A distribution
# runtime built against the system libunwind can fail to unwind the stack at a managed throw and abort with
# an internal runtime error (0x80131506); Microsoft's builds bundle their own libunwind. A non-interactive
# ssh shell may also not load the profile that puts dotnet on PATH.
if [ -x "$HOME/.dotnet/dotnet" ]; then
  export PATH="$HOME/.dotnet:$PATH"
  export DOTNET_ROOT="$HOME/.dotnet"
fi
if ! command -v dotnet >/dev/null 2>&1; then
  echo "RESULT: FAIL (dotnet is not on PATH on the gate host)"
  exit 1
fi
dotnet_host="$(command -v dotnet)"
dotnet_root="${DOTNET_ROOT:-$(dirname "$(readlink -f "$dotnet_host")")}"
echo "dotnet: $dotnet_host (SDK $(dotnet --version 2>/dev/null < /dev/null || echo '?'))"
for coreclr in "$dotnet_root"/shared/Microsoft.NETCore.App/*/libcoreclr.so; do
  if [ -f "$coreclr" ] && ldd "$coreclr" 2>/dev/null | grep -q 'libunwind'; then
    echo "WARNING: $coreclr links the system libunwind. This runtime can abort at a managed throw; install Microsoft's SDK in ~/.dotnet (see docs/TESTING.md, Gate Host)."
  fi
done

# The remote script arrives on standard input, so every command that could read input runs with it
# redirected from /dev/null; a .NET process that inherited the script as input intermittently aborted at
# start with an internal runtime error.
run_gate() {
  if [ ! -d "$worktree/.git" ]; then
    # The gate refs are not branches, so a clone would see an empty repository; add the remote instead.
    { git init -q "$worktree" && git -C "$worktree" remote add origin "$repo"; } || { echo "RESULT: FAIL (worktree init)"; return 1; }
  fi
  cd "$worktree" || return 1
  git fetch -q origin "refs/gate/$sha:refs/gate/$sha" || { echo "RESULT: FAIL (fetch)"; return 1; }
  git checkout -q --force --detach "$sha" || { echo "RESULT: FAIL (checkout)"; return 1; }
  # Build outputs from an earlier commit must not leak into this one.
  git clean -q -ffdx

  echo "Building $sha on $(uname -sm), $(getconf _NPROCESSORS_ONLN 2>/dev/null || echo '?') cores"
  build_start=$(date +%s)
  # A runtime built against the system libunwind can abort with an internal runtime error before anything
  # compiles (see the dotnet selection above). That abort is retried, at most twice, and only when the log
  # holds nothing else; every other build failure fails the gate at once. A crash report is written to the
  # log directory for each abort, and each retry is printed.
  local attempt
  for attempt in 1 2 3; do
    DOTNET_DbgEnableMiniDump=1 DOTNET_DbgMiniDumpType=1 DOTNET_EnableCrashReport=1 \
      DOTNET_DbgMiniDumpName="$logs/build-abort-$attempt.%p" \
      dotnet build src/Armada.sln < /dev/null > "$logs/build.log" 2>&1 9>&-
    if ! grep -q 'Internal CLR error' "$logs/build.log" || grep -q -E 'Determining projects to restore|Restored |Build succeeded| error ' "$logs/build.log"; then
      break
    fi
    # Keep the crash report; the dump beside it is large and only the report is read.
    find "$logs" -maxdepth 1 -name "build-abort-$attempt.*" ! -name '*.crashreport.json' -delete
    cp "$logs/build.log" "$logs/build-abort-$attempt.log"
    if [ "$attempt" -lt 3 ]; then echo "Build aborted by an internal .NET runtime error before compiling; retrying ($attempt of 2)."; fi
  done
  if ! grep -q 'Build succeeded' "$logs/build.log" || grep -q ' error ' "$logs/build.log"; then
    grep ' error ' "$logs/build.log" | sort -u | head -40
    echo "RESULT: FAIL (build)"
    echo "Build log: $logs/build.log"
    return 1
  fi
  echo "Build: $(( $(date +%s) - build_start ))s"

  declare -a gate_args=()
  if [ -n "$shards" ]; then gate_args=(--shards "$shards"); fi
  ARMADA_TEST_KEEP_LOGS=1 ARMADA_TEST_LOG_DIR="$logs/runners" scripts/common/run-tests.sh "${gate_args[@]}" < /dev/null > "$logs/run-tests.log" 2>&1 9>&-
  status=$?
  dotnet build-server shutdown < /dev/null > /dev/null 2>&1 9>&- || true
  cat "$logs/run-tests.log"
  echo "Gate logs: $logs"
  if [ "$status" -ne 0 ] || [ "$(grep -E '^RESULT: ' "$logs/run-tests.log" | tail -1)" != "RESULT: PASS" ]; then
    return 1
  fi
  return 0
}

# One gate at a time on the whole host. A gate build and its test runners hold several GB, and two at once
# run a host without swap out of memory, so every gate takes the host lock here, on the host, for the whole
# run, and the EXIT trap releases it after this run's build servers are shut down. The owner file names the
# holder; a lock whose owner process is gone is reported and taken over, so a killed gate cannot wedge the
# host. A caller that already holds the lock passes --lock-held.
lock_taken=0
release_host_lock() {
  if [ "${gate_tmp_owned:-0}" = "1" ]; then
    dotnet build-server shutdown < /dev/null > /dev/null 2>&1 9>&- || true
    rm -rf "$gate_tmp" 2>/dev/null || echo "WARNING: could not remove gate temp directory $gate_tmp"
  fi
  if [ "$lock_taken" = "1" ]; then
    # Release only a lock this gate still owns. When the owner file names another holder, or is gone,
    # the lock is left in place and reported, so this gate never removes a lock another gate holds.
    local current_pid
    current_pid="$(sed -n 's/^pid=//p' "$host_lock/owner" 2>/dev/null)"
    if [ "$current_pid" = "$$" ]; then
      rm -f "$host_lock/owner"
      rmdir "$host_lock" 2>/dev/null || echo "WARNING: could not remove host lock $host_lock"
    else
      echo "WARNING: host lock $host_lock is not this gate's (owner pid '${current_pid:-none}', this gate $$); it is left in place"
    fi
  fi
}
trap release_host_lock EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

if [ "$lock_held" = "1" ]; then
  echo "Host lock: held by the caller ($host_lock)"
else
  waited=0
  until mkdir "$host_lock" 2>/dev/null; do
    owner_pid="$(sed -n 's/^pid=//p' "$host_lock/owner" 2>/dev/null)"
    # Only a numeric pid of a process on this host can be checked; any other owner, or no owner file,
    # counts as live, so a waiter never takes the lock from a holder it cannot see.
    if [[ "$owner_pid" =~ ^[0-9]+$ ]] && ! kill -0 "$owner_pid" 2>/dev/null; then
      # Only one waiter can move the owner file, so only one takes over. The moved file is checked again:
      # when its owner is alive (another waiter took over first), it goes back untouched.
      claimed="$host_lock.stale.$$"
      if mv "$host_lock/owner" "$claimed" 2>/dev/null; then
        claimed_pid="$(sed -n 's/^pid=//p' "$claimed" 2>/dev/null)"
        if ! [[ "$claimed_pid" =~ ^[0-9]+$ ]] || kill -0 "$claimed_pid" 2>/dev/null; then
          mv "$claimed" "$host_lock/owner" 2>/dev/null || true
        else
          echo "Host lock: owner pid $claimed_pid is gone; taking over ($(tr '\n' ' ' < "$claimed"))"
          rm -f "$claimed"
          rmdir "$host_lock" 2>/dev/null || true
          continue
        fi
      fi
    fi
    if [ $(( waited % 300 )) -eq 0 ]; then
      echo "Host lock: waiting ${waited}s for $host_lock ($(tr '\n' ' ' 2>/dev/null < "$host_lock/owner" || echo 'no owner file'))"
    fi
    sleep 15
    waited=$(( waited + 15 ))
  done
  lock_taken=1
  printf 'pid=%s\nutc=%s\nscratch=%s\nref=%s\nsha=%s\n' "$$" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$scratch" "$gate_label" "$sha" > "$host_lock/owner"
  echo "Host lock: taken ($host_lock)"
fi

if [ "$min_available_mb" -gt 0 ] && [ -r /proc/meminfo ]; then
  waited=0
  while :; do
    available_mb=$(( $(sed -n 's/^MemAvailable: *\([0-9]*\) kB/\1/p' /proc/meminfo) / 1024 ))
    if [ "$available_mb" -ge "$min_available_mb" ]; then break; fi
    if [ $(( waited % 300 )) -eq 0 ]; then
      echo "Memory: ${available_mb} MB available, waiting for ${min_available_mb} MB (${waited}s)"
    fi
    sleep 15
    waited=$(( waited + 15 ))
  done
  echo "Memory: ${available_mb} MB available (minimum ${min_available_mb} MB)"
fi

if command -v flock >/dev/null 2>&1; then
  exec 9> "$scratch/gate.lock"
  if ! flock -n 9; then
    echo "RESULT: FAIL (another gate is running in $scratch)"
    exit 1
  fi
fi

# Queueing can consume the headroom measured before transfer. Recheck while both locks are held,
# before fetch/checkout/build and before removing anything in this scratch directory.
if ! bash -c "$disk_admission_script" -- "$scratch" "$min_free_disk_mb"; then exit 1; fi

# A queued gate creates its temp only after the prior holder has completed cleanup under both locks.
# A refused waiter owns neither temp cleanup nor build-server shutdown.
if [ -L "$gate_tmp" ] || ! mkdir -p "$gate_tmp"; then
  echo "RESULT: FAIL (cannot create gate temp directory $gate_tmp)"
  exit 1
fi
export TMPDIR="$gate_tmp" TMP="$gate_tmp" TEMP="$gate_tmp"
gate_tmp_owned=1
if ! find "$gate_tmp" -mindepth 1 -delete; then
  echo "RESULT: FAIL (cannot empty gate temp directory $gate_tmp)"
  exit 1
fi

run_gate
exit $?
REMOTE
status=$?
set -e

if [ "$status" -ne 0 ]; then
  echo "Gate FAILED for $short_sha (exit $status)."
  exit 1
fi
echo "Gate PASSED for $short_sha."
