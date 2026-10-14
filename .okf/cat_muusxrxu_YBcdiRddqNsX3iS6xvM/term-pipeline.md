---
type: "Reference"
title: "Pipeline: ordered persona stages (ppl_) and the built-in pipelines"
description: "Pipeline (ppl_) = ordered PipelineStage list; built-ins WorkerOnly, Reviewed, Tested, FullPipeline (8 stages), Recorded; stages can require review."
timestamp: "2026-10-05T05:29:51.3286600Z"
created: "2026-10-05T05:29:51.0486220Z"
slug: "term-pipeline"
category: "cat_muusxrxu_YBcdiRddqNsX3iS6xvM"
links: ["term-persona", "arch-pipeline-resolution", "arch-pipeline-stage-handoff", "arch-review-gates"]
version: "1"
salience: "0.5"
---
Term: Pipeline (src/Armada.Core/Models/Pipeline.cs prefix ppl_; PipelineStage pps_ with Order, PersonaName, IsOptional, RequiresReview, ReviewDenyAction). Built-ins seeded by src/Armada.Core/Services/PersonaSeedService.cs:
- WorkerOnly: Worker (system fallback; single-stage path skips persona fields)
- Reviewed: Worker -> Judge
- Tested: Worker -> Test Engineer -> Judge
- FullPipeline: Product Manager -> Architect -> Worker -> Usability Engineer -> Test Engineer -> Linter -> Judge -> Recorder (README's shorter table is outdated; code is authoritative)
- Recorded: Worker -> Recorder
In the seeded definitions every stage except Recorder has RequiresReview=true and Judge has ReviewDenyAction=FailPipeline. The built-in FullPipeline's stage list is reset to the shipped definition on startup whenever it differs (copy it to customize); other built-ins keep user edits.
Resolution: explicit dispatch pipeline (id or name) > vessel DefaultPipelineId > fleet DefaultPipelineId > WorkerOnly ([[arch-pipeline-resolution]]). Each mission description gets its own chain of stage missions titled "<title> [<Persona>]" linked by DependsOnMissionId. Custom: `create_pipeline name=SecureRelease stages='[{"personaName":"Worker"},{"personaName":"SecurityAuditor"},{"personaName":"Judge"}]'`.
