# Selective upstream integration review

Assessment date: 2026-09-12 (2026-09-13 UTC). This report is an assessment and plan. No implementation, merge, test run, or deployment is claimed here. Live work state belongs in Armada objectives; this report contains no operational IDs.

## Recommendation

Use selective adaptation. Do not merge upstream wholesale. The dashboard has useful gaps, but several upstream implementations are smaller than the current fork. Preserve the fork's recovery, immutable Check gates, scheduler, provider routing, coordination, process ownership and deployment controls.

Start with compatibility and provider tests, then captain quarantine controls and bounded dashboard evidence. Add ownership scope through authenticated server contracts before scoped UI. Evaluate model endpoints separately. Harbor, self-rebuild and native memory remain decision work, not enabled features.

## Integration changes after assessment

The [foundation evidence](foundation.md) now includes provider startup repairs,
unchanged migration-history checks and 29 passing isolated provider/scenario
combinations. SQLite, PostgreSQL and SQL Server run 47 ordinary cases; MySQL
runs 48, including full-value Unicode uniqueness. Combined unit/API/runtime and
dashboard checks passed. No deployment is claimed. Missing field capabilities
remain explicit in the [additional entity matrix](foundation-entities.md).

A later fetch found upstream `44d3eb5e` (Linter persona and pipeline changes).
Those 12 paths were inspected separately. The original assessment and inventory
remain pinned to `19242085`; the later addition is deferred to persona/pipeline
review and does not change this foundation repair.

The local usage-aware routing work is now committed and integrated with the
incident changes. Combined validation passed: 3,982 unit tests,
907 automated API tests, 183 runtime tests, and 77 dashboard tests. The solution
build completed with 212 warnings and no errors. Dashboard output was rebuilt.
Live provider collection, all-provider upgrade tests and a new image rollout
were not performed by this integration. These results cover this combined
change, not the remaining upstream phases.

Preserve Routing V2 contracts when integrating upstream: approved persona
account routes, explicit model and persona constraints, usage windows and
freshness, reserve thresholds, concurrent account limits, safe unknown-data
handling, and disabled-by-default configuration. Do not restore legacy
preference ordering over enabled V2 routes. See [usage routing](../USAGE_ROUTING.md)
for the landed implementation.

## Fixed evidence

| Reference | Commit |
| --- | --- |
| Fork | `21786ec0d4ca48b8174bf7fb399b3d63e66ba2b9` |
| Upstream | `19242085eed77c9d542bd29d250c8530699c5a97` |
| Merge base | `e9e3021fac0d146a035b03dbe4df61cebd3a6fdc` |

A later source change landed before this report was committed:
`fbb6e33567d43a75b9e0151b8cb44a09ba636222` prevents failure evidence at or before
an incident's mitigation time from reopening it. Its production diff and paired
tests were inspected as a delta. This strengthens the retain-fork recovery
decision. It is outside the frozen inventory and test-file counts above; no
new runtime proof is claimed by this assessment.

The fetched fork and local checkout matched. Git reports 1,187 fork-only and 324 upstream-only commits. These are ancestry counts, not missing-feature counts: earlier selective ports can have different commit IDs.

[Inventory](inventory.json) records every source/configuration path in the three comparisons and every upstream-only commit. Vendor, built dashboard and archive paths have separate counts and are not imported as features. Each inventory area is a routing label, not a claim that every line passed review. Reproduce with `git diff --name-only <base> <tip>`, `git log <fork>..<upstream>`, and `git diff <fork> <upstream>` at the commits above. Skip AppleDouble metadata files.

| Area | Upstream changes since base | Fork changes since base | Direct-tip differences |
| --- | ---: | ---: | ---: |
| Backend | 323 | 633 | 695 |
| Database and migration scripts | 126 | 164 | 174 |
| Dashboard source/config | 137 | 136 | 88 |
| Platform/runtime/CLI/build | 162 | 118 | 139 |
| Tests | 302 | 523 | 494 |
| Documentation | 40 | 39 | 47 |
| Other source/configuration | 6 | 6 | 8 |
| Built dashboard | 192 | 185 | 164 |
| Vendor tree | 6,832 | 5,413 | 5,715 |
| Archive | 4 | 11 | 15 |

