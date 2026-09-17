# Changelog

All notable changes to Armada are documented in this file.

---

## Unreleased

### Changed

- Model-tier routing now uses the captain capability tier as the only source of
  a captain's tier. `preferredModel` `low`, `mid`, and `high` name a tier floor
  (Economy, Standard, Premium); Legacy Routing admits captains at or above the
  floor, tries the lowest admitted tier first, then a higher captain
  `preferenceRank` (new, -1000 to 1000, default 0), then non-native-first, the
  preferred persona, and a random model among equal captains. A persona with the
  new `specialist` flag always requires a Premium captain, even without a
  preferred model. A concrete model pin that no idle captain runs takes the tier
  of the roster captains on that model, else its built-in family tier, else no
  floor. The legacy `low` selector no longer maps to `mid`.
- Routing selects in three layers: eligibility (persona locks and the tier
  floor), order (Legacy Routing), and choice (Smart Routing, when enabled).
  Smart Routing only reorders the captains the first two layers admit: a
  `lighter` model below the floor is never chosen, and the `capacity_escalation`
  reading never raises or lowers the floor. The usage preview gives every
  captain a verdict with a `layer` (`eligibility`, `routes`, `usage`); an
  eligibility verdict has outcome `excluded` and reason `persona_not_allowed`,
  `below_tier_floor`, or `model_pin_mismatch`.
- `modelTier.midTierModels`, `highTierModels`, `familyClassificationRules`,
  `specialistPersonas`, `withinTierStrategy`, and `withinTierPreferenceOrder`
  are retired. On the first startup that finds them, Armada moves them onto
  records once: listed and rule-matched models become captain tiers (high is
  Premium, mid is Standard, an unclassified model is Economy), the preference
  order becomes captain ranks (first listed ranks highest), and the specialist
  list becomes persona flags. It logs every change, copies the settings file to
  `settings.json.pre-tier-migration-<UTC time>.json`, removes the retired keys,
  and stamps `modelTier.tierRecordsMigratedUtc`. A stamped file is never
  migrated again, and retired keys in it load and are ignored. The settings API
  ignores the retired keys. `reservedHighTierSlots` stays a setting and now
  counts Premium captains.
- Captains persist `preferenceRank` and personas persist `specialist` on every
  provider (SQLite migration 105, PostgreSQL 106, SQL Server 100, MySQL 97).
  REST captain create and update, `armada_create_captain`, and
  `armada_update_captain` accept `preferenceRank`; the MCP captain tools also
  accept `tier`. REST, MCP, and WebSocket persona writes accept `specialist`,
  and an update that omits it keeps the stored flag. Armada reads captain and
  persona routing records on every dispatch pass and for each usage preview.
- Dashboard: the captain modal and captain detail page edit the preference
  rank beside the capability tier, and the persona detail page shows and edits
  the specialist flag. Settings > Routing no longer shows tier lists, family
  rules, the within-tier strategy and order, or specialist personas; it keeps
  reserved Premium slots and non-native-first. Persona model list options come
  from captain models, and the preview table shows each verdict's layer.
- Conflict recovery requests the `high` tier selector instead of a concrete
  model, and its inline playbook no longer names models.
- The server gate script runs builds and tests with MSBuild node reuse off and without the gate lock descriptor, and shuts build servers down afterwards, so a finished gate no longer leaves workers holding the lock that refuses the next gate.
- Dashboard: Settings > Routing has a Legacy Routing / Smart Routing mode
  switch, a persona model lists table (Default, Lighter, Stronger model chips
  with captain counts and an all-accounts-exhausted warning), collapsed
  persona restrictions with a note when one admits no captain, and a preview
  that shows the Legacy Routing order, the usage filter per captain, the
  capacity reading, and the chosen captain. A new Settings > Typed decisions
  tab shows the effective mode (with an "Off — no Jev key" banner), saves or
  removes the provider key without ever displaying it, and edits the global
  mode and each decision's mode and threshold.
- Typed decisions are Off unless a provider key is available. Without a key the
  effective global mode is `Off` with reason `typed_decisions_no_key` (startup
  log, `GET /api/v1/status`, `GET /api/v1/settings`, and the new
  `GET /api/v1/typed-decisions`), whatever the stored mode. With a key, every
  decision runs at its own mode. The key comes from `ARMADA_TYPESAFE_KEY` or,
  when unset, from `<data directory>/secrets/typesafe-api-key`, and adding or
  removing it takes effect without a restart. Every adapter is now wired at
  startup over one switchable client.
- `typedDecisions` hot-reloads in place, so decision points that already hold
  the section see a reloaded mode.

- Smart Routing no longer replaces Legacy Routing. With
  `modelTier.usageRouting.enabled`, Armada keeps the Legacy Routing order (model
  tiers, persona locks, within-tier ranking, non-native-first, capability
  scoring, the persona default captain, and the high-tier slot reserve, which
  now apply with Smart Routing on) and filters it by account usage: captains on
  Exhausted accounts (measured windows, login problems, provider holds, the
  account concurrency limit, or unknown data under `Block`) are removed, and
  captains on Low or Reserve accounts move after the others, in their legacy
  order. `reservedPersonas`, `reservedPriorityAtOrAbove`, and the recovery
  threshold keep their meaning. A mission waits as `WaitingForProviderUsage`
  with reason `usage_exhausted_or_account_capacity` or the first account code.
- `personaRoutes` restrict a persona to the named accounts and models and no
  longer set an order. A persona without routes is unrestricted.
- `POST /api/v1/settings/usage-preview` returns the pipeline: `legacyOrder`,
  per-captain `usageFilter` verdicts, `modelGroups`, the `capacity` reading, and
  `chosen`. The request accepts optional `missionTitle` and `missionText`;
  without them the typed-decision client is not called.
- A shipped typed decision that is missing from a stored
  `typedDecisions.decisions` map now runs at its shipped mode instead of `Off`.

### Added

- `scripts/linux/server-gate.sh <ref> [--ssh-host <alias>] [--scratch-dir <path>]` runs the full test gate for one
  committed ref on a Linux gate host over ssh. It pushes the commit to a scratch bare repository on the host under a
  gate-only ref, checks it out detached in a scratch worktree, builds, runs the sharded combined runner with the logs
  kept on the host, prints the combined summary, and exits non-zero on any failure. The host and scratch directory
  come from the arguments or `ARMADA_GATE_SSH_HOST` / `ARMADA_GATE_SCRATCH_DIR`; it refuses to run while tracked files
  have uncommitted changes. A Linux host is now the recommended gate host (`docs/TESTING.md`, "Gate Host").
  `run-tests.sh` accepts `ARMADA_TEST_LOG_DIR` to write the runner logs to a named directory, which it never deletes.
- Test hosts (unit, automated, runtimes and shared) turn off git auto maintenance, auto gc and `receive.autogc` for
  every git process they start, including the production code under test and the receiving side of a push to a
  file-path remote, through `GIT_CONFIG_COUNT` entries plus a generated `GIT_CONFIG_SYSTEM` file that includes the
  original system configuration. Production git defaults are unchanged. A traced Branch Cleanup Sweep run went from
  259 `git maintenance` processes to none.
- The unit test runner can be split into shards. `test/Armada.Test.Unit` accepts `--shard <index>/<count>` and
  `--list-suites`; suites are assigned deterministically, balanced by the committed
  `test/Armada.Test.Unit/shard-weights.json` (regenerated from unit logs by `scripts/common/generate-shard-weights.py`),
  and every suite named in `test/Armada.Test.Unit/serial-suites.json` runs on shard 1 with its recorded reason.
  `scripts/{macos,linux}/run-tests.sh` now runs the unit shards (min(cores/2, 6) by default, `--shards N` or
  `ARMADA_TEST_UNIT_SHARDS` to override), the automated, runtimes and shared runners at the same time, sums the unit
  totals, and fails when a shard crashes, prints no summary, or the shard suite counts do not add up to the registered
  suites. Extra arguments after a runner name run that one runner unsharded. Tests that waited out real timeouts,
  intervals or retry backoffs now inject the delay: `ArmadaServer.HealthLoopInterval`,
  `AgentLifecycleHandler.ProcessLivenessInterval`, a `SelfDeployNativeCommandRunner` pipe drain timeout, a
  `TimeProvider` for the `OpenCodeServerLauncher` startup deadline, and retry-wait functions for
  `ReleaseWebhookDispatcher`, `DeepSeekInferenceClient` and `VoyageEmbeddingClient`. Production defaults are unchanged.
- Administrator routes for typed decisions: `GET /api/v1/typed-decisions`,
  `PUT /api/v1/typed-decisions` (global and per-decision modes and thresholds,
  validated and saved), `PUT /api/v1/typed-decisions/key` (writes the key file,
  204), and `DELETE /api/v1/typed-decisions/key`. The key is never logged,
  recorded, stored in settings, or returned, and the routes are excluded from
  request history.

- `modelTier.usageRouting.personaModels`: per-persona `default`, `lighter`, and
  `stronger` model lists. Smart Routing groups its filtered order by the lists,
  tries the chosen list first, then the others (default, stronger, lighter),
  then captains whose model is in no list.
- The `capacity_escalation` typed decision (ships `Gate`, threshold 0.90) asks
  whether a mission is lighter, default, or stronger work for a persona with a
  `lighter` or `stronger` list. Every failure or low-confidence answer is
  `default`; the rule and model verdicts are recorded on every call; the reading
  is cached per mission.

### Security

- Every WebSocket command now has one declared authorization rule in
  `WebSocketCommandRegistry`, and the command handler enforces it before the
  command runs, for every caller. Before, the handler checked nothing: persona,
  pipeline and prompt template reads and changes found records by name across
  every tenant and never called the ownership rule, and only the hub's blanket
  global-administrator gate kept narrower sessions out. The handler dispatches
  only declared commands; an undeclared command returns `unknown_command`.
- A command's rule is the stricter of its REST route and its MCP tool.
  `get_persona`, `get_pipeline` and `get_prompt_template` are open to any
  authenticated session and find the record through the shared caller scope, so
  another tenant's record returns `not_found` with no data.
  `list_missions_summary` keeps its caller-scoped query. Every other command
  needs a global administrator and returns `global_administrator_required` to
  anyone else, because the matching MCP tool is reserved for global
  administrators. A command without a caller returns `authentication_required`.
  A refused command writes nothing.
- `update_persona`, `delete_persona`, `update_pipeline` and `delete_pipeline`
  find the record through the shared caller scope and apply the shared edit
  rule, returning `not_found` or `forbidden`.
- WebSocket updates keep the fields REST keeps. `update_fleet` and
  `update_vessel` could move a record to another tenant or owner through the
  body, and `update_mission` replaced the whole mission, so it could set a status
  past the transition gates. The three commands now share one merge rule with
  their REST routes: ownership and creation time stay as stored, and a mission
  update writes metadata only and refuses a vessel or voyage change
  (`mission_binding_immutable`).
- `create_persona` and `create_pipeline` over WebSocket can no longer create a
  built-in record.
- A persona create now applies the default captain rule on REST
  (`POST /api/v1/personas`), MCP (`create_persona` accepts `defaultCaptainId`)
  and WebSocket. Before, a create stored any `DefaultCaptainId`, including a
  captain in another tenant. A refused create returns `default_captain_not_found` or `default_captain_persona_locked`
  and writes nothing.
- The dashboard persona pages show the server's refusal reason when a save
  fails, instead of "Save failed.".

### Removed

- The `routing_hint` typed decision and route `shapes` tags are retired in
  favour of `capacity_escalation`. Settings files that still contain them load
  and the values are ignored.

### Fixed

- A persona update now saves `DefaultCaptainId`, so the dashboard's Default
  Captain edit takes effect. The REST `PUT /api/v1/personas/{name}`, the MCP
  `update_persona` tool and the WebSocket `update_persona` command share one rule:
  `null` or empty clears the default, an omitted field keeps it, an id outside the
  persona's tenant returns `default_captain_not_found`, and a captain whose
  `AllowedPersonas` excludes the persona returns `default_captain_persona_locked`
  (REST 400). A refused update writes nothing.
- The model-endpoint health sweep now runs the same real provider request as a
  manual validation, so a registered embedding or inference endpoint whose base
  URL rejects a bare GET (VoyageAI, OpenAI) is no longer read as Unhealthy while
  its model answers correctly. The recorded health error carries the specific
  probe reason instead of a generic message.

- Deleting a captain no longer reports an error when the captain is actually
  removed. Dependent cleanup (telemetry events, planning sessions) after the row
  delete is now fully best-effort, so a cleanup failure leaves an orphan for a
  later sweep instead of failing the delete the user already saw succeed.
- `apiCaptainCloudProviders` is hot-reloadable, so enabling a cloud provider for
  API-endpoint captains applies without an admiral restart.
- Autonomous recovery compares two failing-test sets without regard to order, so
  the same failures printed in a different order by a parallel test runner are
  recognised as a repeat (the repeated-failure operator note is no longer lost).
- The context index warns when a chunk-metadata sidecar key matches no generated
  chunk, naming any that carry a must_retrieve safety domain, and an independent
  test now asserts every must_retrieve key resolves and every managed vessel keeps
  a safety leaf — so a renamed heading or a file falling under the sub-chunk
  threshold can no longer silently drop a safety rule from a slimmed brief.

### Added

- `review_substance` holds a thin PASS by the probability the model puts on the two lowest substance levels, not by
  the expected score and its own confidence. A headings-only review split between levels scored just above
  partly-evidenced with confidence 0, so the hold could not fire. Without level probabilities the old score rule
  applies.
- `lint_finding` asks only about the findings the Linter listed instead of twenty fixed slots, which cut one live
  single-finding request from about 14,000 to about 1,150 input tokens with the same answers. A typed decision with
  nothing to ask sends nothing.
- Every typed decision ships in `Gate`, and the captain typed-decision tool ships enabled. Each decision still acts
  only at or above its `gateThreshold`, only in its conservative direction (hold, flag, escalate, annotate, order),
  and never approves, lands, dispatches, deletes, or writes memory; the per-decision `mode` and the global cap stop a
  decision without a deploy.
- A synthetic typed-decision evaluation set covers `failure_cause`, `refusal`, `runtime_failure`,
  `review_substance`, and `lint_finding`. Each case builds its requests through the decision's own adapter; reference
  pairs state the answer for each variant, and consistency pairs require an irrelevant change not to move the answer.
  `armada_typed_decision_eval` (operator, global administrator) runs it and returns the report; it also runs in the
  background when the provider reports a model version not yet evaluated (`typedDecisions.evalOnModelChange`, default
  `true`). Each run records a `typed_decision.eval` event.
- Decisions that ask about several independent items (`criteria_lint`, `inbox_triage`, `memory_candidate`,
  `followup_routing`, `memory_review`, `owner_digest`) share provider requests: items are packed into requests of at
  most 100 questions within the per-request state budget, and each item is still gated and recorded on its own event,
  which carries `batch_size` and its share of the request's tokens.
- Typed-decision state is sent as a JSON object instead of an escaped JSON string. Property names and string values
  are redacted in place, and an oversized object has its longest strings shortened until it fits, so the provider
  always receives valid structured JSON.
- A rejected typed-decision request records the provider's redacted explanation (`unavailable_detail`), so a 422 for
  a malformed question names the field instead of reading like an outage.
- Typed decisions never read a noul answer's confidence as its probability. The provider returns a noul with no
  confidence; five adapters fell back to confidence and could merge, nominate, flag, link, or mark a default safe on a
  confident answer that carried no probability.
- The context-index chunk-metadata sidecar can live at a stable path outside the
  git checkout, set by `contextRetrieval.chunkMetadataPath`. When set, the index
  generator loads the must_retrieve safety leaves and read_when triggers from that
  path instead of the default `<docs root>/context-index/chunk-metadata.json`, so
  the operator-local sidecar is not lost to a `git clean` of the checkout. A
  scrubbed `docs/context-index/chunk-metadata.example.json` documents the format.

- A native-runtime captain (Claude Code, Codex, and the other CLI runtimes) can
  reference a registered inference model endpoint. When a captain has a model
  endpoint id, the launch resolves the endpoint’s base URL, key, and model onto
  the run instead of the inline captain credentials, so an external captain’s
  provider and key are managed on the model-endpoints surface. The endpoint must
  be an enabled inference endpoint the captain can see; a captain with no
  endpoint id keeps using its inline credentials unchanged.

- Typed decisions now parse TypeSafe score answers. The provider returns a score `legend` as an index-keyed
  object; the client expected a list, so every decision that asked a score question (`review_substance`,
  `lint_finding`, `flake_score`, `owner_digest`, `inbox_triage`, and the captain tool) failed with
  `unavailable: parse` and its rule always stood. Typed-decision events also record the concrete model version
  the provider reports (`model`, for example `jev-1.13.0`) and each answer's `probabilities`, so a threshold
  review can compare margins and separate a model change from a behaviour change.
- Subscription accounts can be deleted and hard-refreshed from the Dashboard. `DELETE
  /api/v1/usage-accounts/{accountId}` is refused with `account_has_captains` (409) while the account lists
  captains; otherwise it cancels a pending login, removes the account and every persona route that names it
  (dropping a persona left with no routes), saves settings, forgets the account's usage state, and deletes the
  server-derived account folder. A `homeDirectory` that is not that folder is left in place
  (`account_home_not_managed`). It emits `account.deleted`. `POST /api/v1/usage-accounts/{accountId}/refresh`
  reads that one account's usage now, bypassing `refreshIntervalMinutes`, and reruns its login check; an active
  provider retry-after is still honoured (`usage_refresh_rate_limited` with `retryAfterUtc`, no provider call),
  and concurrent refreshes of one account share one read. The account card gains **Refresh usage** with the
  observed time, the Manage view gains **Delete account** with a confirm step, and the section states how often
  usage refreshes and when data counts as Unknown.
- Dashboard Codex device login reads the one-time code the Codex CLI prints on the line after its label, in
  color and with uneven group lengths; it no longer fails with `account_login_prompt_not_found`.
- Memory proposals are stored in the database. A new `memory_proposals` table
  (SQLite 104, PostgreSQL 105, MySQL 96, SQL Server 99) holds durable lessons
  the typed-decision system nominates for the owner's AI-Memory, which is
  read-only to the admiral. The `memory_candidate` decision now writes there
  instead of a proposal file, and a weekly papercut sweep in the health loop
  feeds it. The new `memory_review` decision reviews the memory
  records a finished Recorder stage wrote: it may only lower salience, link a
  duplicate with a `duplicate-of:` tag, and store a proposal for a fleet rule; it
  never deletes or rewrites a record. Both decisions ship Off with threshold
  0.90 and fail closed. The operator tools `armada_list_memory_proposals` and
  `armada_dismiss_memory_proposal` read and close proposals; they are outside
  mission scope and require a global administrator.
- Operators can skip named pipeline stages at dispatch. `armada_dispatch`
  (`skipStages`, `skipStagesReason`), REST `POST /api/v1/voyages`
  (`SkipStages`), WebSocket `create_voyage` and alias dispatch drop the named
  stages when the voyage is materialised, and the remaining stages chain across
  the gap so the Judge depends on the last kept stage. The autonomous scheduler
  applies a skip only from an operator-confirmed `preparation.stageSkip` with a
  `confirmedBy`; an unconfirmed list skips the objective as
  `stage_skip_unconfirmed`. All paths share one rule, which refuses the Judge
  (`stage_skip_judge_refused`), a name outside the effective pipeline
  (`stage_skip_unknown_persona`), and a skip that leaves only the Judge
  (`stage_skip_leaves_no_work`). Each dropped stage records a
  `voyage.stage_skipped` event naming the persona, reason and confirmer.
  Auto-skip stays off: the `stage_optional` preview Warning is advice, and
  a refinement summary never writes a stage skip.
- `armada_check_prior_art` is now in the caller-scoped tool list, so a
  mission-scoped captain can call it; operator-control tools stay out of scope.
  The Worker prompt names the tool, `docs/MCP_API.md` documents it, a read-only
  `PriorArtAnalyst` persona is seeded as a built-in (no pipeline carries it),
  and a candidate on an unlanded branch or ref now carries a bounded excerpt
  read from that ref.

- The `papercut_merge` pair decision now fails closed like every other
  typed-decision adapter. A client that throws records
  `typed_decision.unavailable` and the listing returns the plain grouping, and
  `armada_list_papercuts` passes its own call token (bounded at two minutes) to
  the merge decisions instead of an unbounded one.

- The `refusal` decision now files a `BriefContradiction` papercut when, in
  `Gate`, it reads a run as `blocked_on_premise` at or above threshold. The
  papercut goes through the existing papercut parser path, and its reason is
  the redacted output tail. The refusal verdict does not change.

- The `runtime_failure` decision now reports a suspected provider account
  fault. In `Gate`, a `fleet_wide` reading at or above 0.9 records a
  `provider.account_fault_suspected` event and posts a broadcast
  coordination-board note that name the captain key family, never the key. The
  path never benches a captain.
- Subscription accounts can be added and logged in entirely from the Dashboard,
  with any number of accounts per runtime. The Routing tab's new Subscription
  accounts section creates a server-derived account folder
  (`<data directory>/accounts/<id>`, mode 0700), saves the account with its
  runtime and folder, and runs the runtime's own login: Codex device login and
  Claude Code sign-in with a pasted code show only the provider link and code;
  OpenCode and Cursor take an API key once, written with mode 0600 and never
  returned, logged, or stored in settings. Captains of the same runtime can be
  assigned or cloned onto the account. One login runs per account and a pending
  login stops after 15 minutes. New admin-only routes live under
  `/api/v1/usage-accounts/{accountId}/login/` (`home`, `start`, `code`, `key`,
  `status`, `cancel`) and are never recorded in request history. Cursor accounts
  gain `launchCredentialFile`, a key file in the account folder, as an
  alternative to `launchCredentialEnv`; a missing or empty file reads
  `account_launch_credential_unavailable`. The policy JSON editor moves under an
  Advanced section.
- The code index can take its embedding provider from a registered Embedding
  model endpoint instead of the `codeIndex` settings block. When an enabled
  Embedding endpoint is registered, the code index uses its base URL, model, and
  server-side key; `codeIndex.embeddingEndpointId` pins a specific one when more
  than one is enabled. With no endpoint registered it falls back to the
  `codeIndex` `EmbeddingApiBaseUrl` / `EmbeddingApiKey` / `EmbeddingModel`
  settings, so behaviour is unchanged until an endpoint is added. This lets the
  embedding provider and its key be managed on the model-endpoints surface
  rather than in settings.json or an environment variable. The client resolves
  the endpoint at startup, so add or change it then restart the admiral.

- Context retrieval can now supply a captain brief's Shared Memory section,
  behind the `contextRetrieval.briefSlimmingEnabled` flag (default off). While
  off, brief generation is byte-for-byte unchanged: the section still names the
  memory root and tells the captain to read every file under `shared/`. While
  on, the section carries the always-on core rules inline, the leaves retrieved
  as relevant to the mission's vessel and persona (its must-retrieve safety
  leaves always included) within `contextRetrieval.briefLeafBudgetBytes`, and a
  one-line pointer to the `armada_fetch_context` tool for more by topic, in
  place of the read-every-file instruction. Fail-safe: when the context index is
  unavailable or retrieval degrades, the section falls back to the full memory
  section, so a failure degrades to today's behaviour, never to fewer rules. The
  slimmed section's core, must-retrieve, and leaf byte counts are recorded on
  the `mission.prompt_budget` telemetry so the per-persona before/after cost is
  measurable. Enabling the flag is a separate, deliberate step.

### Changed

- A Judge PASS the `review_substance` decision holds for operator review is
  now a real hold instead of an activity line. The mission records
  `HeldForOperatorReview` and its reason (new mission columns on every provider:
  SQLite migration 103, PostgreSQL 104, MySQL 95, SQL Server 98). While held,
  the Judge completion path neither hands off nor lands the PASS and keeps its
  dock, the landing handler refuses it, and the landing drain does not treat the
  review chain as passed; each logs the hold reason. The inbox lists the mission
  as `judge_pass_held`. The new operator-only MCP tool `armada_review_hold`
  clears the hold (the PASS proceeds through the normal handoff or landing path)
  or fails the mission, writing `mission.hold_cleared` or `mission.hold_failed`
  with the operator and reason. Nothing clears a hold automatically.

- The `objective_preflight_model_flag` finding is now preflight-class for the
  operator dispatch gate: `forcePreflight: true` passes it the same way it
  passes an incomplete preflight, and any other blocking finding still refuses
  the dispatch. The `objective.preflight_overridden` event names the
  model-flagged question numbers, and a refusal lists them as
  `ModelFlaggedQuestions`. The autonomous scheduler still skips a flagged
  objective.

- The dashboard is usable on phone-width screens. Below 640px the sidebar
  becomes an off-canvas drawer opened from a top-bar menu button, the content
  spans the full width, and wide tables scroll within their own container. The
  desktop layout and the icon-rail collapse are unchanged.

- The usage-aware routing capability is named **Smart Routing** and the
  model-tier selector it builds on is named **Legacy Routing**, in the
  Settings > Routing dashboard and the routing docs. Settings keys are
  unchanged.

- The Create/Edit Captain modal no longer shows the inline provider-credential
  fields for an API Endpoint captain. That runtime draws its base URL and key
  from the referenced inference endpoint, and the admiral already rejects inline
  captain credentials for it, so the modal now selects the endpoint as the sole
  credential surface and never submits an inline key or base URL for it. Native
  runtimes keep the inline fields, which remain the way to point a captain at an
  externally served model.

### Security

- Scoped the captain tools preflight to the requesting viewer. The Ask tools
  report (the planned-Ask endpoint preflight) probed Armada MCP with the admiral
  launch credential, which the endpoint maps to a global administrator in the
  default tenant, so it listed the whole operator catalog to any viewer -
  including a tenant admin of another tenant. The preflight now probes with the
  requesting viewer's own scoped session token, the same credential the next Ask
  launch carries, so it lists only the tools that viewer may use and never an
  operator-only tool the viewer's scope refuses. With no issuable viewer scope
  (no authenticated caller, no session-token service, or MCP not served) it
  presents no credential and lists no Armada tool; it never falls back to the
  launch credential. This completes the mission-launch scoping already landed for
  mission and chat captains: a mission captain runs with the dispatching caller's
  own scoped token (interactive dispatch) or the objective and vessel tenant scope
  carried on its voyage (autonomous dispatch, which has no interactive caller),
  never the operator launch credential, never operator-only tools, and never
  another tenant's records.
### Context system

- Added a startup context-index generator. On boot the admiral now reads the
  AI-Memory tree (`shared/`, `repos/`, `machine-notes/`) and the Armada `docs/`
  tree and writes two artifacts under the data directory:
  `context-index/manifest.json` and `context-index/context-core.md`. The
  manifest maps every documentation and memory chunk to its logical path,
  topic, one-line summary, read-when trigger, `applies_to` audience, tier
  (`core` or `leaf`), optional `must_retrieve` domains, and byte size. The core
  bundle concatenates every `tier: core` chunk under a short header. A single
  owner-approved tier configuration decides what is core: three memory files in
  full (repository boundary and leak-prevention, land-then-sync, typed-decision
  non-negotiables) and named sections of three mixed files (the unified-memory
  Boundaries, Proving-a-fix, Domain-scope and Reporting-style sections, the
  sole-memory-source pointer and its four-loaders rule, and the Armada
  direct-edit rule), plus a synthesized index-and-retrieval chunk. The step is
  purely additive: it only reads AI-Memory, changes no loader, and never gates
  how memory currently loads. It is fail-open, so a generation error logs a
  warning and never blocks startup. Output is deterministic: text is normalized
  to LF, chunks are stably ordered, and neither artifact carries a timestamp or
  a host-absolute path, so repeated runs are byte-identical. This is the
  generator and startup wiring only; retrieval, brief slimming, and loader
  changes are later work.

- Added the context retrieval layer over the generated index, and a
  captain-facing fetch tool. `ContextRetrievalService` answers a request
  (query and/or topics, requesting persona, vessel, and a leaf byte budget)
  with an ordered result: every `tier: core` chunk first and never
  budget-limited; every leaf whose `must_retrieve` domain matches the request
  (a vessel's safety leaves), also never budget-limited; then the ranked
  relevant leaves that fit the byte budget, filtered by `applies_to` so a
  persona request excludes other-persona leaves while `all` stays eligible.
  Ranking is deterministic (keyword and topic match over the chunk metadata
  and a light body match, ties broken by topic id) behind an injectable
  `IContextLeafRanker`, so a future typed relevance decision can re-order or
  widen the leaf set without touching the core rule. Any error fails safe:
  every core chunk plus a conservative leaf superset, never zero core, never
  an exception into the caller. The new `armada_fetch_context` MCP tool is
  mission-scoped, read-only, and informative: it runs the service for a
  captain's query, returns the relevant leaf bodies (the core already ships in
  the brief), resolves the caller's vessel and persona from the mission,
  enforces a per-mission call budget from settings, and writes no Armada
  record (it logs only the query length and a short hash, never the raw
  query). The service is built in-process at startup by reusing the index
  generator. Additive: brief generation and the loaders are unchanged.

- Added a chunk-metadata sidecar so the index carries hand-authored retrieval
  metadata without editing AI-Memory. The generator now reads an optional
  repository-versioned file, `docs/context-index/chunk-metadata.json`, that
  maps a chunk id (the manifest `id`, equal to the chunk topic) to a `summary`,
  a concrete `read_when` trigger, an `applies_to` scope, and, for a small set
  of safety-shaped per-vessel leaves, a `must_retrieve` domain. The generator
  merges the sidecar OVER the auto-derived metadata: a sidecar field wins where
  present, and the auto-derived value fills every gap, so a chunk with no
  sidecar entry is unchanged. The sidecar is metadata ABOUT the memory and docs
  chunks, never a copy of their content, and it carries no `tier`: it never
  promotes or demotes a chunk, so the core allowlist and the always-on core
  bundle are byte-identical with or without it. The shipped sidecar enriches
  every AI-Memory leaf and the key operator docs; a vessel's memory leaf can be
  tagged `must_retrieve` with its vessel token so its source-fidelity and
  hang-escalation rules are always retrieved for that vessel's task. Resolution is
  automatic (the default file under the docs root) and fully guarded: a missing
  or malformed sidecar is ignored and the index still generates. Additive: the
  server wiring, brief generation, and the loaders are unchanged.

