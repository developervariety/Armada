#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
DASHBOARD_DIR="${REPO_ROOT}/src/Armada.Dashboard"
DIST_DIR="${DASHBOARD_DIR}/dist"
TARGET_DIR="${HOME}/.armada/dashboard"
BUILD_DIR="$(mktemp -d)"

echo
echo "[deploy-dashboard] Starting dashboard build and deploy"

if [ ! -f "${DASHBOARD_DIR}/package.json" ]; then
    echo "ERROR: Dashboard project not found at ${DASHBOARD_DIR}"
    exit 1
fi

cd "${DASHBOARD_DIR}"
if [ ! -d "node_modules" ]; then
    echo "[deploy-dashboard] Installing dependencies..."
    npm install
fi

echo "[deploy-dashboard] Building..."
# Build to a temporary output directory so the tracked dist/ tree in the checkout
# stays untouched. Building in place would dirty dist/index.html and block the
# next `git pull --ff-only` on the host.
npm run build -- --outDir "${BUILD_DIR}"

if [ ! -f "${BUILD_DIR}/index.html" ]; then
    echo "ERROR: Dashboard build did not produce index.html"
    rm -rf "${BUILD_DIR}"
    exit 1
fi

echo "[deploy-dashboard] Deploying dashboard to ${TARGET_DIR}"
rm -rf "${TARGET_DIR}"
mkdir -p "${TARGET_DIR}"
cp -R "${BUILD_DIR}/." "${TARGET_DIR}/"
rm -rf "${BUILD_DIR}"

echo "Dashboard deployed to ${TARGET_DIR}"
