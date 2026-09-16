---
topic: ops.standard-workflow
summary: The operator's standard voyage workflow, chapter 4, as retrievable sub-topic leaves.
read_when: You operate Armada and need the capture-to-close workflow, or one step of it.
applies_to: [orchestrator]
tier: leaf
---

# Standard Workflow (chunked)

This file is the worked-example chunk form of `docs/armada-ops.md` chapter 4,
"Standard Workflow". The original chapter stays intact. This file proves the
chunk shape from the context-system design (`docs/design/context-system.md`):
one large chapter becomes one topic file, split into single-topic sub-sections,
each with its own front-matter.

## Core promotions (not gated here)

A few rules embedded in this chapter are safety or proof non-negotiables. In
the two-tier model they live in the always-on CORE, not in this leaf. They are
listed here as pointers, never as retrieval-gated content:

- The quiet-host gate rule (enumerate TERMINAL states, never guess active
  ones) — a safety rule; a miss destroys running work.
- Resolve every failed Check; never use `resolve_check` to hide a failure — a
  proof rule.
- A `Complete` label is not proof of landing; verify the target commit — a
  proof rule.
- Authorization and hard limits ship verbatim in every brief and are never
  elided.

The sub-topics below are leaves, retrieved on demand.

---

## 4.1 Capture the work

```yaml
topic: ops.standard-workflow.capture
summary: Find or create an objective before dispatch; add acceptance criteria; separate deferred work.
read_when: You are starting non-trivial work and need a durable record before dispatch.
applies_to: [orchestrator]
tier: leaf
```

For non-trivial work, find or create an objective before dispatch.

1. Search with `list_objectives`, `list_backlog`, or
   `armada_enumerate(entityType: "objectives")`.
2. Read the record with `get_objective` or `get_backlog_item`.
3. Add clear acceptance criteria and verification requirements.
4. Put deferred work in a separate record. Do not bury it in closed-record
   prose.

Use backlog refinement when intent is unclear and repository context is not yet
needed. Use a planning session when the work is tied to a vessel and must become
dispatch-ready.

---

## 4.2 Inspect before dispatch

```yaml
topic: ops.standard-workflow.inspect
summary: Read the target repo rules and history; read the coordination board; record prepared facts and preflight.
read_when: You are about to dispatch and must confirm the work is real, not already done, and not claimed.
applies_to: [orchestrator]
tier: leaf
```

Read the target repository rules and inspect the current code and git history.
Confirm the work is not already present. Confirm the vessel, pipeline, model
tier, landing mode, protected paths, and workflow profile.

Read the coordination board before you dispatch or touch incidents
(`armada_coordination_read`). Post a claim note before you start
(`armada_coordination_post`) so a concurrent session does not dispatch the same
work or rescue the same incident twice. General board notes are advisory.
Voyage-tagged notes can enter the next stage brief when the provider supports
that lookup.

If code indexing is enabled, use `armada_index_status` before index-dependent
work. If it is disabled, use checkout search and set `codeContextMode` to `off`.

Record prepared facts in the objective `preparation` field. Set a verified
source and target anchor. Give each claim a stable id, a kind, evidence, and a
`dependsOn` of `Source`, `Target`, both, or `None`. Keep claims concise; Armada
limits their count and size. Set `requiredForDispatch` when the facts are
mandatory, and name at least one `requiredClaimKinds`. Then each such claim
needs evidence and a `verifiedUtc`. Use `requiredSiblingInputs` for a sibling
vessel, its checkout path, and required artifact paths. Preview blocks work when
a required claim, anchor, sibling declaration, or artifact input is absent or
stale.

When an anchor changes, Armada keeps the claims and marks only the dependent
ones `NeedsRecheck`. Recheck those and record them `Verified`. An objective
update replaces the whole `preparation` object, so send every value that must
remain.

---

## 4.3 Select the mission shape

```yaml
topic: ops.standard-workflow.mission-shape
summary: Implementation vs Audit/Research mode; how mode derives from objective Kind; the objective brief; preflight.
read_when: You are choosing a mission mode or pipeline, or a report-only voyage armed code Checks unexpectedly.
applies_to: [orchestrator]
tier: leaf
```

Use mission mode `Implementation` for work that must produce a commit. Use
`Audit` or `Research` for report-only work. Read-only modes need no commit and
must not receive implementation-only instructions.

Both dispatch paths derive the mode from the objective `Kind` when a mission
states none: a `Research` objective runs read-only; every other Kind runs
`Implementation`. Autonomous dispatch has no per-mission `mode` and relies on
this. Operator dispatch applies the same rule to any mission linked through
`objectiveId` that omits `mode`. An explicit per-mission `mode` overrides the
derived mode.

