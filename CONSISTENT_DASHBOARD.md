# Consistent Dashboard Plan

## Status Snapshot

As of `2026-05-16`, Armada has two different remote UI stories:

- the full React dashboard in `src/Armada.Dashboard`
- the custom proxy shell in `src/Armada.Proxy/wwwroot`

That split is now the main source of product drift, duplicated UX work, duplicated API shaping, duplicated docs, and tunnel-method sprawl. The current proxy host now serves as a small portal and relay in `src/Armada.Proxy/ArmadaProxyServer.cs`, while the Armada side routes remote dashboard traffic only through `src/Armada.Server/RemoteDashboardRelayService.cs`.

This plan replaces that model with a simpler one:

- one real dashboard UI
- one proxy portal for login and deployment selection
- one relay model for `/dashboard`, `/api/v1/*`, and `/ws`
- one reduced tunnel contract focused on remote dashboard extension rather than per-feature proxy APIs

Top-level completion criteria:

- [x] 2026-05-16 Codex: The proxy dashboard is reduced to a minimal access portal rather than a second product UI.
- [x] 2026-05-16 Codex: The React dashboard is the only long-lived dashboard experience for both local and remote usage.
- [x] 2026-05-16 Codex: The proxy owns only proxy-local concerns: login, deployment selection, relay, policy, and health.
- [x] 2026-05-16 Codex: The tunnel now supports generic HTTP and `/ws` relay for the dashboard, and the legacy feature-specific methods have been removed server-side.
- [x] 2026-05-16 Codex: The old bounded proxy route family under `/api/v1/instances/{instanceId}/...` is removed.
- [x] 2026-05-16 Codex: `RemoteControlOperationsService` is gone from the live server path along with the related legacy tunnel compatibility services.
- [x] 2026-05-16 Codex: The docs now describe the portal-plus-relay model and no longer present the retired proxy shell as the primary remote UX.

## How To Maintain This File

- Use `[ ]` for not started, `[~]` for in progress, and `[x]` for complete.
- Add a short dated note after an item when useful, for example: `[~] 2026-05-20 AB: HTTP relay request envelope merged`.
- Do not delete completed tasks; mark them complete so the execution trail stays visible.
- If scope changes, update `Locked Decisions`, `Target Architecture`, `Workstreams`, and `Acceptance Criteria` together.
- If a task is intentionally deferred, leave it open and add the reason rather than silently removing it.
- If the team decides to keep any portion of the old proxy shell, document exactly why in `Locked Decisions`; the default assumption of this plan is that the shell is being gutted.

## Product Goal

Armada should have one dashboard product surface, not two.

The target user flow is:

1. open the proxy
2. authenticate to the proxy
3. select a connected Armada deployment
4. land in the same React dashboard experience already used locally
5. use the dashboard through proxy-backed `/api/v1/*` and `/ws` relays

The proxy is not the product UI anymore. The proxy is the remote-access broker for the product UI.

## Problem Statement

The current design creates avoidable complexity in four places:

1. UI duplication: the proxy shell re-implements slices of the full dashboard in separate HTML, CSS, and JavaScript.
2. API duplication: the proxy exposes many entity-specific remote routes instead of forwarding the real dashboard routes.
3. Tunnel duplication: the server tunnel keeps growing a custom method family for each feature area.
4. Documentation and verification duplication: every new remote feature needs its own shell docs, shell tests, shell UX, and shell bug fixing.

The practical result is predictable:

- the full dashboard is the real product
- the proxy shell is always behind
- remote users want the real dashboard anyway

## Non-Goals

- Building a third UI surface
- Keeping the current proxy shell as a co-equal long-term dashboard
- Preserving the huge instance-scoped proxy route family as the primary remote-control model
- Expanding `RemoteControlOperationsService` to cover even more dashboard features
- Solving full delegated identity or SSO in the same first cut if that blocks transport simplification
- Rewriting the React dashboard routing basename away from `/dashboard` unless a blocker is proven

## Locked Decisions

