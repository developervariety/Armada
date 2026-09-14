# Armada Operator Guide

This guide is the canonical operating procedure for Armada. It describes how
an orchestrator creates, monitors, verifies, lands, and closes work. It also
lists every MCP tool that the server registers.

Use [MCP_API.md](MCP_API.md) for transport and schema discovery. Use
[DELIVERY_OPERATIONS.md](DELIVERY_OPERATIONS.md) for release and deployment
detail. Use [MERGING.md](MERGING.md), [PIPELINES.md](PIPELINES.md), and
[SCHEDULING.md](SCHEDULING.md) for subsystem detail. Use
[OPERATIONAL_ASSETS.md](OPERATIONAL_ASSETS.md) for playbooks, runbooks,
workflow profiles, environments, personas, pipelines, and their links.

## 1. Source Of Truth

Armada records are the source of truth for work and delivery state.

| Record | Owns |
| --- | --- |
| Objective or backlog item | Scope, acceptance criteria, priority, constraints, and deferred work |
| Planning or refinement session | Decisions made while the scope is not ready for dispatch |
| Voyage and mission | Work assignment and captain execution |
| Check | Command evidence and gates |
| Merge entry | Review, integration, test, and landing state |
| Release | A candidate or shipped unit |
| Deployment | Rollout, approval, verification, and rollback |
| Incident | Failure impact, diagnosis, mitigation, and closure evidence |
| Runbook execution | Evidence that a repeatable procedure ran |
| Event, signal, and request history | Timeline and communication evidence |

Do not use mission prose as a replacement for these records. Do not treat a
captain report as proof. Run the command or query that proves the result.

## Selective upstream integration

Use [the upstream integration review](upstream-review/README.md) for the fixed
comparison, decisions, preservation requirements and validation gates. The
review describes planned work, not deployed features. Keep current objectives
and delivery state in Armada. Armada platform work uses direct edits; the
review campaign must not dispatch voyages, missions or planning captains.

## 2. Available Features And Active Policy

The repository contains features that an operator can disable. Documentation
of a feature does not mean that a deployment enables it.

Check these settings before you depend on the related workflow:

| Setting or record | Effect |
| --- | --- |
| `codeIndex.enabled` | Enables code search, graph search, and context packs. |
| `seedDockRuntimeMcpConfig` | Gives supported captains the local Armada MCP URL through runtime-appropriate dock or launch configuration. Default: enabled. |
| `apiCaptainCloudProviders` | Lists the hosted providers (`OpenAI`, `Anthropic`, `Gemini`) an API-endpoint captain may run against. Default: empty, so only operator-hosted `Ollama` and `OpenAICompatible` endpoints run. Azure OpenAI, Vertex AI and Bedrock are not available. |
| `autonomousRecovery.enabled` | Enables bounded server-side mission recovery. |
| `incidentLifecycle.enabled` | Enables evidence-driven incident transitions. |
| `remoteTrigger.enabled`, mode, and `agentWake.deliveryMode` | Enables AgentWake process and/or signal delivery. |
| Objective `AutoDispatchEnabled` and scheduler state | Enables autonomous objective dispatch. |
| Vessel or voyage landing mode | Selects `LocalMerge`, `PullRequest`, `MergeQueue`, or `None`. |
| Vessel default pipeline | Selects the normal persona path. |
| Workflow profile | Defines the commands that Checks and delivery operations run. |

When a feature is off, use the explicit fallback. For example, search the
checkout directly when code indexing is off. Do not call disabled tools in a
loop.

## 3. Connection And Discovery

The Admiral exposes stateless Streamable HTTP MCP at `/mcp`. `/rpc` is a
compatibility alias. An SSH stdio bridge can forward a local MCP client to a
loopback-bound remote Admiral. The bridge must connect to the running Admiral.
It must not start a second embedded Admiral process.

Every MCP request must carry a credential. A request without one gets `401`;
nothing falls back to a default administrative identity. Only a global
administrator sees the operator catalog. `docs/MCP_API.md` lists the caller
rules, the captain launch credential and the per-runtime headers.

Operator migration when an Admiral with MCP authentication is deployed:

1. On the server, write one header line to a protected file, for example
   `printf 'X-Api-Key: %s\n' "<admiral API key>" > ~/.armada/mcp-auth-header`,
   then `chmod 600` it. Never put the key in a board note, brief or shell history.
2. Set `ARMADA_MCP_AUTH_HEADER_FILE` to that absolute path in the `env` of every
   SSH stdio bridge entry. The bridge refuses requests until it is set.
3. Set `ARMADA_API_KEY` in the environment of every direct HTTP client. Entries
   written by `armada mcp install` reference it by name; re-run the install to
   update an entry written before this change, and add the header by hand to any
   entry you wrote yourself.
4. Refresh the Helm CLI with the Admiral image, then prove a read-only tool call
   through each client. Expect `401` from any client you did not update.
5. Captains need no change on supported runtimes; they receive the launch
   credential at launch. A Mux entry written by an earlier install has no
   `auth` object; re-run `armada mcp install` to add it.

Start each operator session with:

1. Call `armada_status`.
2. Call `armada_enumerate` with small pages for active voyages, missions,
   captains, merge entries, incidents, objectives, and checks.
3. Call `armada_drain_audit_queue` before new dispatches.
4. Read each relevant open objective in full.
5. Check incidents and the merge queue before you create more work.
6. Check `armada_unlanded_branches` when prior work can exist outside the
   normal landing path.
7. Call `armada_list_papercuts` to see the friction that recent captains
   reported. Section 4.9 gives the triage rules.

MCP `tools/list` is paginated. Follow `nextCursor` until it is absent. The
normal built-in catalog fits on one 500-tool page. Pagination remains active
for larger extension catalogs. A client that ignores `nextCursor` can hide
valid tools.

Supported captains receive the local MCP URL through runtime-appropriate dock
and launch configuration. Claude strict mode and Codex receive explicit launch
arguments because a project file alone is not sufficient for those paths. The
catalog also contains dispatch,
administration, deployment, restore, purge, and server-control actions. Those
tools stay outside normal captain scope; the operator owns them unless the
mission explicitly assigns the action. Set `seedDockRuntimeMcpConfig=false`
only when the deployment intentionally removes all Armada tools from captains.

`armada_enumerate` supports these entity types:

`fleets`, `vessels`, `captains`, `missions`, `voyages`, `docks`, `signals`,
`events`, `merge_queue`, `memories`, `personas`, `prompt_templates`, `pipelines`,
`playbooks`, `objectives`, `incidents`, `checks`, `releases`, and
`deployments`.

Use `pageSize` from 10 to 25 unless a larger page is necessary. Large text
fields are excluded by default. Request `includeDescription`, `includeContext`,
`includeTestOutput`, `includePayload`, or `includeMessage` only when needed.

## 4. Standard Workflow

### 4.1 Capture The Work

For non-trivial work, find or create an objective before dispatch.

1. Search with `list_objectives`, `list_backlog`, or
   `armada_enumerate(entityType: "objectives")`.
2. Read the selected record with `get_objective` or `get_backlog_item`.
3. Add clear acceptance criteria and verification requirements.
4. Put deferred work in a separate objective or backlog item. Do not bury it
   in closed-record prose.

Use backlog refinement when intent is still unclear and repository context is
not yet needed. Use a planning session when the work is tied to a vessel and
must become dispatch-ready.

### 4.2 Inspect Before Dispatch

Read the target repository rules and inspect the current code and git history.
Confirm that the requested work is not already present. Confirm the vessel,
pipeline, model tier, landing mode, protected paths, and workflow profile.

Read the coordination board before you dispatch or touch incidents
(`armada_coordination_read`). Post a claim note before you start
(`armada_coordination_post`) so a concurrent operator session does not dispatch
the same work or rescue the same incident twice. General board notes are advisory. Voyage-tagged notes can enter the next
stage brief when the database provider supports that lookup. See 8.9 for the tool list.

If code indexing is enabled, use `armada_index_status` before index-dependent
work. If it is disabled, use checkout search and set `codeContextMode` to
`off`.

Record prepared repository facts in the objective `preparation` field. Set a
verified source and target anchor. Give each claim a stable ID, a kind, evidence,
and a `dependsOn` value of `Source`, `Target`, both, or `None`. Use the claim
kinds for source paths, dispatch entry points, reusable types, catalogue inputs,
provisioning, response rules, cleanup, consumers, ledgers, uncertainty, and
owner decisions. Keep claims concise. Armada limits the claim count and size.

Set `requiredForDispatch` when the prepared facts are mandatory. Name at least
one `requiredClaimKinds` value. Each preparation claim must then have evidence
and a `verifiedUtc` value. Use `requiredSiblingInputs` to name the sibling
vessel, its checkout path, and required extraction-artifact paths. Dispatch
preview blocks work when a required claim, immutable anchor, sibling declaration,
or artifact input is absent or stale.

When a source or target anchor changes, Armada keeps the claims and marks only
the dependent claims `NeedsRecheck`. Recheck those claims and record them as
`Verified`; do not repeat unrelated research.

An objective update replaces the complete `preparation` object. Send all source,
target, and claim values that must remain. A claim that depends on an anchor stays
unverified until that anchor has an immutable `resolvedCommit`.

### 4.3 Select Mission Shape

Use mission mode `Implementation` for work that must produce a commit. Use
`Audit` or `Research` for report-only work. Read-only modes do not require a
commit and must not receive implementation-only instructions.

Both dispatch paths derive the mode from the objective `Kind` when a mission
does not state its own: a `Research` objective runs its missions read-only;
every other Kind runs `Implementation`. Autonomous (scheduler) dispatch has no
per-mission `mode` argument and relies on this entirely. Operator dispatch
(`armada_dispatch` / REST) applies the same rule to any mission linked through
`objectiveId` that omits `mode`, so a `Research` objective dispatched through an
Implementation pipeline keeps every declared stage, but each effective stage
runs read-only and produces or reviews reports instead of code. This preserves
specialist analysis, test review, and Judge coverage. An explicit per-mission
`mode` overrides the objective-derived mode.

Armada classifies a voyage as fully report-only only when every effective
stage is `Audit` or `Research`. Pipeline shaping keeps the complete declared
graph for every mode. The classifier controls automatic Check arming and Judge
validation: a fully report-only voyage does not arm Build or UnitTest Checks,
and its Judge can accept a no-commit report without
`[JUDGE-CHECK-EXCLUSION]`. If any effective stage is `Implementation`, the
complete implementation Check and delivery contract still applies.

Operator and autonomous dispatch use the same server-rendered objective brief.
The brief includes prepared research, constraints, and evidence. An operator
mission keeps its mission-specific text first. The server appends the objective
brief once and inherits the objective start ref only when the mission has no
explicit ref. Do not copy objective fields into an operator prompt; link the
dispatch with `objectiveId` and let the server render the current record.

Run `preview_objective_dispatch` before objective dispatch. It is read-only. It
reports target, pipeline, captain, Check, repository, brief, and dependency
findings. A busy compatible captain is capacity information and does not make
the objective unready.

Use the vessel's configured pipeline unless the approved work calls for a
different existing pipeline. Use the full configured persona path. Do not
remove review stages only to make a voyage faster.

Dispatch with `preferredModel: "low"`, `"mid"`, or `"high"`. Do not put a
concrete provider model in an ordinary mission brief.

### 4.4 Dispatch

Use `armada_dispatch` for a voyage. Put all ordered missions for one vessel in
one request. Use aliases and dependency aliases when order matters.

Include:

- `objectiveId` for durable work;
- the vessel ID;
- the pipeline when the vessel default is not correct;
- one mission title and complete description per unit of work;
- mission mode;
- model tier;
- exact scope and exclusions;
- required verification;
- only the prestaged files that downstream stages can read and need.

`armada_dispatch` is durable-first. A successful response means that the
voyage and mission rows exist. Assignment, dock provisioning, and captain
launch continue asynchronously. Save the voyage ID. Do not redispatch only
because the first status call shows `Pending`.

Assignment selects and claims only an idle captain of the mission's own tenant.
A mission or captain with no tenant belongs to the default tenant. A Pending
mission whose tenant has no idle captain stays Pending and waits for one; an
idle captain of another tenant never takes it. The dispatch sweep, the
scheduler, rescues and restarts all use this one claim.

An objective can have only one normal nonterminal voyage. Scheduler, operator,
bare REST, and remote-control dispatch paths take the same tenant-scoped
database admission lease before they create a voyage and hold it until the
objective link is durable. A competing request returns
`objective_already_dispatched` and names the winning voyage without creating a
losing voyage. A link failure cancels the new voyage and its active mission
rows. A terminal voyage permits an intentional successor. Recovery uses a
separate explicit rescue link because it continues a failed chain.

An engaged dispatch hold is checked before admission and before either voyage
creation path, including alias-ordered missions. A held dispatch records no
attempt, takes no lease and creates no voyage row.

Admission waits are bounded. When another request holds an objective's
admission for longer than five seconds, dispatch creates nothing and returns
409 `objective_dispatch_busy` with `Retryable: true` and `RetryAfterSeconds`.
Bare REST and remote-control dispatch return the same code. The scheduler
records the skip reason `admission_busy` and tries again on a later sweep.

A planning-session dispatch admits the session objective and every other
objective linked to the session in one operation. Their leases are taken in a
stable order before the voyage is created, so two planning dispatches over
overlapping objectives cannot deadlock, and every link is made inside that
admission. If any objective is busy or already dispatched, every lease is
released and no voyage is created. If a later link fails, the objectives already
linked are restored and the voyage is cancelled. The REST and MCP planning
dispatch paths perform no objective link after the voyage exists.

Every admitted dispatch writes a durable attempt record as events:
`objective.dispatch_attempt.started`, `objective.dispatch_attempt.voyage_created`
and `objective.dispatch_attempt.closed`. The attempt id is the holder of every
admission lease it owns. Just before each objective write, linking renews every
lease with a compare-and-set. A dispatch that no longer owns its leases cannot
link, and its voyage is cancelled. The health loop reconciles an attempt that
never closed once its owner no longer holds a live lease. Reconciliation first
takes the attempt's admission. A voyage already linked to any admitted objective
is the winner: it is never cancelled, and it is linked to the remaining admitted
objectives that have no other active voyage. An active voyage that no objective
links is cancelled with its missions. When the process stopped before the voyage
id was recorded, the voyage is matched by the recorded title, vessel and start
time. If more than one voyage matches, nothing is cancelled and the attempt is
closed as unresolved with a warning.

Data expiry runs on the health loop every 100 cycles, on every database
provider. It purges through the database driver, so SQLite, PostgreSQL, MySQL
and SQL Server apply the same rules. With `dataRetentionDays` set above 0 (the
default is 30), a run deletes rows older than that many days:

- Complete or Cancelled voyages by `completed_utc`, with every mission in them.
- Complete, Failed or Cancelled missions without a voyage, by `completed_utc`.
  A mission whose parent is deleted is kept and loses the parent link.
- Read signals and events, by `created_utc`.
- Inactive docks with no captain, by `created_utc`.
- Landed, Cancelled or Failed merge entries, by `completed_utc`.

The same run deletes production metric facts (`mission_attempt_facts`,
`preparation_claim_observations` and `lane_state_transitions`) older than
`productionFactRetentionDays`, by `created_utc`. The default is 365; `0` keeps
facts forever. A production summary window before that cutoff reports the
fact-based measures as unobserved or unknown, never as a value
(`docs/production-metrics.md`).

Each run logs one line, `data expiry summary: dataRetentionDays=<n>
cutoff=<utc> productionFactRetentionDays=<n> factCutoff=<utc> deleted=<total>`
followed by `<table>=<count>` for every table the run purged, including runs
that delete nothing. A retention set to 0 shows its cutoff as `disabled` and
its tables are not listed. With both retentions set to 0 the run logs
`data expiry skipped` and deletes nothing. A failed statement names its table and provider in the
`data expiry failed:` line. The first run on a database that never expired
data can delete many rows; take a backup before the first deployment that
enables it.