- Added the context-retrieval coverage census: a harness
  (`Armada.Core/Context/Census/ContextCoverageCensus.cs`) and a committed report
  (`docs/context-index/coverage-census.md`) that measure the built index and the
  retrieval service to gate whether the orchestrator and captain briefs can be
  slimmed. It reports four numbers: safety recall (an invariant that every core
  and every domain-matched must_retrieve chunk is always returned, pinned by a
  registered unit test), read_when leaf recall, the byte-reduction distribution
  against the eager baseline, and a failure replay over recent papercut signals.
  The invariant part is a gate test; the two database-sampling parts run by hand
  and are never flaky in the suite. First run: safety recall 100%, read_when
  recall 93.6%, byte reduction 87.8-95.3% (median 92.2%), and five failure-replay
  regressions traced to oversized whole-file leaves, so brief wiring stays blocked
  until the large sources are sub-chunked.
- Cleared the coverage-census gate. Three changes remove the oversized-leaf root
  cause the census found: the generator now sub-chunks any leaf whose body
  exceeds a named 8 KB threshold at its section headings (recursively at the
  next-deeper heading level for a section still over the threshold), so a single
  load-bearing rule is a small, individually retrievable leaf; the retrieval leaf
  fill now SKIPS a leaf that would exceed the remaining budget and keeps filling,
  so a smaller relevant leaf ranked below a large one is still included, with
  ranked order and the budget cap preserved; and the chunk-metadata sidecar tags
  each per-vessel source-fidelity / safety section `must_retrieve` for its own
  vessel domain, and re-keys the sub-chunked
  session-workflow sections. Core chunks and small files are never sub-chunked, so
  the core allowlist and the core bundle are unchanged. Re-run: safety recall
  100%, read_when leaf recall 100% (62 of 62), byte reduction 78.4-86.2% (median
  86.2%), and zero failure-replay regressions among the mapped cases, so the
  census now PASSES and the brief-wiring step is unblocked.
### Documentation

- The Postman collection carries requests for the subscription account routes (delete, usage refresh, login folder, login start, code, key, status, cancel), the typed-decision routes (read, update, key save and removal) and the in-place server restart, so the route contract covers every served API route.
- Typed-decision hygiene, with no behaviour change. The configuration chapter no
  longer calls the typed-decision system off by default: the global mode ships
  `Gate` and the system is inert until the key is present; the recovery, review,
  preflight and papercut-merge decisions ship in `Gate` and the rest ship `Off`.
  Test fixtures and source comments now use generic
  placeholders (example vessel names, example paths, example source families)
  instead of operator-specific repository, product, and server-path names; the
  context coverage census reads its vessel list from the chunk set instead of a
  committed list.

- Split the operator guide `docs/armada-ops.md` into per-chapter files under
  `docs/ops/` (`NN-slug.md`), each with retrieval front-matter (`topic`,
  `summary`, `read_when`, `applies_to`, `tier`), and replaced the monolithic
  file with a thin index that links the eleven chapters. Orchestrators can now
  load one chapter instead of a ~183 KB wall. Refreshed the moved content to
  the current platform state: the typed-decision catalogue (through D26 prior
  art, the captain tools, and the Gate-from-first-deploy contract), the Linter
  review stage and its pipelines, the in-place `POST /api/v1/server/restart`
  route, the Voyage AI code-index embedding client, and a container image
  deploy section. Repointed the guide-content unit test and the README typed-
  decisions link to the new files. Docs only; no behaviour change.

- Brought the README and the remaining operator-facing docs current with the
  platform state and aligned them to the retrieval front-matter style. The README
  `Upstream vs Fork` section now reads as a divergent superset described by
  capability -- the typed-decision catalogue (D1 through D26 prior art, gated from
  first deploy, plus the captain tools), the Linter and Recorder pipeline stages,
  usage-aware routing, the in-place Restart Server, the Voyage AI code-index
  embeddings, the hardened-but-disabled self-deploy, and the in-progress
  context-index system -- and points to the upstream parity standpoint. Corrected
  the `Tested` and `ProductDevelopment` stage lists to carry the Linter, the
  Recorder placement, and the MCP tool count (now 195). Added
  `topic`/`summary`/`read_when`/`applies_to`/`tier` front-matter to `MCP_API.md`,
  `MERGING.md`, `DELIVERY_OPERATIONS.md`, `OPERATIONAL_ASSETS.md`, `PIPELINES.md`,
  and `PERSONAS.md`. Docs only; no behaviour change.

### Code index

- Replaced the code-index embedding client with a Voyage AI client. The fork
  now uses Voyage AI for code-index embeddings instead of an
  OpenAI-compatible DeepSeek endpoint. The client posts to the Voyage
  embeddings endpoint with an `input_type` of `document`, reads a typed
  response, keeps the input order by index, and fails closed to an empty
  result on any transport, status, or parse error rather than throwing into
  the caller. A fresh configuration now targets Voyage by default: the
  embedding base URL is `https://api.voyageai.com/v1` and the model is
  `voyage-code-3`. The embedding API key stays configuration-driven; supply it
  from the environment to enable live indexing. The former DeepSeek embedding
  client is removed. The separate inference client used for summarization is
  unchanged and still targets its configured endpoint.

### Pipelines

- Added a Linter stage before the Judge in the pipelines that produce vessel
  code: the built-in `Tested` pipeline (Worker, TestEngineer, Linter, Judge)
  and the reference-porting pipeline (Worker, PortingReferenceAnalyst,
  TestEngineer, Linter, Judge). The Linter runs at the mid tier and flags
  overengineering and style before review, so slop is caught across the fleet,
  not only in ProductDevelopment. The Recorder placement is unchanged, and
  `FullPipeline` stays without a Linter as the minimal review shape.

### Server control

- Added an in-place restart action. A new `POST /api/v1/server/restart` route
  performs the same graceful stop as `POST /api/v1/server/stop` and honours the
  same `RequireAuthForShutdown` guard, returning `{ "Status": "restarting" }`.
  In production the Admiral runs under a container restart policy, so the
  graceful stop is the restart: the supervisor relaunches the Admiral once the
  process exits, with a brief period of downtime and no orphaned child process.
  The dashboard Server page gains a Restart Server button beside Stop Server,
  disabled in proxy mode and behind the same confirm dialog.
### Context system (design)

- Added a design for a progressive-disclosure context system: a small always-on
  core (the safety, boundary, proof, and land-then-sync non-negotiables plus the
  index and how to retrieve more) and retrievable leaves fetched by topic on
  demand, for orchestrators and every captain persona. The design defines the
  chunk front-matter (`topic`, `summary`, `read_when`, `applies_to`, `tier`), a
  generated topic-to-path manifest, a docs-and-memory mode for
  `armada_context_pack` that always returns the core and fails safe, a bounded
  typed relevance-router decision (`context_route`, D27) that only widens and
  orders the optional leaf set and never gates a core rule, per-persona brief
  slimming, and the sole-memory-source contract. See
  `docs/design/context-system.md`. A worked example rewrites the largest
  `docs/armada-ops.md` chapter as a chunk under `docs/ops/` with an example
  manifest; the original chapter is unchanged. Design only; no runtime change.

### Build and deployment

- Fixed the server image so it records the commit it was built from. The build
  passes the commit as the `_GitSha` build-arg; the project now uses it as the
  source revision and turns off the SDK source-control query, which in a
  container has no `.git` to read and otherwise leaves the informational
  version with no commit. `BuildInfo.RunningCommit`, and so `armada_status`
  `BuildDrift`, now report the running commit instead of null for a container
  build. A normal build with a working tree is unchanged.


### Captain tool discovery

- Made the busy-captain tool-discovery test deterministic across platforms. The
  `supplied-profile-probe` case now asserts that the configured MCP server's own
  reachability is false (its command is a nonexistent path, so the stdio probe
  cannot start it), instead of pinning the aggregate `ReachableServerCount` to
  zero. The aggregate also counts the runtime built-in tool inventory, which is
  legitimately reachable wherever the runtime CLI schema is installed (the deploy
  host) and absent where it is not, so the old assertion passed only where the CLI
  happened to be missing. Production reachability logic is unchanged and was
  already correct: a missing-command server is counted unreachable on every
  platform.


### Typed decisions: prior art (D26)