The three reviewers made disjoint primary reviews of dashboard, backend, and platform. The integration review covered tests, documentation, inventory, overlap and objectives. The platform follow-up classified every remaining Other path, including Proxy and Postman. The source census is complete. Semantic review is by capability, with deeper reads of high-risk paths; it is not a line-by-line certification of every changed file. All unproved behavior has a validation gate below.

## Findings that determine the order

- Both tips already have Captain Detail **Lift Quarantine**. Neither list has manual quarantine controls. The fork MCP bench/unbench service is richer, while REST release writes state directly. Unify the service contract before adding buttons. Guard active process ownership.
- Upstream adds generic crash-loop tracking, more reset-time grammar, readable log/tool data, persisted dock anchors and richer user scope. These are candidates for adaptation, not replacement of the fork services.
- The fork dashboard fetches 200 full missions for its home view. Upstream uses 10 summaries and direct voyage vessel associations. Port the bounded contract and prove complete counts.
- Applied migration numbers conflict. Fork maxima are SQLite 82, PostgreSQL 83, MySQL 74 and SQL Server 77; upstream reaches 70. Upstream migration 70 is memory, while fork SQLite 70 is coordination leases. Replacing history can silently skip schema work. Append only after a fresh provider census.
- Upstream recovery creates standalone implementation rescues and infers mitigation from Complete status. Preserve the fork's full pipeline, source ref, incident and landing evidence.
- Upstream MCP and Harbor source contains authentication fallbacks that need correction before adoption. These are source observations, not a tested claim about any live deployment. See the exact methods in [backend.md](backend.md).
- Upstream rebuild assumes SQLite backup helpers and can continue after backup failure. Preserve the current deployment system until provider-safe backup and rollback are proved.
- An independent native memory store and automatic Recorder guidance would conflict with one external durable memory source. Evaluate compatible capture and retrieval improvements; do not enable a second authority or append recall instructions as an incidental port.

## Detailed decisions

- [Dashboard matrix and evidence](dashboard.md), with [path census](dashboard-census.tsv).
- [Backend matrix and evidence](backend.md), with [normalized source census](backend-census.csv).
- [Database, runtime, SDK, CLI, Proxy, packaging and operations](platform.md).
- [Full commit and path inventory](inventory.json).

Port means reuse an isolated compatible change. Adapt means keep the useful behavior but integrate with fork contracts. Retain means the fork already has the feature or has stronger behavior. Defer means a separate decision or proof is required. Reject means the reviewed implementation must not be imported as written. None of these labels authorizes a live action.

## Phased objectives

All work uses direct edits. No Armada mission, voyage, planning captain or rescue is dispatched for this plan. Every related objective keeps automatic dispatch disabled. Dependencies below are also stored as objective blockers. Several objectives share a phase; phase numbers describe order, not an independent work queue.

### Phase 0: Complete upstream comparison and publish the direct-edit handoff

Compare the pinned base, fork and upstream in both directions. Inventory every changed path and upstream commit. Review capabilities by dashboard, backend, database/runtime/CLI, and tests/docs. Record limitations honestly: static family review is not line-by-line verification or runtime proof.

Acceptance:

- Publish the complete path/commit census and evidence matrix with port/adapt/retain/defer/reject decisions.
- Correct the README capability comparison and stale operator instructions.
- Create and re-read every phase objective with automatic dispatch off and no voyage/mission links.
- Publish a final handoff with exact comparison commits, objective IDs, blockers, validation status and next direct-edit step.

### Phase 1: Define fork preservation gates and append-only schema integration

Before any port, create a contract matrix for preserved scheduling, objective preparation, pipeline graph, immutable Check gates, recovery, sibling lanes, coordination/wakes, provider routing, process identity, output artifacts, MCP SDK and deployment. Map entity fields through create/update/read/query on all four providers. Fork migration maxima at audit: SQLite82, PostgreSQL83, MySQL74, SQL Server77; upstream70 has conflicting meanings. Re-measure at implementation. Never replay upstream migration history.

