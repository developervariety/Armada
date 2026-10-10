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

The startup seed service creates or reconciles these definitions. Built-in
pipelines cannot be deleted. Specialist pipelines are deployment configuration,
seeded through `additionalPipelines` with matching personas and templates.
See [Operational Assets](OPERATIONAL_ASSETS.md#8-pipelines).

## Resolution

Dispatch can name a pipeline. If it does not, Armada resolves a vessel default,
then a fleet default, then `WorkerOnly`. A source repository can need a stronger
default. Configure a specialist pipeline when approved reference
material is a normal part of that vessel's work.

Read current fleet and vessel settings before a default change. Do not infer a
default from a previous voyage.

## Execution

Armada creates missions for the first stage order. When all sibling missions
at that order reach a successful terminal state, Armada advances to the next
order. A required failure stops normal advancement. The operator must inspect
the failed mission, evidence, and recovery options.

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

Each handoff block ends with a report-essentials section for the stage that
just finished: the complete-output reference (`mission-output:<id>`) with its
length and UTF-8 SHA-256, the verdict, every blocking finding, and the
follow-up, residual and added-tests sections, within a 4,000-character bound.
It sits after the bounded output preview and the diff. Description, metadata,
and total-budget trimming pin each report heading and complete-output reference
before trimming the narrative. Voyage board notes cannot displace those
references. Compaction of older blocks retains their report essentials.

A mission's dock outlives its captain while the mission still lands or is
approved from it: a mission in `WorkProduced`, `PullRequestOpen` or `Review`
keeps its dock through stall recovery and the orphan-dock reaper, and the disk
sweep skips its orphan-dock scan for a pass in which the protected docks
cannot be read.

A mission that depends on a stage in another voyage waits on that stage. When
that stage was cancelled or failed and an autonomous rescue of its voyage
completed, the dependant waits on the rescue's completed stage of the same
persona instead; the change is recorded as `mission.dependency_rewired`, and the
original stage stays in the record. An objective whose failed voyage was
recovered reconciles to `Completed` once the rescue voyage completes: a rescue
of a failed stage also recovers the failed stages above it in the same chain,
because it started from that stage's commit and re-ran them.

A rescue for a reviewer rejection is a Worker started from the reviewed
commit. The brief carries a digest-backed pointer to the full Judge output and
instructs every rescue stage to read it with `armada_mission_output` before it
edits or re-verifies. The tool returns the safely redacted output in pages; the
stage continues until `hasMore` is false and checks `complete` and `sha256`.

The rescue findings list includes numbered or bulleted defects from
Correctness, Failure Modes, Tests, Evidence, Residual Risks and Verdict, plus
existing `NOT DELIVERED`,
`NOT MET`, `NOT RESOLVED` and `Blocking` items. It keeps file and line anchors,
places NOT MET criteria after defects, removes MET criteria and narration
before shortening, and uses one list marker per item. The embedded full-review
cap is 12,000 characters. When the report fits, the brief carries it whole.
When it does not, the brief keeps the protected sections whole after removing
narration and MET criteria. If those protected sections exceed the bound, the
brief labels the embedded list incomplete and directs the stage to the complete
parent output. Gate-log truncation keeps its existing cap and content-filter
recovery behavior.

Description, metadata and total-budget trimming keep each generated rescue-root
artifact reference with its ID, length, digest and exact read notice. The
original rescue description remains available as bounded narrative.

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
- `get_pipeline`;
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
add criteria. Control markers in narrative fields render as literal text.
Operator-supplied handoff marker lines render as literal text before the objective
brief is appended. A same-objective retry is idempotent only when the complete
rendered brief is the last complete objective frame before the first generated
handoff marker, with line endings normalized across hosts. It preserves generated
handoff markers in an already-augmented description.
Criteria that differ only by letter case remain separate requirements.
Duplicate or unrelated claims do not cover a missing criterion.
Any `NOT MET` refuses PASS. The gate checks the citation format; the Judge checks its truth.

Objective dispatch renders every acceptance criterion in full. It folds line
breaks and other whitespace in each criterion into spaces so one stored item
stays one Judge entry. Description, metadata, and total-budget trimming retain
the complete criteria block and report references before trimming surrounding
context. If this required content exceeds a budget, it remains intact and
budget telemetry reports the excess.
For `[DOD:DOC-ONLY]` missions with only non-code changes, the Judge reviews the
document diff without running the full test suite.
