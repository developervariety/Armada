# Armada Pipelines

A pipeline is an ordered set of persona stages. Stages with the same order run
as parallel siblings. Use [OPERATIONAL_ASSETS.md](OPERATIONAL_ASSETS.md) for
the complete operator procedure.

## Built-In Pipelines

| Pipeline | Stages |
| --- | --- |
| `WorkerOnly` | Worker |
| `Reviewed` | Worker, Judge |
| `Tested` | Worker, TestEngineer, Judge |
| `FullPipeline` | Architect, Worker, TestEngineer, Judge |
| `ProductDevelopment` | Product Manager, Architect, Worker, Usability Engineer, TestEngineer, Judge |
| `DiagnosticProtocolTested` | Worker, DiagnosticProtocolReviewer, TestEngineer, Judge |
| `TenantSecurityTested` | Worker, TenantSecurityReviewer, TestEngineer, Judge |
| `MigrationDataTested` | Worker, MigrationDataReviewer, TestEngineer, Judge |
| `PerformanceMemoryTested` | Worker, PerformanceMemoryReviewer, TestEngineer, Judge |
| `ReferencePortingTested` | Worker, PortingReferenceAnalyst, TestEngineer, Judge |
| `FrontendWorkflowTested` | Worker, FrontendWorkflowReviewer, TestEngineer, Judge |
| `Reflections` | MemoryConsolidator |
| `ReflectionsDualJudge` | MemoryConsolidator, then two parallel Judge stages |

The startup seed service creates or reconciles these definitions. Built-in
pipelines cannot be deleted.

## Resolution

Dispatch can name a pipeline. If it does not, Armada resolves a vessel default,
then a fleet default, then `WorkerOnly`. A source repository can need a stronger
default. For example, use `ReferencePortingTested` when approved reference
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
- Use reflection pipelines only through the memory-consolidation workflow and
  only when owner policy enables learned facts.

The pipeline does not replace workflow Checks. Mission roles produce work and
review. Workflow profiles and Checks provide command evidence.

## Captain Boundary

Pipeline captains do not need Armada MCP access. Give each captain its mission,
dock, repository context, selected playbooks, and runtime tools. The operator
uses MCP to dispatch, monitor, interrupt, land, recover, and close records.