- [x] 2026-05-16 Codex: The long-term remote dashboard experience is the existing React dashboard, not the custom proxy shell.
- [x] 2026-05-16 Codex: The proxy shell is reduced to a minimal portal for authentication, deployment selection, and relay-entry only.
- [x] 2026-05-16 Codex: Proxy-local APIs move off `/api/v1/*` to avoid colliding with relayed Armada routes.
- [x] 2026-05-16 Codex: The proxy serves the dashboard at `/dashboard` so the existing React basename can remain intact.
- [x] 2026-05-16 Codex: The proxy relays Armada REST traffic at `/api/v1/*` without per-entity proxy UI reimplementation.
- [x] 2026-05-16 Codex: The proxy relays Armada dashboard WebSocket traffic at `/ws` with compatibility semantics.
- [x] 2026-05-16 Codex: The tunnel contract now includes only generic dashboard transport primitives.
- [x] 2026-05-16 Codex: The initial relay contract uses explicit body-size guardrails instead of chunked transfer; restore remains blocked and larger-body relay can be added only when the real dashboard demands it.
- [x] 2026-05-16 Codex: High-risk local-only actions may be denied by proxy policy even when the transport is generic.
- [x] 2026-05-16 Codex: Double auth is the current intermediate state: proxy login first, Armada login second.
- [x] 2026-05-16 Codex: `src/Armada.Proxy/wwwroot/app.js` and `src/Armada.Proxy/wwwroot/app.css` shrank drastically after cutover.
- [x] 2026-05-16 Codex: The old `/api/v1/instances/{instanceId}/...` route family has been removed from the proxy.

## Current State

### Full Dashboard

- The React dashboard is built in `src/Armada.Dashboard`.
- It expects to run under `/dashboard`.
- It expects same-origin REST calls under `/api/v1/*`.
- It expects same-origin WebSocket traffic at `/ws`.

Relevant files:

- `src/Armada.Dashboard/src/App.tsx`
- `src/Armada.Dashboard/src/api/client.ts`
- `src/Armada.Dashboard/src/context/WebSocketContext.tsx`
- `src/Armada.Dashboard/vite.config.ts`

### Proxy Host

- The proxy serves a large custom shell from `src/Armada.Proxy/wwwroot/index.html`, `app.js`, and `app.css`.
- The proxy exposes proxy-local auth at `/api/v1/auth/*`.
- The proxy exposes a large number of instance-scoped remote routes under `/api/v1/instances/{instanceId}/...`.
- The proxy terminates the Armada outbound tunnel at `/tunnel`.

Relevant files:

- `src/Armada.Proxy/ArmadaProxyServer.cs`
- `src/Armada.Proxy/wwwroot/index.html`
- `src/Armada.Proxy/wwwroot/app.js`
- `src/Armada.Proxy/wwwroot/app.css`
- `src/Armada.Proxy/Services/ProxyAuthService.cs`
- `src/Armada.Proxy/Services/InstanceRegistry.cs`
- `src/Armada.Proxy/Services/RemoteInstanceSession.cs`

### Tunnel/Server Side

- The server tunnel is managed by `src/Armada.Server/RemoteTunnelManager.cs`.
- The server-side remote dashboard path now runs through `src/Armada.Server/RemoteDashboardRelayService.cs`.
- The tunnel docs and capability manifest now describe the reduced relay contract.

Relevant files:

- `src/Armada.Server/RemoteTunnelManager.cs`
- `src/Armada.Server/RemoteDashboardRelayService.cs`
- `docs/TUNNEL_PROTOCOL.md`
- `docs/WEBSOCKET_API.md`
- `docs/PROXY_API.md`
- `docs/REMOTE_MGMT.md`

## Target Architecture

### Browser Flow

1. Browser opens `/`.
2. Proxy serves a minimal portal UI.
3. Browser authenticates against proxy-local endpoints under `/proxy-api/v1/*`.
4. Browser selects a connected instance and the proxy stores that selection in the proxy session.
5. Browser navigates to `/dashboard`.
6. Proxy serves the same React dashboard bundle used for local Armada.
7. Dashboard calls `/api/v1/*`; the proxy relays those requests to the selected remote instance.
8. Dashboard opens `/ws`; the proxy relays that WebSocket session to the selected remote instance.

### Route Ownership

#### Proxy-local routes