A voyage is fully report-only only when every effective stage is `Audit` or
`Research`. Pipeline shaping keeps the whole declared graph for every mode. The
classifier controls Check arming and Judge validation: a fully report-only
voyage arms no Build or UnitTest Check, and its Judge can accept a no-commit
report without `[JUDGE-CHECK-EXCLUSION]`. One `Implementation` stage puts the
whole implementation Check and delivery contract back on.

Operator and autonomous dispatch use one server-rendered objective brief. An
operator mission keeps its own text first; the server appends the objective
brief once and inherits the objective start ref only when the mission has no
explicit ref. Do not copy objective fields into an operator prompt; link with
`objectiveId`.

Run `preview_objective_dispatch` before objective dispatch. It is read-only and
reports target, pipeline, captain, Check, repository, brief, dependency, and
preflight findings. A busy compatible captain is capacity information, not
unreadiness.

Objective dispatch enforces the dispatch preflight. Record an answer for each
numbered preflight question on the objective (`preparation.preflight`). Dispatch
is refused while a question is unanswered, a must-be-yes question is no, or the
open-owner-question is yes; the preview reports `objective_preflight_incomplete`
with the offending numbers, and the scheduler skips the objective. An operator
may set `forcePreflight` on `armada_dispatch` to override an incomplete
preflight only; any other blocking issue still refuses, and the override records
an `objective.preflight_overridden` event.

Use the vessel's configured pipeline and the full persona path. Do not remove
review stages to save time. Dispatch with `preferredModel: "low"`, `"mid"`, or
`"high"`, not a concrete provider model.

---

## 4.4 Dispatch

```yaml
topic: ops.standard-workflow.dispatch
summary: armada_dispatch is durable-first; the tenant-scoped admission lease; the dispatch hold; data expiry.
read_when: You are calling armada_dispatch, or a dispatch returned an admission, hold, or already-dispatched code.
applies_to: [orchestrator]
tier: leaf
```

Use `armada_dispatch` for a voyage. Put all ordered missions for one vessel in
one request. Use aliases and dependency aliases when order matters. Include the
`objectiveId`, the vessel id, the pipeline when the default is wrong, one title
and description per unit of work, the mode, the tier, scope and exclusions,
required verification, and only the prestaged files downstream stages need.

`armada_dispatch` is durable-first. A success means the voyage and mission rows
exist. Assignment, dock provisioning, and captain launch continue
asynchronously. Save the voyage id. Do not redispatch because the first status
reads `Pending`.

Assignment claims only an idle captain of the mission's own tenant. A Pending
mission whose tenant has no idle captain waits; an idle captain of another
tenant never takes it.

An objective can have only one normal nonterminal voyage. Every dispatch path
takes a tenant-scoped database admission lease before it creates a voyage and
holds it until the objective link is durable. A competing request returns
`objective_already_dispatched` and names the winner without creating a loser. A
terminal voyage permits an intentional successor; recovery uses a separate
explicit rescue link.

An engaged dispatch hold is checked before admission and before either voyage
path. A held dispatch records no attempt, takes no lease, and creates no voyage.

Admission waits are bounded. When another request holds the admission longer
than five seconds, dispatch creates nothing and returns 409
`objective_dispatch_busy` with `Retryable: true` and `RetryAfterSeconds`. The
scheduler records `admission_busy` and retries on a later sweep.

Every admitted dispatch writes a durable attempt record as events
(`objective.dispatch_attempt.started`, `.voyage_created`, `.closed`). The health
loop reconciles an attempt that never closed once its owner no longer holds a
live lease. A voyage already linked to an admitted objective is the winner and
is never cancelled. Reconciliation looks back seven days; automatic event
retention never deletes an attempt record younger than that. Before deleting
events by hand, exclude the `objective-dispatch-attempt` entity type unless
every attempt is closed.

Data expiry runs on the health loop every 100 cycles, on every provider. With
`dataRetentionDays` above 0 (default 30) a run deletes older Complete/Cancelled
voyages and their missions, orphan terminal missions, read signals and events,
inactive captain-less docks, and terminal merge entries. It also deletes
production metric facts older than `productionFactRetentionDays` (default 365;
0 keeps forever). Each run logs one `data expiry summary:` line. The first run
on a never-expired database can delete many rows; back up before the first
deployment that enables it.

A Completed or Cancelled objective always rests in the `Inbox` backlog state;
Armada moves it there in the same write that makes it terminal. A terminal
objective never lists as `ReadyForDispatch`.

Long operations can return an accepted job. Poll `armada_job_status`. A job left
Accepted or Running past the stale threshold is failed automatically, so poll a
job past its expected runtime.

### Controlled parallel dispatch

```yaml
topic: ops.standard-workflow.dispatch.parallel
summary: maxConcurrentVoyages is a ceiling, not a rule to run one at a time; the sibling lane rule and its leases.
read_when: You are enabling a second lane, or a dispatch was refused lane_busy or waited on a vessel mutex.
applies_to: [orchestrator]
tier: leaf
```

