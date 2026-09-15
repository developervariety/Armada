# Armada platform comparison

Evidence anchors: fork `21786ec0d4ca48b8174bf7fb399b3d63e66ba2b9`; upstream `19242085eed77c9d542bd29d250c8530699c5a97`; base `e9e3021fac0d146a035b03dbe4df61cebd3a6fdc`.

The fork needs selected ports. A replacement of platform files would remove important fork behavior. No tracked files changed. No Armada operation or dispatch ran. This report is a temporary audit artifact, not durable memory.

## Coverage and limits

| Area | Direct tip changed files | Upstream commits since base |
|---|---:|---:|
| Core database | 168 | 37 |
| Runtimes | 48 | 27 |
| Harbor application | 10 | 10 |
| Helm CLI | 21 | 17 |
| Core C# client | 1 | 4 |
| Scripts | 46 | 12 |
| Docker | 11 | 2 |
| Migration scripts | 6 | 1 |

The 311 changed paths in this table do not overlap. Commit counts overlap. I read the commit family census, direct tip statistics, and selected behavior diffs. I did not read every changed line in these 311 files. No build or runtime tests ran. Package availability, current advisories, cloud behavior and actual database upgrades remain unverified. The integration review owns the detailed test-framework census and adaptation plan.

The follow-up checked all 8 paths classified `other` in [inventory.json](inventory.json): `.cursor/mcp.json`, `.gitattributes`, `.gitignore`, `Armada.postman_collection.json`, Proxy project and server, solution, and Directory.Build.props. The Postman check enumerated method/raw-URL pairs; it is not a full request-body or authorization review.

## Database: adapt before feature ports

Both tips declare migrations in `src/Armada.Core/Database/{provider}/Queries/TableQueries.cs`; MySQL also declares them in its database driver. Fork migration high-water marks are SQLite 82, MySQL 74, PostgreSQL 83, SQL Server 77. Declaration counts are 82, 62, 68, 62 respectively. Upstream high-water is 70 for all four providers (69, 54, 53, 52 declarations respectively). These counts are not a claim that every preceding number exists.

Numbers have different meanings. SQLite upstream 29 is planning; fork 29 is prestaged files. Upstream 70 is native memory; fork 70 is coordination leases. The runner reads MAX(version) and skips every migration at or below it. Replacing the migration list therefore silently skips upstream features on an existing fork database. Historical migrations also diverge before the common source base because prior integration used fork numbering.

Decision: ADAPT. Preserve applied history. Use an append-only range above 83 after remeasuring at implementation. Do not run upstream `migrate_v0.8.0_to_v0.9.0_*` against a fork database. Build an entity/column/create/update/read/query/provider matrix before writing new migrations. Validate fresh install, upgrade from each fork provider, repeated startup, and retained fork data. Use structural schema checks where a previous optional port can already have created a column. Do not equate a migration number with a capability.

A direct diff of `Postgresql/Implementations/MissionMethods.cs` shows upstream persists requested captain, review deadline, redispatch attempts and tier absent from the shown fork INSERT/UPDATE. Upstream also removes assignment state, stage order, prestaged files, recovery fields, start ref, retry-skip captains and aggregate queries. Decision: ADAPT the accepted fields; RETAIN the fork behavior. Round-trip non-default values after reopening connections, under each tenant/user scope. Investigate all four providers, not PostgreSQL alone.

Upstream DB fixes `d50b82195` and `e2cf39b18` are partly converged already. Fork MySQL centralizes native timestamp reads in `ReadUtc` and `ReadUtcNullable`; upstream inlines equivalent native DateTime conversion. Fork and upstream SQL Server captain deletion clear sender/recipient signal references, and fork has an additional scoped overload. Decision: RETAIN. Prove fractional seconds and UTC under a non-UTC process timezone and test signal-linked deletion under every scope. Do not claim these are new missing fixes.

Upstream `scripts/common/run-db-parity-tests.sh` and OS wrappers are absent in the fork. Decision: ADAPT. It uses temporary Docker provider instances and upstream's Database suite. The integration review owns adjustment to fork test projects and suite registration. Keep all fork regression tests, named skips, failure exits and cleanup. No blind test-framework migration. Prove every requested provider ran; do not accept a green result with missing providers.

Quarantine storage is already shared: SQLite fork migration 55 and upstream 53 add `quarantine_until_utc` and `quarantine_reason`. Decision: RETAIN storage and ADAPT UI/API wiring. A missing button does not justify a second schema. Prove write, reload, exclusion from selection, expiry and manual release.

New upstream ownership scopes for endpoints, playbooks, skills, personas, pipelines, templates, workflow/project profiles span commits `f1c35d788`, `97eaf3ec8`, `c0d17f0bc`, `1975bfeea`, `4ad347308`, `6b4f06b43`. Decision: ADAPT with API authorization work. Prove same-tenant cross-user visibility and mutation, cross-tenant denial, and compatible fork resource ownership. This is a backend enrichment, not just UI labels.

