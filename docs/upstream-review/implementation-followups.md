# Implementation follow-ups

Implementation takeover checkpoint: 2026-09-13, source and deployed baseline
`b4b79b3e`. The security deployment is confirmed by the running image and its
restart time. The previous status-only review flow is now implementation work:
local agents implement isolated changes, and the root reviewer accepts them
before landing. Automatic Armada dispatch remains disabled. Operational
objectives remain the authority for assignment, evidence and decisions.

The owner changed the final dashboard direction: port upstream appearance and
interactions as the last implementation task, preserving Chatroom and captain
provider configuration fields. Retain Routing V2, objective preparation and claim
evidence, and richer quarantine, recovery and landing controls. Use upstream Inbox;
remove separate Notifications history and Code Index pages. This supersedes incremental layout polish, while server safety
and compatibility contracts remain intact. No final dashboard acceptance is
claimed by earlier partial browser or test evidence.

Current accepted SDK source is `4a0787e5`: supported profile, skill, Ask,
objective, refinement, delivery, list and enumeration wrappers are present.
The combined client suite ran 86 tests with no failures or skips. Helm,
Postman, branch-client coverage and isolated HTTP round trips remain open.
These changes are not deployed.

Harbor and self-rebuild require hardened implementations. Harbor must validate
credentials; self-rebuild must stop after backup failure. Keep existing deployment
and rollback controls until replacement acceptance.

Ask launch and discovery fixes landed in `cb2ccb14`. Temporary chat runtimes
receive their MCP configuration; the UI reports the planned endpoint separately
from an active mission. Root checks passed: chat/discovery 11, launch planner 11,
runtime 184, captain API 76 and rendered UI 7. The dashboard bundle was rebuilt.
Deployment and a live read-only Ask tool call remain unverified.

Read-only branch inspection landed in `c69d5627`. It uses persisted paths,
preserves refs, and reports corrupt HEAD as an error. Root checks passed:
89 API tests and 60 Git service tests. Optional writes remain absent.

Crash-loop protection landed in `b4ed7ab6` through the existing quarantine
service. It preserves active work and stronger or indefinite holds. Root checks
passed on SQLite (69), PostgreSQL (69), MySQL (70), and SQL Server (69).
The PostgreSQL comparison and null parameter defects found during review were
fixed without schema changes. The unused health monitor was removed. These
changes are not deployed.

Advisory UI labels landed in `8f6cd7c2`, with four rendered tests passing.
The response model documentation carries the same wording in `e16bd1b9`.

Model endpoint persistence remains unaccepted. The provider review found and
fixed a MySQL string-default mismatch. Fresh installs and historical upgrades
pass on all four providers, but the MySQL partial-index restart still fails.
The service candidate also needs a scoped health sweep, safe credential handling,
real model validation and health writes that preserve concurrent configuration.
Do not enable endpoints or advance the objective on the persistence result alone.

Self-rebuild has a candidate preflight gate and a default refusal provider.
Provider-native backup, isolated restore and candidate/process proof remain in
progress. An interface and mocked success flags do not satisfy deployment proof.

A fetch of both remotes found a later upstream Linter persona addition. The
comparison remains pinned to the accepted review anchor. The new persona and
FullPipeline change need a separate disposition; do not change active pipelines
as an incidental part of this integration.

Keep each entry until it has a disposition and evidence. Use these states:
**Open** (confirmed source gap), **In progress** (implementation exists but is
not accepted), **Verify** (a concern or missing proof, not a confirmed runtime
defect), **Closed** (landed fix plus evidence), or **Deferred** (explicit reason
and decision). Recheck source before acting; another session is implementing
these areas. Do not duplicate its changes.