Armada can run several voyages at once. `maxConcurrentVoyages` is a fleet-wide
ceiling. Keep `maxConcurrentVoyagesPerVessel=1` unless one vessel is proven safe
for parallel docks and suites.

Before enabling a lane, confirm: no other active voyage owns the same files or
objective; no other active voyage uses the same vessel unless proven safe for
concurrent docks; captains will not run conflicting dock-side suites or move a
shared sibling; the objective is not hardware-dependent or operator-only; the
brief premise is true at the target tip; and required ordering is in
`BlockedByObjectiveIds`.

Prefer parallel voyages on different vessels. Treat two repositories as one lane
when either suite builds or tests the other through a sibling reference. Every
dock is `docks/<Vessel>/<mission>/<Vessel>` with declared siblings beside it,
pinned at provisioning, never shared between docks. A retry reuses its own
sibling; a stale one logs both commits and records `dock.sibling_stale`.

Declare the build relation with `buildParticipant: true` on the sibling entry of
the vessel that builds the other. The scheduler then applies the per-vessel
ceiling to every vessel in the lane and reports a refusal as
`lane_busy:<vesselA>+<vesselB>`. A read-only sibling stays `false` and forms no
lane. Armada reserves all lane members with durable leases before it checks
mission rows and provisions a dock, so two instances cannot both admit sibling
writers. A mission that loses the reservation waits in `WaitingForVesselMutex`.

Objective tools accept `suggestedPlaybooks` (pairs of `playbookId` and
`deliveryMode`). The scheduler copies them to the voyage before the first
mission; use it for campaign rules that must reach every stage.

### Start ref: continue from an accepted tip

```yaml
topic: ops.standard-workflow.dispatch.start-ref
summary: startFromRef cuts the first stage's branch from an accepted tip; both resolution failures are loud.
read_when: You are re-dispatching from a recover/ ref, or a dispatch failed start_from_ref_missing.
applies_to: [orchestrator]
tier: leaf
```

An objective can carry `startFromRef` (a branch, tag, or commit). The FIRST
stage's branch is cut from that ref instead of the default branch; later stages
continue as usual. Use it to re-dispatch from a `recover/<name>-<sha>` ref that
holds an accepted Worker or TestEngineer tip, so the new voyage does not rebuild
accepted work.

The ref resolves twice, both failures loud. At dispatch, an unresolvable ref
refuses the whole dispatch before any voyage row exists (`start_from_ref_missing`,
never `dispatch_error`). At assignment, the branch is cut in the bare repository
before the dock is provisioned; a ref gone by then fails the mission with
`start_from_ref_missing` and launches no captain. There is no fallback to the
default branch. A dependent mission ignores the field.

### Model pinning

```yaml
topic: ops.standard-workflow.dispatch.model-pin
summary: preferredModel takes a tier or a literal model name; a literal pin is best-effort, not a guarantee.
read_when: You need a specific model for one mission and must know whether the pin holds.
applies_to: [orchestrator]
tier: leaf
```

`preferredModel` normally takes a tier (`mid` or `high`; legacy `low` maps to
`mid`). It also accepts a literal model name: the dispatcher first filters idle
captains by an exact case-insensitive model match, then falls back to tier
routing. A pin is best-effort. Verify the assignment after dispatch when the pin
matters; benching is never required to pin a model.

### Read-only pipeline resolution

```yaml
topic: ops.standard-workflow.dispatch.readonly-pipeline
summary: An explicit pipeline keeps all stages for Audit/Research; only the core fallback collapses to one stage.
read_when: A report-only voyage lost its review stages, or you are resolving a pipeline for read-only work.
applies_to: [orchestrator]
tier: leaf
```

An explicit pipeline keeps all declared stages for Audit and Research. Mode
controls the deliverable and Check requirements, not the graph. The core
dispatch fallback differs: when all missions are read-only and no pipeline id
reaches the core resolver, it skips defaults and uses a single stage. Link the
objective and its pipeline through the shared dispatch path, and inspect preview
first. Do not infer the pipeline from mode alone.

---

## Stage handoff is verified, not assumed

```yaml
topic: ops.standard-workflow.stage-handoff
summary: A stage proves it contains the upstream commit; stage_base_missing is a provisioning fault, not the stage's.
read_when: A stage failed stage_base_missing, or a downstream stage looks like it is missing upstream work.
applies_to: [orchestrator]
tier: leaf
```

A downstream stage inherits its predecessor's branch. Inheriting a branch NAME
is not inheriting its commit: a local ref can predate the upstream push, and the
worktree looks correct while missing the work.

Armada proves containment before the captain starts. A stage whose checkout
lacks the upstream commit fails immediately with `stage_base_missing`, naming the
commit, the branch, and the upstream mission, and says the fault is provisioning,
not the stage's own work. Read that before the diff.