Depends on: Complete upstream comparison and publish the direct-edit handoff.

Acceptance:

- Record each current migration version and semantic owner for all providers; existing fork migration bodies remain immutable.
- Design only append-only accepted feature migrations above actual maxima, with explicit equivalent-column guards.
- Run a fresh install, fork upgrade, repeat startup and non-default-field persistence tests against each supported provider in isolated databases.
- Record real baseline command results and test discovery; prove upstream replacement cannot delete protected fork fields or behavior.

### Phase 1: Adapt upstream test discovery and provider parity without coverage loss

Upstream moved tests to src/Test.Shared with Touchstone descriptors and xUnit/NUnit adapters; fork retains much larger regression coverage plus some shared tests. Static C# file counts at base/fork/upstream:158/596/218, not executed test totals. Port useful auto-discovery, database templates, deterministic waits and isolated parity runner only after case-level mapping. Keep existing runner until equivalent execution is proved. Reject broad warning suppressions, silent empty suite success, age-only deletion of another live run, and shared database resets across concurrent tests.

Depends on: Define fork preservation gates and append-only schema integration.

Acceptance:

- Map every existing executable case and registration to retained or migrated ownership; no regression case disappears due to folder replacement.
- Negative sentinels prove discovery, nonzero failure exit, expected filtering and named skips in every adopted adapter.
- Database tests run against explicitly provisioned SQLite/PostgreSQL/MySQL/SQLServer and cannot reset another active case.
- Measure wall time and per-suite runtime before claiming speed gains; full required fork suites pass with zero unexpected failures.

### Phase 2: Implement accepted backend persistence and evidence enrichment

Use the foundation schema/contract matrix to implement verified missing field roundtrips, bounded mission summaries with voyage vessel associations and memory-admission reasons, persisted dock git-anchor snapshots with bounded per-path history, readable log DTOs/redaction compatibility, and effective auto-land/DoD/recovery detail contracts required by dashboard. Reuse fork services, subject extraction, immutable start refs and current Check gates. Do not introduce upstream duplicate recovery, state machines or auto-land semantics.

Depends on: Define fork preservation gates and append-only schema integration; Adapt upstream test discovery and provider parity without coverage loss.

Acceptance:

- Every claimed persistence defect has a failing non-default create/update/reopen/read test before repair on each supported provider; schema additions are append-only.
- Summary counts and voyage associations remain correct beyond the first page without full-description payloads.
- Git-anchor snapshot records actual provisioning source and is bounded, stable, typed and tolerant of absent old data.
- Log, admission, auto-land and recovery DTOs expose actual fork service outcomes with shared redaction and no duplicate gate.
- Focused tests plus full relevant fork suites pass; UI contracts are documented before dashboard wiring.

### Phase 2: Unify captain quarantine controls and add dashboard actions

At the fixed assessment tip the fork had CaptainDetail Lift Quarantine and POST /api/v1/captains/{id}/unquarantine, MCP bench/unbench used a richer CaptainQuarantineService, REST unquarantine bypassed that service, and neither dashboard list had a manual Quarantine action (implementation status is tracked on the owning objective). Add reason/expiry display and explicit Quarantine/Unquarantine actions through one safe service contract. Adapt upstream crash-loop and reset parsing only for proved missing cases. Bench currently clears process/mission/dock metadata: reject busy captains or use a coordinated stop with proof; never orphan work.

Depends on: Define fork preservation gates and append-only schema integration.

Acceptance:

- UI list/detail and MCP share tenant-authorized quarantine/release semantics and consistent typed responses, including already-released state.
- Manual reason and duration/indefinite hold survive reload; quarantined captains are excluded from assignment; expiry and release work.
- Busy-captain action cannot clear ownership while a process still runs; race and cancellation cases are tested.
- Add behavioral UI/API/service tests for idle, busy, quarantined, expired, unauthorized, not-found and repeated actions; document controls.

