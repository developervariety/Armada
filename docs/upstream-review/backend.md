# Backend comparison and port plan

This is a source review. It is not an implementation or a live security test.

Fixed source points:

- Fork: `21786ec0d4ca48b8174bf7fb399b3d63e66ba2b9`.
- Upstream: `19242085eed77c9d542bd29d250c8530699c5a97`.
- Shared base: `e9e3021fac0d146a035b03dbe4df61cebd3a6fdc`.

Every path below is relative to the repository. `U` means the upstream tip. `F` means the fork tip. Line numbers refer to those fixed tips, not the next merged tree. The complete normalized file census is in [backend-census.csv](backend-census.csv). No tracked source or live Armada state was changed by this agent.

## Result

Use selected ports and adaptations. A full replacement of Core or Server would remove required fork behavior. Upstream has copied several fork concepts, but the names do not mean that the implementations have the same depth.

The clear additions are owner-scoped configuration and MCP caller identity, richer chat and runtime log presentation, a log exporter, branch controls, crash-loop handling, git-anchor context, managed model endpoints, Harbor, and server rebuild slots. Some need major controls before they are safe to enable.

The fork already has the backend for unquarantine. Its quarantine service is richer than upstream. The dashboard should use that service. Do not replace it with the upstream direct state writes.

## Coverage and method

I read the upstream commit families, compared the direct tips, made a complete path census, and traced selected production call paths. Database implementations are assigned to another agent.

The census covers every `.cs` file under Core and Server, except `Database/`:

| Direct-tip class | Files |
| --- | ---: |
| Equal after line-ending normalization | 321 |
| Present on both tips, different content | 194 |
| Upstream only | 105 |
| Fork only | 367 |
| Union | 987 |

Core services: 223 fork files and 132 upstream files; 38 upstream-only, 129 fork-only, 52 different shared files, and 42 equal shared files. Server: 198 fork files and 156 upstream files; 18 upstream-only, 60 fork-only, 92 different shared files, and 46 equal shared files.

The upstream change set from the base contains 323 non-database Core/Server paths: 320 C# files, two project files, and the server Dockerfile. This is a complete path census. It is not a claim that all 194 different bodies received a line-by-line review. The matrix covers the production capability families. Runtime adapters, database migrations, tests, SDK clients, package compatibility, and dashboard details need the companion reports.

## Capability matrix

