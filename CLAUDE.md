# Armada — repository rules

Durable per-repo rules only: project context, code style, build and test
commands, architecture, and the upstream sync protocol. Per-mission briefs and
shipped-feature changelogs do NOT belong here — the admiral pays for this file
on every dispatch. Cross-cutting `project/` rules live in the
`proj-corerules-summary` playbook, which is auto-attached on this vessel.

## Project

Multi-agent orchestration system that scales human developers with AI. C#/.NET.
It coordinates AI coding agents ("captains") to work on tasks ("missions")
across git repositories ("vessels"). It exposes MCP tools for fleet, vessel,
captain, mission, voyage, dock, signal, and merge-queue management.

**Armada MCP includes operator-control tools.** Supported captains receive a
launch-configured connection. The endpoint has no per-captain authorization
boundary. Captains may use only the tools authorized by their mission.
Dispatch, administration, deployment, restore, purge and server control remain
operator actions unless explicitly assigned. As an operator, prefer `armada_enumerate` with a small `pageSize` (10-25),
narrow with filters (`vesselId`, `status`, date ranges), and set the include
flags (`includeDescription`, `includeContext`, `includeTestOutput`,
`includePayload`, `includeMessage`) only when you need that data.

## Durable memory

`AI-Memory` is the sole durable memory source for every runtime. On this server
it is at `/srv/armada/AI-Memory`, mounted read-only.

Read `shared/` in full plus `repos/armada/`. The index is a map and holds no
rules, so reading it alone tells you nothing. Skip the other repositories'
folders and the host notes unless your work touches them.
`AI-Memory/imports/` and `AI-Memory/archived/` are provenance only; never read
them.

## ProtectedPaths

This vessel registers `ProtectedPaths = ["**/CLAUDE.md"]`. Captains cannot
modify this file — the merge gate rejects commits that touch it. To propose a
rule change, emit a `[CLAUDE.MD-PROPOSAL]` block in your final report; the
orchestrator applies it directly and pushes a normal commit.

## Build

```bash
dotnet build src/Armada.sln
```

## Test

```bash
dotnet run --project test/Armada.Test.Automated/Test.Automated.csproj --framework net10.0
dotnet run --project test/Armada.Test.Unit/Test.Unit.csproj --framework net10.0
dotnet run --project test/Armada.Test.Runtimes/Armada.Test.Runtimes.csproj --framework net10.0
dotnet run --project src/Test.Automated/Test.Automated.csproj --framework net10.0
```

Armada's own tests run through `dotnet run --project`, not `dotnet test`. Every
other vessel uses `dotnet test`; using the wrong one here reads as a broken test
setup.

The four suites are independent processes and share no state, so all four can
run at once. `scripts/{linux,macos}/run-tests.sh` does that and prints a
combined result; pass `unit`, `automated`, `runtimes`, or `shared` to run just one. It
also unsets `ANTHROPIC_*` for the child, because `ClaudeCodeProviderRoutingTests`
asserts on the environment a captain process would inherit and fails when the
caller exports those variables.

Sharded and concurrent runs are the default. The script splits the unit runner
into N shard processes (`--shard i/N`, N = min(cores/2, 6); override with
`--shards N` before the suite name or `ARMADA_TEST_UNIT_SHARDS`), sums their
totals, and fails when a shard crashes, prints no summary, or the shard suite
counts do not add up to the registered suites. Extra arguments after the suite
name run that one runner unsharded:
`scripts/macos/run-tests.sh unit --suite "Git Service"`. `--list-suites` prints
the suite names a run (or one shard) would execute.

Shards are balanced by `test/Armada.Test.Unit/shard-weights.json`; regenerate it
with `scripts/common/generate-shard-weights.py` from unit logs. A suite that
touches process-global or machine-wide state, or asserts a wall-clock bound, is
listed with its reason in `test/Armada.Test.Unit/serial-suites.json` and always
runs on shard 1. A new suite of that kind goes in that list in the same change.
A test that waits on a real timeout, interval or retry backoff takes the delay
through an injectable parameter (constructor `TimeSpan`, `TimeProvider`, or delay
function) with the production default unchanged, rather than waiting it out.

**A test runs only when `RunTest` is called for it.** `TestSuite` has no
reflection-based discovery, so a `public async Task` method that is never
registered in `RunTestsAsync` never executes, never fails, and never appears in
the totals. After adding tests, confirm the suite total moved by the number you
added.

## Architecture

