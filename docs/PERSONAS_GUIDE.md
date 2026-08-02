# Persona Operator Guide

Use personas to give each mission one clear role. Use pipelines when work needs
several roles. The canonical asset rules are in
[OPERATIONAL_ASSETS.md](OPERATIONAL_ASSETS.md).

## Select A Persona

Use `Worker` for a small, well-defined implementation. Use `Architect` when
the work needs decomposition. Use `TestEngineer` or `Judge` for an independent
test or review mission.

Use a specialist only when the change has that risk:

| Risk | Persona | Normal pipeline |
| --- | --- | --- |
| Protocol or hardware access | `DiagnosticProtocolReviewer` | `DiagnosticProtocolTested` |
| Tenant isolation or secrets | `TenantSecurityReviewer` | `TenantSecurityTested` |
| Schema or stored data | `MigrationDataReviewer` | `MigrationDataTested` |
| Memory or throughput | `PerformanceMemoryReviewer` | `PerformanceMemoryTested` |
| Porting from an approved reference | `PortingReferenceAnalyst` | `ReferencePortingTested` |
| Frontend workflow or accessibility | `FrontendWorkflowReviewer` | `FrontendWorkflowTested` |

Use `ProductDevelopment` for a broad product change that needs requirements,
architecture, implementation, usability, tests, and review.

Do not dispatch `MemoryConsolidator` as ordinary delivery work. The reflection
workflow owns it. Learned facts and reflection can also be disabled by policy.

## Inspect Before Dispatch

1. Call `armada_enumerate` for personas and pipelines.
2. Read the selected persona with `get_persona`.
3. Read its prompt template.
4. Read fleet, vessel, persona, and captain default playbooks.
5. Remove inactive or irrelevant defaults.
6. Confirm the vessel workflow profile and expected Checks.

Use a stable persona or pipeline name in operator procedures. Resolve the live
ID from Armada. Do not copy private database IDs into public documentation.

## Dispatch

For one role, use `armada_dispatch` and select the persona. For several roles,
select a pipeline. The pipeline determines stage order and parallel siblings.

Give the mission:

- one concrete objective;
- the exact repository and scope;
- required tests and evidence;
- stop conditions;
- the required result-marker format.

Do not give the captain Armada MCP tools. The captain works in the assigned
dock. The operator performs Armada status, Check, landing, release, deployment,
incident, and recovery operations.

## Review Results

Read mission state with `armada_mission_status`. Read voyage state with
`armada_voyage_status`. Confirm command evidence and result markers. A role
name does not prove that its work was correct.

The operator must verify:

- the mission used the expected persona and prompt version;
- selected playbooks were active and relevant;
- required Checks contain real evidence;
- review findings have a clear disposition;
- landing and closeout match repository policy.

## Manage Personas

Use `create_persona` only when no built-in role fits. Give a custom persona one
narrow purpose and one active prompt template. Use `update_persona` to change
description, template, active state, or defaults. Built-in personas cannot be
deleted.

Persona and captain default playbooks are operator configuration. Read the
current list before replacement. An empty list clears that layer.