Reconciliation looks back seven days. Automatic event retention
(`dataRetentionDays`) never deletes an attempt record younger than that, even
when the retention period is shorter. Cascade cleanup cannot reach attempt
records, because they carry no captain, vessel, mission or voyage id. Manual
deletion is the one exception. `armada_delete_events`, the event delete routes
and tenant or user deletion remove attempt records like any other event. If an
unclosed attempt's records are deleted, reconciliation can no longer find an
orphan voyage from that attempt. Before deleting events by hand, exclude the
`objective-dispatch-attempt` entity type unless every such attempt is closed.

Inside one Admiral process, objective linking is also serialized per objective
by an in-memory keyed lock. That lock is local defense-in-depth only; it is not
cross-instance admission. The database lease is the guarantee. A lock entry
exists only while a caller holds or waits for that objective, so the lock set
stays bounded however many objectives the process links.

A Completed or Cancelled objective always rests in the `Inbox` backlog state.
Armada moves it there in the same row write that makes it terminal, on every
path: a manual update, an import, a recovery link, and scheduler reconciliation
after the linked voyages land. A terminal objective therefore never lists or
selects as `ReadyForDispatch`, and an operator does not need to move a finished
row to `Inbox` by hand. Existing terminal rows in an active backlog state are
moved by a schema migration; rows with a nonterminal status are unchanged.

When `voyageDispatch.rejectStagePersonaTitlePrefixes` is true and the
prefix list is not empty, a mission title that already carries a listed
stage-persona prefix such as `[Worker] ` is rejected with 400 and code
`mission_title_carries_stage_persona_prefix`. The product default leaves
this guard off. Such a title is a materialized pipeline STAGE, not a
task: the pipeline prepends the persona itself, so dispatching prior
stage missions as tasks multiplies the work by the stage count. Dispatch
the objective's ORIGINAL task once, with the leading `[<persona>] ` tag
removed.

Long operations can return an accepted job. Poll `armada_job_status` with the
returned job ID. Background jobs are reaped on a health-loop cadence: a job
left Accepted or Running past the stale threshold (a worker that hung or died)
is failed automatically so it reaches a terminal status instead of reading as
in-flight forever. Poll a job past its expected runtime; if it was reaped, the
status is `Failed` with a reason naming the stale window.

#### Controlled parallel dispatch

Armada can run several voyages at once. The scheduler's
`maxConcurrentVoyages` is a fleet-wide ceiling, not a requirement to dispatch
one voyage at a time. For normal autonomous work, keep two or three independent
writable lanes ready when the fleet has enough safe work. Keep
`maxConcurrentVoyagesPerVessel=1` unless one vessel is proven safe for parallel
docks and suites.

Before an operator enables a lane, confirm:

- a different active voyage does not own the same files or objective;
- no other active voyage uses the same vessel unless its suite and worktree
  policy are proven safe for concurrent docks;
- captains will not run conflicting dock-side suites or move a shared sibling;
- the objective is not hardware-dependent or operator-only;
- the brief premise is true at the target tip; and
- required ordering is in `BlockedByObjectiveIds`.

Prefer parallel voyages on different vessels. Treat repositories as one lane
when either suite builds or tests the other through a sibling-project reference.
Every dock is provisioned as `docks/<Vessel>/<mission>/<Vessel>` with its
declared siblings beside that checkout (`docks/<Vessel>/<mission>/<Sibling>`), so
a sibling is pinned at provisioning and is never shared between docks. A dock
that re-provisions the same mission (a retry) reuses its own sibling; when that
sibling is behind the branch tip the admiral logs both commits and records
`dock.sibling_stale`.

Declare that relation with `buildParticipant: true` on the sibling entry of the
vessel that builds the other; the scheduler then applies the per-vessel ceiling
to every vessel in the lane and reports a refused dispatch as
`lane_busy:<vesselA>+<vesselB>`. A read-only sibling (a decompiled or
extraction-artifact tree) stays `false` and forms no lane.
The same lane rule is an assignment gate for all dispatch sources, including
operator missions, recovery missions, and missions inserted for a later sweep.
Armada reserves all lane members with durable database leases before it checks
active mission rows and provisions a dock. Thus, two server instances cannot
both admit sibling writers after they read the same idle state. A mission that
loses this short reservation waits in `WaitingForVesselMutex`. An unlinked
operator voyage with repository work also consumes scheduler capacity.
Armada-owned Checks serialize on the host interlock, but a captain can still run
a suite directly in its dock.
Use bounded read-only helpers to prepare future lanes; use captains and voyages
for repository writes. When throughput looks low, inspect both the scheduler
ceiling and how many objectives have `AutoDispatchEnabled=true`.

Objective create and update tools accept `suggestedPlaybooks`, an array of
`playbookId` and `deliveryMode` pairs. The scheduler copies these selections to
the voyage before it creates the first mission. Use this field for campaign
rules that must reach every pipeline stage.

#### Start ref: continue from an accepted tip

An objective can carry `startFromRef` (a branch, tag or commit in the vessel
repository, set through `create_objective` / `update_objective`). When the
scheduler or an operator dispatches a voyage for it, the FIRST stage's branch is
cut from that ref instead of the vessel default branch; later stages continue
the branch as usual. Use it to re-dispatch a row from a `recover/<name>-<sha>`
ref that holds an accepted Worker or TestEngineer tip, so the new voyage does
not rebuild what a Judge already accepted.

The ref is resolved twice, and both failures are loud. At dispatch, a ref that
does not resolve refuses the whole dispatch before any voyage row exists; the
scheduler reports it as `start_from_ref_missing` (never `dispatch_error`) and
writes an `objective_scheduler.start_from_ref_missing` event. At assignment,
the branch is cut at the ref in the bare repository before the dock is
provisioned; if the ref has gone by then, the mission fails with
`start_from_ref_missing` and no captain is launched. There is no fallback to
the default branch: a captain working on the wrong base reads as a captain
defect two stages later. A dependent mission ignores the field, because it
continues its predecessor's branch.

#### Model pinning

`preferredModel` normally takes a complexity tier (`mid` or `high`; the
legacy `low` value maps to `mid`) and Armada picks the best-fit available
captain. It also accepts a literal
model name (for example `claude-opus-5`, `gpt-5.6-sol`,
`opencode-go/glm-5.2`): the dispatcher first filters idle
captains by an exact case-insensitive model match, and only then falls back to
tier routing. A pin is therefore best-effort, not a guarantee -- when no idle
captain carries that exact model, Armada classifies the model into a tier and
picks a peer. Verify the assignment after dispatch when the pin matters;
benching is never required to pin a model.

#### Read-only pipeline resolution

An explicit pipeline keeps all declared stages for Audit and Research. Mode
controls the deliverable and Check requirements; it does not trim that graph.
The core dispatch fallback is different: when all missions are read-only and
no pipeline ID reaches the core resolver, it skips vessel/fleet defaults and
uses a single stage. Link the objective and its selected pipeline through the
shared dispatch path, and inspect preview before dispatch. Do not infer the
pipeline from mission mode alone.

### Stage handoff is verified, not assumed

A downstream pipeline stage inherits its predecessor's branch. Inheriting a
branch NAME is not the same as inheriting its commit: a local ref can predate
the upstream stage's push, and the resulting worktree looks correct while
missing the work.

Armada now proves the containment before the captain starts. A stage whose
checkout demonstrably lacks the upstream commit fails immediately with
`stage_base_missing`, which names the commit, the branch, and the upstream
mission, and says plainly that the fault is in provisioning rather than in the
stage's own work. Read that before reading the diff - the previous version of
this failure presented as a Worker that could not compile its own change, and
the diagnosis started in exactly the wrong place.

What is NOT failed, deliberately:

| Condition | Verdict |
| --- | --- |
| Mission has no upstream stage | Not applicable |
| Dependency is in another vessel | Not applicable - commits are not shared across repositories |
| Upstream produced no commit (Audit, Research) | Unverified, stage proceeds, fact logged |
| Ancestry probe could not answer | Unverified, stage proceeds, fact logged |

A base that could not be proved is never reported as one that was. The git
ancestry probe answers true, false, or UNKNOWN, and its default for any
implementation that does not consult a real repository is unknown - so a stub
can never manufacture a passing verification.

### The captain brief is bounded as a whole

A persisted mission description can grow through handoffs, rescues, mailbox
notes and board notes. The brief a captain receives is bounded where it is
rendered, so no persisted description can push it over
`CaptainInstructionByteBudget`:

1. One bounded copy of the description feeds every module that embeds it: the
   metadata module and any persona template that restates the objective.
2. When the assembled brief is still over budget, the backstop elides, in
   order: content modules (objective scope, project context, style guide,
   playbooks, existing instructions); then every embedded copy of the
   description, measured in bytes, keeping the head brief and the newest
   handoff block; then reference modules (skills, git anchors, code-index
   guidance). The persona prompt, rules and output contract are never elided.
3. A rescue description caps the scope, the reviewer feedback and the failure
   reason separately, so a gate log in the failure reason cannot dominate it.

The `mission.prompt_budget` event records the bytes actually written. A brief
reporting `OverBudget=true` after this backstop means the protected skeleton
alone exceeds the budget; read its module sizes before raising the budget.

### Authorization reaches every captain the same way

Record the owner's authorization for a project in the project profile's
`authorizationPolicy` field (dashboard: Project Profiles, Authorization Policy;
REST: `PUT /api/v1/project-profiles/{id}`). The profile that applies to a
vessel is chosen vessel, then fleet, then global. Every brief path renders
the section `## Authorization and Hard Limits` from that one field: the policy
verbatim, then fixed hard limits (secrets, tenant isolation, protected paths,
destructive operations) that the policy cannot relax. The section is
never elided by the budget backstop. Never put credentials or license
material in the policy; it is copied into every brief.

When a captain refuses its mission, completion classifies the refusal before
any other gate: the structured `[ARMADA:RESULT] REFUSED: <reason>` line the
section asks for, then provider safeguard text, then declining prose in the
captain's closing lines (never when it wrote a completion marker). Every
refusal is recorded as a `mission.policy_refusal` event with its kind, reason
and decision.

| Condition | Outcome |
| --- | --- |
| A model declined and no owner policy applies to the vessel | Normal completion handling |
| First refusal or provider safeguard block, an approved captain exists on another runtime | Requeued once as `mission.policy_refusal_continued`; every captain on the refusing runtime is excluded, with no fall-back |
| Refused or blocked again after the continuation | Mission fails with `policy_refusal:` and the reason |
| No approved captain on another runtime | Mission fails with `policy_refusal:` and the reason |

A provider safeguard block follows the same rule whether it appears in a
completed run's output or ends the captain process, and with or without an
owner policy. There is no other safeguard re-route: the captain is not benched,
and the blocked runtime is never retried. Autonomous recovery does not rescue a
`policy_refusal:` failure, because a rescue would repeat the blocked path; route
it by hand. The continuation never weakens provider safety policy.

### A readable file is not a runnable one

Prepared research that needs a runtime declares it in
`preparation.executionRequirements`: `operatingSystem`, `architecture`,
`executables`, `dependencyPaths`, `isolationBoundary` (`Container` or `Host`)
and `licensedContext` (a name listed in the settings
`AvailableLicensedContexts`; never license material). Dispatch preview checks
them against the environment captains launch in, not only against a path the
Admiral can read, and blocks with one `execution_*` finding per missing
requirement. Preview never runs a declared executable; it only resolves it on
the captain PATH. The scheduler and manual dispatch read the same preview.

### A quiet-host gate must enumerate TERMINAL states, not guess at active ones

Before any action that interrupts running work - restarting the Admiral,
rebuilding its image, reclaiming docks - operators check whether the host is
busy. The obvious query is wrong.

A voyage is not only `InProgress`. It is also `Open`, and a gate written as
`where status = 'InProgress'` reports a quiet host while `Open` voyages are
running captains. That is a false quiet, and acting on it destroys work.

Enumerate what is FINISHED and treat everything else as active:

```sql
select count(*) from voyages
where status not in ('Complete','Failed','Cancelled');
```

Written that way, a status added to the vocabulary later reads as active and
the gate fails safe. Written the other way, a new status is invisible and the
gate fails silently open.

Voyages alone are not enough either. Check executing missions and captain
processes too, because a mission can outlive its voyage's terminal status:

```sql
select count(*) from missions where status in ('InProgress','Assigned','Testing');
```

The same reasoning applies to any "is it safe to act?" predicate in Armada.
State the finished set, not the busy set.

### 4.5 Monitor

Dispatch is the start of the operator loop.

1. Subscribe with `scripts/autonomy/watch-armada.mjs`. Use `--voyage` for one
   voyage and `--participant` for directed board messages. Use
   `--exit-on-terminal` for a bounded watch.
2. Treat each mission-change event as a stage boundary. Read the mission when
   the event needs more detail.
3. Read mission and captain logs when progress is unclear.
4. Use `armada_captain_diagnostics` before deep process inspection.
5. Inspect incidents and Checks when their events change. Keep periodic state
   reconciliation as a safety check.
6. Send `Mail` or `Nudge` only to a Pending downstream stage. A running stage
   has already frozen its brief and does not read the new signal.
7. Do not use a foreground polling loop. It blocks board messages and misses
   short stage boundaries.
8. When a tool result carries an `[ARMADA WAKE]` banner, pause current work,
   address the message, and acknowledge it with `armada_mark_signal_read`.
9. Do not steer a terminal mission. Use restart, recovery, or a new mission.

A quiet captain is not proof of a stall. Compare the mission state, process
ID, dock status, log activity, and elapsed time.

Armada applies the same rule. A stall decision reads three signals:

1. The captain's output: its heartbeat, or its provider-progress time for a
   runtime that reports provider progress.
2. The newest write in the mission's dock worktree, excluding `.git`. A
   directory modification time counts, so a deleted file is a write.
3. The committer time of the mission branch tip in the vessel repository.

If any signal is inside the stall window, the captain is not stalled. The
autonomous recovery Mail nudge (window: `stallThresholdMinutes` times
`autonomousRecovery.stallMailNudgeThresholdRatio`) and the admiral
heartbeat-stall kill, restart or failure (window: `stallThresholdMinutes`)
call one shared evaluator. A runtime that streams nothing between tool calls is
therefore not nudged or restarted while its dock or branch keeps changing.

Each decision records an event that names the deciding signal and the
evidence:

- `captain.stall_confirmed`: no signal was inside the window. It is recorded
  for every confirmed decision, before the nudge, restart or failure.
- `captain.stall_cleared`: output was quiet, but a dock write or a branch
  commit was inside the window. It is recorded when a stall first clears or its
  clearing signal changes, then at most once per window while the same signal
  keeps clearing it. Every decision is also logged.

The dock scan reads at most 50,000 entries and stops at the first write inside
the window. The event says when the scan stopped at its bound, when directories
could not be read, when no dock is recorded, and when the branch tip is
unavailable.

### 4.6 Verify With Checks

Create Pending Checks when the objective or voyage is created. Build and unit
test are the minimum for code changes. Add the vessel-profile gates that the
change needs.

Armada runs at most ONE expensive command on a host at a time. Two full build
or test suites at once produce a burst of simultaneous sub-millisecond failures
across unrelated test classes, usually classified Timeout, which reads exactly
like a real regression - and the same command passes alone. A host-wide
interlock now serializes all four callers that run a vessel's build and test
commands: a check run, a pending check executed at the Judge stage, a
definition-of-done gate, and a merge-queue test run.

Two consequences for an operator:

- A check stays `Pending` while it waits for the host slot and changes to
  `Running` only after it acquires that slot. `queueDurationMs` measures the
  ready-and-wait interval. `durationMs` measures command execution. Compare the
  two values before you classify a slow check as a slow suite.
