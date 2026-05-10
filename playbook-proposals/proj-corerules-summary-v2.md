---
name: proj-corerules-summary
description: project CORE RULES 1-22 distilled -- captains can't read project/CLAUDE.md from inside dock; this carries the rule set
type: vessel
---

# project CORE RULES -- distilled reference

A captain can't read `project/CLAUDE.md` from inside a dock -- only the
target vessel's `CLAUDE.md`. This playbook distils the project-wide
**CORE RULES** for missions that touch any vessel under
`project/`. Rule numbers match `project/CLAUDE.md`.

## Headline non-negotiables (read these first)

1. **Tests are required.** Every new public type, service, endpoint,
   or handler gets a test in the parallel `*.Tests` project. Bug
   fixes get a regression test.
2. **No mocking libraries.** Hand-rolled doubles only
   (`RecordingHttpHandler`, `ConstantVehicleDataSource`, etc.).
   `NullLogger<T>.Instance` (otrbuddy/j1939mitm/jpro/otr) or
   `new LoggingModule()` (armada) for loggers.
3. **Algorithm math lives in `j1939mitm` only.** Per-OEM primitives
   are pure static functions in `J1939Mitm.Core/<Manufacturer>/`.
   Signers and command handlers are thin adapters (pack seed -> call
   static signer -> unpack key). Two copies of the same algorithm = bug.
4. **Structured logging only.** `{Foo} {Bar}` placeholders, NOT
   `$"..."` interpolation in `LogX(...)`. (Exception: armada's
   `LoggingModule` doesn't support placeholders -- concat with `+`
   instead. See vessel-armada-codestyle.)
5. **Never log secrets.** Tokens (PASETO, Bearer, session),
   signatures, shared secrets, API keys, passwords, RSA private
   exponents, full seed/key byte sequences. At `Information` level:
   no request/response payloads.
6. **Tenant isolation (multi-tenancy).** Every entity with a
   `FleetId` field gets a `HasQueryFilter(x => BypassTenantFilter ||
   x.FleetId == CurrentFleetId)` registered in the same commit as
   the `DbSet`. (otrbuddy primarily.)
7. **UDS 0x34 (RequestDownload / reflash) stays guarded.** No
   service byte `0x34` in any j1939mitm code path, agent command
   handler, or future `IDiagnosticSession` implementation.
   Reflashing a truck ECU can brick hardware.
8. **J1939 sentinels are "not available", not zero.** `0xFF`
   (8-bit), `0xFFFF` (16-bit), `0xFFFFFFFF` (32-bit). Check before
   trusting; preserve prior value, don't overwrite with zero.
9. **Never hand-edit generated or embedded files.**
   `output/jpro-export/`, `output/otr-export/`, `otrperformance/`
   JADX dump, embedded workflow JSON in otrbuddy. **Fix the
   extractor upstream and re-run.** EF migrations may be edited
   only to fix idempotency issues; document the edit.
10. *(Retired as numbering slot -- removed-feature policy moved to
    repo-local `CLAUDE.md` files. Repo-local rules own their
    removed-feature lists.)*
11. **Read the target repo's `CLAUDE.md`** before making changes.
    Repo-specific rules win on conflict.
12. **Do not reference plan / spec / roadmap docs in code,
    commits, or tracked specs.** Inline the *why*, not "see plan S3"
    / "per the Phase 4 spec" / "tracked in TODO.md". They rot
    independently.

    **Concrete: your commit message subject must NOT carry the
    in-flight sub-project tag of the dispatched mission.** Your
    mission may be titled `SP-B1 M2 -- vehicle commands endpoint with
    last-run join`, but the commit message subject the captain pushes
    must describe the work in conventional-commit form WITHOUT the
    sub-project prefix.

    | Don't (rots -- subject names a roadmap doc) | Do (stands on its own) |
    |---|---|
    | `feat(commands): SP-B1 M1 -- IBundleCatalogue.Commands projection` | `feat(commands): IBundleCatalogue.Commands projection` |
    | `feat(reports): SP-C Phase 1 -- redirect endpoints from FaultCodeEntity to v2` | `feat(reports): redirect endpoints from FaultCodeEntity to v2` |
    | `chore(security): SP-E Phase 0 -- delete AllisonExternalExe orphan` | `chore(security): delete AllisonExternalExe orphan` |
    | `// IParametersView (SP-A V1 M4)` | `// IParametersView -- no-op stubs (this fake exercises diagnostic-text paths only)` |

    Same for code comments -- describe the WHY, never name a
    sub-project tag. The work itself is the durable artifact; the
    sub-project label is in-flight planning vocabulary.

