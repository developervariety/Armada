# Getting Started with Armada

Go from zero to three AI agents working in parallel in under five minutes.

> **⚠️ Security Note:** Armada runs AI agent captains with permission-bypassing flags enabled by default (e.g. `--dangerously-skip-permissions` for Claude Code, `--approval-mode full-auto` for Codex, `--sandbox none` for Gemini). Agents can read, write, and execute code without user confirmation. Be aware of this before proceeding.

## Install

```bash
# Requires: .NET 10 SDK (https://dot.net/download)
# Requires: Claude Code on your PATH (https://docs.anthropic.com/en/docs/claude-code)

dotnet tool install -g Armada.Helm
```

Verify:

```bash
armada doctor
```

Helper scripts are available under `scripts/windows/`, `scripts/linux/`, and `scripts/macos/` if you are working from source. Shared shell implementations live under `scripts/common/`.

If you are installing from a source checkout instead of a published tool package, use the platform install script:

Linux: `./scripts/linux/install.sh`

macOS: `./scripts/macos/install.sh`

Windows: `scripts\windows\install.bat`

Those scripts build the solution, deploy dashboard assets, and install `Armada.Helm` as a global tool from the repo.

## Connect Claude Code

```bash
armada mcp install
```

This configures Armada MCP for Claude Code, Codex, Gemini, and Cursor, and installs the Claude Code orchestrator agent. Use `armada mcp remove` to remove those entries later.

## Start the server

```bash
armada server start
```

Leave this running. Open a new terminal for everything else.

If you want Armada managed as a local deployment on your machine instead of a foreground terminal process, use the source-deployment scripts:

| Task | Linux | macOS | Windows |
|------|-------|-------|---------|
| Publish server and dashboard only | `./scripts/linux/publish-server.sh` | `./scripts/macos/publish-server.sh` | `scripts\windows\publish-server.bat` |
| Install and register a user-scoped local deployment | `./scripts/linux/install-systemd-user.sh` | `./scripts/macos/install-launchd-agent.sh` | `scripts\windows\install-windows-task.bat` |
| Update the deployed server from the current checkout | `./scripts/linux/update-systemd-user.sh` | `./scripts/macos/update-launchd-agent.sh` | `scripts\windows\update-windows-task.bat` |
| Verify the running deployment | `./scripts/linux/healthcheck-server.sh` | `./scripts/macos/healthcheck-server.sh` | `scripts\windows\healthcheck-server.bat` |
| Remove the startup-managed deployment | `./scripts/linux/remove-systemd-user.sh` | `./scripts/macos/remove-launchd-agent.sh` | `scripts\windows\remove-windows-task.bat` |

On Windows, the install and update wrappers also accept a framework override when the machine only has one SDK, for example `scripts\windows\install-windows-task.bat net8.0` or `scripts\windows\update-windows-task.bat --framework net8.0`.

These scripts publish `Armada.Server` into `~/.armada/bin` on Linux and macOS, or `%USERPROFILE%\.armada\bin` on Windows, and deploy dashboard assets into `~/.armada/dashboard` or `%USERPROFILE%\.armada\dashboard`.

The remove scripts unregister the user-scoped startup entry or service, but they do not delete the published files under `~/.armada` or `%USERPROFILE%\.armada`.

Repo-relative deployment script paths:

- Linux: `scripts/linux/install-systemd-user.sh`, `scripts/linux/update-systemd-user.sh`, `scripts/linux/healthcheck-server.sh`
- macOS: `scripts/macos/install-launchd-agent.sh`, `scripts/macos/update-launchd-agent.sh`, `scripts/macos/healthcheck-server.sh`
- Windows: `scripts/windows/install-windows-task.bat`, `scripts/windows/update-windows-task.bat`, `scripts/windows/healthcheck-server.bat`

## Planning Workflow

If you want to work out the plan with a captain before dispatching anything, use the dashboard planning screen:

```text
Dashboard Planning UI
    |
    +--> Reserve captain + dock/worktree
    +--> Chat with the captain inside the UI
    +--> Keep the transcript as the source of truth
    +--> Select the reply you want to use
    +--> Summarize it, open it in Dispatch, or dispatch directly
```