| ID | State | Item |
| --- | --- | --- |
| FOLLOWUP-001 | Closed | Landing retry scope and failure logging landed |
| FOLLOWUP-002 | Closed | Quarantine landed with recorded provider and combined acceptance |
| FOLLOWUP-003 | Verify | Recovery report landed; large-vessel read cost and history completeness remain |
| FOLLOWUP-004 | Closed | DoD validation and bounds landed with recorded adversarial proof |
| FOLLOWUP-005 | Closed | Advisory preview wording and newer failed Check warning landed |
| FOLLOWUP-006 | Closed | Unused monitor retired after shared crash-loop protection and provider tests |
| FOLLOWUP-007 | Verify | Final combined tree and deployed behavior need separate certification |
| FOLLOWUP-008 | Closed | Shared event owner resolution landed; delivery review remains in final verification |
| FOLLOWUP-009 | Verify | Scoped merge-entry repair landed; filter and pagination coverage review remains |
| FOLLOWUP-010 | Verify | Route matrix repaired; browser workflow proof remains |
| FOLLOWUP-011 | Verify | Wildcard captain assignment needs actual dispatch-path regression coverage |
| FOLLOWUP-012 | Closed | Owner approved advisory-only setting; UI and API documentation agree |
| FOLLOWUP-013 | Open | Top-level OpenCode errors need a captured event and regression proof |
| FOLLOWUP-014 | Verify | WebSocket exposure blocked by admin-only subscription; scoped delivery remains open |
| FOLLOWUP-015 | Open | Persona, pipeline and prompt-template read visibility remains unscoped |
| FOLLOWUP-016 | Open | Manual Complete without an active dock bypasses landing proof |

## FOLLOWUP-001 — Landing retry event

Closed for recorded implementation acceptance in `bda6eadb`, then consolidated
onto `EventOwnerScope.ApplyFromMission` in `57c3667e`. Previously the writer
omitted owner scope and silently discarded write failures. It now carries the
mission owner, logs failures and propagates caller cancellation.

Recorded before/after: owner scoped retry-event count 0 -> 1; another user sees
0. Landing Service suite 12/13 -> 13/13. Combined gate at `bda6eadb`: 3,854 unit,
971 API, 183 runtime, no failures/skips. Earlier events are not backfilled.
Independent final review should still include cancellation and event-store
failure behavior; the checkpoint did not rerun those paths.

## FOLLOWUP-002 — Quarantine acceptance

Closed for implementation acceptance at `862ad420`. The remote main ref and
source commit were checked. The implementation session recorded:
- Before: working captain ownership was cleared by bench, release forced Working
  to Idle, a probe released a newer indefinite hold, and provider-group bench
  cleared ownership on busy siblings.
- After: shared scoped REST/MCP/dashboard controls use conditional writes;
  timed release remains conditional; busy siblings retain ownership. Superseded
  direct writes and old bench/release paths were removed.
- Recorded combined result: 3,848 unit, 970 API, 183 runtime; zero failures/skips.
- Recorded provider results: SQLite 68, PostgreSQL 68, MySQL 69, SQL Server 68.
  The PostgreSQL text-expiry cast was included. The earlier failure remains
  documented in `foundation-postgresql-legacy.md`.
- Recorded dashboard result: nine targeted tests passed; strict type check clean.

These are the implementation session's acceptance records, not a new independent
rerun. Deployment and final combined review remain FOLLOWUP-007. Captain health
monitor disposition remains FOLLOWUP-006. Reopen this item if the later review
finds a regression or an uncovered historical expiry shape.

## FOLLOWUP-003 — Recovery report

`MissionRecoveryReportService`, its DTOs, route and unit cases landed in
`862ad420`. See `backend-recovery.md`. The recorded combined result is 3,848
unit, 970 API and 183 runtime tests, with zero failures/skips. Scope, recorded
counters, rescue relations, incident/runbook data, redaction and window limits
have registered tests. This checkpoint did not rerun them.

Keep this entry open for independent scope review, large-vessel read-size proof
or an explicit accepted limit, and historical record limits. Retry and generic writer scope are now fixed
for new records; earlier unscoped records remain unbackfilled.

Required completion:
- Validate ordinary user, tenant-admin and admin reads, including another user
  in the same tenant, linked incidents/runbooks and rescue children.
- Keep a rescue's Complete status separate from evidence that its work landed.
- Preserve per-section unavailable reasons and prove reads execute no recovery.
- Measure large-vessel loading. The current approach reads vessel mission
  summaries before filtering rescue children, and the incident service filters
  in memory. A bounded response does not prove a bounded database read.
- Keep the 100-event window limit visible; older recovery events can fall
  outside it. FOLLOWUP-001 is fixed for new writes; do not claim backfilled
  or complete historical coverage.
- Record an explicit disposition for accepted read-size limits or further query
  work. The implementation is landed; this remaining verification is not a
  request to implement the report again.

## FOLLOWUP-004 — DoD record validation

