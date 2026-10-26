# Running Armada with Docker

This guide covers running the Armada server and dashboard using Docker containers.
For an existing installation, first read the [v1.0.0 upgrade guide](UPGRADING.md).

> **Active deployment note:** The Armada server in the fleet project uses
> PostgreSQL. The SQLite paths and `armada.db` values below describe the local
> standalone Docker example only. They do not identify the active server
> database. Do not inspect or delete `armada.db` as Armada state without first
> checking the loaded database configuration.

---

## Prerequisites

- [Docker](https://docs.docker.com/get-docker/) installed and running
- [Docker Compose](https://docs.docker.com/compose/install/) (included with Docker Desktop)

---

## Quick Start

```bash
cd docker/armada
docker compose up -d
```

This starts two containers:

| Service | Port | Description |
|---------|------|-------------|
| `armada-server` | 7890 | REST API, React dashboard from the dashboard directory, and WebSocket at /ws |
| `armada-server` | 7891 | MCP (agent communication) |
| `armada-dashboard` | 3000 | Standalone React dashboard |

Open the dashboard at **http://localhost:3000** (React SPA) or **http://localhost:7890/dashboard** (served by the Admiral when a dashboard build is deployed).

> **Rebuilding either image does not rebuild the other.** `armada-server` embeds
> no dashboard; the React dashboard is built separately
> (`src/Armada.Dashboard`) and served from the dashboard directory. For the
> standalone compose topology, rebuild the `armada-dashboard` image as well. For
> deployments where the Admiral serves the React dashboard from the data
> directory (`~/.armada/dashboard`), run `scripts/common/deploy-dashboard.sh`
> after the server image rebuild.

### Default Credentials

| Field | Value |
|-------|-------|
| Email | `admin@armada` |
| Password | `password` |

For API access from scripts or curl:

```bash
curl -H "Authorization: Bearer default" http://localhost:7890/api/v1/status
```

---

## Architecture

```
┌──────────────────┐       ┌──────────────────┐
│    Dashboard     │──────▶│  Armada Server   │
│ Planning +       │ :7890 │  (REST + WS)     │
│ Dispatch UI      │       │  ports 7890-7891 │
│ port 3000        │       │                  │
└──────────────────┘       └──────────────────┘
                                │
                         ┌──────┴──────┐
                         │ Local SQLite│
                         │  example    │
                         └─────────────┘
```

The dashboard container serves the React SPA and proxies nothing — the browser makes API calls directly to the server on port 7890. The default standalone example runs the .NET application with an embedded SQLite database. The active fleet deployment uses PostgreSQL instead.

That dashboard includes the planning workflow as well as direct dispatch: you can chat with a captain inside the UI, keep the transcript, and hand the selected reply directly into dispatch without leaving the browser.

Captain processes run with the Admiral and receive
`http://localhost:7891/mcp` through their runtime configuration by default.
Captain MCP requests use mission-scoped credentials and caller-filtered tools.
Keep port 7891 on a trusted interface and do not give operator credentials to captains.

---

## Docker Compose Configuration

The default Armada stack file is `docker/armada/compose.yaml`:

```yaml
services:
  armada-server:
    build:
      context: ../..
      dockerfile: src/Armada.Server/Dockerfile
    ports:
      - "7890:7890"
      - "7891:7891"
    volumes:
      - ./armada.json:/app/data/armada.json
      - ./db:/app/data/db
      - ./logs:/app/data/logs

  armada-dashboard:
    build:
      context: ../..
      dockerfile: src/Armada.Dashboard/Dockerfile
    ports:
      - "3000:80"
    environment:
      - ARMADA_SERVER_URL=http://armada-server:7890
    depends_on:
      - armada-server
```

The proxy stack file is `docker/proxy/compose.yaml`:

```yaml
services:
  armada-proxy:
    build:
      context: ../..
      dockerfile: src/Armada.Proxy/Dockerfile
    ports:
      - "7893:7893"
    environment:
      - ARMADA_PROXY_SETTINGS_FILE=/config/proxysettings.json
    volumes:
      - ./proxysettings.json:/config/proxysettings.json:ro
      - ./data:/app/data
      - ./logs:/app/data/logs
```

### Volumes

| Host Path | Container Path | Purpose |
|-----------|----------------|---------|
| `docker/armada/armada.json` | `/app/data/armada.json` | Server configuration |
| `docker/armada/db/` | `/app/data/db/` | SQLite database files |
| `docker/armada/logs/` | `/app/data/logs/` | Server log files |
| `docker/proxy/proxysettings.json` | `/config/proxysettings.json` | Proxy configuration |
| `docker/proxy/data/` | `/app/data/` | Proxy state files |
| `docker/proxy/logs/` | `/app/data/logs/` | Proxy log files |

### Data Directory Variable

`ARMADA_DATA_DIRECTORY` relocates the whole default data directory: settings,
database, logs, docks and repositories. `ARMADA_DATA_DIR` is an alias. When
both are set to non-empty values, `ARMADA_DATA_DIRECTORY` wins. With neither
set, the default is `~/.armada`. Values in `armada.json` still override the
paths derived from it.

### Server Configuration

Edit `docker/armada/armada.json` to customize:

```json
{
  "dataDirectory": "/app/data",
  "logDirectory": "/app/data/logs",
  "docksDirectory": "/app/data/docks",
  "reposDirectory": "/app/data/repos",
  "admiralPort": 7890,
  "mcpPort": 7891,
  "gitHubToken": null,
  "syslogServers": [
    {
      "hostname": "127.0.0.1",
      "port": 514
    }
  ],
  "allowSelfRegistration": true,
  "rest": {
    "hostname": "0.0.0.0"
  },
  "database": {
    "type": "Sqlite",
    "filename": "/app/data/db/armada.db"
  }
}
```

To use MySQL, PostgreSQL, or SQL Server instead of SQLite, change the `database` section:

```json
{
  "database": {
    "type": "Mysql",
    "connectionString": "Server=db-host;Database=armada;User=root;Password=secret;"
  }
}
```

### AgentWake In A Container

AgentWake process delivery runs the agent CLI inside the Admiral container, not
on the Docker host. The selected executable, its authentication/configuration,
the working directory, and every repository or memory path needed by the fresh
session must therefore exist in that container.

Add the following to the settings file that the running Admiral reports as
loaded (bind-mount that file so the change survives a container replacement):

```json
{
  "remoteTrigger": {
    "enabled": true,
    "mode": "AgentWake",
    "agentWake": {
      "runtime": "OpenCode",
      "deliveryMode": "Both",
      "participantKey": "ops-session",
      "workingDirectory": "/workspace"
    }
  }
}
```

The configured `participantKey` survives an Admiral restart. A transient
`armada_register_agentwake_session` registration does not; when it supplies its
own participant key, it overrides the configured key until the next restart.
Use a transient registration for a controlled probe or for Claude and Codex
resume state. OpenCode starts fresh for every wake, so the note must carry the
complete task and the session must read the board and durable memory. Confirm
the effective owner with `armada_agentwake_status`.

Use `StoredWake` when a resident operator only needs Wake rows. Use
`SpawnProcess` or `Both` only after a controlled spawn test. Do not run a host
service-managed Admiral beside the containerized Admiral; two service graphs
can consume the same settings and state with different registrations.

Valid `type` values: `Sqlite`, `Mysql`, `Postgresql`, `SqlServer`.

---

## Stopping and Restarting

```bash
# Stop Armada containers (preserves data)
cd docker/armada
docker compose down

# Restart
docker compose up -d

# View logs
docker compose logs -f armada-server
docker compose logs -f armada-dashboard
```

For the proxy stack:

```bash
cd docker/proxy
docker compose down
docker compose up -d
docker compose logs -f armada-proxy
```

---

## Factory Reset

To delete all data and start fresh while preserving configuration:

**Windows:**
```bash
cd docker/armada/factory
reset.bat
```

**Linux / macOS:**
```bash
cd docker/armada/factory
./reset.sh
```

Both scripts prompt for confirmation, stop containers, and delete local SQLite database and log files. The `armada.json` configuration file is preserved. If the Docker config points at MySQL, PostgreSQL, or SQL Server instead of the mounted SQLite file, the external database is not modified by the reset scripts.

---

## Building Images from Source

Build scripts are split by platform under `scripts/windows/`, `scripts/linux/`, and `scripts/macos/`. Shared shell implementations live under `scripts/common/`. They build multi-platform images (amd64 + arm64) and push to Docker Hub.

### Build latest only

```bash
Linux:
./scripts/linux/build-server.sh

macOS:
./scripts/macos/build-server.sh

Windows:
scripts\windows\build-server.bat

Linux:
./scripts/linux/build-dashboard.sh

macOS:
./scripts/macos/build-dashboard.sh

Windows:
scripts\windows\build-dashboard.bat
```

### Build latest + versioned tag

```bash
Linux:
./scripts/linux/build-server.sh v1.0.0

macOS:
./scripts/macos/build-server.sh v1.0.0

Windows:
scripts\windows\build-server.bat v1.0.0

Linux:
./scripts/linux/build-dashboard.sh v1.0.0

macOS:
./scripts/macos/build-dashboard.sh v1.0.0

Windows:
scripts\windows\build-dashboard.bat v1.0.0
```

This produces both `jchristn77/armada-server:latest` and `jchristn77/armada-server:v1.0.0` (and the same for the dashboard).

### Building locally (no push)

If you want to build for local use without pushing to Docker Hub, run `docker build` directly:

```bash
# Server
docker build -f src/Armada.Server/Dockerfile -t armada-server:local .

# Dashboard
docker build -f src/Armada.Dashboard/Dockerfile -t armada-dashboard:local .
```

Then update `docker/armada/compose.yaml` or `docker/proxy/compose.yaml` to reference your local tags instead of local builds if you want to pin named images.

### Rebuilding a local image with rollback retention

Before rebuilding a mutable local tag, retain both the image ID used by the
running container and the image ID currently behind that tag. The helper makes
two unique UTC-dated tags in the same repository, checks that neither tag
already exists, and only then runs a local `docker build`. It prints both
retention tags so an operator can use them during rollback. A missing or
stopped container, an unreadable tag, a retention collision, or a failed tag
operation stops before the build. A failed build leaves the retention tags in
place.

Run it from the repository root with the real container and mutable tag for the
local deployment:

```bash
./scripts/common/rebuild-local-image.sh \
  armada-server \
  local/armada-server:latest \
  src/Armada.Server/Dockerfile \
  .
```

The command uses a fixed local build invocation and does not accept a push
flag. The container name and image tag are validated, and all Dockerfile and
context paths are passed as separate arguments. Set `ARMADA_DOCKER_BIN` only
when supplying a Docker-compatible test double; do not use it to bypass the
retention checks.

Each rebuild retains two images, so after a successful build the helper keeps
the newest three distinct images of the repository plus the running and
just-built images, removes the tags matching `ARMADA_PRUNE_TAG_PATTERNS` from
every older image, and prunes dangling layers. Set `ARMADA_RETAINED_KEEP` to
keep a different number, and `ARMADA_PRUNE_TAG_PATTERNS` (space-separated
globs, default `armada-retained-* rollback-before-* candidate-* superseded-*
refused-*`) to change which tags may go. The mutable tag is never removed, and
an image still used by a container or named by an unmatched tag stays on disk.

The build always passes the context's commit as `GIT_SHA`. Set
`ARMADA_CLI_REFRESH` to a number, such as the output of `date +%s`, to also pass
it as the Dockerfile's `CLI_REFRESH` argument: the agent CLI layer is then rebuilt
and every CLI is installed at its latest version. Any other value is refused
before the retention step.

### Host storage cleanup

`scripts/linux/storage-cleanup/` holds a daily, root-run cleanup for the Docker
host. It reclaims space outside the admiral's own disk lifecycle (see
`MCP_TOOL_CATALOG.md` section 8.20). Every host-specific value — roots,
exclusions, the agent home directory, lock and receipt paths, database name
patterns, container names, image tag patterns and retention — comes from one
JSON file, `/etc/armada/storage-cleanup.json`. Each script takes
`--config <path>`, plans by default, and deletes only with `--apply`.
`storage-cleanup.example.json` documents every key.

`daily-storage-cleanup.py` runs the four stages in order under one lock and
writes a `daily-<stamp>.json` receipt plus each stage's output to
`receiptDirectory`:

| Stage | Removes |
| --- | --- |
| `prune-build-outputs.py` | `bin`, `obj` and `node_modules` folders that git ignores and does not track, and isolated `dotnet-cli-home` NuGet caches, under `roots`. With `scratchRoots` set, also a whole top-level scratch folder whose newest file is older than `wholeFolderRetentionDays`. |
| `prune-agent-caches.py` | Expired agent CLI session logs and outputs, orphan OpenCode snapshots, expired OpenCode sessions (native delete after a verified gzip backup, then `VACUUM` when worthwhile), and the old NuGet HTTP cache under `home`. |
| `prune-docker-storage.py` | `rollback-before-*` tags once three dated retention sets exist, tags matching `unusedImageTagPatterns` that no container uses and that are older than `unusedImageMinAgeDays`, the reviewed `disposableVolumes`, and dangling layers. |
| `prune-test-databases.py` | Generated test databases older than `minimumAgeHours` in each target Postgres container, only when no test runner is running. |

Every stage fails closed. It skips a path that a running process references, a
path behind a symlink, and a candidate that changed between plan and delete.
A scratch folder's age is the newest modification time of any file inside it;
directory times do not count and symlinks are not followed. A scratch folder
that holds an `.armada-keep` file anywhere inside it, or that holds an excluded
path, is never removed. An unused image tag is removed with `docker rmi` (never
`-f`); a tag whose image is also named by a `protectedImageTags` entry, or that
matches `protectedTagPatterns`, is never removed.

Install as root:

```bash
sudo scripts/linux/storage-cleanup/install-storage-cleanup.sh --dry-run
sudo scripts/linux/storage-cleanup/install-storage-cleanup.sh
```

The installer copies the scripts to `/usr/local/lib/armada-storage-cleanup` and
the `armada-storage-cleanup` service and timer to `/etc/systemd/system`. It
never overwrites an existing config. Edit the config, run a dry run
(`python3 /usr/local/lib/armada-storage-cleanup/daily-storage-cleanup.py`), read
the receipts, then `systemctl enable --now armada-storage-cleanup.timer`. The
timer runs daily at 05:00 UTC. Test the scripts with
`python3 scripts/linux/storage-cleanup/test-storage-cleanup.py`.

---

## Mux Runtime

The admiral image builds the Mux CLI (`jchristn/Mux`) from source in the `mux`
stage of `src/Armada.Server/Dockerfile` and installs it at `/opt/mux`. The
stage pins Mux to an exact commit (`MUX_REF`) and builds it against a patched
Voltaic, the MCP client library Mux uses. `VOLTAIC_REF` is the Voltaic release
that the Mux commit references. The patch is
`docker/patches/voltaic-mcp-http-client.patch`, and it makes Voltaic's
Streamable HTTP client follow the MCP specification in two places:

- A POST response with a `text/event-stream` body is read as server-sent
  events. The response is the event whose JSON-RPC `id` matches the request. A
  plain JSON body is read as before.
- The `ping` result may be any JSON value. The specification returns `{}`.

Without the patch Mux loads no tool from the Armada MCP endpoint: the endpoint
answers with an event stream and `ping` returns `{}`. The stage packs the
patched Voltaic as `VOLTAIC_VERSION` (`0.7.1-armada.1`). Mux restores Voltaic
only from that local package through `packageSourceMapping`. The build fails
when the patch does not apply or when the staged `Voltaic.dll` does not carry
that version, and the build log prints the staged informational version. To
move Mux, change `MUX_REF` and set `VOLTAIC_REF` and `VOLTAIC_BASE_VERSION` to
the Voltaic release the new commit references. Then check that the patch still
applies.

Mux captains pass configured endpoint, adapter type, temperature, token limit,
turn limit, and system prompt path to `mux print`. `--mux-max-turns` sets the
per-captain agent-loop limit; Armada clamps it to the Mux-supported range of 1
through 100. The base URL is passed through `OPENAI_BASE_URL`. Print mode accepts
the `auto` and `deny` approval policies; other configured values stop launch
with an error. With no policy configured, Armada keeps the existing `--yolo`
default.

The Dashboard captain form retains the Mux turn cap when a captain is edited.
Its advanced Mux fields accept an optional integer from 1 to 100.

## Database Client Tools

Backup (`armada_backup`, `GET /api/v1/backup`, WebSocket `backup`) and the
self-deploy preflight, and startup migration backup use the configured provider's native client tools inside
the admiral container. When a tool is absent they fail with
`native_client_missing_<tool>`. They never report a successful backup without
the tool.

| Provider | Tools the admiral container needs | In `src/Armada.Server/Dockerfile` |
| --- | --- | --- |
| SQLite | none (built in) | yes |
| PostgreSQL | `pg_dump`, `pg_restore`, `psql`, `createdb`, `dropdb`; client major at least the server major | `postgresql-client-16` |
| MySQL | `mysql`, `mysqldump` | `mysql-client-8.0` |
| SQL Server | ODBC 18 `sqlcmd` (`mssql-tools18`) | not installed: needs Microsoft's apt repository and `ACCEPT_EULA=Y`; add it in a derived image |

A PostgreSQL server newer than 16 needs a matching client package in the image.

### Backup before a schema upgrade

Startup and `--validate-database` inspect the stored migration version before
initializing the database. An existing versioned database with pending migrations
requires a native backup and a successful isolated restore check before any
migration runs. Fresh databases and databases at the current schema version skip
this backup.

The archive stays under `<data-directory>/backups/` with a
`pre-migration-<timestamp>-v<stored>-to-v<target>-<unique>.zip` name. Its manifest
records the stored schema version and verified backup state. SQL Server keeps
its native `.bak` artifact at the server path recorded in the manifest; retain
that file with the archive.

The database account needs the same backup, isolated database creation, restore,
and cleanup permissions as `armada_backup`. Native client tools must be present
before an upgrade starts. SQL Server also requires
`selfDeploy.sqlServerBackupDirectory`, a directory writable by the database
server, and the derived image with `sqlcmd` described above.

A failed backup or restore check stops startup with exit code 3 and prints
`DATABASE STARTUP REFUSED`. No migration starts. Correct the reported storage,
client, or permission fault and start again. The server keeps successful
pre-migration backups; it does not automatically restore or remove them.

### Upgrading a running deployment

An image built before these packages were added has none of the server
clients. Adding them takes a rebuild; there is no runtime switch.

1. Rebuild with `scripts/common/rebuild-local-image.sh` so the running image
   and the current tag are retained for rollback, then recreate the container
   (see "Rebuilding a local image with rollback retention").
2. Verify the tools inside the new container:
   `docker exec armada-admiral pg_dump --version` must report 16.x or later
   for the PostgreSQL server in use. For MySQL also check `mysqldump --version`.
3. Run `armada_backup` once. The archive's `manifest.json` must name the
   configured `databaseType`, carry non-zero `recordCounts` and
   `backupValidated`/`restoreVerified` set to `true`.
   `native_client_missing_<tool>` means the image still lacks the tool.

Restore replaces the database only on SQLite; see `docs/REST_API.md`.

## Captain Study Tools

Captains run inside the admiral container, so the tools a study needs must be
in the image. `src/Armada.Server/Dockerfile` installs:

| Tool | Use | Package |
| --- | --- | --- |
| `pdftotext` | Read text from PDF sources | `poppler-utils` |
| `monodis` | Disassemble .NET IL | `mono-utils` |
| `dnfile` (Python) | Walk .NET metadata tables, for example custom attributes | pip `dnfile`, pinned by the `DNFILE_VERSION` build argument |
| `rg` | Search a dock's files | `ripgrep` |
| `python` | Run Python scripts as `python` (it is `python3`) | `python-is-python3` |
| `slopwatch` | Check a .NET change for slop patterns; the .NET captain skill runs it | .NET tool `Slopwatch.Cmd` in `/opt/slopwatch`, pinned by the `SLOPWATCH_VERSION` build argument and linked as `/usr/local/bin/slopwatch` |
| `sha256sum` | Hash an artifact | `coreutils` (in the base image) |

An image built without them takes a rebuild (see "Rebuilding a local image with
rollback retention"). Verify with
`docker exec armada-admiral bash -lc 'pdftotext -v; command -v monodis; python3 -c "import dnfile"; rg --version; python --version; slopwatch --version'`.

## Ports Reference

| Port | Protocol | Service | Description |
|------|----------|---------|-------------|
| 7890 | HTTP | Admiral REST API | REST endpoints, OpenAPI, dashboard, WebSocket at /ws |
| 7891 | TCP | MCP | Model Context Protocol for agent communication |
| 3000 | HTTP | React Dashboard | Standalone SPA (nginx) |

---

## Troubleshooting

**Container won't start:**
```bash
cd docker/armada
docker compose logs armada-server
```
Check that `armada.json` exists and has valid JSON.

**Database permission errors:**
Ensure the `docker/armada/db/` directory is writable. On Linux:
```bash
chmod 777 docker/armada/db
```

**Dashboard can't reach server:**
The React dashboard makes API calls from the browser, not from the container. Ensure port 7890 is accessible from your machine. If running Docker on a remote host, update the dashboard's `VITE_ARMADA_SERVER_URL` environment variable to point to the server's external address.

**CORS errors:**
The Armada server enables CORS on all routes by default. If you see CORS errors, verify you're accessing the correct port (7890 for the API).
