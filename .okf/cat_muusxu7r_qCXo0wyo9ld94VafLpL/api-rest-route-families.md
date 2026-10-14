---
type: "Reference"
title: "REST route families and notable non-CRUD endpoints"
description: "Map of /api/v1 route groups: auth/tenants/users/credentials, status/settings/server, fleets/vessels (+git, readiness, landing preview, build-context, import, health), voyages/missions (+status, review, restart, retry-landing, diff, log, instructions), captains (+tools, unquarantine, chat, stop), ask, signals, events, docks, merge-queue, harbors, jobs, fleet-actions, delivery, backlog/planning, config, memories, token usage, workspace, history."
timestamp: "2026-10-05T05:51:56.0923980Z"
created: "2026-10-05T05:51:49.9254420Z"
slug: "api-rest-route-families"
category: "cat_muusxu7r_qCXo0wyo9ld94VafLpL"
links: ["api-rest-conventions", "api-ask-rest-routes", "api-frozen-surface-1-0"]
version: "1"
salience: "0.5"
---
Full list: docs/API_SURFACE_1.0.md; examples: docs/REST_API.md and Armada.postman_collection.json.
- Auth: POST /authenticate, GET /whoami, PUT /account/password, POST /tenants/lookup, POST /onboarding; /tenants, /users, /credentials CRUD.
- Status/server: GET /status (ArmadaStatus incl. RemoteTunnel), GET /status/health (no auth), GET /doctor, GET|PUT /settings, POST /server/stop|restart|reset, POST /server/rebuild, GET /server/rebuild/status, POST /server/rollback (rebuild/rollback experimental), GET /backup, POST /restore.
- Fleets/vessels: CRUD + /enumerate + /delete/multiple; vessels PATCH /{id}/context, GET /{id}/git-status, GET /{id}/branches, POST /{id}/branches/push|merge, GET /{id}/readiness, GET /{id}/landing-preview, POST /{id}/build-context; /vessels/import/browse|discover, POST /vessels/import, import batches (enumerate, get, categorize, fleet-recommendations/apply, categorization/default-prompt); /vessel-health/enumerate|summary|evaluate, /vessels/{id}/health(+overrides/{criterion}).
- Voyages: CRUD, DELETE /{id} (cancel), /{id}/purge. Missions: CRUD, /summaries, /history, PUT /{id}/status, POST /{id}/review/approve|deny, /{id}/restart, /{id}/retry-landing, GET /{id}/evaluate-autoland, /{id}/diff, /{id}/log (lines, offset), /{id}/instructions, /{id}/github/pull-request, /{id}/landing-preview, /{id}/purge.
- Captains: CRUD, GET /{id}/tools, POST /{id}/unquarantine, /{id}/stop, /stop-all, GET /{id}/log, POST /{id}/chat. Ask: /ask/... ([[api-ask-rest-routes]]).
- Signals (/recent, /recipient/{captainId}, PUT /{id}/read), events, docks (/{id}/repair, /{id}/unstick, /{id}/purge), merge-queue (/process, /{id}/process, /purge, /{id}/purge), harbors (experimental: enable/disable/probe), jobs (/{id}/cancel), inbox, fleet-actions (/{id}/run, /run ad hoc) and fleet-action-runs (/{id}, /targets/enumerate, /targets/{targetId}, /cancel).
- Delivery: workflow-profiles (validate, resolve, preview), project-profiles, skills, environments, check-runs (run, retry, import, GitHub Actions sync), releases (/{id}/github/pull-requests), deployments (approve/deny/verify/rollback), incidents, runbooks (+executions).
- Planning/backlog: /backlog and /objectives (+reorder, import/github, refinement-sessions), /objective-refinement-sessions/{id}/..., /planning-sessions.
- Config/other: personas, pipelines, playbooks, prompt-templates (/{name}, /{name}/reset), memories, model-endpoints, token-usage, request-history, history, workspace/vessels/{vesselId}/..., runtimes/mux/endpoints.