Closed for recorded implementation acceptance in `82097986`. Before, the
report accepted contradictory results and an oversized payload, and labels
retained secrets and unbounded text. Recorded suite result: 13/16 -> 16/16.
The reader now rejects negative counts and contradictory writer shapes and
checks a 262,144-character cap before parsing. Labels are redacted and bounded
to 1,000 characters on write and read. Twelve invalid shapes return Unavailable.

Full Unicode identifiers remain intact. A worst-case supported writer record
measured 40,951 characters and remained readable. Reversed timestamps have an
explicit accepted limit: a real clock step can produce them, so they remain
Recorded. See `backend-dod.md`. Recorded combined gate: 3,871 unit, 977 API,
183 runtime, zero failures/skips. This pass did not rerun it. Historical results
still do not certify a different attempt or current commit, and aggregate DoD
success is not proof that a consumer test ran. Deployment remains FOLLOWUP-007.

## FOLLOWUP-005 — Landing preview and Check authority

Closed for recorded implementation acceptance in `2933e232`. An older pass
followed by a newer failed Check previously produced no preview issue. The same
case now reports `latest_check_not_passed` as a warning. Recorded service suite:
25/27 -> 27/27. Readiness and execution gates are unchanged.

One shared card replaces three page copies. It says "No blocking preview
issues" and identifies the evidence as advisory, not the immutable Check gate.
Recorded dashboard result: 152 tests and build passed. The new card's missing
import before-case is weak behavior evidence; browser proof remains in
FOLLOWUP-010. The preview still reads at most 1,000 scoped runs and an older
pass can set HasPassingChecks. This is an accepted advisory limit, not stronger
landing authority. The separate vessel-setting decision is FOLLOWUP-012.

## FOLLOWUP-006 — Captain health monitor disposition

A source-reference census found `CaptainHealthMonitor` and
`ICaptainHealthMonitor` definitions plus unit tests, but no production caller.
Tests of this standalone class do not prove it participates in live crash-loop
quarantine.

The owner approved retirement after generic crash-loop protection used the
shared service. That change landed in `b4ed7ab6`, with provider and process-exit
regressions. The standalone monitor and its tests are removed. No second
quarantine authority remains. Deployment proof stays in the final rollout gate.

## FOLLOWUP-007 — Final evidence and rollout

Latest recorded combined gate at `b4b79b3e`: 3,891 unit, 999 API and 183
runtime passed. Idle unauthenticated WebSocket tests recorded 99/100 -> 100/100:
the connection stayed open for 30 seconds before, then closed at 15 seconds
after. Planning WebSocket tests recorded 2/2. Earlier coordination authorization
tests recorded 46/47 -> 47/47; cross-tenant API tests 85/87 -> 87/87. The latest dashboard result
previously recorded at `2383853c` remains 156 tests passed; these later
commits contain no dashboard changes. These are implementation-session records,
not an independent rerun.

The observability commit's first gate missed later script edits. The next gate
found the stale fixed-framework assertion (3,878/3,879 unit). `cda05e90` repaired
the guard to check resolver delegation and the default framework; Release
Version tests recorded 9/10 -> 10/10. The later `584f3d38` combined gate includes
that repair. Keep this failure history; do not use the earlier gate as proof of
the full observability commit. Actual configured collector delivery remains a
rollout check. Telemetry remains disabled by default.

The auto-land report landed in `bda6eadb`. It distinguishes recorded decisions
from predicate results, current settings from historical predicates, and missing
or tied records from valid history. Final review must test a decision for a
different entry from the newest merge entry; consumers must not attach the
newer audit result to the older decision. A trigger does not prove landing.

The security deployment now runs the accepted baseline. The implementation
session recorded an isolated corrected backup restore and two schema-validation
passes before deployment, schema 86 -> 88, dashboard and Helm refresh, and
retention of the previous image. The root reviewer confirmed the new image,
restart time and zero restarts. The prior anonymous WebSocket exposure is closed
by the deployed authentication rules; the recorded public probe received
auth.required and close code 1008. No destructive command was sent.

This deployment does not certify remaining features or the final dashboard.
Final browser workflows, served artifact and combined provider evidence must
still match the final accepted source. Retain the original exposure and restore
failure history in objective evidence. The scheduler remains paused.

