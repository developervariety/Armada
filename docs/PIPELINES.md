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
Duplicate or unrelated claims do not cover a missing criterion. Any `NOT MET`
refuses PASS. The gate checks the citation format; the Judge checks its truth.

Description, metadata, and total-budget trimming retain the complete criteria
block before trimming surrounding context. If the contract itself exceeds a
budget, the contract remains intact and budget telemetry reports the excess.
For `[DOD:DOC-ONLY]` missions with only non-code changes, the Judge reviews the
document diff without running the full test suite.