Not failed, deliberately: a mission with no upstream stage; a dependency in
another vessel (commits are not shared across repositories); an upstream that
produced no commit (Audit, Research) — unverified, proceeds, logged; an ancestry
probe that could not answer — unverified, proceeds, logged. The git ancestry
probe answers true, false, or UNKNOWN, and its default for any implementation
that does not consult a real repository is unknown, so a stub cannot manufacture
a pass.

---

## The captain brief is bounded as a whole

```yaml
topic: ops.standard-workflow.brief-budget
summary: One bounded description feeds every module; the backstop elides content then reference modules, never the skeleton.
read_when: A brief reads OverBudget, or you are reasoning about what a captain actually received.
applies_to: [orchestrator]
tier: leaf
```

A persisted description grows through handoffs, rescues, and notes. The brief is
bounded where it is rendered, so no description pushes it over
`CaptainInstructionByteBudget`:

1. One bounded copy of the description feeds every module that embeds it.
2. Over budget, the backstop elides in order: content modules (objective scope,
   project context, style guide, playbooks, existing instructions); then every
   embedded copy of the description, keeping the head brief and the newest
   handoff block; then reference modules (skills, git anchors, code-index
   guidance). The persona prompt, rules, and output contract are never elided.
3. A rescue description caps the scope, the reviewer feedback, and the failure
   reason separately, so a gate log cannot dominate it.

The `mission.prompt_budget` event records the bytes written. `OverBudget=true`
after the backstop means the protected skeleton alone exceeds the budget; read
its module sizes before raising the budget.

---

## Authorization reaches every captain the same way

```yaml
topic: ops.standard-workflow.authorization
summary: Authorization policy renders verbatim in every brief with fixed hard limits; the refusal classifier and re-route.
read_when: You are setting an authorization policy, or a mission was recorded as a policy refusal.
applies_to: [orchestrator]
tier: leaf
```

Note: the hard-limit half of this topic (secrets, tenant isolation, protected
paths, destructive operations) is CORE and ships in every brief unconditionally.
The how-to below is the leaf.

Record the owner's authorization in the project profile's `authorizationPolicy`
field. The applicable profile is chosen vessel, then fleet, then global. Every
brief renders `## Authorization and Hard Limits` from that one field: the policy
verbatim, then fixed hard limits the policy cannot relax. The section is never
elided. Never put credentials or license material in the policy; it is copied
into every brief.

When a captain refuses, completion classifies the refusal first: the structured
`[ARMADA:RESULT] REFUSED: <reason>` line, then provider safeguard text, then
declining prose (never when a completion marker was written). Every refusal
records a `mission.policy_refusal` event.

| Condition | Outcome |
| --- | --- |
| A model declined and no owner policy applies | Normal completion handling |
| First refusal or safeguard block, an approved captain on another runtime | Requeued once as `mission.policy_refusal_continued`; every captain on the refusing runtime is excluded |
| Refused or blocked again after continuation | Fails with `policy_refusal:` and the reason |
| No approved captain on another runtime | Fails with `policy_refusal:` and the reason |

The blocked runtime is never retried and the captain is not benched. Autonomous
recovery does not rescue a `policy_refusal:` failure; route it by hand. The
continuation never weakens provider safety policy.

---

## A readable file is not a runnable one

```yaml
topic: ops.standard-workflow.execution-requirements
summary: preparation.executionRequirements are checked against the captain launch environment, not just an Admiral-readable path.
read_when: A dispatch was blocked with an execution_* finding, or a mission needs a runtime or licensed context.
applies_to: [orchestrator]
tier: leaf
```

Prepared research that needs a runtime declares it in
`preparation.executionRequirements`: `operatingSystem`, `architecture`,
`executables`, `dependencyPaths`, `isolationBoundary` (`Container` or `Host`),
and `licensedContext` (a name in `AvailableLicensedContexts`, never license
material). Preview checks them against the environment captains launch in, not a
path the Admiral can read, and blocks with one `execution_*` finding per missing
requirement. Preview never runs a declared executable; it resolves it on the
captain PATH. The scheduler and manual dispatch read the same preview.

---

## A quiet-host gate enumerates TERMINAL states (CORE)

```yaml
topic: core.quiet-host-gate
summary: Test "is it safe to act?" by the FINISHED set, never the busy set; a status added later then reads as active.
read_when: Always, before restarting the Admiral, rebuilding its image, or reclaiming docks.
applies_to: [orchestrator]
tier: core
```

This rule is CORE. It is shown here for context; it is never retrieval-gated.

Before any action that interrupts running work — restarting the Admiral,
rebuilding its image, reclaiming docks — check whether the host is busy. A
voyage is not only `InProgress`; it is also `Open`. A gate written
`where status = 'InProgress'` reports a false quiet while `Open` voyages run
captains, and acting on it destroys work.

Enumerate what is FINISHED and treat everything else as active:

```sql
select count(*) from voyages
where status not in ('Complete','Failed','Cancelled');
```

Check missions too, because a mission can outlive its voyage's terminal status:

