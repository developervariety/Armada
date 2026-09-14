#!/usr/bin/env bash
# Isolated rehearsal of the supervised self-deploy cutover with real server binaries and a disposable
# SQLite copy. Never run it against a production data directory, database or container host.
set -euo pipefail

usage() {
    cat >&2 <<'USAGE'
Usage: rehearse-self-deploy-cutover.sh --rollback-dll <Armada.Server.dll> --candidate-dll <Armada.Server.dll>
                                      --sqlite-source <database.db> [--work-dir <new directory>]
                                      [--timeout-seconds <seconds>]

Runs three scenarios, each in its own disposable data directory:
  1. preflight-refusal  A candidate that is not an assembly: no restart record, rehearsing admiral exits 1.
  2. commit             A real candidate: the record ends Committed and the candidate answers health.
  3. supervisor-kill    kill -9 of the supervisor after the candidate launch is recorded: a normal
                        start exits 3, and --self-deploy-recover reaches a terminal state with exactly
                        one recorded owner running.

The SQLite source is only read; each scenario works on its own copy. Requires dotnet, python3 and curl.
Set ARMADA_DOTNET_BIN to use a different dotnet. The work directory is kept on failure for inspection.
USAGE
}

die() {
    echo "FAIL: $*" >&2
    exit 1
}

ROLLBACK_DLL=""
CANDIDATE_DLL=""
SQLITE_SOURCE=""
WORK_DIR=""
TIMEOUT_SECONDS=600
DOTNET_BIN="${ARMADA_DOTNET_BIN:-dotnet}"

while [ "$#" -gt 0 ]; do
    case "$1" in
        --rollback-dll) ROLLBACK_DLL="${2:-}"; shift 2 ;;
        --candidate-dll) CANDIDATE_DLL="${2:-}"; shift 2 ;;
        --sqlite-source) SQLITE_SOURCE="${2:-}"; shift 2 ;;
        --work-dir) WORK_DIR="${2:-}"; shift 2 ;;
        --timeout-seconds) TIMEOUT_SECONDS="${2:-}"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) usage; exit 2 ;;
    esac
done

[ -n "$ROLLBACK_DLL" ] && [ -n "$CANDIDATE_DLL" ] && [ -n "$SQLITE_SOURCE" ] || { usage; exit 2; }
case "$TIMEOUT_SECONDS" in ''|*[!0-9]*) die "--timeout-seconds must be a whole number" ;; esac
[ -f "$ROLLBACK_DLL" ] || die "rollback assembly not found: $ROLLBACK_DLL"
[ -f "$CANDIDATE_DLL" ] || die "candidate assembly not found: $CANDIDATE_DLL"
[ -f "$SQLITE_SOURCE" ] || die "SQLite source not found: $SQLITE_SOURCE"
command -v "$DOTNET_BIN" >/dev/null 2>&1 || die "dotnet is required"
command -v python3 >/dev/null 2>&1 || die "python3 is required"
command -v curl >/dev/null 2>&1 || die "curl is required"
if [ "${DOTNET_RUNNING_IN_CONTAINER:-}" = "true" ] || [ -f /.dockerenv ] || [ -f /run/.containerenv ]; then
    die "refusing to rehearse inside a container; self-deploy is disabled there"
fi

ROLLBACK_DLL="$(cd "$(dirname "$ROLLBACK_DLL")" && pwd)/$(basename "$ROLLBACK_DLL")"
CANDIDATE_DLL="$(cd "$(dirname "$CANDIDATE_DLL")" && pwd)/$(basename "$CANDIDATE_DLL")"
SQLITE_SOURCE="$(cd "$(dirname "$SQLITE_SOURCE")" && pwd)/$(basename "$SQLITE_SOURCE")"

if [ -z "$WORK_DIR" ]; then
    WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/armada-self-deploy-rehearsal.XXXXXX")"
else
    [ ! -e "$WORK_DIR" ] || die "work directory must not exist: $WORK_DIR"
    mkdir -p "$WORK_DIR"
fi
chmod 700 "$WORK_DIR"
WORK_DIR="$(cd "$WORK_DIR" && pwd)"
BACKGROUND_PIDS=()
SUCCEEDED=0

log() {
    echo "[rehearsal] $*"
}

free_port() {
    python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1]); s.close()'
}

# record_field <data-dir> <dotted.path>: print a restart record field, or nothing.
record_field() {
    python3 - "$1/self-deploy/restart-record.json" "$2" <<'PY'
import json, sys
try:
    with open(sys.argv[1]) as handle:
        value = json.load(handle)
except (OSError, ValueError):
    sys.exit(0)
for part in sys.argv[2].split("."):
    if not isinstance(value, dict) or value.get(part) is None:
        sys.exit(0)
    value = value[part]
print(value)
PY
}

pid_alive() {
    [ -n "$1" ] && kill -0 "$1" 2>/dev/null
}

stop_pid() {
    local pid="$1"
    pid_alive "$pid" || return 0
    kill -TERM "$pid" 2>/dev/null || true
    for _ in $(seq 1 30); do
        pid_alive "$pid" || return 0
        sleep 1
    done
    kill -KILL "$pid" 2>/dev/null || true
}

