# Production Metrics

Armada uses **verified landed slices per day** as its main production measure.
This measure counts completed delivery, not started work or agent activity.

Do not increase dispatch or Check concurrency from this report alone. First
record a baseline. Then use the queue, quality, and rework measures to find the
actual constraint.

## Time Window

Use half-open UTC windows: `fromUtc <= timestamp < toUtc`.

- Use complete UTC days for a baseline.
- State the window and the capture time.
- Do not include a partial current day in a daily rate.
- Keep the original record. Do not recalculate an old baseline from changed
  current state and replace the old value.

## Slice Cohort

A slice is a leaf objective. It has no objective that names it as
`parentObjectiveId`.

The completion cohort contains leaf objectives whose `CompletedUtc` is in the
window. This rule prevents a later status update from moving a slice to a
different day.

Report these values separately:

1. **Raw completed leaf slices**: all leaf objectives in the completion cohort.
2. **Verified landed slices**: cohort members that pass the evidence rules
   below.
3. **Unverified or uncovered slices**: cohort members for which the evidence
   rules fail or cannot be evaluated.

Never rename the raw count as the verified count.

## Verified Landed Rules

A verified landed slice must meet all of these rules:

- The objective is a leaf and has status `Completed`.
- The objective links at least one voyage.
- Every linked voyage has status `Complete`, or has typed attempt facts that
  prove the full recovery lineage for each independent failed chain. Each
  terminal recovery mission must be `Complete`, have a commit, and belong to a
  `Complete` voyage. Each recovery member keeps the root tenant, user, and
  vessel scope; its recovery voyage can differ. Cancelled siblings with a
  recorded attempt remain part of the evidence check. Missing, ambiguous,
  cross-scope, or incomplete lineage
  stays unverified.
- At least one implementation-producing mission has a commit hash and reached
  `Complete`. A read-only objective must instead have an explicit read-only
  delivery classification.
- Every terminal tip in each delivery chain has typed landing evidence: a
  mission-linked `Landed` merge entry (merge-queue landing) or a `Landed`
  attempt fact (direct landing, such as `LocalMerge` or a merged pull
  request).
- Each participating Check for each terminal tip's commit is `Passed`. A Check
  for another commit is not current. A missing current Check is not a pass.

Return the failed or unavailable rule codes for each uncovered slice. Do not
infer a pass from the objective status alone.

## Measures

Each measure must return its value, numerator, denominator, coverage count,
and availability state. Valid availability states are `available`, `partial`,
and `unavailable`.

### Verified landed slices per day

Count verified landed slices in each UTC day. The window rate is:

`verified landed slices / complete UTC days`

Also report the raw completed leaf rate and evidence coverage.

### Ready-to-dispatch delay

The current historical proxy starts at the first `objective.snapshot` whose
payload has `BacklogState=ReadyForDispatch`. It ends at the first linked
voyage's `CreatedUtc`.

This value is `partial`. Historical records do not store the result of the full
dispatch preflight at each instant. Call it **declared-ready delay**, not full
admission-ready delay. Exclude a row when the ready snapshot or dispatch link
is absent, and report that exclusion.

### Check queue and execution time

- Armed-to-start time is `StartedUtc - CreatedUtc`, or the durable
  `QueueDurationMs` projection. This includes time before an automatic Check is
  eligible to run, so it is not host-slot queue time.
- An Armada-executed Check stores `SlotRequestedUtc` when it asks for
  `HostWideCommandLock`, after profile resolution and isolated-checkout
  preparation and before the wait. The value is persisted before the wait
  and cleared if the run returns to Pending.
- Preparation delay (`preparationDelayMs`) is `SlotRequestedUtc - CreatedUtc`.
- Host-slot queue time (`hostQueueMs`) is `StartedUtc - SlotRequestedUtc`.
- Execution time is `DurationMs`, which is `CompletedUtc - StartedUtc` for
  every started Check, timed-out and cancelled ones included. A Check satisfied
  by a definition-of-done gate pass (`satisfiedBy` set) ran no command, has a
  null `DurationMs`, and is outside the execution-time measure.

Imported external Checks never request the slot and are outside both slot
denominators. An Armada Check that started without a recorded slot request is
counted in `unknown` for both measures. The `check.auto_queued` event is not a
slot request and is never substituted.