Required completion:
- Independent review and relevant combined checks on the final source tree.
- Applicable four-provider fresh, partial-restart, historical upgrade and
  persistence proof. The `bda6eadb` fresh runner result was SQLite 69,
  PostgreSQL 69, MySQL 70, SQL Server 69, all passing; this is not a newly run
  historical/partial-failure matrix for every later change.
- Browser and route/action evidence in FOLLOWUP-010; deployed image/schema,
  served bundle, and endpoint checks, including retired endpoints.
- Preserve routing constraints, immutable Checks, recovery, incident mitigation,
  retired lead/Grok state and disabled automatic dispatch.
- Finish final dispositions before archiving review plans and inventories.

## FOLLOWUP-008 — Generic event ownership

Closed for recorded database-writer acceptance in `57c3667e`. Current source
uses shared `EventOwnerScope` in both generic helpers, with mission-in-hand
scoping for architect and papercut records and consolidation of duplicate
mission writer rules. The resolver names lookup failure and no-owner outcomes.
Ownerless scheduler events and deleted/no-named-entity records stay deliberately
unscoped; earlier records are not backfilled.

Recorded before/after owner-scoped event count 0 -> 1 covers the Admiral exit
path, Architect over-cap, papercut output and the server status-change API.
Recorded combined gate: 3,862 unit, 972 API, 183 runtime, no failures/skips.

Database scoping alone does not certify live-event isolation. WebSocket
authentication landed in `2383853c`; `254b221e` now limits subscriptions to
global administrators (FOLLOWUP-014). `76b75a14` limits all coordination REST
routes to global administrators because rooms remain shared. This blocks access
by narrower roles; it does not add tenant-specific rooms. Tunnel delivery and
all relevant client paths still need final review.
Reopen this entry if the final writer inventory or owner-lookup failure tests
find a defect.

## FOLLOWUP-009 — Scoped merge-entry filters

The repair landed in `bda6eadb`. SQLite and SQL Server tenant and tenant-plus-user
enumerations now apply MissionId, VesselId and Status. Previously a scoped report
could show another mission's newer entry within the same caller scope.

Recorded before: two-mission unit report selected the wrong entry; SQLite
provider test expected 1 entry and got 2. Recorded after: both passed; all four
fresh provider runners passed (SQLite/PostgreSQL/MySQL/SQL Server 69/69/70/69).
SQL Server before-fix behavior was not measured separately. No migration changed.

Keep Verify for remaining coverage review: the added database case checks
mission in both scopes and a nonmatching vessel in tenant scope. Confirm status
and vessel in both scoped overloads, TotalCount, ordering and pagination across
a page boundary. The repair is landed; do not implement it again.

## FOLLOWUP-010 — Dashboard workflow proof

Eleven dashboard slices are landed through `431c2dc7`: bounded home summaries,
coalesced refresh, resource-pressure state, readable captain and mission logs,
markdown with raw copy, mission modes, dock Git evidence, incident recovery,
inherited-pipeline captain assignments, and vessel auto-land editing/display.

The vessel-save defect was repaired in `d6085789`: old flat auto-land form fields
were not stored and saves cleared the real predicate; PUT could also reset
server-owned fields. Recorded Vessel API before 54/56 -> after 56/56. Existing
configuration is now retained and the form edits the actual predicate.

Required completion:
- The route/action matrix is now in `dashboard.md`. `584f3d38` repairs five dead
  calls: recall and restart controls removed, voyage status uses summaries,
  and merge processing/cancel use served routes. Event-by-ID reads now have a
  scoped route (recorded API 404 -> 200; unknown ID stays 404). The source guard
  passes 2/2. Confirm behavior and authorization through real page actions;
  matching method/path strings is not runtime route certification.
- Run real list/detail/modal flows, keyboard actions, both themes, narrow/wide
  viewports and empty/error/loading states. Verify saved data after reload.
- Use an authenticated isolated fixture. The implementation session reports no
  browser verification because its local login step is unresolved. Record the
  concrete access prerequisite or establish an approved fixture session; this
  checkpoint did not verify that password entry is prohibited or unavoidable.
- Confirm the deployed dashboard bundle is built from accepted source.
- Verify restricted-role behavior after the new authorization rules: WebSocket
  subscription refusal, REST refresh, Inbox/Ask, coordination and asset writes.
  These commits add no dashboard changes. Confirm clear permission feedback and
  continued allowed workflows, not only administrator behavior.