- Added the `prior_art` decision — "does this already exist?" asked with
  evidence at three seams and no new persona. A deterministic retriever (the
  "contextual" half) mines identifiers of five characters or more, type and
  method names, and file paths from the objective (or a captain's plan, or a
  diff's added types), searches the landed tip, unlanded mission branches,
  preserved and recovery refs, and open objectives through an injected source,
  and assembles de-duplicated candidates capped at twelve and roughly 24k tokens
  with forty-line excerpts. The model (the "honest" half) answers only over those
  candidates: a per-candidate `delivers` Choice {same_capability, partial_overlap,
  related_only, unrelated} and voyage-level `already_done`,
  `integrate_not_duplicate`, and `reimplements` Nouls, every answer tied to a
  candidate `path:line`, with `unrelated` always available.
- Wired three seams, all additive and informative only:
  - Preflight (extends D5): in Gate, `already_done` adds an
    `objective_prior_art_found` Error issue listing candidates, `integrate_not_duplicate`
    adds an `objective_prior_art_integrate` advisory of landed seams to consume,
    and `already_done` in the uncertain band (0.4–0.7) on a large objective adds a
    `prior_art_analyst_stage_recommended` advisory. The adapter only ADDS issues;
    it never closes or re-scopes a row.
  - Worker premise tool: `armada_check_prior_art`, a mission-scoped captain MCP
    tool that runs the same retrieval for the captain's plan and returns the
    candidates plus typed answers; redacted, per-mission budgeted, one event per
    call, no side effect on any record.
  - Judge (extends D4): on the Worker handoff, a `reimplements` reading prepends a
    review INSTRUCTION to the next brief; it is a review instruction, never a
    verdict.
- The conditional PriorArtAnalyst read-only Research stage is surfaced as an
  operator-confirmed preview recommendation, the same way D19 surfaces a stage
  change; the persona is documented in `docs/PERSONAS.md`.
- Ships `Off` in the decisions map. With the decision Off every seam runs its
  deterministic path unchanged, and a seam with no retrieved candidate never
  calls the model. The whole path fails closed to doing nothing and records one
  event per consulted call.

### Typed decisions: revision kind (D21), test covers (D22), lint finding (D24)

- Added the `revision_kind` gated adapter over the shared typed-decision
  skeleton. It sits after `ParseJudgeVerdict` on a NEEDS_REVISION's revision
  items, before autonomous recovery classifies the failure. The model answers a
  `kind` Choice {behaviour, test, comment_only, doc_only, boundary} per item and
  a voyage-level `all_non_behavioural` Noul. At or above threshold, when no item
  is a behaviour or test change, the seam marks the failure so autonomous
  recovery HOLDS the rescue (reason `revision_comment_only`) and opens an
  incident tagged for operator landing — a comment-only NEEDS_REVISION is an
  operator landing, not a wasted rescue chain (census: 11 rows). A single
  behavioural or test item forces the rule to stand and the rescue proceeds; the
  model never lands. Ships Off (built, dormant); the deterministic rescue path
  stands until a Gate flip.
- Added the `test_covers` gated adapter over the same skeleton. It sits on
  the TestEngineer handoff, over the added test methods and the objective's
  symptom sentence. Per added test the model answers `covers_symptom`,
  `asserts_source_text`, and `would_fail_before_fix` Nouls; a doubted test
  becomes a Judge INSTRUCTION prepended to the next brief ("verify test X fails
  without the change"). It NEVER fails the stage by itself. Ships Off.
- Added the `lint_finding` gated adapter over the same skeleton. It sits on
  the Linter handoff, over each finding the Linter emits. Per finding the model
  answers a `class` Choice {correctness, safety, consistency, style_preference,
  false_positive} and a `severity` Score [cosmetic, should_fix, must_fix,
  blocks_merge]. Only correctness/safety findings at must_fix or above are marked
  BLOCKING for the Judge and style-preference findings become evidence notes; a
  routing note is prepended to the next brief. The Linter's own result is
  unchanged — only the routing is. Ships Off.
- Autonomous recovery's `Classify` now recognises the D21 comment-only marker on
  a failure reason and blocks the rescue with reason `revision_comment_only`,
  reusing the incident the MissionService seam already opened. Every rule
  hard-block is unchanged.

### Typed decisions: pipeline stage necessity (D19) and handoff outcome (D20)

- Added the `stage_necessity` gated adapter over the shared typed-decision
  skeleton. It sits on the dispatch preview's resolved pipeline stages and lets
  the model propose which NON-Judge stages an objective does not need. The Judge
  is never a skip candidate and is never marked. Below the decision threshold a
  stage is retained; at or above it the stage is listed as a `stage_optional`
  Warning the operator confirms through `skipStages` before the voyage is
  materialised; only at or above `0.95` is a stage marked auto-skip. The model
  never removes a stage by itself below `0.95` and never proposes the Judge. Ships
  Off (built, dormant); the preview lists every stage until a Gate flip.
- Added the `handoff_outcome` gated adapter over the same skeleton. It sits
  on the stage handoff in `MissionService`, before the next mission's brief is
  frozen, and turns "failed at the Judge after four stages" into "held after one".
  A `blocked_missing_context`, `blocked_owner_question`, or `off_premise` outcome
  at or above threshold HALTS the voyage before the next stage: the pending
  dependents are cancelled with reason `handoff_blocked:<outcome>`, one incident
  is opened carrying the finished stage's output as the question text, an
  owner-addressed board note is posted for a blocked owner question, and the
  finished stage's branch is preserved. A `partial` outcome does NOT halt; it
  Mails the next stage the unmet acceptance criteria and the voyage continues. The
  decision never approves work, never lands, and never bypasses the Judge — a halt
  opens an incident rather than passing work through, and work that reaches the
  Judge is still judged. Ships Off (built, dormant); the deterministic handoff
  stands until a Gate flip.
- Both follow the shared adapter contract: the deterministic path (the full
  pipeline for D19, the normal handoff for D20) is the fallback for an Off
  decision, an unavailable model, and a below-threshold answer; the call is
  bounded by the settings timeout on the caller's token, fails closed to the rule,
  records a `state_sha256` and byte count but never the state, and never throws
  into its caller. Both are wired only when the live typed-decision client exists.
### Typed decisions: owner-decision digest (D13) and corpus pre-label (D14)

- Added the `owner_digest` scheduled runner (`OwnerDigestRunner`), shaped
  like the health loop and driven once per UTC day by a health-loop maintenance
  step. It collects owner-decision candidates — on this tip, owner-decision
  preparation claims an anchor change re-opened (`NeedsRecheck`), via
  `OwnerDigestHitCollector`; preflight question 13 `needs_owner_ruling`, `owner_ruling`, and
  inbox-triage-classified board-note question sources attach as those signals land —
  ranks each with the `TypedOwnerDigestAdapter` (a `cost_of_waiting` Score over
  `[none, a lane idles today, a captain is guessing now, a landing is held]` and
  a `default_safe` Noul), and posts one owner-addressed board note plus one
  `owner_decisions.digest` event listing the questions by cost with each
  proposed default.
- D13 ships `Off` and is dormant until the decision is enabled, so it is never
  forced on; a day with no candidates is a no-op. It **never answers** a
  question: the deterministic cost from fan-out and age is the fallback, a gated
  model reading may only ESCALATE it (never lower it), every default is a
  suggestion the owner records on the row, and the digest event carries only
  ranking metadata, never the question text. The adapter follows the shared
  skeleton — Off returns the rule with no call; unavailable or below threshold
  returns the rule and records one event; the call is bounded by the settings
  timeout on the caller's token, records a `state_sha256` and byte count but
  never the state, and never throws.
- Added the `corpus_prelabel` operator-side helper
  `scripts/autonomy/draft-corpus-line.mjs` (run outside the admiral). It drafts
  one decision-corpus line (`AI-Memory/corpus/README.md` schema) from an
  incident, a mission failure reason, a Mail signal, or a preflight result. Every
  line it emits carries `"draft": true` and nothing it emits is a confirmed line:
  the operator confirms by removing the flag; the script never removes it and
  pre-fills no decision, only the fields the corpus rule already settles. Its
  self-check is `scripts/autonomy/test-draft-corpus-line.mjs`.
### Typed decisions: flake score, routing hint, change substance (D15, D16, D17)

- **`flake_score`.** The definition-of-done gate (`DefinitionOfDoneGate`)
  now scores a red unit-test result after the deterministic
  `DefinitionOfDoneFailureClassifier` classifies it. The model answers a
  `flake_likelihood` score `[deterministic, likely real, likely load, known
  flaky family]` and an `outside_diff` noul over state carrying the failing test
  names, the assertion lines, the touched files, whether the same tests failed on
  another branch in the last 24 hours, and the classifier's class. In Gate mode a
  `likely load` or `known flaky family` reading at or above threshold triggers an
  isolated, class-filtered re-run of only the failing classes; the re-run's real
  result is the truth (a pass clears the red, a failure leaves it red). The model
  never marks a red check green — only a genuine passing isolated re-run does.
  Ships Off; the re-run runs only for a `dotnet test` command that can be
  isolated, and the red stands unchanged otherwise.
- **`routing_hint`** (retired before release, replaced by `capacity_escalation`):
  a work-shape hint over route tags. Stored settings for it are ignored.
- **`change_substance`.** The extension-based `ChangeSubstanceClassifier`
  stays the rule. When wired, the D17 adapter reads the rescue's added hunks and
  may RAISE a documentation-only (or empty) extension reading to `Substantive`
  for the ineffective-rescue decision (`RescueEffectivenessEvaluator`), so a
  behaviour change is not failed as prose; a `risky` noul at or above threshold
  adds one `CriticalTriggerEvaluator` escalation reason. It NEVER lowers a
  classification. Ships Off.
- All three follow the shared adapter contract over `ITypedDecisionClient` and
  `TypedDecisionAdapterBase`: the deterministic path is the fallback for Off,
  unavailable, and below-threshold; the call is bounded by the settings timeout
  on the caller's token; it records a `state_sha256` and byte count but never the
  state; and it never throws into its caller. The deterministic rule always wins.
### Typed decisions: criteria lint, inbox triage, follow-up routing (D10/D11/D12)

- Added three gated typed-decision adapters over the shared client, all shipping
  `Off`. Each follows the shared contract: the deterministic behaviour is the
  fallback for `Off`, an unavailable model, a Shadow-mode call, and a
  below-threshold answer; the call is bounded by the settings timeout on the
  caller's token, records a `state_sha256` and byte count but never the state,
  and never throws into its caller.
- **`criteria_lint`** (`CriteriaLintAdapter`, wired into
  `ObjectiveRefinementCoordinator.SummarizeAsync`): after a refinement summary is
  finalized, each acceptance criterion is checked against the operator-memory
  defect classes — a presence test over a committed artifact, a pinned pass or
  skip total, something not observable from a dock, a criterion satisfiable by an
  empty diff, and a criterion that mixes two behaviours. Every question is a noul
  phrased as the defect. In Gate mode a criterion whose worst answer is at or
  above the threshold contributes model-flagged `criteria_review` lines appended
  to the refinement summary the operator reads before ReadyForDispatch. The
  adapter NEVER rewrites, reorders, or removes a criterion.
- **`inbox_triage`** (`InboxTriageAdapter`, wired into the `inbox` and
  `armada_coordination_read` MCP tools): inbox items and board notes are scored
  for how urgently a human is needed. In Gate mode each item gains an `attention`
  label (`informational`, `today`, `this_hour`, `blocking_live_voyage`), each
  board note also a `noteKind` (`handoff`, `status`, `question`, `stop_sign`,
  `hold_notice`), and the response is re-ordered by attention. NOTHING is hidden,
  dropped, or dismissed; Off leaves the deterministic severity ordering.
- **`followup_routing`** (`FollowUpRoutingAdapter`, wired into
  `JudgeFollowUpService.CaptureAsync` and the audit-tool backfill path): each item
  in a Judge's Suggested Follow-ups section is given a home. In Gate mode a
  `triaged_objective` home creates a Triaged objective with auto-dispatch OFF, an
  `evidence_note` home appends an evidence note, and a `duplicate_of_existing`
  home LINKS to an existing open objective instead of creating one. A blocking
  item is only flagged for the operator; the model NEVER creates a voyage,
  dispatches, or lands.
- Table-driven unit suites `CriteriaLintAdapterTests`, `InboxTriageAdapterTests`,
  and `FollowUpRoutingAdapterTests` cover Off / unavailable / below / above,
  D10 never-rewrites, D11 nothing-hidden and the attention re-sort, and D12
  never-creates-a-voyage plus duplicate-links, all with the shared
  `FakeTypedDecisionClient`.

### Typed decisions: dispatch preflight text half (D5)

- The objective dispatch preview (`preview_objective_dispatch`, the autonomous
  scheduler, and operator dispatch) now consults the `preflight` typed
  decision AFTER the deterministic preflight block. The deterministic facts
  (Q1/Q2/Q3/Q10/Q11) and the `objective_preflight_incomplete` Error issue are
  computed first and stand regardless of the model; the model only ADDS issues.
- The model answers the text-half battery the code cannot settle: one noul per
  question phrased as the DEFECT for Q1 (premise versus the deterministic facts),
  Q4, Q5, Q6, Q7, Q8, Q9 and Q12, plus a Q13 choice `{none, needs_owner_ruling,
  needs_repo_fact}`. Its state carries the title, description, acceptance
  criteria, non-goals, refinement summary, Kind, vessel name, pipeline stages,
  and the deterministic `facts` object, redacted before egress.
- Ships Gate at threshold `0.80`. In Gate mode a question answered at or above
  the threshold adds an Error issue `objective_preflight_model_flag` to the
  preview — the autonomous scheduler already skips dispatch on any Error issue,
  and its `objective_scheduler.skipped_dispatch_preflight` event lists the code.
  A Q13 `needs_owner_ruling` also posts one owner-addressed board note. The model
  never dispatches, never lands, and never removes a deterministic issue; below
  the threshold, unavailable, or Off leaves the deterministic preview unchanged.
- Follows the shared adapter contract: the deterministic preview is the fallback
  for an Off decision, an unavailable model, and a below-threshold answer; the
  call is bounded by the settings timeout on the caller's token, fails closed to
  the deterministic rule, records a `state_sha256` and byte count but never the
  state, and never throws into the preview.

### Design docs for two Off-by-default typed decisions

- Added `docs/design/typed-decision-leak-hunk.md` (decision `leak_hunk`, D7):
  a design for an advisory per-hunk leak classifier that runs after the
  deterministic `DockBoundaryScanner`. The deterministic scanner and its two
  call sites stay first and unconditional; the model is an additive pass that
  can flag a suspected boundary concern but never changes the scan verdict,
  never blocks a landing, and is off by default.
- Added `docs/design/typed-decision-log-watch.md` (decision `log_watch`, D8):
  a design for a bounded, read-only screen over each `InProgress` mission's
  log that posts a voyage-tagged board note and a `captain.course_flag` event
  when a running captain drifts onto a wrong premise, base, or stage. It never
  stops, steers, or re-dispatches a captain, and is off by default.
- Both are design only: no wired code, tests, or migration. Implementation is
  a later row after the owner reviews the design.
### Captains can consult the typed-decision system directly

- Three mission-scoped MCP tools sit next to the native memory tools:
  `armada_typed_decision` answers typed questions about a piece of state, and the
  pre-shaped helpers `armada_check_premise` (a captain checks its own reading of
  the task before it starts) and `armada_memory_triage` (the Recorder triages a
  memory candidate before writing it).
- Authority does not travel with the tools. Every call redacts its state before
  egress through `DecisionStateRedactor`, is bounded by the per-mission call
  budget in `typedDecisions.captainTool`, writes exactly one
  `typed_decision.captain` event carrying only the state hash and byte count, and
  has no side effect on any Armada record: it dispatches nothing, lands nothing,
  edits no objective, and writes no memory. It never throws into the captain;
  a failure is an `unavailable` result the captain treats as "decide it yourself".
- The tool ships disabled (`typedDecisions.captainTool.enabled` is `false`) and
  returns `unavailable` until an operator enables it. Each helper stays dormant
  until its own decision (`premise_check`, `memory_record`) is enabled.
- The Worker, Judge, TestEngineer and Recorder persona templates each name the
  tool and when to call it.
### Gated typed-decision adapters for recovery, refusal, and runtime failure

- Three deterministic classifiers now consult the typed-decision model (TypeSafe
  Jev) through a shared gated adapter, and each holds an adapter, not the raw
  client. Every adapter follows one skeleton: with the decision Off it returns
  the rule and makes no call; when the model is unavailable it returns the rule
  and records `typed_decision.unavailable`; in Shadow or below the decision
  threshold it returns the rule and records `typed_decision.shadow`; at or above
  the threshold it combines the rule and the model and records
  `typed_decision.gated`. The combine step can only make an outcome more
  conservative — a rule hard-block always wins, and the model never dispatches,
  lands, benches, or converts a block into a rescue.
- `failure_cause` (D1): autonomous recovery consults the model only for a
  failure the rule would rescue. The deterministic repeated-identical-test and
  Infra/Timeout definition-of-done blocks stay hard-blocks that win first. The
  state joins the voyage Checks and the parent's failing test names so the same
  failure text with a different cause is not misjudged. A read of contention,
  provider, environmental, or verdict-form fault at or above threshold, or a
  very likely repeat, holds the rescue for operator review.
- `refusal` (D2): the structured `[ARMADA:RESULT] REFUSED` marker and a provider
  safeguard block stay authoritative. The model may promote a prose refusal the
  phrase rules missed, or demote a quoted phrase only at very high confidence.
  The criteria state the domain: authorized engineering on owned systems, so
  authentication and access-control protocol code is ordinary engineering, never
  a refusal.
- `runtime_failure` (D3): only a bare Crash is offered for change, and only ever
  upgraded to the more conservative UsageLimit or AuthFailure; a recognised
  signature is never downgraded and a crash is never read as clean.
- Each adapter forwards the caller's cancellation token to the client so the
  settings timeout links to it: a slow decision is unavailable, not late. The
  adapters are wired only when the client is live (mode not Off and the key is
  present); otherwise every seam stays on its deterministic rule.
### Gated typed-decision adapter for review substance (D4)

- `review_substance` (D4, ships Gate at threshold 0.85): the Judge-PASS
  structural validator in `MissionService` — the required-heading regex plus the
  narrative-length floor — now consults the typed-decision model through the same
  shared gated adapter. The deterministic regex stays the rule and the fallback.
- The model asks one substance Noul per required review section (is the section
  substantiated, not merely named) and a `substantiated` Score
  (asserted only / partly evidenced / evidenced / evidenced against diff and
  checks). It may only make the outcome more conservative or accept a form-only
  miss: it never fails a PASS the rule accepted, never lands, and never
  dispatches.
- When the rule REJECTED a PASS only because a required heading failed the regex,
  and every section's substance is present at threshold with the review at or
  above `evidenced`, the model accepts it as `heading_form_only` into the SAME
  independent Check gate the rule would have run. A rejection on a real ground —
  empty output or a too-short narrative — is never overturned.
- When the rule VALIDATED a PASS whose substance is thin (`substantiated` at or
  below `partly evidenced`), the model HOLDS it for operator review: the PASS
  stays validated and the Check gate still runs, but a mission-activity line and
  a `typed_decision.gated` event surface it. A held PASS is never auto-failed.
- The state carries the extracted narrative, the required section set, a compact
  diff stat, and a redacted check summary; the adapter is wired only when the
  client is live and otherwise the seam stays on the deterministic regex.
### Typed decisions: papercut merge (D6) and memory candidate (D18)

- The papercut listing (`armada_list_papercuts`, grouped mode) now consults the
  `papercut_merge` typed decision: two groups of the same vessel and category
  that the model judges to be the same underlying issue are folded into one row
  in the listing. The merge is listing-only — no stored papercut event is
  changed and no group is deleted, so turning the decision off restores the plain
  grouping exactly. It considers only the largest groups per vessel and is
  bounded in the number of model calls per listing. A merge at or above the
  threshold records a `papercut.merge_proposed` event (also recorded, but not
  applied, in Shadow mode) alongside the usual `typed_decision.*` event.
- The `memory_candidate` decision (ships Off) asks whether a papercut group
  is a durable cross-session lesson and, in Gate mode above the threshold, writes
  a proposal file under `AI-Memory/corpus/memory-candidates/` for the owner to
  promote or discard. The model never writes memory itself: the proposals folder
  is fixed and never under `shared/` or `repos/`, and a proposal folder that
  would resolve under a loaded memory folder is refused. Proposal text is
  redacted before it reaches the file.
- Both decisions follow the shared adapter contract: the deterministic behaviour
  (no merge, no nomination) is the fallback for an Off decision, an unavailable
  model, and a below-threshold answer; every call is bounded by the settings
  timeout on the caller's token, fails closed to the rule, records a
  `state_sha256` and byte count but never the state, and never throws into the
  caller.

### Dispatch enforces the objective preflight

- An objective now carries a dispatch preflight: an answer for each numbered
  question in the operator dispatch-preflight battery, recorded through
  `update_objective`. Dispatch is refused while any question is unanswered, a
  question that must be yes is answered no, or the open-owner-question question
  is answered yes.
- The objective dispatch preview reports a blocking `objective_preflight_incomplete`
  finding for an incomplete preflight and lists the offending question numbers,
  so the autonomous scheduler skips such an objective and every operator
  dispatch path (MCP, REST, WebSocket) refuses it.
- An operator may set `forcePreflight` on a dispatch to override an incomplete
  preflight. It overrides only the preflight; any other blocking issue still
  refuses the dispatch, and each override is recorded as an
  `objective.preflight_overridden` event naming the operator.
- The preview computes the deterministic preflight questions as facts, shown
  next to the recorded answer so it can be checked against the repository: the
  target vessel count, the deliverable kind against the description, whether the
  named recover refs resolve with a recorded SHA, whether declared sibling tips
  contain the cited commits, and whether the cited paths and identifiers resolve
  at the target tip.
### Mission captains authenticate to MCP with the mission owner's scope, not global admin

- A launched mission captain no longer carries the admiral launch credential
  (`ARMADA_MCP_TOKEN`), which the MCP endpoint maps to global admin in the
  default tenant. It now carries the mission owner's own scoped session token,
  which the endpoint scopes to that owner's tenant and user. A mission -
  including one a tenant admin dispatched, and an autonomous mission - reaches
  only its owner's records and no operator-only tool, closing a cross-tenant
  privilege escalation through a mission brief.
- The owner is the mission's tenant and user; an autonomous mission with no
  owner of its own falls back to the objective owner carried on its voyage.
  When no owner resolves, or no session-token service is available, the launch
  presents no credential and the endpoint refuses it (fail closed), never the
  launch credential.
- The runtime tool-catalog preflight for a running mission captain now probes
  Armada MCP with the same mission-owner scoped token, so the reported tool
  inventory matches what the mission can actually reach, never the launch
  credential.
- The admiral launch credential value never enters a mission captain's
  environment. Chat captains already carry the authenticated caller's own
  scoped session token; both launch paths are now caller-scoped.
### Autonomous recovery detects a repeated identical test failure

- A definition-of-done `TestFail` evaluation now stores the ordered,
  de-duplicated set of failing test identifiers, parsed from the still-whole
  runner output (dotnet and python runners) before it is truncated, with an
  overflow flag when the set could not be kept complete. Autonomous recovery
  reads this set: when a rescue fails on the same complete, non-overflowed,
  non-empty set of tests as its parent, the failure did not change, so no
  further rescue is dispatched, the decision reads
  `repeated_identical_test_failure`, and the incident names the repeated tests.
  An empty, overflowed, unknown or differing set keeps the earlier behaviour,
  and the recovery-budget, policy-refusal and read-only hard blocks still win.
  This is the deterministic fallback the typed foreign-test decision builds on.
### Typed-decision client foundation (TypeSafe Jev), off by default

- A new advisory typed-decision system is wired into the admiral but does
  nothing until the owner enables it and the change deploys. It never approves a
  Judge PASS, never lands, never dispatches, and never silences an alert; a
  deterministic rule stays the tested fallback at every decision point. The
  model can only make recovery MORE conservative (for example hold a rescue),
  never convert a rule hard-block into a rescue, and it gates only at or above
  the per-decision confidence threshold.
- `ITypedDecisionClient` with `TypeSafeDecisionClient` (one POST to
  `{baseUrl}/v1/systemone`, Bearer key read from the environment, timeout from
  settings, no retries) and `NullTypedDecisionClient`. Every timeout, non-2xx,
  or parse error returns an unavailable result with a reason; the client never
  throws into a caller. A decision slower than the timeout is unavailable, not
  late.
- Settings section `typedDecisions` with a global mode (`Off`, `Shadow`, or
  `Gate`; the single kill switch), base URL, model, key env var name, timeout,
  state-character cap, a per-decision map with per-decision mode and gate
  threshold, and a captain-tool sub-block. The effective mode of a decision is
  the minimum of the global mode and the decision's own mode. The six Phase-1
  decisions ship in `Gate`; every other decision is `Off` until its adapter lane
  lands. The system is operationally off until the key is confirmed in the
  container (no key means the null client, whatever the mode) and no consumer
  calls the client in this row. The section is in the hot-reload swap list, so
  the mode flips live and an MCP settings write cannot clobber it.
- `DecisionStateRedactor` removes Armada ids, absolute paths, hosts, URLs,
  commit hashes, and key-shaped tokens before any state egresses, then truncates
  to the character cap while preserving `[ARMADA:` marker lines. A guard test
  proves no protected class survives and that a product identifier does.
- `TypedDecisionRecorder` writes one event per call
  (`typed_decision.gated` when a gate at or above threshold changed the outcome,
  `typed_decision.shadow` when the rule stood — a Shadow-mode call or a Gate-mode
  call below threshold, `typed_decision.unavailable` otherwise) carrying the
  decision, rule verdict, answers, confidences, tokens, latency, gate outcome,
  and the state's SHA-256 and byte count — never the state itself.
- `ArmadaServer` constructs the live client only when the mode is not `Off` and
  the key env var is present, else the null client, and logs which and why at
  startup. No consumer calls the client in this row.

### The admiral image embeds its build commit

- The server image now embeds the commit it was built from, so a running
  admiral reports its build commit and drift detection can tell whether the
  running image is behind the landed code. The image build cannot run git, so
  the rebuild helper passes the build context's commit to the Dockerfile,
  which embeds it as the assembly source revision. A build with no commit
  available keeps the earlier unknown-commit behaviour.

This section is the fork delta on top of v0.9.0. It includes fork-only
capabilities and isolated upstream hunks that were still missing after the
first absorb. Fork-owned routing, the coordination board, autonomy, recovery,
and the code index stay richer than the upstream copies; those were not
replaced.

Focus: operator signal fidelity - make a failure say what actually failed.

### WebSocket create commands record the caller as owner

- `create_fleet`, `create_vessel`, `create_voyage` without missions,
  `create_mission`, `create_captain`, `send_signal`, `enqueue_merge`,
  `create_persona` and `create_pipeline` now record the authenticated session
  caller's tenant and user as the owner, as the matching REST create does. A
  tenant or user named in the command body is replaced. Before, these commands
  stored the body as sent, so a record usually had no owner.
- A create command with no authenticated caller returns `command.error` and
  writes nothing.
- The progress signal from `restart_mission` belongs to the restarted
  mission's tenant and user.
- `create_voyage` with missions still dispatches through the admiral, so its
  voyage and missions take the vessel's owner, as REST and MCP dispatch do.
### A CLI chat captain runs with the caller's scope, not operator access

- A captain chat turn on a CLI runtime (Claude Code, Codex, Cursor, Gemini,
  OpenCode, Mux) now reaches Armada MCP with the authenticated caller's own
  session token, carried by the scoped launch configuration and referenced by
  variable name, never the admiral launch credential. Before, every chat launch
  carried the launch credential, which the MCP endpoint maps to operator access,
  so a non-admin dashboard user who could start a captain chat could reach
  operator-only MCP tools (dispatch, purge, `armada_stop_server`) through the
  captain. The API-endpoint chat path already used the caller token; CLI chat
  now matches it, so a chat turn authenticates one way.
- A chat turn with no authenticated caller leaves the token variable unset and
  reaches no MCP tool. Mission launches are unchanged and still carry the launch
  credential.
- The MCP endpoint accepts a session token presented in the Authorization bearer
  header, after a persistent bearer credential lookup, so a CLI runtime's native
  bearer configuration authenticates with the caller's own scope.

### An interrupted captain run is re-dispatched, not failed

- Exit code -1 that a runtime reports means a stop, shutdown or Admiral
  restart cancelled the run. The process-exit handler now returns
  that mission to Pending, releases the captain to Idle, keeps the voyage
  running and emits `mission.interrupted_redispatched`. Before, every
  non-zero exit failed the mission and halted the voyage.
- `maxInterruptedExitRedispatchAttempts` (default 2, 0-10) bounds the
  re-dispatches per mission. The count comes from the mission's
  `mission.interrupted_redispatched` events, so it survives a restart and
  does not spend the autonomous-rescue budget. The next interruption after
  the budget fails the mission through the normal terminal path.
- An interrupted run is never marked Failed, so autonomous recovery opens no
  incident or rescue for the same exit. Every other exit code still fails,
  including a native crash status that reads as a large negative number on
  Windows and on Harbor runners.
- The health check's `-1` for a process it cannot find is not treated as an
  interruption. That process may have completed after its exit record was
  pruned, so re-running it could repeat finished work.

### MCP create tools record the caller as owner

- `armada_create_fleet`, `armada_create_captain`, `armada_create_mission`,
  `armada_send_signal`, `armada_nudge_voyage`, `create_playbook`,
  `create_workflow_profile`, `armada_enqueue_merge` and the create branch of
  `update_prompt_template` now record the authenticated caller's tenant and
  user as the owner, as the matching REST create does. Before, most wrote the
  default tenant with no user, so a scoped caller could not see its own record.
- `create_playbook` checks file-name uniqueness inside the caller's tenant.
- `create_workflow_profile` keeps a tenant named in the record only for a
  global administrator, as the REST create does.
- The progress signal from `armada_restart_mission` belongs to the restarted
  mission's tenant and user.

### Ask chat is told to use only the tools it has

- A built-in `ask.system` prompt template is now seeded. Every Ask Armada chat
  turn starts with it. It tells the assistant to use only tools provided in
  that session, never to claim tool or MCP access it cannot use, and to say in
  one sentence when it has no tool for a request and name the MCP captain,
  dashboard or CLI instead. Before, no `ask.system` template was seeded, so an
  Ask turn carried no system prompt unless an operator wrote one.
- An operator who already created an `ask.system` template keeps its content;
  seeding only marks it built-in, so **Reset** restores the new default.

### Data directory alias and local-clone vessels

- `ARMADA_DATA_DIR` is accepted as an alias of `ARMADA_DATA_DIRECTORY`. When
  both are set to non-empty values, `ARMADA_DATA_DIRECTORY` wins, so a test or
  rehearsal redirect still isolates a process that also carries the alias. The
  self-deploy rehearsal gate accepts either variable.
- `armada_add_vessel` sets `WorkingDirectory` to the repository when `repoUrl`
  is a local clone (a `file://` URL or a rooted path to a directory with a
  `.git` entry, or a bare repository) and no `workingDirectory` is given.
  `LocalPath` stays unset, because vessel removal deletes that directory.
### API-endpoint chat captains call Armada MCP tools as the caller

- An Ask chat backed by an API-endpoint captain now lists and calls Armada MCP
  tools. The chat issues the caller's own session token, and the runtime sends
  it as its only MCP credential. The MCP endpoint re-reads the user and tenant
  on every request and applies the shared tool access policy, so the captain
  reaches only the tools and records the caller may already reach.
- A chat with no authenticated caller gets no MCP tools. The runtime never uses
  the admiral launch credential for chat tools, and a refused or unreachable
  endpoint leaves the turn on its workspace tools with the reason in the log.
- MCP tool calls appear as tool activity cards like the workspace tools.
- The system prompt of an API-endpoint run now allows every tool the run is
  given, workspace and Armada MCP alike, instead of naming the workspace tools
  as the only usable ones. It still forbids claiming a tool that is absent.
- `GET /api/v1/captains/{id}/tools` now reports, for an API-endpoint captain,
  the Armada MCP tools the requesting caller would be offered in chat, beside
  the workspace tools (`availabilitySource=api-endpoint-caller-mcp`,
  `mcpConnectionPlanned=true`, `armadaToolCount` set). The report issues the
  caller's access the same way chat does and reads the endpoint, so the
  preflight and a chat turn resolve one rule. A request without an
  authenticated caller, or an admiral with no session token service or MCP
  port, reports the workspace registry alone
  (`availabilitySource=api-endpoint-workspace-tools`,
  `mcpConnectionPlanned=false`, zero Armada tools); the report for a CLI
  runtime is unchanged.

### Captain commit messages carry a change manifest

- The commit instructions a captain receives now require a summary line and a
  full manifest: every file added, modified or deleted, with what changed and
  why, before the Armada trailers. The embedded default and the fallback use
  the same text. The rendered block grows from 319 to 589 bytes, and a
  worst-case worker launch prompt with it grows from 985 to 1255 bytes, inside
  the 32768-byte instruction budget.

### Linter persona in the ProductDevelopment pipeline

- A built-in Linter persona now checks the code and documentation a mission
  changed for style and correctness. It fixes clear in-scope violations, flags
  judgment calls, and reports `## Code Style`, `## Code Correctness`,
  `## Documentation`, `## Fixes Applied` and `## Residual Issues`.
- ProductDevelopment runs the Linter at the mid tier after the TestEngineer and
  before the Judge. An upgraded database gains the stage and one Linter persona
  on its next start. FullPipeline is unchanged, so existing FullPipeline users
  see no new stage.
- In an Audit or Research mission the Linter reports findings and does not edit.
### Mission statuses, escalation triggers and settings hold only live values

- The mission status set is `Pending`, `Assigned`, `InProgress`,
  `WorkProduced`, `PullRequestOpen`, `Testing`, `Review`, `Complete`,
  `Failed`, `LandingFailed` and `Cancelled`. REST, WebSocket and MCP status
  transitions accept only these names. The capacity, recovery,
  voyage-completion and dependency-cancellation checks, the Helm table
  renderer and the watch script read only these statuses.
- The escalation triggers are `CaptainStalled`, `MissionOverdue`,
  `MissionFailed`, `RecoveryExhausted` and `PoolExhausted`. A settings file
  whose escalation rule names another trigger does not load; remove that rule.
- The settings model has no mission input-block cap. A settings file that
  carries a key the model does not define still loads, and a save does not
  write the key back.
- A schema migration (SQLite 102, PostgreSQL 103, MySQL 94, SQL Server 97)
  sets each mission stored with the `WaitingForInput` status to `Cancelled`.
  The mission keeps its last update time, takes that time as its completion
  time when it has none, and records the reason `Cancelled while waiting for
  operator input` ahead of any earlier failure reason. Other missions do not
  change.
- The voyage nudge tool and the orchestrator-notes handoff drain do not change.
### Autonomous recovery reads the gate's failure class

- The rescue decision now reads the failure class that the definition-of-done
  gate records in the failure reason (`classification=`). An `Infra` or
  `Timeout` gate failure does not dispatch a rescue. Recovery opens the
  incident with a blocked policy that names the class, because a rescue re-runs
  the same commands on the same host. `Compile` and `TestFail` keep the rescue.
  A reason with no recorded class keeps the earlier rules.
- Before, a gate failure with no configured workflow commands, or a timed-out
  suite, still bought a rescue that failed the same way.
- The gate classifier reads a missing .NET runtime ("You must install or update
  .NET to run this application", a framework that "was not found") and a
  testhost process that exited with an error as `Infra`. Before, a test command
  with that output read as `TestFail`.
### A diverged working checkout is preserved and raised after landing

- When a `LocalMerge` landing finds that the vessel working checkout holds
  commits the landing repository lacks, Armada now pushes the checkout `HEAD`
  to a `recover/working-checkout-<sha>` branch in the landing repository, emits
  one `landing.working_checkout_diverged` event, and opens one incident that
  names the recover branch, the full SHA, the commit count and the operator
  steps. Before, the landing only wrote a failure reason on the mission, and
  the divergence stayed invisible until someone read it.
- The checkout is never reset and nothing is pushed to a remote. A later
  landing that finds the same divergence with the incident still open adds no
  second event or incident. When the checkout cannot be read, counted or
  pushed, the incident says which step failed.
### Checks left Running by a stopped admiral are cancelled at startup

- Startup now cancels every Check whose status is Running and whose start time
  predates the running process, with a summary naming the restart. A check
  executes inside the Admiral process, so such a record could never reach a
  verdict: it counted as unresolved forever, held a Judge PASS and then rejected
  it. A check started by the running process is untouched.

### A running check's checkout survives a prune and a reclaim sweep

- The private checkout a Build or UnitTest check executes in is now locked as a
  git worktree for the length of the run and unlocked before removal. A
  `git worktree prune` run from a process that cannot see that directory no
  longer removes the live worktree's admin entry mid-run.
- The checkout directory carries its check run id, and the storage reclaim sweep
  keeps the checkout of any check that has not reached a verdict instead of
  reclaiming it on modification time alone. An unreadable check list protects
  every checkout for that pass.

### An unstamped voyage-armed Check never measures the default branch

- A voyage that ends between the eligibility read and the stamping read leaves
  its armed Check with no branch and no commit. The executor no longer runs such
  a record: it is set `Canceled` with a summary naming the missing stamp. Before,
  it ran in a checkout of the vessel's default branch and reported base-branch
  failures as failures of the work under review.
- An unstamped record of a live voyage keeps waiting for a stage to commit, and
  one whose voyage is `Complete` still runs, because that work is on the default
  branch.
- A check that did not execute is recorded as `check.auto_not_run` instead of
  `check.auto_failed`.

### A voyage mixing Audit and Research missions is report-only

- A voyage counts as fully report-only when every mission is Audit or Research,
  in any combination. Before, it had to be all Audit or all Research, so a
  voyage that mixed the two took the code Check gates and its Judge PASS was
  rejected for having no green Build and UnitTest, which such a voyage never
  produces.
- A voyage carrying any Implementation mission keeps the code Check gates.

### Check readiness reads shell syntax as syntax, not as programs

- The command-dependency probe no longer treats a loop variable, a shell
  keyword, or the header of a `for`, `select` or `case` construct as a program
  to find on PATH. A check command that uses a loop is no longer blocked with a
  missing-dependency error for a name that is not a program.
- A command named by a shell variable is resolved at run time, so it is not
  probed. A genuinely missing binary, inside a loop body or outside one, is
  still reported as a blocking readiness error.
### A rejected Judge PASS carries the rejecting check's evidence

- A Judge PASS rejected by a failed independent Check now records, for each
  rejecting Check, the commit that Check measured and a bounded tail of its
  output. The incident opened for the rejection carries that reason, so it says
  what failed and against which tip. Before, it named only the failed check
  ids, and the diagnosis stayed in the check record.
- The tail is redacted with the same secret redaction applied to stored command
  output, is capped per Check, and is carried for the first few rejecting
  Checks only, so a large log cannot inflate the incident.

### A Judge is told to write the sections its verdict is judged against

- The review sections a Judge must emit now come from one mode-aware source
  that the verdict validator reads as well, so a brief, a launch prompt and the
  validator can no longer name different sets.
- A read-only (Audit or Research) Judge launch prompt now names the report
  sections, Evidence and Residual Risks, instead of the implementation ones.
  Before, that prompt asked for Tests and Failure Modes while the validator
  required the report set, so a Judge that followed its prompt had its PASS
  rejected for missing sections.
- Implementation briefs, prompts and contracts are unchanged.

### A claimed completion with no change is a no-op, however long it ran

- The no-op completion gate now fails an Implementation mission whose persona
  must produce a commit when nothing changed since its dock was provisioned,
  whatever its runtime and however long its output. Before, a captain that ran
  for minutes, narrated at length and printed the completion marker passed the
  gate with an empty diff, and the empty result reached the pipeline as
  progress.
- The rule reads the same persona set as the landing gate, so reviewer stages
  that approve without committing are untouched, and Audit and Research
  missions stay exempt because their deliverable is a report.
- A dock whose start commit cannot be read gives an unknown, not a proven empty
  result. An unknown never fails a mission; the existing short-run rule still
  decides there.

### Incident lifecycle sweep reaches every open incident

- The incident lifecycle sweep now reads only non-terminal incidents. It no
  longer reads Closed and RolledBack ones. Before, it read the newest page of
  all incidents and then dropped the terminal ones. Once that page held only
  closed incidents, every later sweep evaluated nothing while older open
  incidents stayed open.
- The sweep orders open incidents oldest update first and resumes after the
  last incident the previous sweep evaluated. When it reaches the end, it
  starts again from the oldest. Incidents that a sweep leaves unchanged can no
  longer hold the page, so every open incident is evaluated within one full
  pass of `incidentLifecycle.maxIncidentsPerSweep`-sized sweeps.
- Incident enumeration accepts `ExcludeTerminal`, `OldestFirst`, and an
  oldest-first keyset cursor (`AfterLastUpdateUtc`, `AfterId`).
### Shared-runner fork differences are recorded as decided

- Shared cases that assert behaviour the fork has decided not to adopt now
  carry their own disposition kind, "Intentional fork difference". The shared
  runner prints that prefix on each skip and counts the kind separately, so a
  decided difference no longer reads as pending owner work. A stale record of
  this kind still fails discovery.
- Six cases move to that kind: three expect a seeded "Test Engineer" persona
  and an expanded FullPipeline, and three expect a "Model Context Updates"
  section in generated instructions. The fork keeps its TestEngineer persona
  and four-stage FullPipeline, because renaming or re-ordering seeded personas
  on upgraded databases would break operator pipelines and prompt templates.
  Its generated instruction sections are deliberate.
- The unused planning-session inactivity default constant (60 minutes) is
  removed. The effective default stays 0, which disables the timeout.
- The shared case record lists 40 named skips, 34 duplicate records and 6
  intentional fork differences, and no case awaits an owner decision. The
  MCP vessel token override, WebSocket mission summary and requested-captain
  assignment cases now run and pass.
### Assignment honours the requested captain and its fallback tier

- A mission's requested captain now decides assignment. The requested captain
  can come from the mission itself, a voyage captain override, or a persona
  default. When it is idle, it is assigned ahead of the other idle captains.
  Before, assignment stored the choice and then ignored it.
- A requested captain that is busy, quarantined, excluded, reserved, in
  another tenant, or not approved by usage routing is never used. Normal
  routing then runs over idle captains at or above the fallback tier, and the
  lowest tier at or above that floor is preferred. The floor is the mission's
  stored tier, or the requested captain's own tier when none is stored. When
  no captain meets the floor, the mission waits. It is not given to a
  lower-tier substitute.
- Every substitution and every wait records a `mission.requested_captain`
  event. The event names the requested captain, why it was not used, and the
  tier. An unchanged wait is recorded once, not on every tick. A mission with
  neither field set is assigned exactly as before.
- Operator dispatch, the scheduler, restarts, rescues, review re-queues and
  the health-check dispatch of Pending work all apply this one rule through
  captain selection.
### Read-only missions without a commit skip the definition-of-done gate

- A Research or Audit mission whose dock head still equals its dock start
  commit now skips the build and unit-test definition-of-done gate. It
  completes its stage and hands off to the next stage. Before, the gate ran
  the vessel's commands against the unchanged base branch, so a suite that was
  already red there failed a mission that changed nothing, and its Judge stage
  was cancelled.
- The skip is never silent. The mission activity log, the recorded
  definition-of-done evaluation event (outcome `Skipped`) and the mission
  report all name the reason `read_only_no_commit`.
- A read-only mission that did commit, and every Implementation mission, keep
  the gate unchanged. When the start or head commit cannot be read, the gate
  runs.
### MCP and WebSocket gain the two operations only REST had

- `armada_add_vessel` and `armada_update_vessel` accept `gitHubTokenOverride`.
  Every surface that writes a vessel applies it through one rule: omitted keeps
  the stored value, an explicit empty string clears it, any other value
  replaces it, trimmed. The value is write-only. No REST, MCP or WebSocket
  result returns it; results carry `HasGitHubTokenOverride` instead. A
  WebSocket `update_vessel` that omits the override now keeps the stored
  token instead of wiping it.
- The MCP argument normalizer keeps an empty string for a string property whose
  schema declares `emptyStringClears: true`, instead of treating it as omitted.
  `gitHubTokenOverride` declares it, so `""` clears the override over MCP.
- The MCP vessel tools now run as the authenticated caller. `armada_add_vessel`
  records the caller's tenant and user as the owner, as a REST create does.
  `armada_update_vessel` reports a vessel the caller may not change as not
  found and writes nothing to it, the token override included.
- The WebSocket command hub serves `list_missions_summary`. It reads through
  the same caller-scoped query as `GET /api/v1/missions/summaries`, so it
  returns the same `EnumerationResult<MissionSummary>` shape and the same rows
  to the same caller. A command without a session caller is refused.
### REST queries, fleet updates and server data integrity

- Every REST route percent-decodes query-string values once, through one
  shared reader, before it parses them. An encoded timestamp such as
  `fromUtc=2026-09-14T00%3A00%3A00.000Z` now selects the requested window on
  mission history, token-usage summaries, request-history summaries and every
  other route that reads a query value. Before, the web server passed values
  through undecoded, the timestamp failed to parse, and the route silently
  used its default window.
- Mission history counts a mission as complete once it has produced work:
  WorkProduced, PullRequestOpen, Testing, Review and Complete. Failed and
  LandingFailed count as failed; only in-flight and cancelled missions count
  as other. This matches upstream.
- A fleet update keeps the stored tenant, owner and creation time, and keeps
  `Active` and `DefaultPlaybooks` unless the body names them. A tenant user's
  update with only a name and description no longer removes the fleet from the
  tenant's scope or reactivates it.
- The per-vessel GitHub token override is persisted by the PostgreSQL, MySQL
  and SQL Server providers, with the same write-only rule SQLite already
  applied: an update that omits it keeps it, an empty value clears it, and a
  value replaces it.
- PostgreSQL timestamp reads return the stored UTC instant on a host in any
  time zone, for TEXT values in ISO 8601 form or PostgreSQL's own
  `yyyy-MM-dd HH:mm:ss+00` form.
- The token-usage summary matches a legacy `mission.token_usage` event to its
  table record by mission identity, in any window. A usage whose record and
  event fall on opposite sides of a window edge is counted once, in the window
  of its record.
- Deleting an objective broadcasts an `objective.deleted` WebSocket event to
  the objective's owner scope, carrying its id, tenant and user.
- The WebSocket API reference documents the exact data of
  `objective-refinement-session.summary.created` (`sessionId`, `messageId`,
  `summary`) and `objective-refinement-session.applied` (`sessionId`,
  `objectiveId`, `summary`), including the fields of the summary object.
- A tool schema property can declare `emptyStringClears`; the MCP argument
  normaliser then passes an empty string through instead of treating it as
  omitted. `armada_update_captain` declares it on every field whose
  description says an empty string clears it, so those clears reach the
  handler. An empty Mux option string is stored as cleared.
- `armada_get_vessel` and `armada_update_vessel_context` read the vessel within
  the caller's scope, so a caller outside the owning tenant or user gets
  "Vessel not found" and changes nothing.
### Dashboard pages show complete and current data

- The sidebar "Needs You" count requests the administrator-only inbox only
  for administrators. It runs at most one request at a time, and a refused or
  slow request no longer causes another request on every live event.
- The top-bar health indicator shows a warning, with its reason, when the
  running build is behind the landed commit. Before, it always read healthy.
- Notifications carry the time the server recorded the event. Deployment,
  objective and incident notifications open their record, like mission,
  voyage and captain notifications.
- After the live-update connection reconnects, or the server reports missed
  events, open pages receive a resync message so they can reload.
- The chatroom ignores a slow read for a room the operator has left, shows the
  recipient of a directed note, and says when it shows only the newest 200
  notes.
- The Docks list loads every dock, so its sorting, filters, pages and record
  count cover all docks. Captain and vessel names refresh with the list. Dock
  detail refreshes and shows "not found" for a dock that does not exist.
- Fleet detail reads the fleet by id, refreshes, and shows "not found" for a
  fleet that does not exist. A fleet edit sends the default playbooks back, so
  the update keeps them.
- The Voyages list filters by status on the server and keeps the server's
  order and record count, so its filter and pages cover every voyage.
- Voyage detail refreshes on a timer or on request, and a refresh keeps the
  page on screen. Its progress line counts landing failures as failed and
  cancelled missions as finished.
- Create Voyage no longer shows Auto-Push, Auto-Create PRs and Auto-Merge PRs.
  The voyage request never carried them; landing follows the vessel settings.
- The Merge Queue filters by status on the server. Delete on an active entry
  says the entry is cancelled, because the server cancels an active entry and
  deletes only a finished one.
- The Incidents list filters by status, severity and search on the server
  and pages through every incident. Its total and status cards come from
  server totals, not from the first 500 incidents.
- Incident detail applies a live change to the open incident. When the form
  holds unsaved edits, it keeps them and says the incident changed.
- The Signals type filter uses the parameter the server reads for signals and
  lists Heartbeat and Wake. The send form still offers only the types an
  operator sends by hand.
- Token Usage refreshes on the chosen interval and shows one model under two
  runtimes as two separate series.
- The Backlog list reads every page of backlog items, so its counts, filters
  and rank moves cover the whole backlog, not the first 500 items.
- Backlog item detail keeps unsaved edits when the item changes elsewhere and
  offers the newer copy. It shows a refinement summary the server announces,
  reloads the item when a summary is applied, and says when the item was
  deleted. Its parent and blocked-by pickers list every backlog item.
- The Runbooks list takes execution totals and per-runbook counts from the
  server, not from the first 500 executions. Runbook detail shows its
  execution total and says when it lists only the newest executions.
- The Users, Tenants and Credentials pages list every record, not only the
  first 10, and credentials show owners and tenants past the first 10 by name.
- The Checks list sends its status, type, source and vessel filters to the
  server and pages through every run. Its summary cards come from server
  totals, not from the newest 500 runs.
- Check detail follows live changes to a pending or running check, and says
  when its previous-run comparison searched only the newest runs.
- History loads the filters that were applied. A saved view or a changed URL
  loads its filters, and a refresh no longer sends filter text still being
  typed. The source-type filter lists every source type, the page shows the
  server total, and an export reads every page instead of the first 500
  entries.
### Dashboard pages follow upstream

- Vessel, mission and merge queue detail show the upstream inline landing
  preview with its "Ready To Land" or "Needs Review" pill. The vessel setting
  reads "Require Passing Checks To Land".
- Captain chat waits 330 seconds and a vessel context build waits 900 seconds
  for the server. A Mux captain requires a named Mux endpoint.
- The captain forms set the capability tier, and an API Endpoint captain
  selects the inference endpoint it drives. The captain list and detail show
  the tier badge. The list uses the upstream two-column form, runtime order and
  header order, and shows a quarantined captain as a stalled tag with its
  release time; the reason is the tag's tooltip.
- Mission detail offers Land for a WorkProduced mission and Retry Landing for a
  LandingFailed mission. A Review mission offers neither Land nor Mark
  Complete, because the server refuses a landing retry and a manual Complete
  while the mission is in Review. The mission `DELETE` route cancels, so the
  action is Cancel and the mission stays on screen; Purge returns to the
  mission list.
- The mission list keeps server paging for the creation-time order. A title,
  status or branch filter, or a title, status or priority sort, reads every
  page at the server page cap and filters, sorts and pages in the browser. The
  status filter lists PullRequestOpen and LandingFailed.
- Captains, captain detail, mission detail and vessel detail refresh on the
  auto-refresh interval and show the loading spinner only on the first load.
  Captain and vessel detail report a missing record as not found. Vessel detail
  reads the vessel by id.
- Merge entry detail polls while the queue is working the entry and shows the
  pull request link, merge failure class, failure summary and conflicted files.
- The home Active Voyages card opens the voyages tab of the missions page, a
  home mission row's View JSON shows its summary row, and the mission history
  chart reloads on each home refresh.
- The Vessels page has no Workspace header button, and API Explorer and
  Requests do not link to each other from their headers. Page header buttons
  use the upstream order.
### Dashboard follows the upstream shell

- `/notifications` opens Needs You, and the notification bell stays in the top
  bar. The dashboard has no Code Index page and no `/token-usage` path.
- The sidebar grid, setup wizard highlights, required Mux endpoint, and login
  fields follow upstream.
- Home leads with an Ask Armada band that opens Ask, Needs You, Dispatch, and
  Diagnostics. The blank Ask Armada chat shows a greeting.
- The stylesheet is the upstream stylesheet plus one block for fork features:
  the coordination board, workspace code view, and status badges.

### Record ownership and configuration tabs

- Personas, pipelines, and prompt templates show a Visibility column and detail
  field. An administrator chooses the visibility of a new record. The dashboard
  hides the create, edit, duplicate, and delete actions the server refuses.
  Visibility is fixed when a record is created.
- Configuration has an Endpoints tab: model endpoints with health history,
  validation, and a health sweep for global administrators. Any user can create
  a personal endpoint; an administrator can create a tenant-wide one.
- Configuration has a Memory tab: durable memories with type and text filters.
  Delete shows only on memories the viewer may change.

### Routing settings keep unsaved edits

- Settings > Routing holds the model routing policy (tier lists, specialist
  personas, reserved slots, strategy, preference order, family rules, dispatch
  guard, model providers, additional assets) and Smart Routing. Each part saves
  only its own changed fields, so a save in one part never replaces the other.
- A refresh, or a save in another section, keeps unsaved edits on the Routing
  and Server tabs. Fields the operator did not edit take the new server values.
- `PUT /api/v1/settings` applies each supplied field and keeps absent fields as
  stored. `modelTier.usageRouting`, `modelProviders`, and each additional asset
  list replace the stored value whole when supplied.
### Silent failures name themselves

- Deleting a remote branch that origin does not hold counts as already deleted.
  Armada never pushes mission branches, so landing cleanup no longer records
  `merge_queue.branch_cleanup_failed` for them. One rule reads git's
  "remote ref does not exist" outcome for landing cleanup, the merge-queue
  purge, terminal reaping, dock reclaim and the branch cleanup sweep. The sweep
  summary counts `origin refs already absent` apart from removals and failures.
  When its lease delete fails, the sweep lists origin again, so a ref another
  writer removed after the listing counts as absent, while a ref that moved to
  another commit stays a failed operation and is kept.
  An unreachable origin, a rejected push or an authentication failure is still
  reported with git's reason, and the merge-queue purge now logs it at Warn
  instead of Debug.
- A genuine terminal process-exit failure that halts its voyage opens one High
  incident for the mission. Recovery skips missions of cancelled voyages, so
  before, such a failure never reached incident triage.
- A mission that no captain of its tenant can ever serve (persona or tier)
  records one `mission.unassignable_by_construction` event, and opens one High
  incident after 10 consecutive assignment passes. Before, it logged a Warn every
  tick forever and read as a capacity problem.
- Cancelling or halting a voyage marks its `Pending` armed Checks `Canceled`
  with the reason `voyage_cancelled`. The check executor applies the same rule
  to `Pending` records of any `Cancelled` or `Failed` voyage (`voyage_cancelled`
  or `voyage_failed`). These records no longer sit `Pending` forever or count in
  `PendingChecksRequired`.
- The landing-drain safety net no longer diffs the vessel's default-branch
  checkout when a mission has no dock worktree; that diff was always empty. It
  records a named no-dock outcome, logs it at Warn, flags the branch for review,
  and the sweep summary counts unmeasured branches (no dock, diff failed). A
  failed diff also logs at Warn instead of Debug.
- `ArmadaStatus.OverdueRunbookExecutionsCount` is removed. Runbook executions
  have no due time, so the field was never computed and always read 0, which
  read as "none overdue".
- A bad enum value on `armada_list_incidents`, `armada_create_incident`,
  `armada_update_incident` or `update_release` returns the field name and every
  valid value, through the same helper the check tools use. Before, the call
  failed with a bare deserialization error. The deployment tools take no enum
  arguments.
- An "insufficient balance" provider failure benches the captain like a credit
  failure. Quota reset parsing also reads a numeric `Retry-After` in seconds,
  "try again in / resets in N seconds, minutes, hours or days", and an ISO-8601
  reset instant, with no upper bound on the window. The stderr log gate keeps
  every line those forms appear on.
- The captain MCP connectivity probe accepts both `application/json` and
  `text/event-stream` and reads SSE `data:` bodies. Before, a Streamable HTTP
  server rejected the probe or its SSE answer failed to parse, and a connected
  captain read as having no Armada tools.
- Empty catches in landing retry, process-exit handling, the definition-of-done
  gate, dock provisioning, voyage cancel and mission delete, merge recovery,
  captain recovery, agent lifecycle and the base agent runtime now log the
  failure at Warn with what failed and what state it may have left. Behaviour is
  otherwise unchanged. This now also covers repository seeding cleanup, the
  Windows read-only attribute pass before a directory delete, final-message file
  deletion, heartbeat-loop cancellation and captain and mission output heartbeat
  writes. Killing a timed-out git process stays silent only when the process has
  already exited; any other kill failure logs at Warn.

### Recovery ignores missions closed by terminal-voyage reconciliation

- A mission that terminal-voyage reconciliation moved to Failed or Cancelled is
  no longer treated as a fresh failure. Recovery skips it before any write. It
  opens no incident, dispatches or defers no rescue, and records no recovery
  attempt. The failed-mission sweep, the rescue re-check after a dispatch hold
  clears, and mission outcome handling all apply the same rule. Before, the
  repair's fresh update time put every reconciled Failed mission inside the
  sweep's 24-hour window, so recovery opened incidents and dispatched rescues
  for work whose voyage had already ended.
- An incident already linked to such a mission closes as superseded, with a
  note that names reconciliation as the cause.
- One rule writes the reconciliation failure reason and recognises it, so the
  reconciler and every reader cannot drift apart. A genuine failure under a
  Failed voyage keeps its normal incident and rescue.
- Reconciliation now records a durable marker on the mission row
  (`reconciled_utc` and `reconciled_reason`), and the rule decides from that
  marker, not from the failure reason text. A later writer that replaces the
  reason no longer returns the mission to recovery. The reason text is still
  written for people. Restarting the mission clears the marker. The migration
  (SQLite 100, PostgreSQL 101, MySQL 92, SQL Server 95) backfills the marker on
  existing Failed and Cancelled missions whose reason the rule recognised.

### WebSocket command events reach the record's owner

- The mission and voyage change events that the WebSocket `cancel_voyage`,
  `cancel_mission` and `restart_mission` commands cause now follow the changed
  record's owner, like the same change made through REST or MCP. They reached
  global administrators only, so a tenant administrator did not see a change
  to its own tenant's mission. The calling session, the owning user and the
  administrators of the record's tenant receive them; another tenant's
  sessions do not.

### Mux authenticates to the Armada MCP endpoint

- Mux captains and Mux entries written by `armada mcp install` now carry a
  credential, so the authenticated MCP endpoint no longer refuses them. The
  captain's scoped `mcp-servers.json` uses Mux's `auth` object with a
  `bearer_token` scheme that references `ARMADA_MCP_TOKEN`, and the install
  entry uses an `api_key` scheme that sends `X-Api-Key` from
  `ARMADA_API_KEY`. No credential value is written to either file.
- The captain tool inventory probes a Mux captain's configured HTTP servers
  with the credential each server's `auth` object declares, so an
  authenticated server no longer reads as unreachable.
- The Helm client-payload check reads a Mux entry's `auth` object and proves it
  reaches the served endpoint instead of failing the entry as uncredentialed.
- The Gemini CLI install command form
  `gemini mcp add --scope user --transport http --header 'X-Api-Key: ${ARMADA_API_KEY}' armada <url>`
  is not yet verified against an installed Gemini CLI; `docs/MCP_API.md` names
  the check to run on a host that has one.

### Fleet status is a global-administrator read

- `GET /api/v1/status` now requires a global administrator. It returned
  fleet-wide captain, mission, voyage and signal state to any authenticated
  caller. An anonymous caller receives `401`, and a tenant user or tenant
  administrator receives `403`, matching the WebSocket `status.snapshot`,
  which already withheld fleet status from narrower sessions.
  `GET /api/v1/status/health` stays unauthenticated.
- The request-history suites use `GET /api/v1/whoami` as their captured
  request, because every role may read it.
- Every client of the status route names the refusal instead of treating it as
  a failure. The dashboard home page shows "Fleet status is available to
  global administrators." once, keeps loading the caller's own fleets,
  vessels, captains and missions, and does not request the status route again
  on refresh or live events. `armada status` prints the refusal and exits 1,
  and `armada watch` stops with the same message; it had reported
  "Connection lost" and retried forever. The SDK `GetStatusAsync` throws an
  `HttpRequestException` with status 403 and that message.

### Mission assignment stays inside the mission's tenant

- Mission assignment now selects and claims only a captain of the mission's own
  tenant. Selection read every idle captain and the claim matched the captain
  id alone, so a Pending mission could be assigned to another tenant's idle
  captain, and was assigned when that captain was the only one idle.
- Every assignment path (dispatch sweep, scheduler, rescue and restart) runs
  through the one assignment claim, which now names the tenant. The claim that
  ignored tenants is removed from the database interface and all four
  providers. A mission or captain with no tenant belongs to the default tenant,
  matching how older rows were backfilled.
### Test hosts start no agent CLI

- The automated test host and the shared end-to-end fixture no longer start
  the agent CLIs installed on the machine running the suite. A dispatched
  mission used to start the real runtime, for example `claude` on `PATH`, with
  that user's tooling and credentials. Those processes exited or ran against
  test docks, and every exit offered Pending work to other idle captains, so a
  captain a later test expected to stay idle could go Working. A test runtime
  start now ends only when it is stopped, which removes that trigger. The
  dispatcher still assigns Pending work to any idle captain, so a test that
  leaves work open can still hand it to a captain a later test creates; tests
  must cancel the work they dispatch.
- `ArmadaServer` accepts an `AgentRuntimeFactory`. Without one it builds its
  own from settings, as before, so production launches are unchanged.
- Both test hosts supply a test runtime factory. Every CLI runtime is a
  non-launching runtime: a start registers a synthetic process that stays
  running until it is stopped, then reports exit code 137. A test that needs
  another outcome raises the exit explicitly.
- After the run, the automated runner and the shared runner fail with
  `RESULT: FAIL (agent process launches)` when any agent process was started,
  and name each executable and its exit code. A suite that needs a real
  runtime opts in through `ARMADA_TEST_REAL_RUNTIMES` and skips with a named
  reason when the runtime is not opted in or not on `PATH`.
- `CaptainToolService` accepts the user profile directory it reads runtime
  configuration from; the default is still the current user's profile. A busy
  Claude Code or Gemini captain's describe reads `.claude.json` or
  `.gemini/settings.json` there and starts every MCP server listed, so the
  captain tool discovery unit tests read the developer's configuration and
  started its MCP bridges, which hung the unit suite. Those tests now pass an
  empty temporary profile, and a guard points the process profile at a
  sentinel configuration and fails when any discovery case lists or starts
  its server.
- The unit, automated, runtimes and shared runners, and the end-to-end
  fixture, remove model-provider credentials and agent-session variables
  (`ANTHROPIC_*`, `OPENAI_*`, `CLAUDE_CODE_*` and related names) from their own
  process before any test runs, unless `ARMADA_TEST_KEEP_PROVIDER_ENVIRONMENT`
  is set.

### Captain writes accept configuration only

- Captain create and update on REST, MCP and WebSocket now share one input
  mapping. It defines which fields a caller may set: name, runtime, model, model
  endpoint, provider key and base URL, system instructions, personas, runtime
  options, tier and default playbooks. Before, REST and WebSocket create stored
  `State`, the assignment, process, heartbeat and quarantine fields from the
  request body. A caller could create a captain already `Working` or with a
  forged quarantine, and bypass the state machine.
- A request that sends a server-owned field with a value other than its default
  (create) or its stored value (update) is refused. REST returns `400`, MCP
  returns a tool error and WebSocket returns `command.error`. The message starts
  with `captain_server_owned_field:` and names every refused field. Nothing is
  written. Before, MCP dropped such fields without saying so.
- WebSocket update no longer clears an existing quarantine, tenant, user or
  process liveness. REST update no longer takes `LastProcessAliveUtc` from the
  body. Stop, quarantine (bench) and unquarantine (unbench) remain the only ways
  to change captain state.
- `armada captain update` no longer sends identity fields. The SDK
  `CreateCaptainAsync` and `UpdateCaptainAsync` still take a whole `Captain`
  but send only its configuration fields, so a captain read back from the
  server can be sent again without a refusal.
### Harbor mission execution

- A captain or a vessel can opt into running its missions on one enrolled Harbor
  runner through `Harbor.MissionRoutes`. Routes do nothing while Harbor is
  disabled, and a mission without a route launches locally as before. A captain
  route takes precedence over a vessel route. Harbor stays disabled by default.
- A routed launch runs on its runner or not at all. It is refused by name, with
  no local fallback, when Harbor execution is not registered, the runner is
  enrolled to another tenant or user (`harbor_runner_owner_mismatch`), the
  runtime cannot run as a runner process, the captain uses an account login, the
  dock is outside the route's directory map, or the launch needs an environment
  variable that may not leave the Admiral
  (`harbor_launch_environment_unsupported`, naming the variables). Provider keys
  and the Admiral's MCP launch credential never travel to a runner.
- The runtime builds the launch plan exactly as for a local launch, and the
  runner's output and exit pass through the same parsing, mission log and
  lifecycle events. Each job runs under a synthetic process identifier that the
  process supervisor holds together with its stop, so process ownership,
  liveness, stall detection, the health check, stop and recovery treat a Harbor
  job like a local process. The dock, branch and landing stay on the Admiral.
- Harbor jobs are durable on SQLite, PostgreSQL, MySQL and SQL Server: job
  identity, runner, owner, enrollment and connection generation, state, host
  process id, exit code, last output sequence, mission and named failure reason.
  Writes are revision-guarded, so a delayed write never overwrites a newer
  state. At start the Admiral fails every job an earlier process left unfinished
  with `harbor_admiral_restarted`, and a runner that later reports one is
  refused as not rebindable.
- Every claim, output, exit and job error from a runner is revalidated against
  its durable enrollment before it changes a job. A runner revoked or
  re-enrolled on any Admiral instance is refused by name on its next frame
  (`runner_enrollment_revoked`, `runner_enrollment_generation_stale`), its link
  closes and its jobs are lost. Heartbeat refusals name the same reasons instead
  of `runner_owner_unavailable`.
- A runner disconnected longer than `Harbor.DisconnectedJobGraceSeconds`
  (default 180) loses its live jobs with `harbor_runner_disconnected`. A stop
  for a job whose runner is disconnected releases it with
  `harbor_job_released_runner_unavailable`.
- Operators list, inspect and stop Harbor jobs through
  `GET /api/v1/harbor-runners/jobs`, `GET /api/v1/harbor-runners/jobs/{jobId}`,
  `POST /api/v1/harbor-runners/jobs/{jobId}/stop` and the MCP tools
  `armada_harbor_jobs`, `armada_harbor_job` and `armada_harbor_job_stop`. They
  apply the same runner authority rule as enrollment; a job the caller may not
  see reads as not found, and a stop needs a tenant or global administrator.
- The in-process API runtime and Harbor jobs take synthetic process identifiers
  from one allocator, so the two can never share an identifier.
- A routed launch re-checks the mission tenant rule: a captain of another tenant
  is refused with `harbor_captain_tenant_mismatch` before anything reaches the
  runner. A mission or captain with no tenant belongs to the default tenant.
- A routed launch builds its plan with the runtime factory's built-in adapter,
  which never starts a local process, so a factory that replaces local launches
  (such as a test host's non-launching factory) still runs the real plan on the
  runner. A runtime with no CLI launch plan is refused with
  `harbor_runtime_unsupported`.

### Data expiry on every provider

- Data expiry now purges through a provider-neutral database driver method set,
  so it runs on PostgreSQL, MySQL and SQL Server as well as SQLite. The service
  opened a SQLite connection with the configured connection string, so on every
  other provider it failed each run with an unsupported connection-string
  keyword and never expired anything.
- The retention rules are defined once and cover the same tables as before:
  completed voyages and their missions, completed standalone missions, read
  signals, events, released docks and finished merge entries. Objective
  dispatch attempt events inside the reconciliation look-back are still kept.
  A mission whose expired parent is deleted loses the link on every provider.
- Each run logs one `data expiry summary:` line with the cutoff and per-table
  deleted counts, including runs that delete nothing. A failed statement names
  its table and provider.
- Production metric facts (mission attempt facts, preparation claim
  observations and lane state transitions) are now retained for
  `productionFactRetentionDays`, default 365, and purged by the same run; `0`
  keeps them forever. A production summary window older than the retention
  reports those measures as unobserved or unknown instead of a value.
### Per-account captain logins

- A usage account can name its captain `runtime` and a login `homeDirectory`,
  or for Cursor the name of a key variable. Its captains launch with
  `CLAUDE_CONFIG_DIR`, `CODEX_HOME`, `XDG_DATA_HOME`, or `CURSOR_API_KEY`.
  Accounts without these fields launch on the shared login, as before.
  Settings hold paths and variable names only.
- Validation rejects an unsupported account runtime, a collector for another
  runtime, a captain whose runtime differs from its account, and a login on an
  account that lists a captain with its own provider key or base URL.
- A missing home, login file, or Cursor key makes the account Exhausted with a
  named reason in settings status and the usage preview. A launch on such an
  account fails with that reason instead of using the shared login. When such
  an account blocks every approved route, the routing decision reason is that
  account code (for example `account_login_expired`), so the preview `reason`
  and the scheduler's deferred-mission log name it instead of a generic
  allowance shortage.
- Claude Code and Codex accounts also run the runtime's own login status
  command in the account home, in the background with a timeout and a cached
  result. An expired or revoked login reads `account_login_expired`; a hang,
  missing CLI, or unreadable output has its own named reason. OpenCode and
  Cursor keep the file or variable check, because their status commands cannot
  verify one account's credential.
- Codex external-provider profiles are written into the account `CODEX_HOME`.
  The Codex usage collector measures each account through its own home, so two
  Codex accounts report separate windows. Claude and OpenCode Go collectors
  read the login file inside the account home.
- A quota, billing, or authentication failure on one captain holds its whole
  account Exhausted until the retry time and quarantines the account's idle
  captains, so the re-routed mission goes to a different account.
- The Smart Routing account template and status table show the runtime and the
  hold expiry. Rollout of a second subscription account needs an owner
  decision under the provider's terms. See
  [account logins](docs/USAGE_ROUTING.md#account-logins).

### Provider-aware backup and restore

- MCP `armada_backup`, REST `GET /api/v1/backup` and the WebSocket `backup`
  command now share one backup service. It takes a verified provider-native
  backup of the configured database (SQLite, PostgreSQL, MySQL or SQL Server)
  and restores that backup into an isolated target to check it. The manifest
  records the provider, the schema version and the record counts that provider
  reports. Previously every provider got a SQLite snapshot of
  `databasePath`, so a PostgreSQL admiral reported a successful backup of an
  unused empty file.
- A failed native backup or isolated restore check now returns a named reason
  and leaves no archive. SQL Server keeps its artifact on the database host and
  the manifest records that path.
- Restore replaces the database only on SQLite. It restores through the SQLite
  online backup API into the configured database file, after a verified safety
  backup. PostgreSQL, MySQL and SQL Server are refused with
  `restore_unsupported_for_provider_<type>` before the archive is read. An
  archive from another provider is refused with `backup_provider_mismatch`.
  REST returns 409 for refusals and 500 for failures.
- Backup archives write `settings.json` with every secret replaced by
  `[REDACTED]`: database and connection-string passwords, API keys, tokens,
  provider keys and other values the shared rule in `SecretRedactor` marks
  secret. The manifest records `settingsRedacted` and `redactedSettingCount`.
  Restore merges archived settings onto the host's settings, keeps the host's
  own secrets, omits redacted values that have no local counterpart, and never
  writes the placeholder.
- The server image installs the PostgreSQL 16 client (`pg_dump`, `pg_restore`,
  `psql`, `createdb`, `dropdb`) and the MySQL 8.0 client (`mysql`,
  `mysqldump`) that native backup and the self-deploy preflight run. SQL
  Server's ODBC 18 `sqlcmd` still has to be added in a derived image, because
  it needs Microsoft's repository and an EULA acceptance.
- `scripts/common/install-database-client-wrappers.sh` links a container
  wrapper under each native client name, so native backup and its database
  test can run on a host whose databases run in containers without client
  packages. The database backup case reports a named, counted skip
  (`native_client_missing_<tool>`, `sqlserver_backup_directory_not_configured`)
  when a prerequisite is absent, and the database test summary lists skipped
  cases with their reasons.
- A missing native client or `dotnet` runtime is reported as
  `native_client_missing_<tool>` by the backup provider, the backup service,
  the self-deploy preflight and the candidate validator, instead of a generic
  stage failure.

### Mission lifecycle after a voyage ends

- A mission no longer stays WorkProduced after its voyage ends. One rule decides
  its terminal status from landing evidence, not from the voyage status. Work
  whose commit is on the vessel default branch, or whose merge entry landed,
  becomes Complete. Unlanded work becomes Failed under a Failed voyage and
  Cancelled under a Complete or Cancelled voyage. The mission records a named
  reason, and each change records a `mission.terminal_voyage_reconciled` event.
- The rule keeps a mission unchanged, and names why, when ancestry is unknown,
  a landing is still in flight, the voyage ended less than ten minutes ago, or
  a Complete voyage asked for no landing.
- The health loop reconciles voyages that ended in the last 24 hours. The new
  `armada_reconcile_terminal_voyage_missions` tool repairs older rows. It is a
  dry run by default and never deletes branches, refs or commits.
- WorkProduced to Failed is now a legal mission transition.
- Branch cleanup stops keeping branches for reconciled missions, so the sweep's
  kept-for-active-missions count drops after a repair. Status counts stop
  reporting ended work as WorkProduced.

### API endpoint runtime lifecycle

- Preserve synthetic API captain liveness until the loop exits, reject pre-cancelled
  starts, and clean registrations when lifecycle callbacks fail.
- Record provider-reported streaming token usage and fail closed for unsuccessful,
  oversized, or iteration-exhausted responses.
- Resolve tenant-owned endpoints for captain chat and stop routes without creating
  an unconfigured API runtime.
- Manual completion proves an API captain's ownership from its running in-process
  loop, and unsupported runtimes still fail closed.
- A lifecycle test launches an API captain and proves provider-reported usage
  reaches the mission token usage event.
- Ask chat applies the same endpoint admission rule as mission launch to the exact
  endpoint snapshot it runs, so private, disabled or mismatched endpoints are refused
  before a runtime exists.
- API captain tool calls are emitted as canonical activity records carrying only the
  tool name, primary path or pattern, and status, so chat and mission output keep them
  apart from the model answer.
- The captain tool catalog reports an API captain's real workspace tool registry
  instead of an Armada MCP preflight, and planning sessions report API captains as
  unsupported instead of failing at runtime creation.
- Hosted cloud providers (OpenAI, Anthropic, Gemini) are refused for API captains
  until `apiCaptainCloudProviders` lists them; operator-hosted Ollama and
  OpenAI-compatible endpoints need no opt-in. Loopback fixtures verify each hosted
  provider's request path, credential header, model and workspace tool catalog.
  Azure OpenAI, Vertex AI and Bedrock remain unavailable.

### A captain's first terminal marker ends its stage

- The first `[ARMADA:VERDICT] PASS|FAIL|NEEDS_REVISION` or
  `[ARMADA:RESULT] COMPLETE` line in a captain's streamed output is recorded
  as the stage's terminal marker. When the process is still running
  `autonomousRecovery.terminalMarkerGraceSeconds` later (default 60, 5-3600),
  Armada stops it, records `captain.terminal_marker_stop`, and completes the
  stage from the recorded output as a clean exit.
- No stall Mail nudge is sent to a mission whose output already carries its
  terminal marker. Each withheld nudge is logged and counted, and one
  `autonomous_recovery.mail_nudge_suppressed` event names the marker.
- The recorded Judge verdict is the first canonical `[ARMADA:VERDICT]` line;
  a later re-review verdict cannot replace it. Output with no canonical line
  keeps the existing fallback to the runtime's `[verdict]` echo or a labelled
  verdict.

### A quiet captain that is still writing its dock is not stalled

- A stall decision now reads three signals: the captain's output (its
  heartbeat, or provider progress for a runtime that reports it), the newest
  write in its dock worktree outside `.git`, and the committer time of its
  branch tip. Any signal inside the stall window clears the stall. A runtime
  that streams nothing between tool calls is no longer nudged, killed or
  restarted while it edits files or commits.
- One shared evaluator makes the decision. The autonomous recovery Mail nudge
  and the admiral heartbeat-stall kill, restart and recovery-exhausted failure
  both call it; neither keeps its own threshold check.
- Each decision records `captain.stall_confirmed` (every time) or
  `captain.stall_cleared` (when it first clears or its clearing signal changes,
  then at most once per stall window). The event names the deciding signal and
  carries the evidence: output age, newest dock write, entries read, and branch
  tip time. The nudge event carries the same evidence.
- A failure to stop a stalled captain's process is now logged instead of
  swallowed.

### A completion is de-duplicated per launch, not per mission

- The completion handler still skips a repeat completion for the same launch
  of a mission for 30 seconds, because the process-exit callback and the
  health check can both report one exit. A completion for a later launch is now
  processed inside that window, whichever path returned the mission for another
  attempt: a Judge Check-hold or missing-verdict re-run, a refusal or safeguard
  continuation, a transient requeue or quota re-route, an operator restart, a
  review denial, a merge-recovery redispatch, a stale-captain reset, or a stall
  relaunch. Before, a requeued mission that completed again inside the window
  stayed InProgress with its completion silently dropped.
- A launch is identified by the mission's start time and agent process. One
  rule decides it for every path, and the refusal continuation's private
  release of the guard is removed. A late duplicate for a mission that has not
  been launched again is still skipped.

### Rescue effectiveness follows the objective's declared deliverable

- The ineffective-rescue rule now reads what the linked objective kind
  declares as its deliverable. A rescue under a `Chore` objective, whose
  deliverable is a committed document such as an audit report, a
  discoveries record or a census, is effective when it commits any change,
  documentation included. An empty change set is still `ineffective_rescue`.
- `Research` objectives stay report-only. `Feature`, `Bug`, `Refactor`,
  `Initiative` and unlinked rescues still fail as `ineffective_rescue` when
  they change nothing or only documentation.

### Dispatch hold covers autonomous rescues

- Autonomous rescue dispatch now obeys the fleet-wide dispatch hold through the
  same admission rule as operator and scheduler dispatch. While the hold is
  engaged, a recoverable failure creates no rescue voyage or mission and spends
  no recovery attempt. The incident's recovery notes name the hold once per
  engagement (`dispatch_hold`, holder, time, reason), and an
  `autonomous_recovery.rescue_deferred_dispatch_hold` event is recorded.
- The first recovery sweep after the hold clears re-evaluates every deferred
  rescue, including failures older than the sweep lookback window. A hold
  refusal is a typed `DispatchHoldActiveException`, so callers can defer the
  work instead of failing it.

### Captain brief budget

- Every module that embeds the mission description now reads one bounded copy.
  Persona templates that restate the objective previously embedded the full
  persisted description, so an Architect brief could exceed the captain
  instruction budget several times over.
- The total-budget backstop now shrinks the embedded description (every copy,
  by bytes, keeping the head brief and the newest handoff block) and then the
  reference-only modules (skills, git anchors, code-index guidance) when content
  modules alone cannot bring a brief under budget.
- A rescue brief bounds the failed mission's failure reason, so a failure reason
  that holds a whole gate log no longer multiplies the rescue description.

### Authorization policy in every captain brief

- Project profiles carry an optional authorization policy. Every brief path
  (operator dispatch, the objective scheduler, retries and autonomous rescues)
  renders it through one shared module, verbatim, together with fixed hard
  limits on secrets, tenant isolation, protected paths and destructive
  operations that no policy can relax. The module is never elided by the
  brief budget backstop. Schema migration adds the column on all four providers.
- Persona overrides, skills and the authorization policy resolve the project
  profile through one shared lookup.
- A captain refusal is classified by kind (the structured
  `[ARMADA:RESULT] REFUSED` marker first, then provider safeguard text, then
  declining prose) and recorded as a `mission.policy_refusal` event with its
  reason. When the brief carried an owner policy, the mission gets at most one
  continuation on an approved captain of a different runtime; assignment then
  excludes every captain on the refusing runtime with no fall-back. A second
  refusal, or no approved alternate, fails the mission with the reason instead
  of retrying the blocked path.
- A provider safeguard block that ends the captain process follows the same
  rule, with or without an owner policy. It replaces the previous safeguard
  re-route, which benched the captain and retried up to five times on any
  peer, including the runtime that blocked.
- Autonomous recovery does not rescue a mission that failed with a
  `policy_refusal:` reason, because a rescue would repeat the blocked path.

### Execution environment requirements in dispatch preflight

- Prepared research can declare what the captain execution environment must
  provide: operating system, architecture, executables, dependency paths,
  isolation boundary and a licensed context by name. Values that are not short
  names are refused for the licensed context, so no license material or
  credential is stored.
- Dispatch preview checks each requirement against the environment captains
  launch in (executables by PATH lookup, never run) and reports every
  unavailable one as a blocking `execution` finding. The objective scheduler and
  manual dispatch share this preview. `AvailableLicensedContexts` in settings
  lists the licensed context names captains can use.

### Objective dispatch admission

- The in-process objective link lock no longer keeps one entry for every
  objective the process ever linked. Entries are reference counted and removed
  when no caller holds or waits for the objective, while callers for one
  objective stay serialized. The lock is local defense-in-depth; the database
  admission lease remains the cross-instance guarantee.
- A Completed or Cancelled objective now rests in the `Inbox` backlog state in
  the same row write that makes it terminal, whether the change comes from a
  manual edit, an import, a recovery link or scheduler reconciliation after
  landing. One shared rule applies on every objective write and read, so a
  terminal objective never lists or selects as `ReadyForDispatch`. A data
  migration on all four providers moves existing terminal rows out of active
  backlog states and leaves nonterminal rows unchanged.
- A planning-session dispatch now admits the session objective and every other
  objective linked to the session before it creates the voyage, taking their
  leases in a stable order, and links them all inside that admission. A busy or
  already-dispatched objective refuses the whole dispatch before creation; a
  later link failure restores the linked objectives and cancels the voyage. The
  REST and MCP planning routes no longer link objectives after creation.
- A busy objective admission now returns a bounded, retryable
  `objective_dispatch_busy` conflict with `RetryAfterSeconds` after five
  seconds instead of waiting until the request is cancelled; the scheduler
  skips with `admission_busy`. Each admitted dispatch writes durable attempt
  events whose id holds the admission leases, and linking re-confirms lease
  ownership with a compare-and-set renewal. The health loop reconciles an
  attempt that stopped between voyage creation and linking: a voyage linked to
  any admitted objective is kept, an unlinked orphan voyage is cancelled, and an
  ambiguous match is reported without cancelling anything.
- Shared voyage dispatch checks the admiral's dispatch hold before admission
  and before any voyage is created. An alias-ordered dispatch no longer creates
  and then cancels an empty voyage while the hold is engaged.
- Automatic event retention keeps objective dispatch attempt records younger
  than the seven-day reconciliation look-back, whatever `dataRetentionDays`
  says, so an unclosed attempt cannot be purged before it is reconciled. Manual
  event deletion remains an explicit operator action and is documented as a
  risk.

### Slop Check for .NET vessels

- Added a `Slop` Check type. Dispatch arms it beside Build and UnitTest on
  .NET vessels, on the operator, scheduler and recovery paths, and never arms
  it alone. `VoyageCheckArming.ArmSlop` switches it off.
- The Check runs Armada's own classifier on the added lines of the reviewed
  diff, so it works for every captain runtime and needs no installed tool.
  Skipped or ignored tests, project-wide `NoWarn`, and central package version
  bypasses fail it. Empty catch blocks, literal delays and warning suppressions
  are reported as WARN findings in the output and do not fail it.
- A `slop-allow <Rule>: <reason>` comment suppresses one finding when it names
  the rule and records a reason. Every condition that prevents classification
  fails the Check with its reason; none passes it.
- The dispatch preview lists the Slop Check when dispatch would arm it, and the
  dashboard offers the type for .NET workflow profiles.

### Health-loop maintenance and branch cleanup sweep

- Health-loop maintenance steps now run in isolation. A step that failed on
  every run of a short cadence aborted every longer cadence sharing its cycle
  numbers: data expiry failing on PostgreSQL every 100 cycles meant the branch
  cleanup sweep (200) never ran, and disk reconciliation and the code-index
  staleness sweep lost every run on those cycles. A failing health check no
  longer stops the cycle count. Each failure logs as `<step> failed: <reason>`.
- The branch cleanup sweep logs one summary line on every run, with its counts
  and the reason for each skipped vessel, including runs that removed nothing.
- Under `LocalAndRemote` the sweep lists origin itself, so a landed mission
  branch that exists only on origin is removed. Origin deletions carry a lease
  on the measured tip.
- Landed preserved refs (`refs/armada-preserved/`) are removed from the vessel
  bare and, under `LocalAndRemote`, from origin once their tip is older than
  `branchCleanupPreservedRefRetentionDays` (default 14; `0` keeps every
  preserved ref). Unlanded preserved refs are always kept and counted.
- The sweep keeps a landed branch that a non-terminal mission still names, and
  it reports a vessel whose default branch is missing as an error instead of a
  clean run. `recover/` refs and human branches remain outside its scope.
- The sweep now also retires the dock and mission reclaim anchors
  (`refs/armada/docks/<dockId>`, `refs/armada/missions/<missionId>`) that dock
  reclaim pushes to origin and that hosted remotes never prune. An anchor is
  kept while its dock or mission is live, while a `recover/` branch points at
  the same commit, while its tip is unlanded, and inside
  `branchCleanupPreservedRefRetentionDays`. Origin deletions carry a lease on
  the measured tip, and the summary line counts each family separately.
### Production attempt facts

- Mission launches, automatic re-runs, restarts, review denials, failures and
  landings now append durable attempt facts with the original mission of the
  chain and the typed autonomous-rescue marker, on all four database
  providers.
- The production summary calculates first-pass acceptance and rescue runtime
  share from these facts across the whole attempt chain, including recovery
  voyages that are not linked to the objective. Missions that ran before facts
  existed are reported as historical with explicit coverage instead of being
  classified from parent links or titles.

### Lane time and host-slot wait

- Armada-executed Checks persist the host command-slot request time,
  separate from creation and start, on all four database providers. The
  production summary reports preparation delay and pure host-slot wait
  separately from execution, with unknown coverage for Checks started
  without a recorded request.
- Each scheduler sweep records shared-lane eligibility, occupancy, capacity
  and fleet-wide block reason as append-only transitions with a trust
  window. The production summary reports eligible idle lane-minutes
  separately from fleet-capacity and dispatch-hold time, with unobserved
  lane-minutes and incomplete intervals stated explicitly.

### Preparation claim observations

- Objective preparation writes and dispatch links append durable claim
  observations (established, re-established, revalidated, reused) with the
  claim id, immutable anchors and a one-way fingerprint, on all four database
  providers. Claim text, evidence paths and search queries are never stored.
- The production summary reports repeated research as re-established claims
  only, separates stale-claim revalidation and reuse, and states slice
  coverage and per-source-family observation counts.

### Post-land regression links

- Incidents carry a typed regression purpose (consumer or ledger), cause, and
  links to the originating objective and landed commit. Checks carry the
  purpose and the same links, stored on all four database providers, and a
  Check retry keeps them. REST and MCP incident and Check writes accept and
  validate the fields.
- The production summary reports consumer and ledger regression rates per
  group separately, with unattributed, unlinked, outside-cohort and
  not-a-regression records counted explicitly.

### Native self-deploy preflight

- Added provider-native backup, owned isolated restore and candidate database
  validation for all four providers. Native process output and cancellation are
  bounded, and Unix settings and backup directories use private permissions.
- Private self-deploy and backup storage on Windows now uses verified owner-only
  ACLs: a protected descriptor owned by the current user with a single
  full-control rule for that user, read back after every change. Anything else
  fails closed with `private_storage_acl_unverified`, and an existing directory
  is verified but never modified. The real Windows ACL test runs only on
  Windows and has not yet been run on a Windows host.
- The native preflight is now the default for every self-deploy cutover: backup,
  owned isolated restore, candidate `--validate-database` and cleanup against
  the running admiral's database. A failed step, a missing native utility, or
  SQL Server without `selfDeploy.sqlServerBackupDirectory` refuses the cutover
  before any process starts or restart record is written. The placeholder
  preflight that always refused is removed. Self-deploy stays disabled by
  default.
- Self-deploy Release builds use the bounded native command runner with an
  argument list, bounded output capture, configured timeout, and caller
  cancellation that terminates and observes the child process tree.
- Added a local Docker rebuild helper that retains the running image and the
  current mutable tag under unique dated tags before the build. Retention
  collisions and Docker inspection or tag failures stop the build, and failed
  builds keep both rollback references.

### Supervised self-deploy cutover and rollback

- Replaced the PID-only watchdog scripts with a server supervisor mode
  (`--self-deploy-supervise`) launched from the immutable rollback artifact.
  Processes are identified by id and start time, so a reused id is never
  signalled, and the candidate starts only after the previous admiral is
  confirmed gone. The old `selfDeploy.supervisorScriptRelativePath` setting is
  removed.
- The running build and the candidate build are copied into content-addressed,
  read-only artifacts in private storage and re-verified before every launch. A
  changed or added file refuses the launch.
- A durable restart record with compare-and-swap transitions carries the
  admiral-supervisor handshake, both launches, health outcomes and rollback. A
  normal admiral start is refused while a restart is unresolved, and
  `--self-deploy-recover` drives an interrupted restart to a terminal state
  without promoting an unsupervised candidate.
- Health cutover requires the loopback health endpoint to report healthy from a
  server started after the launch, within `selfDeploy.healthTimeoutSeconds`.
  A failed candidate is stopped and the rollback artifact is relaunched; a
  candidate that advanced the schema blocks rollback for an operator restore.
- Self-deploy fails closed inside a container, when the rollback or candidate
  capture fails, when the admiral identity or schema version cannot be read,
  when the supervisor does not arm, and while another restart is unresolved.
  New bounds: `selfDeploy.handshakeTimeoutSeconds` and
  `selfDeploy.oldProcessExitTimeoutSeconds`.
- Private self-deploy directories now create every missing parent with
  owner-only permissions, and process termination is confirmed by observed exit.
- The self-deploy release store is bounded on each cutover. It keeps the
  running and rollback releases, every release an unresolved restart record
  names, and the newest `selfDeploy.retainedPreviousReleases` others (default
  2). It removes nothing while the restart record is unreadable.
- Added `scripts/common/rehearse-self-deploy-cutover.sh` and a gated server
  `--self-deploy-rehearse` mode. With real binaries and disposable SQLite
  copies, the rehearsal proves three things: a preflight refusal leaves no
  record, a healthy candidate commits, and a `kill -9` of the supervisor makes a
  normal start refuse and recovery end with one healthy owner. The mode is
  refused unless `ARMADA_SELF_DEPLOY_REHEARSAL=isolated-disposable` and a data
  directory override are set.
- Cutover decisions re-read a briefly unverifiable process state for a bounded
  window before failing closed. A candidate that exits at once is therefore
  recorded as exited rather than as unverifiable.
### Code index context pack budget

- A context pack whose budget runs out before search produces results now
  stages the lexical pack instead of throwing. On a loaded host the budget
  could expire before the local index was read, and the build failed with
  `TaskCanceledException`. The build now reruns the search lexically on the
  caller token: one local read, with no index update, embedding or graph boost.
  The pack still reports `context_pack_budget_expired`. Caller cancellation
  still propagates.
- The context pack budget, the summarizer timeout and the elapsed metrics run on
  an injectable `TimeProvider`, which defaults to the system clock. The budget
  and summarizer tests advance a controlled clock after the stage under test
  starts, instead of racing real delays against real budgets.

### Remote dashboard websocket relay

- A relay session is removed from relay state before `armada.ws.closed` or
  `armada.ws.error` is published. Before, the receive loop published the event
  first, so a message the proxy sent in reaction still found the closing
  session: it reached the socket and got 202 or 502 instead of 404. Removal
  matches the session instance, so a new session that reuses the proxy socket
  id is never evicted. A test sends from inside the closed-event publish and
  requires 404.

### Deterministic assignment test harnesses

- Unit test harnesses that build a mission service now inject resource-pressure
  admission with a fixed memory probe. Before, they used the production default,
  which reads live host and container memory. Under a loaded full gate that
  measurement fell below the admission floor, and the assignment, pipeline
  handoff and self-heal families failed together with "should assign" false.
  They passed alone. Each harness keeps its own admission policy.
- Added a self-heal test proving that a missed handoff deferred by memory
  pressure still stamps the upstream branch, parks at WaitingForResourcePressure
  and leaves the captain idle.
- Provider-routing tests no longer read the environment of the process that
  runs them. A new process start copies that environment, so an exported
  ANTHROPIC_BASE_URL looked like a routing write and nine "left alone" tests
  failed. Decision tests now start from an empty environment. Tests that clear
  the provider key restore it even when an assertion fails.
- Added a test pinning the launch behavior against an exported operator
  endpoint and key: a native captain inherits both unchanged, and a routed
  captain replaces both with the provider's values.
- Dispatch now tracks its queued background assignment work, and tests can
  wait for that work to finish. Review-gate and dock-retry tests used to poll
  until the first stage looked assigned and then continued while the background
  work still visited later stages. Under load the review action or retry lost
  the sibling-lane lease or the mission row to that work and left the mission
  Pending. Those fixtures now wait for the queue to drain and assert the settled
  state, including captain release.
- The refusal-continuation safeguard test read the mission row while the queued
  assignment for the continuation was still running, so under load it saw
  `Assigned` where it expected `Pending`. It now waits for queued assignment
  work to drain and asserts the settled result: the continuation runs exactly
  once, on the alternate-runtime captain, and no captain on the blocking runtime
  takes it.
- The lost-admission dispatch test drove the loss with a 90 ms lease and a
  100 ms sleep. Under load the lease could lapse or renew on its own timer, and
  dispatch then succeeded. The test now keeps the default lease and releases
  it explicitly after the voyage is created, so only the link fence can
  detect the loss. A failure message names what dispatch returned.
- The fixed resource-pressure probe and its admission factory moved to the
  shared test infrastructure, so there is one copy. The unit harnesses use it
  from there, and every mission service the shared Touchstone service suites
  build now injects it. Those suites used the host memory probe and, under a
  memory cap, deferred assignments that their assertions expect to launch.
- The MCP enumerate status-filter test created two assignable missions and
  expected them to be Pending. When the suite ran alone, idle captains left by
  earlier cases claimed them and the filter found fewer than two. The missions
  now depend on a cancelled mission, which is never assigned, so they stay
  Pending whatever captains exist. Each create response must report Pending.
- The status and event tests now cancel the voyages they dispatch, in a
  `finally` block, before the test returns. Those voyages sit on real vessels,
  so each of their missions launched an agent. Every agent exit triggers a
  sweep that assigns Pending work to any idle captain. The open voyages kept
  offering missions into later suites. The authentication test's newly created
  captain was sometimes claimed before its delete, which returned the
  documented 409. A cancelled voyage's missions are never assigned again.

### Helm configuration and branch client

- Add grouped help and help aliases, with detected OpenCode and Mux MCP setup.
  Retain Board commands and the Codex startup timeout.
- Preserve unrelated JSONC text and UTF-8 BOMs during managed entry changes.
  Reject malformed original input and ambiguous managed entries before writes.
- Add typed read-only vessel branch inspection and actual HTTP route tests for
  health, vessel serialization, branch inspection and authentication errors.
- Per-command help renders for every registered command without starting an
  Admiral; `voyage create --help` no longer fails on unescaped markup. Helm
  request and response bodies use named enum values, so `ask` reads the
  Admiral's reply kind and objective status and priority are sent by name.
- The embedded Admiral reads settings through the same loader as Helm commands,
  so it binds the saved ports and accepts the saved bearer key instead of
  silently starting on defaults. First-run settings initialization writes once
  and never rewrites an existing file.
- `armada mcp install` payloads for Claude Code, Cursor and Gemini CLI (and Mux
  or OpenCode when present) are checked against a live Admiral: each advertised
  URL answers an MCP `initialize`, and file-based Claude Code and Cursor entries
  install and remove idempotently while keeping comments and other servers.

### Guarded vessel branch writes

- Add tenant-administrator push and merge routes for a vessel's landing
  repository. Each request names source, target and (for a push) the remote.
  Refs are validated with git. Pushes go only to an `origin` that matches the
  vessel repository URL, never force and never delete. Merges fast-forward or
  create an explicit merge commit, advance the target by compare-and-swap,
  verify ancestry and never push.
- Refuse writes, with a named reason and no ref change, for a dirty or
  detached working checkout, a missing ref, a checked-out target, a conflict,
  a non-fast-forward update, a protected or release target, and a branch still
  owned by an unlanded mission or an active merge-queue entry.
- Mission landing, merge-queue entry processing and branch writes now share one
  per-vessel repository slot. A write that finds it held is refused as busy.
- The branch listing reports write-control availability. The vessel page shows
  branches with Push and Merge controls only when available, a confirmation
  dialog stating source, target, strategy or remote, and busy and refusal
  states.

### Shared test runner

- `src/Test.Automated` is in `src/Armada.sln`, so the solution build compiles the shared suite runner on every change.
- Shared end-to-end suites start their server with the same scheduler and capacity settings as the automated runner, and the mission and voyage suites cancel each case's active work afterwards, so fleet capacity admission no longer refuses later creates with 409.
- Shared MCP cases call the Streamable HTTP endpoint with the event-stream accept header, use the served tool names, follow `tools/list` pagination, and wait for asynchronous dispatch and merge-queue jobs.
- Shared WebSocket cases authenticate each session before subscribing or sending a command.
- A REST vessel update that omits `gitHubTokenOverride` keeps the stored override; only an explicit value replaces or clears it.
- Request-history summary buckets sit on the epoch grid for every bucket width, through the same rule token-usage summaries use, so a two-hour bucket no longer splits into hourly buckets.
- The shared runner runs in `scripts/common/run-tests.sh` as the `shared` suite. It lists every skipped case with its reason and exits with code 2 when discovery fails, the suite filter matches nothing, or the selection would execute nothing.
- Shared cases that duplicate an executed legacy case, or assert behaviour the fork does not implement, are recorded once and reported by every runner as named, counted skips. A record that names no case, or a legacy owner its file no longer registers, fails discovery.
- The shared review-gate fixture waits for dispatch's queued assignment work through the admiral's drain, which the core assembly now exposes to the shared test assembly, instead of racing it.

### API collection and client route contracts

- The Postman collection now covers every served Admiral `/api/` route and every
  proxy `/proxy-api/` route, with request bodies taken from the fork request
  models, admin and destructive-action notes, and new Ask, Coordination, Jobs,
  Model Endpoints, Project Profiles, Skills, Token Usage and Code Index folders.
- Removed examples for tenant, user and credential enumerate routes the Admiral
  does not serve, and rebuilt eight request bodies that had been truncated to
  invalid JSON.
- Replaced the retired proxy instance-management examples with the proxy login,
  deployment selection and logout routes plus relayed Armada requests that send
  the proxy session header and keep the Armada bearer credential.
- A contract suite reads the live Admiral and proxy routing tables and fails when
  a collection request has no served route and method, a served API route has no
  request, a JSON body does not parse, a relayed proxy request lacks its session
  header, a variable is undefined, or a no-auth Admiral example is refused.
- A client route contract invokes every public `ArmadaApiClient` method and fails
  when a call reaches a route the Admiral does not serve.
- Collection requests for vessel branch push and merge, and a Harbor folder for
  runner enrollment create and revoke. Harbor requests start with `Requires
  Harbor:`; the contract reads a second Admiral started with Harbor and
  WebSockets enabled, and fails when a marked request is served with Harbor off
  or a Harbor-only route is unmarked or has no request.
- The REST API reference no longer lists tenant, user and credential enumerate
  routes, which the Admiral does not serve.

### Model endpoint health persistence

- Track remaining configuration, migration, heartbeat and manual-completion
  review findings before accepting their integration candidates.

- Update endpoint health only when the stored update timestamp matches the
  observed version. Preserve concurrent configuration changes on all providers.
- Preserve SQL Server fractional timestamps so valid health updates can pass
  the version check.
- Run endpoint health sweeps in a separate cancellable loop so a slow provider
  cannot delay the core captain and dispatch health loop.
- Require exact provider metadata for captain endpoint foreign keys, classify
  deletion conflicts by provider error code, and enforce the named Mux endpoint
  validation contract.

### Harbor identity core

- Add a disabled session registry that requires a verified principal and an
  authoritative runner owner. Deny unknown owners and stale connections.
- Bind pending responses to the runner and connection generation. Issue unique
  request IDs and reject replayed responses. Transport and process launch remain
  separate integration work.

### Harbor runner enrollment

- Persist administrator-controlled runner ownership with tenant, user,
  authentication-method, and credential identifiers. Raw credential values are
  never stored in the enrollment record.
- Use atomic generation checks for enrollment and revocation. Revalidate the
  enrollment and its credential on every owner resolution so revoked runners
  and credentials fail closed without a process restart. Harbor remains
  disabled by default.
- Bind each live session and pending response to the durable enrollment
  generation. Revocation and re-enrollment invalidate old sessions and pending
  work before a new generation can create work.
- Validate Harbor schema types, nullability, keys, names and MySQL binary
  collations before replay. Exercise fresh, restart, malformed-schema and
  provider race cases against isolated databases.
- Revalidate connected sessions against the durable generation. A session that
  fails, including after a revocation on another instance, is removed and its
  pending work is canceled; re-enrollment does not revive it. A newer durable
  generation replaces a stale connected session instead of blocking the new
  owner.
- Apply one authority rule to enrollment, reuse of a revoked runner and
  revocation: a tenant administrator needs the owner in the same tenant, and
  the owner must not be a global administrator.
- Add a combined database scenario that interrupts the captain endpoint-link
  and Harbor enrollment migrations in order, rejects an incompatible partial
  table and checks restart, history, persistence and conditional writes. It
  asserts the specific migration versions, so later migrations do not break it.

### Harbor runner link

- Add a Harbor WebSocket link that stays disabled by default (`Harbor.Enabled`). When disabled no link or
  enrollment route is registered. When enabled it requires the WebSocket server and is served on the existing
  REST port.
- Authenticate the link only through the standard credential headers. Tenant, user and access-key headers are
  ignored, invalid credentials are refused before any frame is read, and the handshake must name a runner
  enrolled to the verified principal.
- Authorize each launch and stop through the same runner authority rule as
  enrollment and revocation, so a tenant administrator cannot command a runner
  owned by a global administrator; the runner owner is also allowed. Server-issued job identifiers are bound to the runner,
  enrollment generation and connection generation. Foreign, stale, replayed, out-of-order and duplicate runner
  reports are refused with stable reasons.
- Reject duplicate launches and enforce advertised capacity; never fall back to another runner. Rebind live jobs
  after a reconnect with the same enrollment; jobs from a revoked or earlier enrollment become lost.
- Add administrator enrollment and revocation routes that read owner identity from the credential record.
- Remove the unimplemented git and standard-input message types, and add an output sequence number. Isolated
  tests drive two fake runners over the real transport with SQLite enrollment and real credentials.

### OpenCode provider failures

- Parse captured top-level provider errors into bounded, redacted activity.
- Fail Ask replies after terminal provider errors, including errors after partial
  text. Preserve the named failure in retryable planning and refinement sessions.

### Model endpoint persistence

- Add append-only endpoint storage on all four database providers. Preserve
  Unicode IDs, full-value uniqueness and existing migration history.
- Default endpoints to disabled. Reject incompatible partial schemas and corrupt
  stored provider, kind and scope values.

### Scoped model endpoint service and captain links

- Add authenticated model endpoint CRUD and provider-specific validation routes.
  Tenant-wide and user-specific ownership rules, write-only API keys, disabled
  defaults, bounded requests, disabled redirects and safe error responses apply
  across the service.
- Restrict health sweeps to global administrators. Health updates use a
  conditional timestamp write so a probe cannot overwrite a concurrent endpoint
  edit.
- Persist nullable captain endpoint links in a new migration after the endpoint
  migration for each provider. Captain admission checks tenant, private-owner,
  inference-kind, enabled-state and model compatibility. Provider foreign keys
  reject endpoint deletion when a captain link races the service check.

### Self-deploy safety gate

- Require validated backup, isolated restore and candidate proof before a
  supervised restart. Missing or conflicting proof fails closed and records
  an incident. The default provider refuses cutover until implemented.
- Exclude raw provider exception text from persisted failures.

### Manual mission completion safety

- Route manual Complete transitions through immutable ancestry, Check, Judge,
  and captain-process ownership proof before landing or status mutation. Read
  all scoped Checks across pages, preserve intermediate pipeline handoffs, and
  keep failed or pending REST Check results blocking.
- WebSocket `transition_mission_status` and MCP `armada_transition_mission_status`
  now use the same operator transition path as the REST status route, so a
  manual Complete meets the same review, Judge, Check, process and ancestry gates
  on every entry point. A refusal returns its named reason and leaves the
  mission unchanged; these surfaces previously marked unproven work Complete.

### Ask MCP launch and discovery

- Configure each temporary chat runtime with its Armada MCP connection. Preserve
  provider configuration and other MCP entries in the OpenCode overlay.
- Check the planned Ask endpoint separately from an active mission runtime, and
  distinguish unavailable tools from unverified connection state in the dashboard.

### Advisory landing setting

- Vessel forms, details, and the shared landing preview identify the passing
  Check preference as advisory. Actual Check and landing gates are unchanged.
- The preview response model documentation states the same limit for API clients.

### Generic crash-loop protection

- Repeated distinct runtime crashes use the existing quarantine service.
  Atomic writes preserve active work and stronger holds. Bounded counters
  exclude provider failures and retain concurrent crash evidence.
- Removed the unused CaptainHealthMonitor. Actual quarantine recovery stays
  with the existing service.

### Vessel branch inspection

- Added scoped, read-only vessel branch inspection with tip metadata and
  divergence counts. Repository and HEAD failures return explicit errors.
  Inspection preserves refs and uses persisted vessel paths.

### Typed client route coverage

- Added list and enumeration methods for checks, deployments, environments,
  profiles, releases and skills, plus objective ordering, refinement-session
  lists, release pull requests, jobs and filtered token records.

- Added typed methods for workflow and project profiles, skills, Ask, objectives,
  and refinement sessions. Both objective and backlog refinement aliases remain
  available, with route, query, request, and response contract tests.
- Added typed `ArmadaApiClient` methods for existing fork routes covering
  readiness, landing previews, environments, deployments, releases, checks,
  token summaries, mission pull requests, and jobs.
- Added route contract coverage for every new wrapper, including request JSON,
  typed responses, encoded IDs, server error details, and cancellation.

### Skipped migration versions refuse startup

- Every provider driver reads its applied schema version through one shared
  ledger rule. If `schema_migrations` records a version above a known
  migration that has no row, startup stops with
  `SkippedMigrationVersionsException` (`skipped_migration_versions:`), naming
  the provider, the applied maximum and every missing version. Before this,
  a migration numbered below an applied version never ran and nothing was
  logged. Retired version numbers absent from the code's list, and ledger
  versions the code does not know, are not gaps.
- The refusal happens before any migration, schema guard or prerequisite step,
  and records nothing, so a restart refuses the same way. Existing migration
  bodies and history are unchanged.
- The PostgreSQL `postgres-legacy` scenario reads its historical timestamp
  columns from the operational repair contract and converts only those. It
  proves each is TEXT before repair and timestamptz after startup, and that
  timestamp columns added by later migrations are never touched. It no longer
  pins a column count, and no longer converts later columns that no historical
  database stored as text.
- The database runner adds the `skipped-version` scenario for all four
  providers. Two unit tests that deleted a ledger row to fake an upgrade now
  stop the real upgrade before that version instead.

### Database test invocation documentation

- The testing guide now states that the database runner requires `--type`.
  Omitting it prints usage and exits with code 2; it does not run SQLite tests.
  The guide also distinguishes console runners from the shared NUnit/xUnit
  adapters.

### Coordination board requires a global administrator

- Every `/api/v1/coordination` route (rooms, messages, presence, claims and
  participants) now requires a global administrator. Rooms are found by key
  alone, so the board is shared by every tenant. Before, any authenticated user
  read and posted to every room, and creating a room returned another tenant's
  room with the same key. The dashboard board page and `armada board` use
  administrator credentials and are unaffected.

### Inbox, Ask and captain chat respect caller scope

- `GET /api/v1/inbox` and `POST /api/v1/ask` now require a global
  administrator. Both answer from fleet-wide missions, captains, merges and
  incidents that carry no tenant or user scope. Before, any authenticated user
  read that state for every tenant.
- `POST /api/v1/captains/{id}/chat` finds the captain inside the caller's
  scope and returns `404` for another tenant's captain before the captain's
  runtime starts. Before, a tenant administrator could chat with, and run the
  model of, a captain in another tenant.

### WebSocket events reach only the sessions that may read them

- Any authenticated WebSocket session may now subscribe. Before, subscribe
  required a global administrator because every broadcast went to every
  subscriber.
- Each broadcast carries a delivery scope taken from the record it describes:
  missions, voyages, captains, check runs, objectives, deployments, incidents,
  runbook executions, planning and refinement sessions, and events the Admiral
  writes. It reaches the owning user, the owning tenant's administrators and
  global administrators. An event with no known owner reaches global
  administrators only.
- Mission status transitions made through the shared transition path, from
  REST, WebSocket `transition_mission_status` or MCP, broadcast with the
  mission owner's scope. Other events caused by a WebSocket command still reach
  global administrators only. A refused transition is a reply to the requesting
  session alone and changes nothing.
- MCP `armada_transition_mission_status` runs as the authenticated caller. A
  mission the caller may not read is reported as not found and is not changed.
- Replayed and catch-up events obey the same scope as live ones.
- The fleet status and reconciliation snapshot go to global administrators
  only; other sessions receive a scoped snapshot and reload through REST.
- Ask chat chunk, tool and thinking events reach only the caller who started
  the turn and global administrators. Before, every subscriber received every
  captain chat turn.

### MCP requests authenticate

- Every MCP HTTP request now authenticates through the REST authentication
  service. A missing or invalid credential gets `401` before any tool runs.
  Before, the endpoint accepted every request and ran each tool with a fixed
  default tenant-administrator context.
- Tools read the authenticated caller of their own request, and creates record
  that caller's tenant and user. The default context helper is removed; a tool
  call without a caller fails instead of borrowing administrator authority.
- A global administrator lists and calls the whole catalog. Any narrower role
  lists and calls only the caller-scoped persona, pipeline, prompt template and
  memory tools.
- `armada_reconcile_terminal_voyage_missions` also refuses any caller other than
  a global administrator itself, for a dry run as well as an apply, because the
  repair reads and rewrites missions in every tenant.
- Captain processes receive a per-start launch credential in their environment.
  Claude Code, Gemini, Cursor, OpenCode and Codex configurations reference it by
  variable name, so no credential is written to a dock or scoped config file.
  Mux has no header support, so a Mux captain cannot reach the endpoint.
- With dock MCP delivery enabled, every captain launch carries the credential,
  including Cursor, Gemini and OpenCode captains that read only their dock
  configuration. A subscription-account login switch leaves the credential and
  its Codex reference in place.
- `armada mcp install` writes Claude Code, Cursor, Gemini CLI and OpenCode
  entries with an `X-Api-Key` header that references `ARMADA_API_KEY` by name in
  each client's syntax, so an installed client authenticates once that variable
  is set. Before, the entries carried no credential and every request got `401`.
  Mux has no header field and still cannot authenticate.
- The SSH stdio bridge requires `ARMADA_MCP_AUTH_HEADER_FILE`, a protected
  server-side file holding the credential header, so the key never appears in a
  command line. `armada mcp stdio` sets an explicit local operator identity.
- An objective-linked voyage dispatch must carry its caller. REST, MCP,
  planning and remote-control dispatch pass one; a dispatch without a caller is
  refused instead of reading the objective as a default administrator.

### Persona, pipeline and prompt template reads respect ownership

- Personas, pipelines and prompt templates now store an owning user and an
  ownership scope (`TenantWide` or `UserSpecific`) on all four database
  providers. Records that existed before are tenant-wide. Ownership scope is
  separate from the applicability scope of workflow and project profiles.
- List, enumerate and name lookups follow one shared ownership rule, the same
  rule native memory uses. A global administrator reads everything, nobody else
  crosses a tenant, and a user-specific record is visible only to its owner and
  tenant administrators. Built-in records stay readable to every caller, and
  totals count only visible records. Before, any authenticated user could list
  and read every tenant's records.
- Dispatch, dispatch preview, persona resolution, test ownership and objective
  pipeline links use a record only when the owner of the vessel or mission may
  use it. A refused reference is logged and kept, not cleared as missing.
  Before, a vessel default could put another user's private pipeline into a
  dispatch.
- Mission prompt resolution never uses a user-specific prompt template. Before,
  a private template of the same name replaced the shared template in every
  mission prompt.
- MCP persona, pipeline and prompt template reads and the persona, pipeline and
  template entries of `armada_enumerate` apply the same rule, and MCP creates
  record their owner.

### Persona, pipeline and prompt template writes respect ownership

- Persona and pipeline create, update and delete now require a tenant
  administrator. Update and delete find the record inside the caller's tenant;
  a global administrator still reaches every tenant. Before, any authenticated
  user could update or delete another tenant's record by name.
- Persona and pipeline create records the caller's tenant and never a built-in
  flag from the request body. Before, the body's `TenantId` and `IsBuiltIn`
  were stored as sent, so a request could plant a record in another tenant or
  make it undeletable.
- Prompt template create, update and reset now require a global administrator,
  because templates are shared by every tenant and are found by name alone.
  Before, any authenticated user could rewrite or reset a shared template.

### WebSocket sessions authenticate before they read or change state

- The `/ws` hub now requires an `authenticate` message (bearer or session
  `token`, or `apiKey`) before any other route. Before, any client that reached
  the admiral port received the status snapshot and every broadcast, and could
  run `stop_server`, `restore` and every create, update and delete command
  without credentials.
- Invalid credentials receive `auth.failed`, and any other route before
  authentication receives `auth.required`; the server then closes the session.
- A session that does not authenticate within 15 seconds of connecting
  receives `auth.required` and is closed. Before, a client could open `/ws`,
  send nothing, and hold the socket until it disconnected.
- WebSocket commands require a global administrator, because the command
  handler applies no tenant or user scope.
- `subscribe` also requires a global administrator, because the status snapshot
  and broadcast events are not yet filtered by tenant or user. A session with
  narrower rights receives `subscribe.forbidden` and keeps its connection, so
  the dashboard does not reconnect in a loop; its pages refresh through REST.
- The dashboard authenticates with its session token. `watch-armada.mjs` reads
  `ARMADA_API_KEY` or `ARMADA_TOKEN`, and exits with a hint when the hub
  refuses it instead of reconnecting.

### Vessel updates keep what the form does not edit

- `PUT /api/v1/vessels/{id}` keeps the stored tenant, user, creation time and
  auto-land calibration count. Before, an update
  wrote them from the body, so a normal save reset the tenant and user to null.
- Vessel create and update accept the `autoLandPredicate` object under a key
  in any letter case. Before, the route bound the body to the vessel model
  before the handler ran, so a PascalCase key returned `400`.
- Dashboard vessel saves send the vessel as loaded with the form changes
  applied, so protected paths, sibling repositories, default playbooks and
  thresholds survive a save. Auto-land rules are edited as the vessel's
  `autoLandPredicate`. The copied flat auto-land fields the server never
  stored were removed; before, each save cleared the stored predicate.
- Workspace context saves use `PATCH /api/v1/vessels/{id}/context`. Before,
  they sent a PUT with only the context fields, which reset the rest of the
  vessel.

### Dashboard controls without a server endpoint

- The event detail page loads its record through the new
  `GET /api/v1/events/{id}`, scoped like the other event routes. Before, it
  called a route the server did not have and could not load.
- The Voyages "Voyage Status" view reads the voyage mission summary, and
  merge queue "Process" and "Cancel" call the existing process and cancel
  routes. Before, all three called routes that did not exist.
- Captain "Recall", "Restart Server", and the Planning "Stream responses"
  and "Show thinking" toggles are removed. The server had no recall or restart
  route, and planning messages ignored the stream and thinking options.
- A dashboard test compares every API client call with the server's route
  registrations, so a call to a missing route fails the build's tests.

### Framework selection in POSIX scripts

- `publish-server.sh`, `install-mcp.sh`, `remove-mcp.sh` and `update.sh`
  accept `-f <framework>`, `--framework <framework>`, a leading bare `net*`
  value, or `ARMADA_TARGET_FRAMEWORK`, and default to `net10.0`, as the
  Windows scripts already did. Before, they always used `net10.0`. Only a
  leading `net*` token is read as a bare framework, and `publish-server.sh`
  still forwards the remaining arguments to `deploy-dashboard.sh`.

### Telemetry log export

- When telemetry is enabled with a Loki or OTLP endpoint, the Admiral log
  stream is exported at Information level and above. Before, the Loki exporter
  was configured but received no Admiral log entries.
- Exported text passes through the shared secret redactor, includes exception
  text with its stack, and is kept to 16,000 characters; the exception object
  is not exported. Forwarding failures are counted, forwarding cannot recurse,
  and a restart exports each entry once. Telemetry stays disabled by default.

### Chat and planning tool activity

- Ask Armada and Planning keep runtime activity records out of the captain's
  answer. Before, an OpenCode, Codex, Cursor or Gemini captain's reply and the
  stored planning message included lines such as
  `[ARMADA:ACTIVITY] tool read src/File.cs (ok)` before the real answer.
- A tool activity record now becomes a tool card: Ask Armada sends it as an
  `ask.tool` event and Planning as a `planning-session.tool` event, with the
  tool name, its redacted primary argument and whether it succeeded. A started
  call and its completion update one card. Other activity records are dropped
  from chat and planning text.
- Mission output and chat and planning share one activity-record rule. A tool
  card reported already completed keeps its argument.

### Definition of Done stored-record validation

- The mission Definition of Done report reads a stored evaluation record as
  Unavailable, with a named reason, when it combines an outcome with fields
  the writer never produces (a pass with failure details, a skip without a
  reason, a failure without a command label), has a negative recovery attempt
  count, or is larger than 262,144 characters. The size is checked before the
  record is parsed. Before, such records were reported as real results.
- The skipped reason and command label are redacted and kept to 1,000
  characters when a record is written and when it is read. Before, only the
  failure output was redacted and bounded. Identifiers are never truncated.
- A reversed start and completion time is still reported, because a clock
  step can produce it on a real evaluation.

### Landing preview Check wording

- The landing preview adds a `latest_check_not_passed` warning when the newest
  check run in its scope did not pass but an older run did. Before, the older
  pass hid the newer failure and the preview reported no issue. Readiness is
  unchanged; the warning does not add a gate.
- The required-checks and no-passing-checks messages state that they come from
  the preview scope. Before, the message said checks were required "before
  landing may proceed", although no landing path reads the vessel setting.

### Captain chat tool activity

- Ask Armada and the Planning chat show a one-line result preview on each
  finished tool card, and the turn statistics add the number of completed tool
  calls and the time summed across them. Ask Armada labels tool cards with the
  selected captain's runtime.
- The chat transcript follows new content while the reader is at the bottom,
  including tool cards that grow without new reply text. Scrolling up stops
  following; a new turn resumes it. Before, only reply text changes were
  followed, so tool activity could grow out of view.

### Vessel auto-land and landing mode display

- Vessel detail shows the configured auto-land rules from the stored
  predicate: on with its file, added-line and path limits, off with the rules
  it keeps, not configured, or a stored predicate that cannot be parsed.
  Per-vessel Definition of Done controls were not imported; Definition of Done
  stays a global setting.
- The vessel form, filter and table describe each landing mode with the
  upstream labels and short descriptions.

### Captain assignment for inherited pipelines

- A voyage captain assignment with persona `*` (or an empty persona) now applies
  to every step that has no exact persona assignment; an exact assignment still
  wins. Mission captain resolution and the objective dispatch preview use the
  same rule. Before, both matched persona names exactly, so a wildcard
  assignment was stored and ignored.
- Dispatch with an inherited pipeline offers one "All steps" captain assignment
  row that sends the `*` persona.

### Incident recovery detail

- An incident that names a mission shows that mission's recovery report from
  `GET /api/v1/missions/{id}/recovery`: recovery attempts against the budget,
  an exhausted budget, landing retries, the failure reason, and each rescue
  mission as a link with its status and commit. Truncated sections and
  unavailable reasons are stated, and a load failure is shown instead of an
  empty report. Upstream's per-incident failure kind and rescue count were not
  imported; the mission recovery report is the richer source.

### Dock starting point

- Dock detail shows the Git evidence captured at provisioning: the provisioned
  commit, target branch and tip, the snapshot state (Seeded, Complete or
  Incomplete), recent commits on the mission's paths, paths not on the
  provisioned revision, external source trees, and prior-art search results.
  An incomplete snapshot shows its sanitized error code, truncation and
  resolution error; a dock without a snapshot says so. The snapshot is context
  evidence, not a landing gate.

### Mission modes in the dashboard

- The mission create form offers Implementation, Audit (read-only) and Research
  (read-only), and mission detail shows the stored mode. The server already
  stores the mode sent on create; an API case now pins that contract.

### Mission markdown

- Mission detail renders the mission description as GitHub-flavored markdown,
  with a "Copy raw markdown" button that copies the stored source.
- The shared log viewer takes a `markdown` option; the mission instructions view
  uses it. Copy still copies the raw text. The mission log stays plain text,
  because runtime logs are event lines, not markdown. Raw HTML in markdown is
  not rendered as elements.

### Readable captain logs

- Captain detail reads its log with `formatted=true` and shows typed entries by
  default. Thinking, tool call, tool result and status entries carry a label, so
  they stay distinct from answer text; redacted and truncated entries carry a
  chip, dropped noise is hidden, and a page cut at the server limit says so.
  "Show Raw" returns the redacted raw text view. Kinds are runtime
  observations, not mission outcomes.
- The mission log viewer uses the same readable entries by default, keeps
  follow mode and line-count selection in both views, and copies the raw text.

### Bounded dashboard home

- The home page reads the 10 newest mission summaries instead of 200 full
  missions, and reads each active voyage's vessels from
  `GET /api/v1/voyages/{id}/mission-summary`. Before, a voyage whose missions
  were older than the recent slice showed no vessel.
- Home refresh uses one path: WebSocket events, the timer and the refresh button
  fold into at most one follow-up load while a load runs. Before, every event
  started another full reload. The timer is now the shared auto-refresh
  selector (default 30 seconds, "None" stops it). A mission's full JSON is read
  only when opened.
- `GET /api/v1/status` adds `MissionsWaitingForResourcePressure`: the current
  number of pending missions whose stored assignment state is
  `WaitingForResourcePressure`. The home Active Voyages card shows it when above
  zero. It is not a count of refused admission checks and does not reset on
  restart.

### Events carry their owner's scope

- The generic admiral and server event helpers, the architect over-cap event
  and papercut events now write the owner's tenant and user, so tenant admins
  and members see them in `GET /api/v1/events` and scoped reports. Before, those
  events had no tenant or user and only an unscoped administrator saw them.
- One shared rule (`EventOwnerScope`) resolves the owner from the mission, then
  voyage, vessel, captain and the event's entity. Copied tenant and user
  assignments in the landing handler, recovery orchestrator, landing retry and
  lifecycle events were replaced by it. Event types, messages and broadcast
  payloads are unchanged; earlier events are not backfilled.

### Mission auto-land detail

- `GET /api/v1/missions/{id}/auto-land` returns the vessel's current auto-land
  predicate, the latest recorded auto-land decision (outcome, merge entry,
  redacted reason, predicate at decision time) and the latest merge entry audit
  fields in the caller's scope. It evaluates no predicate and reads no diff; a
  malformed or tied latest decision reads `Unavailable`. The DoD report now uses
  the same shared history state, and its DoD-specific enum was removed.
- Scoped merge entry reads on SQLite and SQL Server (tenant, and tenant plus
  user) now apply the mission, vessel and status filters. Before, a tenant admin
  or member listing one mission's merge entries also got other missions' entries.

### Shared, ownership-safe captain quarantine

- Manual quarantine and release use one scoped service for REST, MCP and the
  dashboard. New `POST /api/v1/captains/{id}/quarantine` takes a required
  reason and an optional expiry or duration; `unquarantine` now goes through the
  same service and returns a typed `CaptainQuarantineResult` (Quarantined,
  Released, NotQuarantined, Busy, NotFound, InvalidRequest).
- The hold is a conditional database write on all four providers: it applies
  only while the captain is Idle or already held and owns no mission, dock or
  process. Before, a manual bench cleared a running captain's mission, dock and
  process, and a release forced any captain, including a Working one, to Idle.
  The REST release no longer writes captain state directly, and the id-only
  `BenchAsync`/`UnbenchAsync` and whole-row `ClearQuarantineAsync` service
  methods were removed in favor of the scoped API.
- The expiry sweep and quota probe release only a hold that is still timed, so
  an indefinite operator hold placed while a probe runs is no longer cleared
  from the sweep's earlier read.
- A provider spend cap no longer strips work from other captains on the capped
  model: the failing captain and idle siblings are held, and a busy sibling keeps
  its mission, dock and process until its own run returns the cap.
- The captains list shows a quarantined captain as a stalled tag with its
  release time and the hold reason as the tag's tooltip, and offers Quarantine
  and Lift Quarantine; Captain Detail adds Quarantine. Both use one dialog (reason
  plus a duration, an expiry, or until released) and report the server's typed
  outcome, including Busy and NotQuarantined.

### Mission recovery detail

- `GET /api/v1/missions/{id}/recovery` returns recorded recovery counters
  against the current rescue budget, landing retries, rescue missions, linked
  incidents with their runbook executions, and recent recovery events, in the
  caller's scope. It reuses the scoped incident and runbook services, redacts
  and bounds free text, dispatches nothing, and never reads a rescue's Complete
  status as a landing.

### Landing records carry the mission owner's scope

- The landing retry event (`mission.landing_retry`) now carries the mission's
  tenant and user, so the mission owner's scoped reads and the recovery report
  include it. A failed retry-event write is logged instead of silently
  discarded; retry authorization and landing gates are unchanged.

- The landing handler now writes the mission's tenant and user on the merge
  entry it enqueues and on all of its landing events (pull request open,
  already integrated, auto-land triggered and skipped, enqueued, completed,
  no commits, landing failed, branch cleanup failed). The landing-drain safety
  net also records the merge entry user and scopes its events. Merge-queue
  processing events (target advanced on failure, branch cleanup failed, target
  ref sync skipped, working directory synced or skipped, bare HEAD restored or
  skipped) now carry the entry user as well as its tenant. Before this, a
  scoped (non-admin) read of the mission's own merge entry or landing events
  returned nothing. A merge entry carries a tenant only when the mission and
  vessel share it, because queue processing reads both the vessel and the
  mission in the entry's tenant; otherwise the entry stays unscoped as before.
  The landing-drain safety net follows the same rule (it previously used the
  mission tenant, then the vessel tenant). Deleting a user or tenant now also
  deletes the merge entries attributed to it. Records written before this
  change are not backfilled and remain visible only to unscoped administrators.

### Definition-of-done evaluation history

- Mission completion records each definition-of-done result as a scoped
  `mission.definition_of_done_evaluated` event with a typed, versioned
  payload: Passed, Skipped, NotVerifiable, Failed or EvaluationError, with
  captain, dock, branch, known commit, attempt count, times, and redacted,
  bounded failure output. A failed event write is logged and does not change
  the gate outcome or the mission decision.
- `GET /api/v1/missions/{id}/definition-of-done` returns the configuration of
  the gate mission completion actually runs, or reports that no gate is active
  (a settings reload does not replace a gate built at startup), as the gate
  resolves it (enablement, persona and doc-only
  rules, selected workflow profile and scope, command presence without command
  text, consumer settings) and the latest recorded evaluation in the caller's
  scope. Reading it runs no gate and no diff. No record reads `NotRecorded`; a
  malformed, unknown-version or same-timestamp latest record reads
  `Unavailable` and never falls back to an older result.

### Skipped definition-of-done results

- The mission activity log records a skipped definition-of-done gate
  (disabled, persona not applied, doc-only marker) as `validation skipped`
  with its reason. It no longer writes `validation passed` for a gate that ran
  no build or test. Completion policy is unchanged: a skipped gate still
  accepts the work.

### Effective landing previews

- Landing previews share the handler's voyage, vessel, global and legacy
  configuration resolution. Responses expose the selected source and flags.
  Unreadable voyage overrides report unavailable configuration. Check summaries
  are redacted and bounded; Failed status with exit code zero is not Passed.
  Existing execution gates retain authority.

### Recorder stage in the ProductDevelopment pipeline

- The built-in `ProductDevelopment` pipeline now ends with a `Recorder` stage, so
  finished product work distils its durable findings into native captain memory.
  The stage runs at the `mid` tier so it never competes for the scarce high-tier
  specialist and Judge captains, and it produces no commit by design.
- The no-op completion gate and the ineffective-rescue gate now exempt the
  `Recorder` persona the same way they exempt the `Architect`: a persona whose
  deliverable is not a repository diff is not read as a false-complete. The
  `Recorder` also runs detached, like the reviewer personas, because it reads the
  finished work and commits nothing. Only the `ProductDevelopment` pipeline gains
  the stage; no other built-in pipeline changes.

### Consumer test gate on behavior-breaking waves

- The definition-of-done gate now RUNS a declared consumer's unit-test suite,
  not only its build, when the producer change can break the consumer's
  behavior. "Can break" is decided from the producer diff against its default
  branch: the consumer suite runs when a changed non-test file falls under a
  triggering path prefix, taken from the producer's sibling declaration
  (`ConsumerTestTriggerPaths`) or the `DefinitionOfDone.ConsumerTestTriggerPaths`
  default. A change to tests, docs, or outside every prefix builds the consumer
  but runs no suite, so the producer pays for the consumer suite only on the
  changes that matter. A failing consumer suite fails the gate with the named
  reason `consumer_tests_failed: <consumer>`, distinct from a consumer build
  failure. `DefinitionOfDone.RunConsumerTests` (default true) switches it off.

### Recorded mission admission

- Persist the actual global workload and resource-pressure decision in mission
  details and summaries. Reads do not run admission checks. Revision checks
  reject stale writes; a refusal and its waiting state are written together.
  Preserve Unicode IDs, existing policy limits and process ownership.

### Captain brief AI-Memory repository folder

- Match a vessel to its folder under the AI-Memory `repos/` directory by
  reducing both the vessel name and each folder name to lower-case letters and
  digits, and use the folder's real name. A folder whose name carries
  separators (`some-vessel` for vessel `SomeVessel`) now reaches the brief and
  the deferred-facts lookup instead of silently resolving to nothing.
- When two or more folders reduce to the same name, choose none and state the
  ambiguity in the brief.
- When no folder resolves, the brief says why, and the admiral logs the reason
  once per vessel per process (Info for no matching folder, Warn for an
  ambiguous match or a memory root that cannot be probed). An unreadable root
  still does not fail the dispatch.

### Native captain memory: store

- Added a native captain memory store: a `Memory` record (`mem_` prefix) with a type
  (Episodic, Semantic, Procedural), an optional topic, an optional stable key, a one-line
  summary, content, a salience used to order recall, a version counter, provenance
  (source kind plus voyage, mission and vessel identifiers), a vessel association, and tags.
- Records are owned by a tenant and a user and carry a scope (tenant-wide or user-specific).
  Storage reads, updates and deletes are fenced by tenant, and a key is unique inside a
  tenant, so two writers cannot create the same key at once.
- An update applies only at the version the caller read, so a concurrent change is refused
  instead of overwriting the other writer.
- New migrations add the `memories` and `memory_tags` tables: SQLite 86, PostgreSQL 87,
  MySQL 78 and SQL Server 81. No existing table changes shape.
- Native memory holds captain working memory. An external durable memory rule delivered in
  a mission brief still wins over a native record on conflict.

### Native captain memory: service, MCP tools and REST API

- Added `MemoryService`: caller-scoped create, read, update, delete and search. A caller
  sees the tenant-wide records of its own tenant plus its own user-specific records, and
  never a record of another tenant. Only a tenant or global administrator changes a
  tenant-wide record.
- Recall orders by salience, then by recency, and filters by type, topic, vessel, voyage,
  mission, creation date and free text over content, summary, topic, key and tags.
- A write that names a key updates the record holding that key. A key held by a record the
  caller may not change is refused as a conflict, and the refusal does not disclose that
  record. A write may carry the version it read, so a concurrent change is refused instead
  of overwritten.
- Validation keeps a record a distilled finding: content is required and bounded, and keys
  and tags are lowercase slugs, so one record is addressable by the same string on every
  database provider.
- New MCP tools `search_memory`, `get_memory`, `create_memory`, `update_memory` and
  `delete_memory`, plus a `memories` entity type on `armada_enumerate`. The MCP surface
  carries no per-request identity, so the tools act as an administrator of the default
  tenant and reach no other tenant. A refusal returns a code an agent can act on: invalid,
  not_found, forbidden or conflict.
- New REST API under `/api/v1/memories`: list and search, create or update by key, read,
  change and delete, with 409 on a conflict.

### Native captain memory: the Recorder persona and memory recall

- Corrected the MCP catalog size in the operator guide and removed the stale tool count from
  the MCP guide. The guide now states how the number is counted, so it can be re-measured.

- Added the built-in `Recorder` persona with its own editable prompt. It reviews the
  finished work of a voyage, classifies what is worth keeping, reconciles it against what
  is already recorded, and writes it to native memory. It writes memory only: it changes
  no repository file, no vessel model context, and no shared external memory repository,
  and it hands anything that belongs in shared memory to the operator as a proposal.
- Added the built-in `Recorded` pipeline: Worker, then Recorder. It is seeded only when no
  pipeline of that name exists, so an operator pipeline with that name is kept as it is.
- No existing pipeline gains a Recorder stage. Where the Recorder belongs in a pipeline is
  an owner decision, so startup changes no other pipeline.
- Every other built-in persona template now carries a Recall Existing Memory section that
  tells the agent to read the vessel model context and search memory before acting, to
  treat a record as working memory rather than proof, and to let a rule from the brief's
  Shared Memory section win over a memory record on conflict. Startup adds that section once
  to a built-in persona template that lacks it and changes nothing else, so an operator edit
  is kept. The Recorder does not receive it.

### Judge Check gate on queued armed Checks

- Treat an armed, not-yet-run voyage Check as queued work once the voyage has a
  commit under review. The Judge gate holds the PASS for it and the voyage
  completion gate holds completion, instead of reading it as "no green
  independent Checks attached" and rejecting a valid PASS.
- Stamp such a Check with the branch and commit the Judge reviewed before the
  gate classifies it, so it measures the reviewed tip even when no stage in the
  voyage committed anything new.
- A failed Check at the reviewed commit still rejects a PASS, a green for
  another commit still does not count, and fully report-only (Audit or
  Research) voyages still need no code Checks. One rule in `CheckRunGateRules`
  decides this for both gates.

### Landing past a worktree that holds the target branch

- Create the LocalMerge integration worktree detached at the target tip, so a
  worktree elsewhere that has the target branch checked out no longer blocks
  every landing for the vessel.
- Advance the target branch by a compare-and-swap from the tip the merge
  started at. A target that moved during the merge is reported as drift and
  retried, never overwritten. Push modes push the merged commit to the named
  target branch.
- Report a git refusal with its own words and a failure class:
  `worktree_conflict:` names the blocking worktree path, and other refusals
  read `integration_merge_failed:` with git's message, instead of the generic
  "Integration worktree merge failed".

### Historical PostgreSQL upgrade repair

- Convert the known operational text timestamps, integer duration and approval
  fields, and captain foreign-key delete rule before prerequisite validation.
- Add `--validate-database` to apply and check schema changes without starting
  Admiral services or dispatch. Use an isolated restore for deployment preflight.
- Reject ambiguous or incompatible source data and roll back the complete repair.
  Keep applied migration history and the original prerequisite checksum intact.

### Resource admission evidence

- Capture typed pressure reasons, evaluation time, limits and the OOM deadline
  in the policy decision without running the policy again. Persistence follows
  separately after the historical PostgreSQL upgrade repair.

### Admission refusal handling

- Honor a refused resource-pressure decision even when its explanation is empty.
  Keep the mission waiting before captain selection and use a fallback message.

### Readable runtime log responses

- Add typed text, thinking and tool entries to formatted captain and mission logs,
  with explicit display limits and omission flags.
- Keep legacy text responses and the shared redaction entry point. Protect tool
  names, raw REST/MCP log output and escaped JSON credential forms.

### Durable dock Git anchors

- Record the actual provisioning commit and reuse bounded typed evidence during
  prompt generation. Keep old missing evidence unavailable.
- Pin repository queries to that commit. Preserve ownership during conditional
  completion and clear evidence on dock reuse or reclaim.
- Append provider migrations and preserve PostgreSQL/MySQL dock user ownership.

### Scoped voyage mission summaries

- Add a read endpoint for voyage mission status counts and paged distinct vessel
  IDs. Counts cover all visible missions, including missions outside the vessel
  page, without loading descriptions or captured output.
- Apply authenticated user, tenant administrator or global administrator scope.
  Reject invalid page bounds. Dashboard adoption remains separate work.

### Backend metadata persistence

- Preserve nullable captain tier, mission requested captain and tier, vessel scan
  preferences, and voyage planning provenance across provider reloads.
- Append schema versions without changing applied history. Retain PostgreSQL's
  existing integer scanner column and reject restricted MySQL provenance encoding.
- Add non-default create/update/clear/reopen cases and interrupted migration proof.
  Routing selection, process ownership and active landing protections are preserved.

### Test discovery and provider persistence

- Register 12 existing Unit suites and eight existing API suites. Retain the
  existing executables and reject missing registrations, empty runs, duplicate
  identities and unmatched filters. Record named skips and build source evidence.
- Correct exception assertions that could accept their own missing-exception
  failure. Keep completed results when suite setup or teardown fails.
- Preserve review requirements when expanding pipelines, retain terminal review
  docks until approval, and resolve relative deployment health URLs correctly.
- Isolate proxy assets and Git fixtures; verify request-history cleanup and wait
  for captured records before asserting or deleting them.
- Persist six vessel preview settings on all four providers through new additive
  migrations. Reject incompatible columns, including restricted MySQL text
  encodings. These settings describe previews; existing landing gates still apply.

### Upstream preservation gates

- Repair server startup prerequisites and pending DDL without rewriting applied
  migration history. Add catalog checks, schema locks and MySQL statement recovery.
- Preserve Unicode identifiers and full-value MySQL tenant uniqueness with native
  unique indexes over an internal tenant key and the complete original value.
- Restore missing user-scope read mappings, SQL Server Mission retry exclusions,
  PostgreSQL deployment boolean binding and MySQL native timestamp parameters.
- Add isolated fresh, interrupted, historical-upgrade and concurrent-startup
  fixtures, plus full-value Unicode uniqueness and lease lifecycle tests.
- Make first-boot identity creation atomic, reject incompatible pre-staged
  SQL Server corrections, and prevent concurrent SQLite migration replay.
- Keep tied-time tenant pages stable and restore MySQL captain provider reads.
- Build shared test dependencies in sequence before parallel suite execution.

- Add a fixed four-provider migration manifest and a source check that rejects
  changed history, reused numbers and altered initial SQL inputs.
- The migration source check protects each MySQL and SQL Server `TableQueries`
  member that historical migrations, the initial statement assembly or the
  ledger table reference, and names the member that changed or disappeared. A
  new member with a new migration now passes; before, every addition to the
  MySQL query file failed the check.
- The migration source check also protects statements that historical
  migrations take from shared schema classes, on all four providers, and names
  the changed, missing or renamed member. `--explain` reports whether a changed
  declaration kept identical statement content and changed only its
  description or member names.
- Add repeat-startup and non-default Mission persistence cases to the existing
  database runner. Raise schema checks to the preserved fork baseline.
- Record fresh-install failures for three server providers and unresolved
  field mappings in the [foundation checkpoint](docs/upstream-review/foundation.md).
  The initial failures are retained as baseline evidence; provider repairs are
  described separately. No deployment is claimed.

### Account usage routing

- Preserve omitted vessel and voyage bindings during mission metadata updates; reject explicit rebinding or clearing. API tests now isolate their capacity fixtures and report failed creates directly.
- Add opt-in persona account routes and usage reserves. (Retired: route order no longer replaces Legacy Routing; see the Smart Routing entries above.)
- Add optional account usage collection for Codex, Claude, Cursor, and OpenCode Go, plus file and manual snapshots.
- Add Dashboard policy editing, usage status, budget planning, and an admin draft preview API. Defaults contain no accounts or personal subscription data. See [usage routing](docs/USAGE_ROUTING.md).

### Operator documentation
- Incident mitigation now acknowledges existing failure evidence. Lifecycle
  reconciliation reopens a mitigated incident only for a newer failure.
- Added a fixed-source upstream capability review, full change inventory and
  phased selective-integration plan. Corrected the README comparison and
  captain MCP guidance. The review does not claim that planned ports shipped.
- Autonomous objective sweeps now preflight candidates only as capacity needs
  them. Each pass has candidate and elapsed-time limits, resumes after its last
  examined objective, and stops as soon as fleet capacity is full. Scheduler
  status now reports active, completed, progress, bounded, and error state, and
  concurrent triggers coalesce into one follow-up pass.
- Autonomous recovery no longer lets old, already-handled failures consume the
  ten-item sweep budget. New failures behind a large handled backlog now reach
  their incident, follow-up, or rescue policy in the same sweep.
- Autonomous recovery now rebuilds its review graph from the owning objective's
  selected pipeline. Recovery keeps specialist stages, the reviewed start tip,
  mission mode, review settings, and immutable playbook context, and a repeated
  outcome callback does not create a second graph.
- Missed Judge follow-up captures can now be repaired with the bounded,
  idempotent `armada_backfill_judge_followups` operator tool. It distinguishes
  actionable sections from explicit `(none)` evidence and reports incomplete
  searches. Its parser also accepts anchored legacy `Suggested`, `Recommended`,
  and `Tracked` follow-up labels while rejecting prose mentions. Read-only
  recovery capture now has an injected test seam, preserves
  non-cancellation fault containment, and propagates requested cancellation.
- Objective create and update MCP schemas now expose `suggestedPlaybooks`.
  Scheduler-created voyages can receive the same validated playbook selections
  that the objective model and dispatcher already supported.
- All voyage, mission, recovery, Architect, and restart entry points
  now share one tenant-scoped durable capacity admission lease. The gate counts
  actual work-bearing voyages and standalone missions, resolves transitive
  sibling lanes, returns typed capacity refusals, verifies lease ownership at
  the commit boundary, and cancels partial graphs when creation fails.
- Objective dispatch preparation can now be mandatory. Preview verifies full
  immutable source and target commits, evidence and timestamps for required
  claims, and structured sibling repository and artifact requirements. Runtime
  refinement, MCP schemas, and the Dashboard carry the same preparation shape.
- Terminal failed-voyage missions now enter normal recovery policy instead of
  remaining stranded. Recovery incidents link to objectives that own the
  failed mission or voyage, and rescue checkout priority is produced commit,
  original mission start ref, then same-vessel dependency commit.
- Pipeline dispatch and objective preview now share persona-aware model-tier
  resolution. Non-specialist stages no longer inherit an unassignable `high`
  selector, specialist and Judge stages remain `high`, literal pins stay exact,
  and multi-mission previews evaluate each distinct model requirement.
- Autonomous recovery now preserves Audit and Research scope. It records an
  incident and runbook execution but does not dispatch an Implementation
  rescue. Only a failed Judge creates a durable Judge follow-up; reference
  analysts and test reviewers do not impersonate a Judge. Existing system
  recovery runbooks gain the required mission-mode parameter in place.
- Audit and Research missions now preserve every stage declared by the
  selected pipeline. Reference analysts, test reviewers, and Judges all run
  read-only and pass reports through the configured graph. A fully report-only
  voyage does not arm Build or UnitTest Checks and can accept a no-commit
  report without a Check-exclusion marker. Any Implementation stage keeps the
  normal code and Check gates.
- Monitoring guidance now uses the WebSocket watcher instead of a foreground
  polling loop. It also states that Mail and Nudge reach a Pending downstream
  stage at handoff and cannot change a running stage's frozen brief.
- Production measurement now has one metric contract and a dated first
  baseline. The record separates 127 raw completed leaf slices from a verified
  landed result that is not yet computed, reports coverage and unavailable
  measures, and keeps concurrency unchanged until verified evidence exists.
- A bounded production summary is available through
  `GET /api/v1/production/summary` and `armada_production_summary`. It reports
  verified landing evidence, declared-ready delay, armed-to-start Check delay,
  execution time, rescue share, closeout delay, and explicit unavailable
  states for host-slot delay and other facts that Armada does not yet record.

### Upstream absorb (isolated)
- PostgreSQL and MySQL timestamp reads now tag stored UTC values as UTC without a local-time shift. Npgsql and MySqlConnector return `DateTimeKind.Unspecified`; `ToUniversalTime()` treated that as local time. MySQL also keeps `DATETIME(6)` fractions instead of dropping them through `ToString()`.
- Remaining MySQL delivery/workflow readers (check runs, releases, deployments, request history, workflow profiles) and local `FromIso8601Nullable` helpers use the same UTC-kind path. PostgreSQL and SQL Server token-usage `CreatedUtc` reads no longer go through `ToString()`.
- SQL Server captain delete nulls `signals.from_captain_id` / `to_captain_id` first. SQL Server cannot declare two `ON DELETE SET NULL` FKs to the same parent, so a captain referenced by a signal previously failed to delete.
- Install, update, reinstall, publish-server, MCP, and dashboard-deploy scripts accept `--insecure` / `-k` so npm works behind a TLS-inspecting proxy, and they copy the committed `src/Armada.Dashboard/dist/` when Node.js is not installed.
- Docs: `claude mcp add` plus the enterprise `allowedMcpServers` caveat. Remaining unused-async CS1998 sites that did not already await real work now return `Task.FromResult`. Package bumps: `Microsoft.Data.Sqlite` 10.0.11, `MySqlConnector` 2.6.2, `SyslogLogging` 2.2.1. Helm packs symbols (`snupkg`).
- Mission briefs teach `[ARMADA:TOKENS]` so OpenCode/Cursor runs can report real counts; the parser already existed.
- Planning dispatch releases the reserved captain and dock (server `DispatchAsync` plus the in-place Dashboard Dispatch button). `POST /api/v1/planning-sessions/{id}/stop-turn` aborts an in-flight turn without ending the session.
- Vessel readiness and landing-preview REST routes are wired to the existing evaluators (`GET /api/v1/vessels/{id}/readiness`, `GET /api/v1/vessels/{id}/landing-preview`).
- Dock repair and unstick are exposed on REST and MCP (`armada_repair_dock`, `armada_unstick_dock`). Unstick releases a held captain to Idle and reclaims the worktree.
- Setup-wizard objective container is taller and pins step actions so Register Vessel / Create Captain stay visible.
- Admiral shutdown now kills working agent processes before the token cancel and database dispose, so Helm stop/start does not leave orphan PIDs. Stale-captain cleanup treats a recycled OS PID as dead (`ProcessSupervisor.IsTrackedProcessAlive`).
- Dashboard Chat Stop is an abort, not a timeout. Captain detail can lift quarantine. The captain-tools probe waits 120s. Windows update scripts stop every `Armada.Server` process, including a repo-launched host.

Skipped from upstream (already equal or richer here): captain-map, token-usage charts, dashboard nav, fork-parity cores, Touchstone/test move, Mux install, `POST /api/v1/server/restart`, heartbeat 30s→10s, and the AutoLandPredicate alternate evaluator.

### Routing and settings (code to config)
- Captain routing, specialist-persona reservation, model-family classification, the stage-persona title guard, and the specialist reviewer personas/pipelines/templates are no longer hardcoded. Product defaults are empty and policy-neutral (random within an unconfigured pool, guard off, no family or persona assumption). The former behavior lives in `factory/settings.fleet.example.json` and `Test.Shared.Infrastructure.FleetRoutingSettings`.
- Dashboard Settings now edits tier lists, family rules, within-tier strategy, non-native preference, specialist personas, the dispatch title-prefix guard, model providers, and additional personas/pipelines/templates. `modelTier` and `voyageDispatch` hot-reload; `modelProviders` and additional assets load at startup (the Settings page says so).
- When no tier list and no family rule is configured, `SelectModel` picks randomly among idle persona-eligible captains so a fresh clone still assigns work. Low still maps to mid (platform two-tier architecture).

### Dispatch and model selection
- Long pipeline reports now use a bounded head/tail preview with a durable
  `mission-output:<id>` reference, total length, full UTF-8 SHA-256 digest, and
  explicit completeness state. Authenticated REST and default-tenant MCP readers
  page the redacted persisted output so downstream stages can verify the complete
  safe artifact.
- Start-ref validation and branch creation now verify that the requested revision
  names a commit object in the dock-owning repository and keep its full object
  ID. A raw hexadecimal name for an absent object can no longer pass
  `rev-parse --short` and fail later during branch creation.
- Within a tier, models absent from `withinTierPreferenceOrder` are now chosen at RANDOM among the eligible peers instead of being returned in captain-enumeration order. Previously the ordering helper appended unranked models after the ranked ones and the caller returned the first, so the "random among unranked peers" path was dead whenever any preference order existed - two specialist models (e.g. `gpt-5.6-sol` vs `claude-opus-5`) always resolved to the first-enumerated captain. Ranked models still win by rank (Judges); only genuinely unranked peers are randomized. (`PreferredModelTierSelector.SelectModel`; covered by `SelectModel_High_UnrankedPeers_UseRandomPick_NotEnumerationOrder` and `SelectModel_High_RankedModel_PreferredOverUnranked_RegardlessOfRandom`.)
- Within-tier preference RANK now dominates the native/external captain split for tier selection. `PreferredModelTierSelector.SelectModel` previously split eligible models into external-provider-served vs native and pre-filtered to the external pool BEFORE applying `withinTierPreferenceOrder`, so a lower-ranked external model was chosen over a higher-ranked native one - a high-tier Judge ranked first would lose to a lower-ranked model merely because the latter had an external captain. The ranked search now runs across ALL eligible models (native and external), so the earliest-ranked model with an eligible captain wins regardless of the native/external boundary; the external-first / random tiebreak survives only for genuinely unranked peers (the fix in `39c6d1c3` is preserved). (`PreferredModelTierSelector.SelectModel`; covered by `SelectModel_High_RankedNativeModel_BeatsExternalLowerRankedModel`.)
- The admiral image installs every agent CLI (`claude-code`, `codex`, `gemini-cli`, `cursor-agent-cli`, `opencode-ai`) at `@latest`, with a `CLI_REFRESH` build-arg that busts the install layer so `@latest` actually re-pulls: `docker compose build --build-arg CLI_REFRESH=$(date +%s) armada` refreshes the CLIs, a plain rebuild keeps the last-built versions. `codex@latest` always satisfies the `gpt-6-astra` requirement (>= 0.153.4).

### Objectives and the scheduler
- Objective-linked scheduler, operator, bare REST, and remote-control dispatches now reserve a tenant-scoped durable admission lease before voyage creation. The lease renews while creation and linking run, and the losing request names the active voyage without creating a duplicate. Link failures cancel the new voyage and all active mission rows. The existing link-time check remains a last defense, terminal voyages still permit an intentional successor, and recovery keeps its explicit rescue-link exemption.
- The objective scheduler now has optional persisted campaign fair-share, disabled by default. When enabled, it keeps owner priority bands strict, rotates successful dispatches across roots tagged `campaign:<name>`, preserves rank and ID order inside each campaign and for plain work, and exposes its process-local last-served cursor in scheduler status.
- The objective scheduler now requests a debounced immediate refill when an auto-dispatch objective becomes ready, a dependency objective completes, or a terminal mission or voyage can free a shared lane. Event requests bypass the periodic interval, coalesce burst traffic, wait behind an active sweep, and keep the periodic health-loop sweep as a recovery path. Scheduler status reports the process-local `eventTriggeredSweepCount`.
- Mission assignment now uses one durable admission gate for every dispatch path. It resolves the existing transitive `buildParticipant` sibling lane, reserves each member with database coordination leases, checks actual active mission rows, and persists the winner before it releases the reservation. This prevents concurrent server instances from provisioning two sibling writers. Unlinked operator voyages now count in scheduler occupancy when they contain repository work, and lane derivation is tenant-local and shared by the scheduler and assignment code.
- Objective dispatch now has one read-only preflight for operator and autonomous paths, including linked bare-voyage creation and Helm stdio MCP. It reports the exact target vessel, effective pipeline, configured captain coverage, fallback tiers, required Build and UnitTest commands and inputs, objective and per-mission start refs, preparation refs, sibling inputs, brief completeness, and a complete typed dependency graph with bounded diagnostic chains before Armada creates fleet state. Busy but compatible captains are capacity information, not a readiness failure. REST exposes the same result at `GET /api/v1/objectives/{id}/dispatch-preview` and its backlog alias; MCP exposes `preview_objective_dispatch`. Objective writes reject new dependency cycles, including concurrent opposing writes and cycles through completed rows, with the complete closed path. Scheduler dependency diagnostics now run before capacity, hold, and lane gates instead of hiding blocked rows.
- Operator and autonomous objective dispatch now use one server-authoritative, bounded objective brief. It carries the scope, acceptance criteria, non-goals, refinement summary, rollout constraints, evidence links, verified source and target anchors, reusable types, entry points, inputs, response rules, cleanup, consumer and ledger duties, uncertainty, and owner decisions into each mission. Preparation claims record whether they depend on the source, target, both, or neither; an anchor or execution-target change marks only the affected claims `NeedsRecheck` and keeps the evidence for review. Linked operator missions also inherit the objective start ref when they do not set an explicit ref. Planning sessions and remote-control proxy dispatch preserve the same objective linkage and brief. The Dashboard now supplies only a short editable instruction and lets the server add the durable brief.
- An objective can carry a `StartFromRef` (`startFromRef` on `create_objective` / `update_objective`): the first stage of a voyage dispatched for it is cut from that ref instead of the vessel default branch, so a requeue from a `recover/<name>-<sha>` ref continues accepted work instead of rebuilding it. A ref that does not resolve refuses the dispatch before any voyage row exists (`start_from_ref_missing`, reported as the scheduler skip reason and an `objective_scheduler.start_from_ref_missing` event), and a ref that has gone by assignment time fails the mission by name. There is no fallback to the default branch.
- A requeued objective whose linked voyages have all ended dispatches again instead of sitting in `Dispatched` for ever.
- Objective recovery reconciliation is now failure-chain specific. A terminal original voyage completes the objective only when every independent `Failed` or `LandingFailed` chain root has its own fully landed linked rescue; one rescued branch cannot hide another unresolved branch. Failed historical rescue attempts are evidence, not new objective obligations, so a later successful attempt can close the original chain. Missing linked voyages, malformed mission graphs, unlanded work, and cancelled-only originals fail closed.
- A scheduler pause is attributed to its session (`pausedBy`, `pauseReason`); the autonomy layer may clear only a stale pause whose owner has been absent longer than the configured threshold.
- An engaged dispatch hold is reported as `dispatch_hold`, once per sweep, never as `dispatch_error`; a sweep that dispatches nothing names the constraint with counts (`vessel_count`, `vessel_concurrency`, `objective_skipped`, `no_eligible_objectives`).
- Incident links are annotations: linking an objective to an incident no longer copies the incident's voyage and mission onto the objective, which had silently removed the objective from auto-dispatch.
- Campaign status projects roots the same way as lanes; slices are opt-in and the lane rollup is the contract.
- `armada_add_vessel` / `armada_update_vessel` sibling entries accept `buildParticipant`; an entry that omits it keeps the stored value, as `extractionArtifactPaths` already did.
- Scheduling lanes are derived from sibling declarations: a sibling entry with `buildParticipant: true` (a consumer that builds the sibling through a project reference) joins the two vessels into one lane, and `maxConcurrentVoyagesPerVessel` is applied to the whole lane. A skip is reported as `lane_busy:<vesselA>+<vesselB>` with an `objective_scheduler.skipped_lane_busy` event; a read-only sibling (decompiled or artifact tree) joins no lane. Hand-set blockers were the only serialiser for a cross-vessel wave before.
- Autonomous (scheduler) dispatch derives the mission mode from the objective `Kind`: a `Research` objective dispatches read-only missions (no commit required, an unchanged branch is accepted as success), so a report-only autonomous objective is no longer failed at the Judge for an empty diff. Every other Kind keeps the `Implementation` default.
- Operator dispatch (`armada_dispatch` / REST) now derives the mission mode from a linked objective's `Kind` the same way the scheduler does: a mission that did not state its own mode inherits the objective's read-only mode, so a `Research` objective dispatched through an Implementation pipeline (for example `Tested`) drops the diff-dependent Test Engineer stage and its Judge accepts a sound no-commit report instead of failing the correct empty diff. An explicit per-mission `mode` still wins, and a non-Research objective is unchanged. The scheduler and operator paths call one shared rule (`MissionModes.FromObjectiveKind`), so an objective is judged the same way however it is dispatched.
- Provider branding is neutralized: the per-captain provider-credential dashboard UI and the provider-routing tests no longer name a specific external provider; the labels, comments, and placeholders read as generic provider wording, and the test example uses example.com. Server code was already provider-neutral (the key env var resolves from `ModelProviderSettings.ApiKeyEnv`).
- Provider-brand neutralization continues: the `cun-ai` and `hetzner` example/provider names are removed from source doc-comments, MCP tool descriptions, and tests (generic `example-provider` wording; example domains use `example-provider`/`example.com`). Server behaviour is unchanged — provider resolution was already generic from config; these were naming examples only.

### Checks and the Judge gate
- WebSocket broadcasts now have a process stream ID and monotonic cursor. A reconnect can replay a bounded 128-event, 1 MiB suffix, reports an explicit gap when replay is incomplete, and receives a bounded authoritative fleet or voyage snapshot before live delivery starts. The operator watcher reconciles voyages, missions, captains, and Checks on every connect, detects cursor discontinuities, and can detect a terminal scoped voyage from the snapshot. Existing route-only subscribers remain compatible.
- WebSocket clients now have independent bounded output queues. A slow or failed monitor can no longer block the event producer or delay healthy clients; each client keeps ordered delivery, and overflow causes that client to disconnect explicitly.
- Fleet monitoring now receives `voyageId` on every mission-change payload, reads the captain payload's real `state` field, and reports Check lifecycle changes with voyage filtering. Checks remain `Pending` while they wait for the shared host command slot and become `Running` only when execution starts. The durable `queueDurationMs` projection separates ready/slot delay from command-only `durationMs`; the automatic selection event is now `check.auto_queued`.
- Judge follow-ups are durable mission-linked audit items before merge association. A valid follow-up from any Judge verdict no longer disappears when the reviewed mission has no merge entry yet; the audit drain returns unassociated items, later merge enqueue reconciles them, and verdict recording supports `followUpId` while preserving the existing `entryId` path.
- The automatic Check executor searches stable pages of all Pending Armada Checks until it finds its bounded execution set or reaches the end. An eligible Check older than 200 ineligible records can no longer wait forever, and an interrupted search reports its page and scanned-record count.
- A Judge PASS is held when a green Check measured an older commit than the reviewed tip; the check executor supersedes the stale record (`check.superseded`, Canceled, naming its successor) and arms a fresh one at the tip. A failed Check for an older commit is treated the same way, as stale rather than as a rejection, on Open voyages as well as InProgress ones.
- Rescue voyages ARM their Build and UnitTest Checks instead of running them at rescue dispatch against the default branch, and the scheduler counts rescue voyages toward its ceilings.
- A single deterministic assertion failure classifies as `TestFail`, not `Infra`, even when the output also carries a NuGet warning or the word "dependency".
- Judge rules: accepted work must be an ancestor of the reviewed tip or present in the diff; delivery is proven by the diff under review, not by presence at the tip; a deliverable that lives in a stage's final response is never NOT DELIVERED for being absent from the tree; a citation inaccuracy inside a remark is a Suggested Follow-up, not a NEEDS_REVISION.

### Dispatch, assignment and rescue
- A dispatch whose mission title already carries a stage-persona prefix (`[Worker] `, `[Judge] `, `[Test Engineer] `, `[Architect] `, `[Product Manager] `, ...) is rejected with `mission_title_carries_stage_persona_prefix`. The pipeline prepends the persona itself, so a title that opens with one is a prior run's materialized STAGE mission resubmitted as a task; expanding the pipeline over such descriptions multiplied the work by the stage count (three stage missions became nine, then twenty-seven on the next retry). A real task title never opens with a persona tag; a non-persona bracket tag (for example `[URGENT] `) is unaffected.
- Branch continuation is decided by equality with the dependency's branch name, never by a name merely being present on the row, so a leftover name from an aborted assignment no longer fails a fan-out worker with `stage_base_missing`.
- A planner stage that committed code fails before any fan-out worker is created, instead of every worker failing two stages later.
- A selected captain is reserved in-process before provisioning, so two assignment passes cannot both provision a dock for one idle captain.
- A Worker that failed inside a voyage is rescued through a rescue voyage, never as a standalone mission.
- Rescue briefs: a Judge's narration preamble is dropped before any size budget applies; a Judge report's Suggested Follow-ups and Verdict stay whole in an over-cap brief; a documentation-only rescue under a Research objective is the work, not an `ineffective_rescue`.
- Architect handoff: a front-matter block is titled from its title line, and every spawned brief carries the plan-block-label rule.
- A stale sibling extraction-artifact copy in a shared sibling worktree is refreshed atomically instead of skipped.
- Each dock now owns its directory: the checkout sits at `docks/<Vessel>/<mission>/<Vessel>` and every declared sibling (`../example-sibling`) resolves beside THAT checkout, so a sibling is pinned at provisioning and is never shared with another dock. A dock created after a sibling landing therefore reads the new tip while a running dock keeps the tree its gate started on. The stale-dock sweep, reclaim and the disk-lifecycle orphan scan understand both the nested and the earlier flat shape; a flat leftover at a dock's root is removed before nesting; an empty root is removed on reclaim.
- A dock that reuses a shared sibling worktree behind its branch tip (another dock holds a lease, so the worktree cannot move) logs both commits and emits `dock.sibling_stale`, instead of measuring the older tree silently. The per-dock sibling layout that would remove the condition is an open decision.
- A declared `buildParticipant` sibling (a consumer that builds the sibling through a project reference) that cannot be resolved, throws during provisioning, or comes up as an empty checkout now fails the dock with a `dock.sibling_provision_failed` event, instead of being swallowed into a hollow dock that builds green while missing a required tree. A non-build-participant sibling (a read-only artifact tree whose absence is a tolerated skip boundary) keeps the warn-and-continue path. This closes the "sibling provisioned empty / whole verification stage lost" failure that surfaced as the captain's defect.
- The `mission.git_anchors` brief no longer reports a present nested file as absent. The suffix resolver that finds a file cited by its bare name (`Foo.cs` for `src/<project>/<project>.Core/.../Foo.cs`) used a `:(glob)` pathspec with `git ls-tree`, which does not support glob pathspec magic, so the command threw on every call and the resolver returned "does not exist on this checkout" for every real nested file. It now lists the tracked paths at the revision and matches the suffix in-process, still resolving only when exactly one tracked path matches (an ambiguous bare name stays unresolved).
- A captain actively working through a long tool call is no longer nudged as a `provider_silent_stall`. A parsed progress or `[ARMADA:ACTIVITY]` tool signal now refreshes the provider-progress tracker, which previously only advanced on runtime token-usage updates -- and those go silent while a tool runs, so a Judge running a foreground test suite for ninety seconds looked provider-silent and was mailed a stall nudge mid-work.

### Captain lifecycle
- The handled-exit marker is kept until completion handling (DoD gate, handoff, dock provisioning) finishes. The captain health check re-reads the mission and ignores a vanished PID for a post-work or terminal mission (`captain.process_exit_ignored`) instead of synthesising exit code -1, failing the finished stage and cancelling the Judge with no incident.
- The Cursor runtime passes `--approve-mcps` so the dock's Armada MCP server loads in `--print` runs.
- The one-line AI-Memory pointer is seeded in the runtime's auto-memory folder before launch.

### Storage
- PostgreSQL and SQL Server: token-usage records bind `Estimated` as an integer and `CreatedUtc` as an ISO-8601 string, matching the columns; every insert and every summary read had failed, so the Activity page's Token Usage tab showed nothing. A duplicate token-usage route registration is removed.
- PostgreSQL: the request-history truncation flags are BOOLEAN and the request-history tables are created when absent (migration 81). Every request-history capture had failed with 42804 because the live columns were INTEGER while the model binds a bool, so no request was recorded.

### MCP surface
- Tool-call arguments are normalised against the tool schema once at the transport seam; an empty string for any optional argument is treated as omitted, and string-spelled booleans and numbers are converted.
- `armada_enumerate entityType=checks` withholds each check's command log by default and reports `OutputLength` beside the row; `includeTestOutput=true` returns it whole, and `get_check_run` is unchanged. One 14-row page had measured 26.8 MB on the surface the operator instructions tell operators to prefer.
- The six oversized list tools preview long free-text fields and long primitive arrays, and the default list page size fits a tool result.
- Directed wakes are delivered on any MCP tool result for a session that sends `X-Armada-Participant`; the effective AgentWake participant may acknowledge an unaddressed Wake; long broadcast notes are previewed on a board read while directed mail always arrives whole.
- The inbox lists open incidents and hides failed missions whose voyage has halted.

### Dashboard
- Login: the form uses email autofill semantics, sends bearer and session auth headers, and its layout is repaired; the chat view preserves its reading position.

### Operator watcher
- The operator's blocking poll is replaced by a WebSocket subscription watcher.

### Coordination board (chatroom)
- A shared coordination board keeps concurrent operator sessions on the same page: rooms hold short notes about who is doing what, so a session that reads the board before dispatching no longer mistakes another session's voyage for unowned work or double-dispatches a rescue
- Three entities back it - rooms keyed by a unique slug, messages carrying an author type (Operator / Captain / System) plus optional voyage, mission, vessel, and incident references, and per-room participant presence refreshed by heartbeats - with full SQLite and PostgreSQL implementations and migrations v75/v76; MySQL and SQL Server follow the existing stub convention for planning sessions
- Three MCP tools expose it to operator sessions: `armada_coordination_post` to claim work before starting it and report outcomes, `armada_coordination_read` for recent notes plus active participants, and `armada_coordination_heartbeat` for presence. REST counterparts live under `/api/v1/coordination/`
- The admiral mirrors selected fleet events onto the default room as system notes (`voyage.dispatched`, `voyage.cancelled`, `mission.completed`, `mission.failed`, `mission.cancelled`) through the central event choke point, so new voyages announce themselves without anyone posting manually
- The dashboard gains a `/chatroom` page: room list, presence chips with last-seen times, a chronological stream, a composer, live WebSocket delivery with a polling fallback, and a per-browser heartbeat so an open dashboard shows as present
- Board notes are advisory context only. They never inject into captain briefs; signals remain the handoff-boundary mechanism. Captains can add one-line `[ARMADA:NOTE]` milestones from mission output, and the admiral links those notes to the mission on the board

### Coordination claims
- Sessions can now RESERVE work instead of only posting about it: `armada_coordination_claim` creates a reservation against a vessel or objective with a named holder and an expiry (default 4 hours, clamped 0.5-72). Heartbeats keep a live session's claims alive automatically; a lapsed claim disappears without anyone cleaning up
- A dispatch that overlaps an active claim someone else holds proceeds, but announces the overlap on the board as a system note naming both parties and the claim's expiry - reservations are named and visible, not locks
- The Needs-You inbox gains a StalePeer warning when a session has gone silent for over 15 minutes while still holding an unexpired claim, so an absent peer's reserved work surfaces for adoption instead of quietly blocking everyone
- The dashboard chatroom renders active reservations as amber chips above the message stream - holder, subject, and time-to-expiry - refreshed on board activity so claim and release announcements update the strip immediately
- Claims ride the same SQLite/PostgreSQL implementations as the board (migrations v77/v78) with MySQL/SQL Server stubs; on stubbed backends the conflict check and inbox scan skip gracefully

### Campaign system and captain voice
- Captains get a voice on the board: an `[ARMADA:NOTE] one-line note` marker in agent output becomes a Captain-author board message linked to their mission (20 per mission, credential-redacted through the papercut scrubber). Every mission brief now teaches the channel; the porting playbook asks for milestone notes explicitly. A judge's notes are dropped like its papercuts - it already has the verdict channel
- `armada_campaign_status` answers "where does this campaign stand" in one call: the objective tree under a tag or root objective resolved two levels (hub -> lanes/programs -> slices) with statuses, active claims, and recent board notes
- `armada board` lands in Helm: recent notes, active reservations, and active sessions from the terminal
- Addressed notes now EMIT a Wake signal (`[to=<key>]` payload prefix), so a helper session's next heartbeat or read surfaces a targeted UnreadWakes list instead of requiring a full room re-read - the pause-and-read nudge for sessions inside blocking loops. Acknowledge with armada_mark_signal_read
- An addressed note to the participant key of the registered AgentWake session now also starts that session in `SpawnProcess` or `Both` delivery mode while always retaining the Wake signal row. OpenCode starts a fresh session instead of resuming, so the wake text carries the task and the session reconstructs state from the board and durable memory
- Notes can be addressed to one participant (`toParticipantKey`), turning the board into a work-handoff channel between operator sessions: a new session told only to "join the chatroom" reads broadcast plus its own mail and picks up addressed asks. Voyage-tagged notes now also reach captain briefs - at each stage handoff, notes naming the voyage created since the prior stage started are appended under a dedicated heading; general fleet chatter remains advisory
- A bounded, tested host-helper launcher enforces process caps and timeouts, injects the read-only board/Wake contract, records participant keys, and provides explicit list, kill, and cull operations; the operator guide also prevents one participant key from being owned by both a resident helper and AgentWake
- AgentWake can now retain a stable `participantKey` in settings across Admiral restarts, its status tool reports configured and effective ownership, helper offer mode allows a bounded reassignment window before fallback work, Claude helpers receive the explicit local Armada MCP config required by strict mode, and the autonomy guide defines controlled multi-voyage lane refill instead of treating one global voyage as the normal throughput limit
- The objective scheduler now has a persisted `maxConcurrentVoyagesPerVessel` ceiling (default 1), so a fleet-wide capacity of three can dispatch independent vessels without starting three conflicting voyages on the same vessel; operator-linked voyages count toward both ceilings
- Supported mission captains now receive the local Armada MCP URL by default. Claude strict mode receives an explicit `--mcp-config`, Codex receives a per-process URL override without hiding its authentication, Gemini and Cursor receive their project files, Mux receives an isolated config directory, and OpenCode receives the remote endpoint in its dock config
- The tri-source porting campaign is structured as the first instance of the campaign pattern: hub with `port:jpro` / `port:otr` / `port:dxp` lanes, four strategic programs re-parented beneath, thirteen active items re-parented, completed history left flat. The pattern is opt-in grouping; plain objectives outside campaigns stay the default for ordinary features and fixes

### Dispatch hold
- `armada_dispatch_hold` engages, clears, or inspects a fleet-wide dispatch hold so an operator working on Armada itself can stop new voyages before a rebuild or redeploy. While engaged, every voyage and mission dispatch through the admiral is refused - operator MCP, REST, standalone missions, and the autonomous objective scheduler alike - while in-flight voyages continue
- The refusal names when the hold started, who set it, why, and the exact call that clears it, instead of a generic failure an operator has to decode
- Engaging or clearing posts a system note to the coordination board automatically, so peer sessions learn about the freeze without polling anything
- The guard sits at the admiral's dispatch entries (`DispatchVoyageAsync`, `DispatchVoyageQueuedAsync`, `DispatchMissionAsync`, `DispatchMissionQueuedAsync`), so every current and future caller inherits it rather than re-deriving it
- The hold is runtime state and an admiral restart clears it deliberately: a successful redeploy resumes dispatching on its own, failing open where failing closed would strand a fleet behind a forgotten flag

### Definition-of-done gate
- A passing gate now also builds every vessel that declares the mission's vessel as a sibling repository, so a public-API break is caught while the producer's change is still unlanded instead of surfacing on whatever builds next. The consumer edge is derived from the existing `SiblingRepos` declarations read in reverse, so nothing new has to be configured
- Each consumer is provisioned under a scratch root private to that verification. A shared sibling checkout owned by another dock is reused rather than re-pointed, so verifying through one could compile the consumer against a different commit than the one being reported on
- Consumers are built, not tested: a build catches the break that leaves a target branch red, while running every consumer's suite inside every producer gate would cost more wall time than the gate itself
- A consumer that fails to compile fails the gate; a consumer that cannot be prepared is reported and the gate passes, since a missing profile or repository is a fault in the verification rather than evidence about the change. `DefinitionOfDone.FailOnConsumerVerificationError` reverses that, and `DefinitionOfDone.VerifyDeclaredConsumers` disables the step

### Pipeline
- Manual `Complete` status transitions now use immutable Check and target ancestry proof. An Implementation mission with no active landing dock stays unchanged when its commit is unlanded, its ancestry cannot be verified, or its participating Checks are failed, pending, running, or stale. Review and Judge authority cannot be bypassed. Audit and Research report-only completion remains available.
- A downstream pipeline stage now PROVES its checkout contains the commit its predecessor produced, before a captain is allowed to work in it. Inheriting a branch name is not inheriting its commit: a local ref can predate the upstream stage's push, and the worktree then looks correct while missing the work. One Worker's dock was cut without the preceding stage's commit, rebuilt on a base still carrying errors that stage had already fixed, failed on them, and took ten downstream missions with it - and every symptom pointed at the Worker's own code
- A stage whose checkout demonstrably lacks the upstream commit fails with `stage_base_missing`, naming the commit, the branch, and the upstream mission, and stating that this is a provisioning fault rather than a defect in the stage's work
- A base that cannot be PROVED is not treated as one that was: an unresolvable ancestry probe or an upstream that produced no commit is recorded as unverified and the stage proceeds. Cross-vessel dependencies are exempt, since commits are not shared across repositories
- `IGitService` gained a three-state ancestry probe whose default answer is unknown rather than true, so an implementation that does not consult a real repository cannot report a verification it never performed

### Recovery
- A suitable autonomous rescue now starts from the failed mission's captured HEAD instead of the vessel default branch. It falls back only to an immediate same-vessel dependency commit. A reviewer rescue is blocked when neither commit exists. Armada resolves the ref, then proves the provisioned rescue checkout contains that commit before launch; a missing or unverified base fails as a provisioning fault.
- An autonomous rescue is now judged by what it CHANGED, not by whether it ran. A rescue whose change set is empty, or consists only of documentation, fails with `ineffective_rescue` and the change set named, instead of being accepted because the process stayed alive. The case this addresses ran for twenty-four hours, drew escalating stall nudges, died on a runtime crash, and left one changed documentation file behind - and every liveness measure the platform kept called that a working rescue
- Only rescues are assessed, and only in Implementation mode. A first-attempt mission may legitimately have been dispatched to write documentation, and an Audit or Research mission delivers a report and is never expected to change code - judging those by a diff is the same mistake in the other direction
- The assessment reads changed paths from the diff's `diff --git` headers only, so a hunk body containing a line that looks like a header cannot make a change set describe itself
- It deliberately does NOT compare the rescue's paths against the original mission's: a rescue continues from the preserved failed tip and normally changes the same files, so an overlapping path set would flag the normal case
- The autonomous-rescue marker had two definitions in two files; both now delegate to one, so the rule cannot drift apart

### MCP

- `tools/list` now accepts the protocol-valid request form that omits `params`, which restores tool discovery for clients that use parameterless initial discovery
- Remote-trigger and AgentWake settings now apply through the settings-file watcher, including enable, disable, runtime, participant ownership, delivery mode, and throttle changes, without an Admiral restart
- `run_check`, `retry_check_run`, and `get_check_run` now return a bounded view of a check run - status, exit code, parsed test and coverage totals, artifacts, and the last 40 output lines - instead of the complete command log. A build or test log is routinely one to several megabytes, which overran the tool output limit and returned a truncation error in place of the verdict, forcing a parse step out of band on every call
- The complete log is still available deliberately: `get_check_run` takes `includeOutput=true` for the whole record, and `outputTailLines` to widen the tail. A truncated view reports the full log's size and names the call that fetches it, so nothing is silently withheld
- The tail is whole lines taken from the END of the log, which is where a failure's cause almost always is

### Dispatch
- Dispatch now arms the new voyage's Build and UnitTest Checks itself, so a voyage no longer reaches its Judge stage carrying none. A Judge PASS is rejected without a green independent Check, so a bare voyage was already condemned when it started and nothing said so until the whole pipeline had run. Cancelling a voyage discards its Checks, so a re-dispatch previously started bare again
- Armed Checks are created `Pending`, not executed. They become runnable after a stage commits work, and the executor records the branch and commit before running the check against that exact work, so arming costs nothing at dispatch and never measures the default branch by accident
- A type is armed only when the resolved workflow profile defines its command, and never twice for one voyage - a second Build beside a failed one would leave a green and a red attached, and one failed Check rejects a PASS however many green ones sit beside it
- Arming failures are logged and never fail the dispatch; `VoyageCheckArming.Enabled`, `ArmBuild`, and `ArmUnitTest` control the behavior

### Checks
- A Judge PASS rejected by the real-signal gate now NAMES the Checks that blocked it (id, type and label) in the mission's `FailureReason`, and states that every failed Check must be resolved. The message previously named only the rule, so an operator could not tell which record to inspect; when several Checks failed for one environmental cause, resolving all but one left a leftover that silently rejected the PASS hours later

### Recent dashboard and session updates
- Re-aligned the dashboard with the consolidated navigation while retaining fork-specific Code Index, Notifications, coordination, token-usage, and captain-assignment surfaces
- Added addressed-note wake delivery, registered-session spawning, participant-key visibility, and documentation for helper-session handoffs
- Added bearer and session-auth headers to dashboard requests where the relay requires both forms of authentication
- Repaired dark readiness cards, workspace layout, responsive shell controls, mobile sidebar navigation, and Ask Armada control sizing

### Migration scenarios seed the schema they stop at
- Database migration scenarios that stop the schema below the newest version now seed every row with SQL that names only the columns of the stop version, through one shared test seed helper. The catalog and column prune, reviewer persona prune and dock anchor scenarios no longer seed through driver create methods, which write the newest row shape and fail on every provider once a later migration adds a column to a seeded table

---

## v0.9.0

Focus: upstream v0.9.0 feature ports on top of the fork's delivery-management core.

### Delivery
- Added CD webhook integration: when a release transitions to Shipped (operator approval), the admiral POSTs a `release.shipped` JSON evidence payload (release identity, version, tag, linked voyages/missions/checks) to a configured external endpoint, so any continuous-delivery system can pick up deployment
- Configured under the optional `cdWebhook` key in `~/.armada/settings.json` (`enabled`, `url`, optional `bearerToken`, `timeoutSeconds`, `maxRetries`, `retryBackoffSeconds`); absent or disabled means no behavior change
- Retriable webhook failures (5xx, network, timeout, auth) are retried up to `maxRetries` times with a fixed backoff; non-retriable 4xx responses return immediately
- Added the MCP `test_release_webhook` tool (registered only when the CD webhook is configured): sends a synthetic payload and returns the delivery outcome, for verifying endpoint reachability and authentication before approving releases
- Added `GET /api/v1/releases/{id}/webhook-events` and a CD Webhook Delivery card on the release dashboard page showing each delivered/failed attempt with HTTP status, message, and timestamp; the card supports auto-refresh
- Fixed dashboard tests on hosts where Node's global `localStorage` binding shadows jsdom's (in-memory polyfill in the test setup) and excluded macOS AppleDouble `._*` sidecar files from vitest discovery
- Dispatch outcomes are recorded as `release.webhook.delivered` / `release.webhook.failed` events on the release; transport failures never block or fail the release update

### Workspace
- Added an in-browser dock terminal: `WorkspaceService.ExecAsync` runs a bounded shell command in the vessel working tree (tenant-admins only, killed with its process tree on timeout), exposed as `POST /api/v1/workspace/vessels/{vesselId}/exec` and a dashboard Terminal panel
- Added an in-app review diff: `WorkspaceService.GetDiffAsync` returns a unified working-tree git diff, exposed as `GET /api/v1/workspace/vessels/{vesselId}/diff` and a dashboard Review Diff panel
- Hardened every workspace git invocation with a 30-second timeout, process-tree kill, and pager/credential suppression, so a wedged git cannot hang the workspace endpoints

### Inbox
- Added the needs-you inbox: `InboxService` aggregates missions in Review, failed landings, failed missions, stalled captains, failed merges, and deployments pending approval or failed/verification-failed, ordered most-urgent first
- Exposed the inbox as the MCP `inbox` tool, `GET /api/v1/inbox`, the dashboard Needs You page, the `armada inbox` CLI command (`--critical` filters), and `ArmadaApiClient.GetInboxAsync`

### Landing
- A landing retry that fails now records the conflicted-file list in the mission's `FailureReason` (`IGitService.GetConflictedFilesAsync`), so the operator sees exactly which paths to fix
- The `LocalMerge` post-landing working-directory sync guards on tracked changes only, not on a fully clean tree: an untracked captain scratch directory in the configured checkout no longer forces `working_directory_sync_failed` on otherwise-landed work, because a fast-forward preserves untracked files and the merge the sync calls already tolerated them. Only uncommitted tracked changes (or a wrong branch) still hold the sync (`IGitService.HasUncommittedTrackedChangesAsync`)
- The merge-queue integration worktree is guaranteed a clean slate before it is rebuilt: a locked or partially-removed scratch worktree that survived the best-effort cleanup is now deleted from disk and its stale registration pruned, so `PrepareIntegrationWorktreeAsync` never recreates over a dirty leftover. A left-behind scratch checkout with uncommitted tracked deletions no longer fails the whole landing with `contains tracked modifications` on a disposable `_merge-queue/` worktree

### Reliability
- Background jobs left Accepted or Running past the stale threshold (a hung or dead worker) are reaped as failed on the health-loop cadence instead of reading as in-flight forever

### Captains
- Added `POST /api/v1/captains/{id}/unquarantine`, giving the MCP `armada_unbench_captain` tool a REST counterpart

---

## v0.8.0

Focus: backlog-first delivery management.

### Backlog and Objectives
- Added normalized first-class objective storage with ranked backlog metadata, lifecycle fields, source lineage, and continued `objective.snapshot` event emission
- Added backlog alias REST routes, ranked reorder support, dashboard/.NET client request models, and MCP backlog CRUD plus reorder aliases
- Added objective refinement sessions and transcript messages with explicit captain selection, captain availability checks, summary generation, apply-to-objective support, and server startup wiring

### Delivery Lineage
- Added automatic objective linkage through deployment and incident create/update flows, including inference from linked release, mission, voyage, and deployment context
- Added deployment and incident objective-link helpers so the same objective remains the record of truth as work moves from release into rollout and response

### Release and Migration
- Bumped shared product/package metadata to `0.8.0` across .NET, Helm, dashboard, Postman, and current-version API/documentation surfaces
- Added versioned `v0.7.0 -> v0.8.0` migration handoff scripts with backlog/objective and refinement table guidance for all supported backends
- Updated schema/version verification coverage for the new backlog schema baseline

### Reflection Memory
- Added the vessel reflection schema, threshold settings, and auto-dispatch of reflection missions during the audit-queue drain
- Added the MemoryConsolidator persona, reflection pipelines (single and dual-Judge), output-contract parsing, and consolidate / accept / reject memory-proposal MCP tools
- Added reorganize mode with soft-validation and a dual-Judge gate, quality metrics, and threshold persistence across vessel APIs
- Added v2-F1 pack curation: vessel pack-hint schema, pack-usage mining from captain logs, pack-curate briefs and auto-trigger, and a context-pack pre-selection pass
- Added v2-F2 persona and captain identity memory: persona/captain-learned playbooks, cross-vessel habit-pattern mining, six consolidation modes, and dispatch tooling
- Added v2-F3 fleet memory: fleet-learned playbook schema, fleet-curate threshold and fan-out, and stale-anchor detection for accepted memory notes

### Model-Tier Routing and Provider Neutrality
- Reworked preferred-model routing into two effective tiers (`mid` for Worker missions, `high` for specialist personas); the legacy `low` selector maps to `mid`
- Moved tier membership and provider routes into a configurable registry instead of hard-coded model names
- Added non-native-first selection: an idle external-provider captain is preferred over native models in any tier, unranked models are equal random peers, and OpenCode-runtime captains count as native for the preference
- Added Codex external-provider routing through a per-endpoint `--profile` config layer, OpenCode inline provider overlays, per-captain provider credential overrides masked on the MCP surface, and hot-reloadable settings for admission and tier policy

### Captain Prompt Shaping and Budgets
- Added a hard total-budget backstop that elides lower-priority content modules when a brief exceeds the captain budget; persisted mission descriptions and stage handoffs stay under the same budget
- Added measured prompt-budget telemetry on mission status alongside authoritative runtime token usage
- Added a `mission.git_anchors` brief module: the commit the work starts from, the target branch tip, the recent commits touching each path the mission names, and whether the mission's subject terms already exist on the checkout. A negative prior-art result is stated explicitly and anchored to the commit the search ran against, and prior art counts matching files rather than lines so the number is exact rather than a bounded sample
- Added `MissionSubjectExtractor`, which derives the anchored paths and terms from mission text deterministically and without git, so the selection a brief was built from is reproducible from the mission alone
- Added four anchor queries to `IGitService`, each defaulting to "nothing resolved"; anchors enrich a brief and never gate a dispatch. An unresolved block renders nothing and logs the reason, a partial block is marked `INCOMPLETE` so silence is not read as absence, and the block is capped, line-boundary truncated, and tracked in the prompt-budget ledger
- Added no-op completion rejection: a sub-minute empty-diff completion with a bare marker fails with `no_op_completion_detected` and re-dispatches to a different captain
- Persisted pipeline stage order with barrier parallel stages; the objective-scope brief module is emitted once per dispatch and read-only dispatches stay single-stage

### Recovery, Verification, and Gate Hardening
- Serialized the in-dock DoD gate host-wide under a dock lease so concurrent gates cannot crash the test host
- Attached Build and UnitTest checks to rescue voyages; a Judge PASS without green independent checks is rejected with a named failure reason
- Resolved rescue model tier against the rescue persona so a Worker rescue never strands on a high-tier-only roster
- Required a Judge verdict line before exit; an explicit agent verdict wins over a non-zero process exit, and a rejected PASS cannot loop into a fresh rescue
- Added distinct Judge review lenses, data-diff scoping, and an index-staleness sweep
- Added queryable long-running jobs, unlanded-branch visibility, mission stage-order persistence, and build-drift evaluation
- Fixed cross-tenant objective-link validation, ISO-8601 timestamp binding in five PostgreSQL drivers, and duplicate-reflection dispatch suppression

### Operations and Platform
- Added captain papercuts: structured friction reports collapsed by vessel, category, and problem
- Added a bounded disk-lifecycle scan and reconcile with sibling-worktree leases, and a maintenance sweep that prunes merged mission branches per policy
- Added per-captain provider credential overrides, papercut triage guidance, and authoritative token-usage telemetry through `/api/v1/events/token-usage`
- Added one-shot server provisioning (`bootstrap-server.sh`), virtual-CAN recreation on container start, and admiral image hardening (OpenCode and Mux CLIs, dotnet SDK, bounded build cache)

---

## v0.7.0

Focus: remote access.

### Remote Access
- Added an experimental outbound remote-control tunnel foundation in `Armada.Server`
- New `RemoteControl` settings are persisted in `settings.json` and exposed through `GET/PUT /api/v1/settings`
- Health and status responses now expose `RemoteTunnel` telemetry including state, instance ID, latency, and last error
- React dashboard, legacy dashboard, and `armada status` now surface remote tunnel configuration and live state
- Added request/response handling and server event forwarding on the tunnel contract
- Added `Armada.Proxy` with websocket tunnel termination, instance summaries, recent-event inspection, and live `armada.status.snapshot` / `armada.status.health` forwarding
- Added focused tunnel-backed remote inspection routes for recent activity, missions, voyages, captains, logs, and diffs
- Added bounded tunnel-backed management routes for fleets, vessels, voyages, missions, and captain stop
- Added a proxy-hosted remote operations shell at `/` for mobile-first remote triage, fleet and vessel management, voyage dispatch, mission editing, and captain control
- Added `docs/TUNNEL_PROTOCOL.md`, `docs/PROXY_API.md`, and `docs/TUNNEL_OPERATIONS.md` for the shipped tunnel and proxy contract

### Runtime and Hosting
- Updated the embedded server stack to Watson Webserver 7 for both HTTP and WebSocket handling
- Removed the standalone `WatsonWebsocket` dependency in favor of Watson 7's built-in WebSocket capability
- Fixed interactive server startup so `update.bat` and normal foreground launches no longer hang on startup handoff

### Dashboard and UX
- Reworked the setup wizard into a contained first-run workflow that uses dispatch directly instead of sending users into separate dashboard pages
- Expanded server settings with remote tunnel controls, MCP client references, system path inspection, database backup actions, and clearer hover guidance
- Added press-and-hold reveal controls for remote-control secrets and other protected login/setup inputs
- Added a full playbook management surface in the dashboard with list, detail, editing, delete, and ordered selection UX on voyage dispatch flows
- Added explicit success and warning toast feedback across dashboard mutation flows so save, delete, cancel, stop, and update actions acknowledge completion visibly
- Added a first-class `Workspace` experience with vessel-aware file browsing, editing, search, git status, context curation, and direct planning/dispatch handoff
- Added `System > Requests` and `System > API Explorer` so captured REST traffic, OpenAPI-backed live execution, and replay all live inside the Armada dashboard
- Added a first-class `Delivery` section in the dashboard for workflow-profile management, structured check-run inspection, and release drafting/detail flows
- Added first-class `Operations > Objectives`, `Delivery > Environments`, `Delivery > Deployments`, `Activity > Incidents`, and `System > Runbooks` dashboard surfaces for scoping, rollout, incident response, and guided operational execution
- Added `Activity > History` with saved views and export so cross-entity delivery memory spans objectives, planning, dispatch, checks, releases, deployments, incidents, events, merge activity, and request history

### Playbooks
- Added tenant-scoped markdown playbooks with CRUD across REST, MCP, proxy remote management, dashboard, CLI, SDK, and Postman
- Voyages and standalone missions can now carry ordered playbook selections with per-selection delivery mode: `InlineFullContent`, `InstructionWithReference`, or `AttachIntoWorktree`
- Mission dispatch now snapshots selected playbooks and injects them into mission instructions with resolved path metadata when file-based delivery is requested
- Added playbook persistence tables and schema migration support for SQLite, PostgreSQL, SQL Server, and MySQL
- Added reproducible mission-time storage of playbook filename, markdown content, selection order, and resolved delivery mode so later playbook edits do not rewrite historical execution context
- Added dashboard selection tooling that scales to larger playbook libraries through filtering, batch add/remove, and explicit ordering controls instead of one-card-per-playbook dispatch UI

### Internationalization
- Added a dashboard locale runtime, translation catalog, and persistent language selection available from login and the authenticated shell
- Added initial translations for English, Spanish, Simplified Chinese, Traditional Chinese, Cantonese, Japanese, German, French, and Italian
- Localized shared shell surfaces including login, pagination, notifications, setup wizard flows, and server/settings management views
- Expanded route-level coverage across list, detail, admin, and setup flows so Spanish no longer falls back to English on common table headers, filters, actions, and confirmations
- Routed legacy dashboard confirms, alerts, toasts, pagination affordances, and key static view copy through the shared i18n runtime so non-React surfaces honor the selected locale
- Added locale-aware date, time, and number formatting for dashboard runtime data
- Extended localization coverage to newer operational pages and shared controls so the playbook, dispatch, and administrative flows follow the same runtime and persistence model as the rest of the dashboard

### Planning, Runtimes, and API Tooling
- Added planning-session REST endpoints for list, create, transcript detail, message turns, summarize-to-dispatch, direct dispatch, stop, and delete flows
- Added persistent request-history capture, summaries, scoped delete flows, and replay metadata across SQLite, PostgreSQL, SQL Server, and MySQL
- Added Mux captain runtime integration, Mux endpoint/config support on captains, and runtime helper APIs for saved endpoint discovery
- Added live OpenAPI publishing at `/openapi.json` and `/swagger` to back the dashboard API Explorer and external tooling

### Delivery Workflows
- Added workflow profiles as first-class vessel/fleet delivery recipes for lint, build, unit test, integration test, e2e test, package, publish artifact, release versioning, changelog, deploy, rollback, smoke-test, and health-check flows
- Added workflow-profile validation, scope-aware default resolution, and required secret/config reference declarations across SQLite, PostgreSQL, SQL Server, and MySQL
- Added workflow-profile CRUD, validation, resolve, and enumerate APIs plus `ArmadaApiClient` support and dashboard list/detail/edit flows
- Added vessel readiness, setup-checklist onboarding, and typed workflow-input preflight across Workspace, vessel detail, Planning, Dispatch, and Checks
- Added structured check runs with durable status, timings, logs, artifacts, parsed test/coverage summaries, compare-to-previous-run analysis, retry, branch/commit metadata, and mission/voyage/release linkage
- Added check-run execute/import/read/retry/delete/enumerate APIs, dashboard list/detail flows, and launch hooks from Workspace, vessel detail, mission detail, voyage detail, and release detail
- Added first-class release records with version inference, draft/candidate/shipped state, artifact aggregation, linked work, and refreshable derived notes
- Added first-class objective records with linked vessels, planning sessions, voyages, checks, releases, deployments, incidents, and acceptance criteria
- Added first-class environments and deployments with approval, verification, rollback, request-history evidence, and default-environment seeding on startup
- Added first-class incidents, hotfix handoff, and playbook-backed runbooks with execution history
- Added optional server-global `GitHubToken` configuration plus per-vessel `GitHubTokenOverride` fallback with write-only update semantics, request-history redaction, and `hasGitHubTokenOverride` read models across REST, MCP, WebSocket, and dashboard surfaces
- Added pull-based GitHub delivery integration for objective import from issues or PR scope, GitHub Actions sync into structured checks, and GitHub PR review/check evidence on mission and release detail surfaces
- Added MCP enumeration support for `workflow_profiles`, `check_runs`, `releases`, `objectives`, `deployments`, `incidents`, `runbooks`, and `runbook_executions`
- Added MCP delivery and operations tools for `run_check`, `get_check_run`, `retry_check_run`, `create_release`, `get_release`, `create_objective`, `get_objective`, `create_deployment`, `get_deployment`, `approve_deployment`, `verify_deployment`, `rollback_deployment`, `get_runbook`, `get_runbook_execution`, and `start_runbook_execution`
- Added WebSocket delivery and operations events for `check-run.changed`, `objective.changed`, `deployment.changed`, `deployment.progress`, `environment.health`, and `approval-needed`

### Release and Docs
- Updated shared release metadata, docker tags, Postman examples, REST docs, MCP docs, and WebSocket docs to reflect the shipped objective, environment, deployment, incident, runbook, and history surfaces
- Promoted the shipped remote-management guide into `docs/REMOTE_MGMT.md` and archived the earlier planning doc
- Added no-op `v0.6.0 -> v0.7.0` migration scripts to reflect the release even though no database schema change is required
- Updated README and operator docs for the new playbook lifecycle, delivery modes, workflow profiles, readiness/onboarding, structured checks, releases, history, remote playbook management flows, workspace, request-history, planning-session, Mux, and internationalized dashboard behavior
- Expanded database, automated, MCP, WebSocket, request-history, and dashboard Vitest coverage around workflow profiles, checks, releases, and history

---

## v0.5.0

Focus: dispatch and pipeline stability.

### Dispatch and Pipeline Stability
- Hardened architect-to-worker handoff behavior, mission status freshness, branch cleanup, worktree cleanup, and landing paths
- Improved mission and voyage telemetry so active work reports current state more reliably
- Tightened dock/worktree safety to prevent dirty fresh docks and stale branch leakage

### Captains and Runtime Selection
- Added optional `Model` on captains across SQLite, MySQL, PostgreSQL, and SQL Server
- Captain model selection is exposed through dashboard, REST, MCP, and Postman examples
- Runtime launches now pass the configured model where supported, otherwise the runtime chooses its default
- Captain create/update now validates configured models before saving and returns a user-facing error when the model is invalid or unavailable

### Missions and Pipeline Reliability
- Added `TotalRuntimeMs` on missions, surfaced in API responses and the mission detail dashboard
- Mission create/update now touch parent voyage `LastUpdateUtc` so active voyages report fresh status
- Architect handoff text now strips trailing `[ARMADA:*]` control markers before passing instructions downstream
- Worktree creation now fails fast if a fresh dock is dirty, preventing unrelated tracked-file contamination
- Dock and mission branch cleanup was hardened across no-op landing and published-server worktree reclamation paths

### Git and Landing
- Worktree branch creation now creates the branch ref before attaching the worktree and keeps existing-branch docks on the named branch
- Merge handling now retries with `--allow-unrelated-histories` when needed
- Diff capture now falls back cleanly when there is no merge base instead of producing an empty snapshot
- Architect-only branches are cleaned up after successful fan-out instead of lingering indefinitely

### Dashboard and Docs
- Captain detail now supports editing and displaying the configured model
- Mission detail now uses a four-column layout and shows total runtime
- Login secret inputs now support a press-and-hold reveal control
- Dispatch page no longer shows the redundant detected-task UI or stale task-splitting guidance
- README, REST API, MCP API, compose.yaml, and release metadata are updated for `v0.5.0`

---

## v0.4.0

### Personas and Pipelines
- Added personas: named agent roles (Worker, Architect, Judge, TestEngineer) with custom persona support
- Added pipelines: ordered sequences of persona stages (WorkerOnly, Reviewed, Tested, FullPipeline) with custom pipeline support
- Pipeline resolution: dispatch param > vessel default > fleet default > WorkerOnly
- Architect stage special handling: parses [ARMADA:MISSION] markers to create multiple Worker missions
- Stage handoff: injects prior stage output (agent stdout + diff) into next stage description
- Persona-aware captain routing: AllowedPersonas and PreferredPersona on captains
- Mission dependency chain: DependsOnMissionId gates assignment until predecessor completes

### Prompt Templates
- Every prompt is now template-driven and user-editable (18 built-in templates)
- Categories: mission, persona, structure, commit, landing, agent
- Dashboard two-column editor with parameter reference panel
- MCP tools: get/update/reset_prompt_template
- REST endpoints: /api/v1/prompt-templates CRUD

### Dashboard
- Personas, Pipelines, Prompt Templates pages
- Pipeline dropdown on Dispatch, Voyage Create, Vessel, Fleet
- Mission detail: persona badge, depends-on link, failure reason display
- Captain detail: AllowedPersonas, PreferredPersona fields
- Vessel edit: 95% width, 3-column layout
- Log viewer: LIVE/DONE indicators, follow mode
- Toast notifications instead of layout-shifting banners
- Consistent CopyButton component across all pages
- Version display on login and sidebar

### Infrastructure
- Schema migrations 19-23 across SQLite, MySQL, PostgreSQL, SQL Server
- FailureReason field on missions (surfaced in dashboard)
- Vessel deletion cleanup: cancels missions, deletes docks, bare repo
- Empty repo auto-seed: creates README.md on first dispatch to empty GitHub repo
- Process.Dispose on agent exit to release Windows directory handles
- Crash logging: AppDomain.UnhandledException + TaskScheduler.UnobservedTaskException
- Case-insensitive email login
- CLAUDE.md auto-gitignored in worktrees

### API
- 11 new MCP tools (persona, pipeline, prompt template CRUD)
- 17 new REST endpoints
- 12 new WebSocket commands
- enumerate supports personas, prompt_templates, pipelines
- dispatch accepts pipelineId and pipeline (name) parameters
- Voyage status considers LandingFailed, WorkProduced, PullRequestOpen as terminal

### Documentation
- PIPELINES.md: complete implementation reference
- PERSONAS_GUIDE.md: user-facing guide
- TESTING_PIPELINES.md: 6 end-to-end test examples
- OLLAMA_AS_CAPTAIN.md: implementation plan for Ollama runtime
- VLLM_AS_CAPTAIN.md: implementation plan for vLLM runtime

---

## v0.3.0

### Added

- **Multi-tenant support** -- all operational data (fleets, vessels, captains, missions, voyages, docks, signals, events, merge entries) is scoped by tenant
- **Tenant, user, and credential models** -- `TenantMetadata` (`ten_` prefix), `UserMaster` (`usr_` prefix), `Credential` (`crd_` prefix)
- **Bearer token authentication** -- 64-character random alphanumeric tokens linked to a specific tenant and user, sent via `Authorization: Bearer <token>` header
- **Encrypted session tokens** -- AES-256-CBC self-contained tokens with 24-hour lifetime, sent via `X-Token` header. No server-side session storage required
- **Authentication endpoints** -- `POST /api/v1/authenticate`, `GET /api/v1/whoami`, `POST /api/v1/tenants/lookup`
- **Onboarding endpoint** -- `POST /api/v1/onboarding` for self-registration (gated by `AllowSelfRegistration` setting)
- **Tenant CRUD endpoints** -- `GET/POST/PUT/DELETE /api/v1/tenants` (admin only, with self-read for non-admins)
- **User CRUD endpoints** -- `GET/POST/PUT/DELETE /api/v1/users` (admin only, with self-read for non-admins)
- **Credential CRUD endpoints** -- `GET/POST/PUT/DELETE /api/v1/credentials` (admins: all; non-admins: own credentials)
- **Admin vs non-admin access patterns** -- three-tier authorization: `NoAuthRequired`, `Authenticated`, `AdminOnly`
- **Default data seeding** -- on first boot, creates default tenant, user (`admin@armada` / `password`), and credential (bearer token `default`)
- **React dashboard** -- standalone React dashboard (`Armada.Dashboard`) as a separate deployment option for Docker/production
- **Docker Compose with dashboard** -- `compose.yaml` runs `armada-server` and `armada-dashboard` containers together
- **SQL Server support** -- added SQL Server as a database backend option alongside SQLite, PostgreSQL, and MySQL
- **`AllowSelfRegistration` setting** -- controls whether `POST /api/v1/onboarding` is enabled (default: `true`)
- **`SessionTokenEncryptionKey` setting** -- AES-256 key for session token encryption (auto-generated if not provided)

### Changed

- All REST API endpoints now require authentication (except health check, authenticate, tenant lookup, onboarding, and dashboard routes)
- All operational database queries are tenant-scoped for non-admin users
- Admin users see all data across all tenants
- CORS headers now include `Authorization` and `X-Token` in `Access-Control-Allow-Headers`

### Deprecated

- **`X-Api-Key` header** -- retained for backward compatibility but deprecated. When configured, the server creates a synthetic admin tenant (`ten_system`) and user (`usr_system`). Migrate to bearer tokens for new integrations

---

## v0.2.0

### Added

- Multi-database support (SQLite, PostgreSQL, MySQL) with connection pooling
- Structured `database` object in `settings.json` replacing flat `databasePath`
- Migration scripts for v0.1.0 to v0.2.0 settings conversion
- Merge queue system for automated branch merging
- WebSocket hub for real-time event streaming
- Embedded dashboard at `/dashboard`
- MCP server with full API parity (18 tools)
- Batch delete operations for all entity types
- Enumeration (POST) endpoints with JSON body filtering
- Mission diff and log retrieval endpoints
- Captain log streaming
- Dock (worktree) management endpoints
- Signal system for Admiral-captain communication
- Event audit trail

### Changed

- Settings format: `databasePath` string replaced with `database` object (breaking change)

---

## v0.1.0

### Added

- Initial release
- Core orchestration: fleets, vessels, captains, missions, voyages
- Git worktree isolation for parallel agent work
- Multi-runtime support: Claude Code, Codex, Gemini, Cursor
- Auto-recovery for crashed agents
- REST API on port 7890
- CLI (`armada`) with Spectre.Console
- SQLite database backend
- Zero-config startup with auto-detection