- The contended resource is the host, not the vessel, so checks on DIFFERENT
  vessels serialize against each other too.

The interlock covers commands Armada starts. It cannot see a captain running a
suite by hand inside its own dock, because that is a separate process, so a
dock-side suite can still overlap a gate.

It equally cannot see an OPERATOR running commands over SSH, and that matters
more than it sounds. A vessel whose profile provisions a sibling shares one
sibling directory across every check: each isolated check sandbox is private,
but they all reach the same parent path, and each run fetches and resets it.
The interlock makes that safe between checks, because only one executes at a
time. It does not make it safe against an operator who runs git against that
same directory while a check is executing - which re-points the sibling under a
running build.

The practical consequence is for DIAGNOSIS, not correctness. Reading a shared
sibling's state after a check has finished does not tell you what that check
compiled against: any later run has already moved it. To establish what a check
actually used, read the check's own output - the restored project paths name the
sibling - rather than inspecting the directory afterwards. And do not run git
against a shared sibling while checks are in flight; an out-of-band `reset
--hard` there is indistinguishable, later, from a stale sibling. When a wrong-value failure appears
under load, an isolated re-run remains the discriminator: a contention flake
passes alone, a genuine mismatch fails alone every time.

Use `run_check` to execute a check. Use `retry_check_run` for a real rerun.
Use `resolve_check` only when valid evidence was produced outside Armada. Do
not use it to hide a failure.

These tools return a bounded summary rather than the whole command log; section
8.7 gives the fields and how to fetch the full output when the tail is not
enough.

A passing suite proves only that the suite passed. It proves a fix only when
the check covers the original symptom. Record before and after evidence when
the task is a defect.

A Judge PASS is backed by the real signal, not by the Judge's own report. The
gate reads every Check attached to the voyage and to the Judge mission:

| Check state | Effect on a Judge PASS |
| --- | --- |
| All green | PASS stands |
| Any `Failed` | PASS is rejected |
| Any `Pending` or `Running` | PASS is held, then re-run in place |
| Armed at dispatch and not run yet, on a voyage with a commit under review | Queued work: stamped at the reviewed branch and commit, then PASS is held exactly as for `Pending` |
| `Passed` or `Failed` for a commit other than the reviewed tip | Stale: PASS is held exactly as for `Pending`; the executor cancels the record as superseded and arms a fresh one for the tip |
| None attached | PASS is rejected unless the review carries `[JUDGE-CHECK-EXCLUSION]` |
| `Canceled` | Ignored |

The table applies to voyages that contain implementation work. A fully
report-only voyage has no implementation Checks by design. Its Judge validates
the report structure and evidence, not a code diff or a green Build and
UnitTest pair. Do not use this rule for a mixed-mode voyage.

A green is a statement about one commit. A voyage-armed Check is stamped at the
FIRST stage that commits, and every later stage commits on top, so by the Judge
the only green record can describe a commit several stages back - in the worst
case a planner commit that never landed. The gate therefore compares each
`Passed` record's commit to the tip the Judge reviewed (the Judge mission's own
commit hash) and treats a mismatch as unresolved. The hold message names both
commits. While the PASS is held, the check executor supersedes the stale record:
it is set `Canceled` with a summary naming its successor, a fresh `Pending`
record of the same type is armed unless a queued, running, or tip-green sibling
already covers it, and a `check.superseded` event is written. The stale record
stays as history of what it measured; nothing is deleted or rewritten. A
`Failed` record for an older commit is stale in the same sense: the reviewed
commit may be the fix for it, so it holds rather than rejects, and the re-armed
record at the tip decides. A record that carries no commit at all is stale
when it is attached to a voyage (it measured the default branch) and is left
alone otherwise.

Two consequences follow, and both have cost real voyages.

Resolve EVERY failed Check, not most of them. When several Checks fail for one
environmental cause, resolving all but one leaves a record that rejects the
PASS hours later, long after the cause is forgotten. The rejection now names
the specific Checks that blocked it, so read the `FailureReason` and confirm no
record remains `Failed` before the Judge stage runs. Resolve an environmental
failure as `Canceled`, not `Passed`: the run genuinely did not pass, and the
reason field is where the evidence belongs.

Dispatch arms the voyage's Build and UnitTest Checks itself. A type is armed
only when the vessel's resolved workflow profile actually defines the command
for it, and a type already attached to the voyage is never armed twice - adding
a second Build beside a failed one would leave a green and a red on the same
voyage, and one failed Check rejects a Judge PASS however many green ones sit
next to it.

The armed Checks are `Pending`, not executed. An armed record becomes eligible
to run as soon as a stage has committed to a branch, and it is stamped with that
branch and commit before it executes, so it measures the work rather than the
vessel's default branch. Arming therefore costs nothing at dispatch and still
satisfies the real-signal gate; executing at dispatch would put a full suite on
the host at the moment the first captain starts work. An armed record reads
`command = echo` with no branch until that stamp - that is the correct armed
state, not a broken stub.

Once the voyage has a commit under review, an armed record is queued work, not
an absent Check. The executor queue can reach it after the Judge finishes, and
a voyage whose stages committed nothing new (for example a Judge-only
continuation) has no later commit to trigger the stamp. The Judge gate
therefore stamps every armed, never-run record with the branch and commit the
Judge reviewed, and holds the PASS until the record runs. The voyage completion
gate holds completion on the same record. Before any commit exists, an armed
record stays an inert marker and neither gate waits on it. One rule,
`CheckRunGateRules.ParticipatesInRealSignalGate(run, workCommit)`, decides this
for both gates. A held PASS still uses the bounded Judge wait budget; if the
queue is slower than that budget, the rejection names the unresolved Check ids.

Each sweep searches every stable page of Pending Armada Checks until it finds
its bounded execution set or reaches the end. A full first page of ineligible
records cannot hide an older eligible Check. If cancellation or a database
error stops the search, the warning names the page and the number of records
that the incomplete search scanned.

Arming never fails a dispatch. A voyage that exists without its Checks can
still be armed by hand, whereas refusing to dispatch over a Check record would
turn a convenience into an outage. Set `VoyageCheckArming.Enabled` to `false`
to switch it off, or `ArmBuild` / `ArmUnitTest` / `ArmSlop` to control the
types.

#### The Slop Check

On a .NET vessel, dispatch also arms a `Slop` Check beside Build and UnitTest,
on every dispatch path (operator dispatch, the objective scheduler, and
recovery). It catches reward hacking: a change that makes a build or suite go
green without fixing the problem. The captain runtime does not matter, because
the Check runs in the admiral, not in the captain.

A vessel is .NET when a Build, UnitTest, Lint or IntegrationTest profile
command invokes `dotnet` or `msbuild`, or when the working directory (or one
directory below it) holds a `.sln`, `.slnx`, project file,
`Directory.Build.props`, `Directory.Packages.props` or `global.json`. The
arming log line `slop_not_armed` names why a voyage got no Slop Check. Slop is
never armed alone: a voyage with no Build or UnitTest Check gets no Slop Check
either, because a Slop green says nothing about whether the code compiles.

The Check needs no profile command and no external tool. Armada's own
classifier reads the reviewed diff from the vessel repository: the merge base
of the default branch and the stamped commit, compared with that commit. It
classifies only ADDED lines in C# and MSBuild files outside `bin` and `obj`.
The armed record is stamped, superseded and gated exactly like Build and
UnitTest. Once it runs, its command reads
`armada slop-classifier (native, reviewed diff)`.

| Rule | Severity | Added line that matches |
| --- | --- | --- |
| `SkippedTest` | FAIL | A `Skip =` argument on a test attribute, an `Ignore` attribute, `Assert.Skip` / `Assert.Ignore` / `Assert.Inconclusive` / `Skip.If`, or `#if false` in a file under a test path |
| `ProjectWideNoWarn` | FAIL | A `<NoWarn>` element in a project, props or targets file (a per-package `NoWarn` attribute is not flagged) |
| `CentralPackageVersionBypass` | FAIL | An inline `Version`, a `VersionOverride`, or `ManagePackageVersionsCentrally` set to false, when `Directory.Packages.props` at the reviewed commit enables central package management |
| `EmptyCatch` | WARN | A catch block whose body is empty or holds only a comment |
| `ArbitraryDelay` | WARN | `Task.Delay` or `Thread.Sleep` with a literal duration |
| `WarningSuppression` | WARN | `#pragma warning disable` or a `SuppressMessage` attribute |

An unsuppressed FAIL finding fails the Check, which rejects a Judge PASS like
any failed Check. WARN findings never fail it. They appear in the Check output
(`WARN <Rule> <path>:<line>`) and in the summary, which the
`check.auto_passed` event carries onto the voyage. The WARN patterns are the
ones a byte-exact source reproduction can legitimately contain, so a reviewer
reads them rather than a gate refusing them.

A finding is suppressed per site by a marker in a comment on the flagged line
or on the line directly above it:

```csharp
// slop-allow EmptyCatch: byte-exact reproduction of Decoder.cs:40-44
```

The marker must name the rule and record a reason of at least eight
characters. A marker with no reason, or one naming another rule, is not
honored, and the output says why. Suppressed findings stay in the output with
their recorded reason. Use a marker for an intentional source reproduction or a
pinned source defect, and record the owner decision on the objective as well.

Every condition that prevents classification fails the Check with a reason
that ends `Nothing was examined`: no commit or branch, no repository, git that
cannot start, an unresolvable commit or default branch, or no merge base. None
of them passes. An empty reviewed diff (the commit is already on the default
branch) passes, and the output says that no lines were classified.

Operators may still attach further Checks, and must do so for any gate beyond
build and unit test. What changed is the floor: a voyage no longer reaches its
Judge stage carrying nothing, and a re-dispatch after a cancellation no longer
starts bare because the previous voyage's Checks went with it.

The definition-of-done gate also builds the vessels that DECLARE this vessel as
a sibling repository. A producer's own build cannot observe a break it causes
in a consumer, because the consumer is a different repository with a different
compilation: the producer's gate passes, the branch lands, and the break
surfaces on whatever builds next, attributed to that build rather than to the
change that caused it.

The consumer edge is derived, not configured. A vessel declares the
repositories it depends ON, in `SiblingRepos`; the gate reads that same data in
the opposite direction to find who depends on IT. Nothing extra needs to be set
up for a vessel whose consumers already declare it.

What the gate does for each consumer:

| Step | Behavior |
| --- | --- |
| Provision | A scratch root private to this one verification, never a shared sibling path |
| Producer ref | The mission branch, checked out detached |
| Other siblings | Their declared default branches - only the producer is under test |
| Command | The consumer's `BuildCommand`; then its `UnitTestCommand` when the change reaches a triggering path (see below) |
| Cleanup | Worktrees removed and the scratch root deleted, pass or fail |

The private scratch root is load-bearing. A shared sibling checkout that another
dock already owns is REUSED rather than re-pointed, so verifying through one
could compile the consumer against some other commit while reporting on this
one - the exact false green the step exists to prevent.

A build catches the break that leaves a consumer red at compile time. It cannot
catch a break that still compiles and fails at runtime - an empty catalogue, a
reordered public shape a test oracle pins, a changed frame - which lands green
through a build-only consumer step and surfaces in the consumer's next voyage.
So the gate also RUNS the consumer's `UnitTestCommand`, in the same provisioned
worktree, when the producer change can break the consumer's behavior.

"Can break the consumer" is decided from the producer's own diff against its
default branch. The consumer suite runs when a changed NON-TEST file falls under
a triggering path prefix. The prefixes come from the producer's sibling
declaration on the consumer (`ConsumerTestTriggerPaths`) when it lists any,
otherwise from `DefinitionOfDone.ConsumerTestTriggerPaths` (default: the
protocol-library source root). A change to a test project, to documentation, or
outside every prefix builds the consumer but does not run its suite, so the
producer only pays for the consumer suite on the changes that matter. A path is
a test path when a segment is a test project (`*.Tests`, `*.Test`) or a
`test`/`tests` directory.

A failing consumer suite fails the producer's gate with the named reason
`consumer_tests_failed: <consumer>`, which is distinct from a consumer build
failure (`consumer-build (<consumer>)`) so the two are never confused. Set
`DefinitionOfDone.RunConsumerTests` to `false` to keep the build-only step and
run no consumer suite.

A consumer that fails to COMPILE fails the producer's gate. A consumer that
cannot be PREPARED - no workflow profile, no `LocalPath`, a worktree that will
not provision - is an infrastructure fault in the verification rather than
evidence about the producer's change, so by default it is logged and the gate
passes. Set `DefinitionOfDone.FailOnConsumerVerificationError` to make those
fail instead. Set `DefinitionOfDone.VerifyDeclaredConsumers` to `false` to
switch the step off entirely.

### 4.7 Review And Land

Read the mission diff and relevant logs. Drain the audit queue and record the
audit verdict when needed. Check the merge entry before processing it.

A Judge `NEEDS_REVISION` always creates a durable Judge follow-up. Any other
Judge verdict creates one when it has a non-empty Suggested Follow-ups section.
Armada stores this item before it looks for a merge entry. The audit queue can
therefore return the item with no `entryId`.
Record its verdict with `followUpId`. Armada associates a later merge entry and
mirrors the audit result when delivery metadata becomes available. A linked
follow-up and merge entry appear as one queue item. The older `entryId` verdict
form resolves the linked canonical follow-up and remains safe to use.

If a transient write fault predates this durable capture path, run
`armada_backfill_judge_followups` with an explicit `fromUtc` and optional
`toUtc`. Start with `dryRun: true`, check `incomplete` and `errors`, then run
the same bounded range with `dryRun: false`. A second write pass must report
zero `created` rows. The repair records explicit `(none)` sections as durable
reconciliation evidence with an empty recommendation. The current prompt uses
the exact `## Suggested Follow-ups` heading. Repair also accepts anchored
legacy `Suggested`, `Recommended`, and `Tracked` follow-up labels, including
bold, list-prefixed, qualified, and inline forms. It does not treat a prose
mention as a section.

Use `armada_process_merge_entry` for one reviewed entry. Use
`armada_process_merge_queue` only when the operator intends to start queue
processing. It returns an accepted job and can no-op when a queue run is
already active. Poll the job and merge entry.

Landing behavior comes from the effective landing mode:

| Mode | Result |
| --- | --- |
| `LocalMerge` | Merge into the configured working checkout. Do not push unless separate policy permits it. |
| `PullRequest` | Create or update provider review state. |
| `MergeQueue` | Use the durable integration, test, and landing state machine. |
| `None` | Leave produced work for explicit operator handling. |

`LocalMerge` merges in a temporary integration worktree under
`<docks>/_integration/<mission>`. That worktree is created with a detached HEAD
at the target branch tip, so another worktree that has the target branch
checked out (for example one left in the landing repository by a captain) does
not block the landing. After the merge, the target branch is advanced by a
compare-and-swap (`git update-ref refs/heads/<target> <merged> <tip>`). If the
target moved during the merge, git refuses the update, and the landing retries
as target-branch drift. A worktree that still holds the target branch keeps
its old checkout: its index then shows the landed changes as reverted, so
inspect such a worktree before you remove it.

When git refuses an integration step, the mission `FailureReason` carries git's
message with a failure class. `worktree_conflict:` names the worktree that
holds the needed ref. `integration_merge_failed:` covers every other refusal,
such as a content conflict. Read the class before you retry.

Do not infer successful landing from a `Complete` label alone. Verify the
target branch or remote commit that should contain the work.

Merge-queue landings need extra verification, because the entry status can
mislead:

1. `armada_enqueue_merge` moves the source branch into a
   `refs/heads/armada/merge-queue/<id>` ref. If `armada_process_merge_entry`
   later reports the branch was not found, it may mean the landing succeeded
   (only the original name is gone) or that it failed. Verify with
   `git merge-base --is-ancestor <sha> <target>` in the vessel bare repo
   before re-enqueueing, and restore the branch from the mission commit
   hash when it is genuinely gone.
