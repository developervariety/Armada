#!/usr/bin/env bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HELM_DLL="$SCRIPT_DIR/src/Armada.Helm/bin/Debug/net10.0/Armada.Helm.dll"

run_helm() {
  if command -v armada >/dev/null 2>&1; then
    armada "$@"
    return
  fi

  if [ -f "$HELM_DLL" ]; then
    dotnet "$HELM_DLL" "$@"
    return
  fi

  dotnet run --project "$SCRIPT_DIR/src/Armada.Helm" -f net10.0 -- "$@"
}

echo
echo "[update] Stopping repo-backed Armada MCP stdio hosts if they are running..."
mapfile -t MCP_PIDS < <(pgrep -af "Armada\\.Helm\\.dll mcp stdio" | awk -v repo="$SCRIPT_DIR" 'index($0, repo) > 0 { print $1 }' || true)
if [ "${#MCP_PIDS[@]}" -eq 0 ]; then
  echo "[update] No repo-backed MCP stdio hosts found."
else
  for pid in "${MCP_PIDS[@]}"; do
    [ -n "$pid" ] || continue
    echo "[update] Stopping MCP stdio host PID $pid..."
    kill -9 "$pid"
  done
fi

echo
echo "[update] Stopping Armada server if it is running..."
run_helm server stop || true

echo
echo "[update] Reinstalling Armada tool and redeploying dashboard..."
"$SCRIPT_DIR/reinstall.sh"

echo
echo "[update] Starting Armada server..."
run_helm server start
