#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

# shellcheck source=scripts/common/resolve-framework.sh
source "${SCRIPT_DIR}/resolve-framework.sh"
armada_resolve_framework "$@"

echo
echo "[install] Deploying dashboard..."
"$SCRIPT_DIR/deploy-dashboard.sh"

echo
echo "[install] Building Armada solution (${ARMADA_TARGET_FRAMEWORK})..."
dotnet build "$REPO_ROOT/src/Armada.sln" $ARMADA_DOTNET_MSBUILD_FRAMEWORK_ARGS

echo
echo "[install] Packing Armada.Helm (${ARMADA_TARGET_FRAMEWORK})..."
dotnet pack "$REPO_ROOT/src/Armada.Helm" $ARMADA_DOTNET_MSBUILD_FRAMEWORK_ARGS -o "$REPO_ROOT/src/nupkg"

echo
echo "[install] Installing Armada.Helm as a global tool..."
dotnet tool install --global --add-source "$REPO_ROOT/src/nupkg" Armada.Helm

echo
echo "[install] Completed."
