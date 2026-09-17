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
#
#   <ref>            Local commit to test (a branch, tag or SHA). It must be committed; the script refuses
#                    to run while tracked files have uncommitted changes.
#   --ssh-host       ssh alias of the gate host. Default: $ARMADA_GATE_SSH_HOST.
#   --scratch-dir    Absolute path of the scratch directory on the host. Default: $ARMADA_GATE_SCRATCH_DIR.
#                    Created when absent. Holds repo.git, worktree/, logs/ and a lock file.
#   --shards         Unit shard count passed to run-tests.sh. Default: run-tests.sh's own default.
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
  sed -n '12,24p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
}

ref=""
ssh_host="${ARMADA_GATE_SSH_HOST:-}"
scratch_dir="${ARMADA_GATE_SCRATCH_DIR:-}"
shards=""

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

# Create the scratch bare repository when absent. Nothing else on the host is created or changed.
ssh "$ssh_host" "mkdir -p $quoted_scratch && { [ -d $quoted_scratch/repo.git ] || git init -q --bare $quoted_scratch/repo.git; }"

# Push the commit under a gate-only ref, so no branch on the host moves.
git push -q "${ssh_host}:${scratch_dir}/repo.git" "${sha}:${gate_ref}"

remote_args="$(printf '%q ' "$scratch_dir" "$sha" "$shards")"

set +e
ssh "$ssh_host" "bash -s -- $remote_args" <<'REMOTE'
set -uo pipefail
scratch="$1"
sha="$2"
shards="$3"
repo="$scratch/repo.git"
worktree="$scratch/worktree"
logs="$scratch/logs/$(date -u +%Y%m%dT%H%M%SZ)-${sha:0:12}"
mkdir -p "$logs"

# Build servers outlive the gate. Node reuse would keep MSBuild workers alive after the gate exits, and
# any child that inherited the lock descriptor would hold the lock and refuse every later gate, so reuse is
# off, the build and test commands run with the lock descriptor closed, and build servers are shut down.
export MSBUILDDISABLENODEREUSE=1
export DOTNET_CLI_USE_MSBUILD_SERVER=0

# A non-interactive ssh shell may not load the profile that puts dotnet on PATH.
if ! command -v dotnet >/dev/null 2>&1 && [ -x "$HOME/.dotnet/dotnet" ]; then
  export PATH="$HOME/.dotnet:$PATH"
  export DOTNET_ROOT="$HOME/.dotnet"
fi
if ! command -v dotnet >/dev/null 2>&1; then
  echo "RESULT: FAIL (dotnet is not on PATH on the gate host)"
  exit 1
fi

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
  dotnet build src/Armada.sln > "$logs/build.log" 2>&1 9>&-
  if ! grep -q 'Build succeeded' "$logs/build.log" || grep -q ' error ' "$logs/build.log"; then
    grep ' error ' "$logs/build.log" | sort -u | head -40
    echo "RESULT: FAIL (build)"
    echo "Build log: $logs/build.log"
    return 1
  fi
  echo "Build: $(( $(date +%s) - build_start ))s"

  declare -a gate_args=()
  if [ -n "$shards" ]; then gate_args=(--shards "$shards"); fi
  ARMADA_TEST_KEEP_LOGS=1 ARMADA_TEST_LOG_DIR="$logs/runners" scripts/common/run-tests.sh "${gate_args[@]}" > "$logs/run-tests.log" 2>&1 9>&-
  status=$?
  dotnet build-server shutdown > /dev/null 2>&1 9>&- || true
  cat "$logs/run-tests.log"
  echo "Gate logs: $logs"
  if [ "$status" -ne 0 ] || [ "$(grep -E '^RESULT: ' "$logs/run-tests.log" | tail -1)" != "RESULT: PASS" ]; then
    return 1
  fi
  return 0
}

if command -v flock >/dev/null 2>&1; then
  exec 9> "$scratch/gate.lock"
  if ! flock -n 9; then
    echo "RESULT: FAIL (another gate is running in $scratch)"
    exit 1
  fi
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
