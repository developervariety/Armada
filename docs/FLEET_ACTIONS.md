# Saved Mission Actions

Global administrator operators can save a mission prompt and run it against
selected vessels. Each definition and run belongs to the creating tenant and
user. Tenant administrators, ordinary users, mission credentials and saved Ask
credentials cannot use this operator surface.

## Define and run an action

Open **Mission Actions** in the Dashboard. Save a name, prompt, optional pipeline
and concurrency from one through eight. The prompt can use `{vesselName}` and
`{vesselId}`. Select up to 50 vessels, review the selected targets, and start the
run. An inactive definition keeps its history and does not accept new runs.

The server stores the action, rendered prompts, vessel ownership, effective
pipeline identity and content hash before dispatch. Editing a definition does
not change an accepted run. Deleting a definition preserves its run records.
If the current actor, vessel ownership or pipeline no longer matches the
accepted intent, pending work is refused with a reason.

Each request has a stable caller-supplied request ID. Repeating the same request
returns its saved run. Reusing that ID with different inputs returns a conflict.
The Dashboard keeps the ID after a failed submission response, so an ambiguous
response does not create another run on retry.

## Dispatch and outcomes

A durable state claim permits one dispatch attempt for each target. The
dispatcher uses the existing voyage preflight, hold, Checks and landing rules.
It stores only the exact voyage ID returned by that dispatch.

An exception or interruption after a claim can leave the dispatch outcome
unknown. Such a target stays **Uncertain**, blocks further dispatch in its run,
and has no automatic retry. Startup fences interrupted claims before it accepts
new work. The server does not infer a voyage from a title or a recent row.

Run cards show saved target inputs, current state, reasons and exact voyage
links. Produced work and confirmed delivery are separate outcomes. A completed
voyage alone does not prove that source work landed. Follow the linked mission,
Check, landing and rescue records for their current evidence.

Cancellation records intent first. Pending targets can be cancelled before
dispatch. Known running voyages use the existing voyage cancellation service.
An in-flight dispatch must return its exact voyage before cancellation can act
on it. Uncertain work remains blocked until its original writer is accounted for.

## REST API

| Method | Path | Purpose |
| --- | --- | --- |
| GET, POST | `/api/v1/fleet-actions` | List owned definitions or create one |
| GET, PUT, DELETE | `/api/v1/fleet-actions/{id}` | Read, update or delete an owned definition |
| POST | `/api/v1/fleet-actions/{id}/run` | Save a run with `requestId` and `vesselIds` |
| GET | `/api/v1/fleet-action-runs` | List bounded owned runs |
| GET | `/api/v1/fleet-action-runs/{id}` | Read saved snapshots and current target states |
| POST | `/api/v1/fleet-action-runs/{id}/cancel` | Request cancellation |

Definition bodies contain `name`, `promptTemplate`, optional `pipelineId`,
`concurrency` and `active`. Run responses contain immutable `snapshot.targets`
and mutable `state.targetStates`, joined by `targetId`. Access is checked again
before pending dispatch. The Dashboard and Postman collection use these routes.
There is no separate Mission Actions MCP tool or Helm command.