- Chat tool previews, totals and follow behavior landed in `a39fdcbb`.
  `7bdb2d27` removes activity records from answers and stored planning messages
  and emits tool cards. Captured-record tests reproduced polluted answers before
  and clean text after. Browser chat/layout acceptance remains open; do not
  describe these landed consumer changes as missing implementation.

Several reported before cases fail because a new module did not yet exist;
those are weak behavior evidence. Strengthen page-level proof where needed.

## FOLLOWUP-011 — Wildcard captain assignment

`fc79d2ba` adds one shared selection rule: exact persona first, then `*` or empty
persona fallback. Inherited dispatch offers an All steps assignment. Recorded
preview regression failed before, passed after; exact-over-wildcard guard stayed
green. This changed mission resolution as well as the UI.

The implementation record explicitly says WorkerOnly and busy-preferred-captain
dispatch were not re-tested, and mission resolution has no separate end-to-end
case. Before final acceptance, test the actual assignment path with those cases,
missing/ineligible overrides, exact/wildcard precedence and explicit persona/model
constraints. Preserve Routing V2 and provider/account eligibility. Use isolated
test fixtures; do not dispatch operational work to obtain proof.

## FOLLOWUP-012 — Passing-checks setting disposition

Closed by owner decision: the setting is advisory-only. Vessel forms, detail
views and landing previews state that limit in `8f6cd7c2`; response model XML
documentation agrees in `e16bd1b9`. Four rendered tests passed. Actual immutable
Check, Judge and landing gates remain unchanged. Deployment verification remains
part of the combined closeout.

## FOLLOWUP-013 — OpenCode top-level errors

Runtime answer cleanup is landed and accepted in `7bdb2d27`, but neither compared
adapter parses a top-level OpenCode error event. The implementation record says
no captured sample exists. Obtain a non-secret real JSON event fixture, then
prove that the error produces a named chat/planning failure and activity record,
not an empty answer. Do not guess the schema. A live provider capture is listed
as requiring owner approval in the operational follow-up; an owner-supplied
capture can also supply the missing prerequisite. No such call was made here.

## FOLLOWUP-014 — WebSocket and MCP scope

`2383853c` closes anonymous WebSocket reads and commands. At that commit an
authenticated ordinary user could still receive global snapshots and broadcasts.
`254b221e` now rejects subscription unless the caller is a global administrator.
The send loop checks Subscribed, and activation follows the new role guard.
Narrower callers receive `subscribe.forbidden`; their connection stays open to
avoid reconnect loops. The earlier working-tree fix is now landed. `b4b79b3e`
also closes silent unauthenticated sessions after 15 seconds; the recorded
before/after case is in FOLLOWUP-007. Header-authenticated and frame-authenticated
sessions are intended to remain unaffected; final review should cover both.

This is an interim restriction, not tenant-filtered delivery. Keep Verify for
independent authentication/subscription/broadcast tests and an explicit final
disposition of narrower-role live updates. Test REST refresh and permission
feedback in real dashboard sessions. Preserve global-admin behavior and command
denial for narrower roles. No new live provider or operational action is needed
to test these boundaries.

MCP still uses the fixed operator scope and local operator bridge policy.
Any authenticated replacement needs its own proof; WebSocket changes do not
certify MCP scoping. Coordination REST access is now admin-only in `76b75a14`,
but rooms remain shared by key. Deployment remains FOLLOWUP-007.

## FOLLOWUP-015 — Asset read visibility and remaining user scope

`6cc666ac` restricts persona/pipeline writes to tenant administrators, resolves
update/delete inside their tenant, and derives create ownership from the caller.
Request bodies cannot create built-in records. Prompt-template writes require
a global administrator because templates are shared. These repairs are landed.
The implementation census still identifies persona, pipeline and prompt-template
reads as unscoped; the user/auth objective remains InProgress.