2. Land entries for the same vessel+target one at a time. Concurrent
   processing of the same target collides on the push and both fail with a
   non-fast-forward rejection.
3. After a batch of landings, confirm the pre-batch target tip is still an
   ancestor; a landing rebuilt from an older base can silently drop sibling
   commits that sat at the previous tip. Cherry-pick any dropped commit
   back.
4. After a direct push from the working checkout, sync the vessel bare repo
   (`git fetch origin` then `git update-ref refs/heads/<target>
   refs/remotes/origin/<target>`) so later Checks build the new code.

When a landing retry fails, the mission's `FailureReason` records the
conflicted-file list (`git diff --name-only --diff-filter=U`) so the operator
sees exactly which paths to fix without re-deriving the merge state. Read the
mission's failure reason before deciding the recovery path.

#### Operator branch push and merge

A tenant administrator can push or merge vessel branches from the vessel page
or through `POST /api/v1/vessels/{id}/branches/push` and `.../merge`. There is
no MCP tool for these writes. They act on the landing repository (the vessel
`LocalPath`), not on a dock:

- A merge moves only a local ref. It never pushes. A push is a separate,
  explicit request naming source, target and `origin`.
- Both refuse, with a named reason and no ref change, when the working checkout
  is dirty or detached. They also refuse when the source is the branch of a
  mission that is not `Complete` or has an active merge-queue entry, and when
  the target is protected or is a release branch that must use the merge queue.
  Mission work still lands through its review and Check gates.
- Pushes go only to `origin`, and only when its URL matches the vessel
  `RepoUrl`. They never force or delete. A remote tip that is not an ancestor of
  the source is refused as `non_fast_forward`.
- Writes share the per-vessel slot with mission landing and merge-queue entry
  processing. A write that finds the slot held returns `vessel_busy`. Retry it
  after the landing finishes.
- A successful write emits `vessel.branch_pushed` or `vessel.branch_merged`
  with the verified commit. After a merge, read `WorkingCheckoutSync`. A value
  other than `fast_forwarded` or `skipped_on_other_branch` means the working
  checkout did not follow the landing repository.

### 4.8 Close The Record Chain

Before the objective becomes complete:

1. Link the final voyage and missions.
2. Link the passing Checks.
3. Link a release and deployment when work shipped.
4. Link incidents and their final evidence.
5. Create a new record for every deferred task.
6. Update the objective summary with the verified outcome.

### 4.9 Sweep The Papercuts

Captains report friction they meet on an `[ARMADA:PAPERCUT]` line: a stale
document, a dead link, a brief that contradicts itself, a missing sibling
repository, a test that fails under load. Armada stores each report as a
`papercut` event with the reporting mission, captain, vessel, and voyage.

Read them on a schedule. A report that nobody reads is worse than no report:
the captain paid to write it and the next captain still pays the same cost.

1. Run `armada_list_papercuts` after a voyage closes, and again in the weekly
   sweep with `sinceHours: 168`.
2. Read the count and the distinct-captain count first. One captain reporting
   a problem is an anecdote. Several captains reporting it is a defect with
   evidence.
3. Route the group by category:

   | Category | Owner |
   | --- | --- |
   | `MissingDoc`, `BrokenLink`, `RepoFriction`, `TestFlake` | Backlog item on that vessel |
   | `EnvSetup` | Dock or workflow-profile fix, then a Check to prove it |
   | `BriefContradiction`, `PlatformBug` | Armada objective, direct-edit only |
   | `ToolFailure` | Read the mission log before you accept it; a captain calling a tool it never received is a `BriefContradiction` |

4. Quote the group in the record you create: the count, the distinct-captain
   count, the sample title, and the sample mission IDs. Those missions are the
   evidence.
5. Keep the promotion manual. A high count is not authority to dispatch.

Two signals need a different response than a repository fix:

- **A `BriefContradiction` group is a captain-quality defect, not a vessel
  defect.** It means the brief asks for something the captain cannot do. Fix
  the instruction module, not the repository.
- **A category that one runtime reports and no other runtime reports** is
  usually about that runtime, not about the vessel. Compare the reports before
  you change vessel code.

Judge missions do not file papercuts. A judge reports what it finds through
its verdict, and splitting review feedback across two surfaces means the
operator reads only one of them.

### 4.10 Campaign Work

A campaign is an opt-in objective tree for one large effort: a root tagged
`campaign:<name>`, lane children per source or area, and slices beneath the
lanes. Plain objectives outside any campaign remain the default; do not force
ordinary work into one.

The autonomous scheduler can use optional campaign fair-share. Set
`fairShareWithinPriorityBands=true` with `armada_objective_scheduler_set`. The
setting is false by default. It never moves a lower-priority slice ahead of a
higher-priority slice. In one priority band, it rotates across tagged campaign
roots and keeps rank and ID order inside each campaign. Plain objectives remain
in one default group. Scheduler status shows both the setting and the
process-local last-served cursor.

Operating rules:

1. Claim the slice (`armada_coordination_claim`) before starting; heartbeat
   while working; release when done.
2. Encode wave ordering with BlockedByObjectiveIds, not prose.
3. Attach the campaign's rules playbook (for porting:
   `porting-campaign-rules`) through selectedPlaybooks on dispatch.
4. On landing, link EvidenceLinks - commit SHAs, green Checks - and the
   source-glossary entry the slice extended. No evidence links means not done.
5. Answer "where does this stand" with `armada_campaign_status`, not ten
   enumerations.

### 4.11 Helper Sessions And Operator Ownership

An operator session that starts host-side helpers owns their complete
lifecycle. The autonomous objective scheduler selects ready objectives and
dispatches captains inside Armada. The standalone lead cycle and its external
launcher are retired. Do not restore them through an external timer or
AgentWake registration. Generic operator wakes and bounded helpers remain.

Use `scripts/autonomy/spawn-helper.sh` for bounded host-side helpers:

```bash
scripts/autonomy/spawn-helper.sh spawn census /tmp/census-task.md /path/to/repo
scripts/autonomy/spawn-helper.sh offer ready /tmp/fallback-task.md operator-session /path/to/repo
scripts/autonomy/spawn-helper.sh list
scripts/autonomy/spawn-helper.sh kill census
scripts/autonomy/spawn-helper.sh cull
```

The launcher enforces `AUTONOMY_MAX_HELPERS` (default 2), records PIDs and
participant keys under `AUTONOMY_WORKDIR`, and culls sessions older than
`AUTONOMY_HELPER_TIMEOUT_MIN` (default 90). It supports `opencode`, `claude`,
and `codex`; `AUTONOMY_RUNTIME=command` plus `AUTONOMY_COMMAND` is the local
test adapter. Every prompt receives a fixed contract: use the generated
participant key, drain and acknowledge addressed wakes, stay read-only, post
one outcome, release claims, and exit. Run
`scripts/autonomy/test-spawn-helper.sh` after launcher changes.

`offer` mode posts availability to the named operator and gives it a bounded
four-minute reassignment window. The helper checks for directed Wakes at most
every 25 seconds during that window. It then runs the fallback, accepts the
operator's replacement task, or stands down. `list` shows each helper's mode and
lead key. See `docs/autonomy/helper-offer-prompt.md` for the manual-session
equivalent.

Claude helpers run with strict MCP isolation. The launcher therefore writes a
private Armada-only MCP file and passes it with `--mcp-config`. The default URL
is `http://127.0.0.1:7891/mcp`; override it with
`AUTONOMY_ARMADA_MCP_URL`, or supply an existing file through
`AUTONOMY_CLAUDE_MCP_CONFIG`. Strict mode without the explicit file gives the
helper zero Armada tools and makes the board contract impossible.

The helper's working directory is also its file-sandbox boundary. Give it the
narrowest directory that contains all required evidence. Use a common ancestor
when one task must inspect a checkout, a bare repository, or a sibling. Do not
disable the sandbox to repair a bad working-directory choice.

Three sets from the coordination board define the roster: participants (who is
present and when last seen), active claims (who holds work), and their
difference (a participant present without a claim is idle).

Lead duties:

- Hand a live helper work with an addressed note (`toParticipantKey`) naming
  the task, vessel or objective, and constraints. The note always writes a
  Wake signal, so its next heartbeat delivers the full payload.
- Or stand the helper down explicitly ("stand down, nothing available").
  A script-managed helper must then exit; it must not wait in a polling loop.
- Re-check the roster between loop iterations, not only at session start.

Do not register a script-managed helper for AgentWake. That creates two process
owners for one participant key and can duplicate work. Use one model:

- A bounded script-managed helper handles its initial task and any wake already
  waiting at a tool boundary, reports, and exits.
- An AgentWake process owner has no resident process. Put its stable key in
  `remoteTrigger.agentWake.participantKey` when addressed wakes must survive an
  Admiral restart. A transient registration can override that key for a
  controlled probe until the next restart.

One process owns one participant key. OpenCode AgentWake sessions are always
fresh, so the addressed note must contain the complete task and the bootstrap
prompt must tell the session to reconstruct context from the board and durable
memory. Each wake must carry the task and its limits.

## 5. Recovery And Incident Workflow

Objective closeout evaluates each failed chain in an original voyage. Every
independent `Failed` or `LandingFailed` chain root needs a linked automatic
rescue whose missions all reached `Complete`. A completed rescue for one branch
does not cover a separate failed branch. Failed rescue attempts stay as history
and do not become new closeout obligations; a later completed attempt can
resolve the original failure. A missing linked voyage, a malformed mission
graph, unlanded work, or a cancelled-only original voyage keeps the objective
open.

When autonomous recovery is enabled, Armada classifies failed missions,
creates or updates an incident, records a recovery runbook execution, and can
dispatch a bounded rescue mission. It does not use generic rescue missions for
landing failures. Authentication, quota, review, protected-path, dependency,
and exhausted-recovery failures remain for operator action. While the dispatch
hold is engaged, a rescue is deferred and named on the incident, not
dispatched (section 8.18).

The recovery incident is linked to each objective that already owns the failed
mission or its voyage. This annotation preserves the objective's existing
mission and voyage lineage. It does not add unrelated incident context to the
objective or change its lifecycle state.

Audit and Research failures remain read-only. Recovery records the mission
mode and audit-only scope in the incident, runbook execution, and event, then
stops without an Implementation rescue. A failed Judge also creates one
durable Judge follow-up for its recommendation. Other review stages, including
PortingReferenceAnalyst and TestEngineer, do not create Judge follow-ups. The
system updates an older recovery runbook in place to add the required
`missionMode` parameter and preserves its existing content.

Use this order for manual diagnosis:

1. Read the complete mission and captain logs.
2. Use `armada_captain_diagnostics`.
3. Compare with a known-good mission on the same runtime.
4. Separate provider, host, repository, test, and model failures.
5. Create or update the incident before repeated intervention.
6. Cancel only the exact mission or voyage that must stop.
7. Restart or retry only after the cause is understood.

A mitigation acknowledges failure evidence at or before its recorded time.
Lifecycle reconciliation does not reopen a mitigated or monitoring incident
from that same old failure when both timestamps are available. A newer failure
can reopen it.

Incident closure is evidence-driven. Produce a newer passing check, successful
rescue, shipped release, verified deployment, or completed rollback. Do not
close an incident only because a captain reported success.

### A rescue of a stage inside a voyage re-enters review

The rescue root first uses the failed mission's produced commit. If no commit
was produced, Armada keeps the mission's original `StartFromRef`. If both are
absent, Armada can use the immediate dependency commit only when both missions
use the same vessel. Armada resolves this ref before provisioning and proves
that the new checkout contains it before the captain starts. A missing
ref, a false ancestry result, or an ancestry result that Armada cannot verify
fails closed as a provisioning fault. If a reviewer rescue has no captured
commit, original start ref, or same-vessel dependency commit, Armada leaves the
incident open and does not start a Worker from the vessel default branch. Later
rescue stages inherit the verified Worker branch.

A Worker that fails its gate inside a voyage has already cost that voyage its
TestEngineer and Judge: the pipeline cancels them as blocked dependents when the
Worker fails. Its rescue is therefore dispatched as a rescue VOYAGE — the Worker
revision, then a TestEngineer where the vessel pipeline defines one, then a
Judge — exactly as a Judge rejection is, and with Build and UnitTest Checks
armed. Before this rule a Worker gate failure produced a STANDALONE rescue
mission that passed its own gate and landed through `LocalMerge` with no
reviewer ever reading the final code. A standalone mission that has no voyage
never had review stages and keeps a standalone rescue.

### A captain's first terminal marker ends its stage

A captain that prints its terminal marker and keeps its process running has
finished. Before this rule it looked exactly like a captain still working,
because Armada read the verdict only at process exit. The stall nudge then
asked the finished reviewer to continue, and each nudge started a full new
review.

The first `[ARMADA:VERDICT] PASS|FAIL|NEEDS_REVISION` or
`[ARMADA:RESULT] COMPLETE` line in the streamed output is now recorded. If the
process has not exited `autonomousRecovery.terminalMarkerGraceSeconds` after
that line (default 60, clamped to 5-3600), the lifecycle handler stops it and
records `captain.terminal_marker_stop`. The handler owns that stop, so the
exit completes the stage from the recorded output as a clean exit, whatever
exit code the stop produced.

While the marker is recorded, the recovery sweep sends no stall Mail nudge to
that mission. It logs and counts every withheld nudge and records one
`autonomous_recovery.mail_nudge_suppressed` event that names the marker.
The recorded Judge verdict is the first canonical `[ARMADA:VERDICT]` line. A
later verdict from a re-review cannot replace it. Output with no canonical
line falls back to the runtime's `[verdict]` echo or a labelled verdict.

### A completion is de-duplicated per launch

The process-exit callback and the health check can both report the same
agent exit. The completion handler therefore skips a repeat completion for
the same launch of a mission for 30 seconds after it handled the first one.

A launch is the mission's start time plus its agent process. A completion for
a later launch is processed even inside the 30 seconds. That holds for every
path that returns a mission for another attempt, because each one ends in a
new launch:

| Path | Where it requeues | Guard |
| --- | --- | --- |
| Judge PASS held while Checks resolve | completion handler, in-place re-run | released by the next launch |
| Judge exited with no verdict | completion handler, in-place re-run | released by the next launch |
| Refusal in the captain output | completion handler, continuation on another runtime | released by the next launch |
| Provider safeguard block on exit | process-exit failure, continuation | released by the next launch |
| Probable resource kill or captain unavailable | process-exit failure, transient requeue | released by the next launch |
| Provider quota, credit or spend limit | process-exit failure, re-route | released by the next launch |
| Operator restart | restart of a Failed, Cancelled or LandingFailed mission | released by the next launch |
| Review denied with retry | review decision | released by the next launch |
| Merge failure routed to redispatch | merge recovery | released by the next launch |
| Captain process gone | stale-captain cleanup | released by the next launch |
| Operator transition from WaitingForInput to Pending (REST, WebSocket or MCP) | shared status transition service | released by the next launch |
| Stall recovery | relaunch in place (same start time, new process) | released by the new process |

Assignment rollbacks (Assigned back to Pending when a launch fails) and the
orphan reset of a mission that never started a process return a mission that
was never launched, so no completion for it exists to de-duplicate.

A completion for a requeued mission that has not been launched again is a
late duplicate and is still skipped. Look for the handler's log line naming
the new launch when a completion inside the window is processed.

### A rescue brief keeps the reviewer's instructions, not only its diagnosis

The rescue brief embeds the failed mission's reviewer feedback under a size cap.
The cap exists because an uncapped gate log once tripped a provider's content
filter and opened a self-perpetuating loop; it stays. What changed is the shape
of the cut. A Judge report is written diagnosis first and instructions last
(Completeness, Correctness, Tests, Failure Modes, Suggested Follow-ups,
Verdict, then the `[ARMADA:VERDICT]` line), so a head-first cut kept the table
of what failed and dropped every line that said what to do. Four rescues in one
shift each needed an operator to re-send the tail by Mail.

