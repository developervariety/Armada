#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

echo
echo "[install] Deploying dashboard..."
"$SCRIPT_DIR/deploy-dashboard.sh"

echo
echo "[install] Building Armada solution..."
dotnet build "$REPO_ROOT/src/Armada.sln"

echo
echo "[install] Packing Armada.Helm..."
dotnet pack "$REPO_ROOT/src/Armada.Helm" -o "$REPO_ROOT/src/nupkg"

echo
echo "[install] Installing Armada.Helm as a global tool..."
dotnet tool install --global --add-source "$REPO_ROOT/src/nupkg" Armada.Helm

echo
echo "[install] Completed."
