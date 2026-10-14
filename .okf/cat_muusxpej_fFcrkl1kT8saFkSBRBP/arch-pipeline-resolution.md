---
type: "Project"
title: "Pipeline resolution at dispatch (AdmiralService.ResolvePipelineAsync)"
description: "Pipeline = explicit dispatch value (by id, then by name) > vessel DefaultPipelineId > fleet DefaultPipelineId > null (WorkerOnly behavior); stale vessel/fleet references to deleted pipelines are cleared and logged."
timestamp: "2026-10-05T05:40:15.0251610Z"
created: "2026-10-05T05:40:14.6706780Z"
slug: "arch-pipeline-resolution"
category: "cat_muusxpej_fFcrkl1kT8saFkSBRBP"
links: ["term-pipeline", "arch-pipeline-stage-handoff", "arch-dispatch-to-landing-flow"]
version: "1"
salience: "0.5"
---
src/Armada.Core/Services/AdmiralService.cs ResolvePipelineAsync(pipelineId, vessel): (1) explicit pipelineId -> Pipelines.ReadAsync(id), else ReadByNameAsync(value) for convenience (`--pipeline FullPipeline`); (2) vessel.DefaultPipelineId -> read; if the pipeline no longer exists, set the vessel's DefaultPipelineId to null, persist, warn; (3) vessel's fleet DefaultPipelineId with the same stale-reference cleanup; (4) null -> standard single-mission dispatch with no persona fields (WorkerOnly). A resolved single-stage Worker pipeline also takes the standard path. ValidateDispatchAsync rejects an unresolvable explicit pipeline before anything is created (fleet Mission actions surface this as DispatchRejected). Project profiles can also carry a DefaultPipelineId (Global/Fleet/Vessel). Per-dispatch override: CLI --pipeline, REST pipeline/pipelineId, MCP dispatch pipeline.
