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

### Initial admin password

The container listens on every interface (`rest.hostname` is `0.0.0.0`). A new
database seeds the admin account `admin@armada` with the published default
password and a `default` bearer token, so the Admiral refuses to start on a
non-loopback listener while either is active. Set the first-start password in
the environment of `docker compose`:

```bash
ARMADA_INITIAL_ADMIN_PASSWORD=<admin-password> docker compose up -d
```

The variable applies only while the admin still has the default password, and
it deactivates the `default` bearer token. It is at least 8 characters and not
`password`. Later restarts never overwrite a password changed afterwards, so
the variable can stay set or be removed. Sign in as `admin@armada` with that
password. For API access, create a credential in the dashboard and send it as
`Authorization: Bearer <token>`.

An unattended host that must start with the default credentials can set
`allowDefaultCredentialsOnNetwork` to `true` in `armada.json`. The Admiral then
logs a warning at every start instead of refusing. Anyone who can reach the
port can sign in with the published defaults, so use this only on an isolated
network.

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
    environment:
      - ARMADA_DATA_DIR=/app/data
      - ARMADA_INITIAL_ADMIN_PASSWORD=${ARMADA_INITIAL_ADMIN_PASSWORD:-}
    volumes:
      - ./armada.json:/app/data/settings.json
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
      - ARMADA_PROXY_PASSWORD=${ARMADA_PROXY_PASSWORD:?set ARMADA_PROXY_PASSWORD to the shared secret}
    volumes:
      - ./proxysettings.json:/config/proxysettings.json:ro
      - ./data:/app/data
      - ./logs:/app/data/logs
```

The proxy refuses to start with the built-in shared password, so the stack requires `ARMADA_PROXY_PASSWORD`. Set the same value as `remoteControl.password` on each Armada instance.

### Volumes

| Host Path | Container Path | Purpose |
|-----------|----------------|---------|
| `docker/armada/armada.json` | `/app/data/settings.json` | Server configuration |
| `docker/armada/db/` | `/app/data/db/` | SQLite database files |
| `docker/armada/logs/` | `/app/data/logs/` | Server log files |
| `docker/proxy/proxysettings.json` | `/config/proxysettings.json` | Proxy configuration |
| `docker/proxy/data/` | `/app/data/` | Proxy state files |
| `docker/proxy/logs/` | `/app/data/logs/` | Proxy log files |

### Data Directory Variable

`ARMADA_DATA_DIRECTORY` relocates the whole default data directory: settings,
database, logs, docks and repositories. `ARMADA_DATA_DIR` is an alias. When
both are set to non-empty values, `ARMADA_DATA_DIRECTORY` wins. With neither
set, the default is `~/.armada`. The Admiral reads `settings.json` from that
directory, so the default stack sets `ARMADA_DATA_DIR=/app/data` and mounts
`docker/armada/armada.json` at `/app/data/settings.json`. Values in that file
still override the paths derived from the data directory.

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

## Non-root Containers

The default `armada-server` and `armada-proxy` images run as root. Each Dockerfile also defines a `runtime-nonroot` stage that runs the process as an unprivileged account. The default stage, `runtime`, is the last stage of each Dockerfile, so a build without `--target` produces the root image with the same user, paths and volumes as before. The dashboard image has no non-root stage. It uses the official nginx image, which runs as root.

The `runtime-nonroot` stage takes two build arguments, `ARMADA_UID` and `ARMADA_GID` (both default to `1654`, the .NET base image's `app` user). It reuses an account that already holds the UID, or creates `armada`, and sets the account's home directory to `/home/armada` (mode `0700`). `/home/armada` and `/app/data` belong to that account. The application files under `/app` stay owned by root and are read-only to the account. UID and GID `0` fail the build.

### Opting in

Each stack has a compose override that selects the stage, passes the build arguments, and sets `user:` to the same UID and GID:

```bash
cd docker/armada
docker compose -f compose.yaml -f compose.nonroot.yaml up -d --build

cd ../proxy
docker compose -f compose.yaml -f compose.nonroot.yaml up -d --build
```

`ARMADA_UID` and `ARMADA_GID` in the environment (or an `.env` file next to the compose file) select another account. The build argument and the runtime `user:` must be the same, because the image prepares the home directory for the build-time UID.

### State ownership

A container that runs as UID `N` can write only files that UID `N` (or its group) owns. Every directory the container writes must belong to the chosen UID before the first non-root start:

| Stack | Paths |
|-------|-------|
| Server | `docker/armada/db`, `docker/armada/logs`, and any other bind mount or named volume under `/app/data` (docks, repositories, backups, code index) |
| Server | A volume or bind mount at `/home/armada` that holds CLI homes (`.claude`, `.codex`, `.gemini`, `.config`), Harbor runner state, `.ssh` and `.gitconfig` |
| Proxy | `docker/proxy/data`, `docker/proxy/logs` |

Migrate an existing deployment with the container stopped, after a backup:

```bash
cd docker/armada
docker compose down
tar -czf armada-state-backup.tar.gz db logs
sudo chown -R 1654:1654 db logs

