# Design — `leak_hunk`: a per-hunk leak classifier behind the boundary hook (D7)

Status: design only. This document describes a decision point. It wires no code,
changes no test, and adds no migration. Implementation is a later row, opened
only after the owner reviews this design. Decision id: `leak_hunk`. Default
mode: **Gate** in the shipped settings default, with no effect until an adapter
is wired for this decision.

## Purpose

The deterministic dock-boundary scanner blocks a known set of leaks: protected
paths, `CORE_RULE_5` secret patterns, and configured private-identifier
denylists on public repositories. A denylist can only match names it already
knows. A private context can still reach a repository artifact in a shape no
pattern lists: a paraphrased operator note, an unlisted host or path form, a
customer detail woven into a comment, an orchestration identifier spelled a new
way.

`leak_hunk` adds an **advisory second pass**: a typed-decision model reads each
added hunk after the deterministic scanner has already run, and proposes whether
that hunk leaks private context the patterns missed. It never replaces the
scanner, never gates a landing by itself, and is off until the owner turns it
on.

## Non-negotiables

1. **The deterministic path runs first and unconditionally.** `DockBoundaryScanner`
   and its two call sites decide the block. The model is a strictly additive
   pass that runs after the scanner and cannot suppress, weaken, or skip any
   deterministic finding.
2. **Nothing leaves unredacted.** Every hunk passes through
   `DecisionStateRedactor` before egress. The redactor is the same one the rest
   of the typed-decision system uses. State is recorded as a SHA-256 and a byte
   count, never as text.
3. **The model informs; it never blocks, deletes, or dispatches.** A model
   concern becomes a flag on the record and, at most, a board note. It cannot
   fail a mission, fail a merge entry, or stop a landing on its own.
4. **Gate flags only.** The settings default is `Gate`. In `Gate` mode it flags; it
   never becomes a hard block for a landing without a separate owner decision
   recorded on the row.

## Seams at the base tip

Line references are to the lane's integration base tip; re-verify after later landings.

### The deterministic scanner (stays first, unconditional)

- `src/Armada.Core/Services/DockBoundaryScanner.cs:16` — `class DockBoundaryScanner`.
- `src/Armada.Core/Services/DockBoundaryScanner.cs:41` — `Scan(unifiedDiff, changedFilePaths, vesselId, vesselName, vesselRepoUrl, configuredProtectedPaths, settings)`, the single scan entry point.
- `src/Armada.Core/Services/DockBoundaryScanner.cs:170` — `ParseAddedLinesByFile(unifiedDiff)` already groups the added lines per file. This is the natural place to derive per-hunk state; the model pass reads the same structure the scanner built, after `Scan` returns.
- `src/Armada.Core/Services/ConventionChecker.cs:36-70` — `CheckSecretLine` and the `CORE_RULE_*` pattern table, including `CORE_RULE_5_seed_literal`. This is the deterministic secret gate that always wins.

### Boundary-scanner call sites (the pattern hook — both stay unconditional)

- `src/Armada.Core/Services/MergeQueueService.cs:1148` — `ScanDockBoundaryAsync` result checked in `TestIntegrationWorktreeAsync`; a non-pass transitions the entry to failure and cleans up.
- `src/Armada.Core/Services/MergeQueueService.cs:1685` — `new DockBoundaryScanner().Scan(...)` inside `ScanDockBoundaryAsync` (method at `:1664`), which collects the unified diff against the target branch.
- `src/Armada.Core/Services/MissionService.cs:8939` — `new DockBoundaryScanner().Scan(...)` inside `TryFailMissionForBoundaryViolationAsync` (method at `:8932`), the pre-land scan that fails a mission with a redaction-safe reason.

### Typed-decision foundation (already landed, reused unchanged)

- `src/Armada.Core/Services/Interfaces/ITypedDecisionClient.cs:13` — `ITypedDecisionClient.DecideAsync`; advisory, never throws into the caller.
- `src/Armada.Core/Services/DecisionStateRedactor.cs:69` — `Redact(text, maxChars)`; `:94` — `RedactObject(state, maxChars)`.
- `src/Armada.Core/Models/TypedDecisions.cs:10` — `TypedDecisionRequest`; `:59` — `NoulQuestion(Instructions, TrueMeaning, FalseMeaning)`; `:66` — `TypedDecisionResult`; `:105` — `TypedAnswer`.
- `src/Armada.Core/Settings/TypedDecisionSettings.cs:96` — `For(decisionPoint)`; `:122` — `["log_watch"]` / `:121` `["leak_hunk"] = ... Mode = Off` default.
- `src/Armada.Core/Services/TypedDecisionRecorder.cs:29,35,38` — `EventTypeGated` / `EventTypeShadow` / `EventTypeUnavailable`.

## The seam for the model pass

The model pass sits **after** each `Scan(...)` call returns, in the same caller,
guarded by `settings.TypedDecisions.For("leak_hunk").Mode != Off`. It reuses the
per-file added-line grouping the scanner already computed, so it reads exactly
the added text the deterministic scan read — no second diff, no re-walk.

Two callers hold the pass through an adapter (`LeakHunkAdapter`), one each:

- Merge-queue: `MergeQueueService.ScanDockBoundaryAsync`, after `:1685`.
- Pre-land: `MissionService.TryFailMissionForBoundaryViolationAsync`, after `:8939`.

The adapter is the standard typed-decision adapter shape: `Off` returns
immediately with no model call; unavailable records an event and returns; a
below-threshold answer records a shadow event and returns; an at-or-above
answer records a gated event and attaches a flag. In no branch does the adapter
change the scanner's `Passed` verdict.

## State (one call per added hunk)