| Capability | Decision | Evidence and reason | Required preservation and proof |
| --- | --- | --- | --- |
| Manual quarantine and release | Adapt dashboard; retain backend | F `Services/CaptainQuarantineService.cs:35,60,84,96,135,189` supports timed and indefinite bench, clear, expiry, and optional probe restore. F `Server/Routes/CaptainRoutes.cs:390` already registers unquarantine. F `Mcp/Tools/McpCaptainTools.cs:262` registers bench/unbench when service is supplied. U route `CaptainRoutes.cs:279` writes Idle and clears fields directly. | Use one service for REST, MCP, and dashboard. Keep indefinite holds. Reject or safely stop active assignments before manual bench. Test reason/expiry persistence, restart, release, active-work handling, and authorization. |
| Automatic crash-loop quarantine | Adapt | U `Services/AdmiralService.cs:810-821,861-916` counts crash timestamps within a configured window and quarantines after a threshold. F has provider failure handling and quota probes but no equivalent generic crash timestamp window in Admiral. | Add to the fork service path. Keep quota, credit/auth, provider-wide spend-cap and safeguard rerouting. Do not count clean cancellation or DoD/test failures as runtime crash loops. Test threshold boundary, successful run reset policy, cooldown, and repeated health/exit notifications. |
| Provider reset parsing | Adapt selected grammar | U `Services/ProviderResetParser.cs:34-91` adds numeric Retry-After, relative durations, and ISO reset cues, with a 24-hour bound. F `Services/ProviderQuotaLimitDetector.cs:157-280` already supports epoch, clock and dated provider messages plus runtime-specific fallback windows. | Add missing formats to the fork parser. Do not replace it or apply the upstream 24-hour rejection to all existing provider windows. Test every old format plus Retry-After, relative times, timezone offsets, overflow, past dates, and invalid text. |
| Caller identity and per-user data scope | Adapt, high priority | U `Models/EnumerationScope.cs:43,91` and `Models/ScopedVisibility.cs:23,41,57` centralize enumeration, view/edit, and create scope. U routes use these for Category A/B entities. F has older tenant handling and no `ScopedVisibility` type. | Bring the policy to the fork's official MCP SDK transport. Attribute creates to caller identity. Preserve operator-only control tools and coordination participant identity. Test two users in one tenant, another tenant, tenant admin, global admin, direct IDs, enumerate filters, nested references, edits and deletes. |
| Upstream MCP authentication implementation | Reject as written | U `Server/ArmadaServer.cs:508-545` begins with `IsAuthenticated=true`. Invalid credentials or auth-resolution exceptions do not set denial. U `Mcp/Tools/McpToolHelpers.cs:110-143` gives a no-claims call default tenant-admin context. These are source observations, not exploit-tested claims. | A supplied invalid credential must fail. Define anonymous/local compatibility explicitly. Do not let auth errors become admin authority. Use behavioral transport tests for absent, bad, expired, valid, and cross-tenant credentials. |
| Ask/inbox visibility | Adapt and complete | U `Services/InboxService.cs:61` takes `AuthContext` and filters missions, captains, merges and deployments. U `Services/AskArmadaService.cs:55,157,169,229` filters mission/failure answers. F inbox has richer operator categories, including fork coordination/recovery state. | Keep all fork inbox categories. Note that other U Ask branches still call status/captain/voyage paths without the auth argument; a complete scope review is required. Test every Ask intent, inbox category, status aggregate, and event delivery. |
| Ask Armada and planning chat enrichment | Adapt | U `Server/CaptainChatService.cs:82` scopes captain reads; `:160` separates API tool cards; `:337` handles OpenCode JSONL and thinking; `:423` mints a caller token for API-runtime MCP access. U `PlanningSessionCoordinator.cs:214` adds stream/thinking controls. F already has captain chat, turn metrics and cancellation. | Keep fork coordination, timeouts, resource gates, and dispatch service. Port useful presentation and parser behavior without changing operator tool authority. Test actual runtime fixtures, stop then next turn, errors, no raw protocol text in reply, per-turn tool IDs, scoped event subscribers, and token expiry. API runtime dependency comes later. |
| Readable runtime logs and redaction | Adapt, high priority | U `Services/RuntimeLogFormatter.cs:58-145` reads runtime-specific Claude/Codex/OpenCode events. It delegates redaction to new `SecretRedactor`. F formatter has public `RedactSecrets` used by other output paths. | Centralize redaction with a compatibility entry point. Keep protection on persisted artifacts and operator output, not just UI. Translate upstream raw JsonElement parsing to repository typed models when editing. Test runtime fixtures, malformed events, text/tool/thinking separation, truncation, credentials in tool names/results, and all current redaction callers. |
| Telemetry log export | Adapt | Both tips already have `ArmadaTelemetryHost`, `ArmadaMetrics`, token usage entities/capture, and summary routes. U `Server/ArmadaTelemetryHost.cs:93-183` enables Loki/OTLP logs and bridges SyslogLogging, while F exports metrics/traces only. | Preserve disabled-by-default behavior and redaction. U forwards raw message/exception values; apply the fork data boundary before export. Add a counted forwarding failure path instead of the upstream empty catch. Verify one entry reaches an isolated sink, no recursion, no double subscription after restart/dispose, and errors retain stack traces. |
| Token accounting | Retain; no new core port | `Services/TokenUsageCapture.cs` is identical after line-ending normalization at both tips. `TokenUsageSummaryBuilder` differs only in small logging/format details. The upstream feature is not absent. | Keep real/estimated flags and compatibility parsing. API runtime token capture must join the same path. Do not create another store or report this whole feature as a gap. |
| Git anchors in mission context | Adapt | F `GitAnchorsFormatter.cs:25` already prints start/target/branch and recent commits. U `:26-63` adds subject terms. U `MissionService.cs:1429-1452` persists the snapshot in `Dock.GitAnchorsJson`. F has separate subject extraction and stage-base rules. | Reuse fork subject extraction, immutable start ref and `StageBaseVerifier`. Add bounded per-path history and UI snapshot only where missing. Test repository-relative paths, missing refs, stable snapshots, size bounds, and a path with no recent changes. |
| Model endpoints and API captains | Defer to an isolated, gated adaptation | U `Services/ModelEndpointService.cs`, `ModelEndpointClientFactory.cs`, `Models/ModelEndpoint.cs`, endpoint routes/MCP tools, and `AgentLifecycleHandler.cs:159` add managed endpoint identity, health and remote configuration. F has provider resolution and OpenCode/DeepSeek clients, but not this endpoint registry. | Avoid a second conflicting provider configuration source. First define mapping to fork model provider settings, role/tier policy, credentials, health, and capability profiles. Require endpoint-delete guards, redacted list/read results, per-endpoint credentials, provider-aware health cache keys, and all supported provider contract tests. Coordinate with runtime and schema agents. |
| Harbor remote execution | Defer; reject current auth | U has protocol, connection manager/router, transport, host command/process seams and routes. `AgentLifecycleHandler.cs:931-973` prefers Harbor and falls back to local. `MissionService.cs:346-354` checks the requesting user's Harbor policy. F has no Harbor implementation. | Stage as opt-in after a security design and real deployment need. Preserve local launch behavior, dock/sibling leases, git affinity, stop identity, resource admission and failure evidence. Test reconnect, late output, duplicate exits, lost link, stuck jobs, wrong tenant/user, host affinity, fail-closed required-Harbor mode, and no local fallback after an eligibility race. |
| Harbor handshake authorization | Reject as written | U `Server/HarborLinkEndpoint.cs:128-148` tests only that `x-access-key` is non-empty, accepts tenant identity from `x-tenant-guid`, and resolves user ID as null. The comment says full credential validation is a follow-up. Source observation only. | Validate credentials and bind both tenant and user to authenticated identity before routing or executing any remote command. A non-empty arbitrary header must not grant access. Cover invalid keys, spoofed tenant headers, identity mismatch, token revocation, and reconnect. |
| Server rebuild slots | Defer; retain fork SelfDeploy | U `ServerRebuildService.cs:175-204,256,333-345` adds slots and Harbor health rollback. It uses `McpToolHelpers.PerformBackupAsync/PerformRestoreAsync`, which are SQLite-specific. U `:517-520` continues after backup failure. F `Services/SelfDeployService.cs`, build runner and supervisor support the fork deployment path. | Do not enable a competing deploy mechanism for the current service. Require provider-specific backup/restore and tested rollback before use; keep dispatch hold/drain semantics, deployed commit verification, data migration rollback rules, and fork process ownership. Rollback after new writes needs an explicit data-loss policy. |
| Vessel branch list/push/merge | Port read view; adapt mutations | U `Server/Routes/VesselRoutes.cs:324-445` adds branch controls and `:77` WorkingDirectory fallback. U `Services/GitService.cs:628,693,711` implements list, direct push and direct merge. F has branch inventory and guarded merge/landing services. | Route merge through fork landing/check/protected-path controls. Do not expose direct `git merge` as an alternate gate bypass. Keep mission branch push policy. Validate ref names, target existence, worktree leases, dirty state, remote choice and access. Test conflict, moving target, blocked branch, and branch list fallback. |
| Shared dispatch validation and captain overrides | Adapt small useful gaps; retain fork dispatcher | U `AdmiralService.cs:679-715` validates objective, pipeline, vessel and mission shape. F `Server/VoyageDispatchService.cs:90-202,579` already centralizes preconditions and dispatch, with objective links, capacity, code-context policy and duplicate dispatch handling. | Do not replace with the smaller U validation helper. Verify overrides are persisted before first assignment for every pipeline mode, and rejected when owner/role/model constraints fail. REST, MCP, planning and autonomous dispatch must use one contract. |
| Model-tier selection | Retain fork policy | U `CaptainTierSelector.cs:22-85` has enum tiers and regex family fallback. F shares most of this helper, but actual fork policy is `PreferredModelTierSelector.cs` plus Admiral routing: configured logical tiers, persona restrictions, provider preference and ranked/unranked selection. | Do not switch launch selection to upstream first-in-list enum selection. Preserve configured logical tier semantics, random choice among eligible peers, frontier Judge policy, specialist restrictions and explicit captain override validation. Test the real dispatch path. |
| Mission lifecycle and interrupted recovery | Retain and reconcile narrow fixes | Both have central `MissionStateMachine`; F `:37-45` also has `WaitingForInput`. U `AdmiralService.cs:1666` requeues interrupted exits. F has deeper quota/safeguard rerouting, process identity and completion handling. | Prove interruption and duplicate exit behavior using fork lifecycle tests. Keep WaitingForInput, handoff boundaries, retry budgets, actual process identity and output-after-exit handling. Do not import U `McpToolHelpers.IsValidTransition`, which duplicates and omits state-machine cases. |
| In-dock Definition of Done | Retain fork implementation | U `DefinitionOfDoneGate.cs:27` uses a process-local semaphore and `DefinitionOfDoneClassifier.cs:23` classifies by start/timeout/exit only. F `DefinitionOfDoneGate.cs:159,198,215` has host-wide serialization, containerless test resolution and declared consumer builds, plus evidence-aware classification. | Keep command resolution, consumer checks, timeout process-tree kill, no-work handling, and real Check evidence. A U interface extraction may help later, but is not a behavior upgrade. Tests must cover restore infra errors, actual test failures, stale checks, no tests run, absent container runtime, and producer/consumer build break. |
| Judge contracts and report-only modes | Retain fork, avoid format regression | U new `JudgeContract.cs:42,122,152` centralizes a three-lens output contract; U `MissionService.cs:3322` calls it. F `MissionService.cs:1713-1799,5781-5908` also requires authoritative, current-commit Checks and handles unresolved states. F has rotating Judge lens resolution and richer Research/Audit handling. | Do not let heading/length checks replace Check evidence. Preserve mode propagation, report-only vs committed-doc distinction, reviewer no-op acceptance and rescue substance checks. Any pure helper extraction must keep fork contract behavior. |
| Autonomous mission recovery | Retain fork; reject wholesale U replacement | U `MissionRecoveryCoordinator.cs:205-224` creates a fresh standalone Worker Implementation mission, no voyage, and `:99-119` treats any rescue Complete as landed, then closes Mitigated next cycle. F uses `Server/AutonomousRecoveryOrchestrator.cs`, `IncidentLifecycleOrchestrator.cs`, runbooks, full persona pipelines and armed checks. | Keep preserved start refs, full pipeline, rescue budget/effectiveness, actual landing evidence, cancellation, objective links and incident closeout. Do not attach U recovery as a second maintenance loop. Test one incident/one rescue, dispatch hold behavior, stale complete, failed landing, and report-only rescue. |
| Auto-land policy | Retain fork, adapt UI/dry-run only | U `AutoLandPredicate.cs:30` allows landing when policy is absent/disabled and uses total changed lines. F `AutoLandEvaluator.cs:20-28` uses disabled fail and added-line limits; `Server/MissionLandingHandler.cs:663-703` applies the policy with calibration. These are not identical contracts. | Expose the existing predicate and dry-run through consistent REST/MCP/UI. Define migration of limits explicitly; do not silently change added-lines to total-lines. Preserve calibration and prior Judge/Check gates. Test disabled, empty, deletes, rename, protected paths, glob boundaries and threshold equality. |
| Dock boundary and stage handoff | Retain fork | U has scanner and stage-lag hardening, but F `DockBoundaryScanner.cs` is 301 lines vs U 168, F `DockService.cs` is 2276 vs U 605, and F has `StageBaseVerifier`, `SiblingLeaseRegistry`, `ProtectedPathsValidator` and branch preservation. Size alone is not proof; traced fork DoD/landing/handoff callers depend on these contracts. | Preserve source commit ancestry, reused sibling leases, protected paths, report-only behavior and recovered work. Compare branch-advance cases against U tests before porting any isolated fix. Test fan-out vs continuation, missing predecessor, leased sibling and failed reclaim. |
| Merge queue reliability and process supervision | Retain shared and fork guards | Both contain `ProcessSupervisor` with identical normalized text. U merge lease/operation timeout families are already represented in fork; F queue adds guard/recovery/branch behavior. | Do not port the feature name again. Preserve durable lease, timeouts, moving-target checks, branch recovery and no-op classification. Verify actual merged target builds, not only source branch. |
| Background jobs | Retain shared store; reconcile surfaces | Both tips have identical normalized `Services/JobService.cs` and `JobStateMachine.cs`. F also has `Server/LongRunningJobService.cs:22,49` for accepted/running MCP work. | Avoid replacing fork long-running result APIs with the simpler shared jobs table. Document each job type. Test timeout, terminal pruning, duplicate submission and restart semantics where a surface is changed. |
| Skills and project profiles | Retain core; adapt ownership surface | Both have identical normalized `Services/ProjectProfileService.cs`. U added ownership in models, storage and routes. F already has project-profile resolution and skills. | Add scope consistently across lookup, list, create, update, prompt resolution and inherited references; do not count the whole feature as missing. Verify private references cannot enter another user's prompt. |
| Native memory and Recorder | Defer; do not activate | U `Services/MemoryService.cs`, memory routes/tools and Recorder persona add an independent store. U `PromptTemplateService.cs:166-245` automatically appends recall guidance to built-in working templates on startup. F has its own reflection controls, disabled operationally; owner memory policy requires one durable source. | Keep current memory source and disabled learning/reflection policy. Do not seed Recorder, append recall sections, or start an independent memory store as an incidental port. The accepted design objective now evaluates Git-authoritative capture and indexed retrieval with exact provenance, reviewed Recorder proposals, and measured context loading. This is an improvement opportunity, not a permanent feature rejection. |
| Operational control, coordination and autonomy | Retain shared fork additions; retire standalone lead | F-only `CoordinationService`, `AutonomousObjectiveScheduler`, `LeadCycleCoordinator`, `AgentWakeProcessHost`, dispatch holds, inbox stale-peer support, operational asset audit, production verification and unlanded-branch tools have no U equivalent. | The later owner-approved retirement removes `LeadCycleCoordinator` and dedicated Grok/lead tools; do not restore them. Preserve shared tool registration and route/event wiring. Add regressions at real entry points when a common server file changes. Keep participant headers, wakes, scheduler persistence, policy opt-outs and direct-edit authority. |