- `GET /`
- `GET /proxy-api/v1/status/health`
- `GET /proxy-api/v1/auth/challenge`
- `POST /proxy-api/v1/auth/login`
- `POST /proxy-api/v1/auth/logout`
- `GET /proxy-api/v1/instances`
- `POST /proxy-api/v1/session/instance`
- `GET /proxy-api/v1/session/context`
- `POST /proxy-api/v1/session/logout-instance` or equivalent clear-selection route
- `GET /dashboard`
- `GET /dashboard/*` static assets
- `WS /tunnel`

#### Relayed Armada routes

- `ALL /api/v1/*`
- `WS /ws`

### Tunnel Scope

#### Keep

- handshake
- ping/pong
- instance registration and liveness
- event and state needed for instance selection and relay health
- generic HTTP relay
- generic dashboard WebSocket relay

#### Remove over time

- feature-specific tunnel methods for backlog, planning, workflows, delivery, diagnostics, workspace, and other dashboard entity families
- the requirement to create a new tunnel method every time the dashboard grows a new feature

### Target Tunnel Method Families

The exact names can change, but the contract should collapse toward something like:

- `armada.tunnel.handshake`
- `armada.http.request`
- `armada.http.response`
- `armada.ws.open`
- `armada.ws.message`
- `armada.ws.close`
- `armada.ws.closed`
- `armada.ws.error`
- `ping`
- `pong`

### Policy Model

The transport becomes generic, but policy stays explicit.

- Default posture: relay dashboard routes, but preserve a central deny/allow policy in the proxy.
- Use policy to block or warn on high-risk actions such as factory reset, credential editing, or other actions the team chooses not to expose remotely yet.
- Do not rebuild those features as separate proxy workflows. Deny them centrally if needed.

## Workstreams

### Workstream 0: Decision Freeze And Cutover Prep

#### Scope

Freeze the target model so the team stops adding more remote shell scope while the transport shift is in progress.

#### Tasks

- [x] 2026-05-16 Codex: Mark this file as the controlling plan for proxy/dashboard consistency.
- [x] 2026-05-16 Codex: Mark `PROXY_PLAN.md` as superseded by this plan for long-term direction.
- [x] 2026-05-16 Codex: Freeze net-new feature work in the old proxy shell except for migration blockers and critical fixes by deleting the shell surface from `wwwroot` and moving the product path to `/dashboard`.
- [x] 2026-05-16 Codex: Inventory every proxy-local route in `src/Armada.Proxy/ArmadaProxyServer.cs` and classify it as `keep`, `replace`, `relay`, or `delete`.
- [x] 2026-05-16 Codex: Inventory every method handled by `src/Armada.Server/RemoteControlOperationsService.cs` and classify it as `keep temporarily`, `migrate to generic relay`, or `remove`.
- [x] 2026-05-16 Codex: Proxy-local routes now live under `/proxy-api/v1/*`.
- [x] 2026-05-16 Codex: No relay-cutover feature flags were added because the cutover shipped directly and the legacy shell path has now been deleted end-to-end.

#### Route Inventory Snapshot

- `keep`: `/`, `/app.css`, `/app.js`, `/img/*`, `/proxy-api/v1/auth/*`, `/proxy-api/v1/status/health`, `/proxy-api/v1/instances`, `/proxy-api/v1/session/context`, `/proxy-api/v1/session/instance`, `/proxy-api/v1/session/logout-instance`, `WS /tunnel`
- `replace`: legacy `/api/v1/auth/*` became `/proxy-api/v1/auth/*`; legacy proxy-local health at `/api/v1/status/health` became `/proxy-api/v1/status/health`
- `relay`: `GET /dashboard`, `GET /dashboard/*`, `ALL /api/v1/*`, `WS /ws`
- `delete`: the old `/api/v1/instances/{instanceId}/...` proxy route family and the custom multi-section remote shell it existed to serve

#### `RemoteControlOperationsService` Classification Snapshot

- `migrate to generic relay`: objective/backlog, refinement, planning, workflow, check, environment, release, deployment, incident, runbook, request-history, workspace, pipeline, persona, and prompt-template families now that `/api/v1/*` and `/ws` relay exist
- `keep temporarily`: none
- `remove`: completed on 2026-05-16 by deleting the feature-specific tunnel dispatch path from `src/Armada.Server/ArmadaServer.cs` and reducing the capability manifest to relay-only features

