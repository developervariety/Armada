#!/usr/bin/env bash
# Link database-client-container.py under every native client name into one directory.
# Put that directory first on PATH to run Armada's native backup and database tests on a
# host whose databases run in containers without client packages installed.
set -euo pipefail

usage() {
    cat >&2 <<'USAGE'
Usage: install-database-client-wrappers.sh <target-directory>

Creates pg_dump, pg_restore, psql, createdb, dropdb, mysql, mysqldump and sqlcmd
links to database-client-container.py in <target-directory>. Configure the
containers with the ARMADA_CLIENT_* variables described in that script, then
prepend <target-directory> to PATH.
USAGE
}

[ "$#" -eq 1 ] || { usage; exit 2; }
command -v python3 >/dev/null 2>&1 || { echo "ERROR: python3 is required" >&2; exit 1; }

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WRAPPER="${SCRIPT_DIR}/database-client-container.py"
TARGET="$1"
[ -f "$WRAPPER" ] || { echo "ERROR: wrapper not found: $WRAPPER" >&2; exit 1; }

mkdir -p "$TARGET"
chmod +x "$WRAPPER"
for tool in pg_dump pg_restore psql createdb dropdb mysql mysqldump sqlcmd; do
    ln -sf "$WRAPPER" "$TARGET/$tool"
done
echo "Installed database client wrappers in $TARGET"
