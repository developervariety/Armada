# Harbor Link Protocol

The Harbor link is the WebSocket a Harbor runner opens to the Admiral. The Admiral sends work down the link;
the runner executes it and reports back. This document is the contract both sides implement. It is versioned by
`HarborProtocol.Version` (currently `1.0`); change that constant on any breaking change to the message set.

Harbor is disabled by default. When `Harbor.Enabled` is false the Admiral registers no link route and no
enrollment routes. Stored-job safety checks and the administrator stop-confirmation route remain available.
See [Opt-in operation](#opt-in-operation).

The [headless console runner](HARBOR_RUNNER.md) implements process-only routes,
independent job supervision, durable output replay and opt-in
[Harbor-owned checkouts](HARBOR_CHECKOUTS.md).

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
[HARBOR_IDENTITY.md](HARBOR_IDENTITY.md). Enrollment credentials never appear in frames or logs. An owned launch can
carry its mission-scoped MCP credential in the private launch envelope. The
runner supplies it to the child environment and does not persist or log it.

## Message set

Admiral to runner:

| type | class | purpose |
|------|-------|---------|
| `handshakeAck` | `HarborHandshakeAck` | accept or reject the link; a rejection carries a stable `reason` |
| `launch` | `HarborLaunchRequest` | start a process for a server-issued `jobId` |
| `kill` | `HarborKillRequest` | stop a job: grace period, then kill the process tree |
| `resumeAck` | `HarborResumeResponse` | per-job resume decision and output replay position |
| `error` | `HarborError` | a runner frame was refused; `message` is a stable reason, `jobId` names the job when there is one |

Runner to Admiral:

| type | class | purpose |
|------|-------|---------|
| `handshake` | `HarborHandshake` | name the runner; advertise capabilities and capacity |
| `resume` | `HarborResumeRequest` | retained job identity and output replay proof |
| `started` | `HarborStarted` | a launched process started (host process id and UTC start time) |
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
`credential_revoked_or_mismatched`), the link closes, and jobs without a confirmed process stop remain
`Uncertain`. Revocation does not prove that an agent stopped writing its checkout.

A reconnect within the same Admiral process can bind known jobs from `liveJobIds`. A job missing from that
list stays uncertain. A new enrollment cannot adopt an older enrollment's work. Unknown or foreign jobs receive
an error. A heartbeat alone cannot adopt a job restored after an Admiral restart.

Job records retain the enrolled owner, enrollment and session generations, launch key, runner process identifier
and start time, next output sequence, mission, captain, dock, and Admiral synthetic process identity. Before a
mission launch is sent, one transaction binds the assigned mission and captain to that durable job. A changed
assignment prevents the send.

At startup, the Admiral loads recovery records in bounded pages before the link listener and stale-captain
cleanup. It restores the exact mission attempt without launching an agent. Missing or conflicting identity stays
blocked. This safety check applies even when the Harbor link is disabled. Planned Admiral shutdown detaches
Harbor jobs and retains their assignment.

After an authenticated handshake, a runner sends `resume` with its retained job proofs. Each proof names the
job and launch key received in the launch message, runner process identifier and UTC start time, running or confirmed-stopped state, and the
first and next retained output sequences. The Admiral verifies the durable owner and enrollment, current session,
exact mission attempt, process identity, and a replay range that contains the next persisted sequence. Its
`resumeAck` gives a decision and `replayFromSequence` for each job. Accepted output must arrive in sequence;
missing output does not permit a terminal result to bypass the replay fence. A stopped proof requires a known
exit code (`harbor_resume_exit_code_unknown` otherwise). Jobs without a recorded process identifier and UTC start
time cannot be adopted. These jobs stay fenced until a global administrator confirms their stop.

A confirmed terminal result is durable before lifecycle delivery. A requeue stores its computed captain
and quarantine policy as a completion intent before it clears assignment pointers. Stable fact, event and signal
identifiers let recovery finish that intent without repeating its recorded effects. The Admiral acknowledges delivery only after
the actual mission exit handler completes. The acknowledgment uses the exact job revision. Startup can retry an
undelivered terminal result without applying it to a later mission attempt.