Egress class **G** (source hunks), which the owner approved provisionally for
redacted transmission. Per hunk:

- The added hunk text, capped at **60 lines** and redacted through
  `DecisionStateRedactor` at `TypedDecisions.MaxStateChars`.
- The repository-relative **file path**.
- The vessel's **public display name** (never the vessel id).

Nothing else. No mission id, no voyage id, no dock path, no operator identity —
the redactor strips Armada ids, absolute paths, hosts, hex, and key-shaped
tokens, and the caller passes only the three fields above. The recorder stores
`state_sha256` and `state_bytes`, never the hunk.

Bound the call volume: at most the first N added hunks per changed file and a
per-scan hunk cap (default informed by the four-week review), so a large diff
cannot fan out into an unbounded model spend. A scan over the settings timeout
budget is unavailable, not late.

## Questions

One `NoulQuestion` per hunk:

- `leaks_private_context` — Noul. `TrueMeaning`: "this added hunk carries
  private operator, customer, or orchestration context that does not belong in
  a repository artifact — a leak the deterministic patterns did not catch."
  `FalseMeaning`: "this hunk is ordinary product content."

The instructions state the domain explicitly (see Risk, below): the vessels are
authorized heavy-duty vehicle diagnostic tooling; seed-key exchange, J1939 /
J1708 / UDS SecurityAccess, and cryptographic primitives over owned assemblies
are ordinary engineering, not leaks and not secrets.

Optional second question for triage quality, still advisory:

- `leak_kind` — Choice { operator_note, customer_detail, orchestration_id,
  host_or_path, none } — describes the suspected class so the flag is readable.

## Gate — flag and board note only, never a block

Effective mode is `min(global, decision)`.

- **Off** (default): no model call.
- **Shadow / below threshold**: `typed_decision.shadow` event with the hunk's
  `state_sha256`, the answer, and confidence. No flag surfaces. This is the data
  the four-week review reads.
- **Gate, at or above threshold**: `typed_decision.gated` event, and a
  **flag** on the scan outcome (`leak_hunk_flag` with file path and
  `leak_kind`). The flag is surfaced to the operator through the record and,
  optionally, a board note. The deterministic `Passed` verdict is unchanged; a
  clean deterministic scan still lands. The flag asks a human to look; it does
  not hold the landing.

The one hard rule: `leak_hunk` never sets `DockBoundaryScanResult.Passed` to
false and never transitions a merge entry or mission to failure. A future owner
decision could promote a specific high-confidence class to a hard block, but
that is a separate row, not this design.

## Events

- `typed_decision.shadow` — every recorded below-threshold or Shadow-mode call.
- `typed_decision.gated` — every at-or-above-threshold flag, with `gate_outcome`.
- `typed_decision.unavailable` — timeout, non-2xx, or parse failure; the caller
  proceeds on the deterministic result alone.

Each event carries `MissionId` / `VesselId` / `VoyageId` where the caller has
them, through the recorder's owner-scope path, and the payload holds `decision`,
the answer, confidence, `state_sha256`, `state_bytes`, `input_tokens`,
`output_tokens`, `latency_ms`, and `gate_outcome`. The event never holds the
hunk text.

## Tests it would need

Design-time only; the tests land with the implementation row.

- `LeakHunkAdapterTests` (table-driven, fake `ITypedDecisionClient`): Off →
  deterministic result unchanged, no model call; Shadow → result unchanged plus
  a shadow event; unavailable → result unchanged plus an unavailable event;
  Gate below threshold → result unchanged plus shadow event; Gate at threshold
  → flag attached, `Passed` still reflects only the deterministic scan; every
  branch honours the settings timeout via the linked token.
- A guard that the model pass **cannot** flip `Passed`: feed a deterministic
  clean scan and a model "leak" answer, assert the scan still passes and the
  landing is not blocked; feed a deterministic finding and a model "clean"
  answer, assert the deterministic finding still fails the scan.
- A redaction guard on the hunk state: a fixture hunk containing one instance of
  each redactor class (Armada id, absolute path, host, hex, key-shaped token)
  produces state in which none survives, and a negative case where a product
  identifier (a PGN name, a decoder class name) does survive.
- A volume-bound test: a diff with many hunks issues no more than the per-scan
  cap of model calls.

## Risk: a safety-tuned model misreads authorized diagnostics

The domain is authorized heavy-duty fleet diagnostics. Seed-key exchange, UDS
SecurityAccess, K-line and J1708 timing, and cryptographic constants over owned
ECU assemblies are legitimate, owner-authorized engineering. A safety-tuned
classifier can misread such text — a seed-key routine, an XTEA constant, a
challenge-response table — as a secret or an exfiltration attempt and raise a
false leak flag. The deterministic scanner already carries a `CORE_RULE_5_seed_literal`
pattern for genuine secret literals; the model must not duplicate or second-guess
that as a policy judgement.

Mitigations, all already implied by the non-negotiables:

- The model can only **flag**, never block, so a false positive costs a human
  glance, not a failed landing.
- The question instructions state the domain in plain terms and name seed-key /
  SecurityAccess as ordinary engineering.
- The four-week review measures the false-positive rate on the diagnostic
  vessels first; a decision reversed by operators on more than 5% of its gated
  outcomes drops back to Shadow until its criteria or threshold are fixed.
- The hard guardrail elsewhere in the project (UDS `0x34` reflash is banned)
  is unchanged and is not this decision's concern.

## Not in scope

- No change to `DockBoundaryScanner`, `ConventionChecker`, or the deterministic
  pattern lists.
- No promotion of any model flag to a hard block.
- No captain-facing tool; this is an admiral-side pass over the same diff the
  scanner reads.
