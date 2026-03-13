## Project Context
Armada is a multi-agent orchestration system that scales human developers with AI. It coordinates AI coding agents ("captains") to work on tasks ("missions") across git repositories ("vessels"). Written in C# (.NET), it exposes MCP tools for fleet, vessel, captain, mission, voyage, dock, signal, and merge queue management.

IMPORTANT — Context Conservation: When interacting with Armada MCP tools, ALWAYS prefer armada_enumerate over armada_list_* tools. The armada_enumerate tool supports pagination (pageNumber, pageSize), filtering (by vesselId, fleetId, captainId, voyageId, status, date ranges), and sorting. The armada_list_* tools return ALL records at once and can exhaust context windows with large result sets. Use armada_enumerate with a small pageSize (10-25) to conserve context.

## Code Style
For C#: no var, no tuples, using statements instead of declarations, using statements inside the namespace blocks, XML documentation, public things named LikeThis, private things named _LikeThis, one entity per file, null check on set where appropriate and value-clamping to reasonable ranges where appropriate

# Mission Instructions

You are an Armada captain executing a mission. Follow these instructions carefully.

## Mission
- **Title:** Add missing merge queue MCP tools: delete and purge
- **ID:** msn_mmodt5yk_3G3El3YyMEK

## Description
The merge queue is missing public API/MCP tools for cleanup. Missions have `armada_purge_mission`, voyages have `armada_purge_voyage`, but there are NO equivalent tools for merge queue entries. The internal `MergeQueueService.DeleteAsync()` method exists but is not exposed.

## What to implement

### 1. `armada_delete_merge` MCP tool
- Deletes a single merge queue entry by ID
- Only allows deletion of terminal entries (Landed, Failed, Cancelled)
- Calls the existing `MergeQueueService.DeleteAsync()` method
- Parameter: `entryId` (string, required, mrg_ prefix)
- Follow the exact pattern of `armada_purge_mission` for implementation

### 2. `armada_purge_merge_queue` MCP tool  
- Bulk purge of all terminal merge queue entries (Landed, Failed, Cancelled)
- Optional `vesselId` filter to purge only entries for a specific vessel
- Optional `status` filter (e.g. only purge "Failed" entries)
- Returns count of entries deleted
- Implementation: query all terminal entries matching filters, call DeleteAsync for each

### Key files to modify

1. **`src/Armada.Server/Mcp/McpToolRegistrar.cs`** — Register the two new MCP tools following the existing pattern (look at how `armada_purge_mission` and `armada_purge_voyage` are registered)

2. **`src/Armada.Server/Mcp/McpToolHandler.cs`** (or wherever tool handlers live) — Add handler methods for the two new tools, calling into MergeQueueService

3. **`src/Armada.Core/Services/MergeQueueService.cs`** — May need a new `PurgeAllAsync()` or `DeleteAllTerminalAsync(string vesselId = null, string status = null)` method for the bulk purge. The existing `DeleteAsync` handles single entries.

4. **`src/Armada.Core/Services/Interfaces/IMergeQueueService.cs`** — Add interface method if new service method is added

5. **`MCP.md`** — Update the MCP documentation to include the two new tools with their parameters, descriptions, and examples. Follow the existing documentation format for other tools.

### Implementation guidance

- Study how `armada_purge_mission` is implemented end-to-end (registration → handler → service call) and replicate the exact same pattern
- Study how `armada_cancel_merge` is implemented since it's the closest existing merge queue MCP tool
- The `DeleteAsync` method already handles git branch cleanup (local + remote), so leverage it
- Ensure proper error handling: return clear error if entry not found or not in terminal state
- Follow the project's style guide: no var, XML docs, PascalCase public, _PascalCase private

## Repository
- **Name:** Armada
- **Branch:** armada/claude-code-1/msn_mmodt5yk_3G3El3YyMEK
- **Default Branch:** main

## Rules
- Work only within this worktree directory
- Commit all changes to the current branch
- Commit and push your changes — the Admiral will also push if needed
- If you encounter a blocking issue, commit what you have and exit
- Exit with code 0 on success

## Progress Signals (Optional)
You can report progress to the Admiral by printing these lines to stdout:
- `[ARMADA:PROGRESS] 50` — report completion percentage (0-100)
- `[ARMADA:STATUS] Testing` — transition mission to Testing status
- `[ARMADA:STATUS] Review` — transition mission to Review status
- `[ARMADA:MESSAGE] your message here` — send a progress message

## Existing Project Instructions

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