An over-cap Judge report now keeps `## Suggested Follow-ups`, `## Verdict` and
the verdict line whole, fills the remaining budget from the head, and places
the truncation marker where the omitted middle was, naming the sections it
dropped. An under-cap report is embedded whole. Text without those headers, such
as a gate log, keeps the head-first cut, because its signal is at the top. When
a rescue brief still reads `truncated`, the marker names what is missing; the
full report remains on the failed mission's `ReviewComment`.

### A rescue is judged by what it changed

A rescue that starts, logs, and exits satisfies every liveness measure Armada
keeps: the process lived, the captain reported, the pipeline advanced. None of
that says the defect was touched. One rescue ran for twenty-four hours, drew
escalating stall nudges, died on a runtime crash, and left behind a single
changed documentation file.

An Implementation-mode rescue whose change set is empty, or consists only of
documentation, now fails with `ineffective_rescue` and the change set named in
the `FailureReason`. Read that field before retrying: the correct response is a
replacement brief that quotes the defect, not another attempt at the same one.

Three limits keep this from firing on correct work:

- **Only rescues are assessed.** A first-attempt mission may legitimately have
  been dispatched to write documentation.
- **Only Implementation mode.** An Audit or Research mission delivers a report
  and is never expected to change code. Judging those by a diff is the same
  mistake in the other direction, and it once marked correct work Failed.
- **The objective kind declares the deliverable.** A `Research` objective is
  report-only, so its rescue is not assessed. A `Chore` objective delivers a
  committed document, such as a report under `docs/audits/`, a
  `discoveries.d` record or a census. Its rescue is effective when it commits
  any change, documentation included, and ineffective only when it commits
  nothing. `Feature`, `Bug`, `Refactor`, `Initiative` and rescues with no
  linked objective owe a change that can carry behavior. A committed-document
  deliverable must therefore be filed as `Chore`, not as a code kind.
- **The original mission's paths are NOT compared against.** A rescue is
  expected to rewrite the prior branch from scratch over the same files, so
  treating an overlapping path set as a no-op would flag the normal case.

Documentation means a `.md`, `.txt`, `.rst`, or `.adoc` file, anything under a
`docs/` directory at any depth, or a conventional bare name such as `README` or
`LICENSE`. A directory that merely contains the letters "doc" - `docker/`, a
`DocumentStore/` source tree - is not documentation.

## 6. Release And Deployment Workflow

Use a workflow profile for repeatable commands. Use named environments for
rollout targets.

1. Create a release and link its objective, voyages, missions, artifacts, and
   Checks.
2. Move the release through its supported states only when evidence permits.
3. When a release transitions to Shipped and the `cdWebhook` setting is
   configured, the admiral POSTs a `release.shipped` evidence payload to the
   configured endpoint; delivery or failure is recorded as a
   `release.webhook.*` event on the release. A webhook failure never blocks the
   release update.
4. Create a deployment against a named environment.
5. Approve it when the environment requires approval.
6. Run deployment verification.
7. Use rollback on the same deployment record if verification fails.
8. Link the related incident and runbook execution.

See [DELIVERY_OPERATIONS.md](DELIVERY_OPERATIONS.md) for the detailed procedure.

### Self-deploy preflight

Self-deploy is opt-in (`selfDeploy.enabled` defaults to `false`). After a
successful Release build, the service runs an
injected safety preflight before it prepares the supervised cutover. The preflight
must prove all three conditions: a recoverable backup was created and
validated, restore verification passed, and the candidate server was validated.
If any condition fails, or if the provider returns no result or throws, the
service records the failure, opens an incident, and keeps the current admiral
running.

The native backup provider now supports SQLite, MySQL, PostgreSQL, and SQL
Server. It creates a unique artifact and restores it into a unique isolated
file or database before it reports backup and restore proof. Native utility
passwords are passed through environment variables and are never command
arguments. SQL Server backup paths must be visible to the SQL Server host.
The isolated target stays available for the candidate check and is removed
through the provider cleanup operation after that check. Cleanup requires the
opaque ownership token returned by the provider, so an arbitrary file or
database name cannot be deleted. The backup artifact is kept for rollback.
MySQL requires every source table to use InnoDB for the single-transaction
snapshot contract. MySQL, PostgreSQL, and SQL Server also require their native
client utilities (`mysqldump`/`mysql`, `pg_dump`/`createdb`/`pg_restore`/`psql`,
and ODBC 18 `sqlcmd`) on the executing host. SQL Server utility calls set `-Nm`
or `-No` from `DatabaseSettings.RequireEncryption` and pass `-C` explicitly to
match the typed server-certificate trust contract; a wrapper must not weaken
TLS checks.

The native preflight is the default for every cutover. It runs against the
running admiral's database and writes disposable backups under
`<dataDirectory>/self-deploy/backups`. SQL Server also needs
`selfDeploy.sqlServerBackupDirectory`, a path visible to the SQL Server host;
without it the preflight fails with
`sqlserver_server_backup_directory_not_configured`. Any failed step, missing
native utility or unverifiable private storage refuses the cutover before any
process starts or restart record is written. `SelfDeployCandidateProcessValidator`
writes temporary settings with the effective connection fields and the owned
isolated target, then runs the candidate DLL with `--validate-database`. It
requires both a zero process exit and the validation pass marker before it
reports candidate proof. Candidate validation inspects the isolated restored
copy only; the running database and migration history are not modified by this
provider. A build success or a backup proof alone never permits cutover.
On Unix, the operation directory and settings file are created with owner-only
permissions (`0700` and `0600`); an existing directory with public permission
bits or a symlink is rejected. On Windows, private storage uses owner-only ACLs: each new directory and file
gets a protected descriptor (inheritance removed) owned by the current user,
with one rule granting that user full control, inherited by children. SYSTEM
and Administrators are not granted. The descriptor is read back after every
change; anything else fails closed with `private_storage_acl_unverified`
(`private_storage_acl_apply_failed` when it cannot be applied). An existing
directory is verified, never modified. A path name or temp-root location is
not treated as proof. The rollback artifact remains
inside that private directory. The native runner bounds each captured output
stream and observes every pipe task after a bounded timeout. It closes standard
input only when the request redirected it. A truncation marker fails candidate
proof, so a noisy command cannot hide its validation result; an inherited child
pipe returns the stable `native_command_io_drain_timeout` failure.
The Release build uses this same runner with an argument list and a configured
build timeout. Caller cancellation and timeout both terminate the process tree
and observe the redirected pipes before the build result is returned; a build
that cannot be terminated or drained fails closed.

### Self-deploy supervised cutover

Self-deploy restarts only a process-owned admiral. Inside a container the
container runtime owns the admiral process, so self-deploy fails closed with
`container_host_requires_external_deploy` and the host-side image deployment
remains the only deploy path there.

The running admiral performs these steps, and any failure opens an incident
and keeps it as the owner:

1. Capture the running server directory as the rollback artifact before the
   build, because the build may overwrite that directory.
2. Build, then run the safety preflight.
3. Capture the candidate build directory as the candidate artifact.
4. Read the schema version and its own identity (process id and start time).
5. Create the restart record in `Prepared`. A new record is refused while an
   unresolved or unreadable record exists (`restart_in_progress`).
6. Start the supervisor from the rollback artifact with
   `--self-deploy-supervise <operation>` and wait up to
   `selfDeploy.handshakeTimeoutSeconds` for `Armed`. If it does not arm, the
   admiral aborts the record and stops the supervisor by identity.
7. Write `ExitRequested` and exit.

Artifacts live under `<dataDirectory>/self-deploy/releases/<sha256>`. The
digest covers every relative path, size and content hash. Files are read-only,
symlinks are refused, and each artifact is re-verified before every launch.
The restart record is `<dataDirectory>/self-deploy/restart-record.json`. Every
change is a compare-and-swap under an exclusive record lock, written to a
flushed temporary file and renamed into place. A separate supervisor lock
allows one supervisor or recovery run at a time.

The supervisor verifies both artifacts and the recorded admiral identity, then
writes `Armed`. It waits for `ExitRequested`, then waits up to
`selfDeploy.oldProcessExitTimeoutSeconds` for that exact process to exit. An
admiral that does not exit is terminated by identity; its descendants are not.
A reused process id counts as exited and is never signalled. If exit cannot be
confirmed, or the process state cannot be verified, the record fails and
nothing is launched.

Launches are recorded before and after they happen (`CandidateStarting` with
and without the process identity). Health requires
`GET http://127.0.0.1:<admiralPort>/api/v1/status/health` to report `healthy`
with a `StartUtc` no earlier than the launched process, within
`selfDeploy.healthTimeoutSeconds`. The result is one of:

- The candidate is healthy: `Committed`.
- The candidate exits or stays unhealthy: the supervisor stops it, confirms
  the exit, rereads the schema version and launches the rollback artifact,
  giving `RolledBack` or `Failed`.
- The candidate advanced the schema, or the schema version cannot be read: the
  previous binary is not started, giving `RollbackBlocked`. Restore the
  retained preflight backup before starting the previous binary; writes made
  after the cutover are lost by that restore.

While a record is non-terminal, a normal admiral start exits with code 3 and
names the record. Only the process the supervisor launched for that operation
may start (`ARMADA_SELF_DEPLOY_OPERATION_ID`). After a supervisor or host
interruption, run the server with `--self-deploy-recover`:

- If the recorded admiral still runs before any stop, the record aborts and
  that admiral stays the owner.
- If the interruption came before a candidate launch, the rollback artifact is
  launched. Recovery never launches the candidate.
- A running candidate is committed only if it proves health; otherwise it is
  stopped and rolled back.
- A launch whose identity was not recorded, two running recorded processes, or
  an unverifiable process state all fail without starting anything.

Exit code 0 means the record proves a healthy owner or no record exists.

The release store is bounded on every cutover, after the candidate capture. It
keeps:

- the running release and the rollback release;
- every release named by an unresolved restart record;
- the newest `selfDeploy.retainedPreviousReleases` other releases (default 2,
  range 0 to 20).

Pruning holds the record lock and removes nothing when the record is
unreadable. It ignores entries that are not digest directories and never
follows a symlink. A pruning failure is reported as
`self_deploy.release_prune_failed`, and it does not block the cutover.

Current limits: supervised processes inherit the supervisor's standard streams.
The Windows ACL path has a Windows-only test that has not yet been run on a
Windows host.

### Self-deploy rehearsal

`scripts/common/rehearse-self-deploy-cutover.sh` rehearses the supervised cutover
on an isolated process host. It uses real server binaries and disposable
SQLite copies. Run it only on a workstation or disposable host, never against a
production data directory, database or container host. It refuses to run
inside a container.

```bash
scripts/common/rehearse-self-deploy-cutover.sh \
  --rollback-dll <build>/Armada.Server.dll \
  --candidate-dll <candidate build>/Armada.Server.dll \
  --sqlite-source <copy of a real database>.db
```

Each scenario gets its own private data directory, database copy and free
ports. The script starts the rollback binary with
`--self-deploy-rehearse <candidate dll>`. That mode is refused unless
`ARMADA_SELF_DEPLOY_REHEARSAL=isolated-disposable` and `ARMADA_DATA_DIRECTORY`
are both set. It runs the real cutover (container check, rollback capture,
native preflight, candidate capture, retention, restart record and supervisor
handshake) and skips only the git sync and the Release build. The script
requires `dotnet`, `python3` and `curl`.

1. **Preflight refusal.** The candidate is not an assembly. The rehearsing
   admiral prints `candidate_database_validation_failed` and exits 1, and no
   restart record exists.
2. **Commit.** The record ends `Committed`, the previous admiral exits 0, and
   the candidate answers health.
3. **Supervisor kill.** `ARMADA_SELF_DEPLOY_REHEARSAL_HOLD_SECONDS` holds the
   supervisor after the candidate launch is recorded. The script sends
   `kill -9` to the supervisor while the record reads `CandidateStarting`. A
   normal start then exits 3 with `restart_in_progress`. `--self-deploy-recover`
   exits 0 at `Committed` or `RolledBack`, with exactly one recorded owner
   running and healthy.

The script stops every recorded process on exit. It removes the work
directory after success and keeps it after a failure. Scenarios the unit suite
covers with real processes, but not with the server binary, are not rehearsed
here:

- an unhealthy candidate rolled back after health;
- a schema advance blocking rollback;
- a hung admiral stopped by identity.

Rehearse a server database provider on a disposable database separately before
enabling self-deploy there.

The real utility checks are separate and disabled by default; the default guard
performs no database work. To run them against disposable provider databases, set
`ARMADA_SELF_DEPLOY_INTEGRATION=1`,
`ARMADA_SELF_DEPLOY_INTEGRATION_SCOPE=isolated-test-databases`,
`ARMADA_SELF_DEPLOY_CANDIDATE_DLL`, and
`ARMADA_SELF_DEPLOY_BACKUP_DIRECTORY`. Set typed provider variables with the
`ARMADA_SELF_DEPLOY_SQLITE_*`, `ARMADA_SELF_DEPLOY_MYSQL_*`,
`ARMADA_SELF_DEPLOY_POSTGRESQL_*`, and `ARMADA_SELF_DEPLOY_SQLSERVER_*`
prefixes (`FILENAME` for SQLite; `HOSTNAME`, `PORT`, `USERNAME`, `PASSWORD`,
and `DATABASE_NAME` for server providers). SQL Server also requires
`ARMADA_SELF_DEPLOY_SQLSERVER_BACKUP_DIRECTORY`, which must be visible to the
SQL Server host. The suite uses the injected native runner, so PATH wrappers
can route utilities into isolated provider containers without changing the
application database configuration.

## 7. Configuration And Administration

Treat fleet, vessel, captain, persona, pipeline, playbook, runbook, workflow
profile, environment, template, backup, and server-stop tools as administrator
surfaces. Read the existing record first. Use
`armada_audit_operational_assets` before and after asset changes. Validate
provider models with a live provider call before putting them in a tier.

### Schema migration ledger

Each provider driver applies only migrations above the highest version in
`schema_migrations`. A migration numbered below an applied version would
never run. This happens when parallel branches land out of numeric order.
Every driver therefore reads the ledger through one shared rule before any
migration, schema guard or prerequisite step. If a known migration below the
applied maximum has no row, startup fails with
`SkippedMigrationVersionsException`. The message starts with
`skipped_migration_versions:` and names the provider, the applied maximum and
each missing version. Nothing is written, so every restart refuses the same
way. Version numbers retired from the code's list are not gaps, and neither
are ledger versions the code does not know.

The runner refuses; it does not apply the missing migration late. Reasons:

- A late migration runs against a schema its author never saw. The higher
  versions may have dropped, renamed or backfilled objects it touches, and a
  data statement can then succeed with the wrong result. Nothing reports that.
- A fresh install applies the same versions in the other order, so fresh-
  install schema checks no longer describe the upgraded database.
- MySQL DDL commits statement by statement, so a late migration that fails
  partway leaves partial schema outside a transaction.
- Per-version schema guards (model endpoints, captain endpoint links, Harbor
  enrollment) and the migration checkpoints assume ascending application.

Interrupted migrations do not trip the rule. Each migration records its row
only after its last statement, so an interrupted run leaves the maximum below
it, and the restart resumes normally.

The fix belongs in source, before the migration ships: renumber the unapplied
migration above the highest version any deployed database has applied, on
every provider. A self-deploy candidate check or an isolated-restore boot runs
the same startup, so it reports this refusal and leaves the running admiral in
place. If a build with the lower number already ran against a database, the
owner decides the recovery. Restore from a backup taken before the higher
version, or apply and record the migration by hand after review. Never insert
a ledger row without its schema change.

`scripts/common/verify-fork-migrations.py` is the source-side check. It
refuses a new declaration at or below the fixed manifest baseline. It cannot
see two unlanded branches that both number above that baseline; the startup
rule covers that case.

