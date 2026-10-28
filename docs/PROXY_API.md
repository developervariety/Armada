# Proxy API

**Version:** 1.0.0

`Armada.Proxy` is a portal and relay for the Armada dashboard. The dashboard owns the operations UI.

The shipped proxy responsibilities are:

- browser authentication to the proxy
- connected-instance discovery and selection
- serving the shared React dashboard bundle at `/dashboard`
- relaying Armada REST traffic at `/api/v1/*`
- relaying the dashboard websocket at `/ws`
- terminating the outbound Armada tunnel at `/tunnel`
- enforcing explicit remote-access policy for blocked routes

## Default Bind

By default, the proxy binds to:

- host: `localhost`
- port: `7893`
- portal: `http://localhost:7893/`
- shared dashboard: `http://localhost:7893/dashboard`
- health: `http://localhost:7893/proxy-api/v1/status/health`
- tunnel: `ws://localhost:7893/tunnel`

Configuration is loaded from the `ArmadaProxy` section:

```json
{
  "ArmadaProxy": {
    "dataDirectory": "/app/data",
    "logDirectory": "/app/data/logs",
    "hostname": "localhost",
    "port": 7893,
    "requireEnrollmentToken": false,
    "enrollmentTokens": [],
    "password": "change-this-shared-secret",
    "handshakeTimeoutSeconds": 15,
    "staleAfterSeconds": 90,
    "requestTimeoutSeconds": 20,
    "maxRecentEvents": 50
  }
}
```

The proxy refuses to start while the shared password is blank or the built-in default (`armadaadmin`). Set `password` in the settings file or the `ARMADA_PROXY_PASSWORD` environment variable, which overrides the file. For a throwaway local test only, `allowDefaultPassword` (or `ARMADA_PROXY_ALLOW_DEFAULT_PASSWORD=true`) lets the proxy start with the default and logs a warning.

Login hardening settings, all under `ArmadaProxy`:

| Setting | Default | Meaning |
| --- | --- | --- |
| `loginMaxFailures` | `10` | Failed browser logins or tunnel handshakes from one client address inside the window that trigger a lockout (1 to 1000) |
| `loginFailureWindowSeconds` | `900` | Window over which failures are counted (10 to 86400) |
| `loginLockoutSeconds` | `900` | Lockout length (1 to 86400) |
| `secureCookie` | `false` | Always mark the session cookie `Secure`; set it when the proxy is served over HTTPS |
| `trustForwardedHeaders` | `false` | Trust `X-Forwarded-For`, `Forwarded` and `X-Forwarded-Proto` for the client address and scheme; set it only behind a reverse proxy that overwrites them |

## Browser Model

The remote browser flow is:

1. Open `/`.
2. Request a login challenge from `/proxy-api/v1/auth/challenge`.
3. Submit a SHA-256 proof to `/proxy-api/v1/auth/login`.
4. Receive an authenticated proxy session.
5. List connected deployments from `/proxy-api/v1/instances`.
6. Store the selected deployment with `/proxy-api/v1/session/instance`.
7. Open `/dashboard`.
8. Use the normal Armada dashboard against same-origin `/api/v1/*` and `/ws`.

The proxy session is separate from the Armada application session. After opening `/dashboard`, the user may still need to sign into the selected Armada deployment.

## Authentication And Session Handling

The proxy browser session is primarily cookie-backed:

- cookie name: `armada_proxy_session`
- attributes: `Path=/; HttpOnly; SameSite=Lax`, plus `Secure` when `secureCookie` is set or a trusted `X-Forwarded-Proto` says `https`

For non-browser callers, the proxy accepts `X-Armada-Proxy-Session`. A login that sends `"setCookie": false` receives no cookie, so a native client keeps the session only where it stores the returned token.

Every `/proxy-api/v1` response carries `Cache-Control: no-store`, so no cache keeps a nonce, a session token or the instance list.

The browser never sends the raw shared password. It first requests a nonce and then submits a derived proof:

### `GET /proxy-api/v1/auth/challenge`

Returns a one-time login challenge. The proxy holds at most 16 unused, unexpired challenges per client address and 4096 in total. Past the per-address bound the response is `429`; past the total it is `503`. Both carry `Retry-After` and a body with `error` and `refusal` (`AddressLimit` or `GlobalLimit`):

```json
{
  "nonce": "4f3a0c7a8f6c49d9b6711d2c1a7b5e90",
  "expiresUtc": "2026-05-16T18:30:00Z"
}
```

### `POST /proxy-api/v1/auth/login`

Request:

```json
{
  "nonce": "4f3a0c7a8f6c49d9b6711d2c1a7b5e90",
  "proofSha256": "8f5c4e1e1d7b5d8b2f6c6c987bfb76f5d55a75b8b940f882c817d39de42d83cc"
}
```

Response:

