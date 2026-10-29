# Changelog

Net changes since the latest full upstream merge on 2026-05-24
([merge baseline](https://github.com/developervariety/Armada/commit/cb9030bd5cbaca71efe738b53807f048afd05cd7)).
This compares the merged tree with the current fork. It includes later selected
upstream integrations and excludes changes already present at that baseline.

## Unreleased

- Routing preview accepts capability hints and shows the ranking inputs used by the Legacy selector.

### Report completion

- A complete report can retain an obligation for actual pending downstream review roles without cancelling them, with or without a stated count. The gate verifies the owned dependency chain, associates numbered statements with explicit pending-duty evidence, and still blocks unfinished work, unavailable reviewers and mismatched counts.
### Attempt provenance

- Each captain process launch now records a row in the new `mission_attempt_provenance` table: the captain, persona, runtime, model, runtime version, process id, start and end times, and the outcome (`Running`, `Completed`, `Failed` or `Interrupted`) with an exit code and a reason code. A relaunch closes any row of the mission still running as `Interrupted` with reason `superseded_by_relaunch`. The attempt facts stay the record of what Armada did next; they hold none of these fields.
- The runtime version is the first line of the executable's `--version` output. It is probed once per executable, cached for 30 minutes (5 minutes when empty), and a launch waits at most 3 seconds for it. No model digest is recorded, and a Harbor-routed attempt records no version.
- `GET /api/v1/missions/{id}/attempts` and the MCP tool `armada_mission_attempts` list the attempts of a mission through one reader that resolves the mission through the caller's scope first. A mission credential reads only its own mission over REST, and the tool is not offered to mission callers.
- Attempt rows are deleted with their mission by every mission delete and by the data-retention sweep.
- Migrations: SQLite 133, PostgreSQL 137, MySQL 126, SQL Server 129.

### Lean and Compact persona prompts

- A project profile's persona override accepts `promptVariant` (`Current`, `Lean` or `Compact`). The Worker, Test Engineer, Judge and Architect personas have a Lean body (one demand per line, output contract first and last) and a Compact body, both written for this fork and both keeping the personas' output formats and Armada tool rules. A named prompt template wins over a variant; `Current` is the default. The dashboard profile editor sets the variant. See [Personas](docs/PERSONAS.md#lean-and-compact-prompt-variants).

### Rework prompt layout

- A mission sent back for rework, by a review denial or by an autonomous rescue, receives its reviewer findings as a numbered list with one demand per finding. The demands, the fixed rework rules and the output contract (`[ARMADA:RESULT] COMPLETE` with a per-finding summary) come last. A review denial whose comment holds no structured findings carries the comment whole as finding 1. See [Pipelines](docs/PIPELINES.md).
- A findings list that does not fit its bound drops whole lower-priority findings and states how many it dropped. It no longer cuts a finding short. A Judge review comment longer than the stored limit keeps whole blocking findings instead of a head cut.

### Evaluation voyages and missions

- A voyage and a mission have `isEvaluation`, default false. REST, MCP, WebSocket and the .NET models set it on create. A mission created for an evaluation voyage inherits the flag, and a mission derived from an evaluation mission copies it. The production summary leaves that work out. See [POST /api/v1/voyages](docs/REST_API.md#post-apiv1voyages) and [Captain Writes](docs/MCP_API.md#captain-writes).

### Captain model probe

- `POST /api/v1/captains/{id}/probe` and `armada_probe_captain_model` send one tiny prompt through the captain's existing model validation and report success, latency and a short error. The probe creates no mission. Only a global administrator may run it, and the same captain waits 30 seconds between probes. See [POST /api/v1/captains/{id}/probe](docs/REST_API.md#post-apiv1captainsidprobe).

### Ask tool-call cards after a reload

- A saved Ask turn keeps the tool calls the captain made, including the calls made before a failed turn. Reopening or reloading a conversation shows the cards again, and a conversation reopened mid-turn shows the calls made so far. See [saved conversations](docs/ASK_CONVERSATIONS.md#tool-call-cards-unread-replies-and-browser-state).

### Ask unread replies, drafts and list width

- Each saved Ask thread counts the replies that finished since its owner last opened it. `POST /api/v1/ask/threads/{id}/read` and `/unread` clear and set the count, and the Ask sidebar entry shows how many conversations hold unread replies. The Dashboard also keeps one unsent draft per conversation, reopens the conversation last open, and lets the operator drag the conversation list to a new width. Schema migrations 135 (SQLite), 139 (PostgreSQL), 128 (MySQL) and 131 (SQL Server) add the `unread_count` column.

### Ask composer prompt history

- In the Ask message box, ArrowUp with the caret at the start of the text recalls earlier sent prompts, and ArrowDown with the caret at the end walks forward to the draft. History is per browser and holds 100 prompts.

### Ask work-card links

- Work cards in an Ask conversation link to the mission or voyage page, and the parent and rescue mission ids link to their missions.

### Ask recap

- The default `ask.system` prompt asks for a one or two sentence recap of finished and remaining work at the end of a multi-step answer. A built-in row that still holds the earlier default is upgraded; an edited row is kept.

### Saved Ask conversations over MCP

- `armada_list_ask_threads` and `armada_get_ask_thread` read the authenticated human caller's saved conversations. They are read-only: no MCP tool sends, approves, rejects, changes or deletes. See [Saved Ask Conversations](docs/MCP_API.md#saved-ask-conversations).

### Route authorization census

- Every registered REST route outside the anonymous allow list rejects a caller with no credentials (HTTP 401). The allow list is the health check, login, tenant lookup, self-registration, and the dashboard shell. The OpenAPI document and Swagger UI use the same authorization decision as other routes, so a caller sends a credential to read them.
- The API Explorer's **OpenAPI JSON** button saves the document the page loaded with the session credential. The Explorer has no Swagger UI link, because Swagger UI cannot send the credential.

### Toolchain suggestion

- `GET /api/v1/vessels/{id}/toolchain-suggestion` and `armada_suggest_vessel_toolchain` read marker files in a vessel working directory and return suggested build, test and lint commands for .NET, Node, Python, Go and Rust. The read does not create or change a workflow profile or a vessel command. See [GET /api/v1/vessels/{id}/toolchain-suggestion](docs/REST_API.md#get-apiv1vesselsidtoolchain-suggestion).

### Abbreviated unit-test command

- A workflow profile stores an optional `abbreviatedUnitTestCommand` beside `unitTestCommand`. Checks and the definition-of-done gate keep running `unitTestCommand`. See [Workflow Profiles](docs/OPERATIONAL_ASSETS.md#5-workflow-profiles).

### Mission captain tier

- `armada_create_mission` accepts an optional `tier` (`Economy`, `Standard` or `Premium`). Only captains at or above that tier are assigned. Omitting it keeps the default routing. An unrecognized value is rejected and creates nothing. See [Captain Writes](docs/MCP_API.md#captain-writes).

### Captain model validation

- A captain create or update that fails model validation returns a reason code with the existing message. REST puts the reason on `Data`. MCP and WebSocket return it beside the message. Status codes are unchanged. See [POST /api/v1/captains](docs/REST_API.md#post-apiv1captains).

### Hosted cloud providers

- An API captain's provider is hosted unless it is operator-hosted (`Ollama` or `OpenAICompatible`). A provider this build does not name needs `apiCaptainCloudProviders` before an API captain can use it.

### Setup wizard

- When the setup mission fails or its landing fails, the wizard shows the failure reason and a link to the mission.

### Dashboard

- A refresh spins only its icon. The workflow-profile checkboxes use one row each. Mission lists and mission detail show the linked voyage title, and fall back to the voyage id. The Memory page shows the vessel name, and falls back to the vessel id.

### Test waits

- Shared test waits that poll until a deadline use a monotonic clock, so a wall-clock jump does not shorten the wait. The budgets are unchanged.

### Container base images

- The Dashboard image builds on `node:22.23.4-alpine3.24` and serves from `nginx:1.31.8-alpine3.24`. The proxy image uses the same Node tag for its dashboard build, and its default .NET images are `mcr.microsoft.com/dotnet/sdk:10.0.403-noble` and `mcr.microsoft.com/dotnet/runtime:10.0.14-noble`. A proxy build argument still overrides a pin. The default runtime stage stays root, and the dashboard image has no non-root stage. See [Pinning base images](docs/DOCKER.md#pinning-base-images).

### Dependency patches

- Patch updates only: `Microsoft.Data.SqlClient` 7.0.3, `Microsoft.Data.Sqlite` 10.0.14, `SQLitePCLRaw.bundle_e_sqlite3` 3.0.5, `Microsoft.Extensions.FileSystemGlobbing` 10.0.14, `System.Security.Cryptography.Pkcs` 10.0.14 on `net10.0`, `PrettyId` 2.0.2, `SyslogLogging` 2.2.2, `Radiant` 0.1.3, and `xunit.runner.visualstudio` 4.0.1. Dashboard patch updates: React 19.2.9, React DOM 19.2.9, React Router 7.18.5, Testing Library React 16.3.3, user-event 14.6.8, and `@types/react-dom` 19.2.7. The `net8.0` PKCS pin stays 8.0.2.

### Added

- Operators can explicitly replay a failed six-stage Research voyage with atomic prior-attempt archives, pinned source, scoped historical output and stale-attempt write guards. Ordinary restart eligibility and recovery budgets stay unchanged.

### Fixed

- Host agent-cache cleanup preserves raw sessions, chat databases, transcripts, tool output and database backups for the history archive owner. Disposable cache cleanup remains available. Daily cleanup also retains operator receipts and stage output; a dry run no longer deletes expired receipts.
- Dock repair metadata states that existing worktrees lose unstaged tracked changes and untracked files; missing worktrees are recreated from their retained branch after ownership checks.
- Compact voyage status reads omit unrequested mission descriptions and report previews. Explicit description and output requests retain bounded previews.
- Captain briefs name configured validation commands and input environment names without copying their values, and distinguish manual validation from native Check proof.

### Lost merge-queue handoff

- A mission whose merge-queue enqueue throws or returns no durable entry, and that has no active merge entry, becomes `LandingFailed` with the reason `merge_queue_handoff_lost: <cause>` (`landing_handoff_failed: <cause>` when the landing handler itself throws) and a `mission.landing_failed` event, instead of staying `WorkProduced` with a log line. The landing drain also enqueues a produced mission that has no voyage, on a merge-queue vessel, after five minutes without a merge entry. See [Merging](docs/MERGING.md).

### Pipeline-owned status transitions

- The operator status transition (REST, WebSocket and MCP) refuses a move into `WorkProduced` and a move from `Review` to `InProgress` or `Failed`, with the reasons `manual_transition_work_produced_pipeline_owned` and `manual_transition_review_decision_required`. Completion handling, retry-landing and the review decision paths apply those moves. The mission is unchanged on a refusal. See [REST API](docs/REST_API.md).

### Orphan recovery and operator review

- The orphaned-mission sweep no longer collects `Review` missions. A mission awaiting operator review keeps its mission-to-captain link with a released captain and no process, and the sweep used to complete it again and reset it to `WorkProduced`.

### History filter and order

- The History timeline accepts `excludeReadRequests` to leave out request entries for `GET`, `HEAD` and `OPTIONS`, and `order` (`CreatedDescending` or `CreatedAscending`). The request-history query applies the exclusion in SQL on every provider, before the per-source read limit, so polling no longer fills the request source or marks it truncated. The History page hides read-only requests until `Show read requests` is checked, and its export reads at most 10 pages and says when more entries match. The whole-list routes are unchanged.

### Database memory enumeration

- `POST /api/v1/memories/enumerate` takes an `EnumerationQuery` body (or querystring) plus `search`, `type` and `topic`, and returns one `EnumerationResult` page. `GET /api/v1/memories` and the MCP memory tools share the same read. The database now applies caller visibility (global administrator: every tenant; tenant administrator: its tenant; other callers: tenant-wide records plus their own), the filters, the order (salience, then newest, then identifier) and the page, so a request no longer loads every visible memory. Search treats `%`, `_` and `[` as plain text. The whole-list reads and the single-record routes are unchanged.

### Bounded vessel git-status reads

- `GET /api/v1/vessels/{id}/git-status` reuses a vessel's status for two minutes (fifteen seconds after a failed read) and lets requests that arrive during a read share it, so a list refresh no longer fetches and counts commits for every vessel each time. The Vessels page reads the status of the visible rows only, four at a time, and reads them again on each refresh. The response shape is unchanged.

### Judge blocking rule

- The Judge persona carries a `## When To Block` section: NEEDS_REVISION is for a defect the Judge can show (a criterion whose behaviour is missing or wrong, a failing test, build or command, a regression, data-loss or security fault, or an out-of-scope behaviour change). Requests for more tests or evidence, report or commit hygiene, pending Checks and style are Suggested Follow-ups, not blocks. An existing built-in Judge row receives the section on the next start.

### Claude client label

- The `claudeCodeEntrypoint` setting names the label every Claude Code process sends as `CLAUDE_CODE_ENTRYPOINT`. Unset, the variable stays out and `claude --print` sends `sdk-cli`. See [Claude client label](docs/USAGE_ROUTING.md#claude-client-label).

### Deploy dump rotation

- Host storage cleanup has an opt-in `deploy-dumps` stage (`prune-deploy-dumps.py`). It keeps the newest `keep` pre-deploy database dumps in one directory and removes the older ones, so dump rotation does not depend on a manual step after each deploy.

### Docker stack settings

- The default Docker stack (`docker/armada/compose.yaml`) sets `ARMADA_DATA_DIR=/app/data` and mounts `docker/armada/armada.json` at `/app/data/settings.json`, the file the Admiral reads. The settings, the SQLite database and the logs are in the mounted host directories, and the `rest.hostname` of `0.0.0.0` in that file applies.

### Captain recall tools

- Captain recall is `armada_search_context` for guidance, native facts and indexed code, plus mission-token `search_memory` for record management. `armada_fetch_context` and `armada_mission_code_search` are removed from the MCP catalog and the mission tool scope; the slimmed brief, the built-in personas and the Linter duplication check already name `armada_search_context`. The `codeIndex.captainSearchMaxResults` setting is removed; an existing settings file that still carries it loads unchanged.

### Recall scope

- Let `armada_search_context`, `search_memory` and the other recall tools resolve a mission or vessel stored without an owner, which belongs to the default user. A captain on such a mission received `not_found` instead of recall.

### Machine-readable failure codes

- A WebSocket `command.error` frame for an unhandled command failure carries `code`. Invalid command JSON uses `invalid_argument`. Other failures use `not_found`, `forbidden`, `unavailable`, `conflict` or `internal_error`.
- REST responses for HTTP 501, 503 and 504 keep `Error` and add `Code`: `NotImplemented`, `ServiceUnavailable` or `GatewayTimeout`. Local checkout import keeps `Error` `InternalError` on HTTP 503. See [Error Responses](docs/REST_API.md#error-responses).

- Let concurrent `armada_search_context` queries store a vector for the same source on PostgreSQL, MySQL and SQL Server without a duplicate-key failure: one row remains and every query completes.

- Keep a text-only copy of the most recently searched code indexes in memory for the lexical code part of `armada_search_context`, and reload it when the index file changes, so a query no longer parses every stored embedding.

- Archive an expired history folder of at least `diskLifecycle.archiveBundleMinFiles` files (default 50) as one verified `.tar.gz` object instead of one object per file, so a folder of thousands of small files archives in one round trip. A deny-listed file inside an expired history folder keeps only itself: the rest of the folder is archived and freed around it. Keep a disk-lifecycle background job past the stale limit while its pass is still advancing, and remove idle evidence build outputs that hold read-only folders.

### Paged routes for whole-list reads

- Add paged `POST` routes beside four whole-list routes that return every record: `POST /api/v1/jobs/enumerate`, `POST /api/v1/inbox/enumerate`, `POST /api/v1/model-endpoints/enumerate` and `POST /api/v1/planning-sessions/enumerate`. Each takes an `EnumerationQuery` body or querystring and returns one `EnumerationResult` page. A page size above 1000 is served at 1000 and the response names the size that was asked for.
- A page counts only the records the caller may see: the caller's scope applies before the page is cut, so `TotalRecords` never counts another tenant's or user's records. The order is fixed and ties break on the record id, so a record never repeats or moves between pages.
- The jobs page filters by `Status` (a comma-separated list), `VesselId` and a submission-time range, and carries `UnreadableJournalRecords`, so a job journal record the Admiral cannot read is never missing without a count. The jobs and inbox routes keep the global-administrator requirement of their whole-list routes. The whole-list routes are unchanged.
- The Dashboard reads these routes: the Jobs page shows one page at a time with a pager, and the Needs You, Endpoints, Captain and Planning pages read every page of their list.

### Non-root container profile

- Add an opt-in non-root variant of the Admiral server and proxy images. Each Dockerfile defines a `runtime-nonroot` stage that runs as `ARMADA_UID:ARMADA_GID` (default `1654`), owns `/home/armada` and `/app/data`, and sets `HOME` for agent CLIs and tools. The default `runtime` stage stays last, so a build without `--target` produces the same root image, user, paths and volumes.
- `docker/armada/compose.nonroot.yaml` and `docker/proxy/compose.nonroot.yaml` select the stage, pass the build arguments, and set `user:`. Combine one with its `compose.yaml` to opt in; omit it to roll back.
- The base images are build arguments (`SDK_IMAGE`, `RUNTIME_IMAGE`, `NODE_IMAGE`) whose defaults are the existing floating tags, so a build can pin an exact patch tag or digest.
- [Running Armada with Docker](docs/DOCKER.md) documents the opt-in, the state-volume ownership migration, what breaks when ownership is wrong, verification and rollback.

### Harbor host metrics

- A Harbor heartbeat may include a host sample: CPU percent, memory, the drive that holds the runner state, and counts of live jobs and live captain processes. An older Harbor omits the sample. The Admiral stores nothing for that heartbeat and the host read reports `no-data`.
- The Admiral keeps the samples in memory for each enrolled runner. The ring holds at most 120 samples and drops a sample older than 24 hours. A sample older than 90 seconds marks the host `stale` and the series ends in a gap. A gap field is null. A measured zero stays zero.
- `GET /api/v1/harbor-runners/{runnerId}/metrics` returns that series to the runner owner or a caller with authority over the owner. Another caller receives `404`. The route exists only when Harbor is enabled. The Dashboard Harbor host chart draws a gap for a missing sample and shows the stale time.

### Administrator storage inventory

- Add a read-only storage inventory for global administrators: a bounded, cached scan of the data directory by category (database files, logs, mission logs, docks, repositories, backups, archive staging, code index, operator evidence and the remainder), exact file counts and sizes, the largest files, volume capacity, and database size and table metadata on SQLite, PostgreSQL, MySQL and SQL Server. See [Storage Inventory](docs/STORAGE_INVENTORY.md).
- The scan is bounded by `storage.maxEntries`, `storage.maxDepth` and `storage.timeoutSeconds`. It never follows a link, tolerates files that vanish mid-scan, and reports a partial result with a named reason (`EntryLimit`, `DepthLimit`, `TimeLimit`, `NotReached`, `AccessDenied`) instead of failing silently. Collection deletes and moves nothing.
- `GET /api/v1/storage` returns the cache and never scans; `POST /api/v1/storage/refresh` starts a coalesced background collection and returns `202`. The Admiral also refreshes every `storage.refreshIntervalSeconds` while `storage.enabled` is true; the section hot-reloads.
- Growth history keeps the newest `storage.historyCapacity` samples in `storage-history.json` in the data directory. It needs no database table or migration.
- The Dashboard Storage page shows the categories, largest files, database tables, growth history and partial-result reasons, with a refresh button. Harbor runners do not report storage.

### Repository health

- `GET /api/v1/vessels/{id}/repository-health` reports read-only repository health for one vessel. Findings cover reachability, the default branch, a dirty or behind working checkout, bare-repository divergence from origin, a missing workflow profile, and a recent failed check. The report does not block dispatch, readiness, preflight, Checks, or the definition-of-done gate, and it does not fetch or move refs.
- A Harbor-owned vessel is read on its owner host. An offline owner returns `Unavailable` and no findings. The Dashboard vessel page shows the same report.

- Rank guidance and native facts as one list in `armada_search_context`, so the strongest match leads whatever its source; code results join by reciprocal rank. Read unified-recall memory candidates with a database-side tenant and vessel filter on every provider instead of loading every visible record.

- Add optional local semantic ranking to `armada_search_context`. `contextRetrieval.semanticEndpointId` pins a registered Ollama or OpenAI-compatible Embedding endpoint on a local or private host; hosted endpoints are refused. Guidance and native facts are ranked by BM25 fused with cosine similarity. Vectors are stored per tenant, source and model in `context_embeddings` on SQLite, PostgreSQL, MySQL and SQL Server, re-embedded when content changes, and deleted with their memory. Without the pin, search stays BM25 and makes no embedding call.

- Use unified context recall in generated persona and brief instructions. Share one durable, atomic mission call counter across unified recall and compatibility tools on all four database providers. Bind legacy recall to the authenticated mission and retain existing operator listing and code-filter contracts.

- Add `armada_search_context`: one local query over applicable guidance, native memory and existing code indexes. Keep required guidance outside recall limits, retain source versions and enforce mission and owner scope. Use the existing database driver on SQLite, PostgreSQL, MySQL and SQL Server.

### Historical Judge findings on restart

- A restarted Judge whose earlier run ended `FAIL` or `NEEDS_REVISION` receives a capped excerpt of that run in its description: the opening of the review and its findings sections, at most 6000 characters, marked historical and not a verdict. The block tells the Judge to re-check each finding against the current commit and names `armada_mission_output` for the complete earlier output, which moves to the mission's preserved runs.
- Restarting again replaces the excerpt instead of adding a second block, a description supplied with the restart stays ahead of it, and a rescue brief does not repeat it. A Judge that never ran, a Judge whose earlier run passed, and every other persona restart with an unchanged description.

### Ask proposals in the Needs You inbox

- The Needs You inbox (`GET /api/v1/inbox`, the `inbox` MCP tool and the Dashboard Inbox page) lists the caller's own pending, unexpired Ask action proposals as kind `ask_proposal`, with a link to the saved conversation. The list is scoped to the exact owner and tenant: another user's proposals, another tenant's proposals, resolved and expired proposals, and any mission or saved-chat credential see nothing. The item is read-only and carries neither the approval fingerprint nor the saved arguments; approval and rejection happen only through the exact approval fence in the conversation.
- Proposal summaries name each target vessel. `AskActionProposal.Summary` shows the vessel name from the proposal owner's tenant and falls back to the vessel id when the vessel is unknown or belongs to another tenant. The saved arguments and fingerprint keep the vessel id. The Ask action cards title each proposal with the summary, and `/ask?thread=<id>` opens that saved conversation.

### Bootstrap credentials and startup binding

- The Admiral refuses to start on a non-loopback `rest.hostname` while the seeded default admin password or the `default` bearer token is active. `AllowDefaultCredentialsOnNetwork` accepts the risk and logs a warning at every start instead; a loopback listener only warns. `ARMADA_INITIAL_ADMIN_PASSWORD` sets the admin password on the first start.
- The `default` bearer token stops authenticating once its owner no longer has the default password, and startup deactivates the stored credential. MCP authentication and caller scopes are unchanged.
- `PUT /api/v1/account/password` changes the caller's own password after verifying the current one. `whoami` and `authenticate` report `PasswordChangeRequired` while the default password is active.
- Request history redacts `CurrentPassword`, `NewPassword` and `AdminPassword` fields, so password changes and generated tenant admin passwords are never stored.
- The Docker stack passes `ARMADA_INITIAL_ADMIN_PASSWORD` to the server. The [upgrade guide](docs/UPGRADING.md#seeded-default-credentials) describes the effect on running installs.

### Tenant initial passwords and self-service changes

- `POST /api/v1/tenants` seeds the tenant admin `admin@armada` with the `AdminPassword` from the request, or with a random password returned once in the response. A new tenant admin never has the default password.
- A user who changes their own password through `PUT /api/v1/users/{id}` supplies `CurrentPassword`; a missing value returns `400` and a wrong value `403`. An administrator's reset of another user, and requests that use the configured `ApiKey`, are unchanged.
- The Dashboard Users modal asks for the current password when a user edits their own record and shows the server's refusal message. The tenant form takes an optional admin password and shows a generated one once, with a copy button, in a dialog that discards it on close. A banner links to the password change while `PasswordChangeRequired` is true and never blocks the session.

### Application login attempt limits

- `POST /api/v1/authenticate` throttles email and password logins per account and per client address. Repeated failures lock the key with exponential backoff and return `429` with `Retry-After`, even for the correct password. The `loginRateLimit` settings tune the limits. Token refresh, bearer and API key headers, and mission and Ask session tokens are not limited. See [Login Rate Limiting](docs/REST_API.md#login-rate-limiting).

### Proxy authentication hardening

- The proxy refuses to start while its shared password is blank or the built-in default. `ARMADA_PROXY_PASSWORD` sets the password and overrides the settings file; `allowDefaultPassword` permits the default for a throwaway local test. The proxy Docker stack requires `ARMADA_PROXY_PASSWORD`.
- Failed browser logins and failed tunnel handshakes are limited per client address. A client that reaches `loginMaxFailures` inside `loginFailureWindowSeconds` is locked out for `loginLockoutSeconds` with `429` and `Retry-After`. Active lockouts persist across proxy restarts.
- Login challenges are bounded to 16 per client address and 4096 in total; past a bound the proxy returns `429` or `503` with `Retry-After` and a typed refusal.
- `GET /proxy-api/v1/instances` requires a proxy session.
- The session cookie is marked `Secure` when `secureCookie` is set or a trusted forwarded scheme is `https`. A login with `"setCookie": false` sets no cookie; the native `X-Armada-Proxy-Session` header continues to work. Every `/proxy-api/v1` response carries `Cache-Control: no-store`.
- `X-Forwarded-For`, `Forwarded` and `X-Forwarded-Proto` are ignored unless `trustForwardedHeaders` is set, so a client cannot choose the address its failures are counted against. The requester address the proxy relays to an Armada instance is the socket peer address unless the setting is on.

### Ask Armada

- Ask turns launch a captain's CLI runtime with read-only native tools by default: no native shell execution and no file writes or edits. The restriction applies to saved and temporary Ask turns, is resolved on each launch's own copy, and leaves mission launch defaults and the exact MCP proposal approval unchanged.
- `ask.nativeToolAccess` widens Ask launches to the mission launch defaults. It hot-reloads, and the Dashboard Server page and `PUT /api/v1/settings` edit it.
- Each CLI runtime maps the read-only policy to its own controls: Claude Code, Codex, Gemini, Cursor, OpenCode and Mux. A CLI runtime without a read-only mapping is refused for Ask with a message that names it.

### Refinement turns

- A refinement turn releases its own claim when persistence fails before the turn starts. It releases that claim before the session is published Active, so a request that sees Active can start the next turn. A stop that has moved the session out of Responding keeps that status and leaves the captain Idle.

### Remote MCP sessions

- The API runtime MCP client opens one new session with the same caller credential and sends a request once more when that request carried a session id and received HTTP 404. A second 404, a timeout, an HTTP 5xx response, and a network failure stay failed.

### Codex inventory

- A running Codex captain's tool inventory runs on the host that executes that captain. A selected host that is offline, or whose owner cannot be authenticated, reports the inventory unavailable.

### Publish

- POSIX `publish-server` passes the resolved framework and the matching `TargetFramework` and `TargetFrameworks` properties to `dotnet publish`. Options that are not a framework stay on the dashboard deploy.

### Git reads

- Worktree registration reads `git worktree list --porcelain -z`, so a registered path that contains a newline stays one path.
- Conflicted-file names come from `git diff --name-only -z`, so a name that Git would C-quote stays one path.

### Scratch files in change evidence

- The changed-files read behind the no-op completion gate and scope validation leaves out untracked scratch files (`.backup`, `.bak`, `.orig`, `.rej`, `.tmp`, `.swp`, `.swo`, `~` suffixes, `.DS_Store`, `Thumbs.db`). A Worker that wrote only an editor backup no longer counts as having produced work. Tracked and committed files count whatever their name.

### Changelog-only landing conflicts

- A landing merge, local or through the merge queue, that stops on `CHANGELOG.md` as its only unresolved path keeps both sides with a git union merge and completes the merge commit. Release-note entries added by concurrent missions no longer fail the landing. A conflict in any other file, or a `CHANGELOG.md` conflict missing its base, ours or theirs stage, fails as before.

### No-changes landings need proof

- A direct landing no longer completes a mission as a no-op on an empty diff snapshot or a branch missing from the bare repository when the mission recorded a commit past its start commit that the target branch does not hold. The landing fails as `landing_source_unavailable`, names the recorded and start commits, and keeps the branch for retry. A mission with no commits past its start still completes as before. A failed diff capture and a failed branch lookup are logged as warnings.

## v1.0.0 (2026-10-26)

This is the fork's first 1.0 release. It includes the changes below since the
full-merge baseline. Fork and upstream version numbers are independent. The
[README adoption map](README.md#upstream-adoption-in-v100) identifies imported,
adapted and excluded upstream capabilities. The [upgrade guide](docs/UPGRADING.md)
covers database protection, client compatibility and rollback.

Docker and local publish scripts remain the release targets. CI workflow
publication stays held. Signed installers and package-service publishing are
outside this release.

### Release preparation

- Align the product, shared .NET build, Helm package, Dashboard package and API
  examples on version 1.0.0.
- Record selective upstream adoption and the retained fork policies in README.

### Recent changes

- Gate-input previews accept explicit input-to-source mappings in objective preparation, name supplied paths and their declared affected inputs, and retain uncertain planning reminders. Existing scope exclusions remain authoritative.
- Archive HTTP failures retain bounded provider support request IDs without copying response messages or unrelated headers.


- Report section checks recognize an explicit Markdown heading joined to a known progress-status line outside code examples. Stored report text and artifact digests stay unchanged.
- Architect handoff accepts opt-in version 2 and 3 JSON plan contracts through
  the same parser as MCP. Invalid final revisions cannot fall back to older
  plans. Parallel branches use a durable integration barrier with fixed accepted
  membership, verified rescue identity and proof of landing on the current target.

- Restarted dependent stages retain the verified upstream commit and can restore a missing inherited branch from that commit before checkout provisioning.

- Incident recovery requires newer Check evidence after detection or an operator status decision. Cancellation supersedes only known automatic incidents, and stale lifecycle writes cannot overwrite a newer snapshot. Explicit blank Check links reach the incident update handler.

- Require patched PKCS dependency versions for both supported .NET targets through the shared SQL client dependency graph.

- Dashboard development tools use patched Vite, Vitest, source-map-js, and Undici releases.

- Provider retries retain their owned continuation dock. Dock repair can restore an absent native checkout from its exact local branch, without creating refs or replacing foreign and locked worktrees.

- Deliverable-kind preview facts distinguish negated report-only wording from an explicit committed-test duty. Facts remain informational; dispatch preparation gates are unchanged.

- Dispatch tool schemas require a task title and description, matching runtime validation before objective context is appended.

- Judge briefs name exact-commit native Check identities and statuses, distinguish adopted evidence, and direct reviewers to current receipts before repeating work.
- Cursor whole-message flush metadata preserves report boundaries while partial and mismatched aggregates keep the terminal text unchanged.

- Review finding extraction leaves outside-scope observations and instruction proposals out of revision work. Explicit blockers and unmet criteria remain findings.

- Archive uploads and verification retry transient server or transport failures within a bounded operation. Failed uploads are checked before another PUT; successful uploads retry verification only.

- Completed report-only work can reach its configured pending Judge when that review is its sole outstanding criterion. Unfinished current work and unavailable review still block.
- Local POSIX server publish fails when Dashboard deployment fails and no
  Dashboard is available. It continues only when an existing Dashboard remains.
  Dashboard copies are staged before replacement, with rollback on install failure.

- The Voyages list shares table controls and saves optional column visibility.
  Server paging, status filters, row actions and selection remain page-owned.
- Home dashboard refresh failures show named read errors and a Retry button
  beside the last available data. Partial failures retain voyage labels.
  Chat, log and protocol frames do not start unrelated home-page reloads.

- Integration Check reuse requires a captured native execution-context receipt, exact quoted command text and current profile/environment/budget identity. Unproven or changed context runs its Check again.

- Queued server gates create build temp only after lock admission. Refused gates leave the current holder's build servers and temp files intact.

- The Server area shows Harbor jobs through the existing owner-scoped read API.
  Runner and active-job filters retain bounded reads. Unavailable endpoints,
  empty results, uncertain work and confirmed stops have separate states.

- Global administrators can save Mission actions, select vessels, and follow
  durable run outcomes through REST and the Dashboard. Immutable inputs,
  current authorization and revision claims prevent duplicate dispatch.
  Cancellation uses the existing voyage operations; unknown work stays blocked.

- Global administrators can discover and import existing local checkouts from
  configured folders through the Dashboard, REST and MCP. Import validates
  paths and current ownership, registers the local source, and preserves the
  external checkout when the vessel is removed.

- Saved Ask conversations propose supported actions for exact human approval.
  Turn-bound authenticated credentials, current authorization and durable claims
  fence execution. Work reports separate job completion, produced work, landing
  evidence and rescue lineage.

- Cursor final reports preserve complete message boundaries when the terminal
  text matches their flattened aggregate. Text chunks and distinct final replies
  retain their original content; report completeness checks stay strict.

- Incident lists apply detection-time bounds before paging. Mission credentials
  retain owner scope for incident lists and by-id reads despite owner admin roles.

- Research preflight distinguishes explicit optional document work after review
  from the current deliverable. Current commit duties and other blockers remain.

- Harbor startup reads the final journal after supervisor exit. Empty runner
  heartbeats preserve a known refusal reason and retain the completion fence.

- Dispatch previews retain non-goal context when they exclude gate input files.
  Positive rollout statements still warn about inputs that plans do not name.

- Native Slop findings keep their analysis status and evidence without being
  listed as failing tests. Custom check commands retain test outcomes.

- Mission credentials cannot be exchanged for unrestricted sessions or start
  captain chat. Child MCP access also refuses mission-scoped callers.
- Saved Ask conversations retain server-owned history, titles, captain choices,
  pins and archive state. Owner-scoped storage preserves turn results and
  metrics. Durable request claims prevent duplicate runtime launches and hold
  uncertain interrupted turns. Existing chat routes and access rules remain.

- Judge blocker extraction keeps complete command outcomes and excludes
  passing checks, source agreement and reproduction steps from findings.

- Dashboard reads retry brief connection failures within one request deadline.
  The deadline covers response bodies as well as headers. Ask Armada keeps the
  question and partial reply when delivery is uncertain and permits the next
  question without reloading; chat requests and other writes are never retried.

- Newly armed lane Checks name the work mission they measure. Lanes that share
  a commit retain separate pending, passing and failing review results.

- Declared DoD execution budgets survive post-test, isolated, flake and host-load
  retries. Native isolated retries retain their source profile limit. Each
  vessel or consumer keeps separate build and test budgets; slot waits and
  Harbor transport do not spend command time. Exhausted or unmeasurable
  declared budgets cannot produce a passing result.
  Reused full DoD proof must match the Check's resolved declared budget;
  a changed limit requires fresh verification.
- An explicit profile time budget covers a Build or UnitTest Check's full run
  and isolated retry together. Retry slot waits stay outside that budget.
  Completion now follows retry work, and the output separates active command
  time from retry slot wait so the recorded duration does not hide a retry.
- Opt-in Harbor-owned checkouts keep repository files, Git, verification and
  landing on the enrolled checkout host. Versioned bindings pin ownership and
  commits. Durable provision and cleanup intents, authenticated bounded RPC,
  keyed verification and scoped captain MCP retain the fork's safety rules.
  Existing routes remain process-only; unavailable owners produce named holds.

- Direct landing rechecks stored operator cancellation after integration
  validation and before publishing. A short shared admission boundary keeps
  cancellation and publication in order without holding it during the suite.
  Cancelled source work stays available, and late completion does not emit
  false landing success or failure events.

- A headless Harbor console runs resolved agent commands through independent
  job supervisors. Private bounded journals retain process start identity and
  ordered output across console restart. Authenticated reconnect replays output
  before exit and leaves incomplete proof fenced.

- A captain whose provider rejects its credential is benched, not re-offered the
  mission it cannot run. The process-exit failure-reason extractor selects a
  credit or credential signal (an invalid-key or HTTP 401/403 rejection) in the
  captain's own-runtime log ahead of a later generic result-error record, the
  same priority a provider safeguard and a quota limit already take. The
  recorded reason then carries the rejection, so the existing bench-and-reroute
  path quarantines the captain and re-routes the mission to a peer instead of
  requeueing it to the same failing captain. An ordinary transient failure,
  carrying no credit, credential or quota signal, is unaffected and does not
  bench.

- Harbor jobs retain their exact mission attempt across Admiral restart.
  Verified runners resume with process-start and output-replay checks. Uncertain
  remote writers block replacement and dock reclamation. Confirmed exits are
  delivered through the mission lifecycle before durable acknowledgment.

- Successful DoD gates retain their full vessel command output through consumer
  verification in the existing redacted capture store. A reused Check's mission
  reference leads to that proof, so reviews can recover test footers without
  another run. Failure output and incomplete-output flags remain explicit.

- Check work selection follows dependency order before update timestamps.
  Completing earlier stages after landing no longer replaces final-tip passes
  with checks for an ancestor commit. Supersession also rechecks durable voyage
  status before changing records, including after waiting for a lane lock.

- A transient dock-provisioning fault on a reassigned root mission no longer
  strands it. A retryable provisioning failure keeps the mission's committed
  branch and its retained dock-authority pointer, so the orphan reaper does not
  reclaim the authority dock before the retry. The orphan reaper also preserves
  an active dock a pending root mission still claims by exact identity
  (tenant, vessel, branch), not only by the mission's dock pointer, so a row
  whose pointer a failed provision cleared is not reaped. A dock no root mission
  claims is still reclaimed, and a retry-exhausted mission still releases its
  dock.

- A direct landing recovers a stale integration worktree left at its
  deterministic per-mission path by an attempt that did not clean up, so the
  first landing after it recovers and lands instead of failing on an
  `already exists` path. Only a worktree the landing owns is removed: a path
  that is not a registered worktree of the landing repository is preserved and
  named in the failure reason, and per-attempt cleanup removes only a worktree
  that attempt created. The detached-worktree helper no longer force-removes a
  path that already existed before its own add.

- Missions routed to a full Harbor wait before dock provisioning with a named
  assignment hold. A capacity race at launch returns to the same wait. Explicit
  runner routing, owner checks and the launch-time capacity limit remain in force.

- Cancelling a voyage stops its queued and running DoD gates, including gates
  for produced work. A late gate checks durable cancellation before waiting or
  running a command. Produced mission outcomes and recall-failure rules stay
  unchanged.

- Repeated pipeline handoffs preserve operator requirements after generated
  output and voyage board notes. New blocks have matching end markers;
  replacement and compaction preserve text outside those boundaries and leave
  ambiguous legacy blocks whole.

- Automatic Check discovery has an independent heartbeat after startup repair.
  A health pass waiting for completion or landing work no longer stops new
  Check sweeps. Eligibility, command locks and gate requirements are unchanged;
  shutdown cancels and joins the heartbeat before database disposal.

- Standalone stdio lists server-dependent MCP tools with the same schemas. Calls
  return named `Unavailable` errors when the required service is absent. Local
  record-backed tools and authorization rules remain in effect.

- Runtime provider failures carry typed HTTP status and machine error codes
  through process exit to account routing. Existing quota, credit and
  authentication rules, retry-time parsing and text fallback remain in effect.

- An unchanged Linter uses static review and focused checks instead of running
  the full suite solely because of its role. Tracked edits still require full
  repository verification. Explicit checks, automatic DoD gates and required
  structured Checks remain required.

- API runtimes load large caller-filtered MCP catalogs through local discovery,
  four schemas per page. Small catalogs and discovery-name collisions stay
  eager. Calls keep the same authenticated MCP connection and authorization.

- Local Claude, Cursor, Gemini and OpenCode runs publish final reply artifacts
  from structured completion events after a clean exit. Capture excludes tool
  output and reasoning, keeps existing files, and preserves telemetry and
  transcript fallback when completion is absent.

- Workspace `read_file` defaults to bounded pages with continuation offsets.
  Runtime instruction files remain complete by default within existing byte
  limits. Explicit ranges, path checks and strict UTF-8 remain in effect.

- Caller-filtered MCP tool lists use private cache scope. Shared caches cannot
  serve one caller's tool catalog to another caller.

- The incident lifecycle sweep keeps an open incident's severity when it
  re-observes the failure. It records the new failure as a recovery note and
  leaves the status open, but no longer raises the severity to High, so the
  severity set at creation or by an operator is preserved. The sweep now acts
  only when the failure evidence is new, which stops it from rewriting the same
  open incident every cycle. The reopen of a mitigated incident on a newer
  failure is unchanged.

- SQLite write operations share one cancellable FIFO gate per open database file.
  The gate covers direct and shared persistence, migrations, expiry deletes
  and live restore. Expiry keeps verified export and trace recording before deletion.
  Reads stay independent; transactions retain their turn
  through commit or rollback. Existing durability settings remain in effect.

- Marking an objective auto-dispatchable requires the enabled flag. A call that
  omits it is rejected with a named missing-parameter error instead of defaulting
  to opt-out, so a caller can no longer disable an objective by accident while the
  response reports success. An unknown or misnamed argument field is reported the
  same way rather than ignored. A well-formed enabled=true or enabled=false call
  is unchanged.

- Sending a signal to a captain requires both the captainId and the message
  argument. A call that omits or blanks the message is rejected with a named
  missing-argument error and stores no signal, instead of writing an empty Mail
  while the response reports success; a missing or blank captainId is rejected
  the same way. A well-formed call with a target and a non-empty message is
  unchanged.

- A root (stage-1) mission reassigned after a restart continues on its own
  persisted branch at the committed head instead of cutting a fresh branch from
  the default branch, so committed work is not orphaned. Continuation is
  authorized by an active dock on the same vessel, tenant and branch, resolved
  from the live dock records rather than the mission's dock pointer, so a retry
  after a failed launch or provision still continues the committed branch once
  the dock pointer has been cleared. When the persisted branch still carries
  committed work but no active dock authorizes it, the mission holds with
  `root_continuation_dock_missing` rather than cutting a fresh branch from the
  default branch; when an active dock authorizes the branch but it no longer
  resolves, the mission holds with `root_continuation_ref_missing`. A branch
  name with no committed work and no active dock still gets a fresh captain
  branch, exactly as a first assignment would.

- Consumer verification rebases absolute workflow environment paths from each
  host checkout into its provisioned candidate worktree. Build, tests and retries
  read the proposed dependency instead of an unrelated accepted checkout.
  Relative paths, path lists, literals and unrelated directory prefixes stay unchanged.

- Armed Build and UnitTest checks recheck definition-of-done evidence after
  the host slot wait. Gates publish passes before releasing their slot.
  A matching pass satisfies the check without another full command run.
- DoD build, test, consumer and isolated rerun commands use their workflow
  environment. Reusable command passes use an unambiguous environment fingerprint.

### Added

- Historical database checks restore a native v51 backup into an owned isolated
  target on each provider. They verify linked Unicode, nullable and timestamp
  values before migration, require a verified pre-migration archive, and check
  forward upgrade and a new driver startup with unchanged old migration history.

- REST authorization contracts cover the registered normal and Harbor API
  inventories. Central policy names the administrator requirements already
  enforced by typed-decision, subscription account, model health sweep,
  project-profile and skill handlers. Live MCP tests verify the regular-user
  catalog and reject operator calls before arguments are handled.
- MCP authenticates every credential through the shared authentication service.
  The obsolete random process token and its global administrator bypass are
  removed. Captain launches keep the `ARMADA_MCP_TOKEN` name and use scoped
  mission or user session tokens.

- Startup and `--validate-database` retain a verified native backup before
  upgrading an existing versioned database. The backup must pass an isolated
  restore check before any migration starts. Backup failure stops startup
  with exit code 3 and leaves the migration ledger unchanged. Fresh and current
  databases do not create an extra archive.

- A definition-of-done gate waiting for the host command slot carries a visible
  slot-wait record, like a check run's. The dispatch-hold `RunningGates` list
  gives each gate a `SlotWait` that reads `Running` while it holds a slot,
  `WaitingForSlot` with its `Position` in line while it waits for one, or
  `Queued` before its first command. An operator can now tell a gate queued
  behind others from one that is lost without reading the lock holder's dock.

- A check or gate command the OS terminates by a signal (`128 + signal`; `143`
  is SIGTERM, `137` is SIGKILL) with no failing test named is read as an
  environment interruption, not a code failure. The definition-of-done
  classifier returns `Infra` for such a run rather than `TestFail` or
  `PostTestStep`, and the integration-candidate validator re-runs an interrupted
  required check once; when it is still interrupted, the candidate is
  `Unavailable` with reason `integration_validation_interrupted` instead of
  `Failed`, so a stray kill refuses publication without failing a landing or
  firing a false code-failure rescue. A run that still names a failing test, or
  exits non-zero without a signal, fails exactly as before.

- An unknown shared test database provider is rejected instead of selecting
  SQLite. The shared console runner validates `ARMADA_TEST_DB_TYPE` and its
  `--db-type` input before suite discovery, prints the supported providers, and
  exits with code 2. Unset and empty values keep the SQLite default; supported
  provider names and aliases remain case-insensitive.

- Check summaries read Jest and Vitest per-test counts, final watch summaries,
  and elapsed time, including Windows line endings. Fresh result artifacts take
  priority over console counts. Local Check executions exclude old result and
  coverage files from their summaries while keeping them in the artifact list.
  Multi-project .NET counts still aggregate, with the longest project duration.

- Interrupted Architect fan-out is reconciled from the stored plan. Recovery
  fills missing Workers and partial stage chains with stable create identities,
  preserves existing rows and started work, repairs pending dependency edges,
  and records completion against the plan. Ambiguous or unsafe graphs are held
  for operator review. Clearing that hold retries safe reconciliation; it cannot
  bypass it. Mailbox notes already in the slice briefs are preserved and consumed.

- An ordinary full-row mission update cannot move a cancelled mission to another
  status except `Pending` for restart. A refused write preserves all stored
  fields and timestamps, and returns the current stored status to its caller.
  Explicit status-conditioned writes keep their existing contract. Completion,
  review approval and pipeline handoff stop when they observe cancellation.

- User passwords use salted PBKDF2-HMAC-SHA256 storage on all four database
  providers. Startup upgrades legacy SHA-256 rows; login also upgrades weaker
  hashes. Password-only conditional updates preserve concurrent account changes.
  The `PasswordSha256` API input remains compatible.

- A Build or UnitTest Check that passes because a definition-of-done gate
  already ran the same command in full on the same commit records that gate as
  structured evidence: `satisfiedBy` holds the kind
  (`definition_of_done_gate`), the gate's mission, the command label, the
  commit and the pass time. Its `durationMs` is null, because it ran no command,
  so it is outside the execution-time measure. REST, MCP `get_check_run` and the
  `check-run.changed` event carry the field; the dashboard Check list and detail
  show "Not run" with the gate's mission and commit; the operator watcher prints
  `NOT RUN: satisfied by ...` in place of `run=<ms>`. Persisted on all four
  providers (SQLite 121, PostgreSQL 125, SQL Server 117, MySQL 114).
- A workflow profile sets the time limit of its definition-of-done gate
  commands with `gateCommandTimeoutSeconds` (30 to 3600 seconds; empty uses the
  global `DefinitionOfDone.CommandTimeoutSeconds`). It applies to the vessel's
  build, unit-test command and post-test-step retry, and, on a consumer's
  profile, to that consumer's build and tests. The definition-of-done report
  shows the limit in effect and its source (`CommandTimeoutSource`:
  `workflow_profile` or `settings`). Persisted on all four providers (SQLite
  120, PostgreSQL 124, SQL Server 116, MySQL 113); REST and MCP carry the whole
  profile record, and the dashboard profile editor shows the field.
- A definition-of-done test command whose test runs all report zero failed
  tests but that still exits non-zero fails as the new class `PostTestStep`,
  not `TestFail`. A `Failed: 0` summary is no longer read as test-failure
  evidence. The gate runs such a command once more and takes that result; a
  second `PostTestStep` failure says `0 tests failed`, names the step that
  failed from the command's standard error, and is not re-run in isolation. A
  consumer suite that fails this way carries the label
  `consumer_post_test_step_failed: <consumer>`, and autonomous recovery
  dispatches a rescue with reason `post_test_step_retry:` instead of telling
  the operator to fix the consumer repository.

- The gate locks each consumer-verification worktree while it uses it and
  unlocks it before removal, so a `git worktree prune` run where the scratch
  directory is not visible can no longer delete its registration mid-run.

- A `skipStages` entry names one stage of the effective pipeline by persona, by
  stage order (`"5"` or `"#5"`), or by persona and occurrence in stage order
  (`"Worker#2"` is the second Worker), on every dispatch surface (MCP
  `armada_dispatch`, REST `POST /api/v1/voyages`, WebSocket `create_voyage`,
  alias dispatch, a stored objective skip and the dispatch preview), through the
  one `PipelineStageSkip` parser. A pipeline with a first Worker and a revising
  Worker can drop either one. A bare persona name that two stages carry is still
  refused with `stage_skip_ambiguous_persona`, and its message lists the order
  and occurrence name of each such stage; the Judge is refused by order as well
  as by name. A revising Worker runs unless it is named.

- A mission that a captain could serve but that finds no idle captain records
  `mission.waiting_for_captain` when the wait is first seen and again when its
  reason changes. The reason names the tier floor, the busy captains that may
  take the mission, and how many idle captains each eligibility rule excludes
  (`persona_not_allowed`, `below_tier_floor`, `model_pin_mismatch`).

- A UnitTest or IntegrationTest Check, the landing integration Check among them,
  re-runs its failing tests once alone under the same rule and bound as the
  definition-of-done gate, through the shared isolated re-run plan: a test
  failure with a complete set of at most three failing tests (or three classes
  for `dotnet test`) is re-run through the profile's `isolatedTestRerunCommand`
  or a narrowed `dotnet test` command, and the Check passes only when the re-run
  passes. Its summary then reads `passed on isolated re-run` with the full run's
  exit code and test counts, the re-run tests and the re-run outcome; a re-run
  that fails, or a red that cannot be re-run, stays `Failed` and the summary says
  which. A landing's integration Check also says, for each failing test, whether a
  file named for its class is in the change since the target tip. The
  definition-of-done setting `RerunFailingClassesOnce` switches the gate and the
  Checks together.

- An operator restart of the stage that failed a `Failed` voyage, or of a
  downstream stage cancelled behind it, reopens the voyage. The stage runs on
  the commit of the nearest earlier stage that handed off work (a failed first
  stage runs from its own start); an earlier stage that the terminal-voyage
  rule moved off `WorkProduced` because its work was not landed counts as
  handing off its commit and returns to `WorkProduced`. Each skipped stage is
  recorded `Cancelled` as superseded by the restart with its earlier reason,
  every later stage cancelled behind the restarted stage returns to `Pending`
  and runs again, the voyage returns to `InProgress`, its Checks are armed
  again, and a `voyage.reopened` event is written. A skipped stage that moved
  the branch, a `Complete` or `Cancelled` voyage, a stage an operator
  cancelled, and a stage the terminal-voyage rule failed stay refused with
  `voyage_ended`.

- A prepared objective whose recorded branch moved stays dispatchable when the
  move changed no file it depends on. The dispatch preview reads the files that
  changed between the recorded commit and the branch tip; when none is a cited
  `path:line`, a file the title or description names, or a path in a
  preparation claim, it reports the Warning
  `preparation_<source|target>_revision_moved` with both commits and the
  changed-file count, lists the move in `PreparationAnchorMoves`, and the
  dispatched brief names the current tip. A change to such a file stays the
  Error `preparation_<source|target>_revision_changed` and names the files; a
  commit or tag ref, a recorded value that is not a full commit, or an
  unreadable comparison stays that Error with the reason.
- `armada_objective_scheduler_status` lists `LastSweepSkippedObjectives`: up to
  20 objectives the last sweep examined and did not dispatch, each with its
  reason and the preview's first error code, plus
  `LastSweepSkippedObjectiveCount`. `LastSkipReason` reads `sweep_in_progress`
  while a sweep runs and `sweep_failed` when the sweep threw, so null still
  means only that work was dispatched.

- `preview_objective_dispatch` and `GET /api/v1/objectives/{id}/dispatch-preview`
  take an opt-in `compact` flag. With it they return only the objective id,
  `isReady`, the Error issues and the failed preflight facts, for an audit that
  previews many objectives. The default response is the full preview.
- Every MCP tool accepts abbreviated record ids in its id arguments (`id`,
  `...Id`, `...Ids`) and stores the full id: incident, check-run, mission,
  voyage, merge-queue, signal, captain, vessel, fleet, dock, release and
  deployment tools as well as objective and coordination tools. No visible match
  returns `not_found`; two return `ambiguous_id`. Delete and purge tools keep
  requiring full ids and refuse an abbreviation with `full_id_required`.
- Several sessions can share one SSH bridge configuration without sharing a
  coordination mailbox. With `ARMADA_PARTICIPANT_KEY_UNIQUE=1` each bridge
  process sends the configured participant key plus a random suffix fixed for
  its life, and the configured key as `X-Armada-Participant-Base`.
  `armada_coordination_read` and `armada_coordination_heartbeat` accept that
  base key, or no key, as the session's own, and return the full key as
  `YourParticipantKey`; the heartbeat does not require `participantKey` when
  the header is sent. Without the flag the bridge sends the configured key
  unchanged.
- Writes accept abbreviated record ids and store the full id. `create_objective`
  and `update_objective` resolve the objective being updated, `parentObjectiveId`
  and every linked-id list; `armada_coordination_post` resolves its objective,
  voyage, mission and incident ids, and `armada_coordination_claim` an objective
  subject. One visible match resolves; none reads as not found and stores
  nothing; two return `ambiguous_id`. Check-run and incident ids resolve as well
  as mission, voyage and objective ids.
- The objective scheduler names a vessel whose every auto-dispatch candidate it
  skipped on dispatch preflight: one `objective_scheduler.vessel_preflight_blocked`
  event and one board note list the objectives and their blocking codes, posted
  again only when that set changes.
- A preflight Q9 model flag quotes the brief lines that state a proof or expected
  value, and the lines declared unchanged on purpose apart from them.
- Trace records and the history archive share one archive. Data expiry writes
  the trace record of each expiring voyage, mission, merge entry, check run and
  deployment before it deletes the row, the recorder uploads ended records to
  `trace/`, and `armada_trace` returns the archived content of a record whose
  table row and source row are both gone (`source` `archive`).
  `IArchiveSink.GetBytesAsync` reads an archived object. With the archive off
  nothing is uploaded and the recorder logs that once.
- History archive. Disk lifecycle frees history (mission logs, diffs,
  instruction snapshots, dock metadata and stage records, final messages, whole
  operator-evidence folders and configured captain session roots) only after
  an S3-compatible upload is verified by size and SHA-256; with no archive it
  keeps history by default, and a failed upload keeps the item, records a
  `disk_lifecycle.archive_failed` event and posts a board note. Each pass
  uploads a manifest to `raw/manifests/`. A built-in deny list keeps
  credentials, settings files and their backups, keys and `secrets`/`.ssh`
  folders out of the archive. Data expiry exports each delete statement's rows
  as gzip JSON lines to `raw/db/<table>/` and verifies them before it deletes,
  deletes nothing while no archive is configured, and hands each expiring row
  to `IDataExpiryObserver` observers. The server deletes `raw/` objects past
  `archive.rawRetentionDays` and `trace/` objects past
  `archive.traceRetentionDays`. `docs/ARCHIVE.md` describes the modes, the deny
  list and keeping the last two admiral backups.
- History archive contract. The `archive` settings section (disabled by
  default; hot-reloads) names an S3-compatible endpoint, bucket, region,
  credentials file, key prefix, raw retention (90 days), trace retention (365
  days), captain session roots and extra deny patterns, and
  `archive.keepHistoryWithoutArchive` (default true) keeps history while no
  archive is configured. `IArchiveSink` reports an upload verified only when
  size and SHA-256 match, `IDataExpiryObserver` sees each expiring database row
  before it is deleted, and `ArchiveKeys` fixes the `raw/` and `trace/` key
  layout. `docs/ARCHIVE.md` describes them.
- The mission, voyage and objective read tools accept an abbreviated id (the
  prefix and creation-time part, such as `msn_abcd1234`). It resolves to the one
  full id the caller can see, and the normal scope check then applies. Another
  owner's record reads as `not_found`; two visible matches return
  `ambiguous_id`. An objective that cites an abbreviated id links the record it
  resolves to, so a brief that names a mission by its short id can read it.
- Trace records keep a compact description of every objective, voyage,
  mission, check run, incident, merge entry and deployment for
  `archive.traceRetentionDays` (365) after it last changed, so an operator can walk
  from any id to its landing and deployment after data expiry deletes the raw
  rows. A record holds links (voyage, parent and rescue missions, Challenger,
  merge entries, checks, deployments, incidents, objectives), the lifecycle
  timeline, vessel, captain, model, persona, stage, branch, start ref, work,
  landed and deployed commits, the gate result with failing test names, check
  totals, the Judge verdict and finding count, the failure reason, the last
  4 KB of the final message and archive pointers; free text is redacted. The
  health loop records each record its events name and expires old trace
  records; `TraceExpiryObserver` records each row data expiry is about to
  delete. An ended record is uploaded to `trace/` through the archive sink
  while the archive is enabled. `armada_trace` returns
  the linked chain for a full or short id, and `armada_trace_backfill` fills
  the table from the stored tables in bounded, resumable batches. New tables
  `trace_records`, `trace_links` and `trace_cursors` (migrations SQLite 119,
  PostgreSQL 123, MySQL 112, SQL Server 115).
- Typed-decision quality is measurable against outcomes. Every recorded call
  names its subject (`subject_type`, `subject_id`): the mission, else the
  objective, else the record the seam judged (`papercut_group`, `board_note`,
  `merge_entry`, or the record an inbox item is about), with
  `subject_unavailable_reason` when there is none. Each call also records the
  rule's answer per question (`rule_answers`) and the model's verdict and action
  confidence. The global-operator tool `armada_typed_decision_outcomes` joins
  calls to the subject's later landing result, linked incidents, rescues or
  objective status, per decision and per confidence band, and reads older rows
  through their mission or objective id.
- The `preflight` state carries a `dock` section (provisioned sibling checkouts,
  tools that do and do not resolve on the captain PATH, and the vessel's
  evidence roots by name) and the objective's evidence links and preparation
  claims. Q4 judges dock observability against that section. Q9 judges only the
  value the brief names as the proof of the fix, flags it when that value is the
  same before and after the fix, and never counts a value the brief declares
  unchanged on purpose (parity or control) or one that guards a different defect.
- `PreflightTextAdapter.DescribeRequest` builds the exact redacted state and
  questions the preflight decision sends, and the synthetic evaluation set
  (`armada_typed_decision_eval`) carries a `preflight` reference case for Q9.

- A challenge round can test a Judge NEEDS_REVISION before its rescue. With
  `autonomousRecovery.judgeChallenge.enabled` (off by default; hot-reloads),
  autonomous recovery dispatches a read-only `Challenger` persona on a model
  other than the Judge's to rule UPHOLD or REFUTE on each numbered blocking
  finding. The rescue then lists only the upheld findings and names the refuted
  ones with their evidence; a block whose every finding is refuted dispatches no
  rescue and waits for an operator. A REFUTE below `refuteMinimumConfidence`
  (default 0.70) or without evidence stands as UPHOLD, and a finding that names
  an `unchallengeableMarkers` entry or a protected path always stands. A FAIL, a
  red Check, a criterion conflict and a comment-only revision are never
  challenged. A Challenger that fails, times out, runs on the Judge's model or
  writes an unusable ruling line leaves the rescue with every finding.
  `excludedVesselIds` turns the round off per vessel. Each outcome is a
  `judge_challenge.*` event, listed in the mission recovery report.
- Captains reach declared evidence directories with built-in search and file
  tools. The `captainEvidenceRoots` setting (a list of `{ "path", "vesselIds" }`,
  empty by default) names read-only directories, such as a mounted export tree;
  each root whose `vesselIds` is empty or names the mission's vessel is passed as
  an extra workspace root (`--add-dir`) to Claude Code, Codex and Cursor. A
  declared root that does not exist is left out with a logged warning. The list
  hot-reloads, so a settings reload applies it to the next launch without a
  restart. Harbor
  launches register the same sibling and evidence roots as local launches, mapped
  to the runner's paths through the route's working-directory roots; a root the
  route cannot map is left out with a logged reason.
- Each campaign has its own coordination room. An objective tree whose nearest
  tagged ancestor carries `campaign:<name>` gets a `campaign-<name>` room,
  created on first use like the fleet room. A claim on a campaign objective, and
  an `armada_coordination_post` note that names a campaign `objectiveId`, land
  there when no `roomKey` is given; an explicit `roomKey` wins, and every other
  claim, note and captain note stays in the fleet room. A mirrored fleet event
  for a voyage that serves a campaign objective also posts an untagged copy to
  the campaign room, so voyage-tagged brief injection still sees each note once,
  from whatever room holds it. A heartbeat in any room keeps every claim its
  participant holds alive. `armada_campaign_status` returns each root's
  campaign-room notes under `CampaignRooms` without creating a room.
  `GET /api/v1/coordination/presence` aggregates presence across every room, and
  the dashboard chatroom shows that aggregate, naming each session's rooms, and
  opens on the fleet room. The fair-share scheduler and the room routing share
  one campaign-root rule.
- A workflow profile declares `gateInputPaths`: the repository-relative files
  or glob patterns its required checks read, such as a data dictionary a docs
  check compares with the schema. A save normalizes the list and refuses a
  rooted or parent-relative entry. An Architect brief on the vessel lists them
  in a `Gate Input Files` section, so the plan assigns a gated file to the
  mission whose change touches it. A plan that names none of an entry records
  `mission.architect_plan_gate_inputs_missing` and still fans out, and
  `preview_objective_dispatch` warns with `gate_inputs_unplanned`. Persisted on
  all four providers (SQLite 118, PostgreSQL 122, SQL Server 114, MySQL 111);
  the dashboard profile editor shows the field.
- Deployments pin one immutable candidate. A deployment resolves its
  `SourceRef` once, at create, to the full commit in the vessel's own
  repository and stores it as `SourceCommit`, with an optional
  `ArtifactIdentity`. The deploy and rollback Checks run with that commit as
  `CommitHash`, a Pending deploy or rollback Check is reused only at the same
  commit, and every deployment check command receives
  `ARMADA_DEPLOYMENT_SOURCE_COMMIT`, `ARMADA_DEPLOYMENT_SOURCE_REF`,
  `ARMADA_DEPLOYMENT_ARTIFACT` and `ARMADA_DEPLOYMENT_ID`. A ref that moves
  after create does not change what ships; execution names the move in
  `ValidationSummary`. Once a deployment is approved, started or finished, an
  update that changes its ref, artifact or vessel is refused with
  `deployment_candidate_frozen`. Approval accepts the reviewed candidate
  (`expectedSourceCommit` and `expectedArtifactIdentity` over MCP, `SourceCommit`
  and `ArtifactIdentity` over REST), refuses a different one with
  `deployment_candidate_changed`, and records `ApprovedSourceCommit` and
  `ApprovedArtifactIdentity`; execution refuses a candidate the approval did not
  name with `deployment_approval_mismatch`. An environment's
  `RequiredCheckTypes` names the Check types that must have passed at exactly
  the pinned commit before approval and before execution. Each type is decided
  on its own; a failed, stale, filtered or command-overridden record, or a
  record of another type, never satisfies it. The deciding records are stored in
  `ValidationCheckRunIds` and the verdict in `ValidationSummary`. Migrations add
  the six deployment columns and `required_check_types_json` on SQLite,
  PostgreSQL, SQL Server and MySQL.
  **Default and live impact:** `RequiredCheckTypes` is empty for every existing
  and new environment, so the evidence gate refuses nothing until an operator
  sets it. Existing deployments keep a null `SourceCommit`, run as before, and
  are never re-resolved. A new deployment on a vessel with a git repository is
  pinned; a vessel with none creates an unpinned deployment and the admiral log
  names it. One behaviour changes for every environment: a create or a ref
  change whose ref names no commit in the vessel's existing repository is now
  refused with `deployment_source_unresolved` instead of being stored.
- A mission change summary: `GET /api/v1/missions/{id}/change-summary` and
  the MCP tool `armada_mission_change_summary` share one reader. It lists every
  path one mission stage committed, with its status (`Added`, `Modified`,
  `Deleted`, `Renamed`, `TypeChanged`), by comparing the stage's recorded start
  commit (the dock's stored Git anchor snapshot, the dock start record, or a
  start ref that is a full commit id) with its recorded final commit in the
  vessel's bare repository. It needs no dock, so it still answers after the
  dock is reclaimed, and it is not a diff against the current target branch.
  The answer states both full commit ids, so moving a branch never changes
  them. The mission comes from the caller: a mission credential reads only its
  own mission, and any other caller reads under its tenant and role scope; the
  vessel and both commits come from the mission, never from the request. A
  missing recorded commit, a missing object, a deadline, output past its limit,
  or a busy reader returns `Available: false` with a named `Reason`, never an
  empty change; more paths than `maxPaths` (default 1000, maximum 5000) return
  the first ones with `Complete: false`, `Reason: path_limit` and the total.
  Reads run under a 30-second deadline, a 4 MiB output limit, four concurrent
  reads, and 60 calls per mission credential; caller cancellation propagates,
  including an MCP client's cancellation of the tool call.
- Integration validation for both landing paths. The direct landing and the
  merge queue measure the merged commit itself before the target branch moves
  or is pushed, through one shared validator that runs the vessel profile's
  `BuildCommand` and `UnitTestCommand` as Checks against that commit. A Check
  that already passed at exactly that commit with the same command is reused,
  so a fast-forward landing of a measured tip runs no second gate. Each verdict
  is a `landing.integration_validation` event that names the source,
  target-base and integration commits and the linked Check IDs; the Checks link
  no mission or voyage, so branch-review gates stay separate. A failing combined
  tree is refused with `integration_validation_failed` and leaves the target,
  its remote and the branch unchanged. A target that moves during validation
  makes the candidate stale: the direct landing retries it as target-branch
  drift, and the queue sends the entry back to `Rebasing`, both bounded by
  `MaxLandingRetries`. The queue publishes only the commit its latest evidence
  names, so a restart never publishes on evidence for another candidate.
  No-test-command policy: a vessel whose profile has neither command is
  refused with `integration_validation_command_missing`, also when the queue
  has no `MergeQueueTestCommand` (such an entry passed with no tests before).
  `integrationValidation.noChecksRequiredVesselIds` lists vessels that require
  no checks (outcome `NotRequired`), and `integrationValidation.enabled: false`
  turns the validation off (outcome `Disabled`). Live impact: a vessel with both
  commands in its profile keeps landing, and each landing that is not a
  fast-forward of a measured tip now runs its Build and UnitTest Checks under
  the host-wide command slot. A vessel without either command stops landing
  until a command is added or the vessel is listed. `docs/MERGING.md`,
  "Integration Validation", describes the flow.

- Source-control phase telemetry on the `Armada` meter. Duration histograms
  in seconds cover repository-lock wait, fetch, primary and sibling checkout,
  sibling artifact copy, host-command-slot wait, validation commands and
  landing, each labelled with a closed `outcome` vocabulary and, where a phase
  retries, a `retried` label. Dock provision and reclaim and merge-queue
  completion emit their counters, and a byte counter reports artifact data
  copied into sibling checkouts. Labels carry no path, branch, commit or id.
  A disabled or failing listener leaves every operation result unchanged.
  `docs/production-metrics.md` lists the instruments and how to read them.
- The `handoff_outcome` decision can hold a partial stage for the operator.
  With `typedDecisions.handoffOutcomeHold.enabled` set (default `false`), a
  gated `partial` at or above 0.90 holds the next stage instead of Mailing it,
  and the inbox lists the hold as `handoff_outcome_hold` with the unmet
  acceptance criteria. The operator answers with the new `armada_handoff_hold`
  tool: `release`, `mail_release`, or `rescue`. A hold nobody answers is
  released with the Mail after `typedDecisions.handoffOutcomeHold.timeoutMinutes`
  (default 240). Each ending writes a `typed_decision.labelled` event (`release`,
  `mail_release`, `rescue`, or `hold_timeout`) joined to the decision on
  `state_sha256`, and retention keeps these events whatever their age. Below
  0.90, with the decision unavailable, or with the hold off, the partial Mail is
  unchanged. The hold never cancels, rescues, lands, or dispatches.
- A definition-of-done gate that fails with a `Timeout` failure class while
  the host is overloaded runs once more before the mission is failed. The host
  is overloaded when the 1-minute load average in `/proc/loadavg` is above 2
  times the logical core count. The gate releases the host command slot, reads
  the load every 30 seconds until it is at or below the core count, for at
  most 20 minutes, and then evaluates again. It records
  `mission.definition_of_done_load_rerun` with the load at the timeout and at
  the re-run, the core count, the thresholds and the seconds waited. The
  second result stands, so a second `Timeout` fails the mission as before. A
  `Timeout` at normal load, or on a host without `/proc/loadavg`, fails at
  once.
- The admiral samples its own memory every 5 seconds. While the managed heap
  or the working set is at or above 6 GiB it logs an `[AdmiralMemoryWatch]`
  warning with the managed heap, working set and container memory, and the
  REST requests still running, oldest first with their age, for example
  `requests in flight: POST /api/v1/history/enumerate (40 s)`. The warning
  repeats after each further 1 GiB of growth or every 60 seconds. Every sample
  above the threshold also updates the run marker, so the unclean-exit report
  after an out-of-memory kill carries memory figures from seconds before it.
  The request log records a request only when it completes; this warning names
  the request that is still running.
- An implementation Judge brief carries an `## Independent Check Evidence`
  section. When every latest UnitTest Check record at the commit under review
  has passed, the section names that Check and commit, and the Judge cites it
  as test evidence and runs only focused tests for the changed code, with each
  command and its result. With no such Check, or when the diff changes test
  infrastructure or build configuration, the Judge runs the full test suite in
  the foreground. The Judge template carries the rule in a
  `## Choosing Which Tests To Run` section that reaches the embedded default
  and existing built-in rows through the append-if-missing upgrader, and the
  Judge output contract states it. A Judge no longer repeats a full-suite run
  that an independent Check already measured.
- A Judge declares a required fix that cannot be made without breaking a stated
  acceptance criterion on a standalone
  `CRITERION-CONFLICT: <criterion> -- <conflict>` line and returns
  NEEDS_REVISION. A NEEDS_REVISION or FAIL that carries one is held for an
  operator ruling, not recorded as a failure: the mission stays `WorkProduced`
  under the operator-review hold with a `criterion_review incident <id>:`
  reason, the voyage stays open, the inbox lists it as
  `criterion_review_hold` ("Awaiting operator ruling on a criterion conflict"
  and the incident id), one incident with root cause `criterion_conflict`
  lists each conflict, and a `mission.criterion_conflict` event is recorded.
  `armada_review_hold` clear lands the work as it stands; fail ends it
  `Failed` with `[judge:criterion_conflict]`, so autonomous recovery
  dispatches no rescue (blocked reason `criterion_conflict:`). Either ruling
  closes the incident; a hand close while the hold stands is refused with
  `criterion_review_hold_open`. The Judge template carries the rule in
  a `## When A Required Fix Breaks A Criterion` section that reaches the
  embedded default and existing built-in rows through the append-if-missing
  upgrader, the Judge output contract states the line form, and a rescue's
  re-verification Judge is told to check each required fix against the
  criteria.
- `definitionOfDone.hostCommandMemoryFloorMb` holds a free host command slot
  until the host has that many megabytes of available memory (`MemAvailable`
  in `/proc/meminfo`). It defaults to 6144, is clamped to 0-1048576, and 0
  turns it off; a settings reload applies it without a restart. An unknown
  reading never holds a command. A request held for memory keeps its place in
  line, and Armada reads memory again every 5 seconds while it waits. When no
  slot is held and the request has been held for memory for 15 minutes, it
  runs anyway and is recorded as granted under the floor. The slot snapshot,
  the check `SlotWait` and the automatic check runner's log line name the
  memory reason, for example
  `waiting for free memory: 4,100 MB available, floor 6,144 MB`.

- Both mission-rules templates tell a captain to delete a temporary file with
  `rm <path>`, without `-f`, or to write to a fresh path, and to hash a file
  with `sha256sum <path>` instead of hand-written hashing code. The lines reach
  the embedded defaults and existing built-in rows through the append-if-missing
  upgrader, so operator edits are kept.
- The Linter template carries a `## Test Check` section. For each test the
  mission adds or changes, the Linter reports duplicate or low-value proof
  under `## Residual Issues` as `TEST: <path:line> - <problem>`, and it never
  deletes, skips or weakens a test or loosens an assertion. The TestEngineer
  template carries a `## Test Authoring Gate` that asks what each new test
  protects, what regression fails it and why existing tests miss it. Both
  sections reach the embedded defaults and existing built-in rows through the
  append-if-missing upgrader, so operator edits are kept.
- `diskLifecycle.extraRetentionRoots` keeps operator directories outside
  Armada's own storage bounded. Each entry names a `path`, `retentionHours` and
  `keepNewest`; each direct child of the path is reclaimed once idle past the
  retention and outside the newest kept, unless it holds `.armada-keep` or is a
  symlink. A low-space pass logs a warning and records a
  `disk_lifecycle.low_space` event. `scripts/common/rebuild-local-image.sh`
  keeps the newest `ARMADA_RETAINED_KEEP` distinct images plus the running and
  just-built ones, removes the tags matching `ARMADA_PRUNE_TAG_PATTERNS` from
  older images, and prunes dangling layers.
- `preview_objective_dispatch` warns with `objective_text_conflicting_ids`
  when the stored text calls an objective other than the record or its parent
  current, parent or authorizing, and with
  `objective_text_conflicting_criterion_counts` when stated criterion counts
  disagree with each other or with the criteria list. The brief's Objective
  Scope names the stored parent objective id and, when the text names another
  controlling id, states that such an id does not control.
- `armada_record_judge_followup_handled` records that one Judge follow-up item
  has a home: a fix objective, an owner decision, or a note, with who handled it
  and when. `armada_list_judge_followups` numbers each item, leaves handled
  items out by default, reports `OpenItems` and `HandledItemsOmitted`, and lists
  them again with `includeHandled`. An unknown follow-up, item number, kind or
  objective is refused with a named reason. Migration SQLite 115, PostgreSQL
  119, MySQL 108, SQL Server 111 adds the `judge_follow_up_handled_items` table.
- `armada_list_judge_followups` lists the follow-ups Judge reviews suggested,
  newest first, split into one item per bullet, with vessel, time-window and
  paging filters. An explicit "none" counts as no follow-up.
- Memory proposals gain a `Promoted` state. The new operator tool
  `armada_promote_memory_proposal` records the full AI-Memory commit SHA, the
  operator and the time on an Open proposal after the owner writes the rule by
  hand. It refuses a short SHA (`commit_sha_not_full`) and a dismissed or
  already promoted proposal (`proposal_not_open`). Armada still never writes
  AI-Memory. Migration SQLite 114, PostgreSQL 118, MySQL 107, SQL Server 110
  adds the promotion columns.
- Typed-decision call events carry `estimated_cost_usd`, `cost_is_estimate`
  and `price_table_version`, priced from a new `typedDecisions.pricing` table
  that ships empty. A batched item is priced on its share of the request's
  tokens. With no price the cost is null and `cost_unavailable_reason` says
  why. The observation SQL reports cost per decision, cost per applied action
  and cache hits.
- The `preflight` decision caches its answers by state hash, question
  contract and model, so a repeat preview of an unchanged objective records a
  `typed_decision.cache_hit` event instead of a new provider call. The verdicts
  are the same as a fresh call's.

- Two optional workflow-profile fields let the definition-of-done gate re-run
  the failing tests of a runner that is not `dotnet test`.
  `failedTestLinePattern` is a regular expression with a named group `name`
  that reads failing names from lines the built-in matchers do not know; a
  save refuses an invalid pattern or one without the group, and each match
  has a 250 ms timeout. `isolatedTestRerunCommand` re-runs only the named
  tests, at most three, with the names in the `ARMADA_RERUN_TESTS`
  environment variable and never in the command text. The vessel, flake-score
  and consumer re-runs all use them, each from its own profile. A red whose
  names were read but that has no re-run route now says which field is
  missing. Without the fields, every existing re-run command is unchanged.
  The dashboard profile editor shows both fields.

- A configuration-driven host storage cleanup (`scripts/linux/storage-cleanup/`)
  with a daily systemd timer and installer. It prunes ignored build outputs,
  agent caches, redundant Docker tags and old test databases, and can remove
  idle scratch folders and unused disposable image tags. Every host path and
  pattern comes from `/etc/armada/storage-cleanup.json`.
- The four-week typed-decision observation review
  (`docs/TYPED_DECISION_OBSERVATION_REVIEW.md`) replaces the interim one: per
  decision calls, actions, unavailable calls, latency, tokens and reversals for
  16 September to 14 October, reversal causes, one recommendation per
  decision, and the rescue and papercut baselines. No setting changes.
- `definitionOfDone.hostCommandSlots` sets how many full build or test
  commands (definition-of-done gate commands, check runs and merge-queue test
  runs) the host runs at once. It defaults to 1, which keeps one suite at a
  time, and is clamped to 1-8. Slots are still granted in request order; a
  settings reload applies a new count without a restart. The check slot wait
  and the automatic check runner name every current holder. A count above 1
  risks false Timeout failures on a host without the capacity for it.
- The coordination board reports a participant key that several sessions
  share. A key that posts or heartbeats under two or more names within the
  presence window and switches back and forth (one rename does not count) is
  listed in the `armada_coordination_read` result under `SharedParticipantKeys`,
  the heartbeat result for that key carries `SharedParticipantKey`, and one
  `coordination.shared_participant_key` event is written per key per window.

### Changed

- WebSocket hub limits are settings: `webSocket.replayHistoryEvents` (default
  8,192), `webSocket.replayHistoryBytes` (default 32 MiB) and
  `webSocket.clientQueueCapacity` (default 1,024), read at startup. A
  `check-run.changed` event carries the Check without its output and names
  `outputLength`; the dashboard Check page keeps the shown output and reads the
  new one by id.

- `armada_list_papercuts` groups by vessel by default. A `groupBy` argument
  selects `vessel` (one group per vessel), `category` (one per vessel and
  category) or `title` (one per vessel, category and normalized title, with the
  `papercut_merge` decision). Filters apply before grouping. Each group carries
  `GroupBy`, `VesselName`, `CategoryCounts` and `TitleGroupCount`, and a vessel
  or category group carries its 10 largest title subgroups. A continuation
  token records its grouping mode, and a token used with another mode returns
  an error that names both modes. Tokens issued before this change are
  refused as invalid.
- `scripts/linux/server-gate.sh` runs one gate per host. It takes a host-wide
  lock directory (`--host-lock`, `ARMADA_GATE_HOST_LOCK`, default
  `/tmp/armada-gate-host.lock`) inside the session that runs the gate, writes an
  owner file, waits until the host has `--min-available-mb` of available memory
  (default 8000), and releases the lock from its exit trap after it shuts down
  this run's build servers. A lock whose owner process is gone is taken over and
  reported only when its owner file names a numeric pid that no longer runs on
  the host; any other owner, or no owner file, counts as live. A gate
  releases the lock only while the owner file names its own pid, and otherwise
  leaves it and warns. `--lock-held` serves a caller that already holds the
  lock.
- Dispatch preflight Q1 names a moved symbol at its declaration nearest the
  cited lines, never at an earlier call site. A symbol within 10 lines of its
  cited range passes the question and adds a `preflight_citation_drift` Warning
  with the corrected line. A backticked Armada record id is not searched for as
  code.
- `gate_inputs_unplanned` leaves out the gate inputs an objective's rollout
  constraints or non-goals keep out of the change, names them, and is not raised
  when none is left.

- Claude Code captains launch with `--exclude-dynamic-system-prompt-sections`,
  so the per-repository system-prompt sections (memory path, environment, git
  status) reach the captain in its first message instead of the system prompt.
  Every captain on one model then sends the same system prompt and reads it
  from the prompt cache whichever vessel warmed it, instead of writing a
  vessel-specific copy. `ClaudeCodeRuntime.ExcludeDynamicSystemPromptSections`
  turns the flag off for a CLI that does not accept it.
- A typed-decision call that keeps the rule records `typed_decision.rule_kept`;
  its `gate_outcome` says whether the decision was in Shadow or the Gate-mode
  call took no action. Every reader, the observation SQL and the docs accept
  rows written under the earlier name `typed_decision.shadow`.
- `failure_cause` asks separate closed questions for the cause class, evidence
  availability, code defect and, for a Judge failure only, verdict validity,
  and combines them in code. A code defect suppresses every hold; a well-formed
  NEEDS_REVISION verdict that reports missing evidence holds as environmental,
  never as verdict form. Thresholds are unchanged.
- `typedDecisions.retention.minimumSamplesPerDecision` defaults to 50.
- The Git Service and Dock Service unit suites take their base repository
  (branch `main`, one "Initial commit" adding `README.md`, test user
  configured) from the shared `TestGitRepoHelper` template, moved into place,
  instead of running six git commands per case.

### Removed

- Eleven scripts that nothing referenced: the server bootstrap and
  self-deploy cutover rehearsal scripts, the Windows proxy build, landing
  audit and Markdown backlog import scripts, and the hand-run tests for the
  image rebuild, framework resolution, fork migration check, SSH bridge,
  cycle-log renderer and watcher scripts.
- 132 legacy `test/Armada.Test.Automated` HTTP cases for voyages, fleets,
  captains, events, the merge queue and vessels, each of which the
  `Test.Shared` E2E suite runs with the same route, inputs and assertions; and
  two weaker `Test.Shared` voyage cases whose legacy twins assert more. Mission
  status cases stay in both suites, because the two set up different missions.
- 23 per-adapter typed-decision tests for the refusal, flake-score,
  change-substance, stage-necessity, review-substance, runtime-failure and
  failure-cause adapters that only checked the Off, Unavailable, Shadow and
  below-threshold modes. Those modes are decided by the shared adapter base,
  which `TypedDecisionGateMatrixTests` drives across every cell; each adapter's
  own Gate tests still prove it reads its own decision settings and builds a
  request.

### Fixed

- A stage that exits non-zero after producing some output keeps that captured
  output on its mission. The successful-completion path drained the partial
  stdout/stderr onto the mission, but the terminal process-exit failure path did
  not, so a failed stage finalized with no output and its artifact reported a
  zero-character page with truncation `output_unavailable`. A downstream stage or
  rescue then fell back to the weaker stored description for scope. The failure
  path now drains the same capture and keeps it when the mission has none yet and
  the capture is non-empty, so the artifact reports the real content and
  character count; `output_unavailable` still means genuinely no output was
  captured, distinct from `mission_not_finalized`.

- The landing-drain safety net no longer races a reviewer landing that is
  tearing down the reviewed branch's dock. When a Judge PASS releases, its own
  landing reclaims the dock the instant the hold clears, so a drain sweep running
  at the same moment could read a dock row whose worktree directory was already
  gone and start a git diff against a missing working directory. The drain now
  treats a worktree that is absent on disk as no dock and flags the branch
  unmeasured, the same as a dock with no worktree at all, instead of spawning a
  git process that fails.
- A voyage is no longer given a terminal status while a merge entry for one of
  its missions is still landing. Every voyage-completion writer now keeps the
  voyage open while such an entry is non-terminal (reason `merge_in_flight`),
  alongside the existing holds for an in-flight mission completion and a running
  landing retry. Before, a reopened voyage whose earlier stages had been
  reconciled `Failed` by a prior terminal pass was re-failed the instant its
  restarted Judge PASS released, mid-landing, which cascaded an unlanded-work
  outcome onto the stage that was in fact landing. The landing now resolves the
  voyage once its entry reaches a terminal status.
- The background-job stale reaper no longer fails a landing-retry job while its
  landing is still running. The reaper reaped any job past a flat 30-minute limit,
  but a landing serializes its integration candidate's Build and UnitTest checks
  behind one host lock and routinely runs longer, so a healthy landing was marked
  `Failed` as stale while it was about to succeed, and an operator who trusted the
  false notice could retry or rescue a landing that was about to land. Past the
  limit, the reaper now keeps a landing-retry job while its integration candidate
  still has a Pending or Running check, and reaps it only once no check is
  advancing, so a genuinely stuck job still reaches a terminal status.

- `resolve_check` (and its `armada_resolve_check` alias) returns the bounded
  check-run view by default instead of the resolved record whole. A check whose
  stored log is megabytes returned a response past the tool output limit, so the
  client refused the whole result. The tool now mirrors `get_check_run`: it
  returns status, exit code, parsed totals, and the tail of the log by default,
  names the complete log's size and the call that fetches it, and takes the same
  `includeOutput=true` opt-in to return the complete log and `outputTailLines` to
  widen the tail.
- A voyage whose stage failed through a stalled captain with recovery exhausted,
  or through the stage watchdog firing with no captain heartbeat, now cancels the
  stages waiting behind the failed stage. Both paths keep the voyage alive so
  autonomous recovery can rescue the work, but previously left the blocked
  downstream stages Pending for ever, so the voyage stayed active and kept holding
  a fleet and sibling-lane slot while nothing in it could run. They now cancel the
  stranded stages through the same dependent walker every other voyage-preserving
  failure path uses, so the voyage reaches a terminal state and frees its lane; the
  failed stage, any produced work, and the separate rescue voyage are untouched.

- The Judge-PASS acceptance-criteria walk measures the linked objective's live
  criteria at check time, not the dispatch-time snapshot. When an operator
  narrows an objective's acceptance criteria after its voyage is dispatched, a
  Judge PASS that covers the current criteria is validated instead of being
  refused on the criteria the stored mission description still lists. A mission
  with no linked objective still reads the criteria from its description, so
  ad-hoc missions are unchanged, and a live criterion the review never walks is
  still refused.

- The coordination claim tool rejects a missing action instead of silently
  listing. A call that sends subject fields but no action returns a
  `missing_action` error rather than defaulting to a read that returns an empty
  claim list and creates nothing, so a caller can no longer read a silent no-op
  as a successful reservation. A valid `claim`, `release`, or `list` action is
  unchanged, and an unknown action still returns an error naming the valid values.

- A disk-lifecycle history-archive run no longer floods the log and board when
  the object store's download or transaction cap is exceeded. A read-back verify
  that returns HTTP 403 with a body naming a cap stops the run's verify loop
  after the first such refusal and records one cap event and one board note,
  rather than one capped read-back and one failure line per remaining file. The
  remaining history is kept locally and counted, and the report flags the cap.
  A 403 that names an auth, permission or signature error, not a cap, is still
  reported per object and is never swallowed as a cap.
- A Judge check-wait re-run no longer loses the run it replaces. When a held
  NEEDS_REVISION (or any in-place Judge re-run) resets the mission for a new
  run, the prior run's review output and verdict are preserved and shown before
  the current run in the persisted mission-output read path, each headed by a
  marker that names its verdict. An operator can read every run of a mission and
  the first run's requested revisions, where a re-run previously overwrote them
  and the mission-output read returned empty.
- A held Judge NEEDS_REVISION whose independent Check fails at the reviewed
  commit now routes to a revision Worker instead of re-running the Judge. A
  failed Check proves the work is broken, so a Judge re-run on the same commit
  cannot clear the revision; the held NEEDS_REVISION is recorded (keeping the
  review as the Worker rescue feedback) so autonomous recovery dispatches the
  revision. A held NEEDS_REVISION whose Checks pass, or that no Check gates,
  still re-runs the Judge once, because that revision may have been asked only
  because the Checks were pending.

- A build or test command that only waits on its vessel's suite lock no longer
  holds a host command slot another vessel's command could use. A gate, check
  run or merge-queue test names the vessel whose suite it runs, takes an
  in-process per-vessel suite lock before it queues for a host slot, and
  releases the slot before the vessel lock. One vessel's suite commands are
  serialized before the slot queue instead of each taking a slot and blocking on
  the vessel's own file lock, so with more than one host slot a blocked
  same-vessel command does not starve another vessel. The per-vessel lock is
  always taken before the host slot, so the order cannot deadlock.

- The disk-lifecycle instruction category covers each mission's brief-file
  folders (`<mission>.mission` and `<mission>.memory` beside the instruction
  snapshot). Each folder is one item under `instructionRetentionDays`, aged by
  its newest file, archived file by file and then freed.
- A history folder whose files are all verified in the archive is freed even
  when directories inside it lack owner write: the pass gives each directory
  inside the folder owner read, write and execute first, never changes the
  folder that holds it, and never follows a symlink. An archived item that
  still cannot be freed is named in the report's `Errors`, counted in
  `ArchiveFreeFailures`, recorded as one `disk_lifecycle.archive_free_failed`
  event and posted in the pass's board note. A file whose key already holds the
  same size and SHA-256 is not uploaded again (`IArchiveSink.VerifyStoredAsync`;
  the report counts it in `ArchiveReusedItems`), so the next pass frees the
  item without a second upload.
- A disk-lifecycle pass archives at most `diskLifecycle.archiveMaxFilesPerPass`
  files (default 1000), because the pass runs inside the health loop. Further
  expired history is recorded `deferred` and left for the next pass; the report
  sets `ArchiveCapped` and `ArchiveDeferredItems`, the report event names the
  deferred count, and its payload carries the archive counts.
- A gate's isolated re-run that fails again names its failing tests. The
  re-run read its output without the workflow profile's
  `FailedTestLinePattern`, so a runner whose failure lines only that pattern
  reads left the failure text with a tail of later passing suites and no
  failing test name. Both isolated re-run paths now pass the profile pattern.
- The Judge-finding extraction that numbers findings for the Challenger and
  the rescue brief no longer splits evidence into findings. Items under a
  lead-in that introduces a counterexample or reproduction are context unless
  an item names a defect itself, and a passing count with thousands separators
  (`C#: 1,389 passed, 0 failed.`) or a bare issue count (`Lint: 0 issues.`) is a
  passing test summary.
- A captain's file search lists the files Armada stages in its dock. Git
  excludes `_briefing/`, `.armada/instructions/` and `.armada/playbooks/`, and
  ripgrep-based glob and grep tools read git's exclude file, so a glob found no
  briefing files and captains reported the context pack, shared memory and
  handoff files as not staged. Provisioning writes an excluded `.rgignore` at
  the dock root that negates those folders; a checkout that already holds one
  keeps it unchanged.
- An objective write that changes a confirmed stage skip without
  `confirmStageSkip` says that it dropped the confirmation: the returned
  `stageSkip.confirmationNotice` names what changed (the stages or the reason)
  and how to confirm again, and `stageSkip.droppedConfirmation` keeps the
  server's copy of the last confirmed stages, reason and proof. A later write
  that returns the skip to exactly those stages and that reason restores that
  confirmation, with its original confirmer, time and proof. Both fields are
  server-owned and ignore request values. A write with `confirmStageSkip: true`
  issues a new proof and time even for an unchanged skip.
- The compact dispatch preview (`preview_objective_dispatch` with
  `compact: true`, and the REST `compact=true` query) carries `warningCount` and
  `warningCodes`, so a ready preview that still warns, for example
  `gate_inputs_unplanned`, no longer reads as clean.
- The `[claims]` overlap note no longer fires when the dispatcher holds the
  claim. `armada_dispatch` passes the request's participant key to the dispatch,
  and a claim that key holds is not announced; a claim another participant holds
  still is.
- A session behind a shared bridge configuration reads its own mail. The
  coordination read and heartbeat accept a name the header key posted or
  heartbeated under in the last 24 hours as that key, instead of refusing it as
  a different key; a name only another key uses stays refused. A note
  addressed to such a name comes back whole in the read, as a note addressed to
  the key does, instead of previewed at 600 characters. The
  `armada_enumerate` `includeMessage` description names the stored wake text
  and the `armada_coordination_read messageId=<id>` fetch, and a post that
  carries no identity at all is refused with an error naming `authorName`,
  `authorId` and the `X-Armada-Participant` header.
- The token-usage summary (`GET /api/v1/token-usage/summary`, the dashboard
  Activity > Token Usage tab) reads a long window in a few
  statements. It read the mission records of each event-matched mission one
  query at a time and paged the window's token-usage events with a count per
  page, so a month of usage took about as long as the dashboard waits for an
  answer. Mission records are now read in batches and the events in one
  statement. The event read applies the query's tenant and user scope, so an
  administrator's `tenantId` filter also narrows the events.
- The Token Usage tab shows a failed load as an error with a Retry button, not
  as "No token usage for this time range", and keeps the figures on screen with
  an error line when a refresh fails. An auto-refresh tick while a load is in
  flight waits for that load instead of starting another request, so a slow
  answer no longer leaves the tab loading forever; changing the range cancels
  the request for the old range.

- Coordination writes take the caller's identity when the name is omitted.
  `armada_coordination_post` without `authorName`, the heartbeat and claim
  without `displayName`, and `armada_dispatch_hold` engage without `setBy` use
  the participant's last board name, then the name of the authenticating
  credential, then the participant key; a name the caller passes wins. The REST
  message post and presence routes default `AuthorName` and `DisplayName` the
  same way. A call with no identity at all is still refused.
- A wake for an addressed note names the note: its text reads
  `[from=<author>] [msg=<note id>] <note>`, and a note cut at 500 characters
  ends with its full length and the fetch `armada_coordination_read
  messageId=<id>`. A wake banner item cut at 400 characters names the same
  fetch. `armada_coordination_read` takes `messageId` and returns that one note
  whole, and a read that previews broadcast notes carries a `TruncationHint`
  naming `messageId` and `includeFullContent=true`.
- REST GET-by-id routes for voyages, missions and objectives (including the
  backlog routes and their GET sub-routes) accept an abbreviated id, the prefix
  and eight-character creation-time part such as `vyg_mabc1234`, under the rule
  the MCP tools apply. One visible match reads that record by its full id, no
  match answers `404`, and two or more answer `409` `Conflict` with
  `Data.Reason` `ambiguous_id` and the candidate full ids in
  `Data.Candidates`. An MCP `ambiguous_id` refusal lists the same `Candidates`.
- The objective list `search` matches the objective id and any prefix of it,
  on REST (`/api/v1/objectives`, `/api/v1/backlog`, their `enumerate` routes)
  and `list_objectives`.
- A list page size above the limit is reported, not silently clamped. The
  objective, backlog, incident, runbook and runbook-execution lists serve at
  most 500 rows and the entity lists at most 1000; a larger `pageSize` is
  served at the limit and the response carries `RequestedPageSize` and
  `MaxPageSize`, which are absent otherwise.

- Objective preflight question 1 resolves a bare cited file name that matches
  several files of a declared sibling by the rule the target repository uses:
  the one file whose lines hold the citation, an ambiguity Warning when several
  do, and a failure listing the candidates when none does. Such a name is never
  reported as a missing file. Every question-1 fact names the commit it read.

- Objective preflight question 1 reads a citation that fails at a start commit
  off the vessel's default branch again at the branch tip, and passes it when it
  resolves there. A recovery start on a mission branch whose brief merges the
  branch first no longer fails question 1 on files and lines only the branch
  holds; the fact names the branch and commit where each such citation
  resolved, and a failure names both refs it read.

- Re-pinning a preparation anchor to another commit of the same branch marks a
  dependent claim `NeedsRecheck` only when the move changed a path the claim
  cites (a rename or delete counts for its old path), and the reason names those
  paths. A claim whose cited paths are unchanged stays `Verified`. A claim that
  cites no path, an unreadable or oversized change, and a start ref or vessel
  change still mark every dependent claim. The dispatch preview and the write
  path share one changed-path rule.

- A definition-of-done gate's one re-run after a host-load time limit holds the
  host to itself. The re-run asks the host command lock for the whole host
  (`HostWideCommandLock.AcquireAloneAsync`): it keeps its place in line, starts
  when no other build or test command holds a slot, and no other command is
  granted a slot while it runs. A re-run that queued for an ordinary slot could
  start beside another gate of the same vessel and wait out that gate's suite
  lock inside its own time limit, so it timed out exactly like the first run.
  A command still writing output at its time limit (within a quarter of the
  limit, at most 60 seconds) also triggers the re-run, with reason
  `output_progressing`; the result carries `TimedOutWhileProgressing`, the
  timeout message says so, and `BoundedProcessResult.OutputQuietAtTimeout`
  reports the silence. `mission.definition_of_done_load_rerun` is schema version
  3 and adds `OutputProgressing` and `RerunAlone`.
- A gate `Timeout` whose re-run still reads as host load is rescued once. The
  result carries `HostLoadTimeout`, the failure reason's first line carries
  `(host_load_timeout: ...)`, and autonomous recovery dispatches one rescue with
  reason `host_load_timeout_retry:` instead of refusing it as
  `gate_class_not_rescuable`. A plain `Timeout` on a silent command is still
  refused, and a rescue that times out again is not rescued again.
- A reconnecting WebSocket client no longer loses history to one Check log. A
  change event carried the whole Check output (up to 4 MiB), and a frame larger
  than the 1 MiB replay limit emptied the replay history, so a subscription
  that spanned it reported `snapshot_overflow` and the next resume
  `history_evicted`.

- A subscription whose snapshot catch-up was partly evicted continues in cursor
  order: the `event.gap` names the lost cursor range (`lostFromCursor`,
  `lostToCursor`), its time bounds and a `GET /api/v1/events` query to re-read
  it, the snapshot cursor is the last lost cursor, and the retained events
  follow. Before, `stream.ready` pointed back at the watermark, so the first
  live event read as a skipped sequence, the client reconnected, and the
  reconnect hit `history_evicted` for the same lost events.

- The hub watcher (`scripts/autonomy/watch-armada.mjs`) prints a failed or
  stalled state from a reconnect snapshot only when it has not printed that
  state before, so a reconnect no longer repeats every `LandingFailed`
  mission. Its `EVENT GAP` line names the lost range and the re-read query.

- Objective preflight question 1 resolves a cited path in the trees a dock
  provisions beside the repository: a path under a declared sibling's directory
  name (with or without `../`) at that sibling's tip or under its extraction
  artifact paths, a bare file name any declared sibling tracks, and a path
  under an evidence root that applies to the vessel. A declared sibling no
  longer also needs a required sibling input. A miss there is named against the
  sibling or root, and when every failure of the fact lies outside the target
  repository (`FailsOnlyOutsideTarget`) the Jev q1 reading is a warning, so a
  sibling citation no longer blocks dispatch on the model's score alone.

- Objective create and update on REST and MCP (`create_objective`,
  `create_backlog_item`, `update_objective`, `update_backlog_item`) refuse a
  top-level field the request does not accept and name it
  (`objective_unknown_field`, `UnknownFields`; REST 400), instead of returning
  success and storing nothing.

- An objective's confirmed stored stage skip (`preparation.stageSkip`) is a
  default. An entry that names no stage of the chosen pipeline is dropped and
  reported, and the dispatch proceeds: the preview lists it in
  `droppedStageSkips` with a `stage_skip_default_dropped` Warning, and the voyage
  records a `voyage.stage_skip_default_dropped` event. A stored `Recorder` skip
  does not refuse a dispatch on a pipeline without a Recorder. An operator
  dispatch linked to the objective applies the stored default when it sends no
  `skipStages`, as the autonomous scheduler does, so preview and dispatch agree;
  `skipStages: []` skips nothing, and the stored default then neither applies
  nor gates the dispatch. An entry the dispatch names itself is still refused
  with `stage_skip_unknown_persona` when it names no stage, and an ambiguous,
  Judge or no-work default is refused with a message that names the stored skip.

- A Judge PASS that copies an acceptance criterion holding a secret-shaped value
  (for example `token=<value>`) passes the criteria walk. The Judge's output is
  stored redacted, so the walk passes each brief criterion and each claimed line
  through the same shared secret redactor before it compares them. A criterion
  the Judge copied exactly is not read as missing (`does not list every
  acceptance criterion from the brief`).

- Smart Routing reports a persona model list dead
  (`routing.persona_model_list_dead`, reason `persona_models_matched_nothing`)
  only when eligibility admitted idle captains and none runs a listed model. A
  mission whose only eligible captains are busy waits for capacity and records
  no dead-list event on each assignment pass.

- Every build or test command the admiral runs (Checks, Definition-of-Done
  gate commands, merge-queue test commands, workspace exec, the API-endpoint
  `run_command` tool and the self-deploy build) turns off MSBuild node reuse,
  the MSBuild server and the shared compiler server (`UseSharedCompilation=false`),
  the same as captain launches, through one shared rule. A build no longer
  leaves an idle MSBuild worker node or `VBCSCompiler` running in the admiral
  after it ends. A workflow profile variable of the same name still
  wins for a Check.

- A definition-of-done command that hits its time limit while another build or
  test command holds a host command slot at the same time no longer fails the
  mission at once. With more than one host slot, two gates of one vessel can run
  together, and a suite lock inside the vessel's own commands then makes one
  gate's suite wait out the other's inside its time limit, at normal load. The
  gate now runs once more, queuing for the host slot again, and records
  `mission.definition_of_done_load_rerun` with reason `host_slot_shared`
  (payload schema version 2 adds `Reason` and `SharedHostSlot`). The second
  result stands. The timeout message says when another command held a slot,
  and the result carries `TimedOutOnSharedHost`. A time limit on a command that
  held the host alone at normal load still fails at once.
- A pipeline reads by its id as well as its name. REST
  `GET /api/v1/pipelines/{name}` and the MCP and WebSocket `get_pipeline` match
  the value as a name first, then as an id, so the pipeline id a voyage records
  no longer reads as not found. Visibility rules are unchanged: another
  tenant's pipeline still reads as absent.
- A coordination note addressed to the name a session posts under reaches that
  session after it has only read the board for over an hour, and after an
  admiral restart. The address book holds every name a participant key posted
  or heartbeated under in the last 24 hours, from stored notes, presence rows
  and the admiral's own posts and heartbeats. A name several keys use is
  delivered to each of them: each key's filtered `armada_coordination_read`
  returns the note, each key gets its own wake, and `AddressNotice` names every
  key. A filtered read also returns a note already stored under a name the key
  used in that window.
- A revising Worker stage that finds nothing to fix completes instead of
  failing as `no_op_completion_detected`. When an earlier Worker stage of its
  chain, on the same branch and at a lower stage order, already produced and
  handed off a commit, a completion marker with no new commit means no change
  was needed; the activity log says so and the Judge runs instead of being
  cancelled. The first producing Worker of a pipeline or a rescue keeps the
  no-op rule.
- A Worker whose start ref already holds the work completes instead of failing
  as `no_op_completion_detected`. When the commit its dock started from holds
  commits that the vessel's landing target branch does not have (an objective
  that starts from reviewed work not yet landed), a completion marker with no
  new commit means no change was needed. The activity log names the start
  commit, the target branch and the commit count; the next stage reviews the
  branch against the target, and the landing merges the start-ref commits with
  the branch. A Worker that starts at or behind the target, or whose count git
  cannot read, keeps the no-op rule, so autonomous recovery no longer
  dispatches a rescue that could only repeat the same empty diff.
- `ActiveDispatchedCount` in `armada_objective_scheduler_status` describes the
  latest sweep tick, including a sweep that is disabled or paused. It counts each
  Open or InProgress voyage that holds a mission on a vessel once, by the same
  rule the fleet and per-vessel capacity checks use; Complete, Failed and
  Cancelled voyages never count.
- A branch has at most one unfinished merge entry per vessel and target, so a
  pipeline whose stages all commit to one branch gets one entry, not one per
  stage that produced work. An enqueue for a branch that already has an active
  entry attaches to it and returns that entry; while it is still Queued and the new mission is
  a later stage, the entry is retargeted to that mission, whose work is the
  branch tip. Each attach is recorded as a `merge_queue.enqueue_attached` event
  naming both missions. The landing drain skips an earlier stage whose branch
  entry already belongs to a later stage.
- An entry the queue cancels because its work already landed marks the entry's
  mission, and each earlier stage on the same branch still in WorkProduced,
  Complete, as a landed entry does, so the landing drain does not enqueue that
  work again on every sweep. An operator cancel records `Cancelled by operator`
  on the entry's test output.
- A definition-of-done test that fails again in its isolated re-run no longer
  reads "so this is not a load race". The re-run runs on the same loaded host,
  so the failure text says the re-run under the current host load also failed
  and records the host load at the re-run: the 1-, 5- and 15-minute load
  averages, the CPU count, `MemAvailable`, the other gates, the host command
  slot holders and waiters, and the working captains. On a busy host it says
  the failure may still be load-related.
- A context pack header names both commits it draws on: `Indexed commit` for the
  related evidence and the assigned files taken from the index, and `Start
  commit` for the assigned files read at the mission checkout's start commit. A
  related chunk whose file changed between the two is marked as possibly out of
  date.
- The Git Anchors block searches the declared sibling checkouts for a path
  written from a sibling's own root, and names the sibling that tracks it. A path
  that no searched checkout tracks, while a declared sibling could not be
  searched, is stated as a possible sibling path, not verified here, never as
  absent or new work.
- The Git Anchors block checks a cited file name that matches several files
  against every one of them, the same rule dispatch preflight question 1
  applies. In a provisioned sibling checkout the candidates include the files
  in the sibling's extraction artifact directories, not only the tracked files.
  When the cited lines hold in exactly one file the citation holds; when they
  hold in several the block names them as ambiguous; when they hold in none the
  citation is stale and the block lists every candidate. A range is no longer
  reported past the end of a short same-named file when it holds in the copy
  the text means.
- The Git Anchors line-range check, dispatch preflight question 1 and the
  prior-art excerpt read a file stored through Git LFS as its content, never as
  its pointer. The content comes from a local copy whose size and SHA-256 match
  the pointer: the working-tree file, else the object in the repository's LFS
  object store; the LFS remote is never contacted. When no local copy holds the
  content, a citation of the file is named as stored through Git LFS and not
  checked here, never as past the end of a 3-line file; preflight question 1
  lists it as a note and does not fail on it.
- A pipeline handoff past the description budget no longer loses the middle of
  the prior stage's diff, Judge verdict or test list. The complete handoff text
  is kept on the Admiral and staged into the next stage's dock under
  `_briefing/handoff-full/`, and the cut marker names
  `_briefing/handoff-full/01-handoff.md`. The record and the brief keep their
  cut, so the prompt size does not change. The instruction retention bounds the
  kept text, and a mission purge deletes it.
- A brief no longer tells the captain to read a `_briefing/` file the dock does
  not hold. Each such read points at the staged complete handoff text, or at
  the inline text when none is staged.
- A previewed MCP read field ends with ` ... [preview; call again with
  includeFullContent=true for the full text]`, so a clipped objective,
  incident, runbook, prompt-template or campaign-status field names the flag
  that returns it whole. The captain brief's objective section names the flag
  beside `get_objective`.
- A high model reading on dispatch preflight question 9 no longer blocks a
  brief that states its proof value changes, such as "freezes 2 after the fix
  and 3 at the old commit". When no deterministic fact fails and a brief
  sentence states two different values for the corrected and the defective
  code, the reading adds a `preflight_q9_model` Warning that quotes that
  sentence, not an `objective_preflight_model_flag` Error. A brief that states
  the same value on both sides, or a preview with a failed fact, still blocks.
- Dock git excludes take effect. A dock is a linked worktree, and git reads only
  the shared repository's `info/exclude`, so Armada writes its dock entries
  there. Provisioning excludes `_briefing/`, `.armada/instructions/` and
  `.armada/playbooks/` with the MCP, OpenCode and boundary files, before any of
  them is written, so a repository tool that lists or hashes untracked files
  does not read Armada's staged files.
- A brief names the assigned runtime's instruction file in every section built
  from objective or earlier-stage text. Objective Scope uses the same rewrite as
  the mission description. A staged earlier-stage report keeps its exact bytes,
  and `## Instruction File Names in Staged Reports` names the foreign file it
  mentions and the file to read instead.
- A `## Git Anchors` block copied into a mission description from an earlier
  brief is dropped before the brief adds this dock's own anchors.
- A stage whose checkout does not contain the commit its same-vessel
  predecessor recorded records a `mission.start_behind_predecessor` event, and
  its brief adds one line that names both commits. The stage is not held or
  skipped, and an ancestry probe that cannot answer reports nothing.
- Dispatch preflight question 1 passes a citation whose line range lies inside
  the body of the declaration it names, such as `` `Size` (`Book.cs:20-22`) ``
  for lines inside `Size`. A range that crosses into the next declaration, or
  lies outside every declaration, still fails. A high model Q1 reading adds a
  `preflight_q1_model` Warning, not a blocking Error, when the Q1 citation fact
  passes and no deterministic fact fails.
- Dispatch preflight question 1 checks a bare file name that matches several
  files against every candidate. It passes and names the file when the cited
  lines hold in exactly one, warns `preflight_citation_ambiguous` with the
  candidates when they hold in several, and fails with the candidates when they
  hold in none. A failed question names each failing citation as written, with
  its reason.
- A test network call that does not answer no longer hangs the test run.
  Every connect, send, receive, relay and HTTP call that a test awaits runs
  through `TestTimeout.WithinAsync`. A call that is still running after 60 s
  fails its test with a timeout that names the call.

- A Judge verdict held for a criterion ruling no longer reads as a failure.
  The mission, its voyage and the inbox showed `Failed` until someone opened
  the incident, so the voyage read as a real defect. The held verdict stays
  `WorkProduced` under the operator-review hold, the voyage stays open, and the
  inbox names the ruling and the incident.

- A definition-of-done command that names failing tests and then hits its time
  limit fails as `TestFail` with the named tests, and records `TimedOut` as a
  separate fact; recovery no longer refuses its rescue as a host fault. A gate
  due to start on an overloaded host waits until the host is no longer
  overloaded (at most 20 minutes) and records
  `mission.definition_of_done_load_admission` with reason `host_overloaded`,
  so its time limits count run time. The re-run after a time limit also counts
  a run as under load when the 15-minute load average is above the threshold,
  because the 1-minute average falls once the timed-out command exits. A
  command stopped at its limit names the processes still in its process group
  (Linux; for example a `flock` the command was still waiting on), and a wait
  for the host command slot, which never counts against the limit, is logged
  on its own line.
- A Check whose command times out, stops with an error, or is cancelled by an
  admiral restart records its run time, `CompletedUtc - StartedUtc`.
- The automatic Check runner does not start a Build or UnitTest Check beside a
  running definition-of-done gate of its voyage or mission on the same vessel,
  so the same full suite does not run twice at once. The Check waits, its
  `SlotWait.QueueReason` names the gate, and the wait is reported as
  `check.gate_overlap_*`. A gate that passes as a whole records the commands it
  ran in full at a clean HEAD. An automatic Check of the same command, commit
  and environment then passes and names that gate, and does not run the
  command again. A failed, cancelled or timed-out gate records nothing.

- A Check that waits is visible while it waits. The automatic Check runner
  reports three waits: the host command slot held for free memory
  (`check.memory_wait_*`), every slot busy while requests wait
  (`check.slot_busy_*`), and every runner place holding a started Check while
  more Checks are pending (`check.runner_full_*`). Each wait records a start
  event, one every `definitionOfDone.hostSlotWaitReportMinutes` (default 5) and
  an end event, naming the memory reading, the slot holders and the waiting
  requests in order. A wait that lasts
  `definitionOfDone.hostSlotWaitBoardNoteMinutes` (default 10; 0 turns it off)
  posts one board note. A queued Check's `SlotWait.QueueReason` names a full
  runner.
- A Build or UnitTest Check whose isolated checkout is gone when it takes the
  host command slot makes the checkout again at the same source and runs, or
  fails with a reason that names the removed checkout.

- The objective scheduler and autonomous recovery no longer both start work on
  one objective. One rule says what work already owns an objective: an active
  linked voyage, or a Challenger still ruling on a failed Judge of the
  objective. The scheduler skips a requeued row as `recovery_pending` while its
  Judge is challenged, and a rescue is not dispatched when the objective already
  runs a voyage created after the failure; the incident and an
  `autonomous_recovery.rescue_superseded` event say why.
- Test runs no longer fill the shared temp directory. `TestDataDirectory.Redirect`
  points `TMPDIR`, `TMP` and `TEMP` at the run's own root, which is removed
  when the process exits, and the next run removes a killed run's marked root
  once its process is gone. Temp deletes that fail are counted and named on
  standard error instead of being ignored; a path that another runner removed
  first counts as deleted. `ARMADA_TEST_TEMP_ROOT` names
  the directory for the run roots. `server-gate.sh` sends its build temp files
  to `<scratch-dir>/tmp` on disk, keeps the test roots in the host temp
  directory, and compiles without the shared compiler server. A captain launch
  makes its own temporary directory also when the admiral inherited `TMPDIR`;
  only a `TMPDIR` the launch sets itself is kept as is. The disk lifecycle temp sweep also reclaims expired
  `armada-test-home-`, `armada_coverage_`, `armada-tool-profile-`,
  `armada-preview-`, `armada-staleness-`, `selfdeploy-provider-` and
  `MSBuildTemp` entries.

- `armada_dispatch` refuses a full fleet or sibling lane at submission with
  `fleet_capacity_reached` or `sibling_lane_capacity_reached` and accepts no
  job, instead of accepting a job that fails on capacity seconds later. The
  reservation at creation still decides when capacity changes in between.
- A background job that ends `Failed` or `Lost` posts a board note addressed to
  the MCP session that submitted it (`RequestedByParticipantKey` on the job),
  naming the job, its reason and its objective, beside the `job.failed` or
  `job.lost` event.

- The `handoff_outcome` decision judges a finished stage on its own share of
  the work. Its state names the stage's position and the stages still to run,
  and each acceptance criterion gets a `scope_<n>` Noul asking whether only the
  pipeline as a whole or a later stage can meet it. Such a criterion is not
  Mailed or held as the stage's gap, and a `partial` whose every unmet criterion
  is pipeline-level hands off with no hold. A criterion such as "all pipeline
  stages run in order" no longer holds an early stage.

- `armada_handoff_hold` and `armada_review_hold` report every missing or invalid
  argument in one refusal. `Error` names each one, `Problems` lists each code,
  and `Reason` is the single code or `invalid_arguments`.

- Every Judge brief names the `## Acceptance Criteria` walk and its line form
  when the brief lists criteria. The report-only, revised-report and
  implementation role texts and the report-only output contract listed only the
  review sections, so a report-only Judge that followed its brief was refused
  for the walk it was never asked for. `armada_mission_output` holds a finished
  PASS Judge to the same section rule as its verdict check, whatever its status,
  so `MissingReportSections` names the section a refused PASS lacked.

- The Git Anchors block no longer calls a present file absent when the text
  names it with the repository's own directory in front: the path is anchored
  at the file it names. A path with a declared sibling's directory in front, or
  a bare file name a provisioned sibling tracks, is a sibling path whose cited
  lines are checked in that sibling, and a bare name found nowhere is stated as
  not found rather than as absent.
- A captain waiting on its own build or test run is not stopped as stalled. A
  live descendant of the agent process that started after the agent's last
  output (two minutes of slack) clears the stall as the `child_process` signal,
  until `StallChildProcessCeilingMinutes` (default 45, clamped to [0, 1440], 0
  turns it off) measured from that output. The agent process and the companions
  it starts in its first minute never count. The admiral reads the process tree
  from Linux `/proc`; on a host where it cannot, the decision uses the output,
  dock and branch signals alone, says why in the stall event, and logs it.
- Stall recovery saves a captain's uncommitted dock work before the final
  relaunch and before recovery exhaustion reclaims the dock. The work, untracked
  files included and ignored files excluded, is committed on the dock's HEAD in a
  private index and written to `refs/heads/recover/<missionId>-<n>` in the vessel
  repository; the dock's files, index and branch do not change. The
  `mission.work_snapshot_saved` event names the branch, and an exhausted mission's
  failure reason ends with `uncommitted work saved to <ref>`. A snapshot that
  cannot be written records `mission.work_snapshot_failed`; the relaunch still
  runs, and recovery exhaustion keeps the dock on disk instead of reclaiming it.
- The reclaim anchor refs section of `docs/MCP_TOOL_CATALOG.md` states where
  the anchors live: `DockService` writes `refs/armada/docks/<dockId>` and
  `refs/armada/missions/<missionId>` to the vessel bare repository, and nothing
  in Armada pushes them to origin.
- Every dock removal path saves a dock's uncommitted work before it removes the
  checkout: reclaim (and so unstick, stall recovery and mission completion),
  delete, purge, a provision over a previous attempt's or a finished dock's
  leftover checkout, and the disk lifecycle's orphan-dock reclaim. Each uses the
  shared dock work snapshot to `refs/heads/recover/<missionId>-<n>`, and the
  reclaim log line names the branch. When the work cannot be saved the checkout
  stays: reclaim leaves the dock active, delete returns false, purge and
  provision fail with the reason, and `mission.work_snapshot_failed` names it.
  Both snapshot events appear in the mission recovery report.

- Smart Routing persona model lists fall back when they are dead. When no
  captain that may serve the persona runs a listed model and can take work
  (every such captain quarantined, benched or stopping, or none exists), every
  admitted captain is reachable, the reason is
  `persona_models_dead_fallback:<captainId>`, and the event
  `routing.persona_model_list_fallback` records it. A busy listed captain still
  keeps the lists authoritative. Dead-entry reporting checks the whole fleet,
  so a list whose captains are only busy is not reported dead.
- The dispatch preview counts idle capacity as assignment does: the persona
  model-list fallback applies to role coverage, and `idleEligibleCount` leaves
  out idle captains whose account the usage filter removes.
- One captain's provider quota or credit error holds its usage account and the
  account's other captains for at most `accountHoldMaxMinutes` (default 60),
  whatever reset the provider's message names. A run the provider serves on
  the same account ends the hold and releases every captain on the account that
  a usage hold quarantined, recorded as `usage_account.hold_cleared`.
- Parallel assignment passes of one admiral queue for a sibling lane, up to 30
  seconds, instead of deferring. A pass that reserves the lane and finds no
  captain no longer defers the lane's other missions on every tick.
- A stalled captain is relaunched while the dispatch hold is engaged. The hold
  refuses only new dispatches, and a relaunch continues an in-flight mission in
  its own dock, so stall recovery under the hold stops the stalled process and
  relaunches the agent within `MaxRecoveryAttempts` as it does with no hold.
  The stalled mission no longer waits as `Pending` until the hold clears while
  the other stages of in-flight voyages keep running; the
  `captain.recovery_deferred_dispatch_hold` event and the assignment skip for
  deferred missions are gone.

- `armada_retry_landing` runs the landing as a background job and returns the
  job at once, so a landing that outlasts the MCP client deadline no longer
  reads as a failure while it still runs. Read the outcome with
  `armada_job_status`. One retry per mission runs at a time: a repeated MCP
  request returns the running job (`Joined: true`), REST
  `POST /api/v1/missions/{id}/retry-landing` refuses a second retry with `409`,
  and the landing service refuses one from any other caller, so the same work
  never lands twice. A job names the record it acts on in `SubjectId`.

- A Challenger reads the evidence its brief names. The challenged Judge is the
  Challenger's direct evidence source when the Judge's
  `judge_challenge.dispatched` record names the Challenger, so it reads that
  Judge, the Judge's voyage and the reviewed Worker's report, and the objectives
  the voyage links. The brief's `Judge mission:` line alone grants nothing, and
  every record still needs the caller's tenant and owner.
- A voyage stays in progress while a retried landing of one of its missions
  runs. The retry resets the mission to `WorkProduced`, which counts as done, so
  a completion sweep during the retry read the voyage `Complete` before the work
  was on the target branch, and the terminal-voyage reconciler then cancelled
  the mission whose work was still landing. The completion rule keeps the
  voyage (`landing_retry_in_flight`), the reconciler treats the retry as a
  landing in flight, and the landing-drain sweep skips the voyage instead of
  enqueueing the same work again. When the retry ends, it applies the
  completion rule itself: a landed retry completes the voyage and a failed one
  leaves it `Failed`.
- Judge findings exclude items the Judge reports as agreeing with the
  source. A list under a lead-in such as "Spot checks that agree with source:" is
  context, the same as a list under a passing or reviewed lead-in, so neither the
  challenge round's numbered findings nor the rescue brief's blocking findings
  carry it. Explicit blocking items, items under a lead-in that names a defect or
  asks for a correction, and NOT MET criteria are still findings.
- The `handoff_outcome` hold holds only a delivery stage (a `Worker`, or a stage
  with no persona). A Test Engineer, Linter, Recorder or review stage reads
  `partial` against the objective's acceptance criteria because it does not build
  them, so it keeps the partial Mail and its next stage starts without waiting for
  an operator.
- A Challenger mission never lands the work it reviews. A Challenger is
  dispatched outside a voyage at the commit a Judge blocked, so its completion
  took the normal landing path and merged that commit onto the vessel's main
  branch. Every landing entry point (completion, review approve, hold release,
  retry landing, merge queue enqueue and the landing drain) now consults one
  rule, `MissionLandingEligibility`, which refuses a Challenger and any read-only
  (`Audit` or `Research`) mission dispatched outside a voyage. Completion of a
  refused mission records its result, sets it `Complete` and emits
  `mission.landing_skipped`; enqueue throws, retry landing returns false and the
  landing drain skips it.
- The pass that dispatches a Challenger opens the Judge failure's incident with a
  pending-review note and posts one broadcast board note, "Challenger review
  pending for Judge <id>; rescue will follow", so an operator does not rescue the
  failure by hand while the Challenger rules.

- An Architect mission block that writes its brief on one line,
  `description: <text>`, keeps that text. The fallback block parser treats the
  value as the first line of the mission description and strips only the
  front-matter keys around it (`id:`, `title:`, `preferredModel:`,
  `dependsOnMissionId:`). A `description: |` block body reads as before.
- The mission change summary answers for every pipeline stage after its dock is
  reclaimed. When a stage is assigned, Armada writes the commit its dock sat at
  to a stage start record keyed by mission id (`<logDirectory>/stage-starts/`),
  which the dock reclaim leaves in place and `diskLifecycle.dockMetadataRetentionDays`
  expires. The summary reads that record first, so a chained Judge, Linter or
  later Worker lists only its own changes with `StartCommitSource`
  `stage_start_record`. A dock that names no full commit records nothing, and
  the stage still answers `start_commit_not_recorded`.
- A Judge PASS rejected by a failed Check names the failing tests. The
  rejection reason, and the incident that copies it, carries each rejecting
  Check's test-outcome digest (every failing test with its first exception line,
  and the skips) read from the whole log, ahead of the output tail. A failure
  printed in stderr or far above the last lines is no longer lost. The digest
  reads the output with the failed-test line pattern of the workflow profile the
  Check recorded, and is redacted and bounded like the tail.
- The readiness and dispatch-preview command probe skips a leading `NAME=value`
  environment assignment whose value is a path (for example
  `JAVA_HOME=/usr/lib/jvm/... bash ./gradlew build`) and probes the program after
  it. A name that starts with a digit is not an assignment.
- `armada_mission_code_search` binds a captain credential to the mission it was
  issued to. A supplied `missionId` naming another mission is ignored and named
  in `Warnings`, the search runs on the credential's own vessel, and the call
  is charged to the credential's own mission budget, also when the credential's
  owner is an administrator. The credential's own mission must have its tenant
  and owner. A caller without a mission credential keeps the operator policy:
  an administrator may name any mission, and any other caller only a mission
  in its own tenant.
- Git ancestry reads the exit of `git merge-base --is-ancestor` at the process
  boundary. `TryIsAncestorAsync` answers true on exit 0, false on exit 1 with no
  error text, and unavailable (null) on exit 128, a missing ref or object, a
  corrupt history, an exit 1 that reports a read error, a timeout or a process
  that cannot start. Caller cancellation reaches the git process and
  propagates. The terminal-voyage reconciliation keeps a mission whose
  ancestry query fails as unknown ancestry instead of failing it as unlanded
  work, and manual completion proof reports a commit missing from the
  repository as `manual_completion_ancestry_unknown`. `IsAncestorAsync` stays
  true only on proven ancestry.
- The bounded check-run view that `get_check_run`, `run_check` and
  `retry_check_run` return reads failing test names in `testOutcomes` with the
  `failedTestLinePattern` of the workflow profile the check run recorded, as
  the definition-of-done gate does. A vessel whose runner prints
  `FAIL <name>: <message>` gets its failing tests named in the check run
  summary. A run with no recorded profile, or a profile without a pattern,
  reads the output with the built-in matchers only.
- A read-only (Audit or Research) brief whose objective has an acceptance
  criterion that requires a build or test run (run the tests or a suite, class
  runs, `N/N pass`, `0 failures`, a build or test command) lets that criterion
  win over the read-only default against running builds and tests. The rules
  section tells the captain to run the existing build and test commands the
  criterion needs and quote the totals, to change no tracked file and commit
  nothing, and to report the conflict and the rule it followed. A read-only
  brief with no such criterion keeps the default rule.
- The objective dispatch preview flags each stage whose duty conflicts with the
  objective: a read-only dispatch whose criteria require a build or test run,
  and a TestEngineer stage on an objective whose non-goals forbid test edits.
  Each `stage_duty_conflict` warning quotes the conflict and names the rule
  that wins (the criterion, or the non-goal), which is the rule the brief
  states. The warning does not block dispatch.
- A rescue of a rejected stage states that the review decides what the rescue
  changes. Text carried from the original description or objective that says
  the work is already done or must not be re-implemented keeps the work already
  present, but does not cancel the revisions the blocking findings require. A
  required revision that an objective non-goal forbids is not made, and the
  rescue reports the conflict.
- A captain stop's kill exit is recorded as the stop, not as an out-of-memory
  kill. Every captain stop (an operator stop or recall, a mission or voyage
  cancel, stall-recovery exhaustion, a shutdown) registers the process as a
  requested stop before it stops it. The process's non-zero exit, usually exit
  137, is then recorded as `captain.process_exit_ignored` naming the stop, and
  the mission is not requeued as a transient resource failure: the path that
  requested the stop sets the mission's next state (a recall fails it, a cancel
  keeps it cancelled). A stop that found no running process, or was refused,
  withdraws its registration. An exit 137 that no Armada stop explains is still
  classified as a probable out-of-memory kill and requeued.
- The Git Anchors block checks every line range a brief cites. A bare file
  name cited with lines (`Name.cs:12-20`) is resolved against the files the
  dock's start commit tracks: a unique match is anchored at its tracked path,
  several matches are reported as ambiguous, and none is a stale citation. A
  citation into a declared sibling checkout (`../Sibling/src/Name.cs:96-111`)
  is resolved and read at that checkout's provisioned HEAD; one that names an
  undeclared or unprovisioned sibling is listed as unchecked with the reason.
  A citation's symbol is the backticked identifier written beside it, or the
  one backticked identifier or call expression (`Math.Min(...)`) in the same
  sentence when that sentence cites nothing else, and it is matched as a whole
  word. A range that no longer holds its symbol is listed under "Cited line
  ranges to recheck" with the symbol's current line.
- The autonomous objective scheduler and autonomous rescues stage each
  mission's own context pack through the same seam as operator dispatch; a
  rescue's later stages read the Worker's pack. A pack holds the files the
  mission brief names under `## Assigned files` ahead of ranked evidence, keeps
  ranked evidence only when it touches an identifier or file the mission names,
  and holds one `No relevant indexed chunk:` line when nothing does. The header
  names the start ref and states in a `Commit check:` line whether the indexed
  commit is the start commit; an assigned file changed between them is read
  locally at the start commit. A mission whose pack is not staged gains a
  `Context pack: not staged. Reason: ...` description line and a
  `code_context.not_staged` event.
- Autonomous recovery dispatches no rescue in the producer for a
  definition-of-done `TestFail` on a declared consumer's test suite
  (`consumer_tests_failed: <consumer>`). The gate runs a consumer's suite only
  after the producer's own build and tests pass, so those tests live in the
  consumer's repository, and a producer rescue could only weaken the producer
  change. The decision reason is `consumer_test_failure_not_rescuable`, and the
  incident names the consumer vessel, its id and the failing consumer tests. A
  failure in the producer's own suite is still rescued.
- A cancelled rescue leaves its failure's incident open. The incident lifecycle
  closed the incident as superseded when the latest rescue was cancelled, while
  the failure still needed recovery. It now keeps the incident's status and adds
  one recovery note that names the rescue and quotes its cancel reason (for an
  operator voyage cancel, `Voyage cancelled by operator.`). A cancel of the
  failed mission itself, or of its own voyage, still supersedes the incident.
- An automatic incident close or rollback leaves `RootCause` unset when it
  holds only the text the incident opened with, so a closed incident never
  shows the opened reason as its root cause. `OpenedReason` keeps that text.
- The signals table has an index on type, read flag and creation time
  (`idx_signals_type_read_created`) on every provider. The mission mailbox and
  the stored-wake reader ask for the unread signals of one type, newest first,
  with a count. That count scanned the whole table and the page walked the
  creation-time index past every other signal, so each poll read every signal
  ever recorded, most of them unread Progress signals. Both now search the new
  index and read only the unread signals of the asked type.
- The history timeline (`GET /api/v1/history`, `POST /api/v1/history/enumerate`,
  the dashboard History page) reads missions, check runs, deployments, events
  and request history newest first, at most 2000 records from each source, and
  reads missions without their description, diff snapshot and agent output. A
  source holding more records is named in the new `TruncatedSources` list, and
  only its newest records are in the page and in `TotalRecords`. Incident and
  runbook-execution entries read only their snapshot events instead of every
  event. A history read without a time window loaded every mission with its
  diff, every event three times and every request-history row, and grew the
  admiral by about 10 GB until the out-of-memory killer stopped it. A mission
  entry's description is its failure reason. With an objective or mission
  filter, the named missions are read by identifier, so the story stays
  complete.
- A voyage-armed Check waits while a stage that reports Testing or Review is
  still running. A captain reports those statuses as progress and can still
  commit, so a Check that ran then measured a commit the stage replaced, was
  superseded, and ran again. A stage that reached Review through completion
  carries a completion time and no longer holds the Check back.
- The autonomous-recovery and incident-lifecycle sweeps find a failure's rescue
  missions by parent mission. They read only the matching mission summaries on
  each tick instead of every mission of the vessel, which kept the database
  busy with large reads several times a second. The mission recovery report
  uses the same lookup.
- The incident-lifecycle sweep reads matching check runs without their output
  logs, and the failed-check count reads one row and the total instead of up to
  500 runs with their logs.

- The `SlotWait.Guidance` text on an unresolved Check no longer claims the host runs one
  suite at a time. It says the host runs a limited number of full build or test suites at
  once, which holds for any `definitionOfDone.hostCommandSlots` value.

- The brief byte-budget backstop keeps the role text at the head of a mission
  description it shrinks. A description already bounded with its pinned
  acceptance criteria is shortened in its narrative only, so the criteria no
  longer take the head position and push the role text out.
- Autonomous recovery writes `autonomous_recovery.capacity_deferred` and its
  `rescue_capacity_deferred:` incident note once per change of refusal code or
  limit, instead of once per sweep while capacity stays full; each refusal is
  still logged with the number of rescues waiting. A failure whose incident an
  operator set to `Mitigated` with a written root cause, at or after the
  failure, gets no rescue and one `autonomous_recovery.skipped_mitigated_incident`
  event; a restart or a newer failure is recovered as usual.
- Readers that scan many Checks no longer load each Check's whole output log.
  The history timeline, the production summary, the fleet reconciliation
  snapshot, the status counts and the disk-lifecycle live-Check scan list
  Checks without the `output` column, so the admiral no longer holds every
  stored log in memory at once when the History page opens with no time bound.
  `CheckRunQuery.IncludeOutput` (default true) selects this for any listing,
  including `POST /api/v1/check-runs/enumerate` with `"includeOutput": false`.
  A Check read without its output cannot be written back with an update, so a
  listing never erases a stored log.
- `armada_enumerate` with `entityType=checks` does not read the `output`
  column unless `includeTestOutput` is true, so a page of up to 1000 Checks no
  longer loads every log only to drop it. `outputLength` comes from the
  database's length of the column. `GET /api/v1/check-runs` accepts
  `includeOutput=false`, both REST listings then return `outputLength`, and the
  dashboard's Check lists use it.
- The production summary reads only the Checks created from 90 days before its
  window's start, bounded in the database query, the same lookback as its other
  slice evidence. It no longer reads every stored Check.
- A dispatch preview whose blocker exists but whose stored row cannot be read
  (for example a backlog state that is not a defined value) reports
  `objective_dependency_unreadable` with the stored-data error naming the
  blocker and the field to repair, instead of reporting the blocker as missing.
  The blocker still blocks, and the scheduler counts the code as
  dependency-blocked.
- Releasing a held mission that has dependent stages, by an operator clear or by
  a passing definition-of-done gate re-run, releases the mission's dock after the
  handoff, as completion does, so the next stage can check out the branch.
  `armada_review_hold` refuses `clear` with `gate_rerun_running` while the
  mission's gate is re-running on its dock; the re-run releases or fails the
  hold itself. A stage whose inherited branch is still checked out in the dock
  of an earlier stage of its own chain waits with the assignment hold
  `predecessor_dock_held` instead of failing through the held-dock retry bound.
- An autonomous rescue that re-runs its review stages checks fleet and
  sibling-lane capacity before it writes its rescue voyage, so a full lane no
  longer leaves an empty cancelled voyage on every recovery sweep. The check
  asks the admiral (`IAdmiralService.ProbeCapacityAsync`), which applies its
  own admission rule through `FleetCapacityAdmission.ProbeAsync` without
  reserving. A rescue voyage refused for capacity after it was written, before
  any mission, is deleted.
- The isolated-checkout, commit-count and Slop-execution suites report each of
  their git-backed cases as a named skip when git is not on the path, so the
  runner counts the skips instead of reporting suites that ran nothing.
- An operator's `run_check` or `retry_check_run` runs to completion even when
  its voyage has ended; the automatic Check runner neither discards nor runs a
  Check an operator is executing. Every discarded Check writes its reason into
  its summary and output and records a `check.discarded` event.
  `retry_check_run` follows the `run_check` bounded wait: a retry still waiting
  for the host-wide slot returns unfinished with `slotWait` instead of blocking
  until the client's tool timeout. A retry that ends Canceled returns at once
  with the record and the cancel reason in its summary; the MCP Check Run Tools
  suite covers it.
- The WebSocket idle-session test runs in under a second. It gives its own
  server a 300 ms authentication window and asserts that the refusal arrives no
  earlier than that window and well before the 15-second production window,
  which stays the default for every other session.
- The agent-runtime log tests wait for the runtime to finish handling the
  agent's exit (output drained, final message written, log closed) instead of
  a fixed 2.5-second pause, so a log assertion never reads a half-written file
  and the tests no longer wait when the agent exits at once.
- The runtime test named for stdin encoding asserts it: a launch that does not
  redirect stdin carries no stdin encoding, and the agent still starts.
- The code-index staleness suite reports each of its cases as a named skip when
  git is not on the path, so the runner counts the skips instead of reporting a
  passing suite that ran nothing.
- The Harbor transport tests prove that the Admiral sent a runner nothing with
  an ordering probe instead of a 400 ms quiet wait. The fake runner repeats its
  handshake, and the link answers with one `harbor_handshake_duplicate` error
  after every frame it sent earlier, so the first frame back must be that
  answer.
- `armada_enumerate` scopes every entity type by its per-id read rule: a
  global administrator lists every record, a tenant administrator its tenant's
  records, and any other caller its own records, with playbooks tenant-wide. A
  mission caller lists as its owner without administrator rights, and its
  mission and voyage listings match what the status reads return. An undeclared
  argument is refused by name instead of being ignored, so a misspelled filter
  never returns an unfiltered listing.
- On a voyage that splits into lanes, each Judge's Check gate reads only its own
  lane's records. Another lane's green no longer reads as stale, and another
  lane's red no longer rejects it. The supersession re-arms a stale record at
  its own lane's commit instead of moving records between lanes. A done lane
  runs its Checks while another lane still works, and a lane with no record of
  an armed Check type gets one stamped at its own commit. Voyages that do not
  split behave as before.
- A definition-of-done gate holds the host command slot from its first command
  until it ends. Its unit tests and each consumer build and test follow its
  build directly instead of going to the back of the queue for every command,
  which made one gate wait behind all newer work once per command. A Check or
  merge-queue test that asked before the gate began still runs first; one that
  asks while the gate runs waits for the rest of the gate.
- A voyage-armed Check runs once the voyage's work is final: a stage has
  committed and no stage that can still commit is Pending, Assigned,
  InProgress, or in Testing or Review without a completion time. The Judge, personas whose normal result is an empty diff, and
  read-only missions do not count, so the Check still runs before the Judge
  decides. A Check no longer spends the host command slot on each intermediate
  stage commit only to be superseded by the next stage.
- A brief whose runtime cannot widen its workspace roots names
  `grep -rn <pattern> <sibling path>` for a sibling search; the captain image
  ships `grep`, not `rg`.
- `GetMission_ReturnsAllProperties` accepts a mission the scheduler assigned
  before the read, so it no longer fails when a captain is idle.
- A downstream stage reads the complete report of every earlier stage its brief
  cites. The full redacted output is written to `_briefing/handoff/<mission id>.md`
  in the dock, with the digest the brief states, and the handoff preview names
  the file. Another owner's output is never staged.
- Briefs no longer give two conflicting orders. The Judge acceptance-line form
  uses ASCII (`-- MET.`), matching the ASCII-only rule, and the commit
  instructions name the trailers as the one place Armada ids belong, so a rule
  against ids in committed content covers files, not trailers.
- A rescue of an objective whose non-goals forbid test edits skips its
  TestEngineer stage and records the skip on the rescue voyage, unless the
  failure names the TestEngineer.
- The Linter verifies with the repository's check script (for example
  `scripts/check.sh`) in full when one exists, and a Codex brief names the shell
  commands the Codex runner accepts for temporary-file cleanup and digests.
- A captain whose vessel declares siblings now sees each sibling's provisioned
  HEAD, any uncommitted changes, required paths the checkout lacks, and a
  sibling that provisioning did not produce, in a Sibling Checkouts brief
  section. A sibling input can pin a commit: the brief lists the assigned paths
  changed since an ancestor pin and states that HEAD controls, and dispatch
  preview refuses a pin that does not resolve or is not an ancestor of the
  branch docks provision. Claude Code, Codex and Cursor captains get each
  provisioned sibling as an extra workspace root (`--add-dir`); other runtimes
  get the shell search command in the brief.
- A brief no longer names a host path a captain cannot open: the
  `briefPathRewrites` setting (empty by default, hot-reloaded) rewrites a host
  path prefix in the mission description and the objective scope to the path
  the captain resolves, such as a container mount.
- Git anchors and objective preflight question 1 resolve a cited directory and
  a path spelled in another letter case, so a present test project is no
  longer reported absent as new work. A path written with a trailing slash
  resolves the same way. A name that matches several tracked paths is reported
  as ambiguous, and an absent path cited with line numbers as a stale
  citation. Both read each cited line range and flag one past the end of its
  file or one that no longer holds its named symbol, with the symbol's current
  line; question 1 names a case-only miss with the tracked spelling.
- Two unit tests no longer fail on a loaded host. The stale-job reap test waits
  for its operation to start instead of sleeping a fixed 30 ms, and the relaunch
  test accepts a heartbeat stamp that the new process records after the launch.
- A finished Test Engineer or Product Manager report that lacks a section its
  persona template requires, such as a progress note in place of the report, is
  no longer a complete output artifact. `armada_mission_output` reports
  `report_contract_missing` and names the missing sections, and the next stage's
  handoff lists them as an evidence gap. Every page also carries `PageSha256`
  and `PageUtf8Bytes` for its own content beside the joined-content digest and
  length.
- A failed definition-of-done command, and the rescue brief built from it, names
  every failing test with its first exception line and every skipped test, read
  from the whole standard output and standard error. A bounded tail used to drop
  a failure that printed early or on standard error and named only the last few
  skips. A skip list over 100 gives the count and where the full log is. Check
  run views carry the same list as `testOutcomes`.
- Dispatch no longer stages the vessel's generic baseline context pack for a mission. When the
  pack for the mission's own goal fails or times out, dispatch stages a lexical-only pack for that
  goal instead, read from the local index without a query embedding, graph expansion or summary.
  Every pack header now names the covered roots as well as the goal and indexed commit, and a
  summarized pack keeps the header above its summary.
- A captain brief names the assigned runtime's instruction file even when a
  handoff written on another runtime named `CODEX.md`, `CURSOR.md` or `MUX.md`,
  and lists under `## Named Files Not Staged` each `_briefing/` path the
  description names that the dock does not hold. Objective Scope names the
  objective id.
- A captain brief says that printing an `[ARMADA:NOTE]` line posts a board note,
  so captains no longer look for a board tool, and that an `unavailable`
  typed-decision answer is not a papercut. It names `armada_check_prior_art` as
  unavailable on a vessel where the tool is off.
- Objective Scope states that a non-goal wins over a conflicting role, rescue or
  captain duty, and tells a TestEngineer whose objective forbids test edits that
  it is validate-only.
- A captain reads the records its brief cites. Captain evidence reads follow the
  objectives that authorize the mission or its direct sources, with their
  ancestors, blockers and children, the ids those objectives cite, and the
  missions and voyages they list. A voyage-less review stage can read the
  voyages of its objective and of its direct dependency through
  `armada_voyage_status`, whose counts include mission rows stored without a
  user. `armada_enumerate` lists a mission caller exactly the objectives
  `get_objective` returns. A refused read carries `Reason` (`out_of_scope` or
  `not_found`) without revealing another owner's record.
- The admiral image carries the captain study tools `pdftotext`, `monodis` and
  the Python `dnfile` package, so a study can read PDF sources, disassemble IL
  and walk .NET metadata without hand-written decoders. It also carries `rg`
  (ripgrep), `python` (as `python3`) and the pinned `slopwatch` .NET tool that
  the .NET captain skill runs. A running deployment gets them on its next image
  rebuild.
- Staged AI-Memory files are stable for a whole voyage. The first slimmed brief
  in a voyage pins its memory files, and every later stage receives the same
  bytes, with `_briefing/memory/manifest.json` giving each file's bytes and
  SHA-256. A section that holds only its heading is no longer indexed or staged,
  and the slimmed memory section names only the dock-local memory folder, not
  the host memory root that a dock cannot resolve.
- A fresh SQL Server database migrates past v108. The statement that drops the
  pipeline stage optional flag's default constraint builds its `ALTER TABLE` in
  a variable, because `EXEC` does not accept an expression.
- A captain can omit the id on `armada_mission_status`, `armada_mission_output`
  and `get_objective`. They read the captain's own mission, or the objective that
  authorizes it, instead of failing with a null-id error. A caller with no
  mission credential gets an error that names the missing argument.
- A captain's typed-decision tool call is recorded on the mission its credential
  names. A `missionId` argument that names another mission, such as the
  Architect of the same voyage, is ignored and logged, so the call is no longer
  stored on that other mission.

- A PortingReferenceAnalyst stage holds the mission branch in its dock, because
  it commits parity fixes; it no longer starts detached and leaves its captain
  to move the branch by hand. The no-push mission rules tell a captain in a
  detached checkout to commit on HEAD and leave branch refs alone, since Armada
  moves the mission branch to that commit when the stage ends.

- Rescue briefs carry only a review's real defects. The blocking-findings list
  no longer reads "Minor, not blocking:" or "Observed but not blocking:" items
  as blocking, leaves out items that disclaim a defect, close "Reviewed and
  tested." or "Cosmetic.", or report agreement without naming a defect, and
  keeps the paragraph after a required list out of that list. It now carries
  bold defect labels, paragraphs under a "Confirmed defect:" lead-in, and each
  inline "(n)" change in a Verdict paragraph as its own finding. When a long
  review is reduced, NOT MET criteria from the omitted sections stay in the
  embedded feedback and the note names the reduced sections.

- The PostgreSQL and SQL Server binders name `memory_proposals.promoted_utc`,
  so the startup schema guard accepts the promotion column. A unit test now
  compares every timestamp column a PostgreSQL or SQL Server migration adds
  with the binder's form for it, so a missing entry fails the suite instead of
  stopping the admiral at startup.

- Dispatch stages a context pack built for each mission's own goal. Before, every
  dispatch on an indexed vessel staged the vessel's cached baseline pack, which
  answers a generic architecture question, so captains reported packs that did not
  cover their files. The baseline is now a logged fallback for a goal pack that
  fails, times out or returns no file. The captain brief tells captains to record a
  pack miss in the `Pack:` line instead of a papercut.
- The nine typed decisions the owner switched off after the four-week
  observation review (`inbox_triage`, `stage_necessity`, `prior_art`,
  `revision_kind`, `memory_record`, `memory_review`, `memory_candidate`,
  `flake_score`, `owner_digest`) ship `Off`, so a settings reset or a new
  install no longer turns them back on. Each stays wired; live settings that
  name a mode keep it.

- A rescue runs the stage list its failed voyage was dispatched with, not the
  pipeline as it is at rescue time. Every dispatch records its resolved stage
  list as a `voyage.pipeline_recorded` event, and a rescue anchors on that
  list, so editing, deactivating, deleting or re-pointing a pipeline while a
  voyage runs no longer moves the rescue to the wrong stage. A voyage with no
  recorded list is rescued from the live pipeline and records a
  `rescue.live_pipeline_used` event. `update_pipeline` and `delete_pipeline`
  report `RunningVoyageCount`, the running voyages that keep the old stages.

- `GET /api/v1/status` reads the objectives once and the incident snapshots
  once to build its objective, backlog and incident counts. It read every
  objective once per status and backlog value and every incident snapshot
  once per status and severity value, which made the call take several
  seconds on a large store. The counts are unchanged.

- A rescue of a failed Judge, Linter or TestEngineer no longer hands its Worker
  the failed reviewer's role text as the original mission description. The
  rescue brief removes every persona role section from that description and
  says it did.
- The later stages of a rescue chain no longer get only pointers and a fixed
  "resolve the reviewer feedback" sentence. After a reviewer rejection they
  carry the blocking findings to check; after any other failure they carry the
  failure reason.
- Report essentials read blocking findings under a Linter's `Code
  Correctness` and under `Findings`, `Required Changes`, `Blocking Findings`
  and `Blocking Issues`, and under whole-line bold section labels. A
  `NEEDS_REVISION` or `FAIL` verdict with no readable finding is marked
  incomplete instead of reading as no findings.
- The shared E2E test that lists Open voyages no longer fails when the
  fixture's idle Worker captain dispatches one of its voyages before the list
  call. It checks the filter against each voyage's status after the call
  instead of counting on two voyages staying Open.

- An admiral restart no longer leaves a finished mission held for good when it
  kills that mission's running or queued definition-of-done gate. The health
  loop re-runs the gate: a pass releases the hold and hands off or lands, a
  failure fails the mission into recovery, and a mission whose dock is gone
  stays held and says why. The orphan-dock reaper keeps the dock of a produced
  mission whose gate has no result, so the re-run still has the work to test
  after a restart. `armada_dispatch_hold` `status` and `engage` also
  list `RunningGates`, `GateReruns` and `HostSlot`, so a deploy can wait until
  no gate or check is in flight.
- A Research or Audit mission of an operator-defined persona gets a
  report-only role header and its persona template as guidance. Before, such a
  brief carried no role text and no output contract at all.
- The partial-outcome note that lists unmet acceptance criteria tells only an
  Implementation Worker or TestEngineer to complete them. Every other stage,
  including every Judge and every Research or Audit stage, is told to check
  them, report each one still unmet as a finding, and not do the prior stage's
  work itself.
- A captain brief whose acceptance criteria fill the 12,000-character metadata
  cap keeps at least 8,000 characters of mission context beside them: the role
  text at the head and the newest handoff at the tail. Before, such a brief
  carried only the criteria and an elision marker.
- A Research or Audit Worker that ends `[ARMADA:RESULT] COMPLETE` while its own
  report states that its acceptance criteria are not met now fails with a
  `report_incomplete:` reason that quotes the statement, and the reviewer
  stages after it are cancelled. Before, every reviewer ran and the Judge
  failed the voyage for the same reason.
- A pipeline stage's own description now reaches its captain: dispatch appends
  it to the stage's mission description as a `## Pipeline Stage` section.
  Before, only an internal decision read it. Pipelines whose stages carry
  descriptions show them to captains from this change on.
- In a report-only pipeline, a Worker stage after an earlier Worker in its own
  chain is told to revise the root report instead of writing a new one, and a
  Judge after that revision judges the revised report and checks that each
  reviewer finding was addressed.
- A rescue brief's blocking-findings list leaves out an item whose closing
  sentence only labels it lesser ("Follow-up.", "Follow-up, not a blocker."),
  the same as an item with a parenthetical "(not a blocker)" label. A natural
  rescue listed two such follow-ups as findings to fix.
- A read-only (Audit or Research) brief no longer contradicts itself. Captain
  instructions that order commits are followed by a line that gives the
  read-only rules precedence. A Product Manager or Usability Engineer gets a
  report contract instead of a required specification, so a mission that asks
  for findings, or forbids a specification, has one consistent contract.
- An MCP tool argument sent in snake_case or kebab-case (`mission_id`) is
  renamed to its schema property (`missionId`), unless the caller also sent
  that property. Such a call used to answer `Mission not found`.
- The mission-output handoff points captains at the `armada_mission_output` MCP
  tool only, with its PascalCase field names (`NextOffset`, `HasMore`). It
  named a REST route with no host or port, and captains called it on the MCP
  port. The tool description uses the same field names.
- Docs: `MCP_API.md` states the `Accept` header a `POST` must send and the `406`
  answer without it; the `REST_API.md` log examples use the REST port 7890.

- A mission caller can read missions from the other voyages of its own
  objective. A Judge in a later voyage got `Mission not found` for a mission of
  an earlier voyage, because the evidence scope used objective links only for
  objective reads. One objective with the caller's owner must list both
  missions, by voyage or by mission, and the target must have the same owner;
  missions of other objectives stay out of scope.

- A pipeline that names one persona at two stages (for example Worker, analyst,
  TestEngineer, Worker, Judge) is rescued at the right stage. A rescue anchors
  on the last stage of its persona before the failed stage, or at the failed
  stage when the rescue has the failed persona, and takes that stage's order,
  tier and review settings; a Judge rejection is rescued at the later Worker
  and re-runs only the stages after it, instead of rebuilding every stage after
  the first Worker. A dependant of a cancelled
  stage waits on the rescue stage at the same stage order. A `skipStages` name
  that is the persona of more than one stage is refused with
  `stage_skip_ambiguous_persona` instead of dropping every such stage, and a
  rescue does not carry such a skip.

- Terminal-voyage reconciliation completes an Audit or Research mission that
  recorded no commit, with reason `terminal_voyage_report_only`, under any ended
  voyage. It no longer marks the finished report `Failed` or `Cancelled` with
  `terminal_voyage_no_commit`. A read-only mission that recorded a commit keeps
  the landing-evidence rule. Mission summaries, reconciliation items and the
  `mission.terminal_voyage_reconciled` event carry the mission mode.

- A Judge PASS that the acceptance-criteria check refuses on line shape alone
  (a table, evidence on the next line, a missing criterion line) runs the same
  Judge once more in place with the check's reason and the exact accepted line
  form, instead of failing the mission and starting a Worker rescue. A NOT MET
  criterion still fails at once. The build prompt states the exact line form,
  and a criterion listed twice in a brief is covered by one line.

- A report-only (Audit or Research) pipeline stage reviews the root report
  itself. Each stage received only the stage it depends on, so a Judge after a
  Linter never saw the Worker report, parallel reviewers lost each other's
  findings, and a long report reached the next stage as a head-and-tail preview.
  A report-only dependent now gets an `## Earlier Stage Reports` section with the
  root report whole and every earlier reviewer's findings, the prior report is
  inlined whole up to 24,000 characters, and the description budget is 80,000.

- The rescue findings list no longer shortens a Judge's required changes to make
  room for passing notes. Items under a lead-in that reports passing or reviewed
  behaviour, items under a minor or follow-up lead-in, and items marked not a
  blocker are left out; when the list is still over its bound, plain items that
  name no defect are dropped before any named defect is shortened.
- The disk lifecycle no longer counts a symlinked merge-queue or integration
  worktree entry as reclaimable. The scan followed the link, counted the
  target checkout's bytes, and every reconcile then logged a refused removal.
  Such an entry is now recorded `skipped` as a symlink, as docks already were.
- Autonomous recovery no longer reopens a failure whose incident is closed.
  A rescue deferred by a dispatch hold was kept only in memory, so when the
  hold cleared or the admiral restarted, a failure an operator had already
  closed got a new incident and a duplicate rescue. An incident `Closed` at or
  after the failure now ends recovery for it, with one
  `autonomous_recovery.skipped_closed_incident` event.
- The disk lifecycle reclaims operator evidence: clones and build outputs in a
  folder idle for 24 hours (6 hours when free space is low), keeping receipts,
  logs and any folder marked `.armada-keep`. Whole-folder removal is opt-in
  through `operatorEvidenceRetentionDays`. Folder sizes and ages now include nested files, and the
  report carries the free space it measured.

- The rescue findings list carries a Judge defect written as a paragraph under
  Correctness, Failure Modes or Tests, not only list items, so a rescue sees a
  required revision or a missing proof that the review states in prose.

- API-endpoint conversation compaction records a `mission.context_compacted`
  event for each pass that replaces a tool result, with the removed bytes in
  total and per tool, so its cost is visible on the mission.

- Non-objective dispatch and autonomous rescue planning verify configured captain
  coverage for each required persona and tier before creating work. Missing
  configuration refuses or defers the work without treating busy compatible
  captains as absent or spending a rescue attempt.

- Check cancellation, execution and source stamping re-read and update the
  current record under one state lock. Terminal-voyage discard cancels only
  Pending Checks, and a stale stamp cannot retag a Check after it starts.

- Explicit MCP code-search debug requests return independent embedding vector
  copies within normal result limits. Default search and context-pack results
  remain vector-free.

- Rescue stages use the current owning objective contract and share objective
  preview's execution requirement checks. Missing resources leave a named
  incident without creating rescue work or spending a recovery attempt.

- Objective preflight keeps Jev Q4 dock-observability and Q13 owner-question findings as visible warnings in Gate mode. They do not block dispatch or create an owner note; other high text-question findings remain blocking.

- Successful no-op reconciliation records the completion note `no-op integration: work already at target`, while logs retain the detailed decision reason.

- SQLite coordination room creation ignores only a duplicate room key, so concurrent provisioning keeps the existing room and still reports other constraint failures.

- The dashboard exposes Code Index and Notifications history pages, and routes
  `/token-usage` to the token-filtered Activity page. Index searches clear old
  results when the vessel changes and show why an index is unavailable.
- Merge-queue no-op detection accepts already-integrated implementation work
  only when its reachable branch history includes a commit made after the
  mission started. Missing or unreadable history, review stages, and rescues
  with no substantive work remain refused.

- Captain `[ARMADA:NOTE]` output is stored on the coordination board with the
  assigned captain and mission metadata. Notes redact credentials and enforce
  500-character and 20-per-mission limits.
- The Admiral health check uses OpenCode provider-progress timestamps when it
  decides whether to stop and recover a live captain. Startup grace, terminal
  markers, dock writes, branch commits, and the bounded recovery limit remain
  in effect.

- Blocked recovery for Infra and Timeout gate failures retains the stable
  `gate_class_not_rescuable` reason in the event and incident, with the failure
  class and operator guidance.

- Judge acceptance checks read the verdict attached to each required criterion.
  Literal `MET/NOT MET` instructions and evidence text do not cause false
  rejection; a real `NOT MET` verdict still refuses PASS.

- Merge commits never name a mission branch. Docks install a
  `prepare-commit-msg` hook that rewrites the first line of a merge message Git
  writes, so a captain's `git merge main` and a merge-queue merge no longer
  publish the mission id in history. The operator branch merge builds its
  subject by the same rule.

- Rescue delivery evidence rejects chains that cross tenant, user, or vessel
  scope and includes cancelled siblings whose attempt facts remain.

- A repeated model gateway timeout fails with `provider_timeout` and a
  configured endpoint label. Its one-retry budget follows the mission across
  captain assignments, and retry and failure facts retain the typed cause.

- Dashboard captain forms retain the Mux turn cap across edits and offer only
  approval policies supported by `mux print`.

- MCP captain create and update tools store Mux maximum-turn settings, and clear
  the setting when a captain changes to another runtime.

- Mux captain options reach `mux print`: configured adapter type, temperature,
  token limit, system prompt, and supported approval policy are emitted, and
  `--mux-max-turns` sets a clamped per-captain iteration limit. Unsupported
  print approval policies now stop launch with an explicit error.

- Verified production summaries can count a failed voyage after recovery only
  when typed attempt facts prove every independent rescue chain through
  completed, committed delivery tips with landing and passing Check evidence.
  Ambiguous, incomplete, or cross-scope lineage remains unknown.

- Mission attempt facts keep stable reason codes only when a typed emitter
  provides an allow-listed cause. Watchdog, runtime, definition-of-done, and
  landing failures retain their known causes; unknown causes and free-text
  messages remain unclassified.

- Tenant creation rejects whitespace-only identifiers with a client error
  before storing a tenant or its initial user credentials.

- REST mission and voyage reads enforce the shared mission evidence scope
  before returning metadata, counts, or embedded details, including tokens
  whose owners are administrators.

- Rescue finding lists retain unbulleted `R<number>` and severity findings with
  their explanations and anchors. They omit passing test summaries and test
  gaps do not become positive results. Completed criteria stay out of the list
  when their descriptions contain phrases such as "NOT DELIVERED".

- Dock boundary hook tests detect POSIX shells with a portable command and
  report unavailable tools as named skips instead of passing without execution.

- Check runs honor an explicit commit hash as the exact revision to test. An
  unavailable commit or failed checkout now fails before the command runs,
  instead of testing a branch or the clone's default revision.

- Failed definition-of-done commands retain their redacted bounded-runner output
  in mission-scoped capture files. The evaluation report stores a capture ID,
  byte count, digest, and runner completeness flags. The mission log endpoint
  returns bounded UTF-8 pages after mission authorization. Older records without
  captures remain readable.

- A usage account whose login the provider rejects reads as needing sign-in,
  not Exhausted on a timer. A Codex usage read refused with a credential
  rejection (HTTP 401, an expired or revoked token, "sign in again") names
  `account_login_expired` instead of a generic provider error. A captain's
  authentication failure that names no quota or credit limit refuses its
  account as `account_login_rejected`, records a `usage_account.login_rejected`
  event and an error signal, re-routes the mission, and no longer starts a
  timed quota hold or quarantines sibling captains. A refused account drops its
  stale windows from the status, is not routed or launched, and recovers on the
  next successful usage read or a new login. Account holds name their kind
  (`quota`, `credit`, `auth`) in `holdKind`, and a usage read in which the
  provider serves ordinary usage ends a quota hold early.

- Objective preflight Q1 resolves parent-relative citations only through a
  sibling that is both declared by the target vessel and required by the
  objective. It checks the cited file against that sibling repository's
  current verified tip and keeps ordinary target-repository citations on the
  target revision.

- Objective dispatch keeps long, numerous, and multiline acceptance criteria
  complete for the Judge evidence gate. Mission description, metadata, and
  total-budget trimming preserve complete-output references and digests even
  when large contracts, diffs, and voyage board notes consume the narrative
  budget. Quoted control markers cannot replace the objective contract, and
  operator-supplied handoff markers render as text. Same-objective retries add
  the brief unless it is the last complete frame before the first handoff marker;
  the exact frame comparison normalizes line endings across hosts. Retries keep
  generated handoff markers. Criteria that differ by letter case remain separate
  requirements.

- Git source anchors exclude the reserved generated briefing tree while keeping
  ordinary tracked and absent repository paths in the anchor report.
- Rescue evidence reads support long configured stage chains through a visited
  dependency walk that preserves owner, voyage and vessel boundaries.
- Stored objective stage skips require an explicit authenticated operator
  confirmation with server-recorded identity, time and proof. Legacy skips block
  dispatch until reconfirmed. Changed stage lists or reasons clear prior proof;
  unchanged preparation round trips retain it.
- REST refuses mission-scoped writes to objectives and backlog, including
  reorder and GitHub import. It also refuses linked planning, voyage,
  refinement, release, deployment and incident writes before route side
  effects. Reads, enumerate requests, owner roles in internal workflows, and
  MCP authorization keep their existing rules.
- Judge rescue briefs protect report-only Evidence and Residual Risks sections,
  retain their actionable findings, and state when protected feedback exceeds
  the embedding cap.
- Mission status replies redact operator review and failure text before returning
  it through an authorized read.
- Mission status and full-description reads apply the same caller evidence scope
  as mission output before returning metadata, scope text, or context events.
- Global operators can inspect retained preflight input by event id through
  bounded pages with digest validation; incomplete scans and unrecorded Q4
  rationale are explicit, and no objective or mission ownership fields leave
  the host-local sample store.
- Judge rescue briefs use the full redacted output artifact, keep required review
  findings ahead of completed criteria, and mark bounded excerpts incomplete.
  Every rescue stage receives the failed-output reference; description, metadata,
  and total-budget trimming keep its exact artifact ID, length, digest, and
  generated read notice together. Later stages can read that parent through the
  same-owner rescue chain.
- Objective MCP tools expose stored preflight answers and stage-skip confirmation
  in the preparation schema. Update descriptions state that a supplied
  preparation replaces the full nested object, so clients can send each value
  that must remain when they change repository anchors.
- Cursor usage routing reads Cursor's own models correctly: models matching the
  account's `cursorModelPrefixes` (default `composer-`, `grok-`, `cursor-grok-`)
  draw on the Cursor-models pool, read Low instead of Exhausted while they spill
  into the third-party pool, and never take the third-party API-pool lead.
- Eligible automated Checks for active voyages can finish during a dispatch hold,
  so existing voyages can pass their Check gate and drain. Checks for idle vessels,
  completed voyages, and other unrelated work remain deferred; new mission and
  rescue dispatches remain blocked.
- Smart Routing persona model lists are authoritative: a captain whose model is
  on none of a persona's lists no longer takes that persona's work when the
  listed captains are busy; the mission waits with
  `persona_model_list_excludes`, and the dispatch preview counts the same
  captains. A Cursor captain with API-pool usage leads only its own list group.
- Captains no longer inherit admiral secrets: a local launch removes provider and
  typed-decision key variables, configured `captainEnvironmentScrubNames`, and
  credential-named `ARMADA_` variables before applying its own, and Claude Code
  captains are refused the bare environment-listing commands.
- Stored-prompt upgrade coverage uses a fixed historical fixture instead of
  deriving its prior input from the current prompt.
- Premise checks identify a selected supplied fact for a positive absent-symbol
  reading, or return an explicit unknown identity, using centrally redacted values.
- Default TestEngineer instructions honor explicit objective limits on test-file
  changes while preserving required, permitted validation.
- Mission tokens can read scoped report-chain output and full authorizing
  objectives without inheriting administrator operator tools.
- Voyage status now applies caller scope to the voyage record, summary counts,
  assignment totals and mission details. Mission tokens remain limited to their
  own voyage and its same-owner missions, even when the owner is an administrator.
- Papercut listings expose continuation pages, source and result totals, and
  scan truncation. New arrivals cannot displace the page set; changed retained
  events or merged results require a restart.
- Added-test coverage extraction accepts recognized test attributes and excludes
  unannotated helper methods from its bounded candidate list.

- A coordination note addressed to a session's name reaches the session. A
  `toParticipantKey` that is no participant key but is a name a participant key
  posts or heartbeats under is delivered and woken under that key, so the read
  filter, the wake and `armada_mark_signal_read` agree, and the post result
  carries `AddressNotice`. `armada_coordination_read` and
  `armada_coordination_heartbeat` refuse a `participantKey` that differs from
  the session's participant header instead of reading the header key silently.

- A mission failure reason is never scraped from a sub-tool's activity record.
  When a captain's process exits, the failure-reason extractor skips every
  `[ARMADA:ACTIVITY]` line that is a tool record or carries a runtime label other
  than the captain's own runtime, so a credential or HTTP error from a tool the
  captain invoked (for example an `opencode` call made by a Cursor captain) no
  longer becomes the mission failure reason and no longer benches the captain or
  reroutes the mission as a provider auth or quota failure. The captain's own
  provider telemetry is still read, and the stored failure reason never contains
  the `[ARMADA:ACTIVITY]` marker.

### Additional capabilities

- **Typed decisions:** provider-neutral closed-question classification behind
  deterministic rules; conservative Gate behavior, key-based availability,
  redaction and egress exclusions, custom decisions, evaluation on model changes,
  host-local retained samples with redacted question definitions, request hashes,
  returned model versions and answer distributions; redactor-version cohorts and
  operator reversals. Sample counts are separate from verified training readiness.
  Captain tools provide premise, prior-art, change-quality, memory, and per-item
  readings. Operator tools report evaluations and route findings for review.
- **Native captain memory:** scoped episodic, semantic, and procedural records,
  versioned writes, recall, Recorder review, and operator-managed memory proposals.
- **Context retrieval:** a generated context index, core bundle, scoped leaf
  retrieval, optional metadata sidecar, and optional brief slimming with a full
  memory-reading fallback.
- **Coordination:** shared room messages, claims, presence, directed wakes,
  persistent AgentWake targets, campaign views, a WebSocket watcher, and bounded
  helper processes.
- **Dispatch admission:** objective preparation claims and preflight, dependency
  diagnostics, environment requirements, fleet and sibling-lane capacity leases,
  operator-confirmed stage skips, and durable dispatch jobs with restart-loss
  records.
- **Review and delivery evidence:** Linter and Recorder stages, acceptance-criterion
  evidence checks, operator review holds, .NET Slop and configurable banned-diff
  checks, declared-consumer checks, and scoped admission, dock-anchor,
  definition-of-done, recovery, and landing reports.
- **Account-aware routing:** subscription account usage, separate captain logins,
  usage previews, reserve policies, per-persona model lists and restrictions,
  account failure holds, an optional required-login gate, and Cursor usage from
  saved account API keys with legacy cookie support. Cursor's Composer and Grok
  captains use the Cursor model allowance, while other model IDs use the API
  allowance; the account summary reports partial exhaustion when one pool remains.
- **Remote execution and server control:** Harbor identity, runner enrollment and
  mission execution; guarded vessel branch controls; in-place server restart;
  supervised self-deployment preflight, retained-image rollback, and build identity.
- **Operational measurements:** production summaries, attempt and lane-time facts,
  host-slot wait, preparation observations, and post-land regression links.
- **Verification tooling:** a four-runner Linux gate, balanced unit shards,
  explicit test registration and result manifests, provider migration scenarios,
  API collection coverage, isolated test data directories, one owner suite
  for review-diff coverage, and a local-merge landing case that runs the real
  landing handler. Mission list projection (REST, MCP and the SQLite summary
  query), planning reply bounds, captain-override dispatch, project-profile
  briefs and process liveness are proven by behavior in their owner suites, not
  by source-text checks, and repeated stage-base and merge-queue Git cases run once.

### Additional removals

- Reflection-memory bootstrap, consolidation and learned-fact/pack-hint storage.
  Native captain memory and the external rules repository have separate owners.
- Hardcoded deployment-specific reviewer personas, pipelines, routing lists, and
  domain rules from general product defaults. Deployment settings supply these.
- Inline provider credentials on captain configuration. Captains reference
  inference endpoints or configured account logins.
- Obsolete per-runtime instruction copies and the upstream-review planning folder.
  Current subsystem guides and repository history retain the useful reference.
- Tracked operator-specific guide chapters, context metadata, and local reports.
  The repository supplies templates; deployments keep filled files locally.
- Unreferenced services and helpers: the feature-specific remote-control tunnel
  handlers (the tunnel serves the generic dashboard relay only), unwired
  resource-admission, no-op-completion, reasoning-effort, git-anchor, auto-land
  and dock-boundary helpers, the unused dock state enum, and test-only runtime
  and MCP wrappers. Their tests now exercise the live paths: the operator
  transition service against the whole transition table, the Claude Code launch
  environment, and the OpenCode output transform. Source files hold no literal
  NUL bytes, and the solution gives each automated test project its own name.
- The unused `jobs` table and its `idx_jobs_created` index. Nothing read or
  wrote it: long-running Admiral jobs live in the job journal under the data
  directory, and Harbor and landing jobs keep their own tables. A schema
  migration on every provider (SQLite v106, PostgreSQL v109, MySQL v98,
  SQL Server v101) drops the table with any rows it still holds, so upgraded
  and fresh databases end with the same schema.
- The code-index `signatureModel` setting. Nothing read it: file signatures use
  the summarizer model. A settings file that still carries the key loads
  normally, and the next save drops it.

### Additional behavior changes

- **Each captain launch gets its own temporary directory, removed on exit:**
  agent CLIs and the builds and tests they start wrote to the shared temporary
  directory and left files behind. The OpenCode CLI unpacked a native library
  of about 5 MB there on every run, several hundred a day, and nothing removed
  them. A launch whose caller sets TMPDIR keeps that directory.
- **Report-only reading rules no longer cut a full study short:** the
  context-conservation section of a Research or Audit brief defers to the
  mission's scope, lets a large file be read in chunks to its end and re-read
  after compaction, and reports unfinished required work as unmet acceptance
  criteria. It forbade reading the same path twice and called a partial report
  a success, which captains cited to leave mandatory files unread.
- **A captain's MCP tool list is the captain set:** a mission token reaches the
  caller-scoped tools and the read-only voyage tools, even when the mission's
  owner is an administrator. An administrator-owned mission was listed every
  operator tool, and each model request carried about 51,000 tokens of tool
  definitions; a self-hosted model behind a 60-second gateway could not read
  the first request in time.
- **A voyage ends on its last verdict:** the voyage completion sweeps, and a
  stage's own completion, keep a voyage open while any of its missions is still
  in completion handling. A Judge reads WorkProduced from the moment its captain
  exits until its verdict is applied, and a sweep in that window could write the
  voyage Complete; the later NEEDS_REVISION then left a Complete voyage beside a
  Failed Judge, because a Complete voyage is never rewritten. The landing drain
  also leaves such a voyage alone, so it no longer queues a stage for landing on
  a Judge PASS that the Judge's own handler has not accepted yet.
- **Unfinished in-scope work no longer reads as an owner question:** the
  working personas' blocked-path guidance keeps `[ARMADA:RESULT] BLOCKED` for
  what a captain cannot resolve itself (missing context, a false premise, a
  question only the owner can answer) and asks it to commit finished work and
  end COMPLETE with a `Remaining work:` list when in-scope work remains, so the
  next stage continues it instead of the voyage stopping for the owner. Rows
  that still carry the first wording get the current one; operator edits stay.
- **A voyage is judged against the objective it was dispatched for:** the
  handoff-outcome decision, its partial-handoff note and the linked-objective
  reads resolve a voyage's objective by the objective named in its brief, and
  otherwise prefer the most specific objective over an umbrella that lists the
  same voyage. Taking the first listing objective could pick the umbrella, so a
  child step was checked, and its later stages briefed, against the umbrella's
  acceptance criteria.
- **The landing sweep no longer repeats a step beside a processing pass:** the
  periodic merge-queue landing sweep takes the vessel's repository slot that
  entry processing holds, skips a vessel another pass holds, and re-reads the
  entry once it holds the slot. Before, a sweep could start a second test run of
  an entry already in Testing; that run outlived the landing, held the host
  build slot, and then marked the landed entry failed.
- **Two vessels that declare each other as siblings no longer deadlock their docks:**
  dock provisioning releases the vessel's own repository lock once its worktree
  exists, before it provisions sibling repositories. Holding it across sibling
  provisioning made two such docks, provisioned at the same moment, wait on each
  other's repository forever, so both missions stayed in provisioning.
- **A Judge revision asked for before its Checks finished waits for them:** a
  NEEDS_REVISION given while the independent Checks at the reviewed commit are
  unresolved is held, and once they resolve (or the wait budget ends) the Judge
  runs again with their result, once per mission and without spending recovery
  budget. A NEEDS_REVISION after that re-run is recorded as before.
- **A vessel test that only fails under the loaded suite no longer fails its
  gate:** when the `flake_score` decision does not re-run a red unit-test suite,
  the definition-of-done gate re-runs the failing classes once in isolation (at
  most three), as it already did for consumer suites, and that result stands
  (`RerunFailingClassesOnce`, default on). A red that is not re-run states why in
  the failure text, and one that fails again alone says so.
- **Checks no longer starve behind other vessels' gates:** the host-wide build
  and test slot is granted in request order and names its holder, and a
  definition-of-done gate takes it per command instead of for its whole
  sequence of builds, tests and consumer suites. `run_check` returns an
  unfinished Check after two minutes instead of timing out the caller; the
  Check keeps running. Check views carry `SlotWait` (state, place in line,
  holder, guidance), and the automatic Check runner logs running and waiting
  Checks separately.
- **Bounded image retention on rebuild:** `rebuild-local-image.sh` keeps the
  newest three retention sets after a successful build and removes older sets
  by tag (`ARMADA_RETAINED_KEEP` sets the count). Each rebuild retained two
  full images with no bound before.
- **One re-run after a model gateway timeout:** a captain whose model request
  failed with an HTTP 502 or 504 that says the gateway gave up waiting (for
  example a cold self-hosted model load) re-runs its mission once and records
  `mission.model_gateway_retry`. A second such failure fails the mission as
  before.
- **Quieter git log, working prior-art samples:** `merge-base --is-ancestor`
  answering "no" (exit 1, no error text) is logged at Debug, not as a failed git
  command; a missing ref still warns. The prior-art sample pass prints only the
  matched text, so a common term found on a minified file's megabyte-long line
  returns its path and line instead of failing the output limit.
- **Code search and prior art say when they could not look:** a mission code
  search on an unindexed or failed index returns a `Readiness` remedy naming
  `armada_index_update` and records `code_index.search_unavailable` once an
  hour per vessel. `armada_check_prior_art` returns each candidate's path and
  ref with its readings, plus `searchComplete` and the surfaces whose search
  failed, which the decision state also carries.
- **Docks and landings that cleanup or a failure left behind:** the orphan-dock
  reaper keeps the dock of a mission awaiting review; stall recovery keeps the
  dock of a mission whose work is produced, its pull request open, or under
  review; the disk sweep skips its orphan-dock scan when the protected docks
  cannot be read. A direct landing pushes to origin before it advances the bare
  target, and an exhausted drift retry records `landing.origin_push_failed`
  and opens an incident. A merge-queue error while preparing the integration
  worktree fails the entry as `integration_setup_failed` instead of leaving it
  in Rebasing.
- **Handoffs keep a stage's report essentials; rescues release their
  dependants and close their objective:** each handoff block ends with the
  stage's complete-output reference and digest, verdict, blocking findings,
  follow-ups and added tests, which survive the preview, the description cap,
  compaction and the rescue brief. A cross-voyage dependency on a cancelled or
  failed stage moves to the completed stage of a rescue of its voyage
  (`mission.dependency_rewired`). A rescue of a failed stage recovers the failed
  stages above it in the same chain, so the objective reconciles to Completed.
- **Objectives can forbid native memory writes:** a captain's launch
  credential names its mission, and `create_memory` and `update_memory` refuse
  with `native_memory_forbidden` when that mission's objective carries the tag
  `no-native-memory` or a rollout constraint or non-goal saying the work writes
  no memory. The rule covers rescue stages descended from the objective, so a
  Recorder under such an objective reports only. Other objectives and operator
  credentials are unchanged.
- **Architect plans keep their blocks:** stored output redaction treats a
  quoted span that is not a JSON string as prose, so markdown whose quotes pair
  across lines keeps its text and its `[ARMADA:MISSION]` lines; key-shaped
  values and sensitive JSON properties are still removed. The pipeline handoff
  and `armada_parse_architect_output` read mission blocks through one grammar
  that accepts `[ARMADA:MISSION-END]` and `[/ARMADA:MISSION]`. The
  numbered-line fallback runs only for output with no mission marker and
  records `mission.architect_plan_fallback`.
- **Rescue briefs carry every blocking finding:** a rescue for a reviewer
  rejection lists every item the review marks `NOT DELIVERED`, `NOT MET`,
  `NOT RESOLVED` or `Blocking`, each whole, within a 4,000-character bound,
  before the bounded review excerpt. Findings in the middle of a long review
  are no longer cut away.
- **Landings that do not reach origin are loud and keep their work:** a merge
  queue landing that does not reach origin records a
  `merge_queue.origin_push_failed` event and opens one High incident per vessel
  and target, naming the branch, the git error, and the bare, origin and
  integration heads. The branch sweep counts a branch as landed only when origin
  also contains it (`KeptUnpushed`). A landing retry whose dock was reclaimed
  lands from a temporary worktree at the mission branch tip instead of being
  refused as `landing_evidence_unavailable`.
- **Planner guard and fan-out base for recovery tips:** the planner
  committed-code guard measures what an Architect or Product Manager changed
  from its provisioned start commit to its final commit, so code inherited from
  a recovery tip is no longer reported as `planner_committed_code`. When a
  non-target start commit cannot be resolved, the guard fails closed and says
  so. Workers fanned out from an Architect that started off the target branch
  are cut from the Architect's start commit and verified to contain it.
- **A usage provider's error reply no longer aborts captain exit handling or dispatch:** a Codex
  account whose login the provider rejects answers the usage read with an error, surfaced as
  InvalidDataException, which the collector did not catch. The fault escaped the shared in-flight read
  into every awaiting caller, so a captain's exit handling stopped before its stage handoff and a
  pending-mission dispatch sweep failed. The error is now recorded on the account like any other
  collection failure (`usage_collector_provider_error`).
- **An automatic incident write no longer undoes an operator close:** every automatic writer
  (the incident lifecycle sweep, autonomous recovery) acts on an incident it read as active. When the
  incident is Closed or RolledBack by the time the write lands, the write leaves it unchanged, so a
  stale sweep can neither reopen it nor replace the operator's cause and notes. The lifecycle sweep
  logs the skipped move instead of emitting a move event. An operator can still reopen by hand.
- **A provider's own verdict outranks its usage meter:** the Codex collector reads the
  provider's `ordinaryUsageAllowed` and `rateLimitReachedType`. A spent window whose provider still
  serves ordinary requests makes the account Reserve (demoted, still routable) instead of Exhausted,
  and a provider that refuses ordinary usage makes it Exhausted. With no verdict reported, the
  measured windows decide as before.
- **An operator can retire a seeded pipeline:** the startup seed leaves an
  inactive built-in pipeline inactive instead of reactivating and rewriting it, so a
  deactivated built-in stays retired across restarts. A built-in pipeline that
  neither the code nor `additionalPipelines` declares any more stops being built
  in at the next start and can then be deleted. Deleting a pipeline the seed
  still declares is refused with a reason that names deactivation as the
  durable retirement.
- **A retired pipeline is refused by name, never replaced:** a dispatch whose
  requested pipeline, vessel default or fleet default is inactive is refused
  with `pipeline_inactive` and the pipeline's name and source, instead of
  running it. Setting an objective's suggested pipeline or a vessel or fleet
  default to an inactive pipeline is refused on every write surface. A rescue
  that would run on an inactive pipeline is not created, spends no attempt, and
  names the pipeline on its incident.
- **A deleted pipeline no longer breaks status and subscriptions:** deleting a
  pipeline clears it from every objective that suggests it through the
  objective update, so each objective gets a new snapshot. The objective
  snapshot backfill runs once per admiral process instead of on every status
  read, drops a suggested pipeline that no longer exists
  (`objective.snapshot_pipeline_dropped`), and names and skips one objective it
  cannot restore (`objective.snapshot_restore_skipped`) instead of failing every
  read. A subscribe whose snapshot fails gets `subscribe.error` and can be sent
  again on the same connection. `scripts/autonomy/watch-armada.mjs` prints
  every hub error frame, reconnects after a refused subscribe, and exits 1
  after five refusals in a row.
- **Pipeline stages have no optional flag:** `IsOptional` is gone from the
  stage model, `create_pipeline` and `update_pipeline`, the REST and MCP
  references and the dashboard, and its column is dropped (SQLite 112,
  PostgreSQL 116, SQL Server 108, MySQL 105). Nothing read it, so a stage marked
  optional still ran. Leave a stage out by removing it or with a recorded stage
  skip.
- **A held Pending stage names its hold:** the assignment pass records why it
  holds a Pending mission in `AssignmentHoldReason`, with the holding record in
  `AssignmentHoldEntityId`, persisted on every provider (SQLite 111, PostgreSQL
  115, MySQL 104, SQL Server 107) and shown by mission status, voyage status and
  the dashboard mission page. A stage waiting while its predecessor's
  definition-of-done gate re-verifies declared consumers reads
  `dependency_completion_in_flight`; before, only a repeated log line said so.
  Each named hold records `mission.assignment_held` once when it starts or
  changes and `mission.assignment_hold_released` once when it ends, never one
  event per assignment tick.
- **The latest record of a Check decides every Check gate:** the Judge gate, the
  voyage completion gate and the manual completion proof read, for each Check
  (same voyage, mission, type, workflow profile, executed command and commit),
  only its most recently created record that is not canceled. The label does not
  identify a Check, so an operator re-run of the same command under another label
  supersedes the failed record, and an abbreviated commit hash matches the full
  one. A run of a narrower command stays a separate Check. A retry that is still
  running holds a Judge PASS instead of rejecting it, and a retry that passed
  clears the failure it re-ran. A canceled retry decides nothing, so the failure
  it was meant to replace still stands.
- **Production summary landing evidence and scan scope:** a delivered tip counts
  as landed on a mission-linked `Landed` merge entry or a `Landed` attempt fact,
  so direct landings (`LocalMerge`, merged pull requests) verify as well as
  merge-queue landings. The readiness scan reads only `objective.snapshot`
  events and reports the source as `objective_snapshots`, so other high-volume
  events no longer exhaust the record limit and mark the summary incomplete.
- **The merge queue keeps LocalMerge landings on the local target:** a vessel
  whose effective landing mode is `LocalMerge` merges into the local target and
  pushes nothing, so its local target is legitimately ahead of origin. Integration
  setup, the post-push sync and the failed-landing rollback reset the local
  target to origin only when the move fast-forwards or the vessel lands by
  pushing; otherwise they keep the ref and log a warning that names the kept
  commits, and the post-push sync records a `target_ref_sync_skipped` event. A
  queue entry whose mission commit is already on the target (and on origin for a
  pushing vessel) is `Cancelled` as already landed instead of failing as a no-op.
- **Restarting a mission of an ended voyage is refused:** REST, WebSocket and
  MCP restart share one rule that refuses a mission whose voyage is `Complete`,
  `Failed` or `Cancelled` with `code: "voyage_ended"`, names the voyage and its
  status, and changes nothing. Assignment cancels a `Pending` mission of an
  ended voyage, so the restart would report success and then be undone;
  dispatch a new voyage or a rescue instead.
- **A voyage ends Complete only when its work landed or was never meant to
  land:** a `Failed` voyage becomes `Complete` (`failed_voyage_landed`) only
  when at least one mission is `Complete`; a failed stage that is later
  cancelled keeps it `Failed`. A live voyage with a cancelled stage and no
  `Complete` mission ends `Failed` (`nothing_landed`), and one whose every
  mission was cancelled ends `Cancelled`. Voyages that land work, report-only
  voyages whose missions complete, and voyages that ask for no landing still
  complete.

- **A captain brief carries only what its mode and persona can use:** an Audit
  or Research brief leaves out `engineering` and `testing` skills and states
  Git Anchors as facts, not new work or prior art; the memory read-first rule
  no longer assumes the mission edits; Runtime Signals verdict lines reach only
  a Judge and the Architect rule only an Architect. The output contract is
  stated once in Mission Instructions, not also in the captain instructions and
  the launch prompt, so a Judge without captain instructions now receives it.
  `captainInstructionByteBudget` defaults to 65,536 (0 selects the default),
  and an over-budget brief records a `mission.prompt_over_budget` warning.

- **A retried mission's sibling checkouts are removed when its last dock is
  reclaimed:** a superseded dock whose nested path now belongs to the live
  retry releases its sibling leases instead of keeping them, and sibling
  removal ignores a lease held by a dock that is already reclaimed. Before,
  the leftover lease kept every sibling clone of the mission on disk after
  both docks were gone.
- **The dispatch preview test harness deletes its temporary repository
  directories** after each test, so repeated unit runs no longer accumulate
  empty `armada-preview-*` folders in the temp directory.

- **A stage is handed off only on a recorded definition-of-done result:** the
  lazy handoff dispatch runs for a dependent whose upstream was never handed
  off, and the dangling-handoff recovery, read the upstream's latest gate
  evaluation. They hand off only when it belongs to the upstream's current
  launch and is `Passed`, `Skipped` or `NotVerifiable`. An upstream whose gate
  never recorded a result, such as one whose completion an admiral restart
  interrupted while the gate waited for the host command slot, is held for
  operator review with a `definition_of_done_not_run:` reason, and its
  dependents wait.
- **Cancelling a mission stops its definition-of-done gate:** a mission cancel
  or a status transition to `Cancelled` stops a gate still running or queued
  for that mission. The gate's command process group is killed, the host-wide
  command slot is released, completion records a `Cancelled` evaluation and a
  "validation cancelled" activity line, and the mission keeps its `Cancelled`
  status.

- **A newly armed Check starts on the next sweep:** the automatic Check sweep
  starts each eligible Check on its own task, up to four in flight, and returns.
  A re-armed or newly armed Check joins the host-wide command slot at once
  instead of waiting until every Check of an earlier sweep finishes. The slot
  still runs one build or test command at a time.

- **A held Judge PASS is released without a new Judge run:** a PASS whose
  independent Checks have not reached a verdict stays `WorkProduced` in a
  `judge_check_wait` hold that names the reviewed commit. The health cycle
  releases it to handoff or landing once every Check passes at exactly that
  commit, rejects it when one fails there, and rejects it as unresolved after
  two hours. A green at another commit never releases it, an operator clear
  is refused, and the Judge runs again only when the reviewed commit changes.
  A held PASS keeps its dock through the orphaned-dock sweep.

- **Dock anchor and preserve refs stay in the vessel bare repository:** the
  `refs/armada/docks/*`, `refs/armada/missions/*` and `refs/armada-preserved/*`
  refs written when a dock is reclaimed are now local ref updates. A dock is a
  worktree of the vessel bare, whose `origin` is the hosted remote, so the old
  push sent every anchor, with mission and dock IDs in its name, to that
  remote. The stage handoff also advances the mission branch in the bare only
  and no longer pushes it. Mission branches reach the remote only through the
  pull-request landing path.

- **Model validation runs on the captain's account login:** creating or
  editing a captain on a usage-routing account probes the model with that
  account's login (`CURSOR_API_KEY`, `CODEX_HOME`, `CLAUDE_CONFIG_DIR` or
  `XDG_DATA_HOME`), resolved by the same helper a launch uses. Before, the
  probe ran on the shared login, so a Cursor edit read as a credit or
  authentication failure for a model the account serves.
- **A rescue keeps the stage skips of the voyage it rescues:** the autonomous
  rescue reads the failed voyage's `voyage.stage_skipped` events, drops those
  stages from the rescue chain through the dispatch skip rule, and records a
  skip event on the rescue voyage with the original reason plus "carried from"
  the voyage. A stage whose persona the failure names still runs, and the
  Judge always runs. Before, a rescue rebuilt the whole objective pipeline and
  re-ran the skipped stages.

- **A merge entry for work that already landed is cancelled, not failed:**
  when an entry's branch no longer exists and its mission commit is already
  in the target branch, the entry is cancelled with that reason. Before, a
  landing-drain entry that raced a landing failed after branch cleanup and
  raised a false "merge failed" alert.
- **A consumer suite red is re-run once in isolation:** when a declared
  consumer's unit-test suite fails inside the definition-of-done gate and the
  flake decision did not re-run it, up to three failing classes run again
  alone and that result stands. `DefinitionOfDone.RerunFailingConsumerClassesOnce`
  turns it off.
- **Every rescue inside a voyage is re-judged:** an automatic rescue of any
  non-planner stage that failed inside a voyage (an analyst, TestEngineer or
  Linter as well as a Worker) now runs in a rescue voyage with the pipeline's
  later stages and the Judge chained after it, starting after the rescued
  stage. Before, only a Worker rescue was chained; any other rescue ran
  standalone and could land through LocalMerge without a Judge.
- **Readiness ignores separators inside double quotes:** the command
  dependency probe splits a command at `;`, `&&`, `||` and line breaks only
  outside double quotes. Before, a quoted message such as
  `echo "...; left unchanged"` made every Check on the profile fail with
  `The command dependency 'left' could not be found`.
- **A consumer suite gets the flaky-test re-run:** when a consumer's unit-test
  suite fails inside the definition-of-done gate and the `flake_score` decision
  recommends it, only the failing classes run again in the same worktree, and
  that re-run is the result. The re-run builder now narrows one existing
  quoted `--filter "EXPR"` in place to `(EXPR)&(classes)`, so a wrapped command
  such as `bash -c '... dotnet test ... --filter "Category!=Integration"'` can
  be isolated; a compound command with no filter to narrow is declined.
- **A planner that committed code is rescued by a Worker:** after a
  `planner_committed_code` failure the automatic rescue is a Worker that
  starts from the planner's commit, with a re-Judge chained. Before, it was the
  same planner persona again, which repeated the failure.
- **Stage skips accept the persona identifier form:** `skipStages` ignores
  spaces as well as case, so `ProductManager` names the stage
  `Product Manager`. Before, the identifier form was refused as unknown.
- **A later pipeline stage re-verifies declared consumers:** when a stage the
  definition-of-done gate does not apply to commits production code, the
  consumer build and triggered consumer suites run again against the branch as
  that stage leaves it. A stage whose commits change only test files is not
  re-verified; untracked files in the dock, such as the briefing folder, do not
  count as a change.
  Before, only the Worker stage verified consumers, so a later stage could land
  a consumer break. `DefinitionOfDone.VerifyConsumersAfterLaterStages` turns it
  off.
- **The sibling-tip preflight fact judges only a sibling's own commits:** a
  commit the brief cites is compared with a sibling tip only when that sibling
  holds it. Before, a brief citing the vessel's own commits (a start commit, a
  preserved tip) failed question 11 as a stale sibling.
- **A container stop runs the admiral's shutdown sequence:** SIGTERM is
  handled so the stop reaches `Stop` instead of the runtime exiting as soon as
  the unloading handlers return; before, a container stop killed the admiral
  without stopping agents or disposing the database. The run marker records
  when a stop was requested, and a stop that does not finish is reported as
  `admiral.stop_incomplete` rather than as an unclean exit.
- **The inbox and Ask read failed missions as summaries:** the inbox lists
  Failed and LandingFailed missions from the summary projection, and Ask counts
  missions by status from it. Both read every full Failed row before, with its
  description, diff snapshot and agent output; on a server with about 1300
  failed missions that was near 800 MB of text per inbox poll and raised the
  admiral's memory by 1.5 to 2 GB each time.
- **Consumer verification provisions a sibling's extraction artifacts:** the
  gate's consumer check links each sibling's declared `extractionArtifactPaths`
  from the sibling vessel's host working directory, as a mission dock copies
  them, moving aside a partial tree the sibling worktree already tracks at that
  path. Before, the consumer suite ran without its decompiled trees, so every
  tree-dependent test failed and the gate blamed the producer's change.
- **An admiral run that ends without a clean stop leaves evidence:** each run
  keeps `admiral-run.json` in the data directory with its start, its process id,
  and, every health tick, the time and the managed heap and container memory. A
  clean stop marks it. The next start reports a run with no clean mark in the
  log and as an `admiral.unclean_exit` event, with the last alive time and
  memory, since a kill by the OOM killer or the container runtime runs no
  shutdown code.
- **A Stalled captain with no mission returns to Idle:** the health loop
  releases a captain that has been Stalled for the stall threshold with no
  current mission and no live process, and records `captain.stall_recovered`.
  Before, nothing moved it back and it dropped out of routing until an operator
  intervened.
- **A Slop check with no reviewable work follows the unstamped-check rule:** on
  a voyage that ended before any stage produced reviewable work it is cancelled
  with the reason, like Build and UnitTest, instead of failing and raising a
  High incident about a diff that never existed; on a live voyage it waits.
- **Built-in template defaults reach live servers:** a committed hash history
  per built-in template lets the startup upgrader carry every row that still
  holds an earlier default forward. A unit guard fails when a default changes
  without its hash, and a row edited by an operator is reported once as drift
  instead of being skipped silently.
- **The scheduler skips unadmitted rows before previewing them:** a row whose
  recorded preflight does not admit dispatch is skipped on the record and
  counted as `dispatch_preflight`, without a dispatch preview and without
  spending the sweep's candidate or time bound. Before, each such row cost a
  preview of several seconds, so a sweep reached two of 240 candidates and the
  admitted rows behind them waited for hours.
- **Brief memory is delivered as files:** with brief slimming on, the core
  rules and the mission's retrieved leaves are written into the dock under
  `_briefing/memory/` as files that each fit one read, and the brief's Shared
  Memory section lists them. Memory no longer counts against the instruction
  file's byte budget, so the budget backstop stops eliding a mission's own
  scope, playbooks and description to make room for memory. The new
  `memory_relevance` typed decision sorts retrieved leaves into read-first and
  reference files without removing any.
- **Long mission descriptions are delivered whole:** a description over the
  metadata cap is still embedded as its head and newest handoff block, and the
  full text is written under `_briefing/mission/` as bounded files the elision
  marker names. `captainInstructionByteBudget` defaults to `0`, so the total
  budget backstop no longer elides mission text unless an operator sets a budget.
- **A watcher's reconciliation snapshot reads only linked Checks:** the global
  and scoped snapshots read Check runs by the id of each included voyage and
  mission. Before, they read every Check run created since the oldest active
  record, output included; an old `LandingFailed` mission stayed in the active
  set, so each watcher connect loaded several gigabytes of stored Check output
  and could push the admiral past its container memory limit.
- **A scope keeps its paragraphs in the objective brief:** the objective's
  description renders up to 6000 characters, cut to the space left in the
  brief with the truncation marker, instead of the 1200-character bound of one
  list item that dropped the last instructions of longer scopes.
- **Readiness does not probe a case terminator or a trap as a program:**
  `esac`, `trap`, `wait`, `local`, `readonly`, `declare`, `ulimit`, `umask` and
  `shopt` are shell builtins whose remaining tokens are arguments, so a
  workflow command using them is no longer refused as
  `command_dependency_missing`.
- **The Judge template states the acceptance-criteria grammar the checker
  enforces:** one line per criterion copying its exact text, then MET or NOT
  MET, then evidence on the same line as `path:LINE` or `command: \`...\``,
  with an example line. A test runs the template's own example through the
  acceptance checker, so the instructions and the check cannot drift apart.
  The checker itself is unchanged.
- **A failure while writing mission instructions releases the captain:**
  writing the brief into the dock is part of the launch, so an I/O error or
  an out-of-memory failure while the brief is built takes the launch-failure
  rollback (captain Idle, mission requeued, dock reclaimed, error signal)
  instead of leaving the captain Working with no process until the
  missing-process check marks it Stalled.
- **`armada_mission_status` can return the mission description:** the optional
  `includeDescription` flag returns the stored brief text, which the default
  summary read leaves out.
- **Every typed decision refuses an excluded vessel:** the log watch screen,
  change quality from a supplied diff, the merge-queue leak scan, and inbox and
  board-note triage now pass the vessel to the egress exclusion check, so an
  excluded vessel's content is refused before any provider call and the
  refusal is recorded, as for every other decision.
- **Typed-decision events name their subject:** every decision event and
  retained sample records the objective id and the mission id wherever the
  calling seam knows them, including preflight, prior art at preflight,
  criteria lint, stage necessity, owner digest, dispatch staleness, inbox and
  board-note triage, follow-up routing, log watch, change quality and the
  merge-queue leak scan. The ids are record links only and never enter the
  transmitted state, so the state hash and egress are unchanged.
- **Closing an incident needs a root cause the closer wrote:** REST, MCP and
  the dashboard close dialog refuse an empty cause
  (`incident_root_cause_required`) or the text the incident was opened with
  (`incident_root_cause_unchanged`). The opened text stays in `OpenedReason`;
  a written cause records `RootCauseWrittenBy` and `RootCauseWrittenUtc`.
  Lifecycle and recovery closes are allowed and set `ClosedAutomatically`.
- **The code-index summarizer names no model by default:** its model setting
  is empty until an operator sets one, and the HTTP inference client makes no
  call without a model and logs why. Startup warns when the summarizer is on
  without a model or without a chat endpoint, since an empty base URL sends
  completions to the embedding endpoint.
- **The automated runner owns end-to-end behaviour:** 733 shared end-to-end
  cases repeated an automated-runner case with the same requests and
  assertions, after any extra shared assertion moved to the automated case.
  They are removed, with ten shared end-to-end suites that held only such cases
  and the helpers only they used. The automated copies also run against
  PostgreSQL, MySQL and SQL Server. Shared cases with different input stay.
- **Automated-runner MCP and WebSocket cases carry the checks their shared
  copies made:** MCP entity-create helpers fail on a tool error payload and a
  merge-queue or dispatch job that never finishes, the tool list must name the
  check-run and release tools, every listed tool (not only `armada_` tools)
  needs a description, an input schema and a unique name, the merge-queue tool
  case waits for its job, and the empty mission list must deserialize.
- **One method set per table serves all four database providers:** check runs,
  deployments, deployment environments, releases, workflow profiles, token
  usage, request history, prompt templates, model endpoints, memories, skills
  and project profiles each have a single implementation built on a table
  descriptor (name, key, columns, reader, writer), a scope filter, and a
  provider dialect for first-row, any-row and paged reads. Scope conditions keep
  their text, order and parameter names on every provider, stored forms do not
  change, and no migration runs. Writes that compare and set or span tables (the
  model-endpoint health write, the versioned memory update, memory tags, request
  history detail) stay in their method set. Every paged read binds its page size
  and offset and reads 25 rows for an empty page size, and a creation-time bound
  without a kind reads as UTC on every provider, including PostgreSQL check runs,
  deployments and releases.

- **Dispatch refuses a captain assignment the captain can never take:** a
  `captainAssignments` entry whose captain's `AllowedPersonas`, runtime or tier
  excludes the stage persona, or whose captain is absent, in another tenant or
  benched, is refused before any voyage exists, with the captain, the persona
  and a code such as `captain_persona_not_allowed`. MCP, REST, WebSocket,
  alias and objective dispatch share the check, and the dispatch preview
  reports the same finding. A mission already waiting on such a captain names
  the code in its `mission.requested_captain` event, and a wait no captain can
  ever end records `mission.unassignable_by_construction`.
- **A Mux error event reaches the mission log:** Mux names its event in
  `eventType`, and the structured failure reader recognised only `type`, so a
  Mux provider failure (for example an HTTP 429 from a proxy at capacity) left
  an empty mission log and never reached the throttle detector. The reader
  accepts either field.
- **Stored values written through a shared binder:** every provider writes and
  filters each entity read through a shared column reader through
  `StoredValueBinder`, with one shared writer per row type beside its reader.
  Each provider states how it stores each timestamp column (ISO 8601 text, text
  the server renders from a typed timestamp, a zone-less timestamp, or a
  zone-aware one) and which booleans it stores as integers, and every value is
  sent explicitly typed in its column's stored form. A timestamp without a kind
  is the UTC instant it reads back as. A PostgreSQL zone-less timestamp column
  stores the UTC time whatever the session time zone, the PostgreSQL
  workflow-profile creation-time window compares its bounds as timestamps, and
  the mission admission and model-endpoint health compare-and-set writes match
  the stored update time after every write path. A PostgreSQL database whose
  planning tables predate the planning migration has their text times
  converted to TIMESTAMPTZ (migration 114). After migrations, startup compares
  every timestamp and integer-boolean column the binder writes with the live
  schema and refuses with an "Incompatible schema prerequisite" error that lists
  each mismatched column. Only tables the provider's own schema statements
  (migrations, operational prerequisites and ledgers) create are checked; any
  other table, such as an operator's backup copy, is ignored whatever its name.
- **Stored rows read through shared column readers:** every provider reads
  tenants, users, credentials, fleets, vessels, signals, events, captains,
  missions, mission summaries and history points, merge entries, landing jobs,
  Judge follow-ups, docks, voyages and their playbook selections, objectives,
  objective refinement sessions and messages, planning sessions and messages,
  workflow profiles, Checks, deployment environments, releases, deployments,
  memories, model endpoints, prompt templates, token-usage records,
  request-history entries and details, personas, pipelines and their stages,
  skills, project profiles, playbooks, mission playbook snapshots, Harbor runner
  enrollments and coordination leases, and PostgreSQL and SQLite read
  coordination rooms, participants, messages and claims, through one column
  reader per entity. Columns are read by name. Each
  provider configures only how it stores booleans; a timestamp converts by the
  value the driver returns, so one stored as text or as a zone-less timestamp
  reads as the same UTC instant, with its sub-second digits, whatever the host
  time zone. A required column that is missing, null or unconvertible, a stored
  enum name that is not a defined member where the model has no fallback, and a
  JSON column that holds invalid JSON raise `StoredRowException` naming the
  entity, column and provider instead of reading as a default value; objectives
  and refinement sessions raise their own stored-data exceptions naming the row
  and field, so a list read skips and reports that row. An empty stored
  captain quarantine reason and an empty voyage planning session or message id
  read as none on every provider.
- **Data expiry keeps the records that live only as events:** the newest
  snapshot of every incident, open or closed, and of every runbook execution
  survives the retention cutoff whatever its age, while their older snapshots
  still expire. Objective deletion tombstones and typed-decision reversals are
  never expired. The purge summary names each kept class
  (`kept_incident_latest`, `kept_runbook_latest`, `kept_tombstones`,
  `kept_reversals`, `kept_dispatch_attempts`) with its count.
- **Every CLI captain receives its prompt on stdin:** Claude Code, Codex
  (`exec -`), Gemini and Mux no longer pass the brief as a command-line
  argument, joining Cursor and OpenCode. A brief routinely exceeds the Windows
  command-line limit (32,767 characters, 8,191 through the cmd.exe that runs an
  npm `.cmd` shim), which made a Windows launch fail. Mux had sent the prompt
  both ways and ignored stdin.
- **Every auto-refreshing dashboard page reports a failing load once:** the
  list and detail pages that still reopened the error dialog on each failed
  refresh (among them Dashboard, Server, Incidents, Check Runs, History,
  Request History, Objectives, Vessels, Fleets, Docks and the fleet, dock and
  voyage detail pages) open it once while the load keeps failing and again
  only after a load succeeds. Request History tracks its summary apart from
  its list. The Incidents search box uses the shared search debounce.
- **Shared service and runtime cases carry the checks their unit-runner copies
  added:** twelve shared cases now also assert DataExpiry delete counts, dock
  MCP configs and git anchors, the launch prompt's perform-now wording,
  preserved objective preparation, planning objective lineage, the Judge and
  push-form template rules, the dashboard sibling-path probe, the autonomy
  scripts, and Claude Code `stream-json` output. The twelve unit-runner and
  runtime copies are removed.
- **The shared prompt case for sanitized existing instructions reads the
  generated file:** it read the dock-root `CLAUDE.md` the test itself wrote, so
  it passed even when sanitization was skipped. It now reads
  `.armada/instructions/CLAUDE.md`, and the identical unit-runner copy is
  removed.
- **Unit-runner database and service cases that a shared case already runs
  are removed:** 361 unit cases and 4 runtime cases repeated a shared case with
  the same setup, action and assertions. The shared copy runs in the same gate,
  under the NUnit and xUnit adapters, and against the server database providers.
  Twenty-five unit suites that held only such cases are removed with their
  registrations and shard weights. The shared end-to-end cases and their
  automated-runner copies both stay, because the two harnesses differ in server
  lifecycle and database provider.
- **A captain that ends BLOCKED and keeps running is finished:** the stall
  nudge is withheld and the process is stopped after the terminal-marker grace
  period, so the stage fails with its question instead of being told to
  continue without the owner's answer. BLOCKED is still not a completion
  claim.
- **A stage that ends `[ARMADA:RESULT] BLOCKED` waits for the owner:** every
  persona and mission mode reads a blocked result through one rule: the final
  result or verdict marker at the start of a line is `[ARMADA:RESULT] BLOCKED`.
  The mission fails with a `captain_blocked:` reason that carries the captain's
  question. It does not hand off, its later stages are cancelled, and the voyage
  fails instead of completing. A blocked Judge is not re-run as a missing
  verdict, and autonomous recovery dispatches no rescue. The question reaches the
  owner on an open incident and an owner-addressed board note. A BLOCKED marker
  followed by a later result, or prose that mentions BLOCKED, is not blocked. The
  Architect parser uses the same rule, and the log screen does not read a tail
  that already ends blocked.
- **Mux MCP servers files use the auth fields Mux reads:** a Mux captain's
  scoped `--mcp-config` file and the `armada mcp install` Mux entry write
  `auth` as `type` plus `bearerToken` (or `apiKeyHeader` and `apiKeyValue`).
  Mux ignores unknown auth fields, so the files sent no credential and the
  endpoint refused every Mux captain. The captain tool inventory reads the same
  file through the same model, so its probe sends exactly the headers Mux sends.
- **Missions on tenant-owned vessels carry an MCP credential:** a mission
  whose vessel has no user owner runs as the default user of its tenant, as
  the ownership policy assigns every record without a user. These missions
  launched with no Armada MCP credential, so no captain of any runtime reached
  the Armada tools. The owner must be an active user of the mission's tenant,
  so a user of one tenant never lends its privileges to another tenant.
- **Mux is built against a patched Voltaic:** the admiral image pins Mux to an
  exact commit and builds it against Voltaic with
  `docker/patches/voltaic-mcp-http-client.patch` applied. With the patch, the
  MCP client reads an event-stream POST response and accepts any JSON `ping`
  result, so a Mux captain loads the Armada MCP tools. The image build fails
  when the patch does not apply.
- **Dispatch captain assignments reach the first stage:** a voyage stores its
  `captainAssignments` when it is created, before any mission exists, so the
  root stage's first assignment resolves the named captain as its requested
  captain on plain, alias, REST and WebSocket dispatch. A single-stage dispatch
  stamps its stage persona (`Worker` when no pipeline resolves) on each mission,
  so a `Worker` assignment and the persona's default captain and default
  playbooks apply to it. An admiral without captain-assignment support refuses
  them instead of dropping them.
- **Planning sessions on every provider:** MySQL and SQL Server store planning
  sessions and their transcript messages with the same columns, indexes,
  per-session sequence uniqueness and session-to-message cascade as SQLite and
  PostgreSQL (MySQL migration 103, SQL Server migration 106), so the planning
  routes, tools and captain chat work on all four providers. On SQL Server the
  user reference carries no foreign key, because SQL Server refuses a second
  cascade path from tenants.
- **Named coordination refusal:** on MySQL and SQL Server every coordination
  board operation refuses with `The <provider> database provider does not store
  the coordination board; use SQLite or PostgreSQL.`, which the REST routes return
  as `501`. The automated suite asserts that refusal on those two providers.
- **Parallel pipeline stages keep their submitted order:** each stage stores its
  submitted position (SQLite migration 110, PostgreSQL 113, SQL Server 105,
  MySQL 102), and stages that share an order read back in that position on every
  provider. MySQL and SQL Server returned same-order siblings by stage id.
- **SQL Server tenant and user delete:** the tenant- and user-scoped dock lists
  order by creation time, newest first, like the other providers. They ordered by
  a column docks do not have, so deleting a tenant or a user failed with `500`.

- **Code-index status reads are side-effect free:** the code-index status route
  and tool never clone a missing repository or update the vessel record. The
  status names the case with `RepositoryState` (`Available` or `Missing`), and the
  explicit index update is what clones the repository.
- **The code index follows its settings' data directory:** when
  `codeIndex.indexDirectory` is not set, the index lives in `code-index` under the
  settings' own data directory, not the process-wide default. The runtimes runner
  and the shared-suite xUnit and NUnit hosts redirect the default data directory
  like the other runners, so no test indexes a vessel into the live Armada home.
- **Diff readers share one parser:** `GitDiffPaths` reads hunk bodies by their
  counts and returns each file's one name, line counts and (on request) hunk lines;
  it also reads `--name-status -z` records. Change substance (rescue and planner
  checks, custom-decision `changed_paths`) names a C-quoted non-ASCII file or a
  name holding ` b/` correctly instead of dropping it. The code index reads changed
  names with `-z`, so a change to a non-ASCII file is re-indexed and counts as
  relevant staleness. Auto-land and critical-trigger size limits, the convention
  and dock-boundary secret scans, the banned-diff guard, the leak and
  change-substance model states, and the Judge diff stat count and scan an added
  line whose content starts with `++`. The change-substance state labels each hunk
  with its own file, and the Judge review diff elides a bulk data file whose name
  is quoted or holds ` b/`.
- **Dock boundary hooks:** the pre-commit and pre-push hooks read changed names
  with `-z`, so a protected path with a non-ASCII name (`_briefing/**`,
  `**/CLAUDE.md`) is blocked at commit and push time, and they read added lines
  per hunk, so a `+++` file header is skipped and an added line starting with `++`
  is scanned.
- **Merge-queue conflict files:** the conflicted-file list is read before
  `merge --abort` (NUL-separated, so names arrive unquoted), so the failure
  classifier and the trivial-conflict recovery decision see the real files instead
  of an always-empty list.
- **Provider limits have one definition:** the captain bench decision and the
  crash-loop classifier read the same provider signatures. A throttle (HTTP 429,
  "too many requests") or an overload (HTTP 529, "overloaded") at a process exit
  benches the captain and re-routes the mission instead of failing it and halting
  its dependents; with no published retry time it takes the configured default
  backoff. Status codes count only in a status form, and credit, billing and
  authentication signatures are provider phrases, so a crash that mentions a pool
  capacity, a billing type, a process id, a line number or a filesystem
  permission stays a crash and reaches crash-loop quarantine. The terminal-exit
  "captain unavailable" check uses the same authentication rule.
- **Test failures keep their rescue:** autonomous recovery reads only the first
  line of a definition-of-done `Compile` or `TestFail` reason for its
  environmental and human-review markers, so a failing test named for
  authorization, quota or approval, or an assertion expecting `403 Forbidden`,
  no longer blocks the rescue.
- **Merge-queue host faults:** a merge-queue test run that the definition-of-done
  classifier reads as host trouble is classified `InfraTestFailure` and surfaced,
  not sent to a recovery captain. Every other post-merge test failure is surfaced to
  the operator as well and is never routed to a rebase captain.
- **Secret checks at landing:** the manifest-digest exemption covers only the
  digest, so a secret-shaped run beside a digest on a manifest line fails the
  landing scanner; the auto-land convention audit applies the same exemption and
  stores a CORE_RULE_5 violation with the secret replaced by `<redacted>`. The
  exemption recognises SHA-256 hex and SRI digests, and the base64 SHA-512
  `contentHash` values NuGet writes in `packages.lock.json`, so adding a NuGet
  package no longer fails the landing.
- **Typed-decision status:** the operator status view reads the one no-key
  effective-mode rule the settings use.

- **Process groups for AgentWake, self-deploy and Helm build steps:** AgentWake
  agent runs, self-deploy native commands and Helm `server start` build steps own
  a process group, so a timeout or cancellation also kills a background child that
  left the process tree. The bounded runner resolves the executable before it
  uses the group launcher; a missing or non-executable file starts without the
  group and fails to start exactly as it does without one.
- **Event types match their emitters:** `voyage.completed` is recorded once when
  the voyage completion rule writes a voyage Complete, `captain.stopped` when a
  captain stop stops the agent process, and `voyage.dispatched` also for rescue
  voyages and for voyages created without missions through REST or WebSocket.
  The known event type list names the stall evaluator's `captain.stall_confirmed`
  and `captain.stall_cleared` and drops `mission.created`, `captain.stalled` and
  `voyage.created`, which nothing writes; a unit test compares the list with the
  event types the source writes. The WebSocket generic-event example names types
  that are broadcast.
- **Regression objective links in the caller's scope:** a Check run, import or
  import-update and an incident create or update read `RegressionObjectiveId`
  with the caller's scope, like every other id in the body, and refuse an
  objective the caller cannot read.
- **Shutdown waits for the Harbor job expiry loop:** stop cancels the loop and
  waits for it with the same bounded wait as the health and endpoint-health loops,
  so an expiry pass never reads a disposed database.
- **Dashboard refresh cost and error dialogs:** Incidents, Check Runs and History read
  their name lookups (vessels, environments, deployments, releases, workflow profiles,
  objectives) once when the page opens; the refresh timer reloads only the list. On
  Captains, Prompt Templates, Users, Tenants, Credentials, Events, Signals, Merge
  Queue, Missions and Voyages, a failed load opens the error dialog once; while loads
  keep failing it stays closed after it is dismissed, and it opens again for the next
  failure after a successful load. The mission-history and token-usage charts share
  one time-label helper.
- **Dashboard controls for global administrators:** the workspace terminal and the
  check-run Command Override field show only to a global administrator, since the
  server runs either for no one else. The Credentials page offers no copy for a
  masked token (another user's credential) and does not send the mask back as the
  token when that credential is saved.
- **Dashboard check run page:** opening another run (the "Compared to" link, Retry)
  clears the previous run and shows the loading state; only the newest load writes,
  a failure to read the new run is reported, and a change event for another run no
  longer merges into the run on screen. A background reload failure stays quiet.
- **Dashboard Requests page:** a refresh keeps the selected requests it still returns,
  and the route, status code, principal, credential, tenant and user filters query the
  server once the user pauses typing, not once per keystroke.
- **Dashboard Events, Signals and Merge Queue column filters:** a column filter or a
  column sort covers every record, not only the loaded page. While one is active the
  page reads every record under its server filters and filters, sorts and pages them
  in the browser, as the Missions page does; without one it reads one server page.
  Missions and these three pages keep the selection to the rows their filters show.
- **Dashboard table selection:** a table's selection holds only rows its current
  search and filters show. Select-all takes the filtered rows; a row that a filter
  hides or a refresh no longer returns leaves the selection, so "Delete Selected"
  and other bulk actions never act on a row that is not on the list. Captains,
  Prompt Templates, Users, Tenants and Credentials use the shared table state
  (`useResourceTable`) with the other full-list tables, so the rule holds on each.
- **Dashboard server-paged lists:** Missions, Signals, Events, Merge Queue, Voyages,
  Memories, Check Runs, Incidents and Requests share one paging state
  (`useServerPaging`). When a reload finds the list now ends before the page on
  screen (rows deleted or expired), the page moves to the last page and reloads it
  instead of showing an empty table past the end.
- **Dashboard diff viewer:** the file list and per-file view read Git's diff headers by
  the same rules as the server: C-quoted names (non-ASCII or special characters) show
  the real name and open their own pane, a name containing ` b/` keeps it, and a rename
  or deletion is named from its rename and `---`/`+++` lines. Hunk lines are read by
  the hunk counts, so the `\ No newline at end of file` marker no longer shifts the
  gutter numbers and content that starts with `---` or `+++` is counted and numbered.
- **Dashboard pager:** the page-number box follows every page change the page makes
  (a filter change, a page-size change, a shrinking list), not only Prev and Next, on
  every paged table.
- **Image rebuild helper:** `rebuild-local-image.sh` passes `ARMADA_CLI_REFRESH`
  (digits only) as the Dockerfile's `CLI_REFRESH` build argument, so a rebuild can
  refresh the agent CLIs without editing the Dockerfile, and its behavioural test
  covers the `GIT_SHA` build argument the helper already sends.
- **Judge follow-up empty text:** every provider reads an empty optional Judge
  follow-up text column (tenant, user, voyage, vessel, merge entry, suggested
  follow-ups, audit notes, recommended action) as null, as it does for other
  records. The database runner's follow-up case checks this on every provider.
- **Scoped mission lists apply every filter:** tenant- and user-scoped mission
  lists apply the status, voyage, vessel, captain and mission filters on every
  provider; they applied only the creation-time filters and returned the rest
  of the tenant's missions. Full and summary mission lists at every scope use
  one filter set per provider. The database runner checks each filter at every
  scope, full and summary, on every provider.
- **Mission summary reads on every provider:** PostgreSQL, MySQL and SQL
  Server read Mission-shaped summaries (mission lists, the MCP and WebSocket
  mission reads, voyage status counts) without loading the description, diff
  snapshot or agent output, as SQLite did; they read full rows and discarded
  the text. Tenant- and user-scoped summary lists apply the voyage, vessel,
  captain, mission and status filters on those providers too. The SQLite
  summary read returns stage order and the review fields. All four providers
  share one summary column list. The database runner checks the light fields,
  the filters, the grouped voyage counts and that no heavy column is loaded,
  on every provider.
- **Database runner coverage for landing jobs and Judge follow-ups:** the
  database runner round-trips landing jobs (create, read by id and merge
  entry, state list, update, reopen, delete) and Judge follow-ups (upsert,
  the pending and unassociated lists, single association, audit completion,
  update, reopen) on every provider, comparing every property.
- **Coordination board storage:** the SQLite participant heartbeat is one
  insert-or-update statement, so two first heartbeats for the same key no
  longer race to a unique-constraint failure. Extending a participant's active
  claims returns the number of claims extended on SQLite and PostgreSQL; it
  returned 0 every time. The database runner has round-trip cases for
  coordination rooms, participants, messages and claims; MySQL and SQL Server,
  which do not store the board, report each case as a named skip.
- **SQL Server captain delete:** SQL Server clears the signal references to a
  captain and deletes it in one transaction, and clears only the references to
  captains the delete's tenant or user scope matches. A scoped delete that
  matches no captain, or a delete a referencing row refuses, leaves every
  signal reference in place. The database runner checks this on every
  provider.
- **PostgreSQL playbook empty text:** PostgreSQL reads an empty playbook
  tenant, user or description, and an empty snapshot playbook id, description,
  resolved path or worktree path, as null, as the other providers do. The
  database runner has a playbook round-trip case that checks this on every
  provider.
- **PostgreSQL runner enrollment compare-and-set:** a Harbor runner
  re-enrollment that expects a generation above zero updates the existing
  inactive row or is refused. It no longer inserts a new enrollment when the
  row is gone, which matches SQLite, MySQL and SQL Server.
- **PostgreSQL skill and project profile timestamps:** skill and project
  profile timestamps (stored as text) read through the shared PostgreSQL UTC
  reader, which honours the stored offset, so a host outside UTC no longer
  shifts them by its offset. The creation-time filters on both lists cast the
  stored text to `timestamptz` before comparing, instead of comparing text with
  a timestamp. The database runner has a create, read, update, reopen,
  creation-window and delete case for skills and for project profiles that
  compares every property on every provider.
- **PostgreSQL timestamps read as UTC:** objective `CreatedUtc` and
  `LastUpdateUtc`, model endpoint timestamps and Judge follow-up timestamps read
  through the shared PostgreSQL UTC reader, so they carry `DateTimeKind.Utc` as
  on the other providers and serialize with a `Z`. The database runner checks
  the kind and value of the objective and model endpoint timestamps after a
  reopen.
- **Protocol markers:** `[ARMADA:RESULT]` and `[ARMADA:VERDICT]` markers count
  only at the start of a line, after optional leading whitespace, for every
  reader: the completion claim used by no-op detection, refusal classification,
  the Architect `BLOCKED` result, the handoff-outcome check, the Judge verdict and
  its follow-up label, and progress parsing. A marker mentioned mid-line, glued to
  other text or wrapped in formatting is not a marker. A completion claim is one
  of the exact terminal values (`COMPLETE`, `PASS`, `FAIL`, `NEEDS_REVISION`).

- **Token usage input buckets:** every runtime records input tokens as three
  separate buckets, counted the same way for every provider: uncached input,
  cache-read input and cache-write input. A record's input is their sum and its
  total is input plus output. Records carry a counting-rule marker; rows and
  stored events from before the split read as the legacy rule, keep their stored
  counts and are never rewritten. Token-usage summaries on REST, MCP and the
  dashboard total each bucket from bucketed records only and report legacy input
  and the legacy record count apart. Every token count on `token_usage` is a
  64-bit integer on every provider. Migrations: SQLite 109, PostgreSQL 112, SQL
  Server 104, MySQL 101 (nullable bucket columns; existing counts widened to
  BIGINT in the same version).

- **Runtime provider failures:** every runtime writes one provider failure record,
  `[ARMADA:ACTIVITY] <runtime> error <message>`, for an error event, a failed turn
  (Codex `turn.failed`) and a terminal result that reports an error (Claude Code
  `is_error`, Cursor `is_error`, Gemini `status: "error"`), and the API-endpoint
  runtime writes it for a failed inference call. The provider's message is kept,
  redacted and bounded. Chat, planning and refinement fail the turn on this record
  from any runtime and record it as the failure reason; it never joins the reply.
  The displayed mission log never filters it out.
- **Runtime event parsing:** a tool argument whose value is not text reads as
  absent, so one mistyped argument no longer turns a Claude Code, Cursor or
  OpenCode event into its raw JSON line (tool output included) and no longer hides
  a protocol marker in the same event. Gemini and Mux streamed assistant text is
  joined into whole lines before it becomes a record, so the mission log holds
  one record per line instead of one per token, and a marker split across stream
  events still starts its own line; an unfinished line is written at the next
  tool, error or terminal event, or at process exit.

- **Voyage cancel reports alike on every surface:** REST, WebSocket and MCP
  cancel a voyage through one shared operation that writes one
  `voyage.cancelled` event and broadcasts the voyage and each cancelled mission
  (MCP broadcast nothing, and no surface wrote an event). MCP no longer stops a
  captain's process separately before the recall, which already stops it.
- **The same audit events on REST and MCP:** dock delete, purge, repair, unstick
  and batch delete, event delete and batch delete, merge entry purge and batch
  purge, and captain batch delete write the same `dock.*`, `event.*`, `merge.*`
  and `captain.batch_deleted` events on MCP as on REST, from one definition
  (MCP wrote none).
- **One vessel delete:** REST, WebSocket and MCP delete a vessel through one
  shared operation. Every surface cancelled live missions in the database only,
  leaving their agents running; the shared delete cancels them through the
  mission cancel, which recalls the captain. WebSocket cancelled fewer statuses,
  kept the missions (pointing at a deleted vessel) and removed worktrees with a
  raw directory delete; every surface now deletes all of the vessel's missions,
  purges docks through the dock service, removes the dock directory under the
  configured docks root (MCP used a fixed home path without the name check), and
  writes `vessel.deleted`.
- **One bare voyage create:** a voyage created with no vessel or missions goes
  through one shared create on REST and WebSocket. WebSocket dropped the
  selected playbooks; REST created the voyage before it checked the selections,
  so an unknown playbook left a cancelled voyage behind. Both now resolve the
  selections first and refuse an unknown playbook (REST `400`) without creating
  anything, and the new voyage is broadcast.
- **One mission metadata update:** REST `PUT /api/v1/missions/{id}`, WebSocket
  `update_mission` and MCP `armada_update_mission` change only the fields the
  request names. REST and WebSocket cleared an omitted title or description and
  ignored `Persona`; WebSocket stored a dependency on a mission that did not
  exist; MCP read the dependency outside the caller's scope. Every surface now
  checks a changed dependency or parent in the caller's scope and broadcasts the
  change.
- **One mission diff reader:** REST, WebSocket and MCP read a mission diff through
  one reader: the saved diff file, then the stored diff snapshot, then the live
  worktree. MCP read the mission's summary row, which carries no snapshot, so it
  reported no diff for a mission whose worktree was reclaimed. REST returns `404`
  when there is no diff instead of an error body under HTTP 200.
- **One captain create and update:** REST, WebSocket and MCP create and update
  captains through `CaptainAdministrationService`, which owns the name rule,
  runtime-option normalization for whole-body writes, and model validation
  (including an endpoint change on MCP updates). WebSocket skipped validation and
  normalization, so it created an API-endpoint captain with no model endpoint
  that REST and MCP refuse. The route tests that asserted on the handler source
  text are replaced by behavioural tests of the shared rule.
- **A halted voyage stops its running captains:** when a mission fails without
  recoverable work, its voyage is halted through the one voyage cancel, so the
  captain of every parallel mission still running is recalled (its agent process
  stops and the captain is released) before the mission is written Cancelled.
  The halt used to cancel the missions in the database only, leaving their
  processes running and their captains Working.
- **A retried landing decides its voyage:** REST `retry-landing` and MCP
  `armada_retry_landing` apply the voyage completion rule after the landing, so
  a Failed voyage whose failed work a retry later lands becomes Complete and
  raises the completion hook however old the failure is (the periodic sweeps
  revisit a Failed voyage only for 24 hours, so an older one stayed Failed).
- **One merge cancel that never rewrites a finished entry:** REST
  `POST /api/v1/merge-queue/{id}/cancel` (new), the cancel branch of
  `DELETE /api/v1/merge-queue/{id}`, WebSocket `cancel_merge` and MCP
  `armada_cancel_merge` run one shared cancel. A Landed, Failed or Cancelled
  entry keeps its outcome and completion time (WebSocket and MCP rewrote a
  Landed entry to Cancelled), an unknown entry is refused instead of reported
  cancelled, and a cancel writes `merge.cancelled`. `MergeQueueService.CancelAsync`
  itself leaves a finished entry unchanged, and the merge entry terminal set has
  one definition, `MergeStatusRules.IsTerminal`.
- **One mission restart, and LandingFailed is not restarted:** REST, WebSocket and
  MCP restart run one shared operation whose eligibility lives in
  `MissionRestartService`. A LandingFailed mission is refused on every surface
  and the refusal names retry-landing (WebSocket restarted it and discarded its
  branch and commit). Every surface records a restart signal owned by the
  mission's owner, writes `mission.restarted`, and broadcasts the change. REST
  refuses with `409` instead of a `400` body under an HTTP 200, and rejects an
  unreadable restart body instead of ignoring it.
- **One mission and voyage purge:** mission purge, batch mission delete, voyage
  purge and batch voyage delete run one shared rule on REST, WebSocket and MCP.
  A mission a captain is working is refused (mission purge had no guard, and MCP
  and WebSocket deleted the running captain's worktree). Every surface removes a
  purged mission's dock record and worktree through the dock service and its
  ownership guard, and deletes its log files and saved diff (REST left all of
  them behind); every surface writes the `mission.deleted`, `voyage.deleted` and
  batch events (only REST did).
- **One mission cancel:** REST `DELETE /api/v1/missions/{id}`, WebSocket
  `cancel_mission` and MCP `armada_cancel_mission` run one shared cancel. A
  Complete, Failed or Cancelled mission is refused and keeps its outcome (all
  three rewrote it to Cancelled). A running mission's captain is recalled, which
  stops its agent process (REST left it running; WebSocket released the captain
  in the database only). Stages waiting on the cancelled mission are cancelled
  with it, and every surface writes `mission.cancelled` and broadcasts the change.
- **One session log reader:** mission and captain log pages read the same way on
  REST, WebSocket and MCP. Every surface resolves a mission's newest non-empty
  sidecar log when its canonical log is empty, filters runtime noise, clamps the
  page, counts lines alike, and redacts secret-shaped values; the WebSocket
  `get_mission_log` and `get_captain_log` commands returned them unredacted.
- **Tenant scope for ids in request bodies:** voyage and mission create, mission
  update, vessel create and update, vessel build-context, merge-queue enqueue,
  incident create and update, and planning-session create read every record their
  body names by id with the caller's scope, as they read a path id, and return
  `404` (`400` for an incident create) for a record outside it; the MCP and
  WebSocket create surfaces and the MCP `armada_update_mission` tool apply the
  same rule. The checked ids include a vessel's `FleetId`, a mission's `DockId`
  and an incident's `RegressionObjectiveId`. Caller-less paths read linked
  records only inside the owning tenant: mission dependencies, Judge follow-up
  association, incident lifecycle evidence, and fleet and captain default
  playbooks.
- **User and credential privilege:** a tenant administrator manages the users of
  its own tenant except a global administrator and a protected user; changing,
  deactivating, deleting, resetting the password of, or minting, changing or
  deleting a credential for either returns `403`. Credential reads return the
  bearer token only to a global administrator and to the credential's own user;
  every other caller receives `********`, and a non-global caller's credential
  update keeps the stored token. The creation response still returns the new
  token once.
- **Shutdown authentication by default:** `RequireAuthForShutdown` defaults to
  `true`, so `POST /api/v1/server/stop` and `/restart` require a global
  administrator unless a deployment turns the setting off. Helm sends its
  configured credential, so local stop and restart keep working.
- **Host details and server-wide routes:** `GET /api/v1/doctor` shows the settings
  path, database location and runtime binary paths only to a global
  administrator; other callers receive the same checks without them. The Mux
  runtime routes are global-admin only. Production summary lane time counts, for
  a caller that is not a global administrator, only lanes whose every vessel
  belongs to the caller's tenant.
- **Ordinary-user reads and writes:** an ordinary user's recent signals, a
  captain's signals, vessel git status, the vessels of a fleet, the missions of a
  voyage and the active missions in workspace status are limited to its own
  records, as its lists are. Workspace file writes and token-usage
  delete-by-filter require a tenant administrator, like the vessel and event
  writes beside them. Approve, deny, restart and status-transition signals carry
  the mission's tenant and user. A tenant administrator no longer reads the
  captured requests of a global administrator in its tenant.
- **Enumerate routes:** every `POST .../enumerate` takes the permission level of
  the `GET` list it mirrors, so an ordinary user enumerates playbooks, workflow
  profiles, environments, objectives, backlog and every other listable
  collection it may already list.
- **Foreign ids read as missing:** for a caller that is not a global
  administrator, dispatch preview describes another tenant's captain override and
  pipeline override exactly like a missing one, and Harbor runner enrollment and
  revocation answer another tenant's credential or runner exactly like a missing
  one (an enrollment another tenant holds reads only as `runner_already_enrolled`).
  A deployment environment cannot move to a vessel in another tenant, and deleting
  an objective with no row purges only the caller's own tenant's orphan snapshots.
  update, vessel build-context, merge-queue enqueue, incident create and update,
  and planning-session create read every record their body names by id with the
  caller's scope, as they read a path id, and return `404` (`400` for an
  incident create) for a record outside it; the MCP and WebSocket create surfaces
  apply the same rule. Caller-less paths read linked records only inside the
  owning tenant: mission dependencies, Judge follow-up association, incident
  lifecycle evidence, and fleet and captain default playbooks.
- **Pipeline writes:** REST, MCP and WebSocket create, update and delete a
  pipeline through one service, so a payload is validated, defaulted and stored
  the same way on each. MCP `create_pipeline` and `update_pipeline` accept
  `requiresReview` and `reviewDenyAction`, and a replacement stage list keeps
  each omitted stage field from the existing stage for the same persona, so an
  update no longer turns a review gate off by leaving it out. A stage list without
  orders runs in list order on every surface; REST and WebSocket stored such a
  list with every stage at order 1, which ran them all as parallel siblings. MCP
  accepts `order`, so it can define parallel stages too, and a list that orders
  only some stages is refused. A missing name, an empty stage list (create or update) or a stage
  without a persona is refused on every surface, and a duplicate name returns
  `409` instead of a database error. REST and WebSocket read only the allow-listed
  fields, so an id, tenant, built-in flag or timestamp in the body is ignored. A
  global administrator's update or delete by name reaches its own tenant's record
  before another tenant's.
- **Objective writes:** REST objective and backlog routes and the MCP objective
  and backlog-item tools create, update and delete through the same objective
  service methods. MCP `update_objective` and `update_backlog_item` pass every
  field the service reads, so `autoDispatchEnabled` is no longer dropped, and the
  clearable text fields declare `emptyStringClears`, so `""` clears them on MCP as
  it does on REST. A missing title returns `400` on REST instead of a server
  error; a missing objective is `404` and a linked record outside the caller's
  scope is `400` (it read as `404`). MCP refusals carry `Outcome`, and
  `delete_objective` and `delete_backlog_item` return a tool error for an unknown
  id instead of a protocol error. New MCP tool `delete_backlog_refinement_session`
  matches `DELETE /api/v1/objective-refinement-sessions/{id}`; both remove the
  session from its objective's links through one method.
- **Persona writes:** REST, MCP and WebSocket create, update and delete a
  persona through one service. REST and WebSocket read only the allow-listed
  fields, so an id, tenant, built-in flag or timestamp in the body is ignored,
  and they accept `DefaultPlaybooks` on create and update (an array, or the JSON
  text a stored persona carries); an update through them had ignored it. A
  missing name or prompt template is refused on every surface instead of
  creating a persona named `Worker`, and a duplicate name returns `409`. A global
  administrator's update or delete by name reaches its own tenant's record
  before another tenant's. Deleting a built-in pipeline or persona returns `400`
  on every surface; a tenant administrator of another tenant got `404` on REST.
- **Workflow profile writes:** REST and MCP create, replace, delete and validate
  a workflow profile through one service. A REST replace keeps
  `EnvironmentVariables` instead of dropping them, and both surfaces trim ids and
  commands on create and replace. Validation answers exactly what a create would
  do for the same caller: a global administrator's profile belongs to the tenant
  it names, else the tenant of its fleet or vessel, so REST validate, MCP
  validate and create agree. A blank name returns `400` (MCP: code `invalid`)
  instead of a server error.
- **Validate routes do not reveal other tenants' records:** workflow-profile and
  project-profile validation (and create) read fleets and vessels within the
  caller's scope, so another tenant's fleet or vessel reads exactly as one that
  does not exist. Project-profile validate needs a tenant administrator, as its
  create does.
- **Playbook writes:** REST and MCP create, update and delete a playbook through
  one service. `PUT /api/v1/playbooks/{id}` changes only the fields the body
  names, as `update_playbook` does; it had replaced the whole record with model
  defaults, so a body naming only `FileName` failed. REST create reads only the
  allow-listed fields, so a body id or timestamp is ignored. A non-`.md` file
  name or missing content returns `400` (MCP: code `invalid`) instead of a server
  error, and a duplicate file name returns `409` (MCP: code `conflict`).
- **Prompt template writes:** REST, MCP and WebSocket create and update a prompt
  template through one service. MCP `update_prompt_template` no longer creates a
  missing template (it skipped the create checks); it returns code `not_found`,
  as REST returns `404`. MCP create trims the name and category as REST does and
  refuses blank content. REST create requires `Category` instead of defaulting it.
  Updates on every surface find the template through the caller scope and apply
  the edit rule, and accept `Category` and `Active`; an empty `Description`
  clears it.
- **Built-in personas and pipelines:** only a global administrator may change a
  built-in persona or pipeline, because every tenant uses them; a tenant
  administrator of the tenant that stores them receives `403`.
- **Health loop isolation:** each admiral health-check sub-step and each of the four
  background sweep triggers runs as its own isolated step. A step that throws is
  logged and counted by name, and dispatch, the captain pool, voyage completion,
  escalation and every sweep still run in the same cycle.
- **Coordination board fleet notes:** `[fleet]` notes for mirrored event types come
  from the event store itself, so admiral, mission-service and landing events reach
  the board. Voyage dispatch records a `voyage.dispatched` event.
- **Settings reload:** `crashLoopDetection` and `definitionOfDone` reload in place,
  so the crash-loop tracker and the definition-of-done gate use the new values
  without a restart. The gate is always constructed and records a "disabled" skip
  while off. `codeIndex.stalenessSweepIntervalCycles` and the model-endpoint health
  interval also follow a reload.
- **Disk lifecycle:** passes never overlap; a second pass waits for the running one.
  A folder that cannot be listed is logged and reported by path in the report's
  `Errors` and `ErrorCount` instead of reading as nothing to reclaim.
- **Authentication and scope:** REST, MCP, WebSocket, chat, planning, and captain
  launch paths apply caller ownership. Captains receive scoped credentials;
  administrative tools and cross-tenant events remain restricted. Server-owned
  captain fields and write-only secrets have consistent write contracts.
- **Host command execution:** a check-run `commandOverride`, retrying an imported
  check, and `POST /api/v1/workspace/vessels/{id}/exec` run as the server
  process, so they are global-admin only and refused with `403` for every other
  caller before any process starts. Check-run writes require a tenant
  administrator, and `Deploy` and `Rollback` checks run only through the
  deployment workflow.
- **Check-run links and gates:** check-run run and import refuse a mission,
  voyage or deployment outside the caller's tenant. The voyage, Judge, manual
  completion, supersession and recovery readers count only Checks in the gated
  record's tenant, so another tenant cannot write evidence into a gate.
- **Vessel server paths:** `LocalPath`, `WorkingDirectory` and a local-path or
  `file:` `RepoUrl` are set only by a global administrator on REST, MCP and
  WebSocket; other callers are refused on create and keep the stored paths on
  update. A vessel name must be one safe path segment, and vessel removal deletes
  the dock directory only for such a name.
- **Dispatch and pipelines:** operator and scheduler paths share objective defaults,
  admission, and Check arming. Root missions resolve and persist explicit or
  inherited start commits, return `MissionStartRefs`, and record resolution events.
  Alias dependencies, parallel-stage barriers, review gates, report-only modes,
  and cancellation use consistent lifecycle rules.
- **Voyage completion:** one rule decides when a voyage becomes `Complete` or
  `Failed`. Mission state changes, the health-loop completion check and the
  landing drain all apply it, so they reach the same answer for one voyage.
  Each path raises the voyage completion hook exactly once for each voyage it
  ends; the mission path did not raise it before. A `Complete` or `Cancelled`
  voyage is never rewritten by completion. A decision on a held review under a
  cancelled voyage leaves the voyage `Cancelled`. A `Failed` voyage moves only
  to `Complete`, when its failed work has since landed and its Checks are green.
  It is never written `Failed` again and never reopens. A Pending or Running Check holds completion on every path, and a
  failed Check fails the voyage on every path. A `PullRequestOpen` mission and a
  `WorkProduced` mission held for operator review keep their voyage open.
- **GitHub objective refresh:** re-importing a `Completed` or `Cancelled`
  objective keeps its status; a merged pull request no longer moves it back to
  `Released`. An explicit `StatusOverride` still sets any status.
- **Captain assignment commit:** assignment claims the captain with its
  compare-and-set first and records the dock on the mission only while the mission
  is still Assigned. A lost claim returns the stored mission to Pending with no
  captain, dock or new branch (`WaitingForIdleCaptain`, logged as
  `captain_claim_lost`) and deletes the provisioned dock. A mission that changed
  status keeps that status. Every assignment failure path, including an unresolved
  start ref, a failed dock provision and a failed base check, releases the captain
  only while it still records that mission, so a captain another mission claimed
  meanwhile stays Working. The database drivers carry no transaction wrapper.
- **Review and landing:** Judge PASS requires distinct evidence for every criterion
  and matching immutable Check results. Brief trimming retains the full contract.
  Landing verifies ancestry, preserves a diverged working checkout, and reports
  failed synchronization without discarding its commits. Direct landing and the
  merge queue read the vessel, changed paths and diff through one evidence
  collector and refuse with a named `landing_evidence_unavailable` reason when a
  read fails. Changed paths are read NUL-separated, and one diff-path reader
  decodes Git quoting and keeps the old path of deletions and both paths of
  renames for protected paths, auto-land predicates, critical triggers and
  consumer-test triggers. Branch diffs surface unexpected Git errors instead of
  substituting a working-tree diff. Rollback after a failed landing is a
  conditional push against the inspected head, and merge-queue test commands
  drain both output streams and stop their process tree at
  `MergeQueueTestTimeoutSeconds`.
- **Consumer-test triggers:** reading a producer's changed paths returns a
  result that separates an unreadable change from a verified empty one. When the
  paths cannot be read, the definition-of-done gate runs every consumer suite that
  has trigger prefixes and logs the named `changed_paths_unavailable` reason.
- **Recovery and captain lifecycle:** recovery preserves accepted source commits,
  respects dispatch holds, distinguishes provider and test failures, bounds retries,
  and checks rescue effectiveness against the declared deliverable. Terminal
  markers end stages, and every marker in a multi-line output record is routed,
  so a message or papercut cannot hide a result or verdict. A captain's
  `[ARMADA:STATUS]` marker sets only InProgress, Testing, or Review, so post-work
  and terminal states always pass the completion and landing checks. Interrupted
  and duplicate completions retain launch identity. Cancelling a voyage also
  cancels its Testing and Review missions, including one waiting for a review
  decision, and recalls a captain that still holds one.
- **Persona minimum tiers:** a persona carries an optional minimum capability
  tier in place of the specialist flag. Dispatch, the dispatch preview and
  model-list health combine it with the mission's request by taking the higher
  tier, and no request is capped below what it asks for. Existing specialist
  flags become a Premium floor at migration (Test Engineer: Standard). Persona
  writes that still send the retired `specialist` flag are refused as
  `specialist_retired` on REST, MCP and WebSocket instead of being ignored.
- **Dispatch hold:** automatic Checks obey the hold. The heartbeat runs no
  Pending Check while the hold is engaged, records one named deferral event per
  engagement, and runs the waiting Checks after the hold clears. Operator check
  runs are not held.
- **Stall recovery:** a stalled captain's process is registered as superseded
  before the heartbeat stops it, and the process-exit handler ignores that exit
  and any exit from a process that is no longer the captain's recorded process,
  so a stall kill is never read as a failure or an out-of-memory kill and never
  requeues or reclaims a relaunched mission. The terminal-marker stop uses the
  same registry. A relaunch is written back only while the mission keeps its
  status and captain; otherwise the relaunched process is stopped, the captain
  is released, and `captain.recovery_abandoned` is recorded. While the dispatch
  hold is engaged a stalled captain is stopped and not relaunched: the mission
  returns to Pending, the captain is released,
  `captain.recovery_deferred_dispatch_hold` is recorded once per mission and
  engagement, and assignment skips the mission until the hold clears.
- **Per-mission process state:** a mission's streamed output buffer,
  final-message artifact, first terminal marker and heartbeat throttle are owned
  by the launch that started the mission's current process. Each launch takes
  ownership and starts that state empty; a process exit releases it only while
  the exiting process's launch still owns it. A superseded or stale process's
  late exit, or the exit whose handling relaunched the mission, never clears the
  relaunched process's output or terminal marker.
- **Child processes:** one bounded runner in Core runs short-lived child
  processes. It reads both output streams at once, keeps each within a byte budget
  (beginning and end, with a marker and a count of the omitted bytes), kills the
  process tree on a timeout or a caller cancellation (and the process group for
  shell commands), and gives the readers a bounded drain window after exit or kill,
  so a background child holding a pipe cannot hang the call. `run_command`,
  merge-queue git and test commands, and self-deploy native commands use it.
  Containment differs by platform (see the next bullet).
- **Escaped descendants on Linux:** a group-owning run tags every process it
  starts with a run-unique `ARMADA_CONTAINMENT_ID` in its environment. A timeout
  or cancellation, after the group and tree kill, kills every process of the same
  user that still carries the identifier, so a descendant that called `setsid`
  and double-forked out of the tree and the group dies with the run. Processes
  without the identifier are never signalled; the result names the processes
  killed, the ones it could not read, and an incomplete sweep. macOS still lets
  such a descendant escape (its environment is not readable), and on Windows only
  the live tree is killed.
- **Git processes:** every admiral git command (GitService, pinned anchors, code
  index, workspace, branch writes, readiness, vessel routes, dock seeding and hook
  lookup, landing ref reads, ref audit, disk cleanup, check checkouts, the Slop
  check, git inference, diagnostics) and `gh`/`glab` runs through the bounded
  runner with prompts and the pager off. A hook or warning flood on stderr no
  longer blocks git until its timeout. Network git that had no bound (readiness and
  vessel-route fetch, dock seed push) uses the git timeout. The Slop check fails
  with a named reason when the reviewed diff is larger than 64 MiB instead of
  reading part of it.
- **Check and probe commands:** check runs, Definition-of-Done commands, workspace
  exec, Mux CLI calls, the Docker probe, runtime MCP listings, account login probes,
  version and `command -v` probes, and notifications run through the bounded
  runner. A caller cancellation kills the command instead of leaving it running,
  check and gate commands own their process group, and a failing-test set read
  from truncated output is marked incomplete.
- **Wake, daemon and Helm build output:** AgentWake agent runs and the Helm
  `server start` build steps (server build, `tsc`, `vite`) run through the bounded
  runner, so a child that fills its stderr pipe, or fills it before reading its
  input, no longer hangs the wake or the CLI; a build step that never finishes is
  killed after 15 minutes. The OpenCode server the admiral launches has both output
  streams drained at once into a fixed per-stream budget instead of being held
  whole for the life of the daemon. Helm's MCP client install and remove commands
  run through the bounded runner too, with a 2 minute kill, and `server start` on
  Unix sends the Admiral's console streams to `/dev/null` (the Admiral writes its
  own log), so the server never stops on a full pipe nobody reads.
- **Captain administration:** REST, MCP, WebSocket and the dashboard share one
  service for single stop, emergency stop, deletion and restart. A single stop ends
  a Planning or Refining captain's session and stops and recalls any other captain.
  Stop all covers working captains, planning sessions and refinement sessions and
  reports stopped and failed counts with each failure named; a session kind the
  database provider does not store is named in `UnavailableSources` and the stop
  continues with the other kinds; Helm `captain stop-all`
  calls it once and prints those counts. Single and batch deletion refuse Working,
  Planning, Refining and mission-owning captains and remove the captain's events,
  planning sessions and refinement sessions; a dependent that cannot be removed is
  logged, counted and named in the result. Restart resets runtime state in place
  and keeps the captain's identity, configuration, credentials and holds.
- **Planning sessions on PostgreSQL:** PostgreSQL stores planning sessions and
  their transcript messages (schema migration 108) with the same scoping,
  ordering and message cascade as SQLite, so the dashboard planning page, the
  planning REST routes and MCP tools, planning timeline entries and stop all work
  on PostgreSQL deployments. MySQL and SQL Server still do not store planning
  sessions; their refusal names the provider.
- **Captain names:** captain create on REST, MCP and WebSocket applies one name
  rule: a name another captain already has is refused before anything is written.
  REST answers `409 Conflict` with a `Conflict` error instead of a database error,
  and MCP and WebSocket return the same message as their error result.
- **Dock and ref handling:** active owners protect their worktrees; idle orphan
  processes cannot block reclaim indefinitely. Persistent collisions expose the
  holding path and process IDs and fail after a bounded retry. Managed ref deletion
  records its caller and refuses user and recovery refs. New bare clones enable
  reflogs. A Git transaction hook records raw ref deletions with durable,
  restart-safe event import and OS or verified mission attribution.
- **Model selection:** one capability-tier rule drives eligibility and preview.
  Smart Routing filters and orders eligible captains by account usage and persona
  preferences; dead model-list entries have visible diagnostics and fallback.
- **Dispatch preview role coverage:** the preview reports the captains assignment
  could choose by calling the assignment rules themselves: the mission's tenant,
  quarantine deadlines, Smart Routing persona routes, the requested captain from
  an override or the persona's default captain with its fallback tier floor, and
  a pinned model no captain runs as a tier floor. A requested captain whose persona
  allow-list, runtime capability, or minimum tier excludes the persona is never
  assigned; assignment falls back at its tier and names the reason.
- **Policy refusal continuation:** the alternate captains for a mission continued
  after a policy refusal are the captains assignment could choose: quarantine,
  including a quarantine deadline still in the future, the mission's tenant, the
  Smart Routing persona routes when Smart Routing is on, and the assignment selector. A pinned model that no alternate runs is a tier floor, and a
  captain below the pinned model's tier is not approved because it runs the model.
- **Unassignable missions:** the unassignable-by-construction check counts a
  captain only when the assignment selector could choose it, so a captain that runs
  the pinned model below the persona's minimum tier does not count. A mission whose
  Smart Routing persona routes admit no captain of its tenant that allows its
  persona is named `mission.unassignable_by_construction` and escalates to an
  incident, as a mission with no such captain at all does.
- **Settings reload:** the manual reload endpoint and the settings-file watcher share
  one reload path bound to the server's own settings file. Settings updates and both
  reloads share one candidate validator for account key paths and captain runtime
  bindings. A missing, unreadable, or invalid file is refused and the current
  settings stay live.
- **Live scheduler settings:** the autonomous objective scheduler reads and writes
  the live `autonomousObjectiveScheduler` settings section on every use, so a
  settings-file edit to its enabled flag, pause, interval, ceilings or fair-share
  flag takes effect on the next sweep, and a later scheduler tool change writes the
  edited values back instead of older copies. The branch cleanup cadence and
  preserved-ref retention also hot-reload.
- **Runtime execution:** inference-endpoint captains resolve endpoint credentials;
  API-endpoint missions have bounded command execution and deterministic conversation
  compaction, Anthropic stable-prefix caching, and provider cache/reasoning usage
  counters. Large output keeps diagnostic content and archives its full form.
  Runtime logs expose command and failure context, and launch configuration delivers
  the correct scoped MCP credential for each supported runtime.
- **Mux captain MCP delivery:** a Mux mission or chat launch passes its own
  Armada server file with `--mcp-config` and `--strict-mcp-config`, because
  `mux print` loads MCP servers only from that flag. The captain's config
  directory (`--config-dir`, or `~/.mux`) keeps selecting its endpoints and
  settings, so captains that share a config directory no longer share MCP
  servers, and a launch sets no `MUX_CONFIG_ROOT` variable, which Mux never read.
- **Mux captain tool inventory:** a running Mux mission captain's inventory
  lists the MCP servers its launch delivers through `--mcp-config`, derived from
  the launch plan, instead of the config directory's servers that `mux print`
  never loads; its Armada probe presents the credential the launch resolves for
  the same mission, and a Mux CLI that cannot start no longer hides the
  delivered servers. It always lists a Mux Built-In Tools entry whose tool
  calling flag, base URL and adapter come from the endpoint the launch selects in
  `endpoints.json`, and it states that the built-in tool count and names are not
  reported by `mux --version`, because only `mux probe`, which calls the
  provider, reports them.
- **API-endpoint tool bounds:** `run_command` owns a process group and kills it on
  timeout or cancellation, including children the shell left behind, and caps
  output while reading instead of after a whole line. `edit_file` and `multi_edit`
  refuse empty search text, observe cancellation, and list at most 20 candidate
  lines; each `multi_edit` step must be unique in the content earlier steps produced.
- **Agent process identity:** every stop, kill and liveness path for a local agent
  process (runtime stop and liveness, the captain health check, orphaned-mission
  recovery, the liveness heartbeat, autonomous recovery, planning-session cleanup)
  goes through one identity-checked lookup that disposes its handle. A live
  process whose start time differs from the recorded launch, or is later than the
  mission's launch, is a reused PID: it reads as gone and is never killed
  (`process_identifier_reused`). A live process with no recorded launch in the
  current admiral process, or an unreadable start time, reads as running but is
  never killed (`process_identity_unverified`). Each agent's start time is stored
  next to its process ID on the captain and mission records
  (`process_started_utc`, migrations SQLite 108, PostgreSQL 111, SQL Server 103,
  MySQL 100) and restored at startup, so an agent launched before a restart is
  still verified and stopped; a row stored without a start time stays unverified.
  Runtime stop returns a result (stopped, not running, or refused with a named
  reason); a refused stop is logged and recorded as `captain.stop_refused`, and the
  terminal-marker stop no longer reports it as a stop. Stop sends no shutdown
  request: a 3-second grace period, then a tree kill. A cancelled launch starts nothing, and a launch that fails after start
  kills its child. Gemini, Cursor, and Mux keep JSON error events in the mission log.
- **Launch and test races:** the liveness heartbeat reads its cancellation token
  before it registers the loop, so a process exit that stops the heartbeat while a
  launch starts it no longer fails the launch with a disposed-source error, and a
  loop removes only its own registration. A self-deploy child that exits and is
  reaped before its start time is read is recorded as started (with its launch
  time) and then reads as exited, instead of failing the launch with
  `launch_failed`. The shared and automated test hosts repeat a server start on new
  loopback ports when a found port is taken before the bind; request-history scope
  cases wait for the user's own capture, which is written after the response; the
  scheduler refill case waits for the event-triggered sweep to complete.
- **Indexing:** source chunks follow declaration boundaries, embedding clients use
  configured endpoints, captains search their own vessel, duplicate groups have an
  operator report, and dispatch staleness considers source relevance.
- **Index validity and enrollment:** semantic vectors belong to one embedding
  provider (model, endpoint and vector length); a provider change replaces every
  vector, including those on unchanged files. Index status reports embedding
  completeness apart from lexical freshness, and an update on the same commit
  retries missing vectors while it reuses valid ones. Every automatic refresh
  (landing, merge queue, stale dispatch, staleness sweep) shares one enrollment
  check and never indexes a vessel for the first time; only an explicit update
  does. The Admiral and the stdio MCP host select the inference client through one
  factory.
- **Search never indexes a vessel:** code search, fleet search, context packs and
  the baseline pack cache create no index for a vessel that has never been
  indexed and send nothing to the embedding provider. Vessel search and context
  packs return `Available: false` with `UnavailableReason: not_indexed`; fleet
  search and fleet packs list such vessels in `NotIndexedVesselIds`. An `auto`
  dispatch proceeds without code context and logs the reason; `force` fails and
  names it. A failed index update keeps the last successfully indexed commit, so
  the vessel stays enrolled and the staleness sweep still compares it.
- **Persistence and operations:** all database providers preserve delivery and
  ownership fields, validate migration prerequisites, and support expiry and backup
  evidence. Restore checks provider compatibility and retains local secrets.
  Status, inbox, jobs, and logs report bounded evidence and explicit failure reasons.
- **Stored-record fidelity:** every provider reads back all nine merge-entry audit
  fields through one shared column contract, and signal and event reads return their
  owning user. Pipeline create, update and delete write the pipeline and its stages
  in one transaction, so a failed write leaves the stored pipeline unchanged. The
  driver wiring check covers every method set.
- **Unreadable objective rows:** every provider reads objective list and document
  fields and enum columns through one shared rule. Malformed JSON or an undefined
  enum value is a read error that names the objective and field, never an empty
  list or a default that the next update would write back. Lists and the snapshot
  backfill skip such a row with a warning that counts and names it, and the
  scheduler never dispatches it.
- **Unreadable refinement session rows:** every provider reads the objective
  refinement session status through one shared rule. An unknown stored status is a
  read error that names the session and field, never a `Created` session; lists
  skip such a row with one warning that counts and names it.
- **Retention and history:** data expiry removes captured request history and its
  detail older than `requestHistoryRetentionDays` (default 30; `0` keeps it).
  PostgreSQL stores request capture times as ISO-8601 text, upgrades older rows,
  and indexes request history like the other providers, so same-day time filters
  and retention compare correctly. The history timeline leaves out a source the
  database provider does not store, names it in `UnavailableSources`, and logs it;
  captain deletion logs a skipped or failed dependent cleanup.
- **Delivery history filters:** PostgreSQL check-run, release and deployment
  queries bind UTC date bounds to their timestamp columns. Inclusive date ranges
  and paged totals work without a timestamp/text comparison error.
- **Provider test coverage:** the database runner checks the active-work footprint
  query on all four providers: active voyages count every mission, a mission
  without a voyage counts only while active, and terminal work and other tenants
  are excluded.
- **Helm server control:** `server stop`, `server restart`, `reset` and `config init`
  share one authenticated stop through `POST /api/v1/server/stop` and count the
  Admiral as stopped only when its health route no longer answers. A refused or
  unfinished stop exits non-zero, cancels a restart, and blocks every data deletion.
- **Lease, job, and shutdown lifecycle:** a dock lease release removes only the
  zero-count entry it observed, so a concurrent acquire keeps its lease. Sibling
  lease reconciliation takes the per-target lease lock and keeps a lease whose
  holder dock record is missing until the stale-lease grace has passed. A reaped
  background job is cancelled and its terminal status is final in memory and in
  the journal; each job status is journalled before it is visible. Admiral stop runs once, waits (bounded) for its background loops
  before disposing the database, and disposes it even when an earlier shutdown
  step fails.
- **Background jobs page:** the dashboard Jobs page and REST `GET /api/v1/jobs`
  and `GET /api/v1/jobs/{id}` read the Admiral's long-running jobs (dispatch,
  code index, merge processing, disk lifecycle and similar) from memory and the
  job journal, newest first, and require a global administrator, as
  `armada_job_status` does. The list counts journal records it could not read.
  The job-table service that nothing enqueued into, and
  `POST /api/v1/jobs/{id}/cancel`, are removed; long-running jobs have no cancel.
- **Capacity admission:** one global workload reservation covers the active-workload
  count through the durable Assigned write, so parallel assignments in different lanes
  launch no more captains than `MaxConcurrentCaptainWorkloads`. Fleet-capacity
  admission reads the active-work footprint in one query on every database provider
  instead of enumerating retained voyage and mission history.
- **Recovery bookkeeping:** active-incident lookups filter out closed and
  rolled-back incidents first and read every match, so newer closed incidents
  cannot hide an open one. Voyage cancellation, including captain recovery under a
  cancelled voyage, cancels only Pending, Assigned and InProgress missions.
  Per-mission recovery gates and nudge-suppression records are released once the
  mission is no longer in use.
- **Dashboard and clients:** responsive navigation, scoped controls, complete entity
  forms, structured mission evidence, routing and account controls, endpoint health,
  tool activity, and consistent REST/MCP/WebSocket contracts.
- **Operator entry-point parity:** voyage cancel on REST, WebSocket, MCP and remote
  control runs one operation that stops running missions' agent processes and
  cancels InProgress missions. Batch dock delete refuses active docks per ID like
  single delete. Mission create on REST, MCP and WebSocket merges vessel
  default playbooks through one helper.
  Batch merge-queue purge is limited to a tenant administrator's own tenant.
  Every REST route's auth refusal names `NotAuthorized` (401) or `Forbidden` (403)
  in the body to match the status, through one shared mapping.
  The Helm stdio MCP host supplies mission status transitions and the record-backed
  services the admiral endpoint supplies (review hold, AgentWake, releases,
  deployments, runbooks, captain bench, unlanded branches, terminal-voyage
  reconciliation, disk lifecycle and captain process stop).
- **WebSocket voyage dispatch:** `create_voyage` with a vessel and missions runs the
  shared voyage dispatch service that REST and MCP run, and accepts the same fields:
  captain assignments, pipeline, playbooks, code-context settings, objective and
  forced preflight, and stage skips. A captain override sent over WebSocket is
  stored on the voyage and routes the persona's missions, and the code-index gate,
  code-context preparation and dispatch hold apply as on REST. A refusal returns the
  shared code, the REST status and the full refusal body.
- **Harbor owner lookup:** the runner session registry awaits the durable owner
  lookup for the handshake, heartbeats, launches, stops and runner events, so a slow
  database never blocks a thread. One lookup is bounded by
  `Harbor.OwnerLookupTimeoutSeconds` (default 5); a lookup past the bound is refused
  as `runner_owner_lookup_timeout`, and a failed lookup as `runner_owner_unavailable`.
- **MCP tool results:** one rule decides that a tool result is an error: an
  explicit `isError: true` result, or a top-level non-empty `Error` string. HTTP
  returns such a result with `isError: true` and audits it as `Failed` with its
  message; local stdio applies the same rule. A null or nested `Error` is a
  successful result. `armada_job_status` reports a failed or lost job's reason in
  `FailureMessage`.
- **Remote proxy:** dashboard asset paths must resolve to the bundle directory or
  below it, so an encoded traversal cannot read a sibling directory with the same
  name prefix. A tunnelled request has one deadline covering send and response,
  releases its pending entry on every exit, and reports caller cancellation
  separately from a timeout. The tunnel protocol guide names the six advertised
  features, the two the proxy reads, and the `unsupported_method` answer to any
  method other than the generic relay.
- **MCP tool catalog:** pairs each objective tool with its backlog-named twin
  and states that both call the same operation.
- **Path containment:** one rule decides that a path is inside a root: it is the
  root itself or lies below the root plus a directory separator, compared
  ordinally after normalization. The server and proxy dashboards, check-run
  artifact collection and summary parsing, Workspace browsing, captain runtime
  configuration writes, Harbor directory mapping, dock occupant listing, and the
  API-endpoint workspace tools all apply it, so a sibling directory whose name
  starts with the root's name is outside. Prestaged-file copies, read-context
  staging and disk-lifecycle deletion apply it too, so a directory whose name
  differs from an allowed root only by letter case is not deleted. Self-deploy
  and build-drift reporting share one rule for which vessel holds the running
  server; it compares letter case the way the host's file system does.
- **Papercut merge:** the typed-decision merge works on copies of the listed
  groups, so an unavailable answer after an earlier merge returns the original
  groups with their original counts.
- **Dashboard loading:** a list refresh keeps the bulk selection for rows that still
  exist and drops only rows that vanished. Every read used as a complete set
  (picker options, id-to-name lookups, counts and filters over the whole set) reads
  every page through one `listAll` client wrapper per entity, so pickers and names
  stay complete past the server's page cap. A release lists its linked deployments
  through the release filter and names each linked check run by reading that run.
  A vessel's mission table shows the newest 1000 missions and says so when the
  vessel has more. Every list page whose query follows a filter, search, page or
  sort, and every detail page that follows its route id, applies only its newest
  load: a filter change, an auto-refresh tick, a manual refresh and a reload all
  start a load, and only the newest load writes the rows,
  totals, selection, spinner and errors, so a slower, older response never replaces
  a newer one. The Memories search sends one request with the final text after
  typing pauses, through a shared debounced-value hook, instead of one request
  per keystroke. A detail page shows the spinner and any load failure when the id
  changes and never shows the previous record. The Planning page reads the session
  list and the open session again after a reconnect or an event gap, and applies
  session, message and captain events that arrive while the open session is loading
  on top of the loaded session instead of dropping them. Only the current
  WebSocket drives connection state and reconnects; a replaced socket's late events
  are ignored. The home page's bounded mission-summary refresh has a dashboard test
  in place of source-text checks in the unit suite.
- **Mission brief model context:** the generated instruction file renders the
  vessel model context once as a `## Model Context` section when
  `EnableModelContext` is on and the text is not blank, on the direct and the
  template-resolved paths. The section is read-only background, is counted in the
  prompt-budget telemetry, and may be elided by the total-budget backstop; the
  persona memory guidance tells the captain to read it when the brief carries one.
- **Test ownership:** each behaviour has one executed test implementation. Shared
  copies of executed legacy cases and legacy copies of executed shared cases are
  deleted instead of skipped. Seeded-persona, model-context and default Claude
  argument cases assert the current contract, and the shared runner records no
  named skips.
- **Authentication and API contract tests:** the anonymous, wrong-key and
  empty-key cases probe one shared protected-route table and require exactly 401
  with a `NotAuthorized` body and no protected content; each refused write is
  checked with an authenticated read to prove nothing was stored or changed. The
  captain not-found cases require 404 with a `NotFound` error, the duplicate-name
  case requires 409 and one stored row, and the mission metadata binding cases run
  in the mission update section. Repeated CORS, valid-key and status auth cases
  are removed.
- **Commit secret gates:** the server landing gate and the dock pre-commit and
  pre-push hooks judge a line alike. Every PEM private-key header (bare, RSA, EC,
  DSA, OPENSSH, ENCRYPTED) is blocked. A pattern the server matches without case
  carries an `(?i)` prefix in `.armada/boundary.patterns`, and the hook matches it
  with `grep -i`, so `Bearer` and `Password` are caught at commit time. The hook
  entropy gate strips `=` padding before measuring, as the server does.
- **Typed-decision redaction:** decision state loses every key shape the display
  redactor removes before it leaves the host: labelled secrets, Bearer tokens,
  provider tokens by prefix (now also `github_pat_`, `xox*-` and `glpat-`, which
  the display redactor removes too), and the string value of a property named as
  a secret. Both redactors apply one rule set. `DecisionStateRedactor.Version` is
  3, so samples redacted under earlier rules form their own cohort.
- **Typed-decision egress guard:** one guard decides whether decision state may
  leave the host, and every sending path asks it: the adapter skeleton, custom
  decisions, the captain tools, and the standalone adapters `prior_art`,
  `memory_review`, `papercut_merge`, `memory_candidate`, `followup_routing`,
  `preflight`, `criteria_lint` and `inbox_triage`. A state about an excluded
  vessel or naming an excluded marker is not sent and records
  `egress_excluded_vessel` or `egress_excluded_content`; in a multi-item pass the
  other items are still decided.
- **Egress exclusion covers objectives:** a typed decision whose state concerns
  an objective that lists an excluded vessel in its `VesselIds` sends nothing and
  records `egress_excluded_vessel`, even when the resolved target vessel is
  allowed. The objective-scoped decisions (`preflight`, `prior_art` at
  preflight, `criteria_lint`, `stage_necessity`, `owner_digest`,
  `dispatch_staleness`) pass the objective's and the target's vessels to the
  shared guard.
- **Typed-decision kill switch:** the global `typedDecisions.mode` stops the
  captain tools too. With it `Off`, the general tool, the list tool, the
  pre-shaped helpers and the custom runner make no provider call, record one
  event, and return `unavailable` with reason `typed_decisions_off`
  (`typed_decisions_no_key` when no key resolves). `captainTool.enabled: false`
  still turns the tools off on its own.
- **Documentation:** current contracts replace stale counts, rollout claims, and
  duplicate instructions. Product references are separate from deployment guides;
  the changelog records only the net delta from the upstream merge baseline.