Do not add the values and call the result execution time. Report the median,
p90, p95, count, and coverage. The current surface groups these values by
source family and work type. A later surface can add Check-type and vessel
dimensions without changing the timing definitions.

## Report Surfaces

- REST: `GET /api/v1/production/summary`
- MCP: `armada_production_summary`

Both surfaces use a seven-day default window and reject windows longer than 90
days. Optional `sourceFamily` and `workType` filters use the grouping rules in
this document.

The summary reads Checks created from 90 days before the window's start, in
the database query, without their output logs. This is the same lookback as
the attempt facts, claim observations and objective snapshots. A Check older
than that is not evidence for a slice in the window. There is no upper bound,
because a post-land regression Check linked to a slice in the window counts
whenever it was detected.

### Fact retention

The fact tables `mission_attempt_facts`, `preparation_claim_observations` and
`lane_state_transitions` are kept for `productionFactRetentionDays` (default
365; `0` keeps them forever). Data expiry deletes facts older than that by
creation time. Objectives, voyages, missions, merge entries and Checks follow
`dataRetentionDays` instead.

A fact-based measure over a window before the retention cutoff sees no facts.
It reports them the same way as facts that were never recorded, not as a
value:

- First-pass acceptance counts the slice in `unknown` with reason
  `attempt_facts_not_recorded`, and gives no rate when no slice is eligible.
- Rescue share counts the runs as historical.
- Repeated research counts the slice in `unknown` with reason
  `no_preparation_claims_recorded`.
- Eligible idle lane-minutes count the time as unobserved.

Record a baseline before its window passes the retention cutoff.

### Attempt facts

Armada appends one mission attempt fact at each attempt transition. The
`mission_attempt_facts` table is append-only and is not an event, so event
cleanup does not remove it. Each fact stores:

- the mission, voyage, vessel, tenant, and user
- the root mission: the original mission of the attempt chain. A rescue
  follows its parent. A chained rescue review stage follows the rescue stage
  it depends on.
- the fact type: `AttemptStarted`, `Retried`, `Restarted`, `ReviewDenied`,
  `Failed`, or `Landed`. `Landed` resolves the chain as delivered.
- `IsRescue`: true only when the mission description carries the
  autonomous-rescue marker. A title prefix is never a rescue classification.
- an allow-listed machine reason code. Unknown causes remain null. Free-text
  failure messages are not stored or converted into codes.

Known failure codes include the stage-watchdog heartbeat timeout, runtime
crash, provider usage or authentication failures, provider timeout, typed
definition-of-done classes, and landing push, merge, or no-commit outcomes.
The code comes from the emitter's typed decision. A mission's diagnostic text
does not classify its failure. Missing or unrecognized causes stay unclassified
and do not change the attempt-coverage denominator.

Facts are recorded when a captain process launches, when a transient captain
failure or captain recovery relaunch runs the mission again, when a Judge is
re-run in place, when a mission is restarted from any client, when a review is
denied, when a mission fails or its landing fails, and when work lands.

A mission that ran before facts existed has no `AttemptStarted` fact. The
summary classifies it as historical and does not guess its history.

### Attempt provenance

Armada also records who ran each captain process. The `mission_attempt_provenance` table holds one row per launch:
the mission, voyage, vessel, captain, persona, runtime, model, runtime version, process id, start and end times, the
outcome and an exit code. It is separate from the attempt facts: a fact records what Armada decided next, a provenance
row records what ran. Provenance rows are deleted with their mission and follow `dataRetentionDays`, not
`productionFactRetentionDays`. `GET /api/v1/missions/{id}/attempts` and the MCP tool `armada_mission_attempts` read them
through the caller's mission scope.

### First-pass acceptance

The denominator is a verified slice whose every run in the attempt chain has
an `AttemptStarted` fact. The numerator is an eligible slice whose chain has
none of these facts:

- `Failed`
- `ReviewDenied`
- `Restarted`
- `Retried`, except a Judge re-run that only waited for Checks
  (`judge_check_wait`, or `judge_revision_check_wait` for a revision asked for
  before the Checks finished)
- any fact with `IsRescue`
- a repeated `AttemptStarted` that no `Retried` fact explains

