# Persona Priorities Implementation Plan

## Objective and requirements

Allow each persona to define an ordered hierarchy of captain classes. Roles do not imply a provider, model, or deployment location. Entries match configured captains by optional runtime, model, Mux endpoint, and minimum tier. Matching fields are combined; list order establishes priority.

Follow `~/Code/Agents/requirements`: strict C# style, typed contracts, relational child records across all four database providers, tenant isolation, shared Touchstone test descriptors, localized accessible editors, and synchronized REST/MCP/Postman documentation. Preserve existing behavior when priorities are empty. The user authorizes a backward-compatible minor version increment from 1.0.0 to 1.1.0. Preserve the pre-existing untracked `src/nupkg/` directory and do not modify AGENTS.md.

## Routing contract

`CaptainPriorities` is an ordered list of typed matchers on Persona. `PriorityAvailability` selects `FallThrough` (try lower priorities when earlier matches are unavailable) or `WaitForFirstPriority` (only the first priority may serve the persona). `PriorityExhaustion` selects `Wait` (default, keep pending) or `UseEligibleCaptain` (permit legacy routing after the hierarchy is exhausted). WaitForFirstPriority always waits when the first class is unavailable, including when no configured captain matches it.

Explicit mission/voyage captain overrides retain their legacy precedence. A nonempty hierarchy replaces the legacy persona DefaultCaptainId. All hierarchy candidates must satisfy persona eligibility, mission tier, tenant/owner visibility, and availability. No selection is persisted as a requested captain merely because it matched a hierarchy: each scheduling tick reevaluates availability. Existing DefaultCaptainId behavior remains when the list is empty. Fan-out and stage handoff use the same assignment path.

## Execution checklist

Annotate each item with completion evidence. Use `[ ]` pending, `[-]` in progress, `[x]` complete. Record failures and fixes in the validation log; do not mark verification complete until commands pass.

- [x] 1. Create and enter `feature/persona-priorities`. Evidence: `git switch -c feature/persona-priorities`; initial branch main, only untracked src/nupkg/.
- [x] 2. Inspect contracts, selection, migrations, client surfaces, tests, and applicable requirements. Confirm precedence and compatibility in code.
- [x] 3. Implement typed priority model, availability/exhaustion enums, validation, and pure matching/selection logic. Cover every field, combined constraints, ordering, missing/busy candidates, tier, and empty legacy lists.
- [x] 4. Add relational persistence and additive migration for SQLite, MySQL, PostgreSQL, and SQL Server. Make parent/child writes atomic; round-trip order and clear/delete entries. Test fresh and upgraded databases.
- [x] 5. Integrate selection into mission assignment and blocker explanations. Preserve explicit override precedence and legacy routing. Test pipeline stages and Architect fan-out behavior through the existing common assignment path.
- [x] 6. Expose typed fields through REST, MCP, client models, dashboard/mobile/TUI configuration surfaces. Validate malformed enums and matchers, preserve omitted fields on updates, support clearing with an empty list, and prevent cross-tenant references or selection. Update duplication behavior.
- [x] 7. Expand shared backend, API, persistence, frontend, and regression tests. Build both supported .NET frameworks, run complete backend and frontend suites, and resolve failures.
- [x] 8. Update README, Docker Hub guide, architecture/routing guides, REST_API.md, MCP_API.md, Postman collection, and testing documentation affected by this change. Increment active version sources to 1.1.0 and add changelog entry; preserve historical release references.
- [x] 9. Review the full diff for requirements compliance, unsafe fallback, tenancy leaks, documentation drift, and unintended edits. Record final passing commands and remaining environmental limitations, then deliver the completed change.

## Validation log

Completed implementation and validation on `feature/persona-priorities`. Migration 83 stores ordered child records; active release version is 1.1.0. Runtime/model/tier matchers are provider neutral; endpoint matching uses named Mux endpoints. Model and endpoint values are bounded to 1024 characters consistently in validation, schemas, and server database columns.

- `dotnet build src/Armada.sln`: both supported frameworks built with zero warnings or errors (see `/tmp/persona-build-complete.log`).
- `env -u NO_COLOR dotnet run --project src/Test.Automated --framework net10.0 -- --results /tmp/persona-tests-net10-complete.json` and the equivalent net8.0 run: each completed with 4,494 passed, zero failed, and nine conditional skips out of 4,503. Eight upgrade tests require an explicit baseline and were subsequently run successfully below; the other skip is a server-only backup test in the SQLite run.
- Final focused backend run after validation refinements: `env -u NO_COLOR dotnet run --project src/Test.Automated --framework net10.0 --no-build -- --suites Services.PersonaCaptainPriority,Services.CaptainRouting,Services.PipelineDispatch,E2E.PersonaPriorityApi,E2E.McpTool,Services.ReleaseVersion,Tui.Personas`: 233 passed, zero failed or skipped. Log: `/tmp/persona-contracts-complete.log`.
- Database parity via the existing provider runner adapted to the routing suites: 52 passed each on SQLite, PostgreSQL, MySQL, and SQL Server. Log: `/tmp/persona-db-parity-final.log`.
- `env -u NO_COLOR bash scripts/common/run-upgrade-test.sh --from-ref HEAD --providers all --framework net10.0`: eight passed on each provider, upgrading committed baseline `f49043eb` (version 1.0.0, schema 82) to the working tree (1.1.0, schema 83). Equivalent net8.0 SQLite upgrade: eight passed. Logs: `/tmp/persona-upgrade-parity-final.log`, `/tmp/persona-upgrade-net8.log`.
- Dashboard complete test suite: 751 tests across 140 files passed; production build passed. Logs: `/tmp/persona-dashboard-tests-final6.log`, `/tmp/persona-dashboard-build-final6.log`.
- Mobile complete test suite: 1,201 tests across 75 suites passed; TypeScript checks passed. Logs: `/tmp/persona-mobile-tests-complete.log`, `/tmp/persona-types-complete.log`. Jest reported its existing open-handle warning without failing tests.
- Shared/dashboard lint passed (`/tmp/persona-lint-complete.log`). xUnit routing wrapper passed; NUnit routing descriptors: 82 passed.
- Isolated Playwright verification passed for editing, save/read round-trip, first-priority waiting, desktop light theme, phone dark theme, German localization, no horizontal overflow, and no browser errors. Log: `/tmp/persona-visual-delivery.log`; screenshots: `/tmp/persona-priorities-visual/`. Temporary server/project and test-generated configuration directories were removed.

Validation corrections: disabled environment `NO_COLOR` for the existing TUI shape checks; synchronized release assertions; replaced the upgrade suite's assumption that every release adds prompt templates with checks that shipped templates and user overrides survive. No production behavior was changed to accommodate these failures.

Documentation, all three persona editors, duplication, and dispatch defaults are updated. Tracked dashboard production assets are rebuilt. AGENTS.md and the pre-existing `src/nupkg/` remain untouched. No external release publication, commit, or deployment was performed.

## Publication follow-up

The user subsequently authorized committing, pushing, and merging into main. The .NET SDK inherits version 1.1.0 and the shared Persona contract; its live-server configuration contract now verifies ordered priorities, model/endpoint values, policy preservation, and clearing. REST documentation includes SDK usage.

Final SDK verification: `env -u NO_COLOR dotnet run --project src/Test.Automated --framework net10.0 -- --suites Client.Contract.Config` passed all six live-server contracts with zero failures or skips (`/tmp/persona-sdk-final.log`).
