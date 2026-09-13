# SDK client coverage

The typed `ArmadaApiClient` now covers the fork routes for vessel readiness and
landing previews, deployments, mission landing and GitHub pull request details,
token usage summaries, releases, check runs, and background jobs.

| Client method group | Fork route(s) |
| --- | --- |
| Vessel readiness and landing | `GET /api/v1/vessels/{id}/readiness`, `GET /api/v1/vessels/{id}/landing-preview` |
| Environments | `POST /api/v1/environments`, `GET/PUT/DELETE /api/v1/environments/{id}` |
| Deployments | `POST /api/v1/deployments`, `GET/PUT/DELETE /api/v1/deployments/{id}`, `POST /api/v1/deployments/{id}/approve`, `/deny`, `/verify`, `/rollback` |
| Mission preview and pull request | `GET /api/v1/missions/{id}/landing-preview`, `GET /api/v1/missions/{id}/github/pull-request` |
| Token usage | `GET /api/v1/token-usage/summary` |
| Releases | `POST /api/v1/releases`, `GET/PUT/DELETE /api/v1/releases/{id}`, `POST /api/v1/releases/{id}/refresh` |
| Check runs | `GET /api/v1/check-runs/{id}`, `POST /api/v1/check-runs`, `POST /api/v1/check-runs/sync/github-actions`, `POST /api/v1/check-runs/{id}/retry`, `DELETE /api/v1/check-runs/{id}` |
| Jobs | `GET /api/v1/jobs/{id}`, `POST /api/v1/jobs/{id}/cancel` |

The client uses the fork request and response models, preserves cancellation
tokens, URL-encodes query values, and raises `HttpRequestException` with the
server response for failed POST requests. This slice does not add routes,
database fields, authentication rules, or branch write operations.

The client contract must stay aligned with the registered routes. Add a route
contract test before adding a new public client method. Verify both the HTTP
method and path, request JSON, typed response, non-success status, encoded IDs,
and cancellation behavior.

## Profiles, skills, Ask, and refinement

The second SDK slice covers the supported fork routes for workflow profiles,
project profiles, skills, Ask, objectives, and objective refinement. Objective
refinement is exposed through both objective and backlog creation aliases so
callers can preserve the route family they use.

| Client method group | Fork route(s) |
| --- | --- |
| Workflow profiles | `GET /api/v1/workflow-profiles/{id}`, `POST /api/v1/workflow-profiles/validate`, `GET /api/v1/workflow-profiles/preview/vessels/{vesselId}`, `GET /api/v1/workflow-profiles/resolve/vessels/{vesselId}`, `POST /api/v1/workflow-profiles`, `PUT/DELETE /api/v1/workflow-profiles/{id}` |
| Project profiles | `GET /api/v1/project-profiles/{id}`, `POST /api/v1/project-profiles/validate`, `GET /api/v1/project-profiles/resolve/vessels/{vesselId}`, `GET /api/v1/project-profiles/{id}/persona-preview/{persona}`, `POST /api/v1/project-profiles`, `PUT/DELETE /api/v1/project-profiles/{id}` |
| Skills | `GET /api/v1/skills/{id}`, `POST /api/v1/skills`, `PUT/DELETE /api/v1/skills/{id}` |
| Ask | `POST /api/v1/ask` |
| Objectives | `GET /api/v1/objectives/{id}`, `POST /api/v1/objectives`, `PUT/DELETE /api/v1/objectives/{id}`, `POST /api/v1/objectives/import/github` |
| Refinement sessions | `POST /api/v1/objectives/{id}/refinement-sessions`, `POST /api/v1/backlog/{id}/refinement-sessions`, `GET/DELETE /api/v1/objective-refinement-sessions/{id}`, `POST /api/v1/objective-refinement-sessions/{id}/messages`, `/summarize`, `/apply`, and `/stop` |

Each added wrapper has a route contract test with exact path and query,
non-default typed response assertions, and populated request JSON assertions.
IDs and persona path segments are URL encoded. No unsupported upstream list,
enumeration, Harbor, or branch operation was added.