#### Exit Criteria

- [x] 2026-05-16 Codex: No one is adding meaningful new product surface to the legacy proxy shell because the shell has been removed from the product path.
- [x] 2026-05-16 Codex: The route namespace split is now explicit in code and docs, even though the documentation landed after transport work started.

### Workstream 1: Namespace Split And Session Model

#### Scope

Create a clean separation between proxy-local concerns and relayed Armada concerns.

#### Tasks

- [x] 2026-05-16 Codex: Move proxy-local auth routes from `/api/v1/auth/*` to `/proxy-api/v1/auth/*`.
- [x] 2026-05-16 Codex: Move proxy-local health route from `/api/v1/status/health` to `/proxy-api/v1/status/health`.
- [x] 2026-05-16 Codex: Add `/proxy-api/v1/session/instance` to set the selected deployment for the current proxy session.
- [x] 2026-05-16 Codex: Add `/proxy-api/v1/session/context` to return selected instance metadata, proxy auth status, and relay capability flags.
- [x] 2026-05-16 Codex: Update `src/Armada.Proxy/wwwroot/app.js` to use the new proxy-local route family.
- [x] 2026-05-16 Codex: No compatibility aliases were added, so there is no alias retirement window to manage.
- [x] 2026-05-16 Codex: No proxy-local route occupies a path needed by the real Armada dashboard.

#### Exit Criteria

- [x] 2026-05-16 Codex: The path family `/api/v1/*` is reserved for relayed Armada APIs.
- [x] 2026-05-16 Codex: The path family `/proxy-api/v1/*` is reserved for proxy-local behavior.

### Workstream 2: Gut The Proxy Dashboard Into A Minimal Portal

#### Scope

Replace the large custom proxy shell with a minimal portal.

#### Tasks

- [x] 2026-05-16 Codex: Reduce `src/Armada.Proxy/wwwroot/index.html` to a small portal with:
  - proxy login
  - connected deployment list
  - selected deployment summary
  - open-dashboard action
  - clear-selection/logout action
- [x] 2026-05-16 Codex: Remove legacy shell sections for missions, voyages, captains, fleets, vessels, playbooks, backlog, planning, delivery, diagnostics, and reference.
- [x] 2026-05-16 Codex: Remove shell-only modals, forms, browse panes, and detail panes that duplicate full dashboard behavior.
- [x] 2026-05-16 Codex: Shrink `src/Armada.Proxy/wwwroot/app.js` to portal-only logic.
- [x] 2026-05-16 Codex: Shrink `src/Armada.Proxy/wwwroot/app.css` to portal-only styling.
- [x] 2026-05-16 Codex: Preserve only minimal offline or tunnel-state diagnostics needed to choose a deployment and understand why it cannot be opened.
- [x] 2026-05-16 Codex: Keep the portal intentionally small enough that future product feature work does not land there by habit.

#### Exit Criteria

- [x] 2026-05-16 Codex: The proxy root UI is an access portal, not a second operations dashboard.
- [x] 2026-05-16 Codex: The proxy `wwwroot` code size is materially smaller than today.

### Workstream 3: Serve The Real Dashboard From The Proxy

#### Scope

Make the proxy serve the React dashboard bundle at `/dashboard` without forking it.

#### Tasks

- [x] 2026-05-16 Codex: Decide the asset packaging model:
  - recommended: ship `src/Armada.Dashboard/dist` with the proxy build and serve it directly
  - alternate: copy the built dashboard into proxy `wwwroot/dashboard` at publish time
- [x] 2026-05-16 Codex: Update proxy static asset registration to serve `/dashboard` and `/dashboard/assets/*`.
- [x] 2026-05-16 Codex: Ensure dashboard `index.html`, assets, and i18n files resolve correctly from the proxy origin.
- [x] 2026-05-16 Codex: Keep the React basename at `/dashboard`.
- [x] 2026-05-16 Codex: Avoid creating a proxy-only dashboard fork; remote-specific behavior is runtime-driven.
- [x] 2026-05-16 Codex: No proxy-local bootstrap endpoint was needed; the shared dashboard bundle stays shared.

