#!/usr/bin/env python3
"""Run a native database client inside the database's own container.

Installed under the client's name (pg_dump, pg_restore, psql, createdb, dropdb,
mysql, mysqldump, sqlcmd) by install-database-client-wrappers.sh. Armada's
native backup then finds the tools on PATH on a host that runs the database in
a container but has no client packages installed.

Configuration (environment, all required for the provider in use):
  ARMADA_CLIENT_POSTGRESQL_CONTAINER   container running PostgreSQL
  ARMADA_CLIENT_POSTGRESQL_PORTS       host:container port, e.g. 25432:5432
  ARMADA_CLIENT_MYSQL_CONTAINER        container running MySQL
  ARMADA_CLIENT_MYSQL_PORTS            host:container port, e.g. 23306:3306
  ARMADA_CLIENT_SQLSERVER_CONTAINER    container running SQL Server
  ARMADA_CLIENT_SQLSERVER_PORTS        host:container port, e.g. 21433:1433
  ARMADA_CLIENT_SQLSERVER_SQLCMD       sqlcmd path inside the container
                                       (default /opt/mssql-tools18/bin/sqlcmd)

Passwords are forwarded only by name (PGPASSWORD, MYSQL_PWD, SQLCMDPASSWORD)
through `docker exec -e`, never as arguments. A host path given to
pg_dump --file, mysqldump --result-file or as the pg_restore input is read or
written on the host and streamed through the container.
"""
import os
import pathlib
import subprocess
import sys

PROVIDERS = {
    "postgresql": {"tools": {"pg_dump", "pg_restore", "psql", "createdb", "dropdb"}, "secret": "PGPASSWORD"},
    "mysql": {"tools": {"mysql", "mysqldump"}, "secret": "MYSQL_PWD"},
    "sqlserver": {"tools": {"sqlcmd"}, "secret": "SQLCMDPASSWORD"},
}


def fail(message, code=2):
    sys.stderr.write("database-client-container: " + message + "\n")
    raise SystemExit(code)


def main():
    name = pathlib.Path(sys.argv[0]).name
    provider = next((key for key, value in PROVIDERS.items() if name in value["tools"]), None)
    if provider is None:
        fail("unsupported client name '" + name + "'")
    prefix = "ARMADA_CLIENT_" + provider.upper() + "_"
    container = os.environ.get(prefix + "CONTAINER", "").strip()
    ports = os.environ.get(prefix + "PORTS", "").strip()
    if not container or ":" not in ports:
        fail("set " + prefix + "CONTAINER and " + prefix + "PORTS (host:container)")
    host_port, container_port = ports.split(":", 1)
    binary = os.environ.get(prefix + "SQLCMD", "/opt/mssql-tools18/bin/sqlcmd") if provider == "sqlserver" else name

    args = []
    for argument in sys.argv[1:]:
        argument = argument.replace("--port=" + host_port, "--port=" + container_port)
        argument = argument.replace("," + host_port, "," + container_port)
        args.append(argument)

    output_path = None
    input_path = None
    kept = []
    for argument in args:
        if name == "pg_dump" and argument.startswith("--file="):
            output_path = argument.split("=", 1)[1]
        elif name == "mysqldump" and argument.startswith("--result-file="):
            output_path = argument.split("=", 1)[1]
        else:
            kept.append(argument)
    if name == "pg_restore" and kept and pathlib.Path(kept[-1]).is_file():
        input_path = kept.pop()

    command = ["docker", "exec", "-i", "-e", PROVIDERS[provider]["secret"], container, binary] + kept
    source = open(input_path, "rb") if input_path else sys.stdin.buffer
    destination = open(output_path, "wb") if output_path else sys.stdout.buffer
    try:
        result = subprocess.run(command, stdin=source, stdout=destination, stderr=subprocess.PIPE, check=False)
        sys.stderr.buffer.write(result.stderr)
    except FileNotFoundError:
        fail("docker is not installed or not on PATH", 127)
    finally:
        if input_path:
            source.close()
        if output_path:
            destination.close()
    raise SystemExit(result.returncode)


if __name__ == "__main__":
    main()
