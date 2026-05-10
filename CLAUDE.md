## Project Context
Armada is a multi-agent orchestration system that scales human developers with AI. It coordinates AI coding agents ("captains") to work on tasks ("missions") across git repositories ("vessels"). Written in C# (.NET), it exposes MCP tools for fleet, vessel, captain, mission, voyage, dock, signal, and merge queue management.

IMPORTANT -- Context Conservation: When using Armada MCP tools, use armada_enumerate with a small pageSize (10-25) to conserve context. Use filters (vesselId, status, date ranges) to narrow results. Only set include flags (includeDescription, includeContext, includeTestOutput, includePayload, includeMessage) to true when you specifically need that data -- by default, large fields are excluded and length hints are returned instead.

## Code Style
For C#: no var, no tuples, using statements instead of declarations, using statements inside the namespace blocks, XML documentation, public things named LikeThis, private things named _LikeThis, one entity per file, null check on set where appropriate and value-clamping to reasonable ranges where appropriate

## ProtectedPaths semantics

This vessel registers `ProtectedPaths = ["**/CLAUDE.md"]` in its admiral configuration. Captains spawned in any armada-vessel dock cannot modify this file -- the merge gate rejects commits that touch it with a coaching message. To propose a rule change, captains emit a `[CLAUDE.MD-PROPOSAL]` block in their final report; the orchestrator applies the proposed edit directly (synced from origin first) and pushes a normal commit.

This file holds **durable per-repo rules only** -- project context, code style, build / test commands, architecture, coding standards, and the upstream sync protocol below. Per-mission briefs and shipped-feature changelogs do NOT belong here; they accumulate token cost on every dispatch (admiral inlines this file into the captain prompt). Cross-cutting `project/` rules live in the `proj-corerules-summary` playbook (auto-attached on this vessel).

# Armada - Claude Code Instructions

## Project
Multi-agent orchestration system for scaling human developers with AI. C#/.NET.

## Build
```bash
dotnet build src/Armada.sln
```

## Test
```bash
dotnet run --project test/Armada.Test.Automated --framework net10.0
dotnet run --project test/Armada.Test.Unit --framework net10.0
dotnet run --project test/Armada.Test.Runtimes --framework net10.0
```

## Architecture
- `Armada.Core` - Domain models, database interfaces, service interfaces, settings
- `Armada.Runtimes` - Agent runtime adapters (Claude Code, Codex, extensible via IAgentRuntime)
- `Armada.Server` - Admiral process: REST API (SwiftStack), MCP server (Voltaic), WebSocket, web dashboard
- `Armada.Helm` - CLI (Spectre.Console), thin HTTP client to Admiral

## Coding Standards

### Naming
- Private fields: `_PascalCase` (e.g., `_Database`, `_Logging`)
- No `var` keyword - always use explicit types
- Async methods: suffix with `Async`, include `CancellationToken token = default`
- Use `.ConfigureAwait(false)` in library code (Core, Runtimes)
- Enums: PascalCase with `Enum` suffix, decorated with `[JsonConverter(typeof(JsonStringEnumConverter))]`
- ID prefixes: flt_, vsl_, cpt_, msn_, vyg_, dck_, sig_, art_

### Language Restrictions
- **No `var`** - always use explicit types (e.g., `List<Fleet> fleets = ...` not `var fleets = ...`)
- **No tuples** - define a class or use out parameters instead of `(string, int)` or `ValueTuple`
- **No direct `JsonElement` access** - always deserialize JSON into a strongly-typed class instance (e.g., `JsonSerializer.Deserialize<Fleet>(json)`) rather than using `GetProperty()` / `GetString()` on `JsonElement`
- **XML documentation** - all public members must have `<summary>` XML doc comments

### File Organization
- One class per file, filename matches class name
- Use `#region` blocks: Public-Members, Private-Members, Constructors-and-Factories, Public-Methods, Private-Methods
- `using` statements go **inside** the `namespace` block, not above it
- Using order: System first, then third-party, then project namespaces

### Patterns
- Constructor injection with null checks: `?? throw new ArgumentNullException(nameof(x))`
- Logging: SyslogLogging with `private string _Header = "[ClassName] ";`
- Database: interface-per-entity pattern (IFleetMethods, IVesselMethods, etc.)
- Settings: nested config objects with validation in setters

### Libraries (use these, they are mine)
- SwiftStack (NuGet) - REST API framework
- Voltaic (NuGet) - MCP/JSON-RPC library
- SyslogLogging (NuGet) - Logging
- PrettyId (NuGet) - ID generation with prefixes

## Key Concepts
- Admiral = coordinator process
- Captain = worker agent (Claude Code, Codex, etc.)
- Fleet = collection of repositories
- Vessel = single git repository
- Mission = atomic work unit
- Voyage = batch of related missions
- Dock = git worktree for a captain
- Signal = message between admiral and captains

## Upstream Sync Protocol

This is a fork of `jchristn/Armada`. Upstream lives at remote `upstream`,
our fork at `origin`. The fork accumulates orchestration features that
aren't in upstream (PR-fallback flow, recovery pipeline, audit queue,
cross-vessel deps, captain-lifecycle hardening, etc.).

**Branch retention note:** keep `origin/fix/memory-dashboard-oom` until
upstream either merges or explicitly rejects the upstream memory/OOM PR.
Do not delete that branch during cleanup just because the local fork has
already absorbed the fixes; we previously lost a Cursor-related bug branch
by cleaning it up before the upstream disposition was settled.

**Whenever a mission merges from `upstream/main` into our `main`, that
same voyage MUST also update `README.md`'s `## Fork features vs
upstream` section.** The README delta is part of the upstream-sync
deliverable, not a follow-up. Either:

- the merge commit itself includes the README edit, OR
- a follow-up commit on the same voyage and same captain branch
  updates the README before the merge lands.

### What goes in the section

- A short header: "Last upstream sync: `<merge-commit-sha>` (N upstream
  commits absorbed)" plus the date.
- **Fork-only features** subsection: bulleted list of features our fork
  has that upstream does not. Each bullet ends with the relevant fork
  commit SHA(s) for git anchor. Keep bullets to 1-3 sentences each.
- **Upstream features in-tree but not actively wired** subsection:
  lists features upstream ships that we keep in-tree but don't wire
  into our orchestration flow yet (e.g. Mux runtime, Planning Sessions
  UX), each with a one-line "why" (typically: not needed for our
  multi-runtime captain pool yet).
- Section sits in `README.md` immediately after `## Why Armada`'s
  "Who It's For" subsection, before `## Features`.

### Trigger

Any commit that:
- Merges `upstream/main` into our `main`, OR
- Cherry-picks an upstream commit, OR
- Reverts a previously-absorbed upstream feature.

### Why this matters

Without explicit fork-vs-upstream tracking in `README.md`, anyone
comparing forks loses the feature-delta context. The `README.md` is
the most-likely-read entry point; bury the delta and it's invisible.
Future maintainers (including future captains) need this to know what
to preserve through subsequent upstream merges.