### Harbor runners (disabled by default)

`Harbor.Enabled` defaults to false. While false the Admiral registers no Harbor link route and no Harbor
enrollment routes, and local captain execution is the only execution path. Keep it false on the production
Admiral until the Harbor acceptance is recorded; the current container deployment needs no change.

To evaluate Harbor on an isolated Admiral, set `Harbor.Enabled` to true in `settings.json` and restart.
`WebSocketEnabled` must stay true, or the Admiral logs that Harbor was not registered. The link path
(`Harbor.LinkPath`, default `/harbor/link`) is served on the existing REST port, so the container's published
port and TLS termination apply unchanged. Enroll each runner against a bearer credential with
`POST /api/v1/harbor-runners/enrollments` and revoke with `.../{runnerId}/revoke`.

The link authenticates only through the standard credential headers; tenant, user and access-key headers are
ignored. Each job is bound to one runner, its enrollment generation and its connection generation, so a stale or
foreign link cannot report for it. Every job frame and heartbeat is revalidated against durable enrollment, so a
runner revoked or re-enrolled on any Admiral is refused by name on its next frame and its link closes. The wire
contract is `docs/HARBOR_PROTOCOL.md`; ownership and revocation are `docs/HARBOR_IDENTITY.md`.

Mission launch stays local unless a route opts a captain or a vessel in. `Harbor.MissionRoutes` entries name a
`RunnerId` and either a `CaptainId` or a `VesselId`; a captain route wins over a vessel route. The mission owner
must be the runner's enrolled tenant and user. The runner works in the Admiral's dock path unchanged (a shared
mount), or under `RunnerWorkingDirectoryRoot` when `AdmiralWorkingDirectoryRoot` is also set. A routed launch is
refused by name and never falls back to a local process: owner mismatch, a captain of another tenant
(`harbor_captain_tenant_mismatch`), an account-login captain, a provider-key
captain (its variables may not leave the Admiral), a dock outside the directory map, or Harbor execution not
registered. The dock, branch and landing stay on the Admiral. A Harbor job appears to process liveness, stall
detection, stop and recovery as a synthetic process id, so a runner job that is lost reads as a dead process.

Jobs are durable. At start the Admiral fails every job an earlier process left unfinished
(`harbor_admiral_restarted`). A runner disconnected longer than `Harbor.DisconnectedJobGraceSeconds` loses its jobs
(`harbor_runner_disconnected`). List, inspect and stop jobs with `GET /api/v1/harbor-runners/jobs`,
`GET .../jobs/{jobId}` and `POST .../jobs/{jobId}/stop`, or the MCP tools `armada_harbor_jobs`,
`armada_harbor_job` and `armada_harbor_job_stop`. Enabling mission routes in production needs a separate,
owner-approved rollout.

### Runtime MCP startup

Ask starts a separate temporary runtime with its own MCP launch configuration.
The dashboard calls the captain tools endpoint with `context=ask` to check the
planned endpoint. A successful probe proves tool discovery from the server,
not a connection from a running chat process. Failed and empty probes show
the returned reason. See [Ask MCP availability](upstream-review/ask-mcp.md).
An API-endpoint captain has no MCP client: its tools response lists the
built-in workspace tools it actually runs, and Ask applies the launch endpoint
admission rule before starting it.

**Cursor captains need `--approve-mcps`.** cursor-agent discovers a workspace
`.cursor/mcp.json` but leaves its servers "not loaded (needs approval)" in a
non-interactive `--print` run; `--trust` covers the workspace only. The runtime
passes `--approve-mcps` so the dock's Armada server loads. Proof is a Research
smoke mission whose report lists the Armada server by name; the file on disk
proves nothing by itself.

**Send Claude prompts on stdin.** `claude --mcp-config` accepts multiple
values. A positional prompt after it can be read as another config path.
OpenCode accepts the prompt as its `run` argument.

### Per-captain provider credentials

A captain whose model is served by an external provider (a `provider/model` id such as `example-provider/claude-fable-5`) normally uses
the provider's host-level environment variable (for example `EXAMPLE_PROVIDER_KEY`). A captain may instead carry
its own `apiKey` (and optional `apiBaseUrl`) on its record, which wins over
the environment variable. This lets captains on separate provider subscriptions
run side by side on one Admiral; burn down each subscription, then delete its
captains. The MCP captain surface returns the key masked (last four
characters preserved); the dashboard keeps the raw value so the edit form can
prefill it, and entering the key in the dashboard never passes it through an
orchestrator. Creating a captain whose model is not entitled on the
environment key persists with a warning instead of failing, so a credential
that arrives after creation can be attached from the dashboard.

Vessel instruction files and generated briefing files are protected paths.
Captains must propose instruction changes. The orchestrator reviews and applies
them outside the mission dock.

### AI-Memory repository folder

When `aiMemoryRoot` is set, every captain brief carries a Shared Memory
section. It names the vessel's own folder under `<aiMemoryRoot>/repos/` when
one resolves. The admiral reduces the vessel name and each folder name to
lower-case letters and digits and compares them, so vessel `SomeVessel`
resolves to folder `some-vessel`, `somevessel`, or `Some_Vessel`. The brief and
the `deferred-facts.md` lookup use the folder's real name.

- No match: the brief says the vessel has no folder, and the admiral logs it
  at Info once per vessel per process.
- Two or more folders reduce to the same name: no folder is chosen, the brief
  names the ambiguity, and the admiral logs it at Warn. Rename or remove one
  folder.
- The root cannot be read: no folder is chosen, the brief and a Warn log line
  give the error, and the dispatch continues.

### Vessel Workspace

The Workspace surface (dashboard `Workspace` page, `POST
/api/v1/workspace/vessels/{vesselId}/exec`, and the diff/file/tree/status REST
routes) operates on a vessel's configured working directory. Use it to browse,
edit, search, review a working-tree diff, or run a one-shot shell command.
Workspace shell commands run through the platform shell, are killed with their
process tree when they exceed the timeout, and are restricted to tenant
administrators. Every git invocation the workspace performs is bounded by a
30-second timeout that kills the process tree and suppresses the pager and
credential prompts, so a wedged git cannot hang the endpoints.

Backups can contain operational state. Store them in an approved location and
apply retention limits. Restore, delete, purge, stop, and bulk operations need
an explicit operator decision.

### Model routing and dispatch policy

Product defaults are empty and policy-neutral. A fresh `settings.json`
classifies no model family, reserves no specialist persona, selects
randomly among idle persona-eligible captains, and leaves the
stage-persona title guard off. Edit these keys in `settings.json` or on
the Dashboard Settings page:

| Setting | Hot-reload | Product default | Dashboard control |
| --- | --- | --- | --- |
| `modelTier.midTierModels` / `highTierModels` | Yes | empty | Mid-tier / high-tier model lists |
| `modelTier.familyClassificationRules` | Yes | empty | Family classification rules JSON |
| `modelTier.specialistPersonas` | Yes | empty | Specialist persona list |
| `modelTier.withinTierStrategy` | Yes | `Random` | Within-tier strategy |
| `modelTier.withinTierPreferenceOrder` | Yes | empty | Preference-order JSON |
| `modelTier.preferNonNativeFirst` | Yes | `false` | Prefer non-native first |
| `modelTier.usageRouting` | Yes | disabled, empty accounts | [Usage policy, account status, and preview](USAGE_ROUTING.md) |
| `modelTier.reservedHighTierSlots` | Yes | `0` | Reserved high-tier slots |
| `voyageDispatch.rejectStagePersonaTitlePrefixes` | Yes | `false` | Reject stage-persona title prefixes |
| `voyageDispatch.stagePersonaTitlePrefixes` | Yes | empty | Prefix list |
| `modelProviders` | No (startup) | empty | modelProviders JSON |
| `additionalPromptTemplates` / `additionalPersonas` / `additionalPipelines` | No (startup) | empty | Additional-asset JSON |

A `modelTier.usageRouting` account can also own a separate captain login.
Set `runtime` plus `homeDirectory` (ClaudeCode `CLAUDE_CONFIG_DIR`, Codex
`CODEX_HOME`, OpenCode `XDG_DATA_HOME`), or `launchCredentialEnv` for Cursor
(`CURSOR_API_KEY`). An account without those fields launches its captains on
the shared login, as before. A missing login blocks the account with a named
reason. Claude Code and Codex accounts also run the runtime's login status
command in the background, so an expired or revoked login reads
`account_login_expired`. A quota, billing, or authentication failure on one captain holds the
whole account Exhausted and quarantines its idle captains until the retry time.
Rollout of any second subscription account needs an owner decision under the
provider's terms. See [Account logins](USAGE_ROUTING.md#account-logins).

When both tier lists and family rules are empty, every idle
persona-eligible captain is an equal peer. That is the vanilla dispatch
path. Low still maps to mid: that is the platform two-tier architecture,
not a fleet rule.

Copy `factory/settings.fleet.example.json` into the live settings file to
restore the former hardcoded routing, guard, and specialist-reviewer
assets. The live `~/.armada/settings.json` is not in the repository.
Merge the fleet overlay before you deploy this build if you need today's
behavior. Family rules now apply even when a settings object is supplied
(lists still win), so an unlisted version-bump that matches a seeded
family pattern classifies. That is a small expansion versus a
lists-only production file.

The `docker/` image pins (agent CLI set, `CLI_REFRESH`, `@latest`) are
project infra for this deployment. They are not product defaults. Gate
them with build args when you ship a generic image.

## 8. Complete MCP Tool Catalog

The built-in catalog contains 198 names, counted as the registration names in
`src/Armada.Server/Mcp/Tools`. Some names are compatibility aliases.
Some tool families register only when their service is enabled.

Risk labels:

- **Read**: no intended state change.
- **Write**: creates or updates Armada state.
- **Execute**: starts work, a command, landing, or rollout.
- **Interrupt**: stops or cancels active work.
- **Destructive**: deletes, purges, restores, or stops the server.

### 8.1 Status, Enumeration, Jobs, And Diagnostics

| Risk | Tools |
| --- | --- |
| Read | `armada_status`, `armada_enumerate`, `armada_job_status`, `armada_captain_diagnostics`, `armada_unlanded_branches`, `inbox` |

#### Needs-you inbox

`inbox` returns everything across the fleet that is waiting on a decision or
intervention from the operator, ordered most-urgent first. It answers "is there
anything waiting on me?" in one call instead of polling each entity. Two kinds
of item qualify:

- **Human-in-the-loop**: a mission in Review to approve or reject, or a
  deployment pending approval.
- **Human-out-of-the-loop**: a failed mission, a mission whose work could not
  land, a failed merge, a failed or verification-failed deployment, or a
  stalled captain.

Purely informational state changes are excluded. Each item carries a `kind`,
a `severity` (Critical, Warning, or Info), a title, detail, the referenced
entity, and a dashboard href for one-click navigation. An empty list means
nothing currently needs attention. The same surface is available as the
dashboard `Needs You` page and the `armada inbox` CLI command (`--critical`
shows only critical items).

### 8.2 Fleets And Vessels

| Risk | Tools |
| --- | --- |
| Read | `armada_get_fleet`, `armada_get_vessel` |
| Write | `armada_create_fleet`, `armada_update_fleet`, `armada_add_vessel`, `armada_update_vessel`, `armada_update_vessel_context` |
| Destructive | `armada_delete_fleet`, `armada_delete_fleets`, `armada_delete_vessel`, `armada_delete_vessels` |

### 8.3 Captains

| Risk | Tools |
| --- | --- |
| Read | `armada_get_captain`, `armada_get_captain_log` |
| Write | `armada_create_captain`, `armada_update_captain`, `armada_bench_captain`, `armada_unbench_captain` |
| Interrupt | `armada_stop_captain`, `armada_stop_all` |
| Destructive | `armada_delete_captain`, `armada_delete_captains` |

Manual holds and releases share one quarantine service. REST
`POST /api/v1/captains/{id}/quarantine` and MCP `armada_bench_captain` hold a
captain with a required reason and an optional expiry (none is an indefinite
hold). REST `POST /api/v1/captains/{id}/unquarantine`, MCP
`armada_unbench_captain` and Captain Detail **Lift Quarantine** release it. The
hold is a conditional database write: it applies only while the captain is Idle
(or already held) and owns no mission, dock or process, so a bench never strips
work from a running agent. A refused hold returns `Busy` (HTTP 409); stop the
captain first. A release applies only to a quarantined captain and never forces
a Working captain to Idle. The expiry sweep and quota probe release only a
hold that is still timed (the sweep also requires the expiry to have passed),
so an indefinite hold placed while a probe runs survives. A provider spend cap
holds the failing captain and every idle captain on the capped model; a busy
sibling keeps its mission, dock and process and is held when its own run
returns the cap. The captains list and Captain Detail show the hold reason and
expiry and offer **Quarantine** and **Lift Quarantine**.

### 8.4 Voyages And Missions

| Risk | Tools |
| --- | --- |
| Read | `armada_voyage_status`, `armada_mission_status`, `armada_mission_output`, `armada_get_mission_diff`, `armada_get_mission_log` |
| Write | `armada_create_mission`, `armada_update_mission`, `armada_transition_mission_status`, `armada_reconcile_terminal_voyage_missions` (`dryRun=false`; see 8.26) |
| Execute | `armada_dispatch`, `armada_restart_mission`, `armada_retry_landing` |
| Interrupt | `armada_cancel_mission`, `armada_cancel_voyage` |
| Destructive | `armada_purge_mission`, `armada_delete_missions`, `armada_purge_voyage`, `armada_delete_voyages` |

`armada_transition_mission_status`, the WebSocket `transition_mission_status`
command, and `PUT /api/v1/missions/{id}/status` share one operator transition
path. A manual `Complete` meets the same gates on all three: review approval for
a mission in `Review`, Judge authority for a terminal stage, captain process
release, participating Check state, and target ancestry of the mission commit
when no active dock will land it. A refusal names its reason, for example
`Manual completion blocked: manual_completion_ancestry_unavailable`, and leaves
the mission unchanged. Treat the reason as the finding. Do not retry the same
request on another surface.

`armada_mission_output` pages the authoritative safe report artifact. Follow
`nextOffset` until `hasMore` is false. Then verify `sha256`, `finalized`, and
`complete`. A missing page, incomplete artifact, or digest mismatch is an
evidence gap.

### 8.5 Planning, Objectives, And Backlog

| Risk | Tools |
| --- | --- |
| Read | `list_objectives`, `list_backlog`, `get_objective`, `get_backlog_item`, `preview_objective_dispatch`, `list_backlog_refinement_sessions`, `get_backlog_refinement_session`, `get_backlog_planning_session` |
| Write | `create_objective`, `create_backlog_item`, `update_objective`, `update_backlog_item`, `reorder_objectives`, `reorder_backlog_items`, `create_backlog_refinement_session`, `send_backlog_refinement_message`, `summarize_backlog_refinement_session`, `apply_backlog_refinement_summary`, `stop_backlog_refinement_session`, `create_backlog_planning_session`, `delete_objective`, `delete_backlog_item` |
| Execute | `dispatch_backlog_planning_session`, `armada_decompose_plan`, `armada_parse_architect_output` |

### 8.6 Objective Scheduler

| Risk | Tools |
| --- | --- |
| Read | `armada_objective_scheduler_status` |
| Write | `armada_objective_scheduler_set`, `armada_mark_objective_auto_dispatchable`, `armada_objective_scheduler_clear_stale_pause` |

Scheduler state changed through MCP is persisted to the loaded settings file
and survives an Admiral restart. A pause set through `paused=true` should carry
`pausedBy` (your participant key) and `pauseReason`; both are persisted and
shown by `armada_objective_scheduler_status`. A pause outlives the session that
set it, so without an owner nobody can tell a live deploy window from a
departed peer's leftover.