- `Armada.Core` — domain models, database interfaces, service interfaces, settings
- `Armada.Runtimes` — agent runtime adapters (Claude Code, Codex, extensible via `IAgentRuntime`)
- `Armada.Server` — admiral process: REST API (SwiftStack), MCP server (official MCP C# SDK), WebSocket, web dashboard
- `Armada.Helm` — CLI (Spectre.Console), thin HTTP client to the Admiral

## Key concepts

Admiral = coordinator process. Captain = worker agent. Fleet = collection of
repositories. Vessel = single git repository. Mission = atomic work unit.
Voyage = batch of related missions. Dock = git worktree for a captain.
Signal = message between admiral and captains. Coordination board = shared chatroom (dashboard `/chatroom`, `armada_coordination_*` tools) where operator sessions claim work and captains post `[ARMADA:NOTE]` lines. Campaign = opt-in parent/lane/program objective tree for one large effort.

## Coding standards

### Language restrictions

- **No `var`** — always explicit types (`List<Fleet> fleets = ...`).
- **No tuples** — define a class or use `out` parameters.
- **No direct `JsonElement` access** — deserialize into a strongly-typed class
  rather than calling `GetProperty()` / `GetString()`.
- **`using` statements, not declarations** — the block form, not `using var x =`.
- **XML documentation** on all public members.

### Naming

- Public members: `LikeThis`. Private fields: `_PascalCase` (`_Database`).
- Async methods: `Async` suffix, with `CancellationToken token = default`.
- Enums: PascalCase with an `Enum` suffix, decorated with
  `[JsonConverter(typeof(JsonStringEnumConverter))]`.
- ID prefixes: `flt_`, `vsl_`, `cpt_`, `msn_`, `vyg_`, `dck_`, `sig_`, `art_`.

### File organization

- One entity per file; the filename matches the class name.
- `#region` blocks: Public-Members, Private-Members, Constructors-and-Factories,
  Public-Methods, Private-Methods.
- `using` statements go **inside** the `namespace` block. Order: System, then
  third-party, then project namespaces.

### Patterns

- Constructor injection with null checks: `?? throw new ArgumentNullException(nameof(x))`.
- Null-check on set where appropriate, and clamp values to reasonable ranges.
- Use `.ConfigureAwait(false)` in library code (Core, Runtimes).
- Logging: SyslogLogging with `private string _Header = "[ClassName] ";`.
- Database: interface-per-entity (`IFleetMethods`, `IVesselMethods`).
- Settings: nested config objects with validation in setters.

### Libraries (use these, they are mine)

SwiftStack (REST API), ModelContextProtocol.AspNetCore (MCP Streamable HTTP
server), SyslogLogging (logging), PrettyId (ID generation with prefixes).

## Upstream sync protocol

This is a fork of `jchristn/Armada`. Upstream is remote `upstream`; our fork is
`origin`. The fork accumulates orchestration features that are not upstream
(PR-fallback flow, recovery pipeline, audit queue, cross-vessel deps,
captain-lifecycle hardening).

### Sync the easy way: cherry-pick the delta, never merge the whole

The fork is a divergent superset — roughly 1500 commits ahead of upstream, with a
few hundred new upstream commits since the last sync. A full `git merge
upstream/main` produces thousands of conflicts and takes days; do not attempt it.
Sync incrementally instead — review only what is new upstream and take the pieces
you want:

1. `git fetch upstream`.
2. List only the NEW upstream commits since the baseline below:
   `git log --oneline <baseline-upstream-sha>..upstream/main`.
3. For each change you want, `git cherry-pick <sha>` (add `-n` to stage and
   review before committing). Skip anything the fork already has or supersedes.
4. Resolve any small conflict, build (`dotnet build src/Armada.sln`), run the
   unit suite, and land through the normal flow.
5. Move the baseline SHA below forward and update the README `## Upstream vs
   Fork` section by capability (see the rule below).

**Sync baseline:** fork `ae0431ad1` against upstream `d92e1dce6`. Each sync starts
from this line, so a pass only reviews the new upstream delta, never the whole
history. This is the record that used to live in `docs/upstream-review/`.

**Branch retention:** keep `origin/fix/memory-dashboard-oom` until upstream
merges or explicitly rejects the upstream memory/OOM PR. Do not delete it during
cleanup just because the fork has already absorbed the fixes; we previously lost
a Cursor-related bug branch by cleaning it up before the upstream disposition
was settled.

**Any commit that merges `upstream/main`, cherry-picks an upstream commit, or
reverts a previously-absorbed upstream feature MUST also update the
`## Upstream vs Fork` section of `README.md`.** The README delta is part of the
deliverable, not a follow-up: either the merge commit includes the edit, or a
follow-up commit on the same voyage and branch makes it before the merge lands.

**Describe the delta by CAPABILITY, never by commit SHA.** A hash tells a reader
nothing about what the system does, and it rots: the commit it names moves behind
a merge, a rebase, or a rewrite, and then the line is both meaningless and wrong.
Anyone who wants the commits can ask git, which is authoritative — the merge base
is `git merge-base origin/main upstream/main`. The README's job is to say what the
fork DOES that upstream does not, in prose a maintainer can act on.

So: group the fork's additions by subsystem, one to three sentences each, in terms
of behaviour. Where upstream ships a feature we keep in-tree but do not wire in
(for example the Mux runtime), say so with a one-line reason. Say which features
have since landed upstream too, so they are not mistaken for fork-only differences.
The section sits immediately after `## Why Armada`'s "Who It's For" subsection.

Without this, anyone comparing forks loses the feature delta, and the README is
the most-likely-read entry point. Future maintainers need it to know what to
preserve through later upstream merges.
