# Operator client surfaces

These entry points expose the existing server contracts. A client surface does
not add authority: REST, MCP and background dispatch still check the caller and
record ownership. Discover MCP tools from the running server and follow every
catalog page. Service availability and caller scope can change that catalog.

| Capability and data | REST and Postman | Dashboard | Helm | MCP |
| --- | --- | --- | --- | --- |
| Saved conversations: `AskThread`, `AskTurn` | `/api/v1/ask/threads`; Ask requests | Ask Armada | No dedicated saved-thread command | Read-only list and read tools; no tool sends, decides, changes or deletes |
| Saved approvals: `AskActionProposal`, `AskWorkReport` | Thread proposal and work routes; Ask requests | Approval and work cards; read-only pending items in Needs You | No dedicated command | Scoped turn tools propose actions; human decisions use REST |
| Local checkout import: `VesselCheckoutDiscovery`, `VesselCheckoutCandidate` | Vessel local-checkouts and import-checkout routes; Vessels requests | Vessels import dialog | No dedicated import command | `armada_discover_vessel_checkouts`, `armada_import_vessel_checkout` |
| Saved mission actions: `FleetAction`, `FleetActionRun` | Fleet-actions and fleet-action-runs routes; Mission Actions requests | Mission Actions | No dedicated command | No dedicated Mission Actions tool |
| Harbor jobs: `HarborJobRecord` | Harbor job list and detail GET routes; Harbor requests | Server, Harbor jobs | No dedicated job-list command | `armada_harbor_jobs`, `armada_harbor_job` |
| Harbor host metrics | `GET /api/v1/harbor-runners/{runnerId}/metrics`; Harbor requests | Server, Harbor host chart | No dedicated command | No host-metrics tool |
| Process health and diagnostic checks | `/api/v1/status/health`, `/api/v1/doctor`; Status requests | Server and Diagnostics | `armada doctor` checks its host and configured server | `armada_status`; no doctor tool |

The owning guides define inputs and behavior:
[saved conversations](ASK_CONVERSATIONS.md),
[checkout import](VESSEL_CHECKOUT_IMPORT.md),
[mission actions](FLEET_ACTIONS.md), and [Harbor](HARBOR_RUNNER.md).

## Harbor read contract

`GET /api/v1/harbor-runners/jobs` accepts `runnerId`, `activeOnly` and `limit`.
The default limit is 100; the service clamps it to 1 through 1000. It returns
`{ Jobs: HarborJobRecord[] }`, newest first. The detail GET appends the exact
job ID. Hidden and unknown jobs share the same not-found response.

These REST routes exist only when both Harbor and WebSocket transport are
enabled. HTTP 404 therefore does not prove that there are no durable jobs.
The Dashboard shows endpoint unavailability separately from HTTP 200 with an
empty `Jobs` array. HTTP 401 and 403 remain authentication or permission errors.

MCP registration has a separate boundary. When the Harbor job service exists,
its MCP reads can query durable records even with transport disabled. A missing
service returns the named `Unavailable` result. The Dashboard does not call the
stop, enrollment, revoke or recovery-confirmation operations.

Job state describes the recorded attempt. `Uncertain` is non-terminal, and
`StopConfirmed` is separate from a terminal state. A job timestamp or runner ID
does not establish runner connectivity. The panel does not display launch keys,
owner identifiers, process identifiers or connection generations.

The Harbor host chart reads `GET /api/v1/harbor-runners/{runnerId}/metrics` for
the runner ID in the jobs filter. `no-data` is an empty chart. `stale` names
the last sample time and the series has a gap. A null field is a gap. The chart
does not draw a gap as zero. The series is in memory on the Admiral. Helm and
MCP do not read it. The route is absent while Harbor is disabled, and that
`404` is a hidden or unknown runner when Harbor is enabled.

## Health, storage and backup boundaries

Health reports process reachability, start time, version, Admiral and MCP ports,
remote tunnel state, and available build-drift fields. It does not report a
separate WebSocket port or Harbor runner connectivity.

Diagnostics returns named checks with status and message. Only the global
administrator's database message includes a SQLite file path and size. That
value is a database file size, not host free space or Docker usage. The Helm
doctor command also checks its own host configuration; it is not a substitute
for the Admiral's scoped diagnostic result.

The REST API has no host-storage or cleanup-history feed. The Harbor host metrics
route is a separate in-memory sample ring for one enrolled runner. It is not
that inventory. The MCP
`armada_disk_lifecycle` scan reports Armada-owned storage through its existing
operator contract. A backup download creates and verifies a backup; it is not a
backup-age or restore-readiness status endpoint. Clients do not infer these
missing measurements from health, job state or an empty result.

## Voyage table controls

The Voyages list keeps paging and status filtering on the server. Its shared
table displays the returned order and total count. The column chooser saves
optional column visibility in the current browser. Identity and action columns
remain visible. Column visibility does not change the server query, row
selection or the caller's authority. Refresh results use the latest request;
a slower old response cannot replace the current page.

The Dashboard Storage page, in the System navigation section, shows the
administrator storage inventory: bytes and file counts by category, the largest
files, database tables, growth history and the reason for any partial figure. A
refresh button starts a background collection. The page needs a global
administrator; any other signed-in user sees a notice instead of data. See
[Storage Inventory](STORAGE_INVENTORY.md). Helm has no storage command, and the
disk lifecycle service is a separate scan and cleanup contract.
