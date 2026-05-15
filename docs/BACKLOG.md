# Armada Backlog Guide

`Backlog` is the user-facing workflow for future work in Armada. Internally the persisted record is still `Objective`, so objective IDs, event history, and compatibility routes remain intact while the product language shifts to backlog-first usage.

## What A Backlog Item Is

A backlog item can represent:

- a feature
- a bug
- a refactor
- a research task
- a chore
- an initiative

Each backlog item can carry:

- title and description
- kind, category, priority, rank, backlog state, and effort
- owner, target version, and due date
- parent and blocking backlog links
- acceptance criteria, non-goals, rollout constraints, and evidence links
- linked fleets, vessels, refinement sessions, planning sessions, voyages, missions, checks, releases, deployments, and incidents

Armada keeps that current state in normalized database tables and still emits `objective.snapshot` events for audit and timeline continuity.

## Dashboard Workflow

The React dashboard is the primary user-facing backlog surface.

1. Open `http://localhost:7890/dashboard`
2. Go to `Backlog`
3. Create or open a backlog item
4. Set the backlog metadata you know now
5. Start refinement if you want model help before repository-aware planning
6. Start planning when you are ready to choose the vessel, captain, pipeline, and playbooks
7. Dispatch implementation or draft a release from the same detail view

Backlog detail now keeps the same item linked through:

- refinement transcripts
- planning sessions
- dispatched voyages and missions
- releases
- deployments
- incidents
- `Activity > History`

## Refinement Vs Planning

Refinement and planning are intentionally different workflows.

Refinement:

- requires an explicit captain selection
- does not provision a dock or worktree by default
- can run without a vessel
- is meant for shaping the implementation intent
- stores a transcript, summary, and apply-back path

Planning:

- is repository-aware
- requires a vessel
- uses the selected captain against repository context
- can summarize into a dispatch draft or dispatch directly
- links the resulting planning session and downstream voyage back to the same backlog item

## REST API

Backlog uses the same underlying `Objective` model as the legacy objective routes.

Primary backlog routes:

- `GET /api/v1/backlog`
- `POST /api/v1/backlog`
- `POST /api/v1/backlog/enumerate`
- `GET /api/v1/backlog/{id}`
- `PUT /api/v1/backlog/{id}`
- `DELETE /api/v1/backlog/{id}`
- `POST /api/v1/backlog/reorder`

Compatibility objective routes:

- `GET /api/v1/objectives`
- `POST /api/v1/objectives`
- `POST /api/v1/objectives/enumerate`
- `GET /api/v1/objectives/{id}`
- `PUT /api/v1/objectives/{id}`
- `DELETE /api/v1/objectives/{id}`
- `POST /api/v1/objectives/reorder`
- `POST /api/v1/objectives/import/github`

Refinement routes:

- `GET /api/v1/backlog/{id}/refinement-sessions`
- `POST /api/v1/backlog/{id}/refinement-sessions`
- `GET /api/v1/objectives/{id}/refinement-sessions`
- `POST /api/v1/objectives/{id}/refinement-sessions`
- `GET /api/v1/objective-refinement-sessions/{id}`
- `POST /api/v1/objective-refinement-sessions/{id}/messages`
- `POST /api/v1/objective-refinement-sessions/{id}/summarize`
- `POST /api/v1/objective-refinement-sessions/{id}/apply`
- `POST /api/v1/objective-refinement-sessions/{id}/stop`
- `DELETE /api/v1/objective-refinement-sessions/{id}`

Route permissions follow the normal Armada rules:

- reads are available to authenticated callers in scope
- backlog/objective mutations require tenant admin or global admin
- refinement-session mutations also require tenant admin or global admin

See [REST_API.md](./REST_API.md) for the full route reference.

## Helm CLI

Helm ships first-class backlog CRUD and reorder coverage.

```bash
armada backlog list
armada backlog show obj_abc123
armada backlog create --title "Stabilize release rollout" --priority P1 --backlog-state Inbox
armada backlog update obj_abc123 --kind Feature --target-version 0.8.0
armada backlog reorder obj_abc123 --rank 10
armada backlog delete obj_abc123
```

Helm does not currently expose interactive refinement chat. Use the dashboard, REST API, or MCP when you need the transcript workflow.

## MCP

MCP exposes backlog management and workflow handoff directly.

Core backlog tools:

- `list_backlog`
- `get_backlog_item`
- `create_backlog_item`
- `update_objective`
- `reorder_backlog_items`
- `delete_backlog_item`

Refinement tools:

- `list_backlog_refinement_sessions`
- `create_backlog_refinement_session`
- `get_backlog_refinement_session`
- `send_backlog_refinement_message`
- `summarize_backlog_refinement_session`
- `apply_backlog_refinement_summary`
- `stop_backlog_refinement_session`

Planning handoff tools:

- `create_backlog_planning_session`
- `get_backlog_planning_session`
- `dispatch_backlog_planning_session`

See [MCP_API.md](./MCP_API.md) for tool details.

## History And Lineage

The same backlog item remains the system-of-record link through:

- refinement
- planning
- dispatch
- release
- deployment
- incident
- history

`Activity > History` and `GET /api/v1/history` can filter by `objectiveId` to reconstruct the delivery story of one backlog item. Timeline entries for backlog refinement sessions link back to `/backlog/{objectiveId}` so the transcript and downstream delivery context remain discoverable from one place.
