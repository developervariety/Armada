#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

echo
echo "[remove-mcp] Removing Armada MCP for Claude Code, Codex, Gemini, and Cursor..."
dotnet run --project "$REPO_ROOT/src/Armada.Helm" -f net10.0 -- mcp remove --yes

echo
echo "[remove-mcp] Completed."