```json
{
  "token": "0f34455311b54e719f50927df5ecdfd798f8f27ed4ae45e2a73c0c3b2d194f73",
  "expiresUtc": "2026-05-17T18:30:00Z",
  "selectedInstanceId": null
}
```

The response body includes the token for native clients that send it in `X-Armada-Proxy-Session`; browser callers normally rely on the `Set-Cookie` header instead. Send `"setCookie": false` to receive no cookie.

Failed logins and failed tunnel handshakes are counted per client address. The client address is the socket peer address unless `trustForwardedHeaders` is set. A client that reaches `loginMaxFailures` failures inside `loginFailureWindowSeconds` is locked out for `loginLockoutSeconds`: login and tunnel handshake return `429` with `Retry-After`, even with the right password. Active lockouts are written to `proxy-lockouts.json` in the proxy data directory and survive a restart; failure counts below the limit are in memory only. A successful login clears the client's count.

### `POST /proxy-api/v1/auth/logout`

Invalidates the current proxy browser session and clears the session cookie.

## Proxy-Local Routes

These routes belong to the proxy itself and are never relayed to Armada:

| Route | Auth | Purpose |
| --- | --- | --- |
| `GET /` | no | Minimal login-and-selection portal |
| `GET /proxy-api/v1/status/health` | no | Proxy process health and instance counts |
| `GET /proxy-api/v1/auth/challenge` | no | Browser login challenge |
| `POST /proxy-api/v1/auth/login` | no | Browser login |
| `POST /proxy-api/v1/auth/logout` | yes | Proxy logout |
| `GET /proxy-api/v1/instances` | yes | Connected deployment summaries |
| `GET /proxy-api/v1/session/context` | yes | Current proxy session and selected deployment metadata |
| `POST /proxy-api/v1/session/instance` | yes | Set selected deployment |
| `POST /proxy-api/v1/session/logout-instance` | yes | Clear selected deployment |
| `GET /dashboard` and `GET /dashboard/*` | yes + selected instance | Shared React dashboard bundle |
| `WS /tunnel` | instance auth | Armada outbound tunnel |

`GET /proxy-api/v1/session/context` returns the selected deployment summary plus relay capability flags:

```json
{
  "isAuthenticated": true,
  "expiresUtc": "2026-05-17T18:30:00Z",
  "selectedInstanceId": "armada-1f2e3d4c5b6a",
  "selectedInstance": {
    "instanceId": "armada-1f2e3d4c5b6a",
    "state": "connected"
  },
  "relay": {
    "dashboard": true,
    "api": true,
    "websocket": true
  }
}
```

## Relayed Dashboard Routes

Once a deployment is selected, the proxy exposes the same origin shape expected by `Armada.Dashboard`:

| Route | Purpose |
| --- | --- |
| `ALL /api/v1/*` | Relay to the selected Armada deployment over the tunnel |
| `WS /ws` | Relay the dashboard websocket to the selected Armada deployment |

Behavior when session state is missing:

- `/dashboard*` without a proxy session or selected deployment redirects to `/`
- `/api/v1/*` without a proxy session returns `401`
- `/api/v1/*` without a selected deployment returns `409`
- `/ws` requires both an authenticated proxy session and a selected deployment

The dashboard bundle served from `/dashboard` is the same built output from `src/Armada.Dashboard/dist`, which carries the `i18n/armada.json` catalog.

A `/dashboard/*` path is URL-decoded and resolved against the dashboard bundle directory. The proxy serves a file only when the resolved path is that directory or lies below it (the directory plus a path separator, compared exactly), so an encoded `..` cannot reach a sibling directory whose name starts with `dashboard`. Any other path returns the dashboard index.

Every tunnelled request has one deadline of `requestTimeoutSeconds`, started before the request is sent. The deadline covers both sending the request and waiting for the response, so a stalled tunnel send still times out: an HTTP relay answers `504` and the pending request is released. A browser that disconnects cancels its relay without a timeout response.

## Relay Policy

The relay transport is generic, but remote policy is still explicit.

### Always blocked

- `POST /api/v1/server/stop`
- `POST /api/v1/server/reset`
- `POST /api/v1/status/shutdown`
- `POST /api/v1/status/factory-reset`
- `POST /api/v1/restore`

### Write-blocked administrative families

Non-`GET` and non-`HEAD` requests are blocked for:

- `/api/v1/settings*`
- `/api/v1/tenants*`
- `/api/v1/users*`
- `/api/v1/credentials*`

Blocked requests return `403` with an explicit policy message. The transport does not silently fall back to a proxy-specific workaround.

### Still allowed

- normal dashboard reads across `/api/v1/*`
- normal dashboard writes outside the blocked families
- file downloads such as `GET /api/v1/backup`
- websocket traffic at `/ws`

## Tunnel Compatibility Notes

The proxy uses generic relay methods:

- `armada.http.request`
- `armada.ws.open`
- `armada.ws.message`
- `armada.ws.close`

Armada answers any other tunnel method with `404 unsupported_method`; remote dashboard support extends through the generic relay.