### Phase 2: Port dashboard summaries, refresh controls and operational detail

Port only useful UI deltas: bounded dashboard mission summary and voyage vessel IDs, per-table refresh, busy-button feedback, readable log/tool chips, dock git-anchor panel, Audit/Research mode fields, failure/recovery detail, inherited-pipeline captain override, and available Ask/Planning polish. Preserve fork routes, board, notifications, code-index pages, provider secrets handling, persona constraints, settings overlays, objective preparation and dual auth headers. Use one bounded refresh path; upstream adds refresh controls alongside existing timers/events.

Depends on: Unify captain quarantine controls and add dashboard actions; Implement accepted backend persistence and evidence enrichment.

Acceptance:

- Build a route/action matrix and demonstrate each accepted capability against the fork API; no dead control or invented endpoint.
- Home avoids broad mission downloads and reports authoritative counts; refresh settings do not duplicate poll/event requests.
- Log/thinking/tool data stays distinct from final answer; git anchors and failure fields resolve to stored evidence.
- Browser verification covers list/detail/modal, keyboard, narrow viewport, empty/error/loading states and preserved fork controls; dashboard build and full Vitest pass.

### Phase 3: Adapt per-user ownership and authenticated MCP scoping

Upstream adds user-scoped data, owner-aware resources and admin view-as controls. Adapt the model to the fork official MCP SDK and all fork-only resources. Do not copy upstream fail-open MCP authentication: invalid credentials must never fall through to default administrative context. Keep applicability Scope on workflow/project profiles separate from ownership scope. Preserve local operator bridge policy until the authenticated replacement is tested.

Depends on: Define fork preservation gates and append-only schema integration; Port dashboard summaries, refresh controls and operational detail.

Acceptance:

- Explicit authorization matrix covers anonymous, invalid key, user, tenant admin and global admin across two users and two tenants.
- REST, MCP, inbox, Ask, captain chat, enumeration and all mutation routes enforce the same scope; request creation records authenticated ownership.
- User-owned and tenant-wide assets remain distinct from workflow/project applicability scope, with migration and backward compatibility proof.
- Scope-aware UI and view-as never bypass server authorization; trusted SSH discovery/calls still work without accidental default-admin escalation.
- WebSocket/chat tool-event subscriptions, all Ask aggregate intents, inherited private profile/skill references and global-admin user-only filters obey the same identity scope.

### Phase 3: Port proven runtime protocol gaps and structured log enrichment

Compare real upstream OpenCode JSONL and reasoning shapes with the fork typed parser before writing code. Preserve ModelProviders/OpenCode server injection, credentials isolation, progress/usage/final-output channels, PID identity, bounded stdin, cancellation and output digest paging. Adapt only missing event variants and log-tool metadata; do not replace richer runtime files.

Depends on: Define fork preservation gates and append-only schema integration.

Acceptance:

- Captured non-secret fixtures prove each accepted event shape failed before and passes after; test direct text, part.text, message.content, reasoning, tool start/result, errors and final output.
- All supported runtime adapters retain provider routing, environment isolation, process cleanup and final-answer delivery.
- Token totals distinguish measured runtime data from estimates or self-reports; no fabricated precision.
- Runtime suite and representative launch-free process shims pass; record any authorized live smoke requirement separately.

### Phase 4: Add safe vessel branch inspection and review optional write controls

Upstream adds vessel branch list/push/merge. Start with read-only default/current branch, ahead/behind and branch names. Write controls require fork-aware managed bare/working checkout semantics, serialization and explicit target/push intent. Never import upstream default Push after merge=true. Review held Review mission Land/Mark Complete in the same contract audit but preserve immutable Check and verified landing gates.

Depends on: Adapt per-user ownership and authenticated MCP scoping; Define fork preservation gates and append-only schema integration.

Acceptance:

- Read-only branch listing reports the correct repository/ref and handles detached/missing/error state without modifying refs.
- Before enabling writes, prove branch/ref validation, clean checkout, shared-target serialization, ancestry preservation and authorized remote destination.
- Push is explicit; user confirmation states source/target and remote; no force push or silent side effect.
- Held Review actions cannot bypass required review/Check gates or mark unlanded work complete; verify actual target ancestry.