`armada_objective_scheduler_clear_stale_pause` is the autonomy layer's one
permitted write to the pause: clear only, never engage. It succeeds only when
the pause names its owner, the owner is absent from the coordination presence
window, and the absence exceeds
`autonomousObjectiveScheduler.stalePauseAbsenceMinutes` (floor 30, twice the
presence default; a deploy with verification finishes well inside that). It
wakes every active session and posts a board note naming the stale owner, the
recorded reason, the set time and the measured absence, then clears once and
persists. An unattributed pause is refused and stays an operator's to clear.
The dispatch hold is never touched: it clears itself on a successful redeploy,
so a hold that survives is a deploy that stopped halfway and needs a human.
Pass `dryRun=true` to read the decision and evidence without acting. Scheduler dispatches use the same Build and
UnitTest Check arming path as operator dispatches. A dispatch hold blocks both.
The Admiral also requests a debounced immediate sweep when an eligible objective
becomes ready, a dependency completes, or a terminal mission or voyage can free
a shared lane. These event requests bypass the interval but coalesce burst
traffic. The periodic health-loop sweep remains the recovery path for missed
events. `armada_objective_scheduler_status` reports the process-local
`eventTriggeredSweepCount`.
See `docs/SCHEDULING.md` for eligibility and ordering. Operators maintain
campaign quality and handle work outside the scheduler.

### 8.7 Checks

| Risk | Tools |
| --- | --- |
| Read | `get_check_run` |
| Write | `resolve_check`, `armada_resolve_check` |
| Execute | `run_check`, `retry_check_run` |

`armada_resolve_check` is a compatibility alias for `resolve_check`.

`run_check`, `retry_check_run`, and `get_check_run` return a BOUNDED view of a
check run, not the complete command log. A build or test log is routinely one
to several megabytes; returning it overran the tool output limit, so the caller
received a truncation error instead of the verdict and had to parse the record
out of band on every call - including the common case where the only question
was whether the check passed.

The bounded view carries what answers that question:

| Field | Purpose |
| --- | --- |
| `status`, `exitCode` | The verdict |
| `testSummary`, `coverageSummary` | Parsed totals, replacing a count grepped from the log |
| `artifacts` | Paths to the per-project `.trx` files, which name individual failures |
| `outputTail` | The last 40 lines, where a failure's cause almost always is |
| `outputLength`, `outputTruncated` | How much was withheld |
| `outputRetrieval` | The exact call that returns the rest |

Nothing is silently withheld: a truncated view states the full log's size and
names the call that fetches it. Use `get_check_run` with `includeOutput=true`
for the complete record, or `outputTailLines` to widen the tail.

### 8.8 Merge Queue And Audit

| Risk | Tools |
| --- | --- |
| Read | `armada_get_merge_entry`, `armada_drain_audit_queue` |
| Write | `armada_enqueue_merge`, `armada_record_audit_verdict` |
| Execute | `armada_process_merge_entry`, `armada_process_merge_queue` |
| Interrupt | `armada_cancel_merge` |
| Destructive | `armada_delete_merge`, `armada_purge_merge_queue`, `armada_purge_merge_entry`, `armada_purge_merge_entries` |

### 8.9 Docks, Signals, And Events

| Risk | Tools |
| --- | --- |
| Read | `armada_get_dock`, `armada_list_papercuts` |
| Write | `armada_send_signal`, `armada_nudge_voyage`, `armada_mark_signal_read`, `armada_repair_dock` |
| Destructive | `armada_delete_dock`, `armada_purge_dock`, `armada_unstick_dock`, `armada_delete_docks`, `armada_delete_signals`, `armada_delete_event`, `armada_delete_events` |

#### Coordination Board (Chatroom)

Every coordination tool resolves a blank room key, `fleet`, and the literal
word `default` to the one shared room (`CoordinationRoom.NormalizeKey`). These
aliases must not create separate boards or hide notes from other sessions.

The coordination board is a shared room where concurrent operator sessions and
the dashboard post short notes about what they are doing, so no session is
surprised by a voyage another session dispatched. Notes are visible through board reads. General notes stay advisory;
voyage-tagged notes can enter the next stage brief on supported providers.

- `armada_coordination_post` — post a note. Claim work before you start it;
  report outcomes when you finish.
- `armada_coordination_read` — read recent notes plus who is active. Read this
  before dispatching voyages or touching incidents so you do not duplicate a
  peer session's work.
- `armada_coordination_heartbeat` — refresh your presence while working. The
  heartbeat and read responses carry `UnreadWakes` when notes are addressed to
  your participant key: PAUSE and address those before continuing, then
  acknowledge each with `armada_mark_signal_read`. This is how a session inside
  a blocking loop learns it was handed work at the next tool boundary.

#### Identify your session so wakes reach you on any tool

MCP has no channel that can interrupt a running agent. Armada's MCP transport
is stateless, so the server cannot push, and no client turns an inbound
notification into a model turn. A tool result is the only content a session is
certain to read, so a pending wake rides back on one.

Send the caller's participant key on every MCP request:

```
X-Armada-Participant: <participantKey>
```

Armada then appends an `[ARMADA WAKE]` block to the result of whichever tool
the session calls next, so `armada_status` or `armada_voyage_status` delivers
mail just as `armada_coordination_read` does. Without the header a session is
anonymous and receives no wake — that is deliberate, because the server would
otherwise have to guess whose mail to hand out.

Configure it per client:

- SSH stdio bridge: set `ARMADA_PARTICIPANT_KEY` in the MCP server's `env`.
  `scripts/mcp-ssh-http-bridge.mjs` turns it into the header. The same `env`
  must also set `ARMADA_MCP_AUTH_HEADER_FILE`, or every request is refused.
- Direct HTTP clients: add the header to the server entry's `headers` object.
- Bounded helpers: `scripts/autonomy/spawn-helper.sh` writes the header into the
  generated per-helper config. An `AUTONOMY_CLAUDE_MCP_CONFIG` you supply
  yourself must carry the header, or the helper gets no wakes.

Delivery is not acknowledgement. The banner repeats on every tool result until
the session calls `armada_mark_signal_read`; a wake that stopped appearing
before it was read would be a lost wake. The two coordination tools above are
excluded, because they already return the same wakes in their own payload.

Who may acknowledge a Wake follows who it woke. A session that sends the
participant header may mark read a Wake addressed to it (`[to=<its key>]`), and
the effective AgentWake participant (`armada_agentwake_status`) may also mark
read an UNADDRESSED Wake — a mission-outcome or critical wake prefixed
`[vsl=...]` or `[CRITICAL]` — because that is the session such a wake starts.
Any other authenticated key is refused with the owner named; an anonymous
caller (no header) remains unrestricted. Mail and Nudge signals are consumed by
the stage handoff and are never acknowledged through this tool.

#### Do not wait by polling. Subscribe.

The banner is reliable but not immediate: a session that calls no tool sees
nothing until it does. The instinct is to close that gap with a blocking poll --
a shell `while` loop inside one `ssh_exec` call. Do not. That shape was measured
on one operator session (2026-08-23 23:19Z to 01:50Z) and it costs more than it
looks:

- 68 assistant messages, of which only FIVE carried visible text. The operator
  saw nothing for seven minutes at a stretch, and fourteen minutes before the
  final handoff.
- Three of five turns ended `state=interrupted, stopReason=tool_use`, each within
  four seconds of a failed `ssh_exec`. Two of those restarted the underlying
  session, which is why the run finished one item and wrote a handoff.
- While the loop runs the session makes no MCP calls, so a directed board note
  cannot reach it either. A helper waiting on an answer times out against a lead
  that is technically alive.

The Admiral's WebSocket hub already broadcasts every voyage, mission, incident
and board change. Subscribe to it instead, and let each change arrive as an
event:

Each client has an independent bounded output queue. A slow client does not
delay the event producer or another monitor. If a client cannot keep up, Armada
disconnects it instead of silently dropping events. Reconnect and reconcile
authoritative state after that disconnect.

Each broadcast has a process `streamId` and a monotonic `cursor`. The watcher
keeps the last pair in memory and sends it on reconnect. Armada replays the
available suffix from a bounded 128-event, 1 MiB process buffer. It sends an
explicit `event.gap` if the process changed, the cursor is invalid, history was
evicted, or the reconnect output is too large.

Each subscription also gets an authoritative reconciliation snapshot before
Armada enables live delivery. A global watch gets all Open and InProgress
voyages, their missions and Checks, and all captains. `--voyage` gets the exact
voyage, including a terminal voyage, with its linked missions, Checks, and
captains. The global view also includes active standalone missions and their
Checks. The watcher applies this state before it trusts later live events. It
prints `RECONCILED` after each connect or reconnect. A scoped snapshot for a
purged voyage is empty and does not stop future live delivery.

```sh
ssh <server> 'ARMADA_API_KEY="$(<read the admiral API key>)" \
    node <armada-checkout>/scripts/autonomy/watch-armada.mjs \
    --voyage <voyage-id> --participant <your-key> --exit-on-terminal'
```

The hub refuses a session that does not authenticate. Set `ARMADA_API_KEY` to
the admiral API key, or `ARMADA_TOKEN` to a bearer credential token. Without
either, the watcher prints the refusal and exits instead of reconnecting.
Do not put the key on a command line that other users can read; export it from
a protected environment file.

Drive it with the harness's Monitor tool, so every line becomes a notification.
Each mission line is a stage boundary, which is the only window where a
correction still reaches the next brief. `--exit-on-terminal` ends the watch when
the voyage finishes. `--all-notes` widens it to the whole board; `--quiet-captains`
drops the stall lines.

The watcher notifies and nothing more. It never reads, acknowledges, or consumes
a wake, so the banner and `armada_mark_signal_read` remain the delivery and
acknowledgement path.

### Retired standalone lead integration

The standalone lead scripts, systemd units, and Grok gateway are archived.
The server no longer consumes `grokLead` settings or exposes the restricted
Grok listener, OAuth proof-of-concept broker, lead-cycle MCP tools, or
lead-control REST routes. See the [retirement archive](archive/autonomous-lead/README.md).

Before updating an existing deployment, remove the disabled lead service and
timer, Grok gateway, its credentials and listener configuration, and the
lead-specific AgentWake target. Preserve generic AgentWake for other clients.
The scheduler, coordination board, bounded helpers, watcher, and log renderer
remain supported. Operators now perform lead work in authorized sessions.

### 8.10 Incidents

| Risk | Tools |
| --- | --- |
| Read | `armada_list_incidents`, `armada_get_incident` |
| Write | `armada_create_incident`, `armada_update_incident`, `armada_close_incident` |
| Destructive | `armada_delete_incident` |

### 8.11 Releases

| Risk | Tools |
| --- | --- |
| Read | `get_release` |
| Write | `create_release`, `update_release`, `armada_update_release`, `test_release_webhook` |

`armada_update_release` is a compatibility alias for `update_release`.

`test_release_webhook` is registered only when the `cdWebhook` setting is
configured. It POSTs a synthetic `release.shipped` payload to the configured
endpoint and returns the delivery outcome, so you can verify reachability and
authentication before approving a real release.

### 8.12 Deployments

| Risk | Tools |
| --- | --- |
| Read | `get_deployment` |
| Write | `create_deployment`, `update_deployment`, `armada_update_deployment` |
| Execute | `approve_deployment`, `verify_deployment`, `rollback_deployment` |

`armada_update_deployment` is a compatibility alias for
`update_deployment`.

### 8.13 Runbooks

| Risk | Tools |
| --- | --- |
| Read | `list_runbooks`, `get_runbook`, `list_runbook_executions`, `get_runbook_execution` |
| Write | `create_runbook`, `update_runbook`, `update_runbook_execution` |
| Execute | `start_runbook_execution` |
| Destructive | `delete_runbook`, `delete_runbook_execution` |

### 8.14 Workflow Profiles And Environments

| Risk | Tools |
| --- | --- |
| Read | `list_workflow_profiles`, `get_workflow_profile`, `validate_workflow_profile`, `preview_workflow_profile`, `list_environments`, `get_environment`, `armada_audit_operational_assets` |
| Write | `create_workflow_profile`, `update_workflow_profile`, `create_environment`, `update_environment` |
| Destructive | `delete_workflow_profile`, `delete_environment` |

### 8.15 Personas, Pipelines, Playbooks, And Templates

| Risk | Tools |
| --- | --- |
| Read | `get_persona`, `get_pipeline`, `get_playbook`, `list_prompt_templates`, `get_prompt_template` |
| Write | `create_persona`, `update_persona`, `create_pipeline`, `update_pipeline`, `create_playbook`, `update_playbook`, `create_prompt_template`, `update_prompt_template`, `reset_prompt_template` |
| Destructive | `delete_persona`, `delete_pipeline`, `delete_playbook` |

### 8.16 Code Index, Context Packs, And Graphs

| Risk | Tools |
| --- | --- |
| Read | `armada_index_status`, `armada_code_search`, `armada_context_pack`, `armada_fleet_code_search`, `armada_fleet_context_pack`, `armada_graph_search_symbols`, `armada_graph_get_callers`, `armada_graph_get_callees`, `armada_graph_get_impact`, `armada_graph_suggest_affected_tests`, `armada_graph_get_node`, `armada_graph_get_files`, `armada_graph_explore` |
| Execute | `armada_index_update` |

### 8.17 AgentWake

| Risk | Tools |
| --- | --- |
| Read | `armada_agentwake_status` |
| Write | `armada_register_agentwake_session` |

### 8.18 Dispatch Hold

| Risk | Tools |
| --- | --- |
| Write | `armada_dispatch_hold` |

The hold is fleet-wide: while it is engaged every new dispatch is refused,
whichever session or scheduler asks, and in-flight voyages continue. Engage it
with your session name and a reason before an Admiral rebuild; a successful
restart clears it by design. Hold changes require an authorized operator.

Autonomous recovery obeys the same hold. A recoverable failure that arrives
while the hold is engaged gets no rescue voyage or mission and spends no
recovery attempt. Its incident's `RecoveryNotes` gets one
`Autonomous rescue deferred: dispatch_hold ...` line per engagement, and an
`autonomous_recovery.rescue_deferred_dispatch_hold` event is recorded. The
first recovery sweep after the hold clears re-evaluates each deferred rescue,
even when the failure is older than the sweep lookback window. Deferrals are
runtime state like the hold itself: after a restart, the sweep picks up only
failures inside its lookback window.

Empty voyages created through REST, WebSocket or the remote-control tunnel
dispatch no work. Missions added to them later go through the admiral
dispatch, which the hold refuses.

AgentWake is a process-delivery transport, not the work queue or the source of
truth. Put the authorized operator key in `remoteTrigger.agentWake.participantKey` when
addressed process wakes must work after an Admiral restart. A registration with
its own `participantKey` temporarily overrides the configured key and remains
useful for a controlled probe or a resumable Claude or Codex session. An
addressed board note always creates a Wake signal. With delivery mode
`SpawnProcess` or `Both`, a note for the effective participant key also starts
the runtime process. With `StoredWake`, or when no key matches, the signal
remains for the next heartbeat or read. `armada_agentwake_status` reports the
configured key, effective key, delivery mode, runtime, and transient session.

Nothing is pushed under any delivery mode. The row waits until the session next
calls a tool, and the participant header above is what lets that call carry it.
This mode was called `McpNotification`, which promised a push the transport
cannot carry; it is now `StoredWake`. Settings files using the old spelling keep
loading unchanged, and Armada writes the new one.

OpenCode does not resume an earlier conversation for AgentWake. It starts a
fresh session by design. Put the complete task in the addressed note and make
the bootstrap prompt reconstruct state from the coordination board and durable
memory. Never give the same participant key to a resident process and an
AgentWake registration.

### 8.19 Backup, Restore, And Server Control

| Risk | Tools |
| --- | --- |
| Write | `armada_backup` |
| Destructive | `armada_restore`, `armada_stop_server` |