Disconnect grace expiry, a missing heartbeat job, a failed stop request, or an unavailable owner leaves the job
uncertain. These states retain capacity and prevent replacement assignment or dock reclamation. Elapsed time
never proves that a remote writer stopped. A legacy mission job without a stored dock identity blocks
checkout reclamation in its tenant until its old writer is accounted for. Existing jobs cannot move to another
host as a recovery fallback.

A global administrator can confirm an externally verified stop with
`POST /api/v1/harbor-runners/jobs/{jobId}/confirm-stopped`. The request includes
`expectedRevision`, `enrollmentGeneration`, `runnerProcessId`, `runnerProcessStartedUtc`, and named `evidence`.
The process fields must match the stored identity; both can be null only when no process identity was recorded.
The runner must be disconnected. A stale revision or identity returns 409. This route remains available when
Harbor transport is disabled. Confirmation records the terminal result before it delivers the mission exit.

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

Before it provisions a dock, mission assignment checks the selected captain's
explicit runner route. The capacity probe revalidates the runner session and
checks the mission owner's exact tenant and user. A confirmed full runner leaves
the mission Pending with `AssignmentHoldReason=harbor_runner_capacity_exhausted`
and the runner ID as `AssignmentHoldEntityId`. It reserves no job and creates no
dock or launch-error signal. Repeated full-capacity checks retain the same hold.
The normal assignment sweep retries the mission and releases the hold when the
runner has a slot.

Capacity can change after this advisory probe. The coordinator checks it again
under its launch lock. A launch refused for capacity releases the captain and
reclaims the just-provisioned dock, then records the same pending hold. Other
launch refusals retain their normal failure handling. This wait never selects a
different runner or starts the mission locally.


A routed mission launch builds the runtime's command and arguments on the Admiral, exactly as for a local launch,
and sends them in `launch` with the dock path as `workingDirectory`. The arguments register the same extra
workspace roots as a local launch (provisioned sibling checkouts and `captainEvidenceRoots`), each mapped through the
route's working-directory roots to the path the runner resolves. A root the route cannot map is left out, and the
Admiral logs the root and the mapping reason. The Admiral keeps the dock, branch and landing;
the runner never lands or pushes, and the protocol has no git command. Only non-secret variables travel in
`environment`: a launch that needs a provider key, an account login or any other variable is refused with
`harbor_launch_environment_unsupported` before a job exists. The Admiral's MCP launch credential never travels.

The mission owner must be the runner's enrolled tenant and user (`harbor_runner_owner_mismatch` otherwise). The
durable job record is written before the runner receives the launch. The runner's `output` chunks become lines
in the mission log. A confirmed `exited` result enters the shared mission exit lifecycle. Uncertain jobs retain
their process and assignment fence. A captain stop sends `kill` and waits for confirmed exit and lifecycle
handling; sending the command alone is insufficient. The stored stop request survives restart and yields a
`Stop` decision when its runner returns. There is no fallback to a local process or another runner.

## Owned workspace operations

The optional `workspace` capability version 1 adds typed workspace requests and
results. Enrollment and connection generations fence each exchange. A fresh
request ID correlates one exchange; a stable operation key identifies a durable
mutation across retries. The server rechecks the authenticated enrollment, stored
dock binding, owner and writer state before dispatch. Results must match the
captured session and pending request. No offline or local fallback is allowed.

Repository inspection precedes a saved provision intent. Bound operations carry
the full checkout identity. File paths are relative and bounded. Git operations
select fixed service methods. Verification selects a host-configured command key
and digest. Output chunks carry the retained verification identity and byte
offset, with bounded transfer and full-output hash checks. See
[configuration and recovery](HARBOR_CHECKOUTS.md) for operation limits and holds.
