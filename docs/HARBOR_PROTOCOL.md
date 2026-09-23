# Harbor Link Protocol

The Harbor link is the WebSocket a Harbor runner opens to the Admiral. The Admiral sends work down the link;
the runner executes it and reports back. This document is the contract both sides implement. It is versioned by
`HarborProtocol.Version` (currently `1.0`); change that constant on any breaking change to the message set.

Harbor is disabled by default. When `Harbor.Enabled` is false the Admiral registers no link route and no
enrollment routes. See [Opt-in operation](#opt-in-operation).

## Transport and framing

The runner dials `Harbor.LinkPath` (default `/harbor/link`) on the Admiral's REST port. Because the runner is
the client, the link works from behind NAT and through the container's published port. Use TLS (`wss://`)
through the same TLS termination as the REST API whenever the link leaves the host.

Every frame is one UTF-8 JSON text message carrying exactly one `HarborMessage`. Both ends use
`Armada.Core.Harbor.HarborProtocol`: camelCase properties, enums as strings, null properties omitted, and a
`type` discriminator written first. A binary, empty, malformed or unknown-type frame closes the link.

```json
{ "type": "launch", "correlationId": "c-1", "jobId": "hjob_...", "runtime": "claude" }
```

## Authentication

The WebSocket upgrade is authenticated by the Admiral's normal authentication service from the standard
credential headers only:

- `Authorization: Bearer <credential token>` (required for runners in practice), or
- `X-Token` (session token) or `X-Api-Key`.

No other header carries identity. Tenant, user and access-key headers (for example `x-tenant-guid` or
`x-access-key`) are ignored. When the credential is missing, unknown, inactive, or belongs to an inactive user
or tenant, the Admiral sends `handshakeAck { accepted: false, reason: "harbor_authentication_failed" }` and
closes the link before reading any frame.

The verified principal must match the durable enrollment of the runner named in the handshake: same tenant,
user, authentication method and credential. Enrollment requires a credential for bearer and API-key methods,
so the global `X-Api-Key` cannot own a runner. Enrollment is described in
[HARBOR_IDENTITY.md](HARBOR_IDENTITY.md). Secrets never appear in any message or log.

## Message set

Admiral to runner:

| type | class | purpose |
|------|-------|---------|
| `handshakeAck` | `HarborHandshakeAck` | accept or reject the link; a rejection carries a stable `reason` |
| `launch` | `HarborLaunchRequest` | start a process for a server-issued `jobId` |
| `kill` | `HarborKillRequest` | stop a job: grace period, then kill the process tree |
| `error` | `HarborError` | a runner frame was refused; `message` is a stable reason, `jobId` names the job when there is one |

Runner to Admiral:

| type | class | purpose |
|------|-------|---------|
| `handshake` | `HarborHandshake` | name the runner; advertise capabilities and capacity |
| `started` | `HarborStarted` | a launched process started (host process id) |
| `output` | `HarborOutput` | one output chunk with its `sequence` |
| `exited` | `HarborExited` | the process exited (exit code) |
| `heartbeat` | `HarborHeartbeat` | liveness plus the jobs still running |
| `error` | `HarborError` | a job failed outside its normal exit |

Git delegation and standard-input commands are not part of this protocol version; frames of those types are
unknown and close the link.

## Handshake

The first frame must be `handshake`, within `Harbor.HandshakeTimeoutSeconds`:

```
runner  -> handshake    { harborId, name, protocolVersion, maxConcurrentJobs, capabilities }
Admiral -> handshakeAck { accepted: true }
```

Stable rejection reasons include `harbor_handshake_required`, `harbor_protocol_version_unsupported`,
`harbor_id_invalid`, `harbor_capacity_invalid` (1-1024), `runner_owner_unknown`, `runner_owner_mismatch`,
`runner_owner_lookup_timeout`, `runner_owner_unavailable` and `runner_registration_disabled`. Every rejection
closes the link.

The handshake, every heartbeat, and every launch, stop and runner event resolve the runner's owner again from its
durable enrollment, principal and credential. The lookup is awaited and never blocks a thread. One lookup is
bounded by `Harbor.OwnerLookupTimeoutSeconds` (default 5, range 1-60): a lookup that does not finish in time is
refused as `runner_owner_lookup_timeout`, and a lookup that fails with a database error is refused as
`runner_owner_unavailable`.

Each accepted handshake gets a new connection generation. A second link for the same runner and principal
replaces the first; the old link's next frame is refused with `harbor_session_stale` and the Admiral closes it.
A stale link closing never removes its replacement.

## Jobs and command authorization

The Admiral issues every `jobId` (`hjob_` plus a random value); a runner cannot choose one and identifiers are
never reused. Each job is bound to:

- its runner (a job never moves to another runner, and there is no local fallback),
- the durable enrollment generation that authorized it, and
- the connection generation currently allowed to report for it.

A launch or stop is authorized for the runner owner, or for a caller with authority over the owner under the
same runner authority rule that governs enrollment and revocation (`IHarborRunnerAuthority`): a global
administrator has authority over every owner, and a tenant administrator only over an owner in the same tenant
who is not a global administrator. A launch is refused with a stable reason when the runner is not connected
(`harbor_runner_unavailable`), fails revalidation, is not authorized (`harbor_command_unauthorized`), is at its
advertised capacity (`harbor_runner_capacity_exhausted`), or already has a live job with the same launch key in
the caller's tenant (`harbor_job_duplicate`). A stop goes only to the job's runner and current connection.

Runner frames about a job are accepted only from the bound connection:

| Condition | Reason |
|-----------|--------|
| job unknown | `harbor_job_unknown` |
| job belongs to another runner or an earlier enrollment | `harbor_job_not_owned` |
| job not yet claimed by this connection after a reconnect | `harbor_job_not_bound` |
| second `started` | `harbor_job_started_duplicate` |
| `started` without a positive process id | `harbor_process_id_invalid` |
| `output` sequence below the next expected value | `harbor_output_replayed` |
| `output` sequence above the next expected value | `harbor_output_gap` |
| `output` for a job that is not running | `harbor_job_not_running` |
| second `exited` or job error | `harbor_job_exit_duplicate` |

A refused job frame is answered with `error` and the link stays open.

```
Admiral -> launch  { jobId, runtime, workingDirectory, prompt, arguments, environment }
runner  -> started { jobId, processId }
runner  -> output  { jobId, sequence: 0, stream: "Stdout", data }   (sequence increments by one)
Admiral -> kill    { jobId, gracefulTimeoutMs }                      (optional)
runner  -> exited  { jobId, exitCode }
```

## Heartbeats, revocation and reconnection

Send `heartbeat` well inside `Harbor.IdleTimeoutSeconds`; a link with no frames for that long is closed. Every
heartbeat, and every `started`, `output`, `exited` and job `error` frame, revalidates the durable enrollment and
credential outside the registry lock before it changes a job. A revoked enrollment, a changed enrollment
generation, or an inactive credential, user or tenant is answered with `error` naming the reason
(`runner_enrollment_revoked`, `runner_enrollment_generation_stale`, `runner_principal_inactive`,
`credential_revoked_or_mismatched`), the link closes, and the runner's jobs from that enrollment become `Lost`
with that reason. The revocation may have happened on any Admiral instance.

After a reconnect with the same enrollment generation, the runner lists its still-running jobs in `liveJobIds`.
Listed jobs are rebound to the new connection and continue from their next output sequence. Jobs from an earlier
enrollment generation are refused with `harbor_job_not_rebindable` and become `Lost`; re-enrollment never
revives earlier work. Jobs a reconnected runner does not list become `Lost`. Unknown or foreign listed jobs are
answered with `error`.

Job records are durable. Each state change and each heartbeat persists the job's state, connection generation,
host process id, exit code, next output sequence and failure reason, guarded by a revision so a delayed write
never overwrites a newer state. An Admiral restart ends every link; at start the Admiral marks every job that was
not terminal `Lost` with `harbor_admiral_restarted`. A reconnecting runner that lists such a job is refused with
`harbor_job_not_rebindable`; a job the store does not know is refused with `harbor_job_unknown`.

A runner whose link stays closed longer than `Harbor.DisconnectedJobGraceSeconds` (default 180) loses its live
jobs with `harbor_runner_disconnected`. A stop for a job whose runner is not connected releases it with
`harbor_job_released_runner_unavailable`.

## Opt-in operation

1. Leave `Harbor.Enabled` false in production until the Harbor acceptance is recorded. The current container
   deployment needs no change while Harbor is disabled.
2. To enable on an isolated Admiral, set in `settings.json` and restart:

   ```json
   "Harbor": { "Enabled": true, "LinkPath": "/harbor/link", "HandshakeTimeoutSeconds": 15, "IdleTimeoutSeconds": 90 }
   ```

   `WebSocketEnabled` must stay true; otherwise the Admiral logs that Harbor was not registered.
3. Create a user and a bearer credential for the runner owner, then enroll the runner as a tenant or global
   administrator: `POST /api/v1/harbor-runners/enrollments` with `{ "runnerId", "credentialId" }`.
4. Configure the runner with the credential token and dial `wss://<admiral>/harbor/link` through the port the
   container already publishes. No additional port is needed.
5. Revoke with `POST /api/v1/harbor-runners/enrollments/{runnerId}/revoke`.

6. Opt a captain or a vessel into running missions on the runner. Nothing routes without a route:

   ```json
   "Harbor": { "Enabled": true, "MissionRoutes": [ { "RunnerId": "hbr_build_host", "CaptainId": "cpt_..." } ] }
   ```

   A `VesselId` route covers every captain of that vessel without a captain route. Set
   `AdmiralWorkingDirectoryRoot` and `RunnerWorkingDirectoryRoot` together when the runner sees docks under a
   different root; otherwise it receives the Admiral's dock path unchanged.

## Mission execution

A routed mission launch builds the runtime's command and arguments on the Admiral, exactly as for a local launch,
and sends them in `launch` with the dock path as `workingDirectory`. The Admiral keeps the dock, branch and landing;
the runner never lands or pushes, and the protocol has no git command. Only non-secret variables travel in
`environment`: a launch that needs a provider key, an account login or any other variable is refused with
`harbor_launch_environment_unsupported` before a job exists. The Admiral's MCP launch credential never travels.

The mission owner must be the runner's enrolled tenant and user (`harbor_runner_owner_mismatch` otherwise). The
durable job record is written before the runner receives the launch. The runner's `output` chunks become lines
in the mission log, and `exited` or a lost job completes the mission process exactly like a local process exit.
A captain stop becomes `kill` for the job. There is no fallback to a local process or another runner.