```sql
select count(*) from missions where status in ('InProgress','Assigned','Testing');
```

State the finished set, not the busy set, for any "is it safe to act?"
predicate.

---

## 4.5 Monitor

```yaml
topic: ops.standard-workflow.monitor
summary: Subscribe to the watcher, not a foreground poll; signals reach a Pending stage only; the three-signal stall rule.
read_when: You are monitoring a live voyage, deciding whether a captain stalled, or sending a Mail/Nudge.
applies_to: [orchestrator]
tier: leaf
```

Dispatch is the start of the operator loop.

1. Subscribe with `scripts/autonomy/watch-armada.mjs`. Use `--voyage`,
   `--participant`, and `--exit-on-terminal`.
2. Treat each mission-change event as a stage boundary.
3. Read mission and captain logs when progress is unclear.
4. Use `armada_captain_diagnostics` before deep process inspection.
5. Inspect incidents and Checks when their events change.
6. Send `Mail` or `Nudge` only to a Pending downstream stage. A running stage
   has frozen its brief and will not read the signal.
7. Do not use a foreground polling loop; it blocks board messages and misses
   short stage boundaries.
8. On an `[ARMADA WAKE]` banner, pause, address the message, and acknowledge
   with `armada_mark_signal_read`.
9. Do not steer a terminal mission; restart, recover, or dispatch anew.

A quiet captain is not proof of a stall. A stall decision reads three signals:
the captain's output (heartbeat or provider progress); the newest write in the
mission dock worktree, excluding `.git` (a directory mtime counts, so a deleted
file is a write); and the committer time of the mission branch tip. If any
signal is inside the stall window, the captain is not stalled. The recovery Mail
nudge and the heartbeat-stall kill or restart call one shared evaluator, so a
runtime that streams nothing between tool calls is not nudged while its dock or
branch keeps changing. Each decision records `captain.stall_confirmed` or
`captain.stall_cleared` naming the deciding signal.

---

## 4.6 Verify with Checks

```yaml
topic: ops.standard-workflow.checks
summary: The host interlock; the Judge PASS real-signal gate; arming, staleness, supersession; resolve every failed Check.
read_when: You are creating or reading Checks, or a Judge PASS was held or rejected over a Check.
applies_to: [orchestrator]
tier: leaf
```

Create Pending Checks when the objective or voyage is created. Build and unit
test are the minimum for code changes. Add the vessel-profile gates the change
needs.

Armada runs at most ONE expensive command on a host at a time. Two suites at
once produce a burst of simultaneous sub-millisecond failures across unrelated
classes, usually classified Timeout, that reads like a real regression and
passes alone. A host-wide interlock serializes all four callers (a check run, a
pending check at the Judge stage, a definition-of-done gate, a merge-queue test
run). A check stays `Pending` while it waits for the host slot; `queueDurationMs`
is the wait, `durationMs` is execution — compare the two before calling a check a
slow suite. The interlock cannot see a captain's own dock-side suite or an
operator's SSH commands. Establish what a check used from its own output (the
restored project paths name the sibling), not by inspecting a shared sibling
afterward; any later run has moved it. When a wrong-value failure appears under
load, an isolated re-run is the discriminator.

A Build or UnitTest check runs in a private worktree cut from the vessel repo,
locked while it runs and named by the check run id. Readiness probes the program
each command segment starts with; shell syntax and a `$tool` variable are not
probed, but a real missing binary in a loop body is reported.

Use `run_check` to execute, `retry_check_run` for a real rerun, and
`resolve_check` ONLY when valid evidence was produced outside Armada — never to
hide a failure. A passing suite proves a fix only when the check covers the
original symptom; record before-and-after evidence for a defect.

A Judge PASS is backed by the real signal, not by the Judge's report. The gate
reads every Check attached to the voyage and to the Judge mission:

| Check state | Effect on a Judge PASS |
| --- | --- |
| All green | PASS stands |
| Any `Failed` | PASS rejected |
| Any `Pending` or `Running` | PASS held, then re-run in place |
| Armed, not yet run, voyage has a commit under review | Queued work: stamped at the reviewed commit, then held as for `Pending` |
| `Passed`/`Failed` for a commit other than the reviewed tip | Stale: held; the executor cancels it as superseded and arms a fresh one for the tip |
| None attached | PASS rejected unless the review carries `[JUDGE-CHECK-EXCLUSION]` |
| `Canceled` | Ignored |

At startup every `Running` record older than the new process is set `Canceled`
with a restart summary; nothing re-arms it, so re-arm a cancelled Build by hand
before the Judge stage. Cancelling a voyage marks its `Pending` Checks
`Canceled` (`voyage_cancelled`), and the executor applies the same to a Pending
record on a Cancelled or Failed voyage.