#### Exit Criteria

- [x] 2026-05-16 Codex: The full React dashboard now loads from the proxy origin at `/dashboard`.
- [x] 2026-05-16 Codex: There is no second remote dashboard implementation to maintain.

### Workstream 4: Generic HTTP Relay For `/api/v1/*`

#### Scope

Replace entity-specific proxy route handling with a generic relay for dashboard REST traffic.

#### Tasks

- [x] 2026-05-16 Codex: Add a proxy-side relay path responsible for forwarding browser HTTP requests to the selected remote instance through the tunnel.
- [x] 2026-05-16 Codex: Add server-side tunnel handling for generic HTTP relay requests and responses.
- [x] 2026-05-16 Codex: Relay request method, path, query string, selected headers, content type, and body.
- [x] 2026-05-16 Codex: Relay response status code, selected headers, content type, and body.
- [x] 2026-05-16 Codex: Support JSON, text, and binary bodies.
- [x] 2026-05-16 Codex: Support downloads and uploads used by the current dashboard, with proxy policy explicitly denying high-risk restore and other blocked routes.
- [x] 2026-05-16 Codex: Add explicit bounded-size semantics for large bodies and fail clearly instead of silently assuming unlimited in-memory payloads. The relay now rejects request or response bodies above `Constants.DefaultRemoteRelayMaxBodyBytes` until chunked transfer is intentionally added.
- [x] 2026-05-16 Codex: Propagate cancellation and timeout behavior cleanly.
- [x] 2026-05-16 Codex: Preserve request correlation IDs and forwarded request headers for diagnostics.
- [x] 2026-05-16 Codex: Add a central route policy layer in the proxy so high-risk routes can be denied explicitly without rebuilding UI-specific shims.
- [x] 2026-05-16 Codex: Map `ALL /api/v1/*` in the proxy to the relay path.
- [x] 2026-05-16 Codex: Stop adding new `MapJsonGet/Post/Put/Delete` instance routes for dashboard feature parity by removing the old instance route family.

#### Exit Criteria

- [x] 2026-05-16 Codex: The dashboard can load its REST data from the proxy without per-entity proxy route reimplementation.
- [x] 2026-05-16 Codex: New dashboard REST features do not require new proxy entity endpoints by default.

### Workstream 5: WebSocket Compatibility Relay For `/ws`

#### Scope

Make the full dashboard's live WebSocket behaviors work remotely without rewriting them into proxy-specific polling or event APIs.

#### Tasks

- [x] 2026-05-16 Codex: Add a proxy WebSocket endpoint at `/ws` that the React dashboard can connect to unchanged.
- [x] 2026-05-16 Codex: Add tunnel session multiplexing so multiple browser WebSocket sessions can relay through one connected Armada instance.
- [x] 2026-05-16 Codex: Relay open, message, error, and close semantics cleanly.
- [x] 2026-05-16 Codex: Preserve message ordering per relayed browser socket.
- [x] 2026-05-16 Codex: Preserve close codes and close reasons where practical.
- [x] 2026-05-16 Codex: Handle reconnects cleanly when the remote Armada tunnel drops and recovers. `ProxyDashboardRelayIntegrationTests` now verifies browser WebSocket close on tunnel loss and successful reconnect after tunnel recovery.
- [x] 2026-05-16 Codex: Verify current dashboard live behaviors continue to work through relay with representative browser-session coverage:
  - notifications
  - list refreshes
  - planning and refinement live updates
  - mission/voyage state changes
  - any dashboard WebSocket command flows still in use
- [x] 2026-05-16 Codex: Update `docs/WEBSOCKET_API.md` to describe proxy compatibility once shipped.

#### Exit Criteria

- [x] 2026-05-16 Codex: The dashboard's existing `/ws` client works from the proxy origin without changing the shared client contract.
- [x] 2026-05-16 Codex: The proxy no longer needs custom shell live-update logic for feature parity.

### Workstream 6: Dashboard Proxy Mode And UX Integration

#### Scope

Make the shared dashboard feel coherent when used remotely through the proxy.