Backup is provider-native and verified on every provider. Each run restores
its own artifact into an isolated target and checks it before writing the
archive. The manifest reports the configured provider's schema version and
record counts. Install the provider's client utilities on the admiral host;
SQL Server also needs `selfDeploy.sqlServerBackupDirectory`. A failure returns
a named reason and leaves no archive, so a successful `armada_backup` is
evidence of a restorable backup.

Archives do not carry secrets. `settings.json` is written with database and
connection-string passwords, API keys, tokens, provider keys and every other
value the shared redaction rule marks secret replaced by `[REDACTED]`, and the
manifest records `settingsRedacted`. A restore keeps the target host's own
secrets for those values and never writes the placeholder. Secrets that exist
only in the source host's settings must be set on the target by hand after a
restore.

The server image ships the PostgreSQL 16 and MySQL 8.0 clients. SQL Server's
`sqlcmd` must come from a derived image. An image built before those packages
were added has no server clients, so backup and the self-deploy preflight fail
with `native_client_missing_<tool>` (for example
`native_client_missing_pg_dump`). The deploy window that adds them rebuilds
the image with rollback retention. Inside the new container, `pg_dump
--version` must report a major version at least the PostgreSQL server's. One
`armada_backup` must then produce a manifest with the configured
`databaseType`, non-zero `recordCounts` and both verification flags true.
`docs/DOCKER.md` ("Database Client Tools") has the steps.

Restore replaces the database only on SQLite. On PostgreSQL, MySQL and SQL
Server it is refused with `restore_unsupported_for_provider_<type>`. For those
providers, stop the admiral and restore the archive's native artifact with
`pg_restore`, `mysql` or `RESTORE DATABASE` after taking a fresh backup, then
start the admiral and check health and the schema version.

### 8.20 Disk Lifecycle

`armada_disk_lifecycle` reports and, when explicitly enabled in settings,
reclaims Armada-owned disk storage. Run `action=scan` for the dry-run report
before anything destructive: it returns bytes per owned category (docks, bare
repos, mission logs, diffs, instruction snapshots, dock metadata, integration
and merge-queue worktrees, temp artifacts, backups) plus reclaimable counts.
`action=reconcile` additionally purges stale sibling-worktree leases and, only
when `diskLifecycle.enabled` is true and `diskLifecycle.dryRun` is false,
deletes eligible items. Reclamation fails closed: only paths under the allowed
roots, not symlinks, past their grace period, and not referenced by active
docks, missions, or merge-queue entries are ever touched. Docker image and
build-cache pruning stays an explicit host-side operator action
(`docker builder prune` with the current and rollback images protected), never
a container-triggered deletion. Before rebuilding a mutable local image tag, use
`scripts/common/rebuild-local-image.sh`; it retains the running image and the
current tag first and prints the rollback references for the deployment record.

| Risk | Tools |
| --- | --- |
| Read | `armada_disk_lifecycle` (action `scan`) |
| Destructive | `armada_disk_lifecycle` (action `reconcile`; gated by `diskLifecycle.enabled` and `diskLifecycle.dryRun`) |

### 8.21 Token Usage

`token_usage_summary` summarizes model token usage over a time window: time
buckets with a per-model breakdown, a whole-window per-model aggregate ordered
most-used first, and grand totals for input, output, cached, and total tokens.
Narrow it with `model`, `runtime`, `source` (mission, chat, or planning),
`vesselId`, or `captainId`, and set the window with `sinceHours` or an explicit
`fromUtc`/`toUtc` pair. `bucketMinutes` accepts fractional values.

Read the `estimatedCount` before comparing models. Counts are real only where
the runtime reports usage, and estimated otherwise, so a window mixing runtimes
mixes measured and inferred numbers in one total. Several runtimes report no
usage at all; for those, the admiral-side prompt-byte accounting is the figure
that is comparable across runtimes.

| Risk | Tools |
| --- | --- |
| Read | `token_usage_summary` |

### 8.22 Verified Production

`armada_production_summary` reports verified landed slices for a bounded UTC
window. It also reports raw completed leaf objectives, evidence exclusions,
Check timing, rescue share, and the measures that current records cannot
calculate. First-pass acceptance and rescue share come from durable mission
attempt facts; runs that happened before those facts existed are reported as
historical in each metric's `unknown`, `coverage`, and historical fields.
Post-land consumer and ledger regression rates come only from incidents and
Checks that carry a typed regression purpose; set the purpose, cause,
objective and landed commit on the incident or Check, and read
`regressionCoverage` for unlinked and unattributed records. Repeated research
counts only preparation claims re-established with unchanged evidence and
anchors; revalidation and reuse are separate counts, and slices without claim
observations are reported as uncovered. Check timing separates preparation
delay, pure host-slot wait from the recorded slot request, and execution.
`laneTime` reports eligible idle lane-minutes apart from fleet-capacity and
dispatch-hold time; read its `coverage` and `incompleteIntervals` before
using it. Use
`sourceFamily` and `workType` to select one cohort. Read
`scan.truncated`, `isComplete`, and each metric's availability before you use a
rate. The equivalent REST route is `GET /api/v1/production/summary`.

| Risk | Tools |
| --- | --- |
| Read | `armada_production_summary` |

### 8.23 Native Captain Memory

| Risk | Tools |
| --- | --- |
| Read | `search_memory`, `get_memory` |
| Write | `create_memory`, `update_memory` |
| Destructive | `delete_memory` |

Native memory holds captain working memory: a vessel fact, a prior finding, or a
procedure worth repeating. A record carries a type (Episodic, Semantic,
Procedural), an optional stable key, a salience that orders recall, a version,
provenance, and tags. `create_memory` writes the record with that key in place,
so recording the same finding twice corrects one record instead of scattering
copies. Send `expectedVersion` on a write to be refused instead of overwriting a
newer record.

Two boundaries decide what belongs here:

- The shared external memory repository stays the authority for accepted durable
  rules. When a brief carries a Shared Memory section, an external rule wins over
  a native record on conflict, and a captain reports the conflict instead of
  rewriting either side.
- The memory tools never write to that repository, to repository files, or to the
  vessel model context.

The MCP surface carries no per-request identity, so the memory tools act as an
administrator of the default tenant: they reach every record of that tenant and
no record of another tenant. The feature needs no setting.

### 8.24 The Recorder Persona

The built-in `Recorder` persona reviews the finished work of a voyage and records
what is worth remembering through the memory tools above. It is seeded with its
own editable prompt, and the built-in `Recorded` pipeline runs it after a Worker.

Three operator facts:

- **Startup adds the Recorder only where an owner decision has placed it.** The
  built-in `Recorded` pipeline runs it after a Worker, and the built-in
  `ProductDevelopment` pipeline ends with it at the mid tier so it never competes
  for the scarce high-tier captains. Startup adds it to no other pipeline: whether
  the Recorder belongs at the end of `FullPipeline`, `Tested` or any other
  pipeline is an owner decision, not a side effect of a deploy. A pipeline that
  already carries the name `Recorded` is left exactly as it is.
- **A Recorder stage produces no commit, and the completion gate knows it.** The
  Recorder writes memory, not code, so its empty diff is its success shape. The
  no-op completion and ineffective-rescue gates exempt the Recorder persona the
  same way they exempt the Architect, so a Recorder stage is safe as the terminal
  stage of an implementing pipeline such as `ProductDevelopment`, not only in a
  fully report-only voyage.
- **The Recorder never writes shared memory.** It writes native memory only, and
  hands anything that belongs in the shared external memory repository to the
  operator as a proposal in its summary.

Every other built-in persona template carries a Recall Existing Memory section
telling the agent to read the vessel model context and search memory before it
acts. Startup adds that section once to a built-in persona template that lacks
it and changes nothing else, so an operator edit is kept.

### 8.25 Branch Cleanup Sweep

The health loop runs the branch cleanup sweep every
`branchCleanupSweepIntervalCycles` cycles (default 200, about 100 minutes at a
30-second heartbeat). Every run writes one line to the admiral log
(`admiral.log` under `logDirectory`, dated by day), starting
`[BranchCleanupSweepService] sweep complete:`. The line gives vessels swept,
skipped and in error; branch candidates in the vessel bare and on origin;
landed, kept unlanded, kept for active missions, removed local and removed
origin; the same counts for preserved refs; for each anchor family (`dock
anchors`, `mission anchors`) candidates, kept for active missions, kept for
recover pointers, kept unlanded, kept in retention, removed local and removed
origin; failed operations; and the reason for each skip. A run that removed
nothing still writes the line, so a missing
line means the sweep did not run. A maintenance step failure on the loop logs
as `[ArmadaServer] <step> failed: <reason>`. Each removal also records a
`branch_cleanup.swept` event.

The sweep follows these rules:

- Candidates are `armada/` and `armada-landing/` branches,
  `refs/armada-preserved/` refs, and the reclaim anchors under
  `refs/armada/docks/` and `refs/armada/missions/`. `recover/` refs, human
  branches and other ref families are never touched.
- A ref is landed when its tip is an ancestor of the default branch in the
  vessel bare. A tip that the bare does not hold reads as unlanded and is kept.
- A branch that a non-terminal mission names is kept, even when it reads as
  landed.
- `LocalOnly` removes landed refs from the vessel bare. `LocalAndRemote` also
  lists origin through the vessel working checkout and removes landed refs
  there, with a lease on the tip it measured. `None` skips the vessel.
- A landed preserved ref is removed once its tip commit is older than
  `branchCleanupPreservedRefRetentionDays` (default 14). `0` keeps every
  preserved ref. An unlanded preserved ref is always kept.
- A missing default branch, an unreachable origin or a missing working checkout
  appears as an error or skip in the summary, never as a clean run.

#### Reclaim anchor refs

When a dock is reclaimed, `DockService` pushes the dock `HEAD` to origin under
two names, so a produced commit can be found by dock or mission id without
knowing its SHA. The push goes to origin through the dock worktree, so the
vessel bare normally does not hold these refs and the hosted remote never prunes
them.

- `refs/armada/docks/<dockId>` names every reclaimed dock, including a
  branchless Architect fan-out worker (`AnchorDockCommitAsync`,
  `src/Armada.Core/Services/DockService.cs:720-721`).
- `refs/armada/missions/<missionId>` names the mission that owned the dock, when
  it can be resolved (`src/Armada.Core/Services/DockService.cs:723-728`).

Both families use one retention rule. The sweep keeps an anchor in these cases:

- The dock or mission it names is live. A mission is live while its status is
  not terminal (`Complete`, `Failed`, `Cancelled`). A dock is live while its
  record is active or a non-terminal mission names it.
- A `recover/` branch in the vessel bare, or on origin when the sweep lists
  origin, points at the same commit.
- Its tip is not an ancestor of the default branch in the vessel bare. A tip the
  bare does not hold counts as unlanded.
- Its tip commit is younger than `branchCleanupPreservedRefRetentionDays`. The
  anchors share the preserved-ref window because the same reclaim step writes
  all three families to recover the same commit. `0` keeps every anchor.

Otherwise the sweep removes the anchor from the vessel bare and, under
`LocalAndRemote`, from origin with a lease on the measured tip. A mission that
stays in `WorkProduced` after its voyage ends is not terminal, so its anchor is
counted as kept for active missions until the mission status changes.

To confirm the sweep on a running admiral, read the next summary line. Then
compare `git for-each-ref refs/heads/armada-landing refs/heads/armada
refs/armada-preserved refs/armada/docks refs/armada/missions` in the vessel bare
and `git ls-remote origin` before and after that run. For the anchor families,
count per family on origin through the vessel working checkout:
`git ls-remote origin 'refs/armada/*' | cut -f2 | cut -d/ -f1-3 | sort | uniq -c`.

A WorkProduced mission under an ended voyage is terminal only after 8.26
reconciles it. Until then its branch counts as kept for active missions.

### 8.26 Terminal Voyage Mission Reconciliation

WorkProduced means that work exists and a later stage or landing will act on
it. That is a valid resting state only while the voyage is `Open` or
`InProgress`. The paths that end a voyage count WorkProduced as finished and
leave the mission in place: the completion check, the landing drain, the halt
after a failed stage, and every cancel surface. Without reconciliation those
missions read as live forever to branch cleanup, capacity and recovery.

One rule gives each such mission a terminal status. It uses landing evidence,
never the voyage status alone, because a Failed voyage can hold landed work
and a Complete voyage can hold unlanded work:

| Evidence | Voyage | Mission becomes | Reason |
| --- | --- | --- | --- |
| Commit is an ancestor of the default branch in the vessel bare, or a merge entry is `Landed` | any ended | `Complete` | `terminal_voyage_work_landed` |
| Commit exists and is not on the default branch | `Failed` | `Failed` | `terminal_voyage_work_unlanded` |
| Same | `Cancelled` or `Complete` | `Cancelled` | `terminal_voyage_work_unlanded` |
| Commit absent from the vessel bare | as above | as above | `terminal_voyage_commit_absent` |
| No commit recorded | as above | as above | `terminal_voyage_no_commit` |

Unlanded work is never marked `Complete`. Under a `Complete` voyage it becomes
`Cancelled`, because another stage landed and this stage's commit was
superseded. A `Failed` or `Cancelled` mission carries the reason in
`FailureReason`. Every change records a `mission.terminal_voyage_reconciled`
event with the voyage status, landing probe and commit. The mission keeps its
commit and branch. The pass never deletes branches, refs or commits, and it
runs no rescue or wake.

The rule keeps a mission unchanged in these cases, and counts each by reason:

- `ancestry_unknown`: there is no vessel or repository, the default branch does
  not resolve, or the probe failed.
- `landing_in_flight`: a merge entry for the mission is not yet `Landed`,
  `Failed` or `Cancelled`.
- `voyage_terminal_grace`: the voyage ended less than ten minutes ago.
- `awaiting_manual_landing`: the voyage ended `Complete` with landing mode
  `None`. The work waits for a person to land it.

The health loop runs the rule every 10 cycles for voyages that ended in the
last 24 hours. It logs a
`[TerminalVoyageMissionReconciler] terminal voyage mission reconciliation`
summary line whenever it changes a mission or finds unknown ancestry.

To repair older rows, use `armada_reconcile_terminal_voyage_missions`:

1. Take a database backup in an approved window.
2. Run the tool with its defaults (`dryRun=true`, `includeHistorical=true`). It
   returns a background job; read the result with `armada_job_status`. Check
   `completed`, `failed`, `cancelled`, `kept` and `reasons`. Scope the run with
   `vesselId` or `voyageId` if you need to.
3. Run it again with `dryRun=false`. The counts must match the dry run, apart
   from missions that changed in between (`status_changed`).
4. Run the dry run once more. It must examine zero missions, apart from the
   kept reasons above.
5. Read the next branch cleanup summary. Its `kept for active missions` count
   drops by the branches the repaired missions held.

| Risk | Tools |
| --- | --- |
| Read | `armada_reconcile_terminal_voyage_missions` (`dryRun=true`, the default) |
| Write | `armada_reconcile_terminal_voyage_missions` (`dryRun=false`) |

## 9. Safety Rules

- Read before write.
- Confirm the exact ID before a cancel, delete, purge, restore, rollback, or
  server stop.
- Do not use bulk deletion when a specific record is sufficient.
- Do not retry a dispatch until you know whether the first request created a
  voyage.
- Do not call `resolve_check` to turn an unknown or failed result green.
- Do not change vessel context or shared instructions from a captain mission.
- Do not push, deploy, release, or roll back without the applicable operator
  authority.
- Never put credentials or private operational identifiers in public docs,
  prompts, logs, examples, or commit messages.

## 10. Verification Checklist

Before you report completion:

- The objective state matches the evidence.
- The voyage and each mission have the expected terminal state.
- Required Checks passed with real output.
- The target branch contains the expected commit.
- Release and deployment records match what shipped.
- Incidents have evidence for their final state.
- Deferred work has its own record.
- No sensitive value or private operational identifier entered a public
  artifact.