Dispatch arms the voyage's Build and UnitTest Checks, but only a type the
resolved profile defines, and never a type already attached (a second Build
beside a failed one would leave a green and a red, and one failed Check rejects a
PASS). Armed Checks are `Pending`, not executed; an armed record reads
`command = echo` with no branch until a stage commits and it is stamped with that
branch and commit — that is the correct armed state, not a broken stub. Once a
commit is under review, an armed record is queued work; the Judge gate stamps
every armed, never-run record at the reviewed commit and holds the PASS until it
runs. One rule, `CheckRunGateRules.ParticipatesInRealSignalGate`, decides this
for the Judge gate and the completion gate. A record with no branch and no
commit when its voyage ends is set `Canceled` (`unstamped_voyage_check`), never
run against the default branch.

A green is a statement about ONE commit. A voyage-armed Check is stamped at the
FIRST stage that commits, so by the Judge the only green may describe a commit
several stages back. The gate compares each `Passed` record's commit to the tip
the Judge reviewed and treats a mismatch as unresolved; it supersedes the stale
record (`Canceled` naming its successor, a fresh Pending armed at the tip, a
`check.superseded` event). A `Failed` record for an older commit holds rather
than rejects, because the reviewed commit may be its fix.

Resolve EVERY failed Check, not most of them. One record left `Failed` rejects
the PASS hours later. The rejection names the blocking Checks, each with the
commit it measured and a bounded, redacted output tail. Resolve an environmental
failure as `Canceled`, not `Passed`.

### The Slop Check

```yaml
topic: ops.standard-workflow.checks.slop
summary: A native reward-hacking Check on .NET vessels; added-line rules, FAIL vs WARN, and per-site slop-allow markers.
read_when: A Slop Check failed, or you must know what the reward-hacking gate flags and how to suppress a byte-exact case.
applies_to: [orchestrator]
tier: leaf
```

On a .NET vessel, dispatch also arms a `Slop` Check beside Build and UnitTest,
on every dispatch path. It catches a change that makes a build or suite go green
without fixing the problem. The Check runs in the admiral, so the captain runtime
does not matter. A vessel is .NET when a profile command invokes `dotnet` or
`msbuild`, or the working directory (or one below) holds a `.sln`, `.slnx`,
project file, `Directory.Build.props`, `Directory.Packages.props`, or
`global.json`. Slop is never armed alone: no Build or UnitTest Check means no
Slop Check. Armada's classifier reads the reviewed diff (merge base of the
default branch and the stamped commit versus that commit) and classifies only
ADDED lines in C# and MSBuild files outside `bin` and `obj`.

| Rule | Severity | Match |
| --- | --- | --- |
| `SkippedTest` | FAIL | `Skip =`, `Ignore`, `Assert.Skip`/`Ignore`/`Inconclusive`, `Skip.If`, or `#if false` in a test file |
| `ProjectWideNoWarn` | FAIL | A `<NoWarn>` element in a project/props/targets file |
| `CentralPackageVersionBypass` | FAIL | Inline `Version`, `VersionOverride`, or `ManagePackageVersionsCentrally` false when central management is on |
| `EmptyCatch` | WARN | A catch body empty or comment-only |
| `ArbitraryDelay` | WARN | `Task.Delay`/`Thread.Sleep` with a literal duration |
| `WarningSuppression` | WARN | `#pragma warning disable` or a `SuppressMessage` attribute |

An unsuppressed FAIL fails the Check and rejects a PASS. WARN never fails it;
WARN patterns are what a byte-exact source reproduction can legitimately contain.
Suppress a finding per site with a comment marker on the flagged line or the line
above naming the rule and a reason of at least eight characters:

```csharp
// slop-allow EmptyCatch: byte-exact reproduction of Decoder.cs:40-44
```

A marker with no reason, or naming another rule, is not honored. Also record the
owner decision on the objective. Every condition that prevents classification
fails the Check with a reason ending `Nothing was examined`; an empty reviewed
diff passes.

### Definition-of-done and consumer verification

```yaml
topic: ops.standard-workflow.checks.consumer
summary: The DoD gate skips a read-only no-commit mission and builds (and conditionally tests) every declared consumer.
read_when: A producer change may break a sibling consumer, or a gate failed consumer_tests_failed or consumer-build.
applies_to: [orchestrator]
tier: leaf
```

The definition-of-done gate does not run for a read-only mission that produced
no commit (proved by comparing the dock head with the provisioned commit); the
outcome event `Skipped` names `read_only_no_commit`. A read-only mission that did
commit, and every Implementation mission, run the gate.

The gate also builds the vessels that DECLARE this vessel as a sibling. The
consumer edge is derived from `SiblingRepos` in the opposite direction; nothing
extra is configured. For each consumer it provisions a private scratch root
(never a shared sibling path), checks out the producer's mission branch detached,
uses declared default branches for other siblings, runs the consumer's
`BuildCommand`, and cleans up pass or fail. The private root is load-bearing: a
shared sibling checkout another dock owns is reused, so verifying through it
could compile the consumer against another commit.