In table paths, `Services/` and `Models/` mean `src/Armada.Core/`; `Server/` means `src/Armada.Server/`; bare `Mcp/` means `src/Armada.Server/Mcp/`.

## Upstream commit-family map

These anchors help reproduce the family review. They are review references, not text for the README capability section.

| Family | Representative commits | Treatment |
| --- | --- | --- |
| Reliability schema/process/dock/liveness/merge | `830de78e5`, `b5f1605c1`, `9cb384d64`, `d070933ef`, `c5709f41a`, `34f6d2d6d`, `786b0ec02`, `ba619b13a`, `59a1e2a5e`, `14ce83e86`, `f34cc713e`, `4ddf7ee98` | Already represented or superseded; preserve fork contracts. |
| Shared interactive features | `265cd0a6e`, `907713338`, `1b844fdfd`, `102594095`, `2d88ba9f4`, `9094124d2`, `ce2c94138`, `41b4ad6fb` | Mostly present; review scope and UI additions. |
| First fork-parity round | `9a6aeb546`, `22503368c`, `9c1d44d79`, `1cfd021dc`, `a1e53357b`, `7e375bb32`, `23bc9b848`, `8f4edf69d`, `56291e61a`, `e8d9ac2c2`, `0edb8bc43`, `b6e6364ca`, `5aff57305` | Do not confuse upstream adoption with a missing fork feature. |
| Later parity completion | `b55edd7fb`, `17eca2992`, `302e3886f`, `45b0b0ca2`, `167b972c0`, `d67671617`, `580036ee0`, `8efae5ef6`, `aa2d99c33`, `19863e006`, `db7cec34e`, `9e82d9cdc`, `9b3a1d9ad`, `8239c073e`, `61825f81a` | Select crash/log/anchor additions; retain fork recovery/gates/routing. Runtime agent owns adapter details. |
| Captain map and dispatch override | `1bb425852`, `47525280b`, `299ca2cec` | Verify override-before-assignment and map to fork dispatcher. |
| Token usage | `0de94a066`, `379c0c0df`, `882968081` | Core capture is already the same. UI differences are separate. |
| Model endpoints and API runtimes | `fdcd4dbd3`, `175fdbe3e`, `22bc0947c`, `b27968875`, `e8eecb66d`, `b1e81f25a`, `65630266a` | Optional adaptation after scope/provider foundation. |
| Harbor | `215ddfb2f` through `3bf18519a`, plus `d7cb804a1`, `72aa88cf0`, `837971e0a`, `13e976720`, `6d0bd1367`, `24eddaac7`, `a4c193973`, `94589a845` | New subsystem; auth and execution-policy blockers first. |
| Scope | `fad22f45f`, `65080fe62`, `7a3d45d46`, `5996f7c51`, `f1c35d788`, `97eaf3ec8`, `c0d17f0bc`, `1975bfeea`, `4ad347308`, `1b54c8ec0`, `6b4f06b43`, `a1b177cbe` | Worth adapting; source has incomplete/unsafe edges. |
| Telemetry and logging | `766d75e92`, `2740c06a5`, `aadc72c44`, `8b280d5bf` | Existing metrics/traces; add guarded log export. |
| Native memory | `09cf86bae`, `71b92d4e4`, `486e3ad64` | Defer under current policy; block automatic template mutation. |
| Rebuild | `2ce122d30`, `19522de37`, `b489a25cb` | Defer; preserve existing fork deployment and provider safety. |
| Ask MCP/tools/chat refinements | `08bd62ba4`, `2202a3fd1`, `1349817fd`, `6c28eb125`, `a74b6ea94`, `b4fe2946e`, `d87fb4bac` | Adapt useful chat evidence and scoped identity after API runtime gate. |
| Branch management / review landing | `1a65f4f39`, `2b2e561f5`, `d027a93c4` | Branch read worth porting; mutations must use fork gates. Test held Review without bypass. |
| Package/test/nav/docs housekeeping | Dependency bumps, warning fixes, test moves/harness, dashboard consolidation, final docs and file cleanup | Other agents own detailed census. Do not infer production gaps from moved filenames. |

