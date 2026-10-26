#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
# shellcheck source=resolve-insecure.sh
. "${SCRIPT_DIR}/resolve-insecure.sh"
armada_resolve_insecure "$@"
DASHBOARD_DIR="${REPO_ROOT}/src/Armada.Dashboard"
DIST_DIR="${DASHBOARD_DIR}/dist"
ARMADA_ROOT="${HOME}/.armada"
TARGET_DIR="${ARMADA_ROOT}/dashboard"
BUILD_DIR=""
STAGE_DIR=""
BACKUP_DIR=""
LOCK_DIR="${ARMADA_ROOT}/.dashboard-deploy.lock"
LOCK_OWNED=0

cleanup() {
    local result=$?
    trap - EXIT

    if [ -n "${BACKUP_DIR}" ] && { [ -e "${BACKUP_DIR}" ] || [ -L "${BACKUP_DIR}" ]; } \
        && [ ! -e "${TARGET_DIR}" ] && [ ! -L "${TARGET_DIR}" ]; then
        if mv "${BACKUP_DIR}" "${TARGET_DIR}"; then
            echo "[deploy-dashboard] Restored the previous dashboard after an interrupted deploy."
        else
            echo "ERROR: Could not restore the previous dashboard. It remains at ${BACKUP_DIR}." >&2
        fi
    fi

    if [ -n "${STAGE_DIR}" ] && { [ -e "${STAGE_DIR}" ] || [ -L "${STAGE_DIR}" ]; }; then
        rm -rf "${STAGE_DIR}" || echo "WARNING: Could not remove dashboard staging directory ${STAGE_DIR}." >&2
    fi
    if [ -n "${BUILD_DIR}" ] && { [ -e "${BUILD_DIR}" ] || [ -L "${BUILD_DIR}" ]; }; then
        rm -rf "${BUILD_DIR}" || echo "WARNING: Could not remove dashboard build directory ${BUILD_DIR}." >&2
    fi
    if [ "${LOCK_OWNED}" -eq 1 ]; then
        rm -f "${LOCK_DIR}/owner"
        rmdir "${LOCK_DIR}" 2>/dev/null || echo "WARNING: Could not remove dashboard deploy lock ${LOCK_DIR}." >&2
    fi
    exit "${result}"
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

mkdir -p "${ARMADA_ROOT}"

echo
echo "[deploy-dashboard] Starting dashboard build and deploy"

if [ ! -f "${DASHBOARD_DIR}/package.json" ]; then
    echo "ERROR: Dashboard project not found at ${DASHBOARD_DIR}"
    exit 1
fi

if ! mkdir "${LOCK_DIR}" 2>/dev/null; then
    echo "ERROR: Another dashboard deploy is active (${LOCK_DIR})." >&2
    exit 1
fi
LOCK_OWNED=1
printf 'pid=%s\n' "$$" > "${LOCK_DIR}/owner"

if ! command -v node >/dev/null 2>&1; then
    if [ ! -f "${DIST_DIR}/index.html" ]; then
        echo "ERROR: Node.js is not installed and ${DIST_DIR}/index.html is missing."
        echo "Install Node.js, or commit a built dashboard under src/Armada.Dashboard/dist/."
        exit 1
    fi
    echo "[deploy-dashboard] Node.js is not installed; deploying the committed pre-built dashboard from ${DIST_DIR}"
    SOURCE_DIR="${DIST_DIR}"
else
    cd "${DASHBOARD_DIR}"
    if [ ! -d "node_modules" ]; then
        echo "[deploy-dashboard] Installing dependencies..."
        npm install
    fi

    echo "[deploy-dashboard] Building..."
    # Keep the tracked dist/ tree untouched and stage this build before the live dashboard changes.
    BUILD_DIR="$(mktemp -d)"
    npm run build -- --outDir "${BUILD_DIR}"

    if [ ! -f "${BUILD_DIR}/index.html" ]; then
        echo "ERROR: Dashboard build did not produce index.html" >&2
        exit 1
    fi
    SOURCE_DIR="${BUILD_DIR}"
fi

STAGE_DIR="$(mktemp -d "${ARMADA_ROOT}/.dashboard-stage.XXXXXX")"
chmod 755 "${STAGE_DIR}"
if ! cp -R "${SOURCE_DIR}/." "${STAGE_DIR}/"; then
    echo "ERROR: Could not stage the dashboard files." >&2
    exit 1
fi
if [ ! -f "${STAGE_DIR}/index.html" ]; then
    echo "ERROR: Staged dashboard does not contain index.html." >&2
    exit 1
fi

echo "[deploy-dashboard] Deploying dashboard to ${TARGET_DIR}"
if [ -e "${TARGET_DIR}" ] || [ -L "${TARGET_DIR}" ]; then
    BACKUP_DIR="${STAGE_DIR}.previous"
    if [ -e "${BACKUP_DIR}" ] || [ -L "${BACKUP_DIR}" ]; then
        echo "ERROR: Dashboard rollback path already exists: ${BACKUP_DIR}." >&2
        exit 1
    fi
    if ! mv "${TARGET_DIR}" "${BACKUP_DIR}"; then
        echo "ERROR: Could not move the current dashboard to its rollback path." >&2
        exit 1
    fi
fi

if ! mv "${STAGE_DIR}" "${TARGET_DIR}"; then
    echo "ERROR: Could not install the staged dashboard." >&2
    if [ -n "${BACKUP_DIR}" ]; then
        if mv "${BACKUP_DIR}" "${TARGET_DIR}"; then
            BACKUP_DIR=""
        else
            echo "ERROR: Could not restore the previous dashboard. It remains at ${BACKUP_DIR}." >&2
        fi
    fi
    exit 1
fi
STAGE_DIR=""
if [ ! -f "${TARGET_DIR}/index.html" ]; then
    echo "ERROR: Installed dashboard does not contain index.html." >&2
    if [ -n "${BACKUP_DIR}" ]; then
        rm -rf "${TARGET_DIR}"
        if mv "${BACKUP_DIR}" "${TARGET_DIR}"; then
            BACKUP_DIR=""
        else
            echo "ERROR: Could not restore the previous dashboard. It remains at ${BACKUP_DIR}." >&2
        fi
    else
        rm -rf "${TARGET_DIR}"
    fi
    exit 1
fi

if [ -n "${BACKUP_DIR}" ]; then
    if rm -rf "${BACKUP_DIR}"; then
        BACKUP_DIR=""
    else
        echo "WARNING: New dashboard is active, but the previous copy remains at ${BACKUP_DIR}." >&2
    fi
fi

echo "Dashboard deployed to ${TARGET_DIR}"
