# Test discovery and provider parity

Armada's four test executables own the gate. The shared Touchstone console,
xUnit, and NUnit adapters are separate entry points; a descriptor inventory
alone does not execute tests. See [Testing](../TESTING.md#shared-suite-runner)
and the [reviewed case mapping](test-discovery-cases.md#shared-runner-failure-inventory).

## Registration and identity

Every executable must register each suite and call each case. Runner contracts
reject empty runs, omitted registrations, duplicate identities, invalid filters,
and missing expected exceptions. Named skips require a reason; an all-skipped
run fails.

Generate a case ownership map from fresh current and baseline manifests.
Generated maps are local run artifacts, not tracked docs. Portable PDB checksums
bind a manifest to the entry-project test sources used at build. Symbol identity
must match the executing assembly. This does not certify every referenced
production assembly or line-by-line assertion equivalence.

Use a new results directory and require a successful process exit. A previous
manifest is not evidence for a failed startup.

```sh
ARMADA_TEST_RESULTS_DIRECTORY=<new-results-directory> bash scripts/common/run-tests.sh
python3 scripts/common/map-test-cases.py \
  --manifest-directory <results-with-four-provider-manifests> \
  --baseline-manifest-directory <instrumented-baseline-results> \
  --output <case-map.json>
```

## Fixture contracts

- Exception assertions throw their missing-exception error outside the catch.
- JSON mutation cases verify that the intended input changed.
- Pipeline cases preserve review fields and use immutable Check evidence.
- Health paths treat only HTTP and HTTPS absolute URLs as external URLs.
- Proxy and server fixtures own isolated directories and settings. Repository
  fixtures use real local commits; invalid-runtime cases start no provider.
- Request-history cases wait for capture and verify cleanup of owned records.
- Provider scenarios use isolated databases and preserve migration history.
  Storage assertions do not prove dispatch or landing enforcement.

## Evidence limits

The case mapping records reviewed discovery and ownership decisions. Use the
current executable's registration and fresh manifests for current counts.
A known fork difference must name its disposition; a duplicate must identify
the executing case that owns its behavior. Neither is an unreported pass.
Provider scenarios and ordinary cases have separate results. Report failures,
named skips, and missing manifests rather than substituting a past green run.