## Suggested phase groups

These are area-review suggestions. The final order and dependencies are owned by
[the integrated phased plan](README.md#phased-objectives), which supersedes this
section where the ordering differs.

1. **Freeze the comparison and define contracts.** Store the full census and decisions in product docs. Record fixed source points in the audit report. Build a dependency list and preservation checklist from the matrix. Add objectives with auto-dispatch disabled. No new Armada mission or voyage.
2. **Fix identity and ownership first.** Adopt explicit caller context and configuration scope. Keep the official MCP SDK. Deny bad credentials. Complete Ask/inbox/event visibility. Coordinate the migration with the database agent.
3. **Deliver visible controls on current services.** Add quarantine/release UI, safe branch read, existing background-job status, runtime log chips, readable tool output and git-anchor display. Route all control actions through the fork services. This phase does not need Harbor or native memory.
4. **Add narrow backend enrichment.** Add generic crash-loop quarantine, missing reset grammar, richer runtime/chat parsers and guarded log export. Prove failure paths as well as happy paths.
5. **Assess optional architecture.** Model endpoints/API captains can proceed only with provider, tier, scope, credential and runtime tests. Harbor and rebuild remain blocked on their listed design/proof requirements. Native memory/Recorder remains deferred under current policy.
6. **Consolidate and hand off.** Update README Upstream vs Fork by capability, CHANGELOG, REST/MCP docs, operational guide, settings reference, dashboard guide and tests guide in the same commits as each port. Update current project rules when paths/test commands change. Update existing durable memory only for changed durable procedures; keep objective IDs and current work state out of the source repository.

## Required acceptance proof

- Each selected behavior gets a before/after reproduction or a test that fails on the fixed fork tip and passes after the adaptation.
- Add to existing suites where they own the behavior. Do not add source-text tests that merely pin an implementation spelling.
- Run the fork's actual build and registered test harness. The upstream test move must not silently cause zero execution. Record skipped or unavailable integration dependencies by name.
- For ownership, test both REST and MCP plus event delivery with separate users and tenants. Test invalid credentials as denial.
- For quarantine, prove that no new assignment goes to a held captain and that a valid release makes it eligible again; a successful write response alone is not proof.
- For delivery changes, verify the target branch contains the expected commit and the actual merged target passes gates. A Complete status or a green source branch is insufficient.
- Use isolated local fixtures for branch, transport, crash and telemetry tests. Do not dispatch production work to test these ports.

## Limits and residual questions

No service was started, no exploit was attempted, no runtime was launched, and no build/test suite ran in this read-only review. All auth findings are source observations. I did not test downstream middleware that might add controls before the shown endpoints. That must be checked before labeling a deployed instance vulnerable.

The file census is exhaustive within the stated path scope. The semantic review is family-based, with deep reads of high-risk call paths. It does not prove every hunk is safe. Before implementation, the owning objective must recheck its exact method changes against both fixed tips and the then-current fork tip.

Open questions include: identity propagation on WebSocket broadcast and Harbor upgrade; cross-user prompt/profile references; global-admin user-only filtering (`EnumerationScope.Resolve` ignores a user-only filter without tenant); required-Harbor eligibility races; provider-specific endpoint credentials; scoped chat events; native-memory key uniqueness; behavior of backups on each provider; and package/runtime compatibility. These require targeted tests, not a broad merge.

## Backend storage implementation

See [backend metadata persistence](backend-storage.md) for verified field losses,
new additive migrations and preserved behavior. Storage does not activate routing
or scanner policy, and does not complete the remaining backend enrichment.

See [scoped voyage mission summaries](backend-summaries.md) for the paged vessel
association and status-count contract. Dashboard wiring remains separate.

## Dock snapshot implementation

See [backend-anchors.md](backend-anchors.md) for the source contract and current
validation status. Backend enrichment remains in progress.

## Runtime log implementation

See [backend-logs.md](backend-logs.md) for typed response fields, shared redaction,
validation and remaining consumer work.

## Admission refusal repair

A regression case showed that a custom resource-pressure policy returning
`Admit=false` with an empty reason continued to captain selection. Assignment now
uses the boolean result and supplies a fallback explanation. The global workload
limit still runs first; the pressure probe, OOM cooldown, fleet capacity and
sibling-lane policies retain their existing order and limits. This initial repair did
not persist admission measurements; the implementation below now adds them.

Validation: the new regression failed before the repair (22 passed, 1 failed).
After repair, all 44 assignment, resource-pressure, fleet-capacity and sibling-lane
cases passed with zero failures or skips. No schema change or deployment occurred.

The pressure policy now captures a typed reason, evaluation time, active count,
configured limits and OOM deadline in its decision. Older custom implementations
retain Unknown and nullable evidence. A regression failed before capture; 45
focused assignment and capacity cases then passed. At that stage, the fields were not yet
persisted. The historical PostgreSQL repair and restored-image validation then
passed; the admission implementation below is now validated.

## Persisted admission implementation

The [recorded admission contract](backend-admission.md) defines historical evidence,
conditional writes and read behavior. This supersedes the earlier pending
persistence notes above. Twenty migration scenarios passed, and final ordinary
provider totals are 67/67/68/67 for SQLite/PostgreSQL/MySQL/SQL Server. The combined
tree passed 4,112 unit, 967 API and 183 runtime tests with no failures or skips.
No deployment is included. Effective landing, DoD and recovery projections remain
open.

## Effective landing configuration

See [effective landing configuration](backend-landing.md) for shared resolution,
scoped voyage overrides and preview limits. DoD, auto-land and recovery outcomes
remain open; this read projection does not replace execution gates.
