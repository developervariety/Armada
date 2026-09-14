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
- Every linked voyage has status `Complete`. Historical failed-and-rescued
  voyages stay unverified until Armada retains enough chain history to prove
  each independent recovery.
- At least one implementation-producing mission has a commit hash and reached
  `Complete`. A read-only objective must instead have an explicit read-only
  delivery classification.
- Each independent delivered tip has a mission-linked `Landed` merge entry.
- Each participating Check for each delivered tip's commit is `Passed`. A
  Check for another commit is not current. A missing current Check is not a
  pass.

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
- Host-slot queue time is unavailable until Armada records when a Check starts
  to wait for `HostWideCommandLock`. The `check.auto_queued` event occurs before
  profile resolution and isolated-checkout preparation, so it is not a pure
  host-slot queue timestamp.
- Execution time is `DurationMs`.

Do not add the two values and call the result execution time. Report the
median, p90, p95, count, and coverage. The current surface groups these values
by source family and work type. A later surface can add Check-type and vessel
dimensions without changing the timing definitions.

Report host-slot queue coverage separately. Do not substitute armed-to-start
or `check.auto_queued` time for this value.

## Report Surfaces

- REST: `GET /api/v1/production/summary`
- MCP: `armada_production_summary`

Both surfaces use a seven-day default window and reject windows longer than 90
days. Optional `sourceFamily` and `workType` filters use the grouping rules in
this document.

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
- a bounded machine reason code. Free-text failure messages are not stored.

Facts are recorded when a captain process launches, when a transient captain
failure or captain recovery relaunch runs the mission again, when a Judge is
re-run in place, when a mission is restarted from any client, when a review is
denied, when a mission fails or its landing fails, and when work lands.

A mission that ran before facts existed has no `AttemptStarted` fact. The
summary classifies it as historical and does not guess its history.

### First-pass acceptance

The denominator is a verified slice whose every run in the attempt chain has
an `AttemptStarted` fact. The numerator is an eligible slice whose chain has
none of these facts:

- `Failed`
- `ReviewDenied`
- `Restarted`
- `Retried`, except a Judge re-run that only waited for Checks
  (`judge_check_wait`)
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

The preferred landing source is the `CompletedUtc` of a mission-linked
`Landed` merge entry. If the entry is absent because of another landing mode
or expiry, do not silently substitute a mission timestamp. A report can show
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
  the `obj_` prefix. A landed commit must be 7 to 64 hexadecimal characters.

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

This measure needs time-series samples or state-change intervals that combine
dispatch preflight eligibility, shared-lane occupancy, and capacity. Current
entity end states cannot reconstruct those intervals. Until Armada records
them, return `unavailable` with reason
`lane_eligibility_intervals_not_recorded`.

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

## Baseline Record

Write each accepted baseline to `docs/baselines/`. The record must contain the
window, capture time, raw cohort, verified result, evidence coverage,
unavailable measures, grouping coverage, scan bounds, and the concurrency
decision.