A verified slice with a run that has no attempt fact is counted in `unknown`
with reason `attempt_facts_not_recorded`. The metric reports `rate`,
`coverage` (eligible divided by eligible plus unknown), and `unknownByReason`.

### Rescue share

Rescue missions are the missions whose attempt facts carry `IsRescue`. The
chain includes every mission whose facts name the same root mission, so a
recovery voyage that is not linked to the objective still counts.

`rescue share = rescue mission runtime / classified mission runtime`

Missions that ran before facts existed are counted in
`historicalUnclassifiedMissionCount` and `historicalUnclassifiedMs`. They are
not part of the share. `coverage` is classified missions divided by classified
plus historical missions. Missions without a runtime are counted in
`unknownMissionCount`.

Also report `rescuedSlices / completedSlices` as `rescuedSliceRate`. A slice
with historical runs and no rescue fact is counted in `rescueUnknownSlices`.
This count shows frequency while runtime share shows cost.

### Landed-to-verified-closeout delay

Start at the final required landing time for the slice. End at the objective's
`CompletedUtc` after all required Check and chain evidence is valid.

The landing time is the latest `CompletedUtc` of a mission-linked `Landed`
merge entry or `CreatedUtc` of a `Landed` attempt fact for the tip. If both are
absent, for example after expiry, do not silently substitute a mission
timestamp. A report can show
`mission Complete to objective closeout` as a named proxy with separate
coverage.

### Consumer or ledger regressions after landing

This measure uses typed regression links. A failed Check name or an incident
title is never a classification.

- An incident carries `RegressionPurpose` (`None`, `Consumer`, or `Ledger`),
  `RegressionCause` (`Unclassified`, `LandedChange`, `PreExisting`,
  `Environment`, or `NotRegression`), `RegressionObjectiveId`, and
  `RegressionLandedCommit`. Set them with `armada_create_incident`,
  `armada_update_incident`, `POST /api/v1/incidents`, or
  `PUT /api/v1/incidents/{id}`.
- A Check carries `RegressionPurpose`, `RegressionObjectiveId`, and
  `RegressionLandedCommit`. Set them with `run_check`,
  `POST /api/v1/check-runs`, or `POST /api/v1/check-runs/import`. A retry
  keeps them.
- A link or cause without a purpose is rejected. An objective link must use
  the `obj_` prefix and name an objective the caller can read: a global
  administrator links any objective, a tenant administrator one in its tenant,
  and any other caller one it owns. A landed commit must be 7 to 64
  hexadecimal characters.

A regression record is an incident with a purpose, or a failed Check with a
purpose. An incident that names a Check classifies that Check, so the Check is
not counted again. A failed Check that no incident classifies has cause
`Unclassified`.

Each record is attributed as follows:

1. `PreExisting`, `Environment`, and `NotRegression` causes are counted in
   `regressionCoverage.notRegression`.
2. A record without an objective link is counted in
   `regressionCoverage.unlinked`.
3. A record linked to an objective outside the report cohort is counted in
   `regressionCoverage.outsideCohort`.
4. A record for a cohort slice is unknown when its cause is `Unclassified`
   (`cause_unclassified`), when the slice is not verified
   (`slice_not_verified`), or when its landed commit is not a delivered tip
   of the slice (`landed_commit_mismatch`).
5. Otherwise the slice is a consumer or ledger regression.

Records linked to a cohort slice count whenever they were detected. Unlinked
and outside-cohort records count only when detected inside the window.

Per group, `consumerRate` and `ledgerRate` are distinct regressed slices
divided by `verifiedSlices`, and they are reported separately. `unknown` and
`unknownByReason` state the records that could not be attributed. The metric
is `partial` when any record is unknown. The report-wide
`regressionCoverage` also states `recordsRead`, `attributed`, and unreadable
incident snapshots. The rates count recorded typed regressions only.

### Repeated research per slice

Search counts and context-pack use do not prove that a captain repeated
prepared research. This measure uses durable preparation claim observations.

Armada appends one observation to `preparation_claim_observations` for each
bounded preparation claim at these points:

- **Established**: a verified claim is new, or its statement, evidence, kind,
  or dependency changed. Recorded when an objective is created or its
  preparation is updated, including updates applied from refinement.
