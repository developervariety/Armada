# Test discovery and provider parity

The existing fork executables remain the test owners. No test folder or runner
was replaced. The shared Touchstone console, xUnit and NUnit adapters remain
separate and do not substitute for fork execution. The console runner now builds
with the solution and runs in the test gate; its ownership rules and failure
inventory are in [Testing](../TESTING.md#shared-suite-runner) and
[the case mapping](test-discovery-cases.md#shared-runner-failure-inventory).

## Registration and identity

Twelve compiled Unit suites and eight API suites were not registered. They now
execute: 42 Unit cases and 43 API declarations, including three setup records
and one cleanup record. The [reviewed case list](test-discovery-cases.md) records
fixture repairs and corrected names. Eighteen runner contract cases reject empty
runs, omitted registration, duplicate identities, invalid filters and missing
exceptions. Named skips require a reason; an all-skipped run fails.

The [case ownership map](test-case-ownership.json) compares exact baseline case
identities with discovery execution at `2fdca8e1`. It is a fixed evidence snapshot;
later test additions need new run manifests. Baseline console registrations and database
case declarations are retained. The baseline is the accepted foundation tree.
Portable PDB checksums bind current manifests to entry-project test sources used
at build. Symbol identity must match the executing assembly. This does not
certify every referenced production assembly.
This is case ownership evidence, not line-by-line assertion equivalence.
Use a new results directory and require a successful process exit. A previous
manifest is not evidence for a failed startup.

```sh
ARMADA_TEST_RESULTS_DIRECTORY=<new-results-directory> bash scripts/common/run-tests.sh
python3 scripts/common/map-test-cases.py \
  --manifest-directory <results-with-four-provider-manifests> \
  --baseline-manifest-directory <instrumented-baseline-results> \
  --output <case-map.json>
```

The [shared descriptor inventory](shared-test-inventory.json) contains 150 suites
and 2,291 distinct case descriptors. Descriptor construction executed no hooks
or cases. SQLite declares no skips; each server provider declares ten named
SQLite-only skips. This inventory preserves accessible ownership and exclusions;
it does not certify these adapters or authorize their fixture cleanup behavior.

## Defects exposed by registration

- Exception assertions caught their own missing-exception error. The helper now
  throws that error after the catch. A Postman mutation fixture now changes its
  intended input despite JSON whitespace.
- Pipeline expansion omitted RequiresReview and ReviewDenyAction, and the
  single-worker path bypassed review. Both expansion paths now copy the fields.
  Terminal manual review retains its dock until approval. The callback fixture
  does not certify actual landing or replace immutable Check gates.
- Relative health paths became file URIs. Only HTTP and HTTPS absolute URLs
  bypass base-URL resolution.
- Proxy fixtures shared dashboard assets. They now own isolated directories.
  Release, workflow and objective fixtures use real local Git commits. Provider
  failure fixtures use an explicit invalid runtime path without a live provider.
- Request-history tests wait for asynchronous capture. Cleanup runs in finally,
  checks delete status and verifies absence, and attempts all owned resources.
- Six vessel preview settings reset after reload on all four providers. Additive
  migrations and create/update/read mappings now preserve them. No applied
  migration declaration was changed. Preview settings do not add enforcement.

## Baseline measurement

The baseline identity run uses the foundation commit's suite registrations and
case bodies with the current result recorder and corrected exception assertions.
It records 3,651 Unit, 907 API and 183 runtime identities. Unit reports one
failure: the old Postman mutation did not alter its intended JSON input. This
baseline run is identity evidence, not a passing baseline certification. The
current fixture correction preserves that case and its negative assertion.

## Validation scope

The combined fork run passed 4,042 Unit, 950 API and 183 runtime cases, with no
skips. Wall time was 306 seconds; suite times were 299,339 ms, 157,779 ms and
24,311 ms. Dashboard passed 77 tests. The incremental solution build emitted
106 warnings and zero errors; this is not a clean-build warning census.

Twelve process-level invalid-option/filter checks returned nonzero exits. A
matching-symbol control passed; substituting an unrelated PDB failed without
writing a manifest. The original 274 migration declarations remain protected,
and all ten migration-guard controls passed.

Provider scenarios use
separate provisioned databases; no live application database is reset. The new
preview migration fixture preserves old version rows and existing values through
failure and restart. MySQL rejects latin1 and utf8mb3 preview text columns.

No speed improvement is claimed. No deployment, live provider account collection
or routing policy activation is part of this work.
Backend enrichment, actual gate behavior and later campaign decisions remain open.

A repeated provider run exhausted the 1 GiB MySQL fixture container after many
completed databases accumulated. Three scenarios reached the unchanged 90-second
limit. Completed successful fixture databases were removed, and the isolated
container limit increased to 2 GiB. Timeout results remain in the test evidence;
all nine MySQL scenarios then passed on fresh databases, with the same timeout.
All 33 distinct provider/scenario combinations pass. Final ordinary manifests
contain 53 SQLite, 53 PostgreSQL, 54 MySQL and 53 SQL Server cases. Six map
mutation controls reject case replacement at equal count, duplicate identity,
missing registration, stale source, absent provider and an unreviewed skip.
No production database or service was changed.