#### Tasks

- [x] 2026-05-16 Codex: Add a small proxy-context API the dashboard can query for selected-instance metadata and remote-mode flags.
- [x] 2026-05-16 Codex: Add a dashboard-level remote banner, chip, or context strip showing the selected instance and proxy mode.
- [x] 2026-05-16 Codex: Add a way to switch deployments or return to the proxy portal.
- [x] 2026-05-16 Codex: Make proxy logout and selected-instance clearing reachable from the dashboard shell.
- [x] 2026-05-16 Codex: Decide how to handle auth in the first release:
  - keep proxy auth and remote Armada auth separate, or
  - add session brokerage if it is tractable without blocking cutover
- [x] 2026-05-16 Codex: If double auth remains, make the flow explicit and non-confusing.
- [x] 2026-05-16 Codex: If route policy blocks a dashboard action remotely, show a specific policy-denied message rather than a generic failure.
- [x] 2026-05-16 Codex: Audit dashboard pages for hidden local-origin assumptions. The `Server`, `Settings`, `Tenants`, `Users`, and `Credentials` pages now reflect proxy mode and block or downgrade local-only edit flows appropriately.

#### Exit Criteria

- [x] 2026-05-16 Codex: Remote users are clearly inside the selected deployment context through the proxy portal, dashboard proxy-context strip, and proxy-mode messaging on local-only server controls.
- [x] 2026-05-16 Codex: The dashboard is materially more coherent remotely, including the remaining admin-page audit pass.

### Workstream 7: Deletion Of The Legacy Proxy Surface

#### Scope

Delete the old shape rather than keeping both models indefinitely.

#### Tasks

- [x] 2026-05-16 Codex: Remove the old proxy shell navigation and feature sections.
- [x] 2026-05-16 Codex: Remove the old instance-scoped proxy route family from `src/Armada.Proxy/ArmadaProxyServer.cs`.
- [x] 2026-05-16 Codex: Remove dead shell helper functions, UI rendering logic, and CSS blocks from `src/Armada.Proxy/wwwroot/app.js` and `app.css`.
- [x] 2026-05-16 Codex: Remove `src/Armada.Server/RemoteControlOperationsService.cs` and the related legacy query/management tunnel services now that generic relay is complete.
- [x] 2026-05-16 Codex: Remove old tunnel capability documentation for feature-specific proxy forwarding.
- [x] 2026-05-16 Codex: Remove old tests that only validate the legacy shell path and replace them with portal-plus-dashboard relay tests.
- [x] 2026-05-16 Codex: No compatibility aliases were added during cutover, so there is no alias cleanup left to do.

#### Exit Criteria

- [x] 2026-05-16 Codex: There is one remote-dashboard path, not two.
- [x] 2026-05-16 Codex: The legacy proxy shell, route family, and server-side compatibility dispatcher are gone.

### Workstream 8: Verification, Rollout, And Documentation

#### Scope

Make the cutover safe and observable.

#### Tasks

- [x] 2026-05-16 Codex: Add unit tests for proxy auth, selected-instance session handling, and route-policy decisions.
- [x] 2026-05-16 Codex: Add integration tests for HTTP relay covering:
  - GET
  - POST
  - PUT
  - DELETE
  - JSON bodies
  - binary upload
  - binary download
  - error propagation
- [x] 2026-05-16 Codex: Add integration tests for WebSocket relay covering:
  - open
  - message flow
  - reconnect
  - remote close
  - proxy deny/error behavior
- [x] 2026-05-16 Codex: Add browser-session smoke coverage for:
  - proxy login
  - deployment selection
  - opening `/dashboard`
  - dashboard login if applicable
  - page navigation
  - live WebSocket connectivity
  - representative write flows