13. **Planning artifacts under `project/docs/superpowers/` or
    `project/TODO.md`.** Never inside a repo subdir (the only
    exception is each repo's own `CLAUDE.md`).
14. **Update planning records when work lands.** TODO close-out
    happens in the same session as the landing.
15. **All code changes go through Armada.** Orchestrator does NOT
    Edit/Write tracked code in repo subdirs. Bug fixes, features,
    refactors flow through `armada_dispatch`. (You are the captain
    -- this rule binds the orchestrator, not you.)
16. **Verify before dispatching.** Search code and git history before
    sending work; TODO entries may already be shipped.
17. **Captains never edit `CLAUDE.md`.** Vessels carry
    `ProtectedPaths = ["**/CLAUDE.md"]`; commits to CLAUDE.md are
    rejected with a coaching message. Surface rule proposals as a
    `[CLAUDE.MD-PROPOSAL]` block in your final report:

    ```
    [CLAUDE.MD-PROPOSAL]
    File: <repo>/CLAUDE.md (or project/CLAUDE.md)
    Section: <heading>
    Change: add | update | remove
    Rationale: <one-line why>
    Proposed text:
    ---
    <the rule text>
    ---
    ```

18. **Shipped features document themselves.** Update the closest
    `CLAUDE.md` when workflow rules change. For tools/settings/source-data
    references, update the narrow stable doc that owns that surface
    (`docs/armada-ops.md`, `PORTING-FROM-SOURCES.md`, or the extraction
    inventories). Do not maintain a broad duplicate capability inventory.
19. **Bias to action when `CLAUDE.md` maps cleanly.** Reserve
    `STATUS` blockers for security, schema, irreversible, product, or
    genuinely novel ambiguity. Don't park on a question the rules already
    answer.
20. **Dispatch the whole plan, not phase-by-phase.** When one
    vessel-level alias graph can cover the full plan, dispatch it all
    at once. Do not split later phases into separate dispatches if they
    are known at design time.
21. **`STATUS.md` is the orchestrator-to-user inbox during AFK.**
    Read it first when resuming. Record blockers there only when user
    input is genuinely required; don't park routine progress notes there.
22. **LF line endings on all tracked text files.** Keep text files LF.
    If a file has mixed endings, normalize in a separate chore commit
    before semantic edits.

*(Placeholder: a "review-during-drain" rule will be added here when
Reflections v1 lands. It will govern how captains incorporate
deep-review audit feedback into landing decisions.)*

## Quick test-design rules

- One test = one behaviour. No `_Setup`/`_Teardown` shared state.
- Test name: `{Behavior}_{Condition}_{Expected}`.
- File location varies per vessel -- see the vessel-specific
  `vessel-<name>-tests` playbook attached to your mission.
- DO NOT test: simple DTOs, EF migrations themselves, Razor markup,
  third-party library behaviour.

## Quick logging cheat-sheet

| Vessel | Logger | Style |
|---|---|---|
| armada | `LoggingModule` (SyslogLogging) | `_Logging.Info(_Header + "op " + value)` (concat, NOT `$"..."`) |
| otrbuddy | `ILogger<T>` | `_log.LogInformation("op {Foo} {Bar}", foo, bar)` (placeholders) |
| j1939mitm | `ILogger<T>` (rare; mostly pure static) | `_log.LogDebug("op {Foo}", foo)` |
| jpro | `ILogger<T>` | `_log.LogInformation("op {Foo}", foo)` |
| otrperformance | `ILogger<T>` | `_log.LogInformation("op {Foo}", foo)` |

Never `$"..."` interpolation in `ILogger<T>.LogX(...)` calls.

## Final-report contract

Every mission ends its `AgentOutput` with:

```
[ARMADA:RESULT] COMPLETE
<one to three paragraphs summarising what shipped>
```

`[ARMADA:RESULT] BLOCKED` is also acceptable when the mission can't
land -- explain *why* and what you tried.