- **Reestablished**: the same claim is verified again with a later
  `VerifiedUtc`, an unchanged fingerprint, and unmoved anchors that it depends
  on. This is repeated research.
- **Revalidated**: a claim that needed a recheck, or whose own source or
  target anchor moved, is verified again. This is stale-claim revalidation,
  not repetition.
- **Reused**: each verified claim is delivered to a newly linked voyage.
  Re-linking the same voyage records nothing.

Each observation stores the claim id and kind, the objective, the source
family, the voyage for reuse, the immutable source and target commits, and a
SHA-256 fingerprint of the claim kind, dependency, statement, and sorted
evidence. Claim text, evidence paths, and search queries are never stored.

Per group, `repeatedClaims` counts distinct re-established claims and
`reestablishedObservations` counts every repeat. `revalidatedClaims`,
`reusedClaims`, and `establishedClaims` are reported separately.
`affectedSlices` counts slices with a re-established claim. A slice with no
observation is counted in `unknown` with reason
`no_preparation_claims_recorded`, and `coverage` is covered slices divided by
slices. `repeatedMinutes` stays null because research duration is not
recorded. The report-wide `claimObservationsBySourceFamily` counts every
observation type in the window by source family.

### Eligible idle lane-minutes

Each scheduler sweep records one sample per shared lane in
`lane_state_transitions`. A lane is the set of vessels joined by
build-participating sibling declarations. A sample stores:

- `EligibleCount`: objectives the sweep selected as dispatch candidates for
  the lane. A multi-vessel objective belongs to no lane. This is declared
  eligibility before the per-objective dispatch preview.
- `Occupied`: active voyages that touch the lane.
- `Capacity`: the per-lane concurrent voyage limit.
- `BlockReason`: `FleetCapacity` when the fleet-wide limit is reached,
  `DispatchHold` when a hold is engaged, otherwise `None`.
- `EligibleSourceFamilies` and `ValidForSeconds`.

A row is written when any of these values change, and as a checkpoint before
half of the last row's trust window has passed. The trust window is twice the
longer of the sweep interval and one minute. A lane that stops appearing is
written once as empty.

The summary reconstructs time per lane. A row counts from its creation until
the next row, the end of its trust window, or the window end, whichever is
first. Time outside every trust window is unobserved and counted in
`incompleteIntervals`; the last state is never assumed to continue.

Observed time with eligible work and `Occupied < Capacity` is split three
ways: `eligibleIdleMinutes` (no block), `fleetBlockedMinutes`, and
`holdBlockedMinutes`. The report-wide `laneTime` states `lanes`,
`expectedLaneMinutes` (lanes multiplied by elapsed window minutes),
`observedLaneMinutes`, `unobservedLaneMinutes`, and `coverage`. Each group's
`eligibleIdleLaneMinutes` attributes idle minutes by the eligible source
families and carries the fleet-wide expected minutes and coverage. The lane
summary is fleet-wide and needs an administrator or tenant administrator;
otherwise it is `unavailable` with reason `lane_state_requires_administrator`.

## Grouping

Always include an `unknown` group. Do not infer a group from title text.

- **Source family**: use the first normalized `port:<family>` objective tag.
  Report conflicting multiple `port:` tags as invalid metadata.
- **Work type**: report objective `Category` and `Kind` as separate dimensions.
  Do not combine them into one implicit label.
- Also support vessel and Check type for operational drill-down.

## Read-only Collection

The current MCP surfaces can collect a baseline without production changes:

- `list_objectives` supplies objective state, parent links, tags, linkage, and
  timestamps. Its effective page cap is 500 records.
- `armada_enumerate` supplies paginated voyages, missions, Checks, merge queue
  entries, incidents, and `objective.snapshot` events. Request no command or
  event payload unless the measure needs it.
- `get_check_run` supplies one Check's parsed result. Keep full output disabled
  for metric collection.
- `armada_status` supplies a current state snapshot. It does not supply a
  historical interval.

Follow every page until `TotalPages`, subject to the report's explicit scan
limit. Return these fields for every entity scan:

- requested window
- pages scanned
- records scanned
- `TotalRecords`
- scan limit
- whether the scan was complete