- [x] 2026-05-16 Codex: Validate the remote proxy portal at desktop, tablet, and mobile widths. Playwright validation was run at `1280x900`, `834x1112`, and `390x844`; the dashboard itself is the same shared React bundle already used locally.
- [x] 2026-05-16 Codex: Update `README.md` to describe the new remote-access model.
- [x] 2026-05-16 Codex: Rewrite `docs/REMOTE_MGMT.md` around the portal-plus-dashboard flow.
- [x] 2026-05-16 Codex: Rewrite `docs/PROXY_API.md` around proxy-local APIs plus generic relay behavior.
- [x] 2026-05-16 Codex: Rewrite `docs/TUNNEL_PROTOCOL.md` around the reduced transport contract.
- [x] 2026-05-16 Codex: Update `docs/WEBSOCKET_API.md` to explain remote proxy compatibility mode when shipped.
- [x] 2026-05-16 Codex: Add rollback instructions for backing out to the last legacy-shell release if a hotfix rollback is required.

#### Exit Criteria

- [x] 2026-05-16 Codex: The documentation now describes the system that actually ships.
- [x] 2026-05-16 Codex: The focused relay test matrix proves the shared dashboard transport works remotely through the proxy.

## Recommended PR Sequence

- [x] 2026-05-16 Codex: PR 1 equivalent completed. The plan landed and legacy shell scope was frozen; the cutover shipped directly without feature flags.
- [x] 2026-05-16 Codex: PR 2 equivalent completed. Proxy-local endpoints moved to `/proxy-api/v1/*` and the root portal was simplified.
- [x] 2026-05-16 Codex: PR 3 equivalent completed. The React dashboard is now served from the proxy at `/dashboard`.
- [x] 2026-05-16 Codex: PR 4 equivalent completed. Generic HTTP relay for `/api/v1/*` is live.
- [x] 2026-05-16 Codex: PR 5 equivalent completed. WebSocket relay for `/ws` is live.
- [x] 2026-05-16 Codex: PR 6 equivalent completed. Dashboard proxy context UX and deployment switching/logout flows are in place.
- [x] 2026-05-16 Codex: PR 7 equivalent completed. Legacy instance-scoped proxy routes and the old shell are removed.
- [x] 2026-05-16 Codex: PR 8 equivalent completed. Docs, tests, rollout notes, and feature-flag conclusions are captured in this plan and the rewritten remote-access docs.

## File Map

### Primary Proxy Files

- `src/Armada.Proxy/ArmadaProxyServer.cs`
- `src/Armada.Proxy/Program.cs`
- `src/Armada.Proxy/Settings/ProxySettings.cs`
- `src/Armada.Proxy/Services/ProxyAuthService.cs`
- `src/Armada.Proxy/Services/InstanceRegistry.cs`
- `src/Armada.Proxy/Services/RemoteInstanceSession.cs`
- `src/Armada.Proxy/wwwroot/index.html`
- `src/Armada.Proxy/wwwroot/app.js`
- `src/Armada.Proxy/wwwroot/app.css`

### Primary Server/Tunnel Files

- `src/Armada.Server/RemoteTunnelManager.cs`
- `src/Armada.Server/RemoteDashboardRelayService.cs`
- `src/Armada.Core/Models/RemoteTunnelEnvelope.cs`
- `src/Armada.Core/Constants.cs`

### Primary Dashboard Files

- `src/Armada.Dashboard/src/App.tsx`
- `src/Armada.Dashboard/src/api/client.ts`
- `src/Armada.Dashboard/src/context/WebSocketContext.tsx`
- `src/Armada.Dashboard/src/components/Layout.tsx`
- `src/Armada.Dashboard/src/pages/Server.tsx`
- `src/Armada.Dashboard/vite.config.ts`

### Primary Docs

- `README.md`
- `docs/REMOTE_MGMT.md`
- `docs/PROXY_API.md`
- `docs/TUNNEL_PROTOCOL.md`
- `docs/WEBSOCKET_API.md`
- `PROXY_PLAN.md`

## Acceptance Criteria

- [x] 2026-05-16 Codex: A developer adding a new dashboard REST page does not need to add a new proxy entity route family.
- [x] 2026-05-16 Codex: A developer adding a new dashboard WebSocket-driven behavior does not need to invent a new proxy shell workaround.
- [x] 2026-05-16 Codex: The proxy no longer contains a second operations dashboard with separate UX logic for the same product areas.
- [x] 2026-05-16 Codex: The remote-access model is explainable in one sentence: "The proxy is a portal and relay for the real Armada dashboard."
- [x] 2026-05-16 Codex: The legacy proxy shell can be deleted without losing the intended remote user experience.