# A named volume: run the same chown through a throwaway container.
docker run --rm -v <volume-name>:/state alpine chown -R 1654:1654 /state
```

Replace `1654:1654` with the chosen `ARMADA_UID:ARMADA_GID`. Mounted configuration files (`armada.json`, `proxysettings.json`) only need to be readable by that UID. SSH keys mounted for git must be readable by the account and must keep mode `0600`; mount them at `/home/armada/.ssh`, and a git configuration at `/home/armada/.gitconfig`, because the non-root account has no access to `/root`.

### What breaks when ownership is wrong

- The Admiral cannot write its database, logs, docks or repositories. It fails at start, or fails the first mission that needs the path, with `Permission denied` or a SQLite `unable to open database file` error.
- Agent CLIs (Claude Code, Codex, Gemini, Cursor, OpenCode, Mux) read credentials and write session state under `$HOME`. A CLI home that the account cannot read shows as a logged-out CLI; one it cannot write fails at the first session. Credentials stored under `/root` in a root deployment are not visible to the non-root account and must be copied to `/home/armada` with the new ownership.
- Harbor and runner tools, `dotnet`, `npm`, `git` and `gh` write caches and configuration under `$HOME`. An unwritable home fails those tools.
- Git refuses a repository whose owner differs from the running user (`detected dubious ownership`) unless the directory is listed as safe.
- The proxy cannot write its login rate-limit state or its logs under `/app/data`.

### Verifying

On a Linux host with Docker:

```bash
docker compose -f compose.yaml -f compose.nonroot.yaml up -d --build
docker compose exec armada-server id
docker compose exec armada-server sh -c 'touch /app/data/db/.probe && rm /app/data/db/.probe && echo "HOME=$HOME" && test -w "$HOME" && echo home-writable'
docker compose logs --tail 50 armada-server
curl -fsS http://localhost:7890/api/v1/status/health
```

`id` reports the chosen UID and GID, the write probe succeeds, and the log shows no `Permission denied`. Run `docker compose exec armada-server <cli> --version` for each agent CLI the deployment uses, and sign each CLI in again as that user when its credentials were not copied.

### Rolling back

Run the stack without the override file:

```bash
docker compose -f compose.yaml up -d --build
```

The default image runs as root, and root can read and write files owned by any UID, so no ownership change is needed. Files that the non-root run created stay owned by the non-root UID. Move credentials from `/home/armada` back to `/root` if the root deployment uses CLI sign-ins.

### Pinning base images

The Dashboard Dockerfile pins Node and nginx to exact patch tags. The proxy Dockerfile defaults are exact patch tags. The server image still defaults to the floating .NET SDK tag `10.0`, because that image carries the SDK so a vessel gate can honor a repository `global.json`. A proxy build argument overrides a default, including a digest:

| Image | Build argument | Default |
|-------|----------------|---------|
| Server build and runtime | `SDK_IMAGE` | `mcr.microsoft.com/dotnet/sdk:10.0` |
| Proxy build | `SDK_IMAGE` | `mcr.microsoft.com/dotnet/sdk:10.0.403-noble` |
| Proxy runtime | `RUNTIME_IMAGE` | `mcr.microsoft.com/dotnet/runtime:10.0.14-noble` |
| Proxy dashboard build | `NODE_IMAGE` | `node:22.23.4-alpine3.24` |
| Dashboard build | none (Dockerfile `FROM`) | `node:22.23.4-alpine3.24` |
| Dashboard runtime | none (Dockerfile `FROM`) | `nginx:1.31.8-alpine3.24` |

```bash
docker build -f src/Armada.Proxy/Dockerfile \
  --build-arg SDK_IMAGE=mcr.microsoft.com/dotnet/sdk@sha256:<digest> \
  --build-arg RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime@sha256:<digest> \
  -t armada-proxy .
```

In compose, set the same names under `build.args`.

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

`daily-storage-cleanup.py` runs the stages that `orchestrator.stages` names, in
order under one lock, and writes a `daily-<stamp>.json` receipt plus each
stage's output to `receiptDirectory`. Without `stages` it runs the first four;
`deploy-dumps` runs only when the list names it, because it needs its own
`deployDumps` section. Receipts and stage output are retained for verified history
archival, including on a dry run. The former `receiptRetentionDays` setting is
ignored:

| Stage | Removes |
| --- | --- |
| `prune-build-outputs.py` | `bin`, `obj` and `node_modules` folders that git ignores and does not track, and isolated `dotnet-cli-home` NuGet caches, under `roots`. With `scratchRoots` set, also a whole top-level scratch folder whose newest file is older than `wholeFolderRetentionDays`. |
| `prune-agent-caches.py` | Orphan OpenCode Git snapshot caches and expired NuGet HTTP-cache files under `home`. Raw sessions, chat databases, transcripts, tool output, backups and maintenance copies remain with the history archive owner. |
| `prune-docker-storage.py` | `rollback-before-*` tags once three dated retention sets exist, tags matching `unusedImageTagPatterns` that no container uses and that are older than `unusedImageMinAgeDays`, the reviewed `disposableVolumes`, and dangling layers. |
| `prune-test-databases.py` | Generated test databases older than `minimumAgeHours` in each target Postgres container, only when no test runner is running. |
| `prune-deploy-dumps.py` | Pre-deploy database dumps in `directory` whose name matches `pattern`, except the newest `keep` (default 2) by modification time. A dump that a process holds open is kept. |

The agent-cache stage does not retire raw history. The history archive owns verified upload and local retirement. Archive retirement requires verified remote copies and the supported retention rules; an ordinary local backup is not archive proof. Provider-specific active-writer protection and whole-chat archival must be established before adding mutable chat databases to an archive root.

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