## SDK, CLI and API examples: adapt

`src/Armada.Core/Client/ArmadaApiClient.cs` has 51 unique public async method names in fork and 124 upstream: 73 added, zero removed. Moved methods are excluded from that count. Added families include readiness/landing previews; environments/deployments; workflow/project profiles; skills; releases; checks; objectives/refinement; jobs; token usage; Ask. Map each to fork routes, request/response models and authorization. Use fake HTTP request-contract tests and selected real API round trips. Update SDK documentation in the same phase.

CLI families `43dc251d1`, `f4bf55d2b`, `004b9fb6f`, `9e82d9cdc`, `9b3a1d9ad` add grouped help, voyage help correction, enum serialization and MCP install detection/JSONC support. Decision: ADAPT. `McpConfigHelper.cs` prefers existing OpenCode JSONC and handles Mux's separate configuration. Verify help without server startup, enum requests, existing JSONC preservation, idempotent install/remove and URL correctness. Preserve fork Board commands. Do not activate Mux merely because installation support exists.

`Helm/Commands/BaseCommand.cs` replaces fork Bearer authorization with upstream X-Api-Key. Decision: RETAIN the fork authentication contract. Adapt useful settings reload after embedded startup only where compatible. Prove authenticated remote operation without starting a second Admiral.

`Armada.postman_collection.json`: fork 245 unique method/raw-URL pairs; upstream 308; 99 added and 36 absent upstream. Added examples cover accepted and deferred new features. Of removed pairs, 33 concern proxy routes and three concern credential/tenant/user enumeration. Decision: ADAPT incrementally against actual fork routes. Do not replace the collection or treat removal as evidence the route was removed from the server. Retain/correct valid fork proxy and admin examples. Update request bodies and auth with each accepted backend capability. Deferred endpoints must not be presented as live fork capability.

## Runtime behavior: retain strengths; port proven gaps

`AgentRuntimeFactory.cs` upstream removes fork `OpenCodeServerSettings` and `ModelProvidersSettings` injection while adding endpoint resolution. Upstream replacements of `BaseAgentRuntime.cs`, Claude, Codex, Cursor and OpenCode remove much fork provider routing, structured log conversion, progress/token events, environment handling and exit records. `RuntimeLogNoiseFilter.cs` and `StructuredRuntimeLogFormatter.cs` are fork-only. Decision: RETAIN. Extend constructors/factory for accepted new providers without dropping existing injection.

OpenCode top-level provider failures are typed and rendered as bounded named activity. Response
bodies, headers, URLs, and session metadata remain excluded from mission output. Chat, planning,
and refinement consumers classify that marker as a failed turn, so an error-only stream cannot
be accepted as a successful empty answer.

OpenCode upstream protocol families `8239c073e`, `9b3a1d9ad`, `9e82d9cdc` recognize type/part/eventType, extract direct text/part.text/message.content, and keep reasoning out of reply text. Fork has a larger typed parser and provider integration. Decision: ADAPT only after a fixture proves a missing shape. Keep fork typed JSON rules; upstream uses direct JsonElement. Test stdout/stderr, thinking, errors, final answer, token accounting, credential isolation, long stdin prompt, and cancellation for every supported runtime.

## Larger capabilities: separate deferred phases

Harbor families `215ddfb2f`, `ef18d8753`, `314d3d482`, `7473dbf21`, `9d8707ac9`, `837971e0a`, `a4c193973` add detached host runners, process executor seams, remote runtime, jobs, affinity, link and output mirroring. `src/Armada.Harbor` is wholly absent in fork. Decision: DEFER to a bounded architecture phase. Dependencies: DB routing/affinity, authorization, capability discovery, process and lease identity, reconnect, cancellation, duplicate launch protection. Prove embedded mode, two runners, disconnect/reconnect, log streaming and stop before changing the current deployment.

API captain/tool families `b27968875`, `22bc0947c`, `175fdbe3e`, `13e976720`, cloud endpoint family `65630266a`, chat MCP tools `2202a3fd1` add `ApiAgentRuntime.cs`, `Tools/*`, `Mcp/McpToolClient.cs` and model endpoint DB methods. Decision: DEFER to a separate acceptance phase. Keep the fork provider registry. Dependencies: endpoint ownership, credentials, deletion guard, tool/path bounds, cancellation and token accounting. Prove one controlled local endpoint before remote Harbor use.

Native memory/Recorder `09cf86bae`, four-provider `MemoryMethods.cs`, upstream migration 70: DEFER and keep disabled. Current workspace sole-memory policy must remain intact. Do not turn this into a second active durable memory store during parity work. The design objective now evaluates compatible capture and retrieval improvements with one Git authority, rebuildable indexes, and reviewed Recorder proposals. Activation still requires the documented validation and deliberate policy changes.

A/B rebuild and rollback `2ce122d30`: DEFER. Depends on Harbor, artifact selection, health cutover and persistent schema compatibility. Prove failed build, failed health, rollback and restart recovery before adding operational use.