## Risks And Mitigations

### Risk: Route collisions between proxy-local APIs and relayed Armada APIs

- Mitigation: move proxy-local APIs to `/proxy-api/v1/*` before relay cutover.

### Risk: Large bodies break generic relay assumptions

- Mitigation: keep high-risk large uploads blocked, enforce explicit relay body-size limits, and add chunked transfer only when a real shared-dashboard route needs it.

### Risk: Double auth is confusing

- Mitigation: make it explicit in the portal and dashboard, then iterate toward session brokerage later if needed.

### Risk: Dangerous remote actions become too easy to invoke

- Mitigation: keep a central proxy policy layer with explicit denials or confirmations for selected routes.

### Risk: The team keeps both systems alive forever

- Mitigation: treat legacy route and shell deletion as a required workstream, not optional cleanup.

## Rollback Instructions

If a release must back out to the legacy proxy shell, treat rollback as a binary/version rollback rather than a runtime toggle:

1. Redeploy the last pre-cutover proxy and server release that still contains the legacy shell and the feature-specific tunnel compatibility services.
2. Verify the restored proxy serves the legacy root shell and that the old instance-scoped `/api/v1/instances/{instanceId}/...` routes are present again.
3. Keep `/dashboard` remote-entry links disabled in release notes or operator runbooks for that rollback build, because the legacy release does not represent the new portal-plus-relay contract.
4. Reconnect remote Armada instances against the restored proxy build and confirm legacy tunnel capabilities are advertised again.
5. After the rollback incident is resolved, re-apply this cutover from the current branch rather than attempting to run both remote models side by side.

## Execution Log

Use this section for short dated notes as work lands.

- [x] `2026-05-16` Initial implementation notes: moved proxy-local routes to `/proxy-api/v1/*`, replaced the proxy shell with a minimal login-and-selection portal, served the shared React dashboard from the proxy build, added generic `/api/v1/*` tunnel relay, added `/ws` compatibility relay, added proxy route policy, added dashboard proxy-context UX, and added unit coverage for proxy auth and route policy.
- [x] `2026-05-16` Documentation cutover notes: marked `PROXY_PLAN.md` as superseded, rewrote `README.md`, `docs/REMOTE_MGMT.md`, `docs/PROXY_API.md`, `docs/TUNNEL_PROTOCOL.md`, and `docs/WEBSOCKET_API.md` around the portal-plus-relay model, and recorded route/method classification snapshots in this plan.
- [x] `2026-05-16` Proxy-mode UX notes: updated the dashboard `Server` page so remote proxy sessions disable blocked local-only controls and stop showing MCP bootstrap guidance that is invalid through the proxy.
- [x] `2026-05-16` Relay cutover notes: removed the legacy feature-specific tunnel dispatcher from `ArmadaServer`, reduced the Armada capability manifest to the generic relay contract, and deleted the old remote-control service suites from the unit-test runner.
- [x] `2026-05-16` Legacy shell removal notes: the proxy shell, proxy instance route family, and server-side compatibility surface are now all removed from the live product path; only portal-plus-dashboard relay behavior remains.
- [x] `2026-05-16` Relay verification notes: `RemoteDashboardRelayServiceTests` now cover HTTP method/body/error relay and WebSocket open/message/close behavior, while `ProxyDashboardRelayIntegrationTests` cover proxy login, deployment selection, dashboard route loading, relayed auth/read/write flows, binary upload/download, upstream error propagation, WebSocket denial, and tunnel reconnect recovery.
- [x] `2026-05-16` Dashboard audit notes: `Settings`, `Tenants`, `Users`, and `Credentials` now honor proxy mode in addition to the earlier `Server` page changes, so remote users see policy-aware behavior on the remaining admin surface.
- [x] `2026-05-16` Responsive validation notes: ran Playwright portal validation at desktop, tablet, and mobile widths to confirm the remaining proxy-specific UI stays usable across those sizes.
- [x] `2026-05-16` Runtime-hardening notes: standalone proxy/server startup now tolerates an explicit empty `SyslogServers` list by falling back to console/file logging instead of crashing during process boot.
