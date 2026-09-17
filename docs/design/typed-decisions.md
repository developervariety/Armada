# Typed decisions

The admiral can consult a calibrated advisory classifier at specific decision
points, behind the deterministic rules it never replaces. This document is the
maintainer reference for that system: its principles and the catalogue of
decision points. The authoritative implementation is each adapter under
`src/Armada.Core/Services/TypedDecisions/`; this document summarises, it does not
define.

## Principles

- **Advisory, never authoritative.** A decision can make a call more
  conservative — hold a rescue, flag, escalate, sort, add a field — but never
  approves a review, lands, dispatches, deletes, or writes memory.
- **The deterministic rule stays.** Every decision has a deterministic fallback
  (an exit code, a regex, git ancestry, an isolated re-run). The classifier is a
  tie-breaker over structured state, not a replacement for the rule.
- **Fail closed.** A timeout, rate limit, parse error, or missing key returns the
  rule's verdict and records an `unavailable` event. A slow decision is
  unavailable, not late.
- **Modes.** Each decision runs in `Off`, `Shadow` (record only), or `Gate`
  (advise the gated action). A global mode caps every decision and is the kill
  switch. Both record the rule's verdict and the model's on every call.
- **Off without a key.** The effective global mode is `Off` (reason
  `typed_decisions_no_key`) until a provider key resolves. With a key, each
  decision runs at its shipped or configured mode.
- **Redaction.** Nothing leaves the server unredacted. Events store a hash and
  size of the state, never the state itself. The provider key comes from the
  admiral environment variable or the key file
  `<data directory>/secrets/typesafe-api-key` (folder `0700`, file `0600`), which
  an administrator can write through `PUT /api/v1/typed-decisions/key`. It is
  never stored in settings, logged, recorded, or returned.

## Decision catalogue

Each decision has a stable name (its settings key and event `area`) and a default
mode, which applies once a key is present. Names, not index numbers, are the stable identifiers.

### Enabled by default (`Gate`)

| Name | What it decides |
| --- | --- |
| `preflight` | Whether a dispatch brief passes the pre-dispatch checks the deterministic preflight cannot express in text. |
| `review_substance` | Whether a reviewer verdict is substantive rather than an empty or templated pass. |
| `failure_cause` | The class of a mission failure (infrastructure, test failure, runtime), which governs whether a rescue is warranted. |
| `runtime_failure` | Whether a process/runtime error is recoverable work worth preserving. |
| `refusal` | Whether a captain result is a model refusal rather than genuine output. |
| `papercut_merge` | Whether two papercut reports describe the same underlying issue and should merge. |
| `lint_finding` | How a Linter finding routes to the next stage: only `correctness`/`safety` at `must_fix` or above is marked blocking; a `style_preference` becomes an evidence note. |
| `capacity_escalation` | Smart Routing only: whether a mission is `lighter`, `default`, or `stronger` work for its persona, which chooses the persona model list tried first (threshold 0.90; every failure is `default`; cached per mission). It never makes a captain eligible. |

### Also shipped in `Gate`

| Name | What it decides |
| --- | --- |
| `prior_art` | Whether the requested work already exists, with evidence, before a stage begins. |
| `premise_check` | Whether a brief's stated premise still holds at the target commit. |
| `criteria_lint` | Whether acceptance criteria are testable and unambiguous. |
| `inbox_triage` | The attention level of an inbox item or coordination note (`informational`, `today`, `this_hour`). |
| `followup_routing` | Where a reviewer follow-up belongs (a new row, a blocking voyage, or a note). |
| `flake_score` | Whether a single test failure reads as a load flake or a real defect. |
| `change_substance` | Whether a code change is substantive rather than cosmetic. |
| `stage_necessity` | Whether a pipeline stage is necessary for the work, so a redundant stage can be skipped. |
| `handoff_outcome` | The outcome class of a stage handoff. |
| `revision_kind` | Whether a revision request names a behaviour change or only comment/wording. |
| `test_covers` | Whether a test actually covers the reported symptom. |
| `leak_hunk` | Whether a diff hunk may carry private context that must not reach a repository. |
| `log_watch` | Whether a running captain's log shows trouble worth an operator's attention. |
| `memory_candidate` | Whether an observation is worth a durable memory. |
| `memory_record` | The shape of a memory record for a captured decision. |
| `owner_digest` | A digest of owner decisions for later review. |
| `corpus_prelabel` | A provisional label for a captured decision in the evaluation corpus. |

### Retired

| Name | Replaced by |
| --- | --- |
| `routing_hint` | `capacity_escalation`. The work-shape hint and route `shapes` tags are removed; a stored `routing_hint` entry or `shapes` list is ignored. |

## Captain-facing use

Captains receive a read-only, per-mission-budgeted, redacted tool that returns an
answer they weigh; it never dispatches, lands, or edits a record on their behalf.
The corpus of labelled operator and owner decisions is the ground-truth set every
post-enablement review joins against.
