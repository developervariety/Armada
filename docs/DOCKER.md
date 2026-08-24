# Running Armada with Docker

This guide covers running the Armada server and dashboard using Docker containers.

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
| `armada-server` | 7890 | REST API, built-in dashboard, and WebSocket at /ws |
| `armada-server` | 7891 | MCP (agent communication) |
| `armada-dashboard` | 3000 | Standalone React dashboard |

Open the dashboard at **http://localhost:3000** (React SPA) or **http://localhost:7890/dashboard** (built-in).

> **Rebuilding either image does not rebuild the other.** `armada-server` embeds
> the legacy dashboard only; the React dashboard is built separately
> (`src/Armada.Dashboard`). For the standalone compose topology, rebuild the
> `armada-dashboard` image as well. For deployments where the Admiral serves
> the React dashboard from the data directory (`~/.armada/dashboard`), run
> `scripts/common/deploy-dashboard.sh` after the server image rebuild.

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
Keep port 7891 on a trusted interface: the MCP endpoint does not provide
per-captain authorization.

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
      "participantKey": "armada-lead",
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

Use `StoredWake` (formerly `McpNotification`, still accepted) when a resident
operator only needs Wake rows. Use
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
./scripts/linux/build-server.sh v0.9.0

macOS:
./scripts/macos/build-server.sh v0.9.0

Windows:
scripts\windows\build-server.bat v0.9.0

Linux:
./scripts/linux/build-dashboard.sh v0.9.0

macOS:
./scripts/macos/build-dashboard.sh v0.9.0

Windows:
scripts\windows\build-dashboard.bat v0.9.0
```

This produces both `jchristn77/armada-server:latest` and `jchristn77/armada-server:v0.9.0` (and the same for the dashboard).

### Building locally (no push)

If you want to build for local use without pushing to Docker Hub, run `docker build` directly:

```bash
# Server
docker build -f src/Armada.Server/Dockerfile -t armada-server:local .

# Dashboard
docker build -f src/Armada.Dashboard/Dockerfile -t armada-dashboard:local .
```

Then update `docker/armada/compose.yaml` or `docker/proxy/compose.yaml` to reference your local tags instead of local builds if you want to pin named images.

---

## Ports Reference

| Port | Protocol | Service | Description |
|------|----------|---------|-------------|
| 7890 | HTTP | Admiral REST API | REST endpoints, OpenAPI, built-in dashboard, WebSocket at /ws |
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