cleanup() {
    local data pid
    for data in "$WORK_DIR"/*/data; do
        [ -d "$data" ] || continue
        for field in candidateProcess.processId rollbackProcess.processId supervisorProcess.processId oldProcess.processId; do
            pid="$(record_field "$data" "$field")"
            stop_pid "$pid"
        done
    done
    for pid in "${BACKGROUND_PIDS[@]:-}"; do
        stop_pid "$pid"
    done
    if [ "$SUCCEEDED" -eq 1 ]; then
        # Release artifacts are read-only by design; restore owner write access so they can be removed.
        chmod -R u+w "$WORK_DIR" 2>/dev/null || true
        rm -rf "$WORK_DIR" || echo "[rehearsal] could not remove work directory: $WORK_DIR" >&2
    else
        echo "[rehearsal] work directory kept for inspection: $WORK_DIR" >&2
    fi
}
trap cleanup EXIT

# new_scenario <name>: create a private data directory with a database copy and settings.
new_scenario() {
    local name="$1"
    local data="$WORK_DIR/$name/data"
    mkdir -p "$data"
    chmod 700 "$WORK_DIR/$name" "$data"
    cp "$SQLITE_SOURCE" "$data/armada.db"
    local admiral_port mcp_port
    admiral_port="$(free_port)"
    mcp_port="$(free_port)"
    while [ "$mcp_port" = "$admiral_port" ]; do mcp_port="$(free_port)"; done
    python3 - "$data" "$admiral_port" "$mcp_port" <<'PY'
import json, os, sys
data, admiral, mcp = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
database = os.path.join(data, "armada.db")
settings = {
    "dataDirectory": data,
    "databasePath": database,
    "admiralPort": admiral,
    "mcpPort": mcp,
    "database": {"type": "Sqlite", "filename": database},
    "selfDeploy": {"handshakeTimeoutSeconds": 60, "oldProcessExitTimeoutSeconds": 120, "healthTimeoutSeconds": 180},
}
with open(os.path.join(data, "settings.json"), "w") as handle:
    json.dump(settings, handle, indent=2)
PY
    echo "$data"
}

admiral_port() {
    python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["admiralPort"])' "$1/settings.json"
}

# run_server <data-dir> <log> <hold-seconds> <args...>: start the rollback binary in the background.
run_server() {
    local data="$1" logfile="$2" hold="$3"
    shift 3
    env -u "ARMADA_SELF_DEPLOY_OPERATION_ID" \
        ARMADA_DATA_DIRECTORY="$data" \
        ARMADA_SELF_DEPLOY_REHEARSAL="isolated-disposable" \
        ARMADA_SELF_DEPLOY_REHEARSAL_HOLD_SECONDS="$hold" \
        "$DOTNET_BIN" "$ROLLBACK_DLL" "$@" > "$logfile" 2>&1 &
    LAST_PID=$!
    BACKGROUND_PIDS+=("$LAST_PID")
}

# wait_exit <pid> <seconds>: wait for a background process and set LAST_EXIT.
wait_exit() {
    local pid="$1" seconds="$2"
    for _ in $(seq 1 "$seconds"); do
        pid_alive "$pid" || break
        sleep 1
    done
    pid_alive "$pid" && die "process $pid did not exit within ${seconds}s"
    set +e
    wait "$pid"
    LAST_EXIT=$?
    set -e
}

# wait_state <data-dir> <seconds> <states...>: wait until the record reaches one of the states.
wait_state() {
    local data="$1" seconds="$2"
    shift 2
    local state=""
    for _ in $(seq 1 "$seconds"); do
        state="$(record_field "$data" state)"
        for wanted in "$@"; do
            [ "$state" = "$wanted" ] && { echo "$state"; return 0; }
        done
        case "$state" in Committed|RolledBack|RollbackBlocked|Aborted|Failed) echo "$state"; return 0 ;; esac
        sleep 1
    done
    echo "${state:-NoRecord}"
}

healthy() {
    curl -fsS --max-time 5 "http://127.0.0.1:$(admiral_port "$1")/api/v1/status/health" >/dev/null 2>&1
}

log "work directory: $WORK_DIR"

log "scenario 1: preflight refusal leaves no restart record"
DATA="$(new_scenario preflight-refusal)"
BROKEN="$WORK_DIR/preflight-refusal/broken-candidate"
mkdir -p "$BROKEN"
echo "not a managed assembly" > "$BROKEN/Armada.Server.dll"
run_server "$DATA" "$WORK_DIR/preflight-refusal/admiral.log" 0 --self-deploy-rehearse "$BROKEN/Armada.Server.dll"
wait_exit "$LAST_PID" "$TIMEOUT_SECONDS"
[ "$LAST_EXIT" -eq 1 ] || die "scenario 1: rehearsing admiral exit $LAST_EXIT, expected 1"
grep -F "SELF-DEPLOY REHEARSAL BLOCKED: candidate_database_validation_failed" "$WORK_DIR/preflight-refusal/admiral.log" >/dev/null \
    || die "scenario 1: candidate validation refusal not reported"
[ ! -e "$DATA/self-deploy/restart-record.json" ] || die "scenario 1: a restart record was written"
log "PASS scenario 1: candidate_database_validation_failed, exit 1, no restart record"

log "scenario 2: healthy candidate commits"
DATA="$(new_scenario commit)"
cp -R "$(dirname "$CANDIDATE_DLL")" "$WORK_DIR/commit/candidate"
run_server "$DATA" "$WORK_DIR/commit/admiral.log" 0 --self-deploy-rehearse "$WORK_DIR/commit/candidate/$(basename "$CANDIDATE_DLL")"
OLD_PID="$LAST_PID"
STATE="$(wait_state "$DATA" "$TIMEOUT_SECONDS" Committed)"
[ "$STATE" = "Committed" ] || die "scenario 2: record ended $STATE ($(record_field "$DATA" reason))"
wait_exit "$OLD_PID" 180
[ "$LAST_EXIT" -eq 0 ] || die "scenario 2: rehearsing admiral exit $LAST_EXIT, expected 0"
CANDIDATE_PID="$(record_field "$DATA" candidateProcess.processId)"
pid_alive "$CANDIDATE_PID" || die "scenario 2: committed candidate is not running"
healthy "$DATA" || die "scenario 2: committed candidate does not answer health"
log "PASS scenario 2: Committed, previous admiral exited 0, candidate $CANDIDATE_PID healthy"
stop_pid "$CANDIDATE_PID"

log "scenario 3: kill -9 of the supervisor during the candidate launch"
DATA="$(new_scenario supervisor-kill)"
cp -R "$(dirname "$CANDIDATE_DLL")" "$WORK_DIR/supervisor-kill/candidate"
run_server "$DATA" "$WORK_DIR/supervisor-kill/admiral.log" 60 --self-deploy-rehearse "$WORK_DIR/supervisor-kill/candidate/$(basename "$CANDIDATE_DLL")"
CANDIDATE_PID=""
for _ in $(seq 1 "$TIMEOUT_SECONDS"); do
    CANDIDATE_PID="$(record_field "$DATA" candidateProcess.processId)"
    [ -n "$CANDIDATE_PID" ] && [ "$(record_field "$DATA" state)" = "CandidateStarting" ] && break
    case "$(record_field "$DATA" state)" in Committed|RolledBack|RollbackBlocked|Aborted|Failed) break ;; esac
    sleep 1
done
[ "$(record_field "$DATA" state)" = "CandidateStarting" ] && [ -n "$CANDIDATE_PID" ] \
    || die "scenario 3: never observed a recorded candidate launch (state $(record_field "$DATA" state))"
SUPERVISOR_PID="$(record_field "$DATA" supervisorProcess.processId)"
pid_alive "$SUPERVISOR_PID" || die "scenario 3: supervisor is not running"
kill -KILL "$SUPERVISOR_PID"
for _ in $(seq 1 10); do pid_alive "$SUPERVISOR_PID" || break; sleep 1; done
pid_alive "$SUPERVISOR_PID" && die "scenario 3: supervisor survived kill -9"
[ "$(record_field "$DATA" state)" = "CandidateStarting" ] || die "scenario 3: record moved after the kill"
log "supervisor $SUPERVISOR_PID killed with the record at CandidateStarting"

run_server "$DATA" "$WORK_DIR/supervisor-kill/normal-start.log" 0
wait_exit "$LAST_PID" 120
[ "$LAST_EXIT" -eq 3 ] || die "scenario 3: normal start exit $LAST_EXIT, expected 3"
grep -F "restart_in_progress" "$WORK_DIR/supervisor-kill/normal-start.log" >/dev/null || die "scenario 3: startup refusal reason missing"
log "normal start refused with exit 3 (restart_in_progress)"

run_server "$DATA" "$WORK_DIR/supervisor-kill/recover.log" 0 --self-deploy-recover
wait_exit "$LAST_PID" "$TIMEOUT_SECONDS"
STATE="$(record_field "$DATA" state)"
[ "$LAST_EXIT" -eq 0 ] || die "scenario 3: recover exit $LAST_EXIT with state $STATE ($(record_field "$DATA" reason))"
case "$STATE" in Committed|RolledBack) ;; *) die "scenario 3: recover ended $STATE" ;; esac
OWNERS=0
OWNER_PID=""
for field in oldProcess.processId candidateProcess.processId rollbackProcess.processId; do
    pid="$(record_field "$DATA" "$field")"
    if pid_alive "$pid"; then OWNERS=$((OWNERS + 1)); OWNER_PID="$pid"; fi
done
[ "$OWNERS" -eq 1 ] || die "scenario 3: $OWNERS recorded owners running, expected 1"
healthy "$DATA" || die "scenario 3: recovered owner does not answer health"
log "PASS scenario 3: recover reached $STATE ($(record_field "$DATA" reason)), one owner $OWNER_PID healthy"
stop_pid "$OWNER_PID"

SUCCEEDED=1
log "PASS all scenarios"