### Phase 4: Expand SDK and Helm coverage against fork API contracts

Upstream C# ArmadaApiClient has 73 additional unique public async method names at the audit anchors. Inventory these individually; adapt accepted readiness, landing-preview, delivery, profile, skill, objective/refinement, job, usage and Ask methods to fork routes. Add grouped CLI help/serialization and JSONC-aware MCP install improvements. Preserve fork Board commands and supported Bearer/API-key authentication; do not start a second Admiral from a remote bridge.

Depends on: Adapt per-user ownership and authenticated MCP scoping; Port proven runtime protocol gaps and structured log enrichment.

Acceptance:

- Every missing SDK method has a supported route/model/auth decision and request/response contract test; no endpoint-only placeholder is exposed.
- CLI help works without starting a server; enum requests serialize correctly; settings initialization is tested.
- MCP install/remove preserves existing JSONC and is idempotent across supported clients; the configured endpoint is correct.
- SDK/CLI docs and examples match the fork; run build, contract tests and selected isolated API round trips.
- Update Postman incrementally from real fork route/body/auth contracts; retain valid fork proxy and admin examples.

### Phase 5: Evaluate and integrate model endpoints and API captains behind safe defaults

Upstream introduces model endpoint CRUD/health history, ApiAgentRuntime coding tools, cloud providers and Ask MCP tool calls. Add only after scoped ownership and runtime contracts land. Preserve the fork provider registry. Start with one local endpoint; Harbor execution is not required. The model must receive only the tools actually provided and permitted by its task.

Depends on: Adapt per-user ownership and authenticated MCP scoping; Port proven runtime protocol gaps and structured log enrichment; Expand SDK and Helm coverage against fork API contracts.

Acceptance:

- Endpoint ownership, secret masking, per-endpoint credentials, health failure and delete-in-use guards pass.
- Local API captain has bounded tool calls, filesystem boundary, cancellation, output/usage evidence and no administrative tool escalation.
- Ask/Planning describe actual tool availability and display tool results separately from assistant answers.
- Cloud provider support has explicit optional configuration and tested request translation; unverified providers remain disabled and named.

### Phase 5: Adapt optional observability and dependency updates

Review upstream Prometheus/Loki/Grafana, full exception capture, log-level changes, package updates and framework-aware scripts independently. Preserve official MCP SDK, build SHA embedding, GC/memory settings, AppleDouble exclusions, fork image ownership, temporary dashboard build output and Node-free fallback. This assessment did not verify current package advisories or live external telemetry.

Depends on: Adapt upstream test discovery and provider parity without coverage loss; Port proven runtime protocol gaps and structured log enrichment.

Acceptance:

- Each package change has a concrete reason and compatibility proof; no whole-project-file replacement or broad suppression.
- Optional metrics/log export has secret redaction, bounded retention/cardinality and measured resource cost; default deployment remains usable without it.
- Build/publish scripts preserve supported arguments, real exit status, clean checkout and configurable image source; never pull a different publisher silently.
- Build and targeted transport/native SQLite/startup tests pass for supported frameworks/platforms; docs name optional services and rollback.

### Phase 6: Decide whether detached Harbor runners justify a separate integration

Deferred architecture decision, not routine parity. Upstream adds remote host commands, process delegation, runner identity, dock affinity and reconnect. Static review finds HarborLinkEndpoint accepts nonempty access key without validation and trusts supplied tenant header; no runtime exploit test was performed. Do not enable as supplied. Current direct-edit/container operations remain authoritative.

Depends on: Evaluate and integrate model endpoints and API captains behind safe defaults.

Acceptance:

- Measure a concrete need and benefit compared with current local execution; reject if no useful deployment need exists.
- Before any pilot, design validated credential-to-tenant/user binding, command authorization, durable job identity and replay prevention.
- Isolated tests cover two runners, affinity, disconnect/reconnect, duplicate launches, output order, stop and process ownership.
- Decision records adopt/defer/reject with evidence; approved implementation receives separate direct-edit objectives and no automatic dispatch.