A build catches a compile break, not a runtime break (an empty catalogue, a
reordered public shape a test oracle pins, a changed frame). So the gate also
runs the consumer's `UnitTestCommand` in the same worktree when a changed
NON-TEST producer file falls under a triggering path prefix
(`ConsumerTestTriggerPaths` on the sibling declaration, else
`DefinitionOfDone.ConsumerTestTriggerPaths`). A failing consumer suite fails the
producer gate `consumer_tests_failed: <consumer>`, distinct from a build failure
`consumer-build (<consumer>)`. A consumer that cannot be PREPARED is an
infrastructure fault: logged, gate passes, unless
`DefinitionOfDone.FailOnConsumerVerificationError` is set.

---

## 4.7 Review and land

```yaml
topic: ops.standard-workflow.review-land
summary: Judge follow-ups are durable; the landing-mode table; LocalMerge drift and the recover branch; merge-queue verification.
read_when: You are reviewing a mission, processing a merge entry, or a landing reported diverged or not-found.
applies_to: [orchestrator]
tier: leaf
```

Read the mission diff and logs. Drain the audit queue and record the verdict
when needed. Check the merge entry before processing it.

A Judge `NEEDS_REVISION` always creates a durable follow-up; any other verdict
creates one when it has a non-empty Suggested Follow-ups section. Armada stores
it before it looks for a merge entry, so the queue can return the item with no
`entryId`; record its verdict with `followUpId`. If a transient fault predates
this path, run `armada_backfill_judge_followups` with `dryRun: true` first, check
`incomplete` and `errors`, then run the same range with `dryRun: false`; a second
pass must report zero `created`.

Use `armada_process_merge_entry` for one reviewed entry. Use
`armada_process_merge_queue` only to start queue processing; it returns a job and
can no-op when a run is active.

Landing behavior comes from the effective landing mode:

| Mode | Result |
| --- | --- |
| `LocalMerge` | Merge into the configured working checkout. Do not push unless separate policy permits. |
| `PullRequest` | Create or update provider review state. |
| `MergeQueue` | Use the durable integration, test, and landing state machine. |
| `None` | Leave produced work for explicit operator handling. |

`LocalMerge` merges in a temporary integration worktree under
`<docks>/_integration/<mission>`, detached at the target tip, then advances the
target by a compare-and-swap. If the target moved, git refuses and the landing
retries as drift. A refusal carries a class: `worktree_conflict:` names the
worktree holding the ref; `integration_merge_failed:` covers a content conflict.
After the merge, `LocalMerge` fast-forwards the working checkout. If the checkout
holds commits the target lacks, the fast-forward cannot run and those commits
exist nowhere else: Armada does not reset and does not push, but pushes the
checkout HEAD to `recover/working-checkout-<12-char-sha>`, emits
`landing.working_checkout_diverged`, and opens one incident naming the recover
branch, the SHA, the commit count, and the repair steps. `docs/MERGING.md` has
the commands.

Do not infer a successful landing from a `Complete` label. Verify the target
branch or remote commit. Merge-queue landings need extra verification: a
"branch not found" from `armada_process_merge_entry` may mean success or
failure — verify with `git merge-base --is-ancestor <sha> <target>` in the bare
repo before re-enqueueing. Land entries for one vessel+target one at a time.
After a batch, confirm the pre-batch tip is still an ancestor; a landing rebuilt
from an older base can drop sibling commits.

Operator branch push and merge act on the landing repository, not a dock (vessel
page or `POST /api/v1/vessels/{id}/branches/push` and `.../merge`; no MCP tool).
A merge moves only a local ref; a push is separate and goes only to `origin` when
its URL matches `RepoUrl`, never force or delete, refusing a non-ancestor tip as
`non_fast_forward`. Both refuse on a dirty or detached checkout, on a non-Complete
mission source, and on a protected or release target. A write that finds the
per-vessel slot held returns `vessel_busy`.

---

## 4.8 Close the record chain

```yaml
topic: ops.standard-workflow.close
summary: The six steps to close an objective: link voyages, Checks, release, incidents; record deferred work and the outcome.
read_when: A voyage landed and you are closing its objective.
applies_to: [orchestrator]
tier: leaf
```

Before the objective becomes complete: link the final voyage and missions; link
the passing Checks; link a release and deployment when work shipped; link
incidents and their final evidence; create a new record for every deferred task;
update the objective summary with the verified outcome.

---

## 4.9 Sweep the papercuts

```yaml
topic: ops.standard-workflow.papercuts
summary: Read papercut reports on a schedule; the distinct-captain count is the signal; route by category; keep promotion manual.
read_when: A voyage closed, or it is the weekly sweep, and you are triaging captain friction reports.
applies_to: [orchestrator]
tier: leaf
```