Finish the read-visibility contract across list, enumerate, detail/name lookup
and consumers. Cover tenant-wide and user-specific assets, built-in/shared
visibility, same-name records, cross-tenant denial and same-tenant user boundaries.
The prior implementation session recorded a delegated decision to defer full
read ownership while all production users are global administrators. It also
deferred MCP replacement and tenant-filtered live events. These are explicit
remaining capability limits, not implemented scope isolation. Revisit only as
accepted campaign work with the required schema and client compatibility proof. Full ownership
would reuse the existing memory visibility rule; coordinate through its work
rather than modifying the separate native-memory port. Any schema additions must use new provider-specific migration numbers and
retain full Unicode identifiers and full-value uniqueness. Require applicable
fresh/upgrade/restart/persistence proof before accepting schema work.

`c763ac2b` also restricts fleet-wide Inbox and Ask to global administrators and
resolves captain-chat ownership before starting a runtime. Final review should
check cross-tenant and same-tenant ownership, denied calls starting no runtime,
and allowed administrator/owner calls through isolated fixtures. The recorded
mutating-route census was corrected from 120 to 196 registrations with a pattern
that handles generic types; do not use the incomplete earlier count as coverage.
Write-route coverage does not certify reads or MCP. Keep the remaining route
inventory and final dispositions visible until user/auth acceptance is complete.

## Broader backlog coverage at this checkpoint

The status pass enumerated all 1,433 objective records across 15 pages and
checked current and historical Armada vessel associations, unassigned rows,
titles, tags and relevant descriptions. It found 46 unfinished Armada product
implementation/review rows, plus 3 related workflow or rollout rows. The open
campaign parent is a separate tracking row. Within the 46 product rows, 14 are
campaign children and 32 are outside that campaign. All 47 product/parent rows
have automatic dispatch disabled. This is a record count, not a count of distinct
missing features; operational objectives remain the live inventory.

Campaign work still includes user/auth, dashboard behavior and styling, health
monitor disposition, an OpenCode error fixture, the passing-checks setting
choice, native-memory acceptance, branch inspection, SDK/CLI, optional model
endpoints, Harbor and self-rebuild decisions, final verification, and final
retirement/archive dispositions. Native memory includes landed changes and
belongs to the other session; its open row does not mean nothing was implemented.

Additional product areas outside the campaign need reconciliation before a
claim that all Armada fork work is done:

- Brief policy and false-refusal recovery; memory folder-name matching;
  runtime/account binding; execution-runtime dispatch preflight.
- Deployment incident closeout, including previous-image retention; database
  test-command documentation.
- Rescue effectiveness for committed documentation; dispatch-hold enforcement;
  recovery pipeline preservation.
- Objective terminal/backlog consistency, admission atomicity, lock lifetime,
  admission timeouts and orphan reconciliation.
- Production-review closeout, baseline metrics, attempt/rescue facts,
  regression linkage, preparation reuse and Check host-slot measurements.
- Test timing under load; independent anti-reward-hacking checks.
- Branch cleanup, ref-deletion events and operator StartFromRef propagation.
- Silent-stall detection, Judge verdict/process lifecycle and duplicate
  process-exit handling.
- Brief size and stage-handoff size; campaign rooms and aggregate presence.

Three related rows concern historical report/decision reconciliation,
brief-authoring quality, and a cross-repository CI/CD pilot. Keep them visible
without treating the pilot repository's implementation as Armada source work.

Reconciliation cautions: three Draft rows describe the same duplicate-process-
exit symptom; two InProgress rows cover overlapping brief/handoff size work.
Native-memory and schema-incident rows already contain landed or applied work
but retain acceptance tasks. Do not merge, close or discount any of these rows
without checking their evidence and remaining criteria. No objective was changed
by this pass. This backlog review does not reopen or restore retired lead assets.

## Closure record

For each closed entry, add: final source commit, reproduction before the fix,
verification after it, provider/UI scope where relevant, deployment state, and
remaining limits. Do not erase earlier failed evidence. Campaign objectives
remain the authority for assignment and operational status.

## FOLLOWUP-016 — Manual completion without landing proof

At `0152653c`, the mission status route writes `Complete` directly when no
active dock is available. It logs `mission.manual_complete_no_dock` but checks
neither target ancestry nor immutable Checks. The existing automated test
`ManualComplete_NoDock_EmitsAuditEvent` accepts this behavior. This does not meet
the held Review acceptance rule. Add a regression for unlanded held work and
route completion through the shared proof gates. Keep valid report-only stage
completion distinct from code landing. Also audit active-dock manual completion,
which calls the landing handler directly rather than the Judge completion path.
No production mission was changed during this review.
