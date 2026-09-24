# Armada REST API Reference

**Version:** 0.9.0
**Base URL:** `http://localhost:7890`
**Content-Type:** `application/json`

---

## Table of Contents

- [Authentication](#authentication)
  - [Bearer Token (Recommended)](#bearer-token-recommended)
  - [Encrypted Session Token](#encrypted-session-token)
  - [API Key (Deprecated)](#api-key-deprecated)
  - [Authorization Tiers](#authorization-tiers)
  - [Authorization Matrix](#authorization-matrix)
- [Pagination](#pagination)
- [Error Responses](#error-responses)
- [Endpoints](#endpoints)
  - [Authentication Endpoints](#authentication-endpoints)
  - [Tenant Management](#tenant-management)
  - [User Management](#user-management)
  - [Credential Management](#credential-management)
  - [Harbor Runner Enrollment](#harbor-runner-enrollment)
  - [Harbor Jobs](#harbor-jobs)
  - [Status](#status)
  - [Fleets](#fleets)
  - [Vessels](#vessels)
  - [Voyages](#voyages)
  - [Missions](#missions)
  - [Captains](#captains)
  - [Signals](#signals)
  - [Events](#events)
  - [Docks](#docks)
  - [Merge Queue](#merge-queue)
  - [Code Index](#code-index)
  - [Playbooks](#playbooks)
  - [Memories](#memories)
  - [Prompt Templates](#prompt-templates)
  - [Personas](#personas)
  - [Pipelines](#pipelines)
  - [Workspace](#workspace)
  - [Planning Sessions](#planning-sessions)
  - [Inbox](#inbox)
  - [Background Jobs](#background-jobs)
  - [Backup and Restore](#backup-and-restore)
- [Data Types](#data-types)
  - [Models](#models)
  - [Enumerations](#enumerations)
  - [Request Types](#request-types)
  - [Response Wrappers](#response-wrappers)

---

## Authentication

As of v0.3.0, Armada supports multi-tenant authentication. All endpoints (except those listed as exempt) require authentication. There are three authentication methods, evaluated in the following order:

### Bearer Token (Recommended)

Bearer tokens are the canonical authentication mechanism. Each token is a 64-character random alphanumeric string stored in a `Credential` record, linked to a specific tenant and user.

```
Authorization: Bearer <token>
```

The default installation seeds a credential with bearer token `default`, so `Authorization: Bearer default` works out of the box for single-user setups.

### Encrypted Session Token

Session tokens are self-contained, AES-256-CBC encrypted tokens with a 24-hour lifetime. They are returned by `POST /api/v1/authenticate` and are intended for interactive/dashboard use. No server-side session storage is required -- the token contains the tenant ID, user ID, and expiration timestamp, validated by decryption.

```
X-Token: <encrypted-session-token>
```

### API Key (Deprecated)

The `X-Api-Key` header is retained for backward compatibility. When `ApiKey` is configured in settings, the server creates a synthetic admin tenant (`ten_system`) and user (`usr_system`) on startup. The API key resolves to this synthetic admin identity through the same `AuthContext` path as all other auth methods.

```
X-Api-Key: your-api-key-here
```

> **Deprecation notice:** `X-Api-Key` will be removed in a future version. Migrate to bearer tokens for new integrations.

### Authorization Tiers

All endpoints fall into one of three authorization levels:

| Level | Description |
|-------|-------------|
| `NoAuthRequired` | Health check, tenant lookup, onboarding, authenticate |
| `Authenticated` | All operational CRUD within the caller's tenant |
| `AdminOnly` | Tenant/user/credential management (with self-read exceptions) |

Armada distinguishes three effective caller roles:

- `IsAdmin = true`: global system admin. Can access any tenant and any object in the system.
- `IsAdmin = false`, `IsTenantAdmin = true`: tenant-scoped admin. Can access and manage any object within the caller's tenant, including users and credentials in that tenant.
- `IsAdmin = false`, `IsTenantAdmin = false`: regular user. Can read tenant-scoped operational data within the caller's tenant, but self-service routes are limited to the caller's own user account and own credentials. Server-controlled fields and protected resources cannot be modified directly.

Operational entities persist both `TenantId` and `UserId`. Those ownership columns are indexed and enforced by foreign keys across SQLite, PostgreSQL, SQL Server, and MySQL.

### Authorization Matrix

| Endpoint | Method | Permission | Notes |
|----------|--------|------------|-------|
| `/api/v1/server/stop` | POST | NoAuthRequired\*\*\* | When `RequireAuthForShutdown` is `true`, requires global admin (`IsAdmin = true`) |
| `/api/v1/server/restart` | POST | NoAuthRequired\*\*\* | When `RequireAuthForShutdown` is `true`, requires global admin (`IsAdmin = true`) |
| `/api/v1/status/health` | GET | NoAuthRequired | |
| `/api/v1/authenticate` | POST | NoAuthRequired | |
| `/api/v1/tenants/lookup` | POST | NoAuthRequired | Input: email, returns matching tenants |
| `/api/v1/onboarding` | POST | NoAuthRequired | Gated by `AllowSelfRegistration` setting |
| `/api/v1/whoami` | GET | Authenticated | |
| `/api/v1/status` | GET | Authenticated | Tenant-scoped |
| `/api/v1/settings` | GET | AdminOnly | Server configuration and remote-control settings |
| `/api/v1/settings` | PUT | AdminOnly | Partial update of server configuration and remote-control settings |
| `/api/v1/settings/reload` | POST | AdminOnly | Validate and apply runtime-tunable values from the bound settings file |
| `/api/v1/fleets` | ALL | Authenticated | Tenant-scoped |
| `/api/v1/vessels` | ALL | Authenticated | Tenant-scoped. `LocalPath`, `WorkingDirectory` and a local-path `RepoUrl` are server paths only a global admin sets; see [POST /api/v1/vessels](#post-apiv1vessels) |
| `/api/v1/captains` | ALL | Authenticated | Tenant-scoped |
| `/api/v1/missions` | ALL | Authenticated | Tenant-scoped |
| `/api/v1/voyages` | ALL | Authenticated | Tenant-scoped |
| `/api/v1/docks` | ALL | Authenticated | Tenant-scoped |
| `/api/v1/signals` | ALL | Authenticated | Tenant-scoped |
| `/api/v1/events` | ALL | Authenticated | Tenant-scoped |
| `/api/v1/merge-queue` | ALL | Authenticated | Tenant-scoped |
| `/api/v1/playbooks` | GET/POST/PUT/DELETE | Authenticated / TenantAdmin | Reads are tenant-scoped for any authenticated user. Mutations require tenant admin. |
| `/api/v1/memories` | ALL | Authenticated | Tenant-scoped. A caller sees the tenant-wide records of its tenant plus its own; only a tenant admin changes a tenant-wide record. |
| `/api/v1/prompt-templates` | GET, POST `/enumerate` | Authenticated | Reads follow the ownership rule below. A user-specific template is never resolved into a mission prompt |
| `/api/v1/prompt-templates` | POST/PUT, POST `/{name}/reset` | AdminOnly | Global admin only, because a change affects every tenant. Create records the caller's tenant and user |
| `/api/v1/personas` | GET, POST `/enumerate` | Authenticated | Reads follow the ownership rule below |
| `/api/v1/personas` | POST/PUT/DELETE | TenantAdmin | Create records the caller's tenant and user and never a built-in flag. Update and delete find the persona inside the caller's tenant and require edit rights under the ownership rule; a global admin reaches every tenant. Every tenant uses a built-in persona, so only a global admin may update one; anyone else receives `403` |
| `/api/v1/inbox` | GET | AdminOnly | Global admin only. The inbox reads fleet-wide state that carries no tenant or user scope |
| `/api/v1/ask` | POST | AdminOnly | Global admin only. Answers come from fleet-wide state that carries no tenant or user scope |
| `/api/v1/jobs` | GET | AdminOnly | Global admin only. Long-running background jobs carry no tenant or user scope, the same audience as MCP `armada_job_status` |
| `/api/v1/captains/{id}/chat` | POST | TenantAdmin | The captain is found inside the caller's scope; another tenant's captain returns `404` before its runtime starts |
| `/api/v1/check-runs` | GET, POST `/enumerate` | Authenticated | Tenant-scoped reads |
| `/api/v1/check-runs` | POST/DELETE | TenantAdmin | Run, import, retry, GitHub Actions sync and delete. A linked mission, voyage or deployment must be in the caller's tenant (`400` otherwise). `commandOverride`, and retrying an imported check, are global admin only (`403`). `Deploy` and `Rollback` checks run only through the deployment workflow (`400`) |
| `/api/v1/workspace/vessels/{id}/exec` | POST | AdminOnly | Global admin only. The command runs as the server process, so it is host shell access |
| `/api/v1/coordination` | ALL | AdminOnly | Global admin only. Rooms are found by key alone, so every tenant shares every room, message, claim and participant |
| `/api/v1/pipelines` | GET, POST `/enumerate` | Authenticated | Reads follow the ownership rule below |
| `/api/v1/pipelines` | POST/PUT/DELETE | TenantAdmin | Create records the caller's tenant and user and never a built-in flag. Update and delete find the pipeline inside the caller's tenant and require edit rights under the ownership rule; a global admin reaches every tenant. Every tenant uses a built-in pipeline, so only a global admin may update one; anyone else receives `403` |

**Ownership rule for personas, pipelines and prompt templates.** Each record has
`TenantId`, `UserId` and `OwnershipScope` (`TenantWide` or `UserSpecific`). The
same rule governs native memory. A global administrator reads every record.
Nobody else crosses a tenant. Inside the tenant, a tenant administrator reads
every record, any user reads tenant-wide records, and only the owning user reads
a user-specific record. Built-in records stay readable to every authenticated
caller. List and enumerate totals count only the records the caller may read,
and a record the caller may not read returns `404`. `OwnershipScope` is about
who may see a record. It is separate from the applicability `Scope` of workflow
and project profiles. Records that existed before ownership are tenant-wide with
no owning user.

**Ids in a request body follow the same scope as ids in the path.** A route reads
every record its body names by id with the caller's scope, exactly as it reads a
path id, before it stores or acts on the request. A record outside that scope
returns `404` (an incident create returns `400`), and nothing is created,
dispatched or attached. This covers the vessel and dependency missions of
`POST /api/v1/voyages`; the vessel, voyage, captain, requested captain,
dependency and parent of `POST /api/v1/missions` and the dependency and parent a
`PUT /api/v1/missions/{id}` changes; the captain of
`POST /api/v1/vessels/{id}/build-context`; the vessel and mission of
`POST /api/v1/merge-queue`; the fleet of `POST /api/v1/planning-sessions`; and
the deployment, rollback deployment, release, check run, environment, vessel,
mission and voyage links of incident create and update. The matching MCP and
WebSocket create surfaces apply the same rule. A global admin names any record.

Paths that run without a caller read linked records only inside the owning
record's tenant: a mission's dependency must share the tenant of the vessel the
mission runs in, a Judge follow-up joins only a merge entry of its own tenant,
the incident lifecycle ignores a linked record of another tenant, and fleet and
captain default playbooks enter only a mission of their own tenant.

Dispatch uses a pipeline or persona on behalf of the owner of the vessel or
mission. A user-specific record of another user is refused, whether the
reference is an explicit id, a name, a vessel default or a fleet default. The
refusal is logged, and the reference is kept rather than cleared as missing.
The objective dispatch preview reports `pipeline_not_usable` or
`default_pipeline_not_usable`.
| `/api/v1/planning-sessions` | GET | Authenticated | Planning-session list in caller scope |
| `/api/v1/planning-sessions` | POST | TenantAdmin | Create one planning session in caller scope |
| `/api/v1/planning-sessions/{id}` | GET | Authenticated | Read one planning session in caller scope |
| `/api/v1/planning-sessions/{id}` | DELETE | TenantAdmin | Delete one planning session in caller scope |
| `/api/v1/planning-sessions/{id}/messages` | POST | TenantAdmin | Send one planning turn |
| `/api/v1/planning-sessions/{id}/summarize` | POST | TenantAdmin | Generate a dispatch draft without launching |
| `/api/v1/planning-sessions/{id}/dispatch` | POST | TenantAdmin | Launch a voyage from planning output |
| `/api/v1/planning-sessions/{id}/stop` | POST | TenantAdmin | Stop an active planning session |
| `/api/v1/planning-sessions/{id}/stop-turn` | POST | TenantAdmin | Abort an in-flight turn without ending the session |
| `/api/v1/tenants` | GET (list) | AdminOnly | Global admin only |
| `/api/v1/tenants` | POST | AdminOnly | Global admin only |
| `/api/v1/tenants/{id}` | GET | Authenticated | Global admin: any; tenant admin or regular user: own tenant only |
| `/api/v1/tenants/{id}` | PUT/DELETE | AdminOnly | Global admin only |
| `/api/v1/users` | GET (list) | AdminOnly | Global admin: all users. Tenant admin: users in own tenant |
| `/api/v1/users` | POST | AdminOnly | Global admin: any tenant. Tenant admin: own tenant only |
| `/api/v1/users/{id}` | GET | Authenticated | Global admin: any. Tenant admin: users in own tenant. Regular user: self only |
| `/api/v1/users/{id}` | PUT/DELETE | AdminOnly | Global admin: any. Tenant admin: users in own tenant. Regular user: self-update only |
| `/api/v1/credentials` | GET (list) | Authenticated | Global admin: all. Tenant admin: credentials in own tenant. Regular user: own only |
| `/api/v1/credentials` | POST | Authenticated | Global admin: any tenant/user. Tenant admin: own tenant. Regular user: self only |
| `/api/v1/credentials/{id}` | GET | Authenticated | Global admin: any. Tenant admin: own tenant. Regular user: own only |
| `/api/v1/credentials/{id}` | PUT | Authenticated | Global admin: any. Tenant admin: own tenant. Regular user: own only |
| `/api/v1/credentials/{id}` | DELETE | Authenticated | Global admin: any. Tenant admin: own tenant. Regular user: own only |

**Exempt routes** (no authentication required):
- `GET /api/v1/status/health`
- `POST /api/v1/authenticate`
- `POST /api/v1/tenants/lookup`
- `POST /api/v1/onboarding` (when `AllowSelfRegistration` is enabled)
- `POST /api/v1/server/stop` (when `RequireAuthForShutdown` is `false`, the default)
- `POST /api/v1/server/restart` (when `RequireAuthForShutdown` is `false`, the default)
- `GET /dashboard` and all `/dashboard/*` paths
- `GET /` (redirects to `/dashboard`)

---

## Pagination

All list endpoints return paginated results wrapped in `EnumerationResult<T>`. There are two ways to query:

### GET with Query String Parameters

```
GET /api/v1/missions?pageNumber=2&pageSize=25&status=InProgress&order=CreatedAscending
```

Every route percent-decodes each query-string value exactly once before it parses the value, so an encoded timestamp such as `fromUtc=2026-09-14T00%3A00%3A00.000Z` and its unencoded form select the same window. A `+` is kept as a literal plus (so an unencoded `+00:00` offset stays valid); send a space as `%20`. A value that still does not parse is ignored and the route's default applies.

### POST /enumerate with JSON Body

```
POST /api/v1/missions/enumerate
Content-Type: application/json

{
  "PageNumber": 2,
  "PageSize": 25,
  "Status": "InProgress",
  "Order": "CreatedAscending"
}
```

Query string parameters **override** body values on POST enumerate endpoints, allowing defaults in the body with per-request overrides via URL.

### Pagination Parameters

| Parameter | Type | Default | Range | Description |
|---|---|---|---|---|
| `pageNumber` | int | 1 | >= 1 | Page number (1-based) |
| `pageSize` | int | 100 | 1 - 1000 | Results per page |
| `order` | string | `CreatedDescending` | `CreatedAscending`, `CreatedDescending` | Sort order by creation date |
| `createdAfter` | datetime | null | ISO 8601 | Filter: created after this timestamp |
| `createdBefore` | datetime | null | ISO 8601 | Filter: created before this timestamp |

### Entity-Specific Filters

| Parameter | Applies To | Description |
|---|---|---|
| `status` | missions, voyages, captains | Filter by status value |
| `fleetId` | vessels | Filter by fleet ID |
| `vesselId` | missions, docks, events | Filter by vessel ID |
| `captainId` | missions, docks, events | Filter by captain ID |
| `voyageId` | missions, events | Filter by voyage ID |
| `missionId` | events | Filter by mission ID |
| `type` | events | Filter by event type (alias for `eventType`) |
| `signalType` | signals | Filter by signal type |
| `toCaptainId` | signals | Filter by recipient captain ID |
| `unreadOnly` | signals | `true` to return only unread signals |

### Paginated Response Shape

```json
{
  "Success": true,
  "PageNumber": 1,
  "PageSize": 25,
  "TotalPages": 4,
  "TotalRecords": 87,
  "Objects": [ ... ],
  "TotalMs": 3.14
}
```

---

## Error Responses

All error responses use a consistent JSON format with `Error`, `Description`, `Message`, and `Data` fields:

```json
{
  "Error": "NotFound",
  "Description": "The requested resource was not found.",
  "Message": "Mission not found",
  "Data": {}
}
```

| Field | Type | Description |
|---|---|---|
| `Error` | string | Error code (see table below) |
| `Description` | string | Standard description for the error code |
| `Message` | string | Human-readable message with specific details |
| `Data` | object | Additional context (usually empty) |

### Error Codes

| Error Code | HTTP Status | When Used |
|---|---|---|
| `BadRequest` | 400 | Invalid input, missing required fields, malformed request body, invalid state transition |
| `DeserializationError` | 400 | Request body could not be parsed as valid JSON or does not match the expected type |
| `NotAuthorized` | 401 | Missing or invalid API key / bearer token |
| `Forbidden` | 403 | Authenticated but not authorized for this operation |
| `NotFound` | 404 | Entity not found by the given ID |
| `Conflict` | 409 | Operation conflicts with current state (e.g., deleting an active voyage, retry landing failed) |
| `InternalError` | 500 | Unexpected server error |

### Notes

- The `Error` field always contains one of the error codes listed above.
- The `Message` field provides a specific, actionable description of what went wrong.
- HTTP status codes are set on the response and match the error code mapping above.
- Every route builds its authentication and authorization refusals through one shared mapping, so a `401`
  body carries `NotAuthorized` and a `403` body carries `Forbidden`. A route may name what the caller lacks in
  `Message` (for example `Administrator access required`); the `Error` code always matches the status.
- Clients should check the HTTP status code first, then parse the response body for details.

---

## Endpoints

### Authentication Endpoints

#### POST /api/v1/authenticate

Authenticate with email and password to receive an encrypted session token. This is the login endpoint used by the dashboard.

**Permission:** NoAuthRequired

**Request Body:** [AuthenticateRequest](#authenticaterequest)

```json
{
  "TenantId": "default",
  "Email": "admin@armada",
  "Password": "password"
}
```

**Response:** `200 OK` - [AuthenticateResult](#authenticateresult)

```json
{
  "Success": true,
  "Token": "eyJhbGciOi...",
  "ExpiresUtc": "2026-03-16T12:00:00Z"
}
```

**Errors:**
- `400 Bad Request` - Missing required fields (TenantId, Email, Password)
- `401 Unauthorized` - Invalid credentials or inactive tenant/user

---

#### GET /api/v1/whoami

Returns the authenticated user's tenant and user information.

**Permission:** Authenticated

**Request Headers:** `Authorization: Bearer <token>` or `X-Token: <session-token>`

**Response:** `200 OK` - [WhoAmIResult](#whoamiresult)

```json
{
  "Tenant": {
    "Id": "default",
    "Name": "Default Tenant",
    "Active": true,
    "CreatedUtc": "2026-03-07T12:00:00Z",
    "LastUpdateUtc": "2026-03-07T12:00:00Z"
  },
  "User": {
    "Id": "default",
    "TenantId": "default",
    "Email": "admin@armada",
    "PasswordSha256": "********",
    "FirstName": null,
    "LastName": null,
    "IsAdmin": true,
    "Active": true,
    "CreatedUtc": "2026-03-07T12:00:00Z",
    "LastUpdateUtc": "2026-03-07T12:00:00Z"
  }
}
```

**Errors:**
- `401 Unauthorized` - Not authenticated

---

#### POST /api/v1/tenants/lookup

Look up which tenants a given email address belongs to. Used by the dashboard login flow to determine the tenant before authentication.

**Permission:** NoAuthRequired

**Request Body:** [TenantLookupRequest](#tenantlookuprequest)

```json
{
  "Email": "admin@armada"
}
```

**Response:** `200 OK` - [TenantLookupResult](#tenantlookupresult)

```json
{
  "Tenants": [
    {
      "Id": "default",
      "Name": "Default Tenant"
    }
  ]
}
```

**Errors:**
- `400 Bad Request` - Missing email

---

#### POST /api/v1/onboarding

Self-register a new user within an existing tenant. Requires `AllowSelfRegistration` to be enabled in settings (default: `true`).

Creates a new user and an associated bearer token credential.

**Permission:** NoAuthRequired (gated by `AllowSelfRegistration` setting)

**Request Body:** [OnboardingRequest](#onboardingrequest)

```json
{
  "TenantId": "default",
  "Email": "newuser@example.com",
  "Password": "securepassword",
  "FirstName": "Jane",
  "LastName": "Doe"
}
```

**Response:** `200 OK` - [OnboardingResult](#onboardingresult)

```json
{
  "Success": true,
  "Tenant": {
    "Id": "default",
    "Name": "Default Tenant",
    "Active": true,
    "CreatedUtc": "2026-03-07T12:00:00Z",
    "LastUpdateUtc": "2026-03-07T12:00:00Z"
  },
  "User": {
    "Id": "usr_abc123",
    "TenantId": "default",
    "Email": "newuser@example.com",
    "PasswordSha256": "********",
    "FirstName": "Jane",
    "LastName": "Doe",
    "IsAdmin": false,
    "Active": true,
    "CreatedUtc": "2026-03-07T12:00:00Z",
    "LastUpdateUtc": "2026-03-07T12:00:00Z"
  },
  "Credential": {
    "Id": "crd_abc123",
    "TenantId": "default",
    "UserId": "usr_abc123",
    "Name": null,
    "BearerToken": "aBcDeFgH...",
    "Active": true,
    "CreatedUtc": "2026-03-07T12:00:00Z",
    "LastUpdateUtc": "2026-03-07T12:00:00Z"
  },
  "ErrorMessage": null
}
```

**Errors:**
- `400 Bad Request` - Missing required fields, email already exists in tenant
- `403 Forbidden` - Self-registration is disabled
- `404 Not Found` - Tenant not found

---

### Tenant Management

> **Permission:** Global admin for list, create, update, delete. Authenticated users can read their own tenant.

#### GET /api/v1/tenants

List all tenants (paginated). Global admin only.

**Response:** `200 OK` - [EnumerationResult](#enumerationresult)\<[TenantMetadata](#tenantmetadata)\>

---

#### POST /api/v1/tenants

Create a new tenant. Global admin only.

**Request Body:** [TenantMetadata](#tenantmetadata)

```json
{
  "Name": "Acme Corp"
}
```

**Response:** `201 Created` - [TenantMetadata](#tenantmetadata)

---

#### GET /api/v1/tenants/{id}

Get a tenant by ID. Non-admin users can only read their own tenant.

**Response:** `200 OK` - [TenantMetadata](#tenantmetadata)

---

#### PUT /api/v1/tenants/{id}

Update a tenant. Global admin only.

`Id`, `CreatedUtc`, `LastUpdateUtc`, and `IsProtected` are preserved server-side.

**Request Body:** [TenantMetadata](#tenantmetadata)

**Response:** `200 OK` - [TenantMetadata](#tenantmetadata)

---

#### DELETE /api/v1/tenants/{id}

Delete a tenant. Global admin only.

If the tenant is protected, the server returns `403 Forbidden`.

Deleting an unprotected tenant cascades through all tenant-scoped subordinate resources, including protected users and credentials seeded for that tenant.

The delete flow is ownership-aware: direct delete of a protected tenant, user, or credential returns `403`, but protected child auth records can still be removed as part of an allowed parent delete.

**Response:** `200 OK`

---

### User Management

> **Permission:** Global admins can manage users across the system. Tenant admins can manage users within their own tenant. Regular users can read and update only their own user record.

#### GET /api/v1/users

List users (paginated). Global admins can list all users. Tenant admins can list users in their own tenant.

**Response:** `200 OK` - [EnumerationResult](#enumerationresult)\<[UserMaster](#usermaster)\>

Password fields are redacted in responses.

---

#### POST /api/v1/users

Create a new user. Global admins can create users in any tenant. Tenant admins can create users only in their own tenant and cannot grant global admin.

`IsProtected` is server-controlled and ignored if supplied by the client.
`Password` is plaintext in the request body and is hashed server-side before persistence. `PasswordSha256` is accepted only for backward compatibility.

**Request Body:** user upsert payload

```json
{
  "TenantId": "default",
  "Email": "newuser@example.com",
  "Password": "securepassword",
  "FirstName": "Jane",
  "LastName": "Doe",
  "IsAdmin": false,
  "IsTenantAdmin": false
}
```

**Response:** `201 Created` - [UserMaster](#usermaster) (password redacted)

---

#### GET /api/v1/users/{id}

Get a user by ID. Global admins can read any user. Tenant admins can read users in their own tenant. Regular users can read only their own user record.

**Response:** `200 OK` - [UserMaster](#usermaster) (password redacted)

---

#### PUT /api/v1/users/{id}

Update a user. Global admins can update any user. Tenant admins can update users in their own tenant. Regular users can update only their own user record.

`Id`, `TenantId`, `CreatedUtc`, `LastUpdateUtc`, and `IsProtected` are server-controlled and cannot be modified by API clients.
If `Password` is supplied, the server hashes and stores the new password. If `Password` is omitted or empty, the current password is preserved.

**Request Body:** user upsert payload

```json
{
  "Email": "updated@example.com",
  "Password": "newpassword",
  "FirstName": "Jane",
  "LastName": "Smith",
  "IsAdmin": false,
  "IsTenantAdmin": false,
  "Active": true
}
```

**Response:** `200 OK` - [UserMaster](#usermaster) (password redacted)

---

#### DELETE /api/v1/users/{id}

Delete a user. Global admins can delete any unprotected user. Tenant admins can delete unprotected users in their own tenant. Regular users cannot delete users directly.

If the user is protected, the server returns `403 Forbidden`.

Deleting an unprotected user cascades through that user's subordinate resources inside the tenant.

**Response:** `200 OK`

---

### Credential Management

> **Permission:** Authenticated. Global admins can manage all credentials. Tenant admins can manage credentials in their own tenant. Regular users can list, read, create, update, and delete only their own credentials.

#### GET /api/v1/credentials

List credentials (paginated). Global admin: all credentials. Tenant admin: credentials in own tenant. Regular user: own credentials only.

**Response:** `200 OK` - [EnumerationResult](#enumerationresult)\<[Credential](#credential)\>

---

#### POST /api/v1/credentials

Create a new credential (bearer token). A bearer token is auto-generated if not provided. Admin: can create for any tenant/user. Non-admin: can create for self only.

`IsProtected` is server-controlled and ignored if supplied by the client.

**Request Body:** [Credential](#credential)

```json
{
  "TenantId": "default",
  "UserId": "default",
  "Name": "My API Token"
}
```

**Response:** `201 Created` - [Credential](#credential)

---

#### GET /api/v1/credentials/{id}

Get a credential by ID. Non-admin users can only read their own credentials.

**Response:** `200 OK` - [Credential](#credential)

---

#### PUT /api/v1/credentials/{id}

Update a credential. Global admins can update any credential. Tenant admins can update credentials inside their tenant. Regular users can update only their own credentials.

`Id`, `TenantId`, `UserId`, `CreatedUtc`, `LastUpdateUtc`, and `IsProtected` are server-controlled and cannot be modified by API clients.

**Request Body:** [Credential](#credential)

**Response:** `200 OK` - [Credential](#credential)

---

#### DELETE /api/v1/credentials/{id}

Delete a credential. Global admin: any. Tenant admin: credentials in own tenant. Regular user: own credentials only.

If the credential is protected, the server returns `403 Forbidden`.

**Response:** `200 OK`

---

### Harbor Runner Enrollment

> **Registered only when `Harbor.Enabled` is true.** Harbor is disabled by default; while disabled these routes
> do not exist. **Permission:** Tenant admin. A global administrator may change any runner. A tenant
> administrator may change only a runner whose owner, and previous owner for a revoked runner, is in the same
> tenant and is not a global administrator. The runner link protocol is described in `docs/HARBOR_PROTOCOL.md`.

#### POST /api/v1/harbor-runners/enrollments

Bind a runner identifier to the principal behind an existing active credential. Tenant, user and
authentication method are read from the durable credential, user and tenant records; the request cannot supply
them. An active runner cannot be rebound; revoke it first.

**Request Body:**

```json
{
  "runnerId": "hbr_build_host",
  "credentialId": "crd_..."
}
```

**Response:** `200 OK` - enrollment with `RunnerId`, `TenantId`, `UserId`, `AuthMethod`, `CredentialId`,
`Generation`, `Active`, `CreatedUtc`, `LastUpdateUtc`. No token value is returned or stored.

**Errors:** `400` missing fields or malformed body; `401` unauthenticated; `403` not authorized for the owner
or credential inactive; `409` runner already enrolled or a concurrent change won.

---

#### POST /api/v1/harbor-runners/enrollments/{runnerId}/revoke

Revoke an active enrollment with a generation compare-and-set. Connected links for the runner fail their next
revalidation and are closed; their jobs become `Lost`. Re-enrollment creates a new generation and cannot revive
earlier sessions or jobs.

**Response:** `200 OK` - `{ "Revoked": true }`; `404` when no active enrollment exists; `403` when the caller
lacks authority over the owner.

---

### Harbor Jobs

> **Registered only when `Harbor.Enabled` is true.** Every route applies the runner authority rule that
> enrollment uses: a caller sees a job when it is the runner owner or has authority over the owner. A job the
> caller may not see reads as `404`. Stopping needs a tenant administrator, as enrollment does.

A job record carries `JobId`, `RunnerId`, `LaunchKey`, `TenantId` and `UserId` (the runner's enrolled owner),
`EnrollmentGeneration`, `SessionGeneration`, `State` (`Pending`, `Running`, `Stopping`, `Exited`, `Failed`,
`Lost`), `ProcessId` (host process on the runner), `ExitCode`, `FailureReason`, `NextOutputSequence`,
`MissionId`, `CaptainId`, `Revision`, `CreatedUtc`, `LastUpdateUtc` and `CompletedUtc`. Records are durable;
a job that was not terminal when the Admiral started is `Lost` with `harbor_admiral_restarted`.

#### GET /api/v1/harbor-runners/jobs

List visible jobs, newest first. Query: `runnerId`, `activeOnly` (`true` for jobs that are not terminal),
`limit` (1-1000, default 100).

**Response:** `200 OK` - `{ "Jobs": [ ... ] }`. **Errors:** `401` unauthenticated.

#### GET /api/v1/harbor-runners/jobs/{jobId}

Read one visible job. A job held by this Admiral process includes output progress not yet persisted.

**Response:** `200 OK` - job record; `404` `harbor_job_unknown` when unknown or not visible.

#### POST /api/v1/harbor-runners/jobs/{jobId}/stop

Send a stop to the job's runner and connection. When the runner is disconnected the Admiral releases the job:
it becomes `Lost` with `harbor_job_released_runner_unavailable` and the response carries that reason.

**Response:** `200 OK` - `{ "Stopped": true, "Reason": "" }`. **Errors:** `401` unauthenticated; `403` below
tenant administrator or `harbor_command_unauthorized`; `404` `harbor_job_unknown`; `409` with the stable reason
(`harbor_job_not_running`, `harbor_job_not_held`, `harbor_job_not_bound`, a revalidation refusal, or
`harbor_stop_send_failed`).

---

### Status

#### GET /api/v1/status

Returns aggregate status including captain counts, mission breakdown, active voyages, and recent signals.

**Requires a global administrator.** The status aggregates every tenant, so it
follows the same rule as the WebSocket `status.snapshot`: an anonymous caller
receives `401`, and a tenant user or tenant administrator receives `403`. A
narrower caller reads its own records through the scoped list routes. The
unauthenticated health check is [`GET /api/v1/status/health`](#get-apiv1statushealth).

Clients report a `403` as a role boundary, not a failure. The dashboard home
page names it once and stops requesting the route, `armada status` and
`armada watch` print the refusal and exit 1, and the SDK `GetStatusAsync`
throws an `HttpRequestException` with status `403` and a message that names the
required role.

**Response:** `200 OK` - [ArmadaStatus](#armadastatus); `401 Unauthorized`; `403 Forbidden`

```json
{
  "TotalCaptains": 5,
  "IdleCaptains": 2,
  "WorkingCaptains": 3,
  "StalledCaptains": 0,
  "ActiveVoyages": 1,
  "MissionsWaitingForResourcePressure": 0,
  "MissionsByStatus": {
    "Pending": 3,
    "InProgress": 2,
    "Complete": 10
  },
  "Voyages": [],
  "RecentSignals": [],
  "RemoteTunnel": {
    "Enabled": false,
    "State": "Disabled",
    "TunnelUrl": null,
    "InstanceId": "armada-1f2e3d4c5b6a",
    "LastError": null,
    "ReconnectAttempts": 0,
    "LatencyMs": null,
    "CapabilityManifest": {
      "ProtocolVersion": "2026-04-03",
      "ArmadaVersion": "0.9.0",
      "Features": [
        "remoteControl.handshake",
        "remoteControl.heartbeat",
        "status.health",
        "status.snapshot",
        "settings.remoteControl"
      ]
    }
  },
  "TimestampUtc": "2026-03-07T12:00:00Z"
}
```

---

#### GET /api/v1/status/health

Health check endpoint. **Does not require authentication.**

**Response:** `200 OK`

```json
{
  "Status": "healthy",
  "Timestamp": "2026-03-07T12:00:00Z",
  "StartUtc": "2026-03-07T08:00:00Z",
  "Uptime": "0.04:00:00",
  "Version": "0.9.0",
  "Ports": {
    "Admiral": 7890,
    "Mcp": 7891
  },
  "RemoteTunnel": {
    "Enabled": false,
    "State": "Disabled",
    "TunnelUrl": null,
    "InstanceId": "armada-1f2e3d4c5b6a",
    "LastError": null,
    "ReconnectAttempts": 0,
    "LatencyMs": null
  }
}
```

---

#### GET /api/v1/settings

Returns current server settings including ports, agent configuration, system paths, and remote-control tunnel configuration.

**Response:** `200 OK`

```json
{
  "AdmiralPort": 7890,
  "McpPort": 7891,
  "MaxCaptains": 0,
  "HeartbeatIntervalSeconds": 30,
  "StallThresholdMinutes": 10,
  "IdleCaptainTimeoutSeconds": 0,
  "AutoCreatePr": false,
  "DataDirectory": "C:\\Users\\joelc\\.armada",
  "DatabasePath": "C:\\Users\\joelc\\.armada\\armada.db",
  "LogDirectory": "C:\\Users\\joelc\\.armada\\logs",
  "DocksDirectory": "C:\\Users\\joelc\\.armada\\docks",
  "ReposDirectory": "C:\\Users\\joelc\\.armada\\repos",
  "RemoteControl": {
    "Enabled": false,
    "TunnelUrl": null,
    "InstanceId": null,
    "EnrollmentToken": null,
    "ConnectTimeoutSeconds": 15,
    "HeartbeatIntervalSeconds": 30,
    "ReconnectBaseDelaySeconds": 5,
    "ReconnectMaxDelaySeconds": 60,
    "AllowInvalidCertificates": false
  }
}
```

---

#### PUT /api/v1/settings

Accepts partial updates to editable server settings. When `RemoteControl` is supplied, it replaces the full `RemoteControl` settings object.

**Request Body:** partial settings object

```json
{
  "RemoteControl": {
    "Enabled": true,
    "TunnelUrl": "wss://proxy.example.com/tunnel",
    "InstanceId": null,
    "EnrollmentToken": "bootstrap-token",
    "ConnectTimeoutSeconds": 15,
    "HeartbeatIntervalSeconds": 30,
    "ReconnectBaseDelaySeconds": 5,
    "ReconnectMaxDelaySeconds": 60,
    "AllowInvalidCertificates": false
  }
}
```

**Response:** `200 OK`

Returns the updated settings payload in the same shape as `GET /api/v1/settings`.

A supplied `ModelTier.UsageRouting` is validated before anything is applied: the policy shape and ranges, each
account's key file path against the Admiral's account folder root, and each account's runtime against the captains
it lists. A failing policy returns `400 Bad Request` and changes nothing. The manual reload below and the
settings-file watcher apply the same validation to the settings file.

---

#### POST /api/v1/settings/reload

Re-reads the settings file this server is bound to (the file it loaded at startup and saves to) and applies the
runtime-tunable values in place, without a restart. The settings-file watcher uses the same reload path, so an edit
picked up by the watcher and a manual reload read the same file and accept the same content. The file is validated
as a candidate first, with the same checks as `PUT /api/v1/settings`. Ports, paths, database, API key, agent
definitions and remote-control settings are not reloaded and still require a restart. A section that a running
service holds is updated in place: the crash-loop tracker reads the reloaded `crashLoopDetection` window and
threshold, and the definition-of-done gate reads the reloaded `definitionOfDone` values, including `enabled`, on its
next evaluation. Of `codeIndex`, only `stalenessSweepIntervalCycles` reloads; the rest of that section needs a
restart.

**Permission:** AdminOnly

**Response:** `200 OK` with the settings payload in the same shape as `GET /api/v1/settings`.

**Errors:** `404 Not Found` when the bound settings file does not exist, and `400 Bad Request` when it cannot be
read, is not valid settings JSON, or fails validation. In both cases the current settings are kept.

---

#### POST /api/v1/server/stop

Initiates a graceful shutdown of the Admiral server.

**Permission:** NoAuthRequired by default. When `RequireAuthForShutdown` is `true`, requires global admin (`IsAdmin = true`).

**Response:** `200 OK`

```json
{
  "Status": "shutting_down"
}
```

Helm's `server stop`, `server restart`, `reset` and `config init` all stop the Admiral through this route with the configured bearer credential (`ApiKey` in the Helm settings file). Helm treats the server as stopped only when a connection to `GET /api/v1/status/health` fails. A refused stop request (for example `401` or `403` when `RequireAuthForShutdown` is `true`) or a server that still answers after the wait makes `server stop` exit non-zero, cancels `server restart` before it starts a second instance, and makes `reset` and `config init` refuse to delete any data.

---

#### POST /api/v1/server/restart

Gracefully stops the Admiral server. In production the container or process supervisor restart policy relaunches the Admiral once the process exits, so this acts as an in-place restart with a brief period of downtime. No child process is spawned.

**Permission:** NoAuthRequired by default. When `RequireAuthForShutdown` is `true`, requires global admin (`IsAdmin = true`).

**Response:** `200 OK`

```json
{
  "Status": "restarting"
}
```

---

### Fleets

A fleet is a named collection of repositories (vessels) under management.

#### GET /api/v1/fleets

List all fleets with pagination.

**Query Parameters:** [Pagination parameters](#pagination-parameters)

**Response:** `200 OK` - [EnumerationResult](#enumerationresultt)\<[Fleet](#fleet)\>

```bash
curl http://localhost:7890/api/v1/fleets?pageSize=10
```

---

#### POST /api/v1/fleets/enumerate

Paginated enumeration of fleets with optional filtering and sorting.

**Request Body:** [EnumerationQuery](#enumerationquery) (optional)

**Response:** `200 OK` - [EnumerationResult](#enumerationresultt)\<[Fleet](#fleet)\>

```bash
curl -X POST http://localhost:7890/api/v1/fleets/enumerate \
  -H "Content-Type: application/json" \
  -d '{"PageSize": 10, "Order": "CreatedAscending"}'
```

---

#### POST /api/v1/fleets

Create a new fleet.

**Request Body:** [Fleet](#fleet)

| Field | Type | Required | Description |
|---|---|---|---|
| `Name` | string | yes | Fleet name |
| `Description` | string | no | Fleet description |

**Response:** `201 Created` - [Fleet](#fleet)

```bash
curl -X POST http://localhost:7890/api/v1/fleets \
  -H "Content-Type: application/json" \
  -d '{"Name": "Production Fleet", "Description": "Production repositories"}'
```

---

#### GET /api/v1/fleets/{id}

Get a single fleet by ID, including all its vessels.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `id` | Fleet ID (`flt_` prefix) |

**Response:** `200 OK` - `{ Fleet: Fleet, Vessels: Vessel[] }`
**Error:** `404` - Fleet not found

```bash
curl http://localhost:7890/api/v1/fleets/flt_abc123
```

---

#### PUT /api/v1/fleets/{id}

Update an existing fleet.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `id` | Fleet ID (`flt_` prefix) |

**Request Body:** [Fleet](#fleet) (fields to update)

The body replaces client-editable fields. `TenantId`, `UserId` and `CreatedUtc` always keep their stored values. `Active` and `DefaultPlaybooks` keep their stored values unless the body names them, so a body with only `Name` and `Description` does not reactivate a fleet or clear its default playbooks.

**Response:** `200 OK` - [Fleet](#fleet)
**Error:** `404` - Fleet not found

```bash
curl -X PUT http://localhost:7890/api/v1/fleets/flt_abc123 \
  -H "Content-Type: application/json" \
  -d '{"Name": "Renamed Fleet"}'
```

---

#### DELETE /api/v1/fleets/{id}

Delete a fleet. Vessels in the fleet are not deleted; their `FleetId` is set to null.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `id` | Fleet ID (`flt_` prefix) |

**Response:** `204 No Content`

```bash
curl -X DELETE http://localhost:7890/api/v1/fleets/flt_abc123
```

---

#### `POST /api/v1/fleets/delete/multiple`

Batch delete multiple fleets from the database by ID. Returns a summary of deleted and skipped entries. **This cannot be undone.**

**Request Body:**

```json
{
  "Ids": ["flt_abc123", "flt_def456"]
}
```

**Response:** `200 OK`

```json
{
  "Status": "deleted",
  "Deleted": 2,
  "Skipped": []
}
```

Skipped entries include the entity ID and the reason (e.g., "Not found" or "Empty ID").

---

### Vessels

A vessel is a git repository registered with Armada.

#### GET /api/v1/vessels

List all vessels with pagination.

**Query Parameters:** [Pagination parameters](#pagination-parameters), plus:

| Parameter | Type | Description |
|---|---|---|
| `fleetId` | string | Filter by fleet ID |

**Response:** `200 OK` - [EnumerationResult](#enumerationresultt)\<[Vessel](#vessel)\>

```bash
curl http://localhost:7890/api/v1/vessels?fleetId=flt_abc123
```

---

#### POST /api/v1/vessels/enumerate

Paginated enumeration of vessels with optional filtering and sorting.

**Request Body:** [EnumerationQuery](#enumerationquery) (optional)

**Response:** `200 OK` - [EnumerationResult](#enumerationresultt)\<[Vessel](#vessel)\>

```bash
curl -X POST http://localhost:7890/api/v1/vessels/enumerate \
  -H "Content-Type: application/json" \
  -d '{"FleetId": "flt_abc123", "PageSize": 50}'
```

---

#### POST /api/v1/vessels

Register a new vessel (git repository).

**Request Body:** [Vessel](#vessel)

| Field | Type | Required | Description |
|---|---|---|---|
| `Name` | string | yes | Vessel name. One path segment: no `/` or `\`, no `..`, no `:`, no control characters, and no leading `.` |
| `RepoUrl` | string | yes | Remote repository URL |
| `FleetId` | string | no | Fleet to assign to |
| `DefaultBranch` | string | no | Default branch name (default: `"main"`) |
| `LocalPath` | string | no | Managed bare repository path on the server. Global admin only |
| `WorkingDirectory` | string | no | Working checkout path on the server. Global admin only |

`LocalPath` and `WorkingDirectory` name places on the server's own disk: branch listing, git status, workspace file
access, check runs and vessel removal act on them, and removal deletes `LocalPath`. A `RepoUrl` that is a local path,
a `file:` URL or a `<transport>::<address>` helper makes the server read its own disk. Only a global administrator may
set any of these. Any other caller that sends a non-empty `LocalPath` or `WorkingDirectory`, or such a `RepoUrl`,
receives `403`. A name that is not one safe path segment receives `400` for every caller, because the managed
repository and dock directories are built from it.

**Response:** `201 Created` - [Vessel](#vessel)

```bash
curl -X POST http://localhost:7890/api/v1/vessels \
  -H "Content-Type: application/json" \
  -d '{"Name": "MyRepo", "RepoUrl": "https://github.com/org/repo.git", "FleetId": "flt_abc123"}'
```

---

#### GET /api/v1/vessels/{id}

Get a single vessel by ID.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `id` | Vessel ID (`vsl_` prefix) |

**Response:** `200 OK` - [Vessel](#vessel)
**Error:** `404` - Vessel not found

---

#### PUT /api/v1/vessels/{id}

Update an existing vessel.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `id` | Vessel ID (`vsl_` prefix) |

**Request Body:** [Vessel](#vessel)

The body replaces every client-editable field. A field the body omits is stored as its default, so send the whole
vessel as read with your changes applied. To change only the context fields, use
`PATCH /api/v1/vessels/{id}/context`.

The server keeps `TenantId`, `UserId`, `CreatedUtc` and `AutoLandCalibrationLandedCount` from the stored vessel; body
values for them are ignored. For a caller that is not a global administrator it also keeps the stored `LocalPath` and
`WorkingDirectory`, and it refuses a changed `RepoUrl` that is a server path with `403`. A new name must be one safe
path segment (`400` otherwise).

`autoLandPredicate` must be a JSON object (or `null` to clear it). The key matches in any letter case. A GET returns
the stored predicate as a JSON string, so parse it before sending it back.

**Response:** `200 OK` - [Vessel](#vessel)
**Error:** `400` - `autoLandPredicate` is not a valid predicate object
**Error:** `404` - Vessel not found

---

#### DELETE /api/v1/vessels/{id}

Delete a vessel.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `id` | Vessel ID (`vsl_` prefix) |

**Response:** `204 No Content`

---

#### `POST /api/v1/vessels/delete/multiple`

Batch delete multiple vessels from the database by ID. Returns a summary of deleted and skipped entries. **This cannot be undone.**

**Request Body:**

```json
{
  "Ids": ["vsl_abc123", "vsl_def456"]
}
```

**Response:** `200 OK`

```json
{
  "Status": "deleted",
  "Deleted": 2,
  "Skipped": []
}
```

Skipped entries include the entity ID and the reason (e.g., "Not found" or "Empty ID").

---

#### PATCH /api/v1/vessels/{id}/context

Update only the `ProjectContext`, `StyleGuide`, and `ModelContext` fields of a vessel.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `id` | Vessel ID (`vsl_` prefix) |

**Request Body:**

| Field | Type | Required | Description |
|---|---|---|---|
| `ProjectContext` | string | no | Project context describing architecture, key files, and dependencies |
| `StyleGuide` | string | no | Style guide describing naming conventions, patterns, and library preferences |
| `ModelContext` | string | no | Agent-accumulated context about this repository |

```bash
curl -X PATCH http://localhost:7890/api/v1/vessels/vsl_abc123/context \
  -H "Content-Type: application/json" \
  -d '{"ProjectContext": "C# .NET 8 project with SQLite", "StyleGuide": "Use PascalCase for public members"}'
```

**Response:** `200 OK` - [Vessel](#vessel)
**Error:** `404` - Vessel not found

---

#### GET /api/v1/vessels/{id}/branches

Lists local branches of the vessel repository (`LocalPath`, then
`WorkingDirectory`) with tip metadata and ahead/behind counts against the
default branch. It never fetches or changes refs. A missing repository or a git
failure returns `200` with an `Error` field.

`WriteControls` states which write actions the caller may request:
`MergeAvailable`, `PushAvailable`, the `Remote` a push must name (`origin`), and
a reason code for each unavailable action (`administrator_required`,
`repository_missing`, `remote_missing`, `remote_mismatch`, `unavailable`).
Clients must not offer an action the server reports as unavailable.

**Response:** `200 OK` - branch listing
**Error:** `404` - Vessel not found

---

#### POST /api/v1/vessels/{id}/branches/push

Pushes one branch of the vessel landing repository (`LocalPath`) to the
vessel's `origin`. Requires a global administrator or a tenant administrator of
the vessel's tenant; other users receive `403`, and a vessel outside the
caller's tenant is `404`.

**Request Body:**

| Field | Type | Required | Description |
|---|---|---|---|
| `SourceRef` | string | yes | Local branch name in the landing repository |
| `TargetRef` | string | yes | Remote branch name |
| `Remote` | string | yes | Must be `origin` |

The server validates both names with `git check-ref-format` (short branch
names only), requires the origin URL and any push URL to match the vessel
`RepoUrl`, and refuses while the working checkout is dirty or detached. It
checks that the current remote tip is an ancestor of the source, pushes without
force (never a delete), and verifies the remote tip afterwards. A source that is
the branch of a mission that is not `Complete`, or that has an active
merge-queue entry, is refused unless the push publishes the branch under its
own name. Targets matching protected-branch policy, and release branches when
the vessel requires the merge queue for them, are refused.

**Response:** `200 OK` - `BranchWriteResult` with `SourceCommit`,
`PreviousTargetCommit` and the verified `TargetCommit`

**Refusals** return a `BranchWriteResult` with `Succeeded: false`, a `Reason`
code and a `Message`, and change no refs:

| Status | Reasons |
|---|---|
| `400` | `invalid_request`, `invalid_ref`, `remote_not_allowed` |
| `409` | `repository_missing`, `working_checkout_missing`, `working_checkout_dirty`, `working_checkout_detached`, `source_missing`, `protected_target`, `release_requires_merge_queue`, `mission_branch_not_landed`, `merge_queue_entry_active`, `vessel_busy`, `remote_missing`, `remote_mismatch`, `remote_unreadable`, `non_fast_forward`, `nothing_to_write`, `push_rejected` |
| `500` | `verification_failed`, `git_failed` |
| `503` | `unavailable` |

```bash
curl -X POST http://localhost:7890/api/v1/vessels/vsl_abc123/branches/push \
  -H "Content-Type: application/json" \
  -d '{"SourceRef": "feature/work", "TargetRef": "main", "Remote": "origin"}'
```

---

#### POST /api/v1/vessels/{id}/branches/merge

Merges one landing-repository branch into another. The merge never pushes and
never rewrites the target: `FastForward` requires the target to be an ancestor
of the source; `MergeCommit` creates an explicit two-parent merge commit and is
refused on content conflicts. The target advances by compare-and-swap, and the
server verifies that the new tip contains both the previous target and the
source. Authorization, ref validation, working-checkout, mission, merge-queue
and branch-policy gates match the push route. A target checked out in any
worktree of the landing repository is refused (`target_checked_out`).

**Request Body:**

| Field | Type | Required | Description |
|---|---|---|---|
| `SourceRef` | string | yes | Source branch |
| `TargetRef` | string | yes | Target branch; must already exist |
| `Strategy` | string | yes | `FastForward` or `MergeCommit` |

After a verified merge, a working checkout that is on the target branch is
fast-forwarded from the landing repository. `WorkingCheckoutSync` reports the
outcome: `fast_forwarded`, `skipped_on_other_branch`, `failed_fetch`,
`failed_not_fast_forward`, `not_configured`, `same_as_landing_repository` or
`skipped_bare`. The merge itself is not undone when the checkout cannot follow.

Additional refusal reasons: `invalid_strategy`, `same_ref` (`400`);
`target_missing`, `target_checked_out`, `merge_conflict`, `target_moved`
(`409`).

**Response:** `200 OK` - `BranchWriteResult`

---

#### GET /api/v1/vessels/{id}/readiness

Returns readiness warnings and blocking issues for a vessel. Optional query:
`workflowProfileId`, `checkType`, `environmentName`, `includeWorkflowRequirements`.

**Path Parameters:**

| Parameter | Description |
|---|---|
| `id` | Vessel ID (`vsl_` prefix) |

**Response:** `200 OK` - vessel readiness summary
**Error:** `400` - Invalid `checkType`
**Error:** `404` - Vessel not found

---

#### GET /api/v1/vessels/{id}/landing-preview

Predicts how Armada would land a branch for this vessel. Optional query:
`sourceBranch`. The response includes current `Configuration` with its source,
effective mode, legacy flags and cleanup policy. See the
[landing configuration contract](reference/backend-landing.md) for resolution
and the advisory Check-summary limits.

`hasPassingChecks` is true when any scoped check run passed, including an older
run. A `latest_check_not_passed` warning names a newer run that did not pass.
`isReadyToLand` means the preview found no error issue; it is not the Check gate
for the landed commit.

**Path Parameters:**

| Parameter | Description |
|---|---|
| `id` | Vessel ID (`vsl_` prefix) |

**Response:** `200 OK` - landing preview
**Error:** `404` - Vessel not found

---

### Voyages

A voyage is a batch of related missions tracked together.

#### GET /api/v1/voyages

List all voyages with pagination.

**Query Parameters:** [Pagination parameters](#pagination-parameters), plus:

| Parameter | Type | Description |
|---|---|---|
| `status` | string | Filter by voyage status (`Open`, `InProgress`, `Complete`, `Cancelled`) |

**Response:** `200 OK` - [EnumerationResult](#enumerationresultt)\<[Voyage](#voyage)\>

```bash
curl http://localhost:7890/api/v1/voyages?status=InProgress
```

---

#### POST /api/v1/voyages/enumerate

Paginated enumeration of voyages with optional filtering and sorting.

**Request Body:** [EnumerationQuery](#enumerationquery) (optional)

**Response:** `200 OK` - [EnumerationResult](#enumerationresultt)\<[Voyage](#voyage)\>

---

#### POST /api/v1/voyages

Create a new voyage with optional missions. Missions are automatically dispatched to the target vessel.

**Request Body:** [VoyageRequest](#voyagerequest)

| Field | Type | Required | Description |
|---|---|---|---|
| `Title` | string | yes | Voyage title |
| `Description` | string | no | Voyage description |
| `VesselId` | string | yes | Target vessel ID |
| `Missions` | array | no | List of [MissionRequest](#missionrequest) objects |
| `SelectedPlaybooks` | array | no | Ordered [SelectedPlaybook](#selectedplaybook) rows for all missions. Merge hierarchy: vessel defaults < voyage `SelectedPlaybooks` < per-mission `SelectedPlaybooks`. A duplicate `PlaybookId` is rendered once; most-specific `DeliveryMode` wins. |
| `PipelineId` | string | no | Pipeline ID to use for this voyage (overrides vessel/fleet default) |
| `Pipeline` | string | no | Pipeline name to use for this voyage (alternative to `PipelineId`) |
| `SkipStages` | array | no | Persona names of pipeline stages the operator confirms this voyage does not need (for example `["TestEngineer"]`). The stages are dropped when the voyage is materialised, the remaining stages chain across the gap, and one `voyage.stage_skipped` event is recorded per stage. Naming the Judge returns `400` with `stage_skip_judge_refused`; a name that is not a stage of the effective pipeline returns `400` with `stage_skip_unknown_persona`. |
| `SkipStagesReason` | string | no | Reason recorded on each `voyage.stage_skipped` event. |

**Response:** `201 Created` - [Voyage](#voyage)

```bash
curl -X POST http://localhost:7890/api/v1/voyages \
  -H "Content-Type: application/json" \
  -d '{
    "Title": "API Hardening",
    "Description": "Security improvements",
    "VesselId": "vsl_abc123",
    "SelectedPlaybooks": [
      {"PlaybookId": "pbk_abc123", "DeliveryMode": "InlineFullContent"}
    ],
    "Missions": [
      {"Title": "Add rate limiting", "Description": "Add rate limiting middleware"},
      {"Title": "Add input validation", "Description": "Validate all POST endpoints"}
    ]
  }'
```

---

#### GET /api/v1/voyages/{id}

Get a voyage and all its associated missions.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `id` | Voyage ID (`vyg_` prefix) |

**Response:** `200 OK` - [VoyageDetail](#voyagedetail)

```json
{
  "Voyage": { ... },
  "Missions": [ ... ]
}
```

**Error:** `404` - Voyage not found

---

#### GET /api/v1/voyages/{id}/mission-summary

Read mission status counts and a page of distinct vessel IDs without mission
descriptions or captured output. The caller must be able to view the voyage.
Counts and associations use global scope for a global administrator, tenant
scope for a tenant administrator, and user scope for other authenticated users.
Caller-supplied tenant or user query parameters do not change this scope.

| Query parameter | Default | Allowed values |
| --- | --- | --- |
| `pageNumber` | 1 | Positive integer |
| `pageSize` | 100 | Integer from 1 to 100 |

The response is `VoyageMissionSummary`: `statusCounts` maps each present mission
status to its count across all visible missions; `vessels` is an
`EnumerationResult<string>` with distinct vessel IDs. Vessel pagination does not
limit status counts. A beyond-end page has no objects but retains totals.
Ordering follows database identifier collation. Concurrent writes can change
pages between requests.

Returns 400 for invalid paging, 401 without authentication, and 404 for a missing
or invisible voyage.

#### DELETE /api/v1/voyages/{id}

Cancel a voyage. Sets the voyage status to `Cancelled` and cancels every `Pending`, `Assigned`, `InProgress`, `Testing`, or `Review` mission in it, including a mission waiting for a review decision. The captain of each `Assigned` or `InProgress` mission, and of each `Testing` or `Review` mission it still holds, is recalled first, which stops its agent process; if a recall fails, the voyage stays active and the request fails. A voyage that is already `Cancelled` or `Complete` is returned unchanged with `CancelledMissions: 0`. The REST route, the WebSocket `cancel_voyage` command, the MCP `armada_cancel_voyage` tool, and the remote-control cancel all use this one operation.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `id` | Voyage ID (`vyg_` prefix) |

**Response:** `200 OK`

```json
{
  "Voyage": { "Id": "vyg_abc123", "Status": "Cancelled", "..." : "..." },
  "CancelledMissions": 3
}
```

**Error:** `404` - Voyage not found

---

#### DELETE /api/v1/voyages/{id}/purge

Permanently delete a voyage and all its associated missions from the database. **This cannot be undone.**

**Path Parameters:**
| Parameter | Description |
|---|---|
| `id` | Voyage ID (`vyg_` prefix) |

**Response:** `200 OK`

```json
{
  "Status": "deleted",
  "VoyageId": "vyg_abc123",
  "MissionsDeleted": 5
}
```

**Error:** `404` - Voyage not found

---

#### `POST /api/v1/voyages/delete/multiple`

Batch delete multiple voyages and their associated missions from the database by ID. Voyages that are Open/InProgress or have active missions are skipped. Returns a summary of deleted and skipped entries. **This cannot be undone.**

**Request Body:**

```json
{
  "Ids": ["vyg_abc123", "vyg_def456"]
}
```

**Response:** `200 OK`

```json
{
  "Status": "deleted",
  "Deleted": 2,
  "Skipped": []
}
```

Skipped entries include the entity ID and the reason (e.g., "Not found", "Cannot delete voyage while status is Open", or "Cannot delete voyage with N active mission(s)").

---

### Missions

A mission is an atomic unit of work assigned to a captain (AI agent).

#### GET /api/v1/missions

List all missions with pagination.

**Query Parameters:** [Pagination parameters](#pagination-parameters), plus:

| Parameter | Type | Description |
|---|---|---|
| `status` | string | Filter by mission status |
| `vesselId` | string | Filter by vessel ID |
| `captainId` | string | Filter by captain ID |
| `voyageId` | string | Filter by voyage ID |

**Response:** `200 OK` - [EnumerationResult](#enumerationresultt)\<[Mission](#mission)\>

> **Note:** The `DiffSnapshot` field is excluded from mission list responses to keep payloads compact. Use `GET /api/v1/missions/{id}/diff` to retrieve the full diff.

```bash
curl http://localhost:7890/api/v1/missions?status=InProgress&vesselId=vsl_abc123
```

---

#### POST /api/v1/missions/enumerate

Paginated enumeration of missions with optional filtering and sorting.

**Request Body:** [EnumerationQuery](#enumerationquery) (optional)

**Response:** `200 OK` - [EnumerationResult](#enumerationresultt)\<[Mission](#mission)\>

```bash
curl -X POST http://localhost:7890/api/v1/missions/enumerate \
  -H "Content-Type: application/json" \
  -d '{"Status": "InProgress", "VesselId": "vsl_abc123", "PageSize": 25}'
```

---

#### POST /api/v1/missions

Create and dispatch a new mission. If a `VesselId` is provided, the Admiral will assign a captain and set up a worktree.

**Request Body:** [Mission](#mission)

| Field | Type | Required | Description |
|---|---|---|---|
| `Title` | string | yes | Mission title |
| `Description` | string | no | Detailed instructions for the AI agent |
| `VesselId` | string | no | Target vessel (required for auto-dispatch) |
| `VoyageId` | string | no | Parent voyage ID |
| `Priority` | int | no | Priority (lower = higher priority, default: 100) |
| `SelectedPlaybooks` | array | no | Ordered [SelectedPlaybook](#selectedplaybook) rows for this standalone mission. Merges with vessel defaults (per-mission wins on collision). |

**Response:** `201 Created` - [Mission](#mission)

```bash
curl -X POST http://localhost:7890/api/v1/missions \
  -H "Content-Type: application/json" \
  -d '{"Title": "Fix login bug", "Description": "The login form does not validate email addresses", "VesselId": "vsl_abc123"}'
```

---

#### GET /api/v1/missions/{id}

Get a single mission by ID.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `id` | Mission ID (`msn_` prefix) |

**Response:** `200 OK` - [Mission](#mission)
**Error:** `404` - Mission not found

> **Note:** The `DiffSnapshot` field is excluded from responses to keep payloads compact. Use `GET /api/v1/missions/{id}/diff` to retrieve the full diff.

---

#### GET /api/v1/missions/{id}/output

Returns one page of the mission's authoritative safe output artifact. The
response includes the full UTF-8 SHA-256 digest, total character and byte
counts, and explicit finalization and completeness fields. Secret-shaped values
are redacted before persistence and again when legacy output is read.

Use the returned `NextOffset` until `HasMore` is `false`. Verify `Sha256` after
you join all pages. Do not accept the report when a page is missing,
`Complete` is `false`, or the digest does not match.

| Query parameter | Type | Default | Description |
|---|---|---|---|
| `offset` | integer | 0 | Zero-based UTF-16 character offset. |
| `length` | integer | 16000 | Requested character count, with a maximum of 64000. |

**Errors:** `400` for an invalid page range; `401` or `403` for an unauthorized
request; `404` when the mission is outside the caller's scope or does not exist.

---

#### PUT /api/v1/missions/{id}

Update mission fields (title, description, priority, etc.). Does not change status -- use the status transition endpoint for that.

Mission metadata updates preserve omitted `vesselId` and `voyageId`. Explicit changes, including clearing an existing binding with null, return 409.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `id` | Mission ID (`msn_` prefix) |

**Request Body:** [Mission](#mission) (fields to update)

**Response:** `200 OK` - [Mission](#mission)
**Error:** `404` - Mission not found

---

#### PUT /api/v1/missions/{id}/status

Transition a mission to a new status. Only valid transitions are allowed.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `id` | Mission ID (`msn_` prefix) |

**Request Body:** [StatusTransitionRequest](#statustransitionrequest)

| Field | Type | Required | Description |
|---|---|---|---|
| `Status` | string | yes | Target status name |

**Response:** `200 OK` - [Mission](#mission)
**Error:** `400` - Invalid transition or invalid status name
**Error:** `404` - Mission not found
**Error:** `409` - Manual completion blocked; `Message` is `Manual completion blocked: <reason>`

**Manual completion:** a transition to `Complete` must pass the shared manual
completion gates before anything is written. The same gates apply to the WebSocket
`transition_mission_status` command and the MCP `armada_transition_mission_status`
tool. A mission in `Review` completes only through review approval
(`manual_completion_review_required`), a terminal stage needs Judge authority,
and a live captain process must have released the mission. Participating failed,
pending or stale Checks block completion. With an active dock the mission lands
through the landing pipeline. An intermediate pipeline stage is handed off to its
downstream stage instead. With no active dock, an Implementation mission's commit
must already be an ancestor of the vessel default branch. Without a vessel or
commit the reason is `manual_completion_ancestry_unavailable`. Audit and Research
missions keep their report-only completion contract. A refused request leaves the
mission unchanged.

**Valid Status Transitions:**

| From | Allowed Targets |
|---|---|
| `Pending` | `Assigned`, `Cancelled` |
| `Assigned` | `InProgress`, `Cancelled` |
| `InProgress` | `WorkProduced`, `Testing`, `Review`, `Complete`, `Failed`, `Cancelled` |
| `WorkProduced` | `PullRequestOpen`, `Complete`, `LandingFailed`, `Cancelled` |
| `PullRequestOpen` | `Complete`, `LandingFailed`, `Cancelled` |
| `Testing` | `Review`, `InProgress`, `Complete`, `Failed`, `Cancelled` |
| `Review` | `Complete`, `InProgress`, `Failed`, `Cancelled` |
| `LandingFailed` | `WorkProduced`, `Failed`, `Cancelled` |
| `Complete` | (terminal) |
| `Failed` | (terminal) |
| `Cancelled` | (terminal) |

A captain can also move its own mission by writing an `[ARMADA:STATUS]` line to
its output. That marker only sets `InProgress`, `Testing`, or `Review`, and only
where the table above allows the move. A marker that names a post-work or
terminal status (`WorkProduced`, `PullRequestOpen`, `LandingFailed`,
`Complete`, `Failed`, `Cancelled`) is logged and ignored. Those statuses are
set by the completion and landing paths, which run their checks.

```bash
curl -X PUT http://localhost:7890/api/v1/missions/msn_abc123/status \
  -H "Content-Type: application/json" \
  -d '{"Status": "Assigned"}'
```

---

#### DELETE /api/v1/missions/{id}

Cancel a mission by setting its status to `Cancelled`. Returns the full updated mission.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `id` | Mission ID (`msn_` prefix) |

**Response:** `200 OK` - [Mission](#mission) (with `Status: "Cancelled"`)

**Error:** `404` - Mission not found

---

#### `POST /api/v1/missions/delete/multiple`

Batch delete multiple missions from the database by ID. Returns a summary of deleted and skipped entries. **This cannot be undone.**

**Request Body:**

```json
{
  "Ids": ["msn_abc123", "msn_def456"]
}
```

**Response:** `200 OK`

```json
{
  "Status": "deleted",
  "Deleted": 2,
  "Skipped": []
}
```

Skipped entries include the entity ID and the reason (e.g., "Not found" or "Empty ID").

---

#### POST /api/v1/missions/{id}/restart

Restart a failed or cancelled mission by resetting it to `Pending` for re-dispatch. Clears captain assignment, branch, PR URL, and timing fields. Optionally update the title and description (instructions) before restarting.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `id` | Mission ID (`msn_` prefix) |

**Request Body (optional):**
```json
{
  "Title": "Updated mission title",
  "Description": "Updated instructions for the captain"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `Title` | string | No | New mission title. Omit to keep the original. |
| `Description` | string | No | New mission description/instructions. Omit to keep the original. |

**Response:** `200 OK` - [Mission](#mission) (with `Status: "Pending"`)

**Errors:**
- `400` - Mission is not in `Failed` or `Cancelled` status
- `404` - Mission not found

---

#### GET /api/v1/missions/{id}/diff

Returns the git diff of changes made by a captain in the mission's worktree. Checks for a saved diff file first (captured at completion), then falls back to a live worktree diff.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `id` | Mission ID (`msn_` prefix) |

**Response:** `200 OK`

```json
{
  "MissionId": "msn_abc123",
  "Branch": "armada/msn_abc123",
  "Diff": "diff --git a/src/auth.ts b/src/auth.ts\n..."
}
```

**Error:** `404` - Mission not found or no diff available

---

#### GET /api/v1/missions/{id}/log

Returns the session log (captured stdout/stderr) for a mission. Log files are written to disk when a captain executes a mission. Supports pagination via query parameters.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `id` | Mission ID (`msn_` prefix) |

**Query Parameters:**
| Parameter | Type | Default | Description |
|---|---|---|---|
| `lines` | integer | 100 | Number of lines to return |
| `offset` | integer | 0 | Line offset (0-based, skip this many lines from start) |

**Response:** `200 OK`

```json
{
  "MissionId": "msn_abc123",
  "Log": "Starting mission...\nCloning repository...\n...",
  "Lines": 100,
  "TotalLines": 542
}
```

If the mission exists but has no log file yet, returns an empty log:

```json
{
  "MissionId": "msn_abc123",
  "Log": "",
  "Lines": 0,
  "TotalLines": 0
}
```

**Error:** `404` - Mission not found

```bash
# Get first 50 lines
curl http://localhost:8080/api/v1/missions/msn_abc123/log?lines=50 \
  -H "X-Api-Key: your-key"

# Get lines 100-200
curl http://localhost:8080/api/v1/missions/msn_abc123/log?offset=100&lines=100 \
  -H "X-Api-Key: your-key"
```

---

#### GET /api/v1/missions/{id}/landing-preview

Predicts how Armada would land this mission's branch. The mission must have a vessel.
The current `Configuration` includes the scoped voyage override. An unreadable
linked voyage returns null configuration and a preview error. See the
[landing configuration contract](reference/backend-landing.md).

**Path Parameters:**

| Parameter | Description |
|---|---|
| `id` | Mission ID (`msn_` prefix) |

**Response:** `200 OK` - landing preview
**Error:** `400` - Mission has no vessel
**Error:** `404` - Mission not found

---

#### GET /api/v1/missions/{id}/definition-of-done

Returns the current definition-of-done configuration for the mission and its
latest recorded gate evaluation in the caller's scope. Reading it runs no gate,
no command and no diff, and does not change landing readiness.
`HistoryState` is `Recorded`, `NotRecorded` (no evaluation in scope; not a pass
or failure) or `Unavailable` (the latest record cannot be read; an older record
is never shown instead). The configuration reports whether commands exist, not
their text. See the [definition-of-done history contract](reference/backend-dod.md).

**Path Parameters:**

| Parameter | Description |
|---|---|
| `id` | Mission ID (`msn_` prefix) |

**Response:** `200 OK` - `MissionDefinitionOfDoneReport`
**Error:** `404` - Mission not found

---

#### GET /api/v1/missions/{id}/auto-land

Returns auto-land detail for the mission in the caller's scope: the vessel's
current predicate, the latest recorded auto-land decision (Triggered or Skipped,
with the merge entry, redacted reason and the predicate recorded at decision
time), and the latest merge entry with its audit lane, convention, trigger and
deep-review fields. Reading it evaluates no predicate and reads no diff.
`DecisionState` is `Recorded`, `NotRecorded` or `Unavailable` (malformed or
same-timestamp latest decision; an older decision is never shown instead). See
the [auto-land detail contract](reference/backend-autoland.md).

**Path Parameters:**

| Parameter | Description |
|---|---|
| `id` | Mission ID (`msn_` prefix) |

**Response:** `200 OK` - `MissionAutoLandReport`
**Error:** `404` - Mission not found

---

#### GET /api/v1/missions/{id}/recovery

Returns recorded recovery detail for the mission in the caller's scope: rescue
attempts against the current budget, landing retries, the last recovery action,
rescue missions (vessel missions whose parent is this mission), linked incidents
with their runbook executions, and recovery events among the mission's 100 most
recent events. Reading it dispatches nothing and does not infer a landing from
mission status. See the [recovery detail contract](reference/backend-recovery.md).

**Path Parameters:**

| Parameter | Description |
|---|---|
| `id` | Mission ID (`msn_` prefix) |

**Response:** `200 OK` - `MissionRecoveryReport`
**Error:** `404` - Mission not found

---

### Captains

A captain is an AI agent instance (Claude Code, Codex, etc.) that executes missions.

#### GET /api/v1/captains

List all captains with pagination.

**Query Parameters:** [Pagination parameters](#pagination-parameters), plus:

| Parameter | Type | Description |
|---|---|---|
| `status` | string | Filter by captain state (`Idle`, `Working`, `Stalled`, `Stopping`) |

**Response:** `200 OK` - [EnumerationResult](#enumerationresultt)\<[Captain](#captain)\>

```bash
curl http://localhost:7890/api/v1/captains?status=Working
```

---

#### POST /api/v1/captains/enumerate

Paginated enumeration of captains with optional filtering and sorting.

**Request Body:** [EnumerationQuery](#enumerationquery) (optional)

**Response:** `200 OK` - [EnumerationResult](#enumerationresultt)\<[Captain](#captain)\>

---

#### POST /api/v1/captains

Register a new captain (AI agent).

**Request Body:** [Captain](#captain)

The request accepts configuration fields only:

| Field | Type | Required | Description |
|---|---|---|---|
| `Name` | string | yes | Captain name |
| `Runtime` | string | no | Agent runtime type (default: `ClaudeCode`) |
| `Model` | string | no | Optional model override for this captain. When omitted, the runtime selects its default model |
| `ModelEndpointId` | string | no | Inference model endpoint for an `ApiEndpoint` captain |
| `ApiKey` | string | no | Per-captain provider credential override |
| `ApiBaseUrl` | string | no | Per-captain provider base URL override |
| `SystemInstructions` | string | no | Instructions injected into every mission prompt |
| `AllowedPersonas` | string | no | JSON array of persona names the captain may fill; null means any |
| `PreferredPersona` | string | no | Preferred persona for dispatch routing |
| `RuntimeOptionsJson` | string | no | Runtime-specific options (Mux settings, reasoning effort) |
| `Tier` | string | no | Capability tier: `Economy`, `Standard`, or `Premium`. Null classifies it from the model name. It is the captain's routing tier; a mission's tier floor admits only captains at or above it |
| `PreferenceRank` | integer | no | Preference rank within the tier, -1000 to 1000, default 0. A higher rank is tried first |
| `DefaultPlaybooks` | string | no | JSON list of playbooks merged into every mission this captain runs |

All other captain fields are server-owned: `Id`, `TenantId`, `UserId`, `State`,
`CurrentMissionId`, `CurrentDockId`, `ProcessId`, `RecoveryAttempts`,
`LastHeartbeatUtc`, `LastProcessAliveUtc`, `QuarantineUntilUtc`,
`QuarantineReason`, `CreatedUtc` and `LastUpdateUtc`. A new captain always starts
`Idle`, unassigned and not quarantined, with the tenant and user of the caller. A
request that sends a server-owned field with a value other than its default is
refused with `400` and nothing is stored. The message starts with
`captain_server_owned_field:` and names every refused field. Change captain state
with `POST /api/v1/captains/{id}/stop`, `/quarantine` and `/unquarantine`.

A captain name is unique across the admiral. A create whose `Name` another captain
already has is refused with `409 Conflict`, error `Conflict` and the message
`A captain with that name already exists.`; nothing is stored.

**Response:** `201 Created` - [Captain](#captain)
**Error:** `400 Bad Request` - Invalid or unavailable model, or a server-owned field (`captain_server_owned_field`)
**Error:** `409 Conflict` - Another captain already has the requested name

```bash
curl -X POST http://localhost:7890/api/v1/captains \
  -H "Content-Type: application/json" \
  -d '{"Name": "captain-1", "Runtime": "ClaudeCode", "Model": "claude-sonnet-4-20250514", "SystemInstructions": "You are a testing specialist. Always run tests before committing."}'
```

---

#### GET /api/v1/captains/{id}

Get a single captain by ID.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `id` | Captain ID (`cpt_` prefix) |

**Response:** `200 OK` - [Captain](#captain)
**Error:** `404` - Captain not found

---

#### PUT /api/v1/captains/{id}

Replace a captain's configuration fields: the same fields that
[POST /api/v1/captains](#post-apiv1captains) accepts. A configuration field left out
of the body is cleared, except `RuntimeOptionsJson` on a Mux captain, which is kept.
Server-owned fields always keep their stored values, so an update never changes
state, assignment, process, heartbeat or quarantine. A body may repeat a stored
server-owned value (for example `Id`). A body that sends a server-owned field with
a different value returns `400` with `captain_server_owned_field:` naming the field,
and nothing is written.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `id` | Captain ID (`cpt_` prefix) |

**Request Body:**
```json
{
  "name": "captain-bravo",
  "runtime": "Codex",
  "model": "gpt-5.4",
  "systemInstructions": "Focus on code quality and always run linting before commits."
}
```

**Response:** `200 OK` - [Captain](#captain)
**Error:** `400 Bad Request` - Invalid or unavailable model

**Response:** `200 OK` - [Captain](#captain)
**Error:** `404` - Captain not found

```bash
curl -X PUT http://localhost:7890/api/v1/captains/cpt_abc123 \
  -H "Content-Type: application/json" \
  -H "x-api-key: YOUR_KEY" \
  -d '{"name": "captain-bravo", "runtime": "Codex"}'
```

---

#### POST /api/v1/captains/{id}/stop

Stop one captain. MCP `armada_stop_captain` and WebSocket `stop_captain` run the
same service and apply the same rule. A Planning captain is stopped through its
active planning session and a Refining captain through its active objective
refinement session; the response names the stopped session. Any other captain
has its agent process stopped and is recalled to Idle, which fails its active
mission. No shutdown request is sent: the agent process gets a 3-second grace
period to exit, then its process tree is killed. Only the process the admiral
launched is acted on: a live process whose start time differs from the recorded
launch holds a reused process ID and is left running.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `id` | Captain ID (`cpt_` prefix) |

**Response:** `200 OK` - `CaptainStopResult`

```json
{
  "Outcome": "Completed",
  "Status": "stopped",
  "CaptainId": "cpt_abc123",
  "PlanningSessionId": null,
  "ObjectiveRefinementSessionId": null,
  "Message": "Captain stopped"
}
```

**Error:** `404` - Captain not found; `409` - the captain is Planning or Refining
and its session could not be resolved for a coordinated stop; `500` - the agent
process could not be stopped, so the captain was not recalled.

---

#### POST /api/v1/captains/stop-all

Emergency stop of every working captain, active planning session and active
objective refinement session. Working captains are recalled to Idle; planning and
refinement sessions are stopped through their coordinators. Each stop is
attempted independently and a failure is counted and named, never hidden. MCP
`armada_stop_all` and WebSocket `stop_all` run the same service and return the
same result. It acts on every tenant, so it requires a global administrator; any
other caller receives `403`.

**Response:** `200 OK` - `CaptainStopAllResult`. `Status` is `all_stopped` when
every stop succeeded and `stopped_with_failures` otherwise. `UnavailableSources`
names a session kind the database provider does not store (`PlanningSessions` on
MySQL and SQL Server); no session of that kind can be running, so it
is not a failure and the stop continues with the other kinds. A session kind whose
active sessions cannot be read for any other reason is counted as a failed stop
with `Id` `*`.

```json
{
  "Status": "stopped_with_failures",
  "Stopped": 2,
  "Failed": 1,
  "CaptainsStopped": 1,
  "CaptainsFailed": 0,
  "PlanningSessionsStopped": 1,
  "PlanningSessionsFailed": 0,
  "RefinementSessionsStopped": 0,
  "RefinementSessionsFailed": 1,
  "Failures": [
    { "Kind": "RefinementSession", "Id": "ors_abc123", "Message": "runtime did not exit" }
  ],
  "UnavailableSources": []
}
```

---

#### POST /api/v1/captains/{id}/restart

Restart a captain in place. The record keeps its identifier, configuration,
credentials, model endpoint, base URL, default playbooks, ownership and any
quarantine or bench hold. A leftover agent process is stopped; the mission, dock
and process references, the recovery count and the heartbeat are cleared; a
captain without a hold returns to Idle. The captain is read in the caller's scope.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `id` | Captain ID (`cpt_` prefix) |

**Response:** `200 OK` - [Captain](#captain) as stored after the restart
**Error:** `404` - Captain not found in the caller's scope
**Error:** `409 Conflict` - The captain is Working, Planning or Refining, or owns an Assigned or InProgress mission. Nothing changed.
**Error:** `500` - A leftover process could not be stopped. Nothing changed.

---

#### POST /api/v1/captains/{id}/quarantine

Hold a captain out of assignment through the shared quarantine service (the same
service MCP `armada_bench_captain` uses). The captain is read in the caller's
scope. The write succeeds only while the captain is Idle or already quarantined
and owns no mission, dock or process; a repeated request updates the reason and
expiry.

**Request Body:**

| Field | Type | Description |
|---|---|---|
| `Reason` | string | Required operator reason |
| `UntilUtc` | datetime | Optional future UTC expiry; takes precedence over `DurationMinutes` |
| `DurationMinutes` | integer | Optional positive duration; with neither value the hold is indefinite |

**Response:** `200 OK` - `CaptainQuarantineResult` with `Outcome: Quarantined`
**Error:** `409` - `Outcome: Busy`; the captain owns work or is not Idle, nothing changed
**Error:** `400` - Missing reason, invalid body, non-positive duration or past expiry
**Error:** `404` - Captain not found in the caller's scope

---

#### POST /api/v1/captains/{id}/unquarantine

Release a quarantine through the shared service: a quarantined captain returns to
Idle with its reason and expiry cleared. A captain in any other state, including a
Working captain, is left unchanged and the outcome is `NotQuarantined`.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `id` | Captain ID (`cpt_` prefix) |

**Response:** `200 OK` - `CaptainQuarantineResult`

```json
{
  "Outcome": "Released",
  "Captain": { "Id": "cpt_abc123", "State": "Idle", "QuarantineUntilUtc": null, "QuarantineReason": null },
  "Message": "Quarantine released."
}
```

**Error:** `404` - Captain not found in the caller's scope

---

#### GET /api/v1/captains/{id}/log

Returns the current session log for a captain. The captain's `.current` pointer file is resolved to find the active mission's log file. Supports pagination via query parameters.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `id` | Captain ID (`cpt_` prefix) |

**Query Parameters:**
| Parameter | Type | Default | Description |
|---|---|---|---|
| `lines` | integer | 100 | Number of lines to return |
| `offset` | integer | 0 | Line offset (0-based, skip this many lines from start) |

**Response:** `200 OK`

```json
{
  "CaptainId": "cpt_abc123",
  "Log": "[2026-03-07] Processing task...\nRunning tests...\n...",
  "Lines": 100,
  "TotalLines": 203
}
```

If the captain has no active log (no pointer file or target file missing), returns an empty log:

```json
{
  "CaptainId": "cpt_abc123",
  "Log": "",
  "Lines": 0,
  "TotalLines": 0
}
```

**Error:** `404` - Captain not found

```bash
curl http://localhost:8080/api/v1/captains/cpt_abc123/log?lines=200 \
  -H "X-Api-Key: your-key"
```

---

#### DELETE /api/v1/captains/{id}

Delete a captain, then remove the events, planning sessions and objective
refinement sessions that reference it. The rule and cleanup are shared with the
batch route, MCP `armada_delete_captain` / `armada_delete_captains` and WebSocket
`delete_captain`.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `id` | Captain ID (`cpt_` prefix) |

**Response:** `204 No Content`
**Error:** `404` - Captain not found
**Error:** `409 Conflict` - Cannot delete captain while state is Working, Planning, or Refining. Stop the captain first.
**Error:** `409 Conflict` - Cannot delete captain with N active mission(s) in Assigned or InProgress status. Cancel or complete them first.

---

#### `POST /api/v1/captains/delete/multiple`

Batch delete multiple captains by ID with the same rule and dependent cleanup as a single delete. Captains that are Working, Planning or Refining or own an Assigned or InProgress mission are skipped. Returns a summary of deleted and skipped entries. **This cannot be undone.**

**Request Body:**

```json
{
  "Ids": ["cpt_abc123", "cpt_def456"]
}
```

**Response:** `200 OK`

```json
{
  "Status": "deleted",
  "Deleted": 2,
  "Skipped": []
}
```

Skipped entries include the entity ID and the reason (e.g., "Not found", "Cannot delete captain while state is Working, Planning, or Refining", or "Cannot delete captain with N active mission(s)").

---

### Signals

A signal is a message between the admiral and captains or between captains.

#### GET /api/v1/signals

List recent signals with pagination.

**Query Parameters:** [Pagination parameters](#pagination-parameters), plus:

| Parameter | Type | Description |
|---|---|---|
| `signalType` | string | Filter by signal type |
| `toCaptainId` | string | Filter by recipient captain ID |
| `unreadOnly` | bool | `true` to return only unread signals |

**Response:** `200 OK` - [EnumerationResult](#enumerationresultt)\<[Signal](#signal)\>

```bash
curl http://localhost:7890/api/v1/signals?toCaptainId=cpt_abc123&unreadOnly=true
```

---

#### POST /api/v1/signals/enumerate

Paginated enumeration of signals with optional filtering and sorting.

**Request Body:** [EnumerationQuery](#enumerationquery) (optional)

**Response:** `200 OK` - [EnumerationResult](#enumerationresultt)\<[Signal](#signal)\>

---

#### POST /api/v1/signals

Send a new signal (message).

**Request Body:** [Signal](#signal)

| Field | Type | Required | Description |
|---|---|---|---|
| `Type` | string | no | Signal type (default: `Nudge`) |
| `Payload` | string | no | Signal payload (message content) |
| `ToCaptainId` | string | no | Recipient captain ID (null = to Admiral) |
| `FromCaptainId` | string | no | Sender captain ID (null = from Admiral) |

**Response:** `201 Created` - [Signal](#signal)

```bash
curl -X POST http://localhost:7890/api/v1/signals \
  -H "Content-Type: application/json" \
  -d '{"Type": "Mail", "Payload": "Please check the test results", "ToCaptainId": "cpt_abc123"}'
```

---

#### `POST /api/v1/signals/delete/multiple`

Batch soft-delete multiple signals by marking them as read. Returns a summary of deleted and skipped entries.

**Request Body:**

```json
{
  "Ids": ["sig_abc123", "sig_def456"]
}
```

**Response:** `200 OK`

```json
{
  "Status": "deleted",
  "Deleted": 2,
  "Skipped": []
}
```

Skipped entries include the entity ID and the reason (e.g., "Not found" or "Empty ID").

---

### Events

System events represent state changes and audit trail entries generated automatically by the server.

#### GET /api/v1/events

List system events with pagination.

**Query Parameters:** [Pagination parameters](#pagination-parameters), plus:

| Parameter | Type | Description |
|---|---|---|
| `type` | string | Filter by event type (e.g. `mission.status_changed`) |
| `captainId` | string | Filter by captain ID |
| `missionId` | string | Filter by mission ID |
| `vesselId` | string | Filter by vessel ID |
| `voyageId` | string | Filter by voyage ID |
| `limit` | int | Alias for `pageSize` |

**Response:** `200 OK` - [EnumerationResult](#enumerationresultt)\<[ArmadaEvent](#armadaevent)\>

```bash
curl http://localhost:7890/api/v1/events?type=mission.status_changed&missionId=msn_abc123
```

---

#### POST /api/v1/events/enumerate

Paginated enumeration of events with optional filtering and sorting.

**Request Body:** [EnumerationQuery](#enumerationquery) (optional)

**Response:** `200 OK` - [EnumerationResult](#enumerationresultt)\<[ArmadaEvent](#armadaevent)\>

---

#### GET /api/v1/events/token-usage

Return provider-reported token usage grouped by runtime and model for the requested window.
Armada does not include tokenizer estimates. `days` accepts `1` through `3650` and defaults to `30`.

---

#### `GET /api/v1/events/{id}`

Return a single event by ID in the caller's scope: every event for an administrator, the tenant's events for a
tenant administrator, and the caller's own events otherwise.

**Path Parameters:**

| Parameter | Description |
|---|---|
| `id` | Event ID (`evt_` prefix) |

**Response:** `200 OK` - [ArmadaEvent](#armadaevent)

**Error:** `404` - Event not found or outside the caller's scope

---

#### `DELETE /api/v1/events/{id}`

Delete a single event by ID.

**Path Parameters:**

| Parameter | Description |
|---|---|
| `id` | Event ID (`evt_` prefix) |

**Response:** `204 No Content`

**Error:** `404` - Event not found

---

#### `POST /api/v1/events/delete/multiple`

Batch delete multiple events by ID. Returns a summary of deleted and skipped entries.

**Request Body:**

```json
{
  "Ids": ["evt_abc123", "evt_def456"]
}
```

**Response:** `200 OK`

```json
{
  "Status": "deleted",
  "Deleted": 2,
  "Skipped": []
}
```

Skipped entries include the entity ID and the reason (e.g., "Not found" or "Empty ID").

---

### Formatted runtime log entries

Both `/api/v1/captains/{id}/log` and `/api/v1/missions/{id}/log` accept
`formatted=true`. The response keeps `Log`, `Lines` and `TotalLines`, and adds
`Entries` plus `EntriesTruncated`. Each entry has `Text`, `Kind`, `IsToolCall`,
`ToolName`, `Redacted` and `Truncated`. Kind is `Text`, `Thinking`, `ToolCall`,
`ToolResult`, `Status` or `Mixed`; none is a mission outcome.

Formatted pages return at most 500 entries. Offset addresses input lines using
the route's existing filtering rules; Lines counts returned entries and Log joins
their text. Legacy text mode retains line pagination and applies shared secret
redaction. See [the complete log contract](reference/backend-logs.md).

### Docks

Docks are git worktrees provisioned for captains. These endpoints provide access to dock state and management.

#### `GET /api/v1/docks`

List all docks with optional filtering.

**Query Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `vesselId` | string | Filter by vessel ID |
| `pageNumber` | integer | Page number (1-based, default 1) |
| `pageSize` | integer | Results per page (default 100) |
| `order` | string | Sort order: `CreatedAscending`, `CreatedDescending` |

**Response:** `200 OK`

```json
{
  "Objects": [],
  "TotalRecords": 0,
  "PageSize": 100,
  "PageNumber": 1,
  "TotalPages": 0,
  "Success": true,
  "TotalMs": 0.5
}
```

---

#### `POST /api/v1/docks/enumerate`

Paginated enumeration of docks with optional filtering and sorting.

**Request Body:**

```json
{
  "PageNumber": 1,
  "PageSize": 25,
  "VesselId": "vsl_abc123"
}
```

**Response:** `200 OK` -- Same shape as `GET /api/v1/docks`.

---

#### `GET /api/v1/docks/{id}`

Get a single dock by ID.

**Path Parameters:**

| Parameter | Description |
|---|---|
| `id` | Dock ID (`dck_` prefix) |

**Response:** `200 OK` - Dock object

**Error:** `404` - Dock not found

---

#### `DELETE /api/v1/docks/{id}`

Delete a dock and clean up its git worktree. Blocked if the dock is actively in use by a captain.

**Path Parameters:**

| Parameter | Description |
|---|---|
| `id` | Dock ID (`dck_` prefix) |

**Response:** `204 No Content`

**Error:** `404` - Dock not found
**Error:** `409` - Dock is actively in use by a captain

---

#### `DELETE /api/v1/docks/{id}/purge`

Force purge a dock and its git worktree, even if a mission references it. **This cannot be undone.**

**Path Parameters:**

| Parameter | Description |
|---|---|
| `id` | Dock ID (`dck_` prefix) |

**Response:** `200 OK`

```json
{
  "Status": "purged",
  "DockId": "dck_abc123"
}
```

**Error:** `404` - Dock not found

---

#### `POST /api/v1/docks/{id}/repair`

Runs git worktree repair on the dock. Non-destructive: no work is removed.

**Path Parameters:**

| Parameter | Description |
|---|---|
| `id` | Dock ID (`dck_` prefix) |

**Response:** `200 OK`

```json
{
  "Status": "repaired",
  "DockId": "dck_abc123"
}
```

**Error:** `404` - Dock not found

---

#### `POST /api/v1/docks/{id}/unstick`

Releases any captain still holding the dock back to Idle and reclaims its
worktree so it stops pinning capacity. Committed branch history is preserved.

**Path Parameters:**

| Parameter | Description |
|---|---|
| `id` | Dock ID (`dck_` prefix) |

**Response:** `200 OK`

```json
{
  "Status": "unstuck",
  "DockId": "dck_abc123"
}
```

**Error:** `404` - Dock not found

---

#### `POST /api/v1/docks/delete/multiple`

Batch delete multiple docks and their git worktrees from the database by ID. Each ID follows the single-dock delete rule: a dock that is active with a captain is not deleted and is reported in `Skipped` with the reason `Dock is active with a captain; force purge it individually to remove it`. To remove such a dock anyway, use `DELETE /api/v1/docks/{id}/purge`. Returns a summary of deleted and skipped entries. **This cannot be undone.**

**Request Body:**

```json
{
  "Ids": ["dck_abc123", "dck_def456"]
}
```

**Response:** `200 OK`

```json
{
  "Status": "deleted",
  "Deleted": 2,
  "Skipped": []
}
```

Skipped entries include the entity ID and the reason (`Not found`, `Empty ID`, or the active-dock refusal).

---

### Merge Queue

A bors-style merge queue that batches branches, runs tests, and lands passing batches.

#### GET /api/v1/merge-queue

List merge queue entries with pagination.

**Query Parameters:** [Pagination parameters](#pagination-parameters)

**Response:** `200 OK` - [EnumerationResult](#enumerationresultt)\<[MergeEntry](#mergeentry)\>

```bash
curl http://localhost:7890/api/v1/merge-queue
```

---

#### POST /api/v1/merge-queue/enumerate

Paginated enumeration of merge queue entries with optional filtering and sorting.

**Request Body:** [EnumerationQuery](#enumerationquery) (optional)

**Response:** `200 OK` - [EnumerationResult](#enumerationresultt)\<[MergeEntry](#mergeentry)\>

---

#### POST /api/v1/merge-queue

Enqueue a branch for testing and merging.

**Request Body:** [MergeEntry](#mergeentry)

| Field | Type | Required | Description |
|---|---|---|---|
| `BranchName` | string | yes | Branch to merge |
| `TargetBranch` | string | no | Target branch (default: `"main"`) |
| `MissionId` | string | no | Parent mission ID |
| `VesselId` | string | no | Vessel ID |
| `Priority` | int | no | Queue priority (lower = higher, default: 0) |
| `TestCommand` | string | no | Test command for verification |

**Response:** `201 Created` - [MergeEntry](#mergeentry)

```bash
curl -X POST http://localhost:7890/api/v1/merge-queue \
  -H "Content-Type: application/json" \
  -d '{"BranchName": "armada/msn_abc123", "TargetBranch": "main", "MissionId": "msn_abc123"}'
```

---

#### GET /api/v1/merge-queue/{id}

Get a single merge queue entry by ID.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `id` | Merge entry ID (`mrg_` prefix) |

**Response:** `200 OK` - [MergeEntry](#mergeentry)
**Error:** `404` - Merge entry not found

---

#### DELETE /api/v1/merge-queue/{id}

Cancel a queued merge entry.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `id` | Merge entry ID (`mrg_` prefix) |

**Response:** `204 No Content`

---

#### POST /api/v1/merge-queue/process

Trigger processing of the merge queue. Creates integration branches, runs tests, and lands passing batches. It processes every tenant's entries, so it requires a global administrator; any other caller receives `403`. A tenant administrator processes one of their own tenant's entries with `POST /api/v1/merge-queue/{id}/process`.

**Response:** `200 OK`

```json
{
  "Status": "processed"
}
```

---

#### `DELETE /api/v1/merge-queue/{id}/purge`

Permanently delete a single terminal merge queue entry from the database. Only entries in Landed, Failed, or Cancelled status can be purged. **This cannot be undone.**

**Path Parameters:**

| Parameter | Description |
|---|---|
| `id` | Merge entry ID (`mrg_` prefix) |

**Response:** `200 OK`

```json
{
  "Status": "purged",
  "EntryId": "mrg_abc123"
}
```

**Error:** `404` - Merge entry not found
**Error:** `409` - Entry is not in a terminal state

---

#### `POST /api/v1/merge-queue/purge`

Batch purge multiple terminal merge queue entries from the database by ID. A tenant administrator purges only entries its own tenant owns; an ID owned by another tenant is reported as `Not found`. A system administrator can purge any tenant's entries. Returns a summary of purged and skipped entries. **This cannot be undone.**

**Request Body:**

```json
{
  "EntryIds": ["mrg_abc123", "mrg_def456"]
}
```

**Response:** `200 OK`

```json
{
  "Status": "purged",
  "EntriesPurged": 2,
  "Skipped": []
}
```

Skipped entries include the entry ID and the reason (e.g., "Not found" or "Not in terminal state").

---

### Code Index

Vessel-scoped repository search and symbol graph endpoints. All routes are authenticated and then checked against the vessel ACL.

#### GET /api/v1/vessels/{vesselId}/code-index/status

Return persisted code-index status for one vessel. `Freshness` describes the lexical index against the default branch. `EmbeddingState` describes the semantic vectors separately: `Disabled` (semantic search off), `Unavailable` (no embedding client), `Complete` (every chunk has a vector from the current provider), or `Incomplete` (`MissingEmbeddingCount` chunks have no vector; the next update retries them). `EmbeddedChunkCount` and `EmbeddingDimensions` give the vector count and length. When a refresh is active, the response includes `UpdateStartedUtc`, `UpdateHeartbeatUtc`, `UpdateStage`, `UpdateProgressDone`, `UpdateProgressTotal`, and `UpdateProgressPercent` in addition to `UpdateInProgress` so clients can show live progress without waiting on the update call. After a failed update, `Freshness` is `Error` and `LastError` holds the failure, while `IndexedCommitSha` and the counts still describe the last successful index.

**Response:** `200 OK` - `CodeIndexStatus`

---

#### POST /api/v1/vessels/{vesselId}/code-index/update

Refresh chunks and supported-language graph sidecars for the vessel default branch. This explicit update is the only operation that indexes a vessel for the first time; automatic refresh after a landing, a stale-index dispatch or the staleness sweep skips a vessel that has never been indexed. On the indexed commit it returns at once unless the embedding provider changed or some chunks lack a vector; then it embeds only what is missing or incompatible.

**Response:** `200 OK` - `CodeIndexStatus`

---

#### POST /api/v1/vessels/{vesselId}/code-index/search

Search indexed chunks with lexical, semantic, file-signature, and graph-aware ranking signals. Search never creates an index. For a vessel that has never been indexed, the response has `Available: false`, `UnavailableReason: "not_indexed"`, a `Message`, and no results, and nothing is sent to the embedding provider; run the update route to index the vessel.

**Request Body:**

```json
{
  "Query": "health route",
  "Limit": 20,
  "PathPrefix": "src/",
  "Language": "typescript",
  "IncludeContent": false,
  "IncludeReferenceOnly": false
}
```

**Response:** `200 OK` - `CodeSearchResponse`

---

#### POST /api/v1/vessels/{vesselId}/code-index/search-symbols

Search graph sidecar symbols by simple or qualified name.

**Response:** `200 OK` - `CodeGraphSymbolSearchResponse`

---

#### POST /api/v1/vessels/{vesselId}/code-index/callers

Resolve direct callers for a symbol.

**Response:** `200 OK` - `CodeGraphNeighborsResponse`

---

#### POST /api/v1/vessels/{vesselId}/code-index/callees

Resolve direct callees for a symbol.

**Response:** `200 OK` - `CodeGraphNeighborsResponse`

---

#### POST /api/v1/vessels/{vesselId}/code-index/node

Resolve one graph node with direct callers, callees, and optional source excerpt.

**Response:** `200 OK` - `CodeGraphNodeResponse`

---

#### POST /api/v1/vessels/{vesselId}/code-index/files

Return indexed files grouped with graph symbols.

**Response:** `200 OK` - `CodeGraphFileStructureResponse`

---

#### POST /api/v1/vessels/{vesselId}/code-index/explore

Explore a bounded graph neighborhood grouped by file.

**Response:** `200 OK` - `CodeGraphExploreResponse`

---

#### POST /api/v1/vessels/{vesselId}/code-index/impact

Traverse caller/callee impact from a seed symbol.

**Response:** `200 OK` - `CodeGraphImpactResponse`

---

#### POST /api/v1/vessels/{vesselId}/code-index/affected-tests

Suggest likely affected tests using graph traversal and naming conventions.

**Response:** `200 OK` - `CodeGraphAffectedTestsResponse`

---

### Playbooks

Playbooks are tenant-scoped markdown documents that can be attached to voyages or standalone missions. Each selection carries its own delivery mode so the model receives either the full content inline or a file path it should read.

#### GET /api/v1/playbooks

List playbooks with pagination.

**Response:** `200 OK` - [EnumerationResult](#enumerationresultt)\<[Playbook](#playbook)\>

#### POST /api/v1/playbooks/enumerate

Paginated enumeration of playbooks with optional filtering and sorting.

**Request Body:** [EnumerationQuery](#enumerationquery) (optional)

#### POST /api/v1/playbooks

Create a playbook.

**Request Body:** [Playbook](#playbook)

#### GET /api/v1/playbooks/{id}

Return a single playbook by ID.

**Response:** `200 OK` - [Playbook](#playbook)

#### PUT /api/v1/playbooks/{id}

Update a playbook's file name, description, content, or active state.

**Request Body:** [Playbook](#playbook)

#### DELETE /api/v1/playbooks/{id}

Delete a playbook. Existing mission snapshots remain immutable.

**Response:** `200 OK`

---

### Memories

Native captain memory records: episodic, semantic and procedural findings with provenance. Records are scoped to the caller: a caller sees the tenant-wide records of its own tenant plus its own user-specific records, and never a record of another tenant.

#### GET /api/v1/memories

List or search records, highest salience first, then newest.

**Query:** `search`, `type` (`Episodic`|`Semantic`|`Procedural`), `topic`, `vesselId`, `pageNumber`, `pageSize`

**Response:** `200 OK` - [EnumerationResult](#enumerationresultt)\<Memory\>

#### POST /api/v1/memories

Create a record, or update the record that already carries the same `key` in the caller's tenant. A write by key replaces the record's fields. Add `?expectedVersion=N` to be refused instead of overwriting a newer record.

**Request Body:** Memory

**Response:** `201 Created` on create, `200 OK` on update, `409 Conflict` when the record changed or the key is taken.

#### GET /api/v1/memories/{id}

Return one record by identifier.

#### PUT /api/v1/memories/{id}

Change one record. Only the supplied fields change, and the version increases. Send `expectedVersion` in the body or as a query parameter to be refused instead of overwriting a newer record.

**Response:** `200 OK`, `404 Not Found`, `409 Conflict`

#### DELETE /api/v1/memories/{id}

Delete one record, for example a record that went stale.

**Response:** `204 No Content`

**Memory fields:** `type` (`Episodic`|`Semantic`|`Procedural`), `topic`, `key` (lowercase slug, unique inside the tenant), `summary`, `content`, `salience` (0.0 to 1.0, orders recall), `version`, `sourceKind` (`Voyage`|`Mission`|`Vessel`|`Conversation`|`Manual`|`Other`), `sourceVoyageId`, `sourceMissionId`, `sourceVesselId`, `sourceDetail`, `vesselId`, `tags`, `scope` (`TenantWide`|`UserSpecific`).

---

### Prompt Templates

Prompt templates define the instruction text used when generating captain mission briefs. Armada ships with built-in templates that can be customized. Custom templates can also be created per tenant.

#### GET /api/v1/prompt-templates

List all prompt templates with pagination.

**Query Parameters:** [Pagination parameters](#pagination-parameters)

**Response:** `200 OK` - [EnumerationResult](#enumerationresultt)\<PromptTemplate\>

```bash
curl http://localhost:7890/api/v1/prompt-templates
```

---

#### POST /api/v1/prompt-templates/enumerate

Paginated enumeration of prompt templates with optional filtering and sorting.

**Request Body:** [EnumerationQuery](#enumerationquery) (optional)

**Response:** `200 OK` - [EnumerationResult](#enumerationresultt)\<PromptTemplate\>

```bash
curl -X POST http://localhost:7890/api/v1/prompt-templates/enumerate \
  -H "Content-Type: application/json" \
  -d '{"PageSize": 10}'
```

---

#### GET /api/v1/prompt-templates/{name}

Get a prompt template by name.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `name` | Template name |

**Response:** `200 OK` - PromptTemplate
**Error:** `404` - Template not found

```bash
curl http://localhost:7890/api/v1/prompt-templates/default
```

---

#### PUT /api/v1/prompt-templates/{name}

Update a prompt template's content. Built-in templates can be customized by updating their content.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `name` | Template name |

**Request Body:**

| Field | Type | Required | Description |
|---|---|---|---|
| `Content` | string | yes | Template content text |
| `Description` | string | no | Template description |

**Response:** `200 OK` - PromptTemplate
**Error:** `404` - Template not found

```bash
curl -X PUT http://localhost:7890/api/v1/prompt-templates/default \
  -H "Content-Type: application/json" \
  -d '{"Content": "You are a captain. Follow these instructions...", "Description": "Custom default template"}'
```

---

#### POST /api/v1/prompt-templates/{name}/reset

Reset a prompt template to its built-in default content. Only applicable to built-in templates that have been customized.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `name` | Template name |

**Response:** `200 OK` - PromptTemplate
**Error:** `404` - Template not found

```bash
curl -X POST http://localhost:7890/api/v1/prompt-templates/default/reset
```

---

### Personas

A persona associates a name and description with a prompt template. Personas are used to configure the behavior of captains within a pipeline. Armada ships with built-in personas that cannot be deleted.

#### GET /api/v1/personas

List all personas with pagination.

**Query Parameters:** [Pagination parameters](#pagination-parameters)

**Response:** `200 OK` - [EnumerationResult](#enumerationresultt)\<Persona\>

```bash
curl http://localhost:7890/api/v1/personas
```

---

#### POST /api/v1/personas/enumerate

Paginated enumeration of personas with optional filtering and sorting.

**Request Body:** [EnumerationQuery](#enumerationquery) (optional)

**Response:** `200 OK` - [EnumerationResult](#enumerationresultt)\<Persona\>

```bash
curl -X POST http://localhost:7890/api/v1/personas/enumerate \
  -H "Content-Type: application/json" \
  -d '{"PageSize": 10}'
```

---

#### GET /api/v1/personas/{name}

Get a persona by name.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `name` | Persona name |

**Response:** `200 OK` - Persona
**Error:** `404` - Persona not found

```bash
curl http://localhost:7890/api/v1/personas/default
```

---

#### POST /api/v1/personas

Create a new persona.

**Request Body:**

| Field | Type | Required | Description |
|---|---|---|---|
| `Name` | string | yes | Persona name |
| `Description` | string | no | Persona description |
| `PromptTemplateName` | string | yes | Name of the prompt template to use |
| `MinimumTier` | string | no | Capability floor for missions of this persona: `Economy`, `Standard`, `Premium`, or `null` for none. The mission's request raises it, never lowers it. A body that sends the retired `Specialist` flag returns 400 `specialist_retired` |
| `DefaultCaptainId` | string | no | Captain id missions of this persona prefer; `null` or `""` for none |

**Response:** `201 Created` - Persona
**Error:** `400` - `default_captain_not_found`: no captain with that id exists in the caller's tenant (a captain in another tenant counts as not found)
**Error:** `400` - `default_captain_persona_locked`: the captain's `AllowedPersonas` excludes the persona

A refused create writes nothing. The MCP `create_persona` tool and the WebSocket
`create_persona` command apply the same default-captain rule.

```bash
curl -X POST http://localhost:7890/api/v1/personas \
  -H "Content-Type: application/json" \
  -d '{"Name": "reviewer", "Description": "Code review specialist", "PromptTemplateName": "default"}'
```

---

#### PUT /api/v1/personas/{name}

Update an existing persona. Every tenant uses a built-in persona, so only a global admin may update one; any other caller receives `403`.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `name` | Persona name |

**Request Body:**

| Field | Type | Required | Description |
|---|---|---|---|
| `Description` | string | no | Updated description |
| `PromptTemplateName` | string | no | Updated prompt template name |
| `MinimumTier` | string | no | Updated capability floor; `null` clears it, omitted leaves it unchanged. The retired `Specialist` flag returns 400 `specialist_retired` |
| `DefaultCaptainId` | string | no | Captain id missions of this persona prefer; `null` or `""` clears it, omitted leaves it unchanged |

**Response:** `200 OK` - Persona
**Error:** `400` - `default_captain_not_found`: no captain with that id exists in the persona's tenant (a captain in another tenant counts as not found)
**Error:** `400` - `default_captain_persona_locked`: the captain's `AllowedPersonas` excludes the persona
**Error:** `404` - Persona not found

A refused update writes nothing. The MCP `update_persona` tool and the WebSocket
`update_persona` command apply the same default-captain rule.

```bash
curl -X PUT http://localhost:7890/api/v1/personas/reviewer \
  -H "Content-Type: application/json" \
  -d '{"Description": "Updated reviewer persona", "PromptTemplateName": "review-template"}'
```

---

#### DELETE /api/v1/personas/{name}

Delete a persona. Built-in personas cannot be deleted.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `name` | Persona name |

**Response:** `204 No Content`
**Error:** `404` - Persona not found
**Error:** `403` - Built-in persona cannot be deleted

```bash
curl -X DELETE http://localhost:7890/api/v1/personas/reviewer
```

---

### Pipelines

A pipeline defines an ordered sequence of stages, each associated with a persona. Pipelines control the multi-stage workflow that missions progress through. Armada ships with built-in pipelines that cannot be deleted.

#### GET /api/v1/pipelines

List all pipelines with pagination. Response includes the stages for each pipeline.

**Query Parameters:** [Pagination parameters](#pagination-parameters)

**Response:** `200 OK` - [EnumerationResult](#enumerationresultt)\<Pipeline\>

```bash
curl http://localhost:7890/api/v1/pipelines
```

---

#### POST /api/v1/pipelines/enumerate

Paginated enumeration of pipelines with optional filtering and sorting.

**Request Body:** [EnumerationQuery](#enumerationquery) (optional)

**Response:** `200 OK` - [EnumerationResult](#enumerationresultt)\<Pipeline\>

```bash
curl -X POST http://localhost:7890/api/v1/pipelines/enumerate \
  -H "Content-Type: application/json" \
  -d '{"PageSize": 10}'
```

---

#### GET /api/v1/pipelines/{name}

Get a pipeline by name, including its stages.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `name` | Pipeline name |

**Response:** `200 OK` - Pipeline
**Error:** `404` - Pipeline not found

```bash
curl http://localhost:7890/api/v1/pipelines/default
```

---

#### POST /api/v1/pipelines

Create a new pipeline with stages.

**Request Body:**

| Field | Type | Required | Description |
|---|---|---|---|
| `Name` | string | yes | Pipeline name |
| `Description` | string | no | Pipeline description |
| `Stages` | array | yes | Ordered list of pipeline stages |

**Stage fields:**

| Field | Type | Required | Description |
|---|---|---|---|
| `PersonaName` | string | yes | Name of the persona for this stage |
| `IsOptional` | bool | no | Whether this stage can be skipped (default: false) |
| `Description` | string | no | Stage description |

**Response:** `201 Created` - Pipeline

```bash
curl -X POST http://localhost:7890/api/v1/pipelines \
  -H "Content-Type: application/json" \
  -d '{"Name": "review-pipeline", "Description": "Code with review", "Stages": [{"PersonaName": "default", "Description": "Implementation"}, {"PersonaName": "reviewer", "IsOptional": false, "Description": "Code review"}]}'
```

---

#### PUT /api/v1/pipelines/{name}

Update an existing pipeline. Every tenant uses a built-in pipeline, so only a global admin may update one; any other caller receives `403`.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `name` | Pipeline name |

**Request Body:**

| Field | Type | Required | Description |
|---|---|---|---|
| `Description` | string | no | Updated description |
| `Stages` | array | no | Updated ordered list of pipeline stages (replaces all existing stages) |

**Response:** `200 OK` - Pipeline
**Error:** `404` - Pipeline not found

```bash
curl -X PUT http://localhost:7890/api/v1/pipelines/review-pipeline \
  -H "Content-Type: application/json" \
  -d '{"Description": "Updated pipeline", "Stages": [{"PersonaName": "default"}, {"PersonaName": "reviewer"}]}'
```

---

#### DELETE /api/v1/pipelines/{name}

Delete a pipeline. Built-in pipelines cannot be deleted.

**Path Parameters:**
| Parameter | Description |
|---|---|
| `name` | Pipeline name |

**Response:** `204 No Content`
**Error:** `404` - Pipeline not found
**Error:** `403` - Built-in pipeline cannot be deleted

```bash
curl -X DELETE http://localhost:7890/api/v1/pipelines/review-pipeline
```

---

### Backup and Restore

#### GET /api/v1/backup

Create and download a verified provider-native backup of the configured Armada database, with settings and a manifest.

The backup uses the configured provider's native tooling: the SQLite online backup API, `pg_dump`, `mysqldump`, or SQL Server `BACKUP DATABASE`. The artifact is restored into an owned isolated target and verified before the archive is written. The MySQL, PostgreSQL and SQL Server client utilities must be installed on the admiral host. SQL Server also needs `selfDeploy.sqlServerBackupDirectory`, a path visible to the SQL Server host.

**Response:** `200 OK` — Binary ZIP file stream with `Content-Disposition: attachment; filename="armada-backup-{timestamp}.zip"` header.

**Errors:** `500` with `Message` set to a stable reason (for example `postgresql_backup_failed`, `backup_not_verified`, `sqlserver_server_backup_directory_not_configured`). No archive is produced.

**ZIP Contents:**

| File | Description |
|---|---|
| `armada.db` | SQLite only: verified online backup of the configured database file |
| `database/armada-backup.dump` / `.sql` / `.bak` | PostgreSQL custom-format dump, MySQL dump, or a SQL Server backup readable by the admiral |
| `settings.json` | Current Armada server configuration with every secret replaced by `[REDACTED]`: database and connection-string passwords, API keys, tokens, provider keys, bearer and encryption keys, and any other value the shared redaction rule marks secret |
| `manifest.json` | `databaseType`, `schemaVersion` and `recordCounts` read from the provider; `backupTimestampUtc`, `armadaVersion`, `artifactEntry`, `artifactSha256`, `backupValidated`, `restoreVerified`, `settingsRedacted`, `redactedSettingCount`, and `serverArtifactPath` when SQL Server keeps the artifact on the database host |

**Example:**

```bash
curl -H "X-Api-Key: your-key" http://localhost:7890/api/v1/backup -o backup.zip
```

---

#### POST /api/v1/restore

Restore Armada from a previously created backup ZIP file.

**Request:** Binary ZIP file in the request body (`Content-Type: application/zip`).

**Headers:**

| Header | Required | Description |
|---|---|---|
| `Content-Type` | Yes | `application/zip` |
| `X-Original-Filename` | No | Original filename of the uploaded backup (used in the response message). If omitted, the server's temp filename is used. |

**Validation:**
- Restore replaces the database only when the admiral uses SQLite. PostgreSQL, MySQL and SQL Server return `409` with `restore_unsupported_for_provider_<type>` before the archive is read. A live native restore of a server database cannot be made atomic, and a failure part-way would leave the running database damaged. Restore those providers with their native tools while the admiral is stopped.
- An archive whose manifest names another provider returns `409` with `backup_provider_mismatch`.
- The ZIP must contain `armada.db` that passes `PRAGMA integrity_check` and has a `schema_migrations` table; otherwise `409` with `backup_database_entry_missing` or `backup_database_invalid`.
- A verified safety backup is created first. The database is then replaced through the SQLite online backup API into the configured database file, so open connections stay valid.
- Archived `settings.json` is merged onto this host's settings before anything is replaced. Each `[REDACTED]` value keeps this host's current secret. A redacted value with no local counterpart is omitted, so a placeholder is never written. Non-secret values come from the archive. An unreadable archived or local settings document returns `409` with `restore_settings_unreadable`. The response adds `PreservedSecretCount` and `DroppedSecretCount`.

**Response:** `200 OK`

```json
{
  "Status": "restored",
  "SafetyBackupPath": "~/.armada/backups/pre-restore-2026-03-11-120000-1a2b3c4d.zip",
  "SchemaVersion": 9,
  "Message": "Database restored from backup.zip. Restart the server to reload the restored data."
}
```

**Example:**

```bash
curl -X POST -H "X-Api-Key: your-key" \
  -H "Content-Type: application/zip" \
  -H "X-Original-Filename: backup.zip" \
  --data-binary @backup.zip \
  http://localhost:7890/api/v1/restore
```

> **Note:** Restart the server after restoring to ensure all in-memory state is refreshed.

---

### Workspace

The Workspace endpoints expose a vessel's working directory for browsing,
editing, search, git status, review, and one-shot shell commands.

#### GET /api/v1/workspace/vessels/{vesselId}/tree

List one directory of the vessel working tree.

**Path Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `vesselId` | string | Vessel ID (`vsl_` prefix) |

**Query Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `path` | string | Optional repository-relative directory path |

**Response:** `200 OK` — `WorkspaceTreeResult`

#### GET /api/v1/workspace/vessels/{vesselId}/diff

Return a unified git diff of the working tree against HEAD, optionally scoped
to one path. Used by the in-app review/diff viewer.

**Path Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `vesselId` | string | Vessel ID (`vsl_` prefix) |

**Query Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `path` | string | Optional repository-relative path to scope the diff |

**Response:** `200 OK` — `WorkspaceDiffResult`

```bash
curl http://localhost:7890/api/v1/workspace/vessels/vsl_abc123/diff
```

#### GET /api/v1/workspace/vessels/{vesselId}/file

Read one workspace file with content and metadata.

**Path Parameters:** `vesselId` (vessel ID).

**Query Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `path` | string | Repository-relative file path (required) |

**Response:** `200 OK` — `WorkspaceFileResponse`

#### PUT /api/v1/workspace/vessels/{vesselId}/file

Write text content into the vessel working tree with optimistic concurrency
validation.

**Request Body:** `WorkspaceSaveRequest`

**Response:** `200 OK` — `WorkspaceSaveResult`

#### POST /api/v1/workspace/vessels/{vesselId}/directory

Create a directory inside the vessel working tree.

**Request Body:** `WorkspaceCreateDirectoryRequest`

**Response:** `201 Created` — `WorkspaceOperationResult`

#### POST /api/v1/workspace/vessels/{vesselId}/rename

Rename or move one file or directory inside the vessel working tree.

**Request Body:** `WorkspaceRenameRequest`

**Response:** `200 OK` — `WorkspaceOperationResult`

#### DELETE /api/v1/workspace/vessels/{vesselId}/entry

Delete one file or directory inside the vessel working tree.

**Query Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `path` | string | Repository-relative file or directory path (required) |

**Response:** `200 OK` — `WorkspaceOperationResult`

#### GET /api/v1/workspace/vessels/{vesselId}/search

Search text files in the vessel working tree and return line-level matches.

**Query Parameters:**

| Parameter | Type | Description |
|---|---|---|
| `q` | string | Search text query (required) |
| `maxResults` | int | Maximum matches (default 200, max 1000) |

**Response:** `200 OK` — `WorkspaceSearchResult`

#### GET /api/v1/workspace/vessels/{vesselId}/changes

Return branch status and changed files from the vessel working tree.

**Path Parameters:** `vesselId` (vessel ID).

**Response:** `200 OK` — `WorkspaceChangesResult`

#### GET /api/v1/workspace/vessels/{vesselId}/status

Return high-level workspace status, git branch state, and active mission
overlap context.

**Path Parameters:** `vesselId` (vessel ID).

**Response:** `200 OK` — `WorkspaceStatusResult`

#### POST /api/v1/workspace/vessels/{vesselId}/exec

Run a shell command in the vessel working tree (the in-browser dock terminal).
The command runs through the platform shell (`cmd.exe` on Windows, `/bin/sh`
elsewhere) as the leader of its own process group, and is killed with every
process it started when it exceeds the timeout. `stdout` and `stderr` each keep
256 KiB; past that the beginning and the end stay, with a marker naming the
omitted bytes.
**Global administrators only.** The command runs as the server process, with its
filesystem, network and credentials. Any other caller receives `403`.

**Path Parameters:** `vesselId` (vessel ID).

**Request Body:** `WorkspaceExecRequest`

| Field | Type | Description |
|---|---|---|
| `command` | string | The command line to execute (required) |
| `timeoutSeconds` | int | Timeout in seconds before the process tree is killed (clamped to [1, 600], default 60) |

**Response:** `200 OK` — `WorkspaceExecResult`

```bash
curl -X POST http://localhost:7890/api/v1/workspace/vessels/vsl_abc123/exec \
  -H "Content-Type: application/json" \
  -d '{"command": "git status", "timeoutSeconds": 30}'
```

---

### Planning Sessions

Planning sessions back the dashboard captain-chat flow and transcript-to-dispatch handoff. SQLite and PostgreSQL store planning sessions; on MySQL and SQL Server these routes return `501 Not Implemented` with a message that names the provider.

#### GET /api/v1/planning-sessions

List planning sessions visible to the authenticated caller.

- Response: `200 OK` - `PlanningSession[]`

#### POST /api/v1/planning-sessions

Create a planning session, reserve the selected captain, and provision a planning dock.

```json
{
  "Title": "Refactor request history filters",
  "CaptainId": "cpt_abc123",
  "VesselId": "vsl_def456",
  "FleetId": "flt_xyz789",
  "PipelineId": "pln_fullpipeline",
  "SelectedPlaybooks": []
}
```

- Response: `201 Created`
- Response shape:

```json
{
  "Session": { "...": "PlanningSession" },
  "Messages": [],
  "Captain": { "...": "Captain" },
  "Vessel": { "...": "Vessel" }
}
```

#### GET /api/v1/planning-sessions/{id}

Read one planning session with transcript, captain, and vessel context.

- Response: `200 OK`
- Errors: `404 Not Found`

#### POST /api/v1/planning-sessions/{id}/messages

Append one user message and launch the next planning turn.

```json
{
  "Content": "Summarize the changes and propose a safe rollout."
}
```

- Response: `200 OK` - same detail shape as `GET /api/v1/planning-sessions/{id}`

#### POST /api/v1/planning-sessions/{id}/summarize

Generate a dispatch-ready draft from a selected or inferred assistant message without launching the voyage.

```json
{
  "MessageId": "psm_abc123",
  "Title": "Refresh request history docs"
}
```

- Response: `200 OK` - `PlanningSessionSummaryResponse`

#### POST /api/v1/planning-sessions/{id}/dispatch

Create a voyage directly from planning output. Dispatch also releases the reserved captain and dock.

The session objective and every other objective linked to the session are admitted together before the voyage is created and linked before the response. If any of them is already dispatched or its admission is busy, the request returns `409` with `objective_already_dispatched` or `objective_dispatch_busy` and no voyage is created; `objective_dispatch_busy` is retryable.

```json
{
  "MessageId": "psm_abc123",
  "Title": "Refresh request history docs",
  "Description": "Update docs and validation assets for the shipped request-history feature."
}
```

- Response: `200 OK` - `Voyage`

#### POST /api/v1/planning-sessions/{id}/stop-turn

Abort an in-flight planning turn without ending the session. The reserved captain and dock stay held.

- Response: `200 OK`

#### POST /api/v1/planning-sessions/{id}/stop

Stop an active planning session and release its resources.

- Response: `200 OK` - same detail shape as `GET /api/v1/planning-sessions/{id}`

#### DELETE /api/v1/planning-sessions/{id}

Delete a planning session and its transcript. Active sessions are stopped first.

- Response: `204 No Content`

---

### Inbox

#### GET /api/v1/inbox

Return the operator's "needs you" inbox: everything across the fleet awaiting
a decision or intervention, ordered most-urgent first. It surfaces missions in
Review, failed landings, failed missions, failed merges, deployments pending
approval, failed or verification-failed deployments, and stalled captains.
Purely informational state changes are excluded.

Requires a global administrator. The inbox reads every tenant's missions,
captains, merges and incidents, so a narrower caller receives `403`.

**Response:** `200 OK` — `List<InboxItem>`

```bash
curl http://localhost:7890/api/v1/inbox
```

---

### Background Jobs

Dispatch, code-index refresh, merge processing, disk lifecycle, terminal-voyage
reconciliation and similar operations run as long-running Admiral jobs. The
MCP tool that starts one returns its job ID; `armada_job_status` and these
routes read it. A job carries no tenant or user, so both routes require a
global administrator and a narrower caller receives `403`.

A job's `Status` is `Accepted`, `Running`, `Succeeded`, `Failed` or `Lost`.
Every job is written to the job journal in the data directory, so a job
outlives the admiral process that accepted it. A job that process never
finished reads `Lost` after the next start, with `job_lost_on_restart` in its
`FailureMessage`. Finished jobs are kept for 14 days. Jobs have no cancel
operation; a job that stays `Accepted` or `Running` for 30 minutes is reaped
as `Failed`.

#### GET /api/v1/jobs

List every job the Admiral knows, newest submitted first: the jobs held in
memory and the journalled jobs, including those a previous admiral process
accepted.

**Response:** `200 OK`

```json
{
  "Success": true,
  "Objects": [
    {
      "JobId": "job_abc123",
      "Operation": "voyage_dispatch",
      "Status": "Failed",
      "SubmittedAtUtc": "2026-01-01T00:00:00Z",
      "StartedAtUtc": "2026-01-01T00:00:01Z",
      "CompletedAtUtc": "2026-01-01T00:00:02Z",
      "Result": null,
      "FailureMessage": "dispatch_hold_active: ...",
      "ObjectiveId": "obj_abc123",
      "VesselId": "vsl_abc123"
    }
  ],
  "TotalRecords": 1,
  "UnreadableJournalRecords": 0
}
```

List entries omit `Result`; read one job for it. `UnreadableJournalRecords`
counts journal records the Admiral could not read. Each one is also written to
the Admiral log as a warning, so a shorter list is never silent.

#### GET /api/v1/jobs/{id}

Read one job, including `Result` when it succeeded and `FailureMessage` when it
failed or was lost.

**Response:** `200 OK` — the job. **Errors:** `404` when the job is unknown or
its journal record has expired.

---

## Data Types

### Models

#### TenantMetadata

A tenant in the multi-tenant system.

```json
{
  "Id": "ten_abc123",
  "Name": "Acme Corp",
  "Active": true,
  "CreatedUtc": "2026-03-07T12:00:00Z",
  "LastUpdateUtc": "2026-03-07T12:00:00Z"
}
```

| Field | Type | Default | Description |
|---|---|---|---|
| `Id` | string | auto-generated | Unique ID with `ten_` prefix |
| `Name` | string | `"My Tenant"` | Tenant name |
| `Active` | bool | true | Whether tenant is active |
| `IsProtected` | bool | false | Protected tenants cannot be deleted directly |
| `CreatedUtc` | datetime | now | Creation timestamp (UTC) |
| `LastUpdateUtc` | datetime | now | Last update timestamp (UTC) |

---

#### UserMaster

A user in the multi-tenant system. Passwords are stored as SHA256 hashes and redacted in API responses.

```json
{
  "Id": "usr_abc123",
  "TenantId": "default",
  "Email": "admin@armada",
  "PasswordSha256": "********",
  "FirstName": "Jane",
  "LastName": "Doe",
  "IsAdmin": false,
  "IsTenantAdmin": false,
  "IsProtected": false,
  "Active": true,
  "CreatedUtc": "2026-03-07T12:00:00Z",
  "LastUpdateUtc": "2026-03-07T12:00:00Z"
}
```

| Field | Type | Default | Description |
|---|---|---|---|
| `Id` | string | auto-generated | Unique ID with `usr_` prefix |
| `TenantId` | string | `"default"` | Parent tenant |
| `Email` | string | `"admin@armada"` | Email address (unique within tenant) |
| `PasswordSha256` | string | SHA256("password") | SHA256 hash of password (redacted in responses) |
| `FirstName` | string? | null | First name |
| `LastName` | string? | null | Last name |
| `IsAdmin` | bool | false | Global system admin privileges |
| `IsTenantAdmin` | bool | false | Tenant-scoped admin privileges |
| `IsProtected` | bool | false | Protected users cannot be deleted directly |
| `Active` | bool | true | Whether user is active |
| `CreatedUtc` | datetime | now | Creation timestamp (UTC) |
| `LastUpdateUtc` | datetime | now | Last update timestamp (UTC) |

---

#### Credential

A bearer token credential for API authentication.

```json
{
  "Id": "crd_abc123",
  "TenantId": "default",
  "UserId": "default",
  "Name": "My API Token",
  "BearerToken": "aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789...",
  "IsProtected": false,
  "Active": true,
  "CreatedUtc": "2026-03-07T12:00:00Z",
  "LastUpdateUtc": "2026-03-07T12:00:00Z"
}
```

| Field | Type | Default | Description |
|---|---|---|---|
| `Id` | string | auto-generated | Unique ID with `crd_` prefix |
| `TenantId` | string | `"default"` | Parent tenant |
| `UserId` | string | `"default"` | Owning user |
| `Name` | string? | null | Friendly name |
| `BearerToken` | string | auto-generated | 64-character random alphanumeric token |
| `IsProtected` | bool | false | Protected credentials cannot be deleted directly |
| `Active` | bool | true | Whether credential is active |
| `CreatedUtc` | datetime | now | Creation timestamp (UTC) |
| `LastUpdateUtc` | datetime | now | Last update timestamp (UTC) |

---

#### AuthContext

Represents the authenticated identity context resolved from any authentication method.

```json
{
  "IsAuthenticated": true,
  "TenantId": "default",
  "UserId": "default",
  "IsAdmin": true,
  "IsTenantAdmin": true,
  "AuthMethod": "Bearer"
}
```

| Field | Type | Description |
|---|---|---|
| `IsAuthenticated` | bool | Whether the request is authenticated |
| `TenantId` | string? | Tenant identifier |
| `UserId` | string? | User identifier |
| `IsAdmin` | bool | Global system admin privileges |
| `IsTenantAdmin` | bool | Tenant-scoped admin privileges |
| `AuthMethod` | string? | `"Bearer"`, `"Session"`, `"ApiKey"`, or null |

---

Role semantics:

- `IsAdmin = true`: global system-wide admin with access to every tenant and object.
- `IsAdmin = false`, `IsTenantAdmin = true`: tenant-scoped admin with full access inside that tenant.
- `IsAdmin = false`, `IsTenantAdmin = false`: regular user limited to read-only tenant visibility plus self-service on their own user account and credentials.

Immutable update fields:

- `Id`, creation timestamps, ownership identifiers (`TenantId`, `UserId` where applicable), and `IsProtected` are preserved server-side on update routes.

---

#### WhoAmIResult

Result of `GET /api/v1/whoami`.

```json
{
  "Tenant": { ... },
  "User": { ... }
}
```

| Field | Type | Description |
|---|---|---|
| `Tenant` | [TenantMetadata](#tenantmetadata) | Tenant information |
| `User` | [UserMaster](#usermaster) | User information (password redacted) |

---

#### AuthenticateRequest

Request body for `POST /api/v1/authenticate`.

| Field | Type | Required | Description |
|---|---|---|---|
| `TenantId` | string | Yes | Tenant identifier |
| `Email` | string | Yes | User email address |
| `Password` | string | Yes | Plaintext password |

---

#### TenantLookupRequest

Request body for `POST /api/v1/tenants/lookup`.

| Field | Type | Required | Description |
|---|---|---|---|
| `Email` | string | Yes | Email address to look up |

---

#### TenantLookupResult

Result of `POST /api/v1/tenants/lookup`.

| Field | Type | Description |
|---|---|---|
| `Tenants` | array | List of `{ TenantId, TenantName }` entries matching the email |

---

#### OnboardingRequest

Request body for `POST /api/v1/onboarding`.

| Field | Type | Required | Description |
|---|---|---|---|
| `TenantId` | string | Yes | Tenant to join |
| `Email` | string | Yes | Email address |
| `Password` | string | Yes | Plaintext password |
| `FirstName` | string? | No | First name |
| `LastName` | string? | No | Last name |

---

#### OnboardingResult

Result of `POST /api/v1/onboarding`.

| Field | Type | Description |
|---|---|---|
| `Success` | bool | Whether onboarding succeeded |
| `Tenant` | [TenantMetadata](#tenantmetadata)? | Created/joined tenant |
| `User` | [UserMaster](#usermaster)? | Created user (password redacted) |
| `Credential` | [Credential](#credential)? | Created credential with bearer token |
| `ErrorMessage` | string? | Error message if failed |

---

#### Fleet

A named collection of repositories under management.

```json
{
  "Id": "flt_abc123",
  "Name": "Production Fleet",
  "Description": "Production repositories",
  "Active": true,
  "CreatedUtc": "2026-03-07T12:00:00Z",
  "LastUpdateUtc": "2026-03-07T12:00:00Z"
}
```

| Field | Type | Default | Description |
|---|---|---|---|
| `Id` | string | auto-generated | Unique ID with `flt_` prefix |
| `Name` | string | `"My Fleet"` | Fleet name |
| `Description` | string? | null | Fleet description |
| `Active` | bool | true | Whether fleet is active |
| `CreatedUtc` | datetime | now | Creation timestamp (UTC) |
| `LastUpdateUtc` | datetime | now | Last update timestamp (UTC) |

---

#### Vessel

A git repository registered with Armada.

```json
{
  "Id": "vsl_abc123",
  "FleetId": "flt_abc123",
  "Name": "MyRepo",
  "RepoUrl": "https://github.com/org/repo.git",
  "LocalPath": "/home/user/.armada/repos/MyRepo",
  "WorkingDirectory": null,
  "DefaultBranch": "main",
  "ProjectContext": null,
  "StyleGuide": null,
  "EnableModelContext": false,
  "ModelContext": null,
  "LandingMode": null,
  "BranchCleanupPolicy": null,
  "Active": true,
  "CreatedUtc": "2026-03-07T12:00:00Z",
  "LastUpdateUtc": "2026-03-07T12:00:00Z"
}
```

| Field | Type | Default | Description |
|---|---|---|---|
| `Id` | string | auto-generated | Unique ID with `vsl_` prefix |
| `FleetId` | string? | null | Parent fleet ID |
| `Name` | string | `"My Vessel"` | Vessel name |
| `RepoUrl` | string? | null | Remote repository URL |
| `LocalPath` | string? | null | Local path to bare repository clone |
| `WorkingDirectory` | string? | null | Local working directory for merge on completion |
| `DefaultBranch` | string | `"main"` | Default branch name |
| `ProjectContext` | string? | null | Project context describing architecture, key files, and dependencies |
| `StyleGuide` | string? | null | Style guide describing naming conventions, patterns, and library preferences |
| `EnableModelContext` | bool | true | Whether the model context renders into mission briefs |
| `ModelContext` | string? | null | Repository context rendered into each mission brief as a `## Model Context` section when `EnableModelContext` is true and the text is not blank |
| `LandingMode` | [LandingModeEnum](#landingmodeenum)? | null | Per-vessel landing policy override (null = use global setting) |
| `BranchCleanupPolicy` | [BranchCleanupPolicyEnum](#branchcleanuppolicyenum)? | null | Per-vessel branch cleanup policy override (null = use global setting) |
| `Active` | bool | true | Whether vessel is active |
| `CreatedUtc` | datetime | now | Creation timestamp (UTC) |
| `LastUpdateUtc` | datetime | now | Last update timestamp (UTC) |

---

#### Voyage

A batch of related missions tracked together.

```json
{
  "Id": "vyg_abc123",
  "Title": "API Hardening",
  "Description": "Security improvements across the API",
  "Status": "InProgress",
  "SelectedPlaybooks": [
    {
      "PlaybookId": "pbk_abc123",
      "DeliveryMode": "InlineFullContent"
    }
  ],
  "CreatedUtc": "2026-03-07T12:00:00Z",
  "CompletedUtc": null,
  "LastUpdateUtc": "2026-03-07T12:00:00Z",
  "AutoPush": null,
  "AutoCreatePullRequests": null,
  "AutoMergePullRequests": null,
  "LandingMode": null
}
```

| Field | Type | Default | Description |
|---|---|---|---|
| `Id` | string | auto-generated | Unique ID with `vyg_` prefix |
| `Title` | string | `"New Voyage"` | Voyage title |
| `Description` | string? | null | Voyage description |
| `Status` | [VoyageStatusEnum](#voyagestatusenum) | `Open` | Current status |
| `SelectedPlaybooks` | array\<[SelectedPlaybook](#selectedplaybook)\> | `[]` | Ordered playbook selections recorded on the voyage |
| `CreatedUtc` | datetime | now | Creation timestamp (UTC) |
| `CompletedUtc` | datetime? | null | Completion timestamp (UTC) |
| `LastUpdateUtc` | datetime | now | Last update timestamp (UTC) |
| `AutoPush` | bool? | null | Per-voyage auto-push override (null = use global setting) |
| `AutoCreatePullRequests` | bool? | null | Per-voyage auto-create PRs override |
| `AutoMergePullRequests` | bool? | null | Per-voyage auto-merge PRs override |
| `LandingMode` | [LandingModeEnum](#landingmodeenum)? | null | Per-voyage landing policy override (null = use vessel/global setting) |

---

#### Mission

An atomic unit of work assigned to a captain.

```json
{
  "Id": "msn_abc123",
  "VoyageId": "vyg_abc123",
  "VesselId": "vsl_abc123",
  "CaptainId": "cpt_abc123",
  "Title": "Fix login bug",
  "Description": "The login form does not validate email addresses",
  "Status": "InProgress",
  "Priority": 100,
  "SelectedPlaybooks": [
    {
      "PlaybookId": "pbk_abc123",
      "DeliveryMode": "InstructionWithReference"
    }
  ],
  "PlaybookSnapshots": [
    {
      "PlaybookId": "pbk_abc123",
      "FileName": "CSHARP_BACKEND_ARCHITECTURE.md",
      "DeliveryMode": "InstructionWithReference",
      "ResolvedPath": "C:\\Armada\\runtime\\playbooks\\msn_abc123\\01_CSHARP_BACKEND_ARCHITECTURE.md"
    }
  ],
  "ParentMissionId": null,
  "BranchName": "armada/msn_abc123",
  "DockId": null,
  "ProcessId": null,
  "PrUrl": null,
  "CommitHash": null,
  "DiffSnapshot": null,
  "CreatedUtc": "2026-03-07T12:00:00Z",
  "StartedUtc": "2026-03-07T12:05:00Z",
  "CompletedUtc": null,
  "LastUpdateUtc": "2026-03-07T12:10:00Z"
}
```

| Field | Type | Default | Description |
|---|---|---|---|
| `Id` | string | auto-generated | Unique ID with `msn_` prefix |
| `VoyageId` | string? | null | Parent voyage ID |
| `VesselId` | string? | null | Target vessel (repository) ID |
| `CaptainId` | string? | null | Assigned captain (agent) ID |
| `Title` | string | `"New Mission"` | Mission title |
| `Description` | string? | null | Detailed instructions for the AI agent |
| `Status` | [MissionStatusEnum](#missionstatusenum) | `Pending` | Current status |
| `Priority` | int | 100 | Priority (lower number = higher priority) |
| `SelectedPlaybooks` | array\<[SelectedPlaybook](#selectedplaybook)\> | `[]` | Ordered playbook selections requested for the mission |
| `PlaybookSnapshots` | array\<[MissionPlaybookSnapshot](#missionplaybooksnapshot)\> | `[]` | Immutable playbook materialization used for execution |
| `ParentMissionId` | string? | null | Parent mission ID for sub-tasks |
| `BranchName` | string? | null | Git branch name |
| `DockId` | string? | null | Dock identifier for the mission's worktree |
| `ProcessId` | int? | null | OS process ID of the agent working on the mission |
| `PrUrl` | string? | null | Pull request URL if created |
| `CommitHash` | string? | null | Git commit hash captured on completion |
| `DiffSnapshot` | string? | null | Always `null` in list/status responses to keep payloads compact. Use `GET /api/v1/missions/{id}/diff` to retrieve the full diff. |
| `CreatedUtc` | datetime | now | Creation timestamp (UTC) |
| `StartedUtc` | datetime? | null | Work start timestamp (UTC) |
| `CompletedUtc` | datetime? | null | Completion timestamp (UTC) |
| `TotalRuntimeMs` | long? | null | Total execution runtime in milliseconds |
| `LastUpdateUtc` | datetime | now | Last update timestamp (UTC) |

---

#### Captain

A worker AI agent instance executing missions.

```json
{
  "Id": "cpt_abc123",
  "Name": "captain-1",
  "Runtime": "ClaudeCode",
  "Model": null,
  "SystemInstructions": null,
  "State": "Idle",
  "CurrentMissionId": null,
  "CurrentDockId": null,
  "ProcessId": null,
  "RecoveryAttempts": 0,
  "LastHeartbeatUtc": null,
  "CreatedUtc": "2026-03-07T12:00:00Z",
  "LastUpdateUtc": "2026-03-07T12:00:00Z"
}
```

| Field | Type | Default | Description |
|---|---|---|---|
| `Id` | string | auto-generated | Unique ID with `cpt_` prefix |
| `Name` | string | `"Captain"` | Captain name |
| `Runtime` | [AgentRuntimeEnum](#agentruntimeenum) | `ClaudeCode` | Agent runtime type |
| `Model` | string? | null | Optional model override for this captain. When null, the runtime chooses its default model |
| `SystemInstructions` | string? | null | Per-captain system instructions injected into every mission prompt |
| `Tier` | string? | null | Capability tier (`Economy`, `Standard`, `Premium`); null classifies it from the model name |
| `PreferenceRank` | int | 0 | Preference rank within the tier; a higher rank is tried first |
| `State` | [CaptainStateEnum](#captainstateenum) | `Idle` | Current state |
| `CurrentMissionId` | string? | null | Currently assigned mission ID |
| `CurrentDockId` | string? | null | Currently assigned dock (worktree) ID |
| `ProcessId` | int? | null | OS process ID |
| `RecoveryAttempts` | int | 0 | Auto-recovery attempts for current mission |
| `LastHeartbeatUtc` | datetime? | null | Last heartbeat timestamp (UTC) |
| `CreatedUtc` | datetime | now | Creation timestamp (UTC) |
| `LastUpdateUtc` | datetime | now | Last update timestamp (UTC) |

---

#### Signal

A message between the admiral and captains.

```json
{
  "Id": "sig_abc123",
  "FromCaptainId": "cpt_abc123",
  "ToCaptainId": null,
  "Type": "Progress",
  "Payload": "Mission msn_abc123 transitioned to Testing",
  "Read": false,
  "CreatedUtc": "2026-03-07T12:00:00Z"
}
```

| Field | Type | Default | Description |
|---|---|---|---|
| `Id` | string | auto-generated | Unique ID with `sig_` prefix |
| `FromCaptainId` | string? | null | Sender captain ID (null = from Admiral) |
| `ToCaptainId` | string? | null | Recipient captain ID (null = to Admiral) |
| `Type` | [SignalTypeEnum](#signaltypeenum) | `Nudge` | Signal type |
| `Payload` | string? | null | Message payload |
| `Read` | bool | false | Whether signal has been read |
| `CreatedUtc` | datetime | now | Creation timestamp (UTC) |

---

#### ArmadaEvent

A recorded event representing a state change in the system.

```json
{
  "Id": "evt_abc123",
  "EventType": "mission.status_changed",
  "EntityType": "mission",
  "EntityId": "msn_abc123",
  "CaptainId": "cpt_abc123",
  "MissionId": "msn_abc123",
  "VesselId": "vsl_abc123",
  "VoyageId": "vyg_abc123",
  "Message": "Mission msn_abc123 transitioned to Complete",
  "Payload": null,
  "CreatedUtc": "2026-03-07T12:00:00Z"
}
```

| Field | Type | Default | Description |
|---|---|---|---|
| `Id` | string | auto-generated | Unique ID with `evt_` prefix |
| `EventType` | string | `""` | Event type identifier |
| `EntityType` | string? | null | Related entity type |
| `EntityId` | string? | null | Related entity ID |
| `CaptainId` | string? | null | Related captain ID |
| `MissionId` | string? | null | Related mission ID |
| `VesselId` | string? | null | Related vessel ID |
| `VoyageId` | string? | null | Related voyage ID |
| `Message` | string | `""` | Human-readable event message |
| `Payload` | string? | null | JSON payload with additional details |
| `CreatedUtc` | datetime | now | Event timestamp (UTC) |

**Known Event Types:**
- `mission.created` - Mission was created
- `mission.status_changed` - Mission status transitioned
- `mission.completed` - Mission completed successfully
- `mission.failed` - Mission failed
- `captain.launched` - Captain agent process started
- `captain.stopped` - Captain agent process stopped
- `captain.stalled` - Captain detected as stalled
- `voyage.created` - Voyage was created
- `voyage.dispatched` - A dispatch created the voyage and all of its missions
- `voyage.completed` - All missions in voyage completed
- `voyage.deleted` - Voyage permanently deleted

---

#### MergeEntry

An entry in the merge queue representing a branch to be tested and merged.

```json
{
  "Id": "mrg_abc123",
  "MissionId": "msn_abc123",
  "VesselId": "vsl_abc123",
  "BranchName": "armada/msn_abc123",
  "TargetBranch": "main",
  "Status": "Queued",
  "Priority": 0,
  "BatchId": null,
  "TestCommand": "dotnet test",
  "TestOutput": null,
  "TestExitCode": null,
  "CreatedUtc": "2026-03-07T12:00:00Z",
  "LastUpdateUtc": "2026-03-07T12:00:00Z",
  "TestStartedUtc": null,
  "CompletedUtc": null
}
```

| Field | Type | Default | Description |
|---|---|---|---|
| `Id` | string | auto-generated | Unique ID with `mrg_` prefix |
| `MissionId` | string? | null | Parent mission ID |
| `VesselId` | string? | null | Vessel ID |
| `BranchName` | string | `"unknown"` | Branch to merge |
| `TargetBranch` | string | `"main"` | Target branch |
| `Status` | [MergeStatusEnum](#mergestatusenum) | `Queued` | Current status |
| `Priority` | int | 0 | Queue priority (lower = higher) |
| `BatchId` | string? | null | Batch ID during batch testing |
| `TestCommand` | string? | null | Test command for verification |
| `TestOutput` | string? | null | Test output or error message |
| `TestExitCode` | int? | null | Test process exit code |
| `CreatedUtc` | datetime | now | Creation timestamp (UTC) |
| `LastUpdateUtc` | datetime | now | Last update timestamp (UTC) |
| `TestStartedUtc` | datetime? | null | Test start timestamp (UTC) |
| `CompletedUtc` | datetime? | null | Completion timestamp (UTC) |

---

#### ArmadaStatus

Aggregate status summary returned by the status endpoint.

```json
{
  "TotalCaptains": 5,
  "IdleCaptains": 2,
  "WorkingCaptains": 3,
  "StalledCaptains": 0,
  "ActiveVoyages": 1,
  "MissionsWaitingForResourcePressure": 0,
  "MissionsByStatus": {
    "Pending": 3,
    "InProgress": 2,
    "Complete": 10
  },
  "Voyages": [
    {
      "Voyage": { ... },
      "TotalMissions": 5,
      "CompletedMissions": 3,
      "FailedMissions": 0,
      "InProgressMissions": 2
    }
  ],
  "RecentSignals": [],
  "RemoteTunnel": {
    "Enabled": false,
    "State": "Disabled",
    "TunnelUrl": null,
    "InstanceId": "armada-1f2e3d4c5b6a",
    "LastError": null,
    "ReconnectAttempts": 0,
    "LatencyMs": null,
    "CapabilityManifest": {
      "ProtocolVersion": "2026-04-03",
      "ArmadaVersion": "0.9.0",
      "Features": [
        "remoteControl.handshake",
        "remoteControl.heartbeat",
        "status.health",
        "status.snapshot",
        "settings.remoteControl"
      ]
    }
  },
  "TimestampUtc": "2026-03-07T12:00:00Z"
}
```

| Field | Type | Description |
|---|---|---|
| `TotalCaptains` | int | Total registered captains |
| `IdleCaptains` | int | Number of idle captains |
| `WorkingCaptains` | int | Number of working captains |
| `StalledCaptains` | int | Number of stalled captains |
| `ActiveVoyages` | int | Total active voyages |
| `MissionsByStatus` | dict\<string, int\> | Mission counts grouped by status |
| `Voyages` | array | Active [VoyageProgress](#voyageprogress) objects |
| `RecentSignals` | array | Recent [Signal](#signal) objects |
| `RemoteTunnel` | [RemoteTunnelStatus](#remotetunnelstatus) | Current outbound remote tunnel status |
| `TimestampUtc` | datetime | Snapshot timestamp (UTC) |

---

#### RemoteTunnelStatus

Current outbound remote tunnel status and telemetry.

| Field | Type | Description |
|---|---|---|
| `Enabled` | bool | Whether the remote tunnel feature is enabled |
| `State` | string | Tunnel state (`Disabled`, `Disconnected`, `Connecting`, `Connected`, `Error`, `Stopping`) |
| `TunnelUrl` | string? | Configured or normalized websocket endpoint |
| `InstanceId` | string? | Stable instance identifier advertised during handshake |
| `LastConnectAttemptUtc` | datetime? | Most recent connection attempt |
| `ConnectedUtc` | datetime? | Timestamp when the current/last successful connection was established |
| `LastHeartbeatUtc` | datetime? | Last heartbeat or inbound tunnel activity timestamp |
| `LastDisconnectUtc` | datetime? | Most recent disconnect timestamp |
| `LastError` | string? | Last recorded tunnel error |
| `ReconnectAttempts` | int | Consecutive reconnect attempts since the last successful connection |
| `LatencyMs` | int? | Round-trip latency from the last successful ping/pong |
| `CapabilityManifest` | object | Current handshake capability manifest |

---

#### VoyageProgress

Progress information for an active voyage, nested in ArmadaStatus.

| Field | Type | Description |
|---|---|---|
| `Voyage` | [Voyage](#voyage) | Voyage details |
| `TotalMissions` | int | Total missions in voyage |
| `CompletedMissions` | int | Number of completed missions |
| `FailedMissions` | int | Number of failed missions |
| `InProgressMissions` | int | Number of in-progress missions |

---

#### Dock

A git worktree provisioned for a captain. Docks are managed internally by the Admiral and are not directly created via API.

| Field | Type | Default | Description |
|---|---|---|---|
| `Id` | string | auto-generated | Unique ID with `dck_` prefix |
| `VesselId` | string | `""` | Vessel ID |
| `CaptainId` | string? | null | Captain currently using dock |
| `WorktreePath` | string? | null | Local filesystem path to worktree |
| `BranchName` | string? | null | Branch name checked out |
| `Active` | bool | true | Whether dock is active/usable |
| `CreatedUtc` | datetime | now | Creation timestamp (UTC) |
| `LastUpdateUtc` | datetime | now | Last update timestamp (UTC) |

`GitAnchorsSnapshot` is nullable, versioned context evidence. Version 1 records
`DockId`, `MissionId`, `VesselId`, `ProvisionedCommit`, `ProvisionedUtc`, optional
`ResolvedUtc`, `State` (`Seeded`, `Complete`, `Incomplete`), bounded `Anchors`,
`Truncated` and a sanitized `ErrorCode`. Missing or invalid older data returns
null. It does not replace start-ref, stage-base or landing checks. See
[the snapshot contract](reference/backend-anchors.md).

---

#### Playbook

A reusable tenant-scoped markdown document selected during dispatch.

| Field | Type | Description |
|---|---|---|
| `Id` | string | Playbook ID (prefix `pbk_`) |
| `TenantId` | string \| null | Owning tenant |
| `UserId` | string \| null | Owning user |
| `FileName` | string | Markdown file name, typically ending in `.md` |
| `Description` | string \| null | Human-readable description |
| `Content` | string | Markdown body |
| `Active` | bool | Whether the playbook is available for new selections |
| `CreatedUtc` | datetime | Creation timestamp |
| `LastUpdateUtc` | datetime | Last update timestamp |

---

#### SelectedPlaybook

Playbook selection metadata stored on a voyage or mission request.

| Field | Type | Description |
|---|---|---|
| `PlaybookId` | string | Selected playbook ID |
| `DeliveryMode` | [PlaybookDeliveryModeEnum](#playbookdeliverymodeenum) | How the playbook is delivered to the model |

---

#### MissionPlaybookSnapshot

Immutable mission-time snapshot of a selected playbook.

| Field | Type | Description |
|---|---|---|
| `PlaybookId` | string \| null | Source playbook ID |
| `FileName` | string | Source file name |
| `Description` | string \| null | Source description |
| `Content` | string | Frozen markdown body used for this mission |
| `DeliveryMode` | [PlaybookDeliveryModeEnum](#playbookdeliverymodeenum) | Resolved delivery mode |
| `ResolvedPath` | string \| null | Absolute runtime path when the playbook is materialized as a file |
| `WorktreeRelativePath` | string \| null | Relative dock path when attached into the worktree |
| `SourceLastUpdateUtc` | datetime | Source playbook update timestamp captured into the snapshot |

---

#### WorkspaceExecRequest

A request to run a shell command in a vessel's workspace.

| Field | Type | Description |
|---|---|---|
| `Command` | string | The command line to execute via the platform shell |
| `TimeoutSeconds` | int | Timeout in seconds before the process tree is killed (clamped to [1, 600]) |

---

#### WorkspaceExecResult

The result of running a shell command in a vessel's workspace.

| Field | Type | Description |
|---|---|---|
| `Command` | string | The command that was executed |
| `WorkingDirectory` | string | The working directory the command ran in |
| `ExitCode` | int | Process exit code (-1 when timed out or failed to start) |
| `Stdout` | string | Captured standard output (truncated to a safe maximum) |
| `Stderr` | string | Captured standard error (truncated to a safe maximum) |
| `TimedOut` | bool | Whether the command was killed for exceeding its timeout |
| `DurationMs` | double | Wall-clock duration in milliseconds |

---

#### WorkspaceDiffResult

A unified git diff of a vessel's working tree against HEAD.

| Field | Type | Description |
|---|---|---|
| `Path` | string \| null | The path the diff was scoped to, or null for the whole tree |
| `Diff` | string | The unified diff text |
| `Error` | string \| null | An error message when the diff could not be produced |

---

#### InboxItem

A single actionable item in the operator's "needs you" inbox.

| Field | Type | Description |
|---|---|---|
| `Kind` | string | Machine-readable kind (e.g. `review`, `landing_failed`, `failed`, `stalled_captain`) |
| `Severity` | [InboxSeverityEnum](#inboxseverityenum) | Severity of the item |
| `Title` | string | Human-readable title |
| `Detail` | string | Additional detail |
| `EntityType` | string \| null | The referenced entity type (e.g. `mission`, `captain`) |
| `EntityId` | string \| null | The referenced entity id |
| `Href` | string | A relative dashboard path that takes the operator to the item |

---

### Enumerations

All enumerations serialize as strings in JSON (e.g., `"InProgress"`, not `2`).

#### MissionStatusEnum

| Value | Description |
|---|---|
| `Pending` | Created but not yet assigned to a captain |
| `Assigned` | Assigned to a captain, awaiting work start |
| `InProgress` | Captain is actively working |
| `WorkProduced` | Agent exited successfully; work ready for landing |
| `PullRequestOpen` | Pull request created, awaiting merge confirmation |
| `Testing` | Work complete, under automated testing |
| `Review` | Awaiting human review |
| `Complete` | Successfully completed — code landed (terminal) |
| `Failed` | Mission failed (terminal) |
| `LandingFailed` | Landing (merge/PR) failed; may be retried |
| `Cancelled` | Mission cancelled (terminal) |

---

#### LandingModeEnum

| Value | Description |
|---|---|
| `LocalMerge` | Merge branch into default branch locally and push |
| `PullRequest` | Create a pull request and poll for merge confirmation |
| `MergeQueue` | Enqueue the branch into Armada's merge queue |
| `None` | No automated landing; leave work on the branch |

---

#### BranchCleanupPolicyEnum

| Value | Description |
|---|---|
| `LocalOnly` | Delete the local branch after landing |
| `LocalAndRemote` | Delete both local and remote branches after landing |
| `None` | Do not delete branches after landing |

---

#### VoyageStatusEnum

| Value | Description |
|---|---|
| `Open` | Created, missions being set up |
| `InProgress` | Has active missions in progress |
| `Complete` | All missions completed |
| `Cancelled` | Voyage was cancelled |

---

#### PlaybookDeliveryModeEnum

| Value | Description |
|---|---|
| `InlineFullContent` | Include the entire markdown body directly in the rendered mission instructions |
| `InstructionWithReference` | Materialize the playbook outside the worktree and instruct the model to read the resolved path |
| `AttachIntoWorktree` | Materialize the playbook under the dock worktree and instruct the model to read it there |

---

#### CaptainStateEnum

| Value | Description |
|---|---|
| `Idle` | Available for assignment |
| `Working` | Actively working on a mission |
| `Stalled` | Process appears stalled (no heartbeat) |
| `Stopping` | In the process of stopping |

---

#### AgentRuntimeEnum

| Value | Description |
|---|---|
| `ClaudeCode` | Anthropic Claude Code CLI |
| `Codex` | OpenAI Codex CLI |
| `Gemini` | Google Gemini CLI |
| `Cursor` | Cursor agent CLI |
| `Custom` | Custom agent runtime |

---

#### SignalTypeEnum

| Value | Description |
|---|---|
| `Assignment` | Mission assignment notification |
| `Progress` | Progress update from captain |
| `Completion` | Mission completion notification |
| `Error` | Error notification |
| `Heartbeat` | Heartbeat signal |
| `Nudge` | Ephemeral nudge message |
| `Mail` | Persistent mail message |

---

#### MergeStatusEnum

| Value | Description |
|---|---|
| `Queued` | Waiting to be picked up |
| `Testing` | Currently being tested |
| `Passed` | Tests passed, ready to land |
| `Failed` | Tests failed |
| `Landed` | Successfully merged into target branch |
| `Cancelled` | Removed from queue |

---

#### InboxSeverityEnum

| Value | Description |
|---|---|
| `Info` | Informational; no urgent action required |
| `Warning` | Something needs attention |
| `Critical` | Something is blocking progress and needs prompt action |

---

#### EnumerationOrderEnum

| Value | Description |
|---|---|
| `CreatedAscending` | Sort by creation date, oldest first |
| `CreatedDescending` | Sort by creation date, newest first (default) |

---

### Request Types

#### EnumerationQuery

Query parameters for paginated enumeration. Used as the POST body for all `/enumerate` endpoints.

```json
{
  "PageNumber": 1,
  "PageSize": 25,
  "Order": "CreatedDescending",
  "CreatedAfter": "2026-03-01T00:00:00Z",
  "CreatedBefore": null,
  "Status": "InProgress",
  "FleetId": null,
  "VesselId": "vsl_abc123",
  "CaptainId": null,
  "VoyageId": null,
  "MissionId": null,
  "EventType": null,
  "SignalType": null,
  "ToCaptainId": null,
  "UnreadOnly": null
}
```

All fields are optional. Omitted fields use defaults. See [Pagination](#pagination) for full details.

---

#### VoyageRequest

Request body for creating a voyage with missions.

```json
{
  "Title": "API Hardening",
  "Description": "Security improvements",
  "VesselId": "vsl_abc123",
  "SelectedPlaybooks": [
    {"PlaybookId": "pbk_abc123", "DeliveryMode": "InlineFullContent"}
  ],
  "Pipeline": "FullPipeline",
  "Missions": [
    {"Title": "Add rate limiting", "Description": "Add rate limiting middleware"},
    {"Title": "Add input validation", "Description": "Validate all POST endpoints"}
  ]
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `Title` | string | yes | Voyage title |
| `Description` | string | no | Voyage description |
| `VesselId` | string | yes | Target vessel ID |
| `Missions` | array | no | List of MissionRequest objects |
| `SelectedPlaybooks` | array | no | Ordered [SelectedPlaybook](#selectedplaybook) rows to apply to the voyage |
| `PipelineId` | string | no | Pipeline ID override |
| `Pipeline` | string | no | Pipeline name override |

---

#### MissionRequest

A mission within a VoyageRequest.

| Field | Type | Required | Description |
|---|---|---|---|
| `Title` | string | yes | Mission title |
| `Description` | string | no | Mission description/instructions |

---

#### StatusTransitionRequest

Request body for transitioning a mission status.

```json
{
  "Status": "InProgress"
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `Status` | string | yes | Target status name (case-insensitive) |

---

### Response Wrappers

#### EnumerationResult\<T\>

Paginated result wrapper returned by all list and enumerate endpoints.

```json
{
  "Success": true,
  "PageNumber": 1,
  "PageSize": 25,
  "TotalPages": 4,
  "TotalRecords": 87,
  "Objects": [ ... ],
  "TotalMs": 3.14
}
```

| Field | Type | Description |
|---|---|---|
| `Success` | bool | Whether the operation succeeded |
| `PageNumber` | int | Current page number (1-based) |
| `PageSize` | int | Number of items per page |
| `TotalPages` | int | Total number of pages |
| `TotalRecords` | long | Total records matching the query |
| `Objects` | array\<T\> | Result objects for this page |
| `TotalMs` | double | Query execution time in milliseconds |

---

#### VoyageDetail

Response from `GET /api/v1/voyages/{id}`.

```json
{
  "Voyage": { ... },
  "Missions": [ ... ]
}
```

| Field | Type | Description |
|---|---|---|
| `Voyage` | [Voyage](#voyage) | Voyage details |
| `Missions` | array\<[Mission](#mission)\> | All missions in this voyage |

---

#### MissionDiff

Response from `GET /api/v1/missions/{id}/diff`.

```json
{
  "MissionId": "msn_abc123",
  "Branch": "armada/msn_abc123",
  "Diff": "diff --git ..."
}
```

| Field | Type | Description |
|---|---|---|
| `MissionId` | string | Mission ID |
| `Branch` | string | Branch name |
| `Diff` | string | Git diff output |

---

#### MissionLog

Response from `GET /api/v1/missions/{id}/log`.

```json
{
  "MissionId": "msn_abc123",
  "Log": "line1\nline2\n...",
  "Lines": 100,
  "TotalLines": 542
}
```

| Field | Type | Description |
|---|---|---|
| `MissionId` | string | Mission ID |
| `Log` | string | Log content (newline-delimited) |
| `Lines` | integer | Number of lines returned |
| `TotalLines` | integer | Total lines in log file |

---

#### CaptainLog

Response from `GET /api/v1/captains/{id}/log`.

```json
{
  "CaptainId": "cpt_abc123",
  "Log": "line1\nline2\n...",
  "Lines": 100,
  "TotalLines": 203
}
```

| Field | Type | Description |
|---|---|---|
| `CaptainId` | string | Captain ID |
| `Log` | string | Log content (newline-delimited) |
| `Lines` | integer | Number of lines returned |
| `TotalLines` | integer | Total lines in log file |

---

## Endpoint Summary

| # | Method | URL | Description | Auth |
|---|---|---|---|---|
| 1 | POST | `/api/v1/authenticate` | Authenticate (get session token) | No |
| 2 | GET | `/api/v1/whoami` | Get current identity | Yes |
| 3 | POST | `/api/v1/tenants/lookup` | Lookup tenants by email | No |
| 4 | POST | `/api/v1/onboarding` | Self-register new user | No* |
| 5 | GET | `/api/v1/tenants` | List tenants (paginated) | Admin |
| 6 | POST | `/api/v1/tenants` | Create tenant | Admin |
| 7 | GET | `/api/v1/tenants/{id}` | Get tenant | Yes** |
| 8 | PUT | `/api/v1/tenants/{id}` | Update tenant | Admin |
| 9 | DELETE | `/api/v1/tenants/{id}` | Delete tenant | Admin |
| 10 | GET | `/api/v1/users` | List users (paginated) | Admin |
| 11 | POST | `/api/v1/users` | Create user | Admin |
| 12 | GET | `/api/v1/users/{id}` | Get user | Yes** |
| 13 | PUT | `/api/v1/users/{id}` | Update user | Admin |
| 14 | DELETE | `/api/v1/users/{id}` | Delete user | Admin |
| 15 | GET | `/api/v1/credentials` | List credentials (paginated) | Yes** |
| 16 | POST | `/api/v1/credentials` | Create credential | Yes** |
| 17 | GET | `/api/v1/credentials/{id}` | Get credential | Yes** |
| 18 | PUT | `/api/v1/credentials/{id}` | Update credential | Yes** |
| 19 | DELETE | `/api/v1/credentials/{id}` | Delete credential | Yes** |
| 20 | GET | `/api/v1/status` | System status dashboard | Yes |
| 21 | GET | `/api/v1/status/health` | Health check | No |
| 22 | POST | `/api/v1/server/stop` | Graceful shutdown | \*\*\* |
| 23 | POST | `/api/v1/server/restart` | Graceful restart (stop; supervisor relaunches) | \*\*\* |
| 24 | GET | `/api/v1/fleets` | List fleets (paginated) | Yes |
| 25 | POST | `/api/v1/fleets/enumerate` | Enumerate fleets | Yes |
| 26 | POST | `/api/v1/fleets` | Create fleet | Yes |
| 27 | GET | `/api/v1/fleets/{id}` | Get fleet | Yes |
| 28 | PUT | `/api/v1/fleets/{id}` | Update fleet | Yes |
| 29 | DELETE | `/api/v1/fleets/{id}` | Delete fleet | Yes |
| 30 | GET | `/api/v1/vessels` | List vessels (paginated) | Yes |
| 31 | POST | `/api/v1/vessels/enumerate` | Enumerate vessels | Yes |
| 32 | POST | `/api/v1/vessels` | Create vessel | Yes |
| 33 | GET | `/api/v1/vessels/{id}` | Get vessel | Yes |
| 34 | PUT | `/api/v1/vessels/{id}` | Update vessel | Yes |
| 35 | DELETE | `/api/v1/vessels/{id}` | Delete vessel | Yes |
| 36 | GET | `/api/v1/voyages` | List voyages (paginated) | Yes |
| 37 | POST | `/api/v1/voyages/enumerate` | Enumerate voyages | Yes |
| 38 | POST | `/api/v1/voyages` | Create voyage with missions | Yes |
| 39 | GET | `/api/v1/voyages/{id}` | Get voyage with missions | Yes |
| 41a | GET | `/api/v1/voyages/{id}/mission-summary` | Scoped status counts and paged vessel IDs | Yes |
| 40 | DELETE | `/api/v1/voyages/{id}` | Cancel voyage | Yes |
| 41 | DELETE | `/api/v1/voyages/{id}/purge` | Permanently delete voyage | Yes |
| 42 | GET | `/api/v1/missions` | List missions (paginated) | Yes |
| 43 | POST | `/api/v1/missions/enumerate` | Enumerate missions | Yes |
| 44 | POST | `/api/v1/missions` | Create mission | Yes |
| 45 | GET | `/api/v1/missions/{id}` | Get mission | Yes |
| 46 | PUT | `/api/v1/missions/{id}` | Update mission | Yes |
| 47 | PUT | `/api/v1/missions/{id}/status` | Transition mission status | Yes |
| 48 | DELETE | `/api/v1/missions/{id}` | Cancel mission | Yes |
| 49 | POST | `/api/v1/missions/{id}/restart` | Restart failed/cancelled mission | Yes |
| 50 | GET | `/api/v1/missions/{id}/diff` | Get mission diff | Yes |
| 51 | GET | `/api/v1/missions/{id}/log` | Get mission log | Yes |
| 52 | GET | `/api/v1/captains` | List captains (paginated) | Yes |
| 53 | POST | `/api/v1/captains/enumerate` | Enumerate captains | Yes |
| 54 | POST | `/api/v1/captains` | Create captain | Yes |
| 55 | GET | `/api/v1/captains/{id}` | Get captain | Yes |
| 56 | PUT | `/api/v1/captains/{id}` | Update captain | Yes |
| 57 | POST | `/api/v1/captains/{id}/stop` | Stop captain | Yes |
| 58 | POST | `/api/v1/captains/stop-all` | Stop all captains | Yes |
| 59 | GET | `/api/v1/captains/{id}/log` | Get captain current log | Yes |
| 60 | DELETE | `/api/v1/captains/{id}` | Delete captain | Yes |
| 61 | GET | `/api/v1/signals` | List signals (paginated) | Yes |
| 62 | POST | `/api/v1/signals/enumerate` | Enumerate signals | Yes |
| 63 | POST | `/api/v1/signals` | Send signal | Yes |
| 64 | GET | `/api/v1/events` | List events (paginated) | Yes |
| 65 | POST | `/api/v1/events/enumerate` | Enumerate events | Yes |
| 66 | GET | `/api/v1/merge-queue` | List merge queue (paginated) | Yes |
| 67 | POST | `/api/v1/merge-queue/enumerate` | Enumerate merge queue | Yes |
| 68 | POST | `/api/v1/merge-queue` | Enqueue branch | Yes |
| 69 | GET | `/api/v1/merge-queue/{id}` | Get merge entry | Yes |
| 70 | DELETE | `/api/v1/merge-queue/{id}` | Cancel merge entry | Yes |
| 71 | POST | `/api/v1/merge-queue/process` | Process merge queue | Yes |
| 72 | GET | `/api/v1/vessels/{vesselId}/code-index/status` | Get code-index status | Yes |
| 73 | POST | `/api/v1/vessels/{vesselId}/code-index/update` | Refresh code index and graph sidecars | Yes |
| 74 | POST | `/api/v1/vessels/{vesselId}/code-index/search` | Search indexed code chunks | Yes |
| 75 | POST | `/api/v1/vessels/{vesselId}/code-index/search-symbols` | Search code graph symbols | Yes |
| 76 | POST | `/api/v1/vessels/{vesselId}/code-index/callers` | Resolve direct symbol callers | Yes |
| 77 | POST | `/api/v1/vessels/{vesselId}/code-index/callees` | Resolve direct symbol callees | Yes |
| 78 | POST | `/api/v1/vessels/{vesselId}/code-index/node` | Resolve one graph node | Yes |
| 79 | POST | `/api/v1/vessels/{vesselId}/code-index/files` | List graph files and symbols | Yes |
| 80 | POST | `/api/v1/vessels/{vesselId}/code-index/explore` | Explore a bounded graph neighborhood | Yes |
| 81 | POST | `/api/v1/vessels/{vesselId}/code-index/impact` | Traverse symbol impact | Yes |
| 82 | POST | `/api/v1/vessels/{vesselId}/code-index/affected-tests` | Suggest affected tests | Yes |
| 83 | GET | `/api/v1/vessels/{id}/readiness` | Vessel readiness summary | Yes |
| 84 | GET | `/api/v1/vessels/{id}/landing-preview` | Vessel landing preview | Yes |
| 85 | GET | `/api/v1/missions/{id}/landing-preview` | Mission landing preview | Yes |
| 86 | POST | `/api/v1/docks/{id}/repair` | Repair a dock worktree | Yes |
| 87 | POST | `/api/v1/docks/{id}/unstick` | Release a held captain and reclaim the dock | Yes |
| 88 | GET | `/api/v1/planning-sessions` | List planning sessions | Yes |
| 89 | POST | `/api/v1/planning-sessions` | Create a planning session | Yes |
| 90 | GET | `/api/v1/planning-sessions/{id}` | Get a planning session | Yes |
| 91 | POST | `/api/v1/planning-sessions/{id}/messages` | Send a planning turn | Yes |
| 92 | POST | `/api/v1/planning-sessions/{id}/summarize` | Summarize planning into a draft | Yes |
| 93 | POST | `/api/v1/planning-sessions/{id}/dispatch` | Dispatch a voyage from planning | Yes |
| 94 | POST | `/api/v1/planning-sessions/{id}/stop-turn` | Abort the in-flight planning turn | Yes |
| 95 | POST | `/api/v1/planning-sessions/{id}/stop` | Stop a planning session | Yes |
| 96 | DELETE | `/api/v1/planning-sessions/{id}` | Delete a planning session | Yes |
| 97 | GET | `/api/v1/missions/{id}/definition-of-done` | Mission definition-of-done configuration and latest evaluation | Yes |
| 98 | GET | `/api/v1/missions/{id}/recovery` | Mission recovery counters, rescues, incidents and recovery events | Yes |
| 99 | GET | `/api/v1/missions/{id}/auto-land` | Mission auto-land predicate, latest decision and merge entry audit | Yes |
| 100 | GET | `/api/v1/vessels/{id}/branches` | List vessel branches and write-control availability | Yes |
| 101 | POST | `/api/v1/vessels/{id}/branches/push` | Push a landing-repository branch to origin without force (tenant administrator) | Yes |
| 102 | POST | `/api/v1/vessels/{id}/branches/merge` | Fast-forward or merge-commit landing-repository branches (tenant administrator) | Yes |

This table is a quick route index, not the complete contract. Use `/openapi.json` or `/swagger` for the live REST surface.

\* Gated by `AllowSelfRegistration` setting.
\*\* Non-admin users are scoped to their own records only.
\*\*\* NoAuthRequired by default; requires global admin (`IsAdmin = true`) when `RequireAuthForShutdown` is `true`.

---

## Additional Ports

| Service | Default Port | Description |
|---|---|---|
| Admiral REST API | 7890 | This API (WebSocket available at /ws on the same port) |
| MCP Server | 7891 | Official MCP C# SDK Streamable HTTP transport for AI tool use |

## CORS

All responses include permissive CORS headers:
```
Access-Control-Allow-Origin: *
Access-Control-Allow-Methods: GET, POST, PUT, DELETE, OPTIONS
Access-Control-Allow-Headers: Content-Type, Authorization, X-Token, X-Api-Key
```
## Account usage and persona routing

See [Smart Routing](USAGE_ROUTING.md) for the opt-in policy (usage filter,
persona model lists, the `capacity_escalation` decision, route restrictions),
collectors, credential references, and Dashboard controls. The settings REST API
exposes `providerUsage`; `POST /api/v1/settings/usage-preview` previews the
Legacy Routing order, usage verdicts, model groups, capacity reading, and chosen
captain for a saved or draft policy with settings write permission.

`POST /api/v1/settings/usage-preview` request fields: `persona`, `priority`,
`preferredModel`, `missionTitle`, `missionText`, `usageRouting` (draft). The
typed-decision client is called only when `missionTitle` or `missionText` is
present. Response fields: `reason`, `smartRoutingEnabled`, `hasPersonaRoutes`,
`hasPersonaModels`, `legacyOrder`, `usageFilter`, `modelGroups`, `capacity`
(`choice`, `source`, `asked`), `candidates`, `chosen`, `accounts`, `warnings`,
`scope`. Each `usageFilter` verdict names the `layer` that decided it:
`eligibility` (persona lock or tier floor), `routes`, or `usage`. See [the preview table](USAGE_ROUTING.md#dashboard-and-api). No new MCP tool is
required. Policy updates use `PUT /api/v1/settings` and hot-reload.

### Typed decisions

Administrator only (settings write permission). These routes are never recorded
in request history.

| Method | Path | Body | Response |
| --- | --- | --- | --- |
| `GET` | `/api/v1/typed-decisions` | none | `effectiveMode`, `effectiveReason` (`typed_decisions_no_key` without a key), `storedMode`, `keyPresent`, `keySource` (`env` or `file`), `decisions[]` with `key`, `mode`, `threshold`, `description` |
| `PUT` | `/api/v1/typed-decisions` | `{ "mode"?: "Off"\|"Shadow"\|"Gate", "decisions"?: { "<name>": { "mode"?, "gateThreshold"? } } }` | The same status; 400 for an unknown decision, mode, or a threshold outside 0 to 1 |
| `PUT` | `/api/v1/typed-decisions/key` | `{ "apiKey": "..." }` | 204, no body. Writes `<data directory>/secrets/typesafe-api-key` (folder 0700, file 0600) |
| `DELETE` | `/api/v1/typed-decisions/key` | none | `fileRemoved`, `environmentSuppliesKey`, `keyPresent`, `keySource`, `effectiveMode`, `effectiveReason` |

The key is never returned, logged, or stored in settings. The environment
variable named by `typedDecisions.apiKeyEnv` wins over the file. See
[typed decisions](TYPED_DECISIONS.md).

### Subscription account logins

These routes log a subscription account in from the Dashboard. Each needs
settings write permission (a global administrator), like `PUT /api/v1/settings`;
a tenant administrator or ordinary user gets 403. `{accountId}` must be 1–64
letters, digits, hyphens, or underscores, starting with a letter or digit;
anything else returns 400 `account_id_invalid`. No route accepts a path. These
requests and responses are never recorded in request history.

| Method | Path | Body | Result |
|---|---|---|---|
| POST | `/api/v1/usage-accounts/{accountId}/login/home` | none | Creates `<data directory>/accounts/<accountId>` (mode 0700), idempotent. Returns `AccountId`, `HomeDirectory`, `CursorKeyFile`, `Created`. |
| POST | `/api/v1/usage-accounts/{accountId}/login/start` | none | Starts the runtime's browser login for the saved account and returns a login session with only `VerificationUrl` and `UserCode`. |
| POST | `/api/v1/usage-accounts/{accountId}/login/code` | `{ "code": "..." }` | Relays a pasted Claude Code sign-in code to the pending login. |
| POST | `/api/v1/usage-accounts/{accountId}/login/key` | `{ "apiKey": "..." }` | Stores an OpenCode or Cursor API key in the account folder. The key is never returned. |
| GET | `/api/v1/usage-accounts/{accountId}/login/status` | none | `Session` (last login since start) plus `LoginReady`, `LoginReason`, and `LoginCheckedUtc` from the server's login check. |
| POST | `/api/v1/usage-accounts/{accountId}/login/cancel` | none | Stops a pending login and returns the status. |
| DELETE | `/api/v1/usage-accounts/{accountId}` | none | Deletes the saved account. See below. |
| POST | `/api/v1/usage-accounts/{accountId}/refresh` | none | Reads the account's usage now and reruns its login check. See below. |

A login session has `SessionId`, `AccountId`, `Runtime`, `Method`
(`DeviceCode`, `PasteCode`, `ApiKey`), `State` (`Pending`, `Succeeded`,
`Failed`, `Expired`, `Cancelled`), a safe `Reason`, `NeedsCode`, `Reused`,
`StartedUtc`, `ExpiresUtc`, and `CompletedUtc`.

`start` and `key` need the account saved in `modelTier.usageRouting` with a
`runtime` (404 `account_not_configured`), and its `homeDirectory` or Cursor
`launchCredentialFile` must be the server-derived path (409
`account_login_target_not_managed`). A second `start` while a login is pending
returns that login with `Reused: true`. `code` with no pending paste-code login
returns 409 `account_login_not_waiting_for_code`; `key` while a login is
pending returns 409 `account_login_in_progress`. Failure reasons in a session
include `account_login_cli_unavailable`, `account_login_prompt_not_found`,
`account_login_process_failed`, `account_login_expired_before_completion`, and
`account_login_cancelled`. See [Account logins](USAGE_ROUTING.md#account-logins).

**Delete.** `DELETE /api/v1/usage-accounts/{accountId}` matches the saved
account ID exactly; an unknown ID returns 404 `account_not_found`. While the
account lists captains it returns 409 `account_has_captains` and changes
nothing: unassign the captains first, because a captain left on a deleted
account would launch with the shared login. Otherwise the server cancels a
pending login, removes the account and every `personaRoutes` entry naming it
(a persona whose list becomes empty is dropped), validates and saves settings
as `PUT /api/v1/settings` does (400 `account_delete_policy_invalid`, 500
`account_delete_save_failed`), forgets the account's usage and login-check
state, and deletes `<data directory>/accounts/<accountId>`. It never deletes any
other path. It emits `account.deleted` with the account ID. The response:

| Field | Meaning |
|---|---|
| `AccountId` | The deleted account. |
| `RoutesRemoved` | Persona route entries removed. |
| `PersonasRemoved` | Persona keys dropped because no route was left. |
| `LoginCancelled` | A pending login was cancelled. |
| `HomeDeleted` | The server-derived folder was deleted. |
| `HomeReason` | `account_home_deleted`, `account_home_not_found`, `account_home_not_managed` (the account's `homeDirectory` is another folder, which is left in place), or `account_home_delete_failed`. |

**Refresh.** `POST /api/v1/usage-accounts/{accountId}/refresh` reads one saved
account's usage now, bypassing `refreshIntervalMinutes`, and reruns its runtime
login check, waiting up to the probe timeout plus five seconds. An unknown ID
returns 404 `account_not_found`. While a provider retry-after from an earlier
429 is active, the provider is not called. Concurrent refreshes of one account
share one read. The response has `AccountId`, `Collected` (the usage was read),
`Reason` (`usage_refreshed`, `usage_refresh_rate_limited`,
`usage_refresh_manual_snapshot`, or the collection error code), `RetryAfterUtc`,
`LoginProbeRerun`, and `Status`: the account's `State`, `Reason`,
`ObservedUtc`, `Source`, `CollectionError`, `LoginCheckedUtc`,
`ExhaustedUntilUtc`, and `Windows`, as in `providerUsage`.