1. Start Armada and open `http://localhost:7890/dashboard`
2. Go to `Planning`
3. Pick a captain, vessel, optional pipeline, and playbooks
4. Chat until you have a plan you trust
5. Select the assistant response you want
6. Either summarize it into a cleaner draft, open that draft in the main `Dispatch` page, or dispatch it directly from the planning page
7. Delete the session when you no longer need the transcript, or let Armada clean it up through retention settings

Current planning-session constraints:

- Planning currently supports the built-in `ClaudeCode`, `Codex`, `Gemini`, `Cursor`, and `Mux` runtimes. `Custom` captains are blocked there.
- A planning session reserves the selected captain and a dock/worktree for the selected vessel until you stop the session.
- The captain can inspect and modify the repository while planning.
- Planning is transcript-backed today. Each turn relaunches the runtime with the preserved transcript and repo context instead of keeping a persistent interactive stdin session alive.
- Planning-session persistence is SQLite-first. Non-SQLite backends currently return an explicit unsupported response for planning-session endpoints.
- Armada can summarize a selected planning reply into a dispatch-ready draft before launch.
- You can open that draft in the main `Dispatch` page without copy/paste or dispatch directly from the planning screen.
- Optional cleanup controls are available through `PlanningSessionInactivityTimeoutMinutes` and `PlanningSessionRetentionDays`.

---

## Create a project

We'll create an empty repo and let Armada's agents build the whole thing.

```bash
mkdir ~/code/bookshelf && cd ~/code/bookshelf
git init
git commit --allow-empty -m "Initial commit"
```

If you want agents to push branches, add a remote:

```bash
# Create a repo on GitHub first, then:
git remote add origin https://github.com/you/bookshelf.git
git push -u origin main
```

A local-only repo works fine too — agents work in local worktrees.

## Launch the orchestrator

```bash
claude --agent armada
```

Everything below happens inside this Claude session.

---

## Register the project

> Create a fleet called "demo" and add a vessel named "bookshelf" pointing to ~/code/bookshelf.

Claude calls `create_fleet` and `add_vessel`. You'll see IDs like `flt_...` and `vsl_...` in the response.

## Scaffold the project

> Create a mission on bookshelf: "Initialize a Python project. Create pyproject.toml with FastAPI, uvicorn, and pytest as dependencies. Create src/main.py with a FastAPI app that has a GET /health endpoint returning {"status": "ok"}. Create a README.md with the project name and a one-line description. Run no tests yet."

One captain spins up, creates a worktree, builds the scaffold, and completes. This gives the parallel missions a foundation to build on.

> Check mission status.

Wait until it shows Complete.

## Dispatch a parallel voyage

Now three agents work simultaneously on non-overlapping parts of the codebase:

> Dispatch a voyage called "Core Features" to bookshelf with these missions:
>
> 1. "Book CRUD endpoints. Create src/models.py with a Book dataclass (id, title, author, year, isbn). Create src/books.py with an in-memory store and FastAPI router mounted at /books with GET (list all), GET /{id}, POST (create), PUT /{id} (update), DELETE /{id}. Return 404 for missing books. Import and include the router in src/main.py."
>
> 2. "Search endpoint. Create src/search.py with a FastAPI router mounted at /search. Add GET /search?q=term that searches books by title or author (case-insensitive substring match). Import the book store from src/books.py. Include the router in src/main.py."
>
> 3. "Test suite. Create tests/test_books.py with pytest tests using FastAPI TestClient. Test: create a book, get it by ID, list all books, update a book, delete a book, get a missing book returns 404. Create tests/test_search.py testing search by title, search by author, and empty results. Import the app from src/main.py."

Three captains spin up in isolated worktrees, each working on their own files.

## Monitor progress

> Check voyage status.

You'll see each mission's status — Pending, InProgress, or Complete.

> Show the diff for the book CRUD mission.

Review the code changes. You can do this while other missions are still running.

> Show the captain log for the search mission.

See what the agent is doing in real time.

## Review and land

Once all three missions show Complete:

> Show the diff for each completed mission.

Review the changes. When you're satisfied:

> Enqueue all completed mission branches to the merge queue, then process it.

Armada tests and merges each branch into main in order. Your project is built.

---

## Without the orchestrator

Everything above works from the CLI. No Claude Code required.

