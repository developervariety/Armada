#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

# shellcheck source=scripts/common/resolve-framework.sh
source "${SCRIPT_DIR}/resolve-framework.sh"
armada_resolve_framework "$@"

echo
echo "[remove-mcp] Removing Armada MCP for Claude Code, Codex, Gemini, and Cursor (${ARMADA_TARGET_FRAMEWORK})..."
dotnet run --project "$REPO_ROOT/src/Armada.Helm" -f "$ARMADA_TARGET_FRAMEWORK" -- mcp remove --yes

echo
echo "[remove-mcp] Completed."