If a scan stops at its bound, set affected measures to `partial`. Do not
calculate a full-fleet rate from a newest-record sample.

## Source-Control Phase Telemetry

The admiral records where a mission's source-control time goes on the
`Armada` meter (`ArmadaMetrics`). Nothing is exported until `telemetry.enabled`
is `true`; the Prometheus scrape endpoint then serves the series below on
`telemetry.prometheusPort` (default 9464). With telemetry off, or with a
listener that throws, every operation returns the same result it returns
without telemetry; a refused measurement is counted in
`ArmadaMetrics.RecordingFailures`.

Every duration is a histogram in seconds. The Prometheus series are
`<name>_bucket`, `<name>_sum` and `<name>_count`, with the dots in the name
written as underscores.

| Instrument | Owner | Labels |
| --- | --- | --- |
| `armada.git.repository_lock.wait.seconds` | `VesselRepositoryLock`, `DockService`, `GitService` | `lock`: `vessel_landing`, `dock_provision`, `sibling_provision`, `git_worktree`; `outcome`: `acquired`, `canceled` |
| `armada.git.fetch.seconds` | `GitService.FetchAsync` (the whole call, worktree prune included) | `outcome`: `success`, `failure`, `canceled`; `retried`: `true` when the full fetch was refused and the narrower fetch ran |
| `armada.dock.checkout.seconds` | `DockService` | `kind`: `primary` (the worktree add), `sibling` (from the resolved sibling repository through its worktree and artifact copy; the sibling fetch is recorded on its own); `outcome`: `success`, `reused`, `failure`, `canceled`; `retried`: `true` when a stale holder forced a second worktree add |
| `armada.dock.artifact_copy.seconds` | `DockService` | `outcome`: `copied`, `refreshed`, `current`, `failure` |
| `armada.hostslot.wait.seconds` | `HostWideCommandLock` | `outcome`: `granted`, `canceled` |
| `armada.validation.seconds` | `DefinitionOfDoneGate`, `CheckRunService`, `MergeQueueService` (the command only; the slot wait is not included) | `kind`: `definition_of_done`, `check`, `merge_queue_test`; `outcome`: `passed`, `failed`, `timeout`, `canceled`, `error` |
| `armada.landing.seconds` | `LandingService` (integration-worktree landing), `MergeQueueService` (one pass of an entry through the landing state machine) | `path`: `local_merge`, `merge_queue`; `outcome`: `landed`, `landed_post_step_failed`, `failure`, `failed`, `canceled`, `pull_request_open`, `incomplete`; `retried`: `true` after a target-branch drift retry, or for a merge-queue pass that resumed an entry past Queued or sent it back to Rebasing |

Counters:

| Instrument | Meaning |
| --- | --- |
| `armada.docks.provisioned` | One per dock a provision returns |
| `armada.docks.reclaimed` | One per dock a reclaim moves to inactive |
| `armada.mergequeue.processed` | One per merge-queue entry that reaches a terminal state, labelled `outcome` |
| `armada.dock.artifact_copy.bytes` | Bytes of extraction artifacts written into sibling checkouts, labelled `outcome` (`copied`, `refreshed`) |

Labels are a closed vocabulary. No label carries a path, branch, commit,
credential, or mission, voyage, vessel or entry id, so the series count does
not grow with the fleet. Measurement adds no scan or loop: each value comes
from a stopwatch around work the owner already does, and the artifact byte
count comes from the source signature the copy already measures.

To compare quiet and busy windows, read the `_sum` and `_count` rates of each
duration over the window, the `_bucket` series for its distribution, and the
counters for volume. Repeated fetches show as a fetch count above the
provisions and landings in the same window. A cold provision shows as a large
`kind="primary"` checkout or a long `dock_provision` lock wait; a warm one as
a short checkout and an `artifact_copy` outcome of `current`. End-to-end
landing time, retries included, is `armada.landing.seconds`. Record values the
series do not show (for example clone size on disk) as unknown rather than
derived.

## Baseline Record

Write each accepted baseline to `docs/baselines/`. The record must contain the
window, capture time, raw cohort, verified result, evidence coverage,
unavailable measures, grouping coverage, scan bounds, and the concurrency
decision.