Observability `766d75e92`, `aadc72c44`: ADAPT as optional services. Docker includes Prometheus/Loki/Grafana configuration. Prove useful measurements, retention, redaction and resource use; do not change the default deployment implicitly.

## Build and operations: adapt, preserve fork controls

Upstream updates SyslogLogging 2.2.1 to 2.2.2, Watson 7.0.15 to 7.1.1, SQLite native package shape; adds PolyPrompt, Voltaic and Harbor Avalonia dependencies. Decision: ADAPT by actual use, with restore/build on supported target frameworks and native SQLite startup across supported architectures. Current advisories were not checked in this read-only source audit.

`Armada.Server.csproj` upstream removes official MCP SDK, fork GC/memory settings, internal-test visibility and EmbedGitSha. `Directory.Build.props` removes AppleDouble exclusion. Decision: REJECT wholesale replacement; RETAIN official MCP transport, memory controls, build provenance and regression seams. A transport change is a separate architecture task.

`src/Armada.sln` upstream removes fork Common/Unit/Runtimes/Database test projects and reassigns upstream Shared/Xunit/Nunit/Automated GUIDs, then adds Harbor. Decision: RETAIN existing project registrations and fork regression coverage; ADAPT only additions required by accepted work. Do not accept a smaller suite as successful convergence. The integration review owns test harness details.

`.gitignore` upstream omits node_modules, AppleDouble, DS_Store and bin-* rules. `.gitattributes` upstream only pins shell LF; fork pins broad text LF and binary types. Decision: RETAIN fork files. The upstream vendor tree is not an intentional feature port. Do not import tracked node_modules. Keep dependency manifests and lockfile choices explicit.

Fork `deploy-dashboard.sh` builds into a temporary output directory; upstream dirties tracked dist. Both support prebuilt fallback without Node. Decision: RETAIN fork output handling and fallback. ADAPT framework selection in publish scripts without dropping forwarded dashboard arguments. Prove a clean checkout, correct artifact deployment and error propagation. Preserve fork test runners, watchdogs, SSH bridge, autonomy and bootstrap scripts.

Upstream build-server adds pulls from the upstream publisher after push. Decision: ADAPT configurable fork image ownership; REJECT fixed publisher behavior. No build/push/deploy script ran during this audit.

`.cursor/mcp.json` is a new repository-scoped localhost MCP connection. Decision: DEFER/REJECT automatic import. It is local client setup, not a backend capability, and can conflict with the operator/captain connection boundary. Keep installation guidance/configuration explicit and prove the actual server/port/transport before use.

## Proxy follow-up

Only two Proxy files differ: project dependency and `ArmadaProxyServer.cs`. Direct server diff is 32 lines. Upstream removes needless async from StartAsync and synchronous JSON route handlers, returns Task.CompletedTask/Task.FromResult, and changes successful browser login logging from Info to Debug. HTTP/WebSocket forwarding and session behavior are not enriched by this diff.

Decision: ADAPT low-priority warning cleanup and logging if consistent with the fork logging policy. Keep observable API behavior. The Watson update belongs in dependency compatibility work, not a standalone forced Proxy replacement. Prove challenge/login/logout, selected instance context, relay HTTP and WebSocket, error handling, plus repeated StartAsync. This source comparison provides no runtime proof.

## Suggested phase objectives and acceptance

These are area-review suggestions. The final order and dependencies are owned by
[the integrated phased plan](README.md#phased-objectives), which supersedes this
section where the ordering differs.

1. Foundation: refresh anchors; entity/provider schema matrix; append-only migration plan; preserve fork project and regression census. Evidence: fixed-tip inventory and failed-before fixture for each claimed persistence defect.
2. Provider tests and backend contracts: adapt provider matrix runner; fix accepted persistence/ownership gaps. Evidence: fresh/upgrade/reopen/tenant tests on all four providers, named skips and no missing providers.
3. Dashboard integration: quarantine and other accepted controls against fork contracts. Evidence: browser action, state reload and backend behavior, not button presence alone.
4. SDK/CLI/Postman: adapt supported route coverage, help, installation and examples; retain fork auth and Board. Evidence: request contracts, authenticated calls, clean local config updates.
5. Runtime protocol enrichment: retain fork provider/telemetry behavior; port only proven missing cases. Evidence: real protocol fixtures and stop/token/output behavior.
6. Optional endpoint/API captain capabilities: keep deferred until bounded design and proof exist. No automatic runtime activation.
7. Optional Harbor/observability/A-B capabilities: prove compatibility and rollback separately before deployment.
8. Documentation and handoff: update capability-based README, migration guide, runtime/provider docs, SDK/CLI, install/deploy, API collection and operator procedure with each landed phase. Clearly distinguish available, disabled and deferred capabilities. Preserve the sole memory source. Record direct-edit-only execution and no-dispatch objectives.

The final acceptance gate tests the combined tree. An upstream commit labelled fork parity does not prove its implementation improves the current fork. Recheck the concrete premise before each implementation objective.