### Phase 6: Decide on provider-safe self-rebuild and rollback

Decision recorded in `implementation-followups.md` FOLLOWUP-018: upstream A/B slots are rejected for the PostgreSQL/container target, the hardened fork SelfDeploy is kept for process-owned hosts (disabled by default, fail-closed in containers), and container deployment stays the external image procedure. Upstream dashboard A/B rebuild and Harbor rollback do not directly fit the running PostgreSQL/container deployment. Static review finds SQLite-only backup helpers and continuation after backup failure. Do not activate the upstream rebuild action as a routine UI port.

Depends on: Decide whether detached Harbor runners justify a separate integration; Adapt optional observability and dependency updates.

Acceptance:

- Compare established SelfDeploy/container process with A/B slots for the actual target and record a value decision.
- Require verified provider-native backup/restore, migration compatibility, health cutover and rollback after injected failures.
- Rebuild must fail closed when backup or safety preconditions fail; old and new process ownership cannot overlap.
- Any approved rollout is a separate direct-edit deployment window with source/image/Helm/dashboard evidence and no unsupported system service start.

### Phase 6: Design better memory capture, retrieval and validation

Evaluate useful upstream memory features as an improvement opportunity. Keep accepted durable rules in the configured external memory source. A compatible design can use a rebuildable retrieval index with exact commit, path and section provenance. Recorder output should propose evidence-backed changes for review; it must not silently promote guesses to accepted rules or mutate bootstrap instructions.

Depends on: Complete upstream comparison and publish the direct-edit handoff.

Acceptance:

- Compare native storage and existing memory for correctness, cross-runtime use, freshness, context cost and operator effort.
- Define one authority, stable identifiers, scope, evidence, verification dates and supersession; check contradictions, duplicates and broken links.
- Test capture, reviewed change, accepted commit, index invalidation, concurrent updates, key collisions, deletion and scoped access.
- Measure selective loading against current full loading before changing policy. Do not omit mandatory rules or leak another repository's context.
- Keep operational state in objectives and incidents. Track approved implementation separately with automatic dispatch disabled.

### Integration prerequisite: Review and merge local usage-aware routing

The local feature branch includes work that must be reviewed and committed before merging. A branch reference alone does not include uncommitted work. Establish that the work is ready, preserve it, review account isolation, usage freshness, limits, fallback and explicit model choices, then integrate current main. Preserve incident lifecycle fixes and all existing routing contracts.

Acceptance:

- Review source, tests, documentation, generated assets and third-party provenance; keep secrets and private operational state out of commits.
- Run registered backend suites and dashboard tests/build on the combined tree; resolve failures before landing.
- Update affected docs, land normally to the fork main branch, and record exact source/merge commits and real checks in the objective.
- Add the new routing contracts to the preservation matrix before dependent upstream adaptation. Treat deployment separately.

### Archive work

Archive obsolete project assets with their original paths, retirement reason and replacement links. Do not archive current operating instructions merely because their original implementation plan is complete. After accepted implementation and final dispositions, move this historical review and its inventories into the repository archive, repair inbound links, and retain a current capability and operator guide. Open decisions must remain accessible through current objectives and guidance.

Superseded durable memory belongs only in its existing external memory repository archive. Update all required loaders if the active set changes; archived memory is provenance, not active instructions.

### Phase 7: Verify selective upstream integration and refresh all affected operator docs

After accepted direct-edit implementation phases, validate the combined tree and update the complete affected documentation set: README Upstream vs Fork, CHANGELOG, operator guide, REST/MCP, SDK/CLI, runtime/provider, migration/install/deploy, capability docs, Postman and existing the configured external memory source instructions where behavior changed. Each feature commit must already include its closest docs; this is the consistency and final handoff gate.

