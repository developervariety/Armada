---
topic: "Pipelines"
summary: "Built-in pipeline stage lists, resolution order, and execution barriers."
read_when: "Choosing a pipeline for a voyage, or building a custom stage sequence."
applies_to: orchestrator
tier: leaf
---
# Armada Pipelines

A pipeline is an ordered set of persona stages. Stages with the same order run
as parallel siblings. Use [OPERATIONAL_ASSETS.md](OPERATIONAL_ASSETS.md) for
the complete operator procedure.

Parallel same-order stages dispatch concurrently and the next order barriers on
the whole group: a downstream stage (for example a Judge) starts only after
every sibling reviewer in the previous order has reached a successful terminal
state. Parallel stages run on one vessel, so the vessel must allow concurrent
missions (`allowConcurrentMissions: true`); with the default false, the second
sibling waits for the first to finish, which makes the "parallel" stages run
sequentially. Same-order stages also require enough idle captains to serve them
concurrently.

## Built-In Pipelines

| Pipeline | Stages |
| --- | --- |
| `WorkerOnly` | Worker |
| `Reviewed` | Worker, Judge |
| `Tested` | Worker, TestEngineer, Linter, Judge |
| `FullPipeline` | Architect, Worker, TestEngineer, Judge |
| `ProductDevelopment` | Product Manager, Architect, Worker, Usability Engineer, TestEngineer, Linter, Judge, Recorder |
| `Recorded` | Worker, Recorder |

The startup seed service creates these definitions on a fresh install and
reconciles an active one back to its definition on every start. Specialist
pipelines are deployment configuration, seeded through `additionalPipelines`
with matching personas and templates.

