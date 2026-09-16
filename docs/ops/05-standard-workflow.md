---
topic: "Standard Workflow"
summary: "The full operator loop: capture, inspect, dispatch, monitor, verify with Checks, review, land, and close a record."
read_when: "Dispatching and running any non-trivial work through to a closed record."
applies_to: orchestrator
tier: leaf
---
# Standard Workflow

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
reports target, pipeline, captain, Check, repository, brief, dependency, and
preflight findings. A busy compatible captain is capacity information and does
not make the objective unready.

Objective dispatch enforces the dispatch preflight. Record an answer for each
numbered question of the operator dispatch-preflight battery on the objective
through `update_objective` (the `preparation.preflight` block). Dispatch is
refused while any question is unanswered, a question that must be yes is
answered no, or the open-owner-question question is answered yes; the preview
reports the blocking `objective_preflight_incomplete` finding with the offending
question numbers, and the autonomous scheduler skips the objective. The preview
also computes the deterministic questions as facts so a recorded answer can be
checked against the repository. An operator may set `forcePreflight` on
`armada_dispatch` to override an incomplete preflight or a D5
`objective_preflight_model_flag` finding; it overrides only those
preflight-class findings, any other blocking issue still refuses the dispatch,
and the override is recorded as an `objective.preflight_overridden` event that
names the blocking and the model-flagged question numbers.

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

A Build or UnitTest check executes in a private checkout cut from the vessel's
repository, never in the live working directory. That checkout is locked as a
git worktree while the check runs, so a prune elsewhere cannot unregister it
mid-run, and its directory name carries the check run id, so the storage reclaim
sweep keeps it while the check has no verdict.

Readiness probes the program each command segment starts with, so a check whose
command needs a missing binary is blocked before it runs. Shell syntax is not a
program: the header of a `for`, `select` or `case` construct, the loop and
conditional keywords, and a command named by a shell variable (`$tool`) are not
probed. A real missing binary inside a loop body is still reported.

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

A check executes inside the Admiral process. A record left `Running` when that
process stops can never reach a verdict, so at startup every `Running` record
whose start time predates the new process is set `Canceled` with a summary
naming the restart. Records started by the running process are untouched.
Nothing re-arms such a record: supersession replaces only a `Passed` or `Failed`
record whose commit moved, so a voyage whose only Build record was cancelled
this way needs the gate re-armed by hand before the Judge stage.

A voyage that ends before its armed Checks run does not keep them `Pending`.
Cancelling a voyage (every cancel surface and the halt after a terminal mission
failure) marks each `Pending` Check of that voyage `Canceled` with the summary
`voyage_cancelled: the voyage ended before this Check ran.` The check executor
applies the same rule to any `Pending` record it finds on a `Cancelled` or
`Failed` voyage (`voyage_cancelled` or `voyage_failed`), which covers voyages
ended by any other path and records left from before the rule. Such a record is
no longer counted in `PendingChecksRequired`. A re-dispatch arms fresh Checks.

The table applies to voyages that contain implementation work. A fully
report-only voyage has no implementation Checks by design. Its Judge validates
the report structure and evidence, not a code diff or a green Build and
UnitTest pair. A voyage is fully report-only when every mission is Audit or
Research, in any combination, because neither mode produces a diff. One
Implementation mission puts the whole voyage back on the code Check gates.

A read-only Judge is told to write `## Completeness`, `## Correctness`,
`## Evidence`, `## Residual Risks` and `## Verdict`; an implementation Judge
writes `## Tests` and `## Failure Modes` in place of the middle pair. Every
brief, prompt and output contract renders that list from one mode-aware source,
and the verdict validator reads the same source, so a Judge that follows its
instructions cannot have its PASS rejected for the wrong section set.

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
record remains `Failed` before the Judge stage runs. For each rejecting Check
the reason also carries the commit that Check measured and a bounded tail of its
output, redacted the same way every stored command output is, so the incident
that records the rejection says what failed and against which tip instead of
only which record to open. The tail is capped per Check and only the first few
rejecting Checks carry one, so a large log cannot inflate the incident. Resolve an environmental
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

A voyage can end between the moment the executor selects an armed record and
the moment it stamps it, which leaves the record with no branch and no commit.
The executor never runs such a record against the default branch: while the
voyage is live the record simply waits for a stage to commit, and once the
voyage is `Cancelled` or `Failed` the record is set `Canceled` with a summary
naming the missing stamp (`unstamped_voyage_check`). A record whose voyage is
`Complete` still runs, because that work is on the default branch. A record
that does not execute is reported as `check.auto_not_run`, never as a failure.

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

The definition-of-done gate does not run for a read-only mission (mission mode
Research or Audit) that produced no commit. Armada proves "no commit" by
comparing the dock's head commit with the commit recorded when the dock was
provisioned. When they are equal, the build and unit-test commands would
measure only the base branch, so a base branch that is already red would fail
work that changed nothing. The mission completes its stage and hands off. The
activity log and the recorded evaluation event (outcome `Skipped`) name the
reason `read_only_no_commit`. A read-only mission that did commit, and every
Implementation mission, run the gate as before. If either commit cannot be
read, the gate runs.

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

After the merge, `LocalMerge` fast-forwards the configured working checkout. If
the checkout holds commits that the landing repository's target branch lacks,
the fast-forward cannot run, and those commits exist nowhere else. Armada does
not reset the checkout and does not push to a remote. It pushes the checkout
`HEAD` to `recover/working-checkout-<12-char-sha>` in the landing repository,
emits one `landing.working_checkout_diverged` event, and opens one incident for
the vessel. The incident names the recover branch, the full SHA, the commit
count and the repair steps: land the recover branch through the merge queue,
fast-forward the checkout, confirm that no commits remain on either side, then
close the incident. The same divergence with its incident still open adds no
second event or incident. If the checkout cannot be read, counted or pushed,
the incident names the step that failed. `docs/MERGING.md` has the commands.

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
dispatches captains inside Armada. Generic operator wakes and bounded helpers
support operator sessions.

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