Captains report friction on an `[ARMADA:PAPERCUT]` line. Armada stores each as a
`papercut` event with the reporting mission, captain, vessel, and voyage. Read
them on a schedule; an unread report is worse than none.

1. Run `armada_list_papercuts` after a voyage closes, and weekly with
   `sinceHours: 168`.
2. Read the count and the distinct-captain count first. One captain is an
   anecdote; several is a defect with evidence.
3. Route by category:

   | Category | Owner |
   | --- | --- |
   | `MissingDoc`, `BrokenLink`, `RepoFriction`, `TestFlake` | Backlog item on that vessel |
   | `EnvSetup` | Dock or workflow-profile fix, then a Check to prove it |
   | `BriefContradiction`, `PlatformBug` | Armada objective, direct-edit only |
   | `ToolFailure` | Read the mission log first; a captain calling a tool it never received is a `BriefContradiction` |

4. Quote the group in the record: count, distinct-captain count, sample title,
   sample mission ids.
5. Keep promotion manual. A high count is not authority to dispatch.

A `BriefContradiction` group is a captain-quality defect (fix the instruction
module, not the repository). A category one runtime reports and no other does is
usually about that runtime. Judge missions do not file papercuts; a Judge reports
through its verdict.

---

## 4.10 Campaign work

```yaml
topic: ops.standard-workflow.campaign
summary: An opt-in objective tree for one large effort; optional fair-share scheduling; the five operating rules.
read_when: You are running or scheduling a multi-slice campaign, or reading armada_campaign_status.
applies_to: [orchestrator]
tier: leaf
```

A campaign is an opt-in objective tree: a root tagged `campaign:<name>`, lane
children per source or area, and slices beneath. Plain objectives outside any
campaign remain the default.

The scheduler can use optional campaign fair-share
(`fairShareWithinPriorityBands=true`, false by default). It never moves a
lower-priority slice ahead of a higher one; in one band it rotates across tagged
roots and keeps rank and id order inside each campaign.

Operating rules: claim the slice (`armada_coordination_claim`) before starting,
heartbeat while working, release when done; encode wave ordering with
`BlockedByObjectiveIds`, not prose; attach the campaign rules playbook through
`selectedPlaybooks` on dispatch; on landing, link EvidenceLinks (commit SHAs,
green Checks) and the record the slice extended (no evidence links means not
done); answer "where does this stand" with `armada_campaign_status`.

---

## 4.11 Helper sessions and operator ownership

```yaml
topic: ops.standard-workflow.helpers
summary: An operator owns its host-side helpers' full lifecycle; the bounded spawn-helper launcher; strict MCP delivery; one owner per key.
read_when: You are spawning or managing bounded host-side helpers, or a helper has no Armada tools.
applies_to: [orchestrator]
tier: leaf
```

An operator session that starts host-side helpers owns their full lifecycle. Use
`scripts/autonomy/spawn-helper.sh` for bounded helpers:

```bash
scripts/autonomy/spawn-helper.sh spawn census <task-file> <repo>
scripts/autonomy/spawn-helper.sh offer ready <fallback-file> <operator-session> <repo>
scripts/autonomy/spawn-helper.sh list
scripts/autonomy/spawn-helper.sh kill census
scripts/autonomy/spawn-helper.sh cull
```

The launcher enforces `AUTONOMY_MAX_HELPERS` (default 2), records PIDs and
participant keys under `AUTONOMY_WORKDIR`, and culls sessions older than
`AUTONOMY_HELPER_TIMEOUT_MIN` (default 90). It supports `opencode`, `claude`, and
`codex`. Every prompt gets a fixed contract: use the generated participant key,
drain and acknowledge wakes, stay read-only, post one outcome, release claims,
exit. Run `scripts/autonomy/test-spawn-helper.sh` after launcher changes.

`offer` mode posts availability to a named operator for a bounded four-minute
window; the helper checks for directed Wakes at most every 25 seconds, then runs
the fallback, accepts a replacement, or stands down.

Claude helpers run strict MCP isolation. The launcher writes a private
Armada-only MCP file and passes it with `--mcp-config`; strict mode without the
explicit file gives the helper zero Armada tools. The default URL is the
loopback admiral endpoint; override with `AUTONOMY_ARMADA_MCP_URL` or
`AUTONOMY_CLAUDE_MCP_CONFIG`. The helper's working directory is its file-sandbox
boundary; give it the narrowest directory that holds all required evidence. Do
not disable the sandbox to repair a bad working-directory choice.

The board defines the roster: participants present, active claims, and their
difference (present without a claim is idle). Hand a live helper work with an
addressed note (`toParticipantKey`), or stand it down explicitly (a
script-managed helper must then exit, not poll). Re-check the roster between loop
iterations. Do not register a script-managed helper for AgentWake: one process
owns one participant key. OpenCode AgentWake sessions are always fresh, so the
addressed note must carry the complete task and the bootstrap must reconstruct
context from the board and durable memory.