An operator retires a seeded pipeline by deactivating it (`active: false`). The
seed leaves an inactive built-in pipeline exactly as it is, so the retirement
survives a restart; reactivating it hands it back to the seed. A same-name
pipeline the seed never owned (not built in) is adopted and brought to its
definition, active or not. A pipeline the seed declares
cannot be deleted, because the next start would re-create it, and the refusal
says so. A pipeline that was seeded as built in but that neither the code nor
`additionalPipelines` declares any more stops being built in at the next start,
and can then be deleted like any other pipeline.
See [Operational Assets](OPERATIONAL_ASSETS.md#8-pipelines).

## Resolution

Dispatch can name a pipeline. If it does not, Armada resolves a vessel default,
then a fleet default, then `WorkerOnly`. A source repository can need a stronger
default. Configure a specialist pipeline when approved reference
material is a normal part of that vessel's work.

The first of these references that the vessel's owner may use decides. When it
names an inactive pipeline, the dispatch is refused with the code
`pipeline_inactive`, naming the pipeline and whether it was the requested
pipeline, the vessel default or the fleet default. Armada never substitutes the
next default or `WorkerOnly` for a retired pipeline. The objective dispatch
preview reports the same pipeline as a `pipeline_inactive` blocking issue. A
rescue that resolves the live pipeline (see "Rescue stage list" below) and finds
it inactive is not created and spends no recovery attempt; its incident names
the pipeline.

An objective's suggested pipeline and a vessel's or fleet's default pipeline
cannot be set to an inactive pipeline, on every write surface (MCP, REST and the
dashboard socket). Only a changed reference is checked, so a record whose
pipeline was retired later still accepts edits to its other fields; clear or
replace the reference to dispatch it again.

Read current fleet and vessel settings before a default change. Do not infer a
default from a previous voyage.

Objective preflight question 1 checks ordinary `path:line` citations at the
target repository revision, the resolved start commit. When that commit is not
an ancestor of the vessel's default branch tip, each citation that fails there
is checked again at the branch tip, and a citation that resolves at either
passes; the fact names the branch and commit where it resolved, and a failure
names both refs. Every question-1 fact names the commit it read. A citation the
target does not track is checked in
the trees a dock provisions beside the repository. A `../<Sibling>/...` path, or
a path whose first segment is the directory name of a sibling the target vessel
declares, is checked at that sibling's verified tip, and under its declared
extraction artifact paths in the sibling's working checkout. A bare file name is
searched in every declared sibling; a name that matches several of a sibling's
files follows the same candidate rule as in the target repository (below), so
it is never reported as a missing file. A path whose first segment names an
evidence root that applies to the vessel is checked under that root. Armada
rejects paths that traverse above the root they name. A miss under a declared
sibling or evidence root is named against that root, and the fact reports
`FailsOnlyOutsideTarget` when every failure lies outside the target repository;
the Jev q1 reading on such a fact is a warning, not a blocking flag.

A cited path resolves when the revision tracks it as a file or a directory,
exactly or as a unique suffix. A path that resolves only after ignoring letter
case fails the question and names the tracked spelling. A name that matches
several files (a bare `Program.cs:2610` in a repository with two `Program.cs`)
is checked against every candidate: when the cited lines hold in exactly one,
the question passes and the detail names that file; when they hold in several,
it passes and the preview adds a `preflight_citation_ambiguous` Warning listing
them; when they hold in none, the question fails and lists the candidates. A
failed question names each failing citation as written (`file:line` or
`file:start-end`, or the identifier) with its reason: file missing, line out of
range, ambiguous file name, or outside the declaration span. Question 1 also reads
each cited line range, reading a file stored through Git LFS as its content
(the working-tree file or the LFS object store copy whose size and SHA-256 match
the pointer; when neither holds it, the detail notes the citation as not checked
and the question does not fail on it): a range past the end of its file fails, and so does a
range that no longer holds the symbol the text pairs with it, written as
`` `Symbol` (`path:line`) `` or `` `Symbol` at `path:line` ``, or named as the
one backticked identifier or call expression in a sentence that cites nothing
else. The symbol is matched as a whole word. A range that lies inside the span
of one declaration of its symbol holds, so a brief may cite lines of a method
body by the method's name. The span runs from the declaration line to the brace
that closes its body (braces in strings, character literals and comments do not
count), or, for a declaration line that ends with a colon, to the end of its
indented block. A range that crosses into the next declaration does not hold.
The detail names the line where
the symbol is now: the declaration of the symbol nearest the cited lines, else
its occurrence nearest them, never an earlier call site. A symbol within 10
lines of its cited range still names the right code, so the question passes and
the preview adds a `preflight_citation_drift` Warning with the corrected line.
A backticked Armada record id (`obj_` and the other record prefixes) is a record,
not code, and is not searched for in the tree.

## Execution

Armada creates missions for the first stage order. When all sibling missions
at that order reach a successful terminal state, Armada advances to the next
order. A required failure stops normal advancement. The operator must inspect
the failed mission, evidence, and recovery options.

Every stage of a pipeline runs; a stage has no optional flag. To leave a stage
out, remove it from the pipeline, or skip it for one dispatch or objective with
a recorded stage skip (`skipStages` with a reason), which every later stage and
rescue honours. A skip entry names one stage by persona, by stage order (`5` or
`#5`), or by persona and occurrence (`Worker#2`); use the order or occurrence
form when two stages share a persona, such as a first Worker and a revising
Worker. A revising Worker runs unless it is named, even when every reviewing
stage before it is skipped.

An objective's confirmed stored skip is its default: a dispatch that names no
`skipStages` applies it, and `skipStages: []` skips nothing. A default entry
that names no stage of the chosen pipeline is dropped and reported
(`stage_skip_default_dropped`), so a default written for a longer pipeline does
not refuse a dispatch on a shorter one. A `skipStages` entry the dispatch
itself names must still name a stage of the chosen pipeline.

A stage whose final result is `[ARMADA:RESULT] BLOCKED` is a failure of this
kind: it does not hand off, its later stages are cancelled, and no rescue runs.
Its question goes to the owner on an incident and a board note. See
[PERSONAS.md](PERSONAS.md#a-stage-that-ends-armadaresult-blocked-waits-for-the-owner).

An Architect stage hands off one Worker chain per mission block in its
output. A block opens with `[ARMADA:MISSION]` at the start of a line and ends
at `[ARMADA:MISSION-END]` (or the older `[/ARMADA:MISSION]`), at the next
block, or at the end of the output; a marker inside a sentence is prose. The
handoff and the `armada_parse_architect_output` tool read blocks by this one
grammar. A plan written as a numbered list with no marker at all is read one
mission per numbered line, and the handoff records
`mission.architect_plan_fallback` when it does; an output that carries any
marker is never read that way, so the numbered steps inside a block stay one
mission. Stored output is redacted for secrets, and a quoted span that is not
a JSON string is treated as prose: it keeps its text and plan markers, and only
key-shaped values inside it are removed.

### Structured Architect plans

Architect output can also use a fenced `armada-plan` JSON contract with an
explicit `contractVersion` of `2` or `3`. This format is opt-in; the default
Architect brief still requests mission markers. The parser and handoff use the
same contract checks and the vessel's mission limit. The last structured revision
is authoritative. An invalid, unsupported or unclosed last revision stops
handoff; it cannot fall back to an older revision or a numbered summary.

The JSON plan names requirements, resolved decisions and ordered missions.
Each mission supplies its kind, title, description, acceptance criteria and
validation commands. Criteria name the requirements they cover, a scenario,
an expected result, a verification method, and negative cases or a reason that
none apply. Version 3 also links criteria to decisions and requires decision
coverage. Optional numeric `dependsOn` values refer to earlier missions.
The JSON format does not select a model; normal routing supplies that choice.

The final mission has kind `integration` and sets `waitForOtherMissions` to
`true`. It uses an explicit integration barrier. The barrier records the exact
branch roots and terminal stages before fan-out is complete. It does not add
unrelated or later voyage missions to that set. Accepted branch membership is
fixed; a verified rescue can replace only a branch's current terminal identity.
Each plan branch must have one unambiguous pipeline chain. A branch with
multiple terminal paths is held for review instead of selecting one path.

Integration waits until every accepted branch is `Complete` and its source
commit is proved present on the vessel's current landing target. `WorkProduced`
and an open pull request do not satisfy this rule. A fresh integration starts from the
captured target commit, with no inherited branch from one parallel mission.
Verified retries keep their own integration branch and pinned start commit.
Missing continuation evidence holds the mission instead of replacing its work.
This continuation proof checks the saved branch and its active dock. It does
not rebase the branch when the target moves; normal landing validation still
checks integration against the target used for landing.
This path requires provable local `LocalMerge` or `MergeQueue` landing.
An unavailable repository, uncertain ancestry or Harbor-owned checkout holds
integration with a named reason. It does not fall back to another host.
Legacy marker plans keep their existing dependency and handoff rules.

When the vessel's workflow profile declares `gateInputPaths`, the Architect
brief lists them in a `Gate Input Files` section, and a plan whose blocks name
none of an entry records `mission.architect_plan_gate_inputs_missing` with the
unnamed entries. The plan still hands off. See
[OPERATIONAL_ASSETS.md](OPERATIONAL_ASSETS.md) for the field.

New generated handoff blocks have matching mission-specific opening and closing
markers. The closing marker follows the voyage board notes. Repeated preparation
replaces only a complete matching block; compaction also stays inside that pair.
Plain operator text outside the pair stays unchanged. Legacy or malformed blocks
with no matching close remain whole because their operator-note boundary is unknown.

Each handoff block ends with a report-essentials section for the stage that
just finished: the complete-output reference (`mission-output:<id>`) with its
length and UTF-8 SHA-256, the verdict, every blocking finding, and the
follow-up, residual and added-tests sections, within a 4,000-character bound.
It sits after the bounded output preview and the diff. Description, metadata,
and total-budget trimming pin each report heading and complete-output reference
before trimming the narrative. Voyage board notes cannot displace those
references. Compaction of older blocks retains their report essentials.

Blocking findings are read from the Judge sections (Correctness, Failure Modes,
Tests, Evidence, Residual Risks, Verdict and the criteria sections) and from
the headings other reviewers use: `Code Correctness`, `Findings`,
`Required Changes`, `Blocking Findings` and `Blocking Issues`. A whole-line
bold label such as `**Correctness**` opens its section like a heading. When the
final `[ARMADA:VERDICT]` is `NEEDS_REVISION` or `FAIL` and no finding can be
read, the list is the incomplete-findings marker, so a stage is sent to the
complete output instead of being told there is nothing to fix.


### Full Research attempt replay

A global operator can preview `POST /api/v1/voyages/{id}/research-replay`
with `dryRun: true` and an exact lowercase `sourceCommit`. The supported
inventory is one finalized Failed Research voyage with exactly six original
roles in order: Worker, PortingReferenceAnalyst, TestEngineer, Linter, Worker,
Judge. The Judge must be Failed; all six captures must be retained, terminal,
unheld, native and pinned to that source. Any Harbor job, landing lineage,
Architect integration barrier, active captain, unresolved Check or retained
process binding refuses replay. A foreign tenant, owner or vessel is refused.
This operation does not broaden ordinary restart or reset recovery budgets.

Apply with `dryRun: false`, a unique `requestId`, and the preview's
`expectedFingerprint`. One provider transaction archives every prior report,
status and chronology, playbook snapshot and current stored criteria before
resetting the same six mission IDs and reopening the same voyage. There is no
new voyage or substitute role. Changed state refuses the apply. Retrying an
identical committed request returns its receipt and never erases new work.
The current output starts empty; prior output cannot satisfy a current duty.
Each replay uses new branch names, verifies the exact source before launch,
and fences delayed prior-attempt writes and completion callbacks. Raw logs
and final-message paths use the replay generation, so launching a new replay
does not replace the prior attempt's files or read its success markers.

`armada_replay_research_voyage` exposes the same preview/apply operation.
`armada_research_replay_output` and
`GET /api/v1/research-replays/{replayId}/missions/{missionId}/output` page the
immutable historical output with normal redaction and digests. Read through
`HasMore: false`; `PriorResearchAttempt` labels its historical status and
chronology. Only the exact owner operator or a mission in the same current
replayed voyage can read this history. New briefs name the archive identity.
Follow `PriorResearchAttempt.PreviousReplayId` for earlier archives of this
same owned voyage. The existing mission-output tool reads the current attempt.

Capture is bounded to 16 MiB and refuses oversized history without resetting
anything. Available top-level delivered instruction files are retained, up
to 2 MiB each and 8 MiB total. `ArchiveLimitations` states missing historical
configuration: original runtime settings/template versions, external
supplements and sibling contents are not reconstructed or claimed as archived.
A preview-time opaque `ConfigurationDigest` covers the resolved workflow and
current gate/recovery/capacity policy; raw settings and environment values do
not enter the archive. Settings and external source inputs are not modified by replay. The operator
must retain and recheck required source inputs in the normal fresh preflight.

The append-only `research_replays` table stores the owner, voyage, unique
request key, versioned snapshot digest, private snapshot, durable receipt and
creation time. `missions.research_replay_id` is a generation fence; ordinary
mission writes cannot change it. Migration heads are SQLite 132, MySQL 125,
PostgreSQL 136 and SQL Server 128.

A handoff block carries the stage the dependent names. In an Audit or Research
pipeline every stage reviews the same root report, so a report-only dependent
also gets one `## Earlier Stage Reports` section, placed before its handoff
blocks. It holds the root report inlined whole up to 24,000 characters (a
head-and-tail preview past that, with the verifiable artifact reference), and
the report essentials of every stage before the prior one, oldest first,
including parallel siblings that shared an upstream. Root missions of other
chains in the same voyage are never included. The section is rebuilt on each
handoff, and compaction of handoff blocks leaves it whole. The prior stage's
own report is inlined whole up to the same 24,000 characters, and a report-only
description has an 80,000-character narrative budget instead of 20,000; the
captain brief embeds a bounded copy, and the full description is written into
the dock as files the captain reads.

A handoff past that budget is cut in the mission record: the head and the
newest tail stay, and the middle is elided. The complete handoff text is kept
on the Admiral under `logs/handoffs/<mission id>.md` and staged into the dock
under `_briefing/handoff-full/` as bounded files that join back to the exact
text. The cut marker names `_briefing/handoff-full/01-handoff.md`, and the
brief's metadata marker names it too, so the next stage reads the whole diff,
verdict and test list from its own checkout. The brief keeps its cut, so its
size does not change. A later handoff that fits removes the kept text, and a
mission purge deletes it.

A report-only pipeline can revise its report. A Worker stage whose own chain
already holds a Worker is told to revise the root report: address every
reviewer finding or reject it with evidence, and publish the complete revised
report. A stage after that revision reviews the revised report as the
deliverable: a Judge checks that each finding was addressed, and the earlier
stages section names the root report as the earlier version. Workers of other
chains in the same voyage are never counted.

A stage's own `Description` is appended to its mission description at dispatch
as a `## Pipeline Stage` section ("Stage N of M (Persona): ..."), so two
stages of one persona are told apart. A stage without a description leaves
the mission description unchanged.

A mission's dock outlives its captain while the mission still lands or is
approved from it: a mission in `WorkProduced`, `PullRequestOpen` or `Review`
keeps its dock through stall recovery and the orphan-dock reaper, and the disk
sweep skips its orphan-dock scan for a pass in which the protected docks
cannot be read.

A Pending stage that the assignment pass holds records why on the mission:
`AssignmentHoldReason` names the reason and `AssignmentHoldEntityId` names the
record that holds it, usually the predecessor stage. `armada_mission_status`,
`armada_voyage_status` and the dashboard mission page show both. The reasons are
`dependency_unfinished` (the predecessor has not finished), `dependency_missing`,
`parallel_stages_running`, `cross_vessel_dependency_not_landed`,
`architect_handoff_pending`, `architect_sequenced`, `dependency_held_for_review`,
`dependency_gate_result_missing`, `predecessor_dock_held` (an earlier stage of
the chain still has the inherited branch checked out in its dock),
`no_eligible_captain` (idle captains exist but none may take the mission; see
[Usage Routing](USAGE_ROUTING.md#layer-1-eligibility)), and
`dependency_completion_in_flight`. The last
one is the hold after a predecessor produced work while its completion handling
runs: the definition-of-done gate, which re-verifies every declared consumer
repository when a later stage commits production changes, then the handoff. On a
vessel with consumer suites this hold can last many minutes. Every reason except
`dependency_unfinished` records `mission.assignment_held` once when it starts or
changes, and `mission.assignment_hold_released` once when assignment proceeds;
each event's payload carries the reason and the holding record. A hold that lasts
many assignment ticks records one event, not one per tick.

A mission that depends on a stage in another voyage waits on that stage. When
that stage was cancelled or failed and an autonomous rescue of its voyage
completed, the dependant waits on the rescue's completed stage of the same
persona instead, preferring the rescue stage at the original stage's order when
the pipeline names that persona at more than one stage; the change is recorded
as `mission.dependency_rewired`, and the original stage stays in the record. An objective whose failed voyage was
recovered reconciles to `Completed` once the rescue voyage completes: a rescue
of a failed stage also recovers the failed stages above it in the same chain,
because it started from that stage's commit and re-ran them.

### Rescue stage list

Every dispatch records the stage list it resolved, before operator stage skips,
as one `voyage.pipeline_recorded` event on the voyage: the pipeline id and name,
and each stage's order, persona, description, review settings and preferred
model. Stage skips are recorded beside it as `voyage.stage_skipped` events. A
rescue anchors on and continues from the failed voyage's recorded list, so a
pipeline edited, deactivated or deleted while the voyage runs, or an objective,
vessel or fleet re-pointed to another pipeline, does not change which stages the
rescue runs. The rescue voyage records the same list, so a rescue of a rescue
anchors on it too.

A voyage with no recorded list (one dispatched before recording existed, or a
bare voyage created without a pipeline) is rescued from the live pipeline: the
owning objective's suggested pipeline, then the vessel and fleet defaults. That
rescue records a `rescue.live_pipeline_used` event on the failed mission that
names the voyage and the pipeline it used.

`update_pipeline` and `delete_pipeline` never refuse an edit for a running
voyage. Their responses carry `RunningVoyageCount`, the Open and InProgress
voyages whose recorded list came from the pipeline, so the operator sees which
running work keeps the old stages. REST `PUT` and the dashboard socket's
`update_pipeline` and `delete_pipeline` return the same count; REST `DELETE`
answers 204 with no body.

A rescue takes the place of one pipeline stage and re-runs the stages after it.
A pipeline can name one persona at more than one stage (for example Worker,
analyst, TestEngineer, Worker, Judge), so the failed mission's stage order
chooses the stage. A rescue of the failed persona replaces the last stage of
that persona at or before the failed order. A rescue in another persona (a
Worker that revises a reviewer rejection) replaces the last stage of its persona
before the failed order, so the reviewer runs again in the chain. A Judge
rejection in that example is rescued at the second Worker and re-runs only the
Judge. The rescue takes the order, tier and review settings of that stage. A
mission with no stage order, or with no matching stage before it, uses the first
stage of the rescue persona.

A rescue for a reviewer rejection is a Worker started from the reviewed
commit. The brief carries a digest-backed pointer to the full Judge output and
instructs every rescue stage to read it with `armada_mission_output` before it
edits or re-verifies. The tool returns the safely redacted output in pages; the
stage continues until `hasMore` is false and checks `complete` and `sha256`.

Every rescue stage receives the current owning objective's canonical brief.
The objective named by the original voyage takes precedence over an umbrella
that lists the same voyage. Save binding revision requirements on the objective;
coordination chat does not become an acceptance contract.

Rescue admission checks declared execution requirements through the same
read-only environment probe as objective preview. A missing requirement leaves
an `execution_unavailable` incident and event, creates no rescue work, and spends
no recovery attempt. Sweeps can reconsider it within the original failure's
lookback window. After that window, an operator dispatches a new voyage or rescue
from the objective and preserved work. Executable and path checks do not prove
that a Docker daemon or database service is reachable.

The rescue findings list includes numbered or bulleted defects from
Correctness, Failure Modes, Tests, Evidence, Residual Risks and Verdict, plus
existing `NOT DELIVERED`,
`NOT MET`, `NOT RESOLVED` and `Blocking` items. It keeps file and line anchors,
includes unbulleted `R<number>` and `P0`–`P3` findings with their explanation,
includes a Correctness, Failure Modes or Tests paragraph that names a defect
(a required revision, an incorrect or unsupported claim, a proof or boundary the
change does not establish) or asks for a fix, excludes narration of what was
reviewed, bare lead-in lines and passing test summaries ("C#: 1,389 passed, 0 failed.", "Lint: 0 issues."), places NOT MET criteria after defects, removes
MET criteria and narration before shortening, and uses one list marker per item.
The line that introduces a list decides what its items are: items under a lead-in
that reports passing, agreeing or reviewed behaviour ("The core logic is sound:", "What the
new tests cover:", "**Spot checks that agree with source:**") or that introduces the steps of a counterexample or
reproduction ("Concrete counterexample:", "Steps to reproduce:") are context unless the item itself names a defect, items under a
minor or follow-up lead-in are left out, and an item marked "(not a blocker)", or
one whose closing sentence is only such a label ("Follow-up.", "Follow-up, not a
blocker.", "Cosmetic.", or a closing sentence that calls it a note or not a defect), is left out.
A lead-in covers only its own list and is read by its closing sentence. An item is also left out when it
opens with a lesser label ("Minor, not blocking:", "Observed but not blocking:"), when its bold label
disclaims a defect, when it closes "Reviewed and tested.", and when it reports agreement ("matches the
source", "is otherwise sound", "unchanged", "tested implicitly") without naming a defect, unless it is
marked blocking or NOT MET. A bold label that names a defect ("**Source fidelity defect (blocking).**")
marks a finding, and so does a paragraph under a lead-in that names defects ("Confirmed defect:"). In the
Verdict, lead-in lines and `[ARMADA:` marker lines are not findings, and required changes written inline
in one paragraph ("(1) replace ...; (2) add ...") become one finding each. The brief numbers the findings in review order. When the list does not fit its
4,000-character bound, the list keeps whole findings and drops whole ones, lowest priority first: plain list
items that name no defect, then NOT MET criteria, then named defects. A finding is never cut. The list ends with
a line that says how many findings it dropped and that it is incomplete.
After the original description, the brief ends with a `Demands:` block: one demand per numbered finding, the fixed rework
demands (keep the branch, change only what the findings require, rerun the checks, commit on the existing
branch), and the output contract last.
The embedded full-review
cap is 12,000 characters. When the report fits, the brief carries it whole.
When it does not, the brief keeps the protected sections whole after removing
narration and MET criteria. Every other section (Completeness, Acceptance Criteria
and the like) is reduced to its NOT MET, NOT DELIVERED and NOT RESOLVED lines under
its heading, and the closing note names each reduced section. If those protected sections exceed the bound, the
brief labels the embedded list incomplete and directs the stage to the complete
parent output. Gate-log truncation keeps its existing cap and content-filter
recovery behavior.

Description, metadata and total-budget trimming keep each generated rescue-root
artifact reference with its ID, length, digest and exact read notice. The
original rescue description remains available as bounded narrative.

The rescue brief embeds the failed mission's description without its persona
role section (the `## Your Role:` heading and its paragraph) and says so. A
rescue of a failed Judge, Linter or TestEngineer is a Worker that fixes the
work, so it never receives the reviewer's role text as its own brief.

A rescue that carries a review (a reviewer rejection) states, before the
embedded description, that the review decides what the rescue changes. The
embedded description and its objective can say the work is already done, for
example "restart from preserved work" or "do not re-implement". Such text keeps
the work already present and stops a rebuild, but it does not cancel the
revisions the blocking findings require. When a required revision would change
something an objective non-goal forbids, the non-goal wins: the rescue does not
make that revision and reports the conflict in one line naming the finding and
the non-goal. The same precedence is in the Objective Scope section of every
linked brief.

Each later stage in the rescue chain re-verifies the revision against the
original failure. After a reviewer rejection, its brief embeds the same
blocking findings the rescue Worker received and tells it to confirm each one
is resolved or rejected with evidence. After any other failure, such as a
definition-of-done gate failure, its brief names the failure reason instead.
After a reviewer rejection, the re-verification Judge's brief also tells it to
check each required fix against the acceptance criteria and to declare a fix
that cannot be kept without breaking a criterion as a criterion conflict (see
"Criterion conflicts" below), not to fail the revision against that criterion
again. The rescue Worker and every chained stage receive the failed reviewer's
findings, and an objective-linked rescue also receives the objective brief with
its acceptance criteria, so the conflict is visible to the next Judge.

The failed parent output is in scope for the rescue Worker and later stages in
that rescue chain. The server follows same-owner, same-voyage and same-vessel
dependencies back to the marked rescue root, and exposes only the failed
mission named by that root as an additional rescue-chain read. The walk follows
the stored dependency chain and stops at missing links, cycles, or a change of
owner, voyage or vessel. It visits each dependency at most once. Existing direct
parent and dependency reads remain available within the caller's normal scope.

Stage `preferredModel` values are logical tiers: `low`, `mid`, or `high`.
Provider routing resolves the concrete model. Do not put concrete provider
model names in pipeline documentation or persona prompts.

## Operator API

Use:

- `armada_enumerate` with `entityType: "pipelines"`;
- `get_pipeline` (by name, or by the id a voyage records);
- `create_pipeline`;
- `update_pipeline`;
- `delete_pipeline`;
- `armada_dispatch` to select a pipeline for a voyage.

Read the live schema before a write. Validate every stage persona against the
active persona catalog. Use unique stage order values unless parallel sibling
execution is intentional.

## Selection Rules

- Use `WorkerOnly` only for narrow, low-risk work.
- Use `Tested` for normal changes that need independent tests and review.
- Use `FullPipeline` when decomposition is also required.
- Use `ProductDevelopment` when product and usability decisions are part of
  the requested outcome.
- Use the matching specialist pipeline for its risk area.

The pipeline does not replace workflow Checks. Mission roles produce work and
review. Workflow profiles and Checks provide command evidence.

## Captain Boundary

Pipeline captains receive Armada's local MCP configuration at launch so they
can use coordination and evidence tools consistently. Give each captain its
mission, dock, repository context, selected playbooks, and runtime tools. The
operator still owns dispatch, monitoring, landing, recovery, and closure unless
the mission explicitly delegates one of those actions.

## Judge acceptance evidence

When a brief lists acceptance criteria, the Judge includes an exact
`## Acceptance Criteria` section. Each line copies one criterion's text, then
states `MET` with a `path:line` citation or `command: ` followed by the command
in backticks, or states `NOT MET`. Each criterion needs its own line and evidence.
The contract includes the current marked objective brief and the first operator
criteria section outside older generated briefs. Quoted handoff reports do not
add criteria. For a mission linked to an objective, the check measures the
objective's live acceptance criteria read at check time, not the criteria the
dispatch-time brief captured, so a criterion an operator removes from the
objective after dispatch is no longer required and a PASS that covers the
current criteria is not refused on the removed ones. A mission with no linked
objective reads its criteria from the description. An objective that now lists
no criteria requires no walk; a live criterion the review never walks is still
refused. Control markers in narrative fields render as literal text.
Operator-supplied handoff marker lines render as literal text before the objective
brief is appended. A same-objective retry is idempotent only when the complete
rendered brief is the last complete objective frame before the first generated
handoff marker, with line endings normalized across hosts. It preserves generated
handoff markers in an already-augmented description.
Criteria that differ only by letter case remain separate requirements.
Duplicate or unrelated claims do not cover a missing criterion.
A `NOT MET` verdict for a required criterion refuses PASS, including a later
entry that contradicts a `MET` entry. The same words inside the copied criterion
or its evidence do not act as verdicts. The gate checks the citation format;
the Judge checks its truth. A criterion the brief lists twice, with identical
text, is one criterion.
The Judge's output is stored through the shared secret redactor, so a
secret-shaped value in a criterion (for example `token=<value>`) reaches the
check as `[REDACTED]`. The check passes each brief criterion and each claimed
line through the same redactor before it compares them, so a criterion the Judge
copied exactly still matches. Criteria that read the same once redacted are one
criterion.

A PASS that this check refuses on format alone (no section, an unlisted
criterion, or missing evidence; not a `NOT MET` entry) does not fail the
mission. The same Judge mission runs once more in place, with a
`## Judge Format Correction` section appended to its brief: the check's
reason, the exact accepted line form, and the previous review indented so it
adds no headings or criteria. The re-run does not count against the rescue
budget, and records a `judge_acceptance_format` retry. A second format miss
fails the mission as a NEEDS_REVISION, as before. Every Judge prompt that asks
for the section states the same line form.

Objective dispatch renders every acceptance criterion in full. It folds line
breaks and other whitespace in each criterion into spaces so one stored item
stays one Judge entry. Description, metadata, and total-budget trimming retain
the complete criteria block and report references before trimming surrounding
context. If this required content exceeds a budget, it remains intact and
budget telemetry reports the excess.
For `[DOD:DOC-ONLY]` missions with only non-code changes, the Judge reviews the
document diff without running the full test suite.

### Judge test evidence

An implementation Judge brief carries an `## Independent Check Evidence`
section, written when the brief is generated. The commit under review is the
predecessor stage's commit. The section reads the Checks the Judge gate reads
(the voyage's and the mission's, narrowed to the Judge's lane) and keeps only
the latest record of each Check:

- When every latest UnitTest record at the commit under review has passed, the
  section names the Check id and the commit. The Judge cites that Check as its
  test evidence and runs only focused tests for the changed code, with each
  command and its result. It runs the full suite instead when the diff changes
  test infrastructure or build configuration.
- Otherwise the section says no passing UnitTest Check was recorded when the
  brief was written. The Judge reads current Check receipts before repeating
  work, and runs the full suite in the foreground if no current passing receipt
  covers that commit.

The section also lists the exact-commit lane Check ids, types, sources and
statuses. Native Slop identity uses the native classifier command rule; a custom
Slop command is not labelled native. An adopted result names the earlier gate
and mission and says that this Check ran no command. The snapshot directs a
captain to `armada_enumerate entityType=checks` for its voyage or standalone
mission, with `includeTestOutput=false`. Every result page must be read and the
Check id, commit, type, source and status matched. Pending, failed or unavailable
proof remains unresolved. A separate local tool does not replace a required
native Check or add a requirement absent from the workflow or acceptance criteria.

A Judge re-run in place, for example after its Checks resolve, gets a new brief
and so reads the current Check state. The Judge template's
`## Choosing Which Tests To Run` section and the Judge output contract state
the same rule. The template section reaches existing built-in rows through the
append-if-missing upgrader, and it governs a row's own wording of the test run.
Report-only Judges do not receive the section.

### Criterion conflicts

A fix the Judge requires can be impossible without breaking a stated
acceptance criterion: for example, "bound the cache with LRU eviction" against
"exactly one git process runs per (commit, path) per process". A rescue would
build the fix, and the next Judge would fail it against the unchanged
criterion. The Judge declares such a conflict on a standalone line and returns
NEEDS_REVISION:

```
CRITERION-CONFLICT: <criterion text exactly as the brief lists it> -- <the required fix, and why it breaks the criterion>
```

Armada reads the line mechanically. A line counts when it starts with the
prefix (after an optional list bullet or code quote) and names a criterion and
a conflict around ` -- `. A placeholder that opens with `<` is ignored. When a
NEEDS_REVISION or FAIL verdict carries one or more such lines, the verdict is
held for an operator ruling and is not recorded as a failure:

- The mission stays `WorkProduced` with the operator-review hold set. The hold
  reason opens with `criterion_review incident <id>:` and names the incident.
  Nothing hands off, lands or is rescued while the hold stands.
- The voyage stays open (`operator_review_hold`), and the inbox lists the
  mission as `criterion_review_hold`: "Awaiting operator ruling on a criterion
  conflict", with the incident id. It never appears under Failed.
- One open incident with root cause `criterion_conflict` lists each declared
  conflict for the operator. A `mission.criterion_conflict` event records the
  hold.

The operator rules with `armada_review_hold`:

- `clear` lands the work as it stands (the criterion was wrong, or the
  required fix is out of scope). The mission then hands off or lands as a
  released PASS does.
- `fail` ends the mission `Failed` with the `[judge:criterion_conflict]`
  marker, so autonomous recovery dispatches no rescue against the unchanged
  criterion; its blocked event names the `criterion_conflict:` reason. The
  operator corrects the criterion on the objective and dispatches the work
  again.

Either ruling closes the incident and records the ruling as its postmortem.
Closing the incident by hand while the hold stands is refused with
`criterion_review_hold_open`. The `revision_kind` decision is not consulted
for a verdict held this way.

## Cancellation and stored mission updates

An ordinary full-row mission update preserves a stored `Cancelled` mission unless
the requested status is `Cancelled` or `Pending`. `Pending` is the restart path.
A refused write changes no stored field or mission or voyage timestamp. The
returned mission carries the current stored status. A missing row does not become
a cancelled mission. An explicit status-conditioned update still requires its
expected status to match and keeps its transition contract.

Completion and review approval stop when their write observes a different stored
status. Pipeline handoff also checks cancellation before it prepares dependents.

## Interrupted Architect fan-out

The handoff sweep reconciles an Architect in `WorkProduced` when its voyage is
still open or in progress and its completion is no longer running. Recovery uses
the existing Architect parser, first Worker template, stage shape and dependency
rules. It preserves the plan evidence and delivered mailbox notes. A completion
marker in the Architect description includes a digest of the parsed plan. A
changed plan cannot use a previous completion marker.

New fan-out Workers and cloned stages have stable opaque identifiers for their
Architect slice or template edge. If a create succeeds but its acknowledgement
is lost, the next sweep reuses that row. Older rows are reused only when their
lineage and stored shape give one safe match. Partial chains are completed before
pending slice dependencies are applied. Other fan-out roots are boundaries, not
stages to copy. Direct dependents wait while recovery is in progress.

Recovery does not rewrite a started slice. A missing stage behind started work,
a conflicting brief, an ambiguous match or an unsafe dependency places the
Architect on operator review hold with an `architect_fanout_recovery` reason.
The graph and existing work remain available for inspection. Clear retries safe
reconciliation and keeps the hold if the graph is still unsafe. Fail uses the
existing failed-hold recovery path. A terminal voyage is not re-opened.
