# Upgrade to v1.0.0

Armada v1.0.0 is a release of this fork. Its version number is independent of
upstream. The [README adoption map](../README.md#upstream-vs-fork) records the
selected imports, adaptations and exclusions. The [changelog](../CHANGELOG.md#v100-2026-10-26)
records delivered changes. Preparing or building this release does not upgrade a
running Admiral.

## Release paths

Use the existing [Docker build and deployment path](DOCKER.md), or the local
publish scripts in `scripts/linux`, `scripts/macos` and `scripts/windows`.
Build the server and Dashboard from the same accepted source. The shared .NET
version, runtime product version, Helm package and Dashboard package are 1.0.0.
A version label alone does not prove that a running process contains that source;
check the build commit as well.

Hosted CI workflow publication remains held. This release does not add signed
installers, package-service publishing, mobile clients or a TUI. Local publish
scripts keep their existing framework options. The shared build supports .NET 8
and .NET 10; the repository gate runs all four test runners on Linux with .NET 10.

## Before the upgrade

1. Record the current source commit, image or published directory, settings and
   database provider. Retain the old executable and Dashboard as one rollback set.
2. Stop new dispatch through the existing scheduler and dispatch controls. Let
   active work finish, or account for each local and remote writer before stopping
   the Admiral. Preserve mission branches, docks and uncertain Harbor jobs.
3. Make a provider-native database backup. Keep settings, durable journals and
   required data files with it. Verify an isolated restore. A copied SQLite file
   does not back up a PostgreSQL, MySQL or SQL Server deployment.
4. Rehearse startup against an isolated restore with separate data roots and
   credentials. Prevent the rehearsal from dispatching work or using production
   runners. Verify schema migration, a second startup, representative records,
   authentication and the client contracts used by the deployment.
5. Record passing Checks at the exact candidate commit. Follow the existing
   [release and deployment workflow](DELIVERY_OPERATIONS.md) for approval,
   deployment evidence and verification.

Startup migration protection requires a native backup and verified isolated
restore before it upgrades an existing schema. Failure stops startup. Schema
versions differ by database provider; do not use the product version as a schema
number or run an old migration script because its filename looks similar.

## Compatibility and policy

- HTTP 501, 503 and 504 REST bodies keep their existing `Error` value and add `Code`
  (`NotImplemented`, `ServiceUnavailable` or `GatewayTimeout`). A WebSocket
  `command.error` frame for an unhandled command failure adds `code`. Invalid
  command JSON uses `invalid_argument`. HTTP status codes are unchanged.
- Legacy password rows remain readable and upgrade after successful login.
  New writes use salted PBKDF2. Do not restore weak password storage to downgrade.
- Existing Ask routes remain available. Saved threads, approvals and Mission
  actions add durable owner-scoped records. Interrupted or uncertain actions
  require inspection before retry; do not clear their claims to force a rerun.
- HTTP and standalone stdio list server-dependent MCP tools. In standalone stdio,
  a missing process service returns `Unavailable`. Reconnect MCP clients after
  upgrading so they refresh the catalog. Mission credentials retain their scope.
- Existing Harbor process-only routes retain Admiral-owned checkout operations.
  Harbor-owned checkouts are opt-in. Update the runner with the matching source
  before using new checkout operations. An offline or uncertain checkout owner
  holds work instead of falling back to another host.
- Restart adoption verifies the persisted owner, process identity and output
  replay. Uncertain jobs stay fenced from replacement. A reconnect is not proof
  that an old writer has stopped.
- Architect JSON plans explicitly select version 2 or 3. Their integration
  barrier waits for every accepted branch to land on the current target. Legacy
  marked and numbered plans remain supported; ambiguous graphs and unversioned
  JSON do not gain an automatic compatibility fallback.
- Smart Routing, exact-commit Checks, consumer verification, local landing,
  incident recovery, rescue budgets and verified history archives keep the
  fork's rules. Review loaded settings rather than assuming upstream defaults.
- The administrator storage inventory adds a `storage` settings section and
  `storage-history.json` in the data directory. It needs no database migration.
  See [Storage Inventory](STORAGE_INVENTORY.md). The settings watcher and disk
  lifecycle scans are separate features.

## Seeded default credentials

A new database seeds the admin `admin@armada` with a published default password
and a `default` bearer token. The Admiral now checks them before the REST
listener binds.

- **Admin password already changed.** The server starts as before. It
  deactivates the stored `default` bearer credential, and the token stops
  authenticating. Scripts, Helm (`armada server stop`) and any client that
  still sends `Authorization: Bearer default` need a real credential first:
  create one in the Dashboard and update the client (Helm's `ApiKey`). Do this
  before the upgrade. A rollback to the previous executable does not reactivate
  the credential; the restored pre-upgrade database does.
- **Default password still active, loopback `rest.hostname`** (`localhost`,
  `127.0.0.1`, `::1`). The server starts and logs a warning at every start.
- **Default password still active, any other `rest.hostname`** (including
  `0.0.0.0`, as in the Docker stack). The server exits with code 4 and a message
  that names the active credentials. Resolve it with one of:
  1. Set `ARMADA_INITIAL_ADMIN_PASSWORD` (at least 8 characters, not `password`).
     It applies only while the admin still has the default password.
  2. Change the admin password first: start on `localhost` and call
     `PUT /api/v1/account/password`, or change it in the Dashboard.
  3. Set `AllowDefaultCredentialsOnNetwork` to `true` in settings. Startup then
     logs a warning instead of refusing. This accepts that anyone who can reach
     the port can sign in with the published defaults.
- **Other tenants.** A tenant admin `admin@armada` that was seeded earlier with
  the default password also counts as an active default. Change those passwords
  (an administrator resets them through the user update route) before the
  non-loopback start.

- **Tenant creation.** `POST /api/v1/tenants` no longer seeds the tenant admin
  with the default password. A client that creates a tenant and then signs in as
  `admin@armada` with `password` must send `AdminPassword` in the request, or read
  the generated `AdminPassword` from the response (returned once). Tenant admins
  created earlier keep their passwords.
- **Self-service password change.** `PUT /api/v1/users/{id}` for the caller's own
  record requires `CurrentPassword` when the request changes the password.
  Administrative resets of another user and requests that use the configured
  `ApiKey` are unchanged. The Dashboard Users modal asks for the current password
  when a user edits their own record and sends it with the new one; it shows the
  server's `400` or `403` message when the change is refused. While the signed-in
  account still has the default password, the Dashboard shows a banner that links
  to this change. The banner does not block the session.
- **Tenant creation in the Dashboard.** The tenant form accepts an optional admin
  password. When it is blank, the server generates one and the Dashboard shows it
  once in a dialog, with a copy button, after the tenant is created. Closing the
  dialog discards it.

Run the upgrade rehearsal on the restored copy with the production
`rest.hostname` so this check runs before the cutover.

## Login limits and the remote proxy

- **Credential login limits.** `POST /api/v1/authenticate` locks an account or a
  client address after repeated failed email and password logins and returns
  `429` with `Retry-After`. The client address is the socket peer address, so
  behind a reverse proxy every client shares the proxy's address limit. Raise
  `loginRateLimit` values in settings for that layout. Bearer, API key and
  session tokens are not limited.
- **Proxy shared password.** The proxy refuses to start while its shared
  password is blank or the built-in default. Set `password` in the proxy
  settings file or `ARMADA_PROXY_PASSWORD` before the upgrade.
  `allowDefaultPassword` is for a throwaway local test only.
- **Proxy instance list and cookies.** `GET /proxy-api/v1/instances` requires a
  proxy session. The session cookie carries `Secure` when `secureCookie` is set,
  or when `trustForwardedHeaders` is set and the forwarded scheme is `https`.
  Behind a reverse proxy that overwrites `X-Forwarded-*`, set
  `trustForwardedHeaders` so the proxy limits and logs the real client address.

## Verify and roll back

After an approved deployment, check health, product version and build commit;
confirm database startup and representative scoped reads. Verify the Dashboard
and the MCP catalog, then test the configured local or Harbor execution path
under the deployment's normal Checks. Restore only the scheduler and dispatch
state that the operator authorized. Retain upgrade and verification evidence on
the release and deployment records.

If startup or verification fails, keep new dispatch stopped. Account for remote
writers before replacing the process. An older executable is not a supported
reader of a newer schema. Restore the verified pre-upgrade database together
with its matching executable, settings and Dashboard when rollback requires it.
Preserve any post-upgrade work and journals before restore; a database restore
can discard newer records. Record the rollback and its verification through the
existing deployment workflow.