Depends on: Unify captain quarantine controls and add dashboard actions; Port dashboard summaries, refresh controls and operational detail; Adapt per-user ownership and authenticated MCP scoping; Port proven runtime protocol gaps and structured log enrichment; Expand SDK and Helm coverage against fork API contracts; Adapt upstream test discovery and provider parity without coverage loss; Evaluate and integrate model endpoints and API captains behind safe defaults; Adapt optional observability and dependency updates; Add safe vessel branch inspection and review optional write controls; Implement accepted backend persistence and evidence enrichment.

Acceptance:

- Every reviewed change family has final disposition and every deferred item has an objective or explicit final non-goal.
- Combined build, registered unit/runtime/automated tests, dashboard tests/build and supported provider upgrades pass with actual logs.
- Docs distinguish landed, deployed, deferred and disabled behavior; no stale copy of replaced workflow instructions remains.
- If rollout is authorized, prove running source/image, provider schema, Helm discovery, served dashboard hash and scheduler state; otherwise state deploy pending.
- All phase objectives carry evidence, terminal statuses match results, and automatic dispatch remains disabled throughout.
- Complete the usage-aware routing integration. Archive completed plans with repaired links and retain current operating documentation.

The existing dashboard styling repair objective is reused after dashboard integration. It owns badge, toolbar, modal, long-content, theme and narrow-viewport proof. Do not create a duplicate styling queue.

The separate recovery-pipeline, Check-assignment, admission and timing objectives remain authoritative for their existing defects. This campaign preserves those contracts and links them as related work; it does not duplicate or close them based on this audit.

## Test harness decision

The base, fork and upstream contain 158, 596 and 218 C# test files respectively. These are file counts, not executed case counts. Upstream moves tests into `src/Test.Shared` and discovers `IArmadaTestSuite` implementations through reflection. The fork still runs registered cases through its established test projects and also has some shared test infrastructure. A filename move is not evidence of missing coverage.

Adapt automatic discovery, reusable migrated SQLite templates, deterministic process waits and the four-provider runner only after a case-level map. Keep the existing fork runner until every retained case executes through the replacement. Do not import broad `NoWarn` blocks or accept zero executed cases. Upstream's shared server test database resets between cases; it must not be reused concurrently. Its age-based temporary-file sweep cannot prove that an old path has no live owner. Use process/run ownership and isolated databases.

No package advisory or latest-version recommendation was made. Package versions in the platform report are source differences. Verify current primary package documentation when implementing each upgrade.

## Final exclusions

Do not import tracked `node_modules`, generated bundles from the other source tree, archive plans, broad line-ending changes, repository-local client connection settings, upstream publisher defaults, duplicated recovery loops or weaker auth fallbacks. Rebuild reviewed dashboard source at release time. Keep the fork ignore rules, official MCP SDK, build provenance, GC settings, provider injection and test registrations.

A random Ask greeting has no required functional value for this refresh and is not selected. Harbor, native memory and A/B rebuild are not rejected as concepts; their separate decision objectives define the unblock conditions. Reassess an optional feature when there is a concrete need, not merely because upstream has it.

## Documentation and handoff contract

Update the closest docs in each implementation commit, including a capability-based README delta and CHANGELOG entry. The final consistency pass covers operator guides, REST/MCP, SDK/CLI, runtime/provider, schema/migration, install/deploy, capability documentation and Postman. Update existing external memory instructions only for changed durable procedures; never copy current objective state into memory or public source.

To resume, find the campaign titled **Evaluate and selectively adopt current Armada upstream capabilities** in Armada. Read its children and blockers. Read this report and the exact source at its anchors. Fetch both remotes, compare the new tips, and invalidate only findings whose dependencies changed. First direct-edit objective: **Define fork preservation gates and append-only schema integration**. Do not dispatch the objectives.

The assessment validates inventory and documentation, not production behavior. Builds, database upgrades, browser workflows, cloud endpoints, process launches and security transport tests remain implementation proof. A successful state-write response is not proof a control works. No service restart or deployment is part of this assessment.

## Test discovery implementation

See [test discovery and provider parity](test-discovery.md) for retained runner
ownership, activated suites, fixture repairs and evidence limits. This does not
complete the remaining capability implementation or deployment review.