```bash
# Register
armada fleet add demo
armada vessel add bookshelf ~/code/bookshelf --fleet demo

# Quick dispatch from inside the repo
cd ~/code/bookshelf
armada go "Initialize a Python FastAPI project with a /health endpoint"

# Parallel voyage
armada voyage create "Core Features" --vessel bookshelf \
  --mission "Book CRUD endpoints..." \
  --mission "Search endpoint..." \
  --mission "Test suite..."

# Monitor
armada watch

# Review
armada diff msn_abc123
armada log captain-1
```

---

## Next Steps

**CLI reference**

```
armada go <prompt>             Dispatch a task (infers repo from CWD)
armada watch                   Live dashboard
armada diff [mission]          Review changes
armada log <captain>           Tail agent output
armada status                  System overview
armada doctor                  Health check

armada mission list|create|show|cancel|retry
armada voyage  list|create|show|cancel|retry
armada vessel  list|add|remove
armada captain list|add|stop|stop-all|remove
armada fleet   list|add|remove
armada server  start|status|stop
armada config  show|set|init
armada mcp     install|stdio
```

**Configuration** — `armada config show` to see all settings. Key ones: `MaxCaptains` (concurrent agents), `StallThresholdMinutes` (stall detection), `AutoPush`, `AutoCreatePullRequests`, `DefaultRuntime`.

**Web dashboard** — Built-in web UI with live dashboards, diff viewer, log viewer, and settings editor. Served by the Admiral server at `http://localhost:7890/dashboard/`.

**REST API** — Full CRUD on port 7890 under `/api/v1/`. See `docs/REST_API.md`.

**MCP tools** — 43 tools for fleets, vessels, voyages, missions, captains, signals, events, docks, and the merge queue. Any MCP client can orchestrate Armada. See `docs/CLAUDE_CODE_AS_ORCHESTRATOR.md`.

---

## Running with Docker

If you prefer Docker over a local .NET SDK install:

```bash
cd docker
docker compose up -d
```

This starts the Armada server on port 7890 and an optional React dashboard on port 3000. Open `http://localhost:7890/dashboard` or `http://localhost:3000` in your browser.

Log in with the default credentials:

| Field | Value |
|-------|-------|
| Email | `admin@armada` |
| Password | `password` |

For API access from scripts or curl, use `Authorization: Bearer default`.

Data is persisted in `docker/armada/db/`. To stop: `docker compose down`. To reset all data: run `docker/factory/reset.sh` (or `reset.bat` on Windows).

See the [README](README.md#running-locally-with-docker) for full Docker details including volume layout, configuration, and building images from source.

---

## Authentication (v0.3.0)

As of v0.3.0, all REST API endpoints require authentication. The default bearer token (`default`) provides backward-compatible access:

```bash
curl -H "Authorization: Bearer default" http://localhost:7890/api/v1/status
```

The dashboard login screen accepts the default email (`admin@armada`) and password (`password`). After login, the dashboard uses encrypted session tokens automatically.

Creating a tenant through the admin UI/API also seeds a protected `admin@armada` user and default credential for that tenant.

`IsAdmin` is the global admin flag. `IsTenantAdmin` is the tenant-scoped admin flag. Tenant-created seeded admins are created with `IsAdmin = false` and `IsTenantAdmin = true`.

The effective access tiers are:

- `IsAdmin = true`: full system-wide access.
- `IsAdmin = false`, `IsTenantAdmin = true`: full access within the user's tenant, including user and credential management in that tenant.
- `IsAdmin = false`, `IsTenantAdmin = false`: regular-user access limited to tenant-scoped visibility plus self-service on that user's own account and credentials.

Operational records are owned by both tenant and user. Armada persists and indexes those ownership columns consistently across SQLite, PostgreSQL, SQL Server, and MySQL.

`IsProtected` is server-controlled for tenants, users, and credentials. Protected objects cannot be deleted directly, and immutable fields such as IDs, ownership columns, and creation timestamps are preserved by the API on update.

User creation and user updates accept a plaintext `Password` field. Armada hashes the password server-side before storing it. If `Password` is omitted on update, the existing password is preserved. The dashboard Users modal supports both admin-managed password resets and self-service password changes.

If you want to harden server shutdown, set `RequireAuthForShutdown = true` in your settings. When enabled, `POST /api/v1/server/stop` requires a global admin user with `IsAdmin = true`; tenant admins and regular users cannot shut the server down through the REST API.

For production use, create additional users and credentials via the admin API or dashboard. See `docs/REST_API.md` for details.
