# Typed-decision observation review — interim, 21 September 2026

This is an interim measurement, not the four-week acceptance report. The first gate deployment was on 16 September. Its earliest full review date is 14 October; later decisions require their own deployment and first-use dates. Keep current settings and thresholds. No independently verified outcome set supports a threshold change.

## Window and method

Window: 2026-09-16 00:00:00 UTC through 2026-09-21 00:17:00 UTC, exclusive at the end. The report uses recorded typed-decision events. It includes operator calls and repeated states. Counts must not be called production accuracy or unique training examples.

Reproduce with [the read-only SQL](analysis/typed-decision-observation.sql). Replace `__SINCE__` and `__UNTIL__` with the stated ISO UTC times, then run against PostgreSQL with `ON_ERROR_STOP=1`. The query casts legacy text timestamps explicitly. No retained state text is exported.

There are 4,334 call events across 18 decision IDs, including 2,082 captain-tool events with no mission attribution. There are 28 unavailable calls (0.65%): 27 enforced content-egress exclusions and one HTTP 529. An exclusion is expected policy enforcement, not a model answer or an accuracy failure.

## Per-decision results

Accuracy is **not measured** for every row: there are no independently verified labels joined to these observations. There are zero real operator reversals in this window; the smoke reversal is excluded. This does not establish a zero error rate. The recommendation for each row is **keep current settings** pending verified outcomes.

| Decision | Calls | Unique states | Applied | Unavailable | Latency p50 / p95 ms | Mean recorded input / output tokens |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| capacity_escalation | 120 | 35 | 7 | 0 | 184.5 / 262.1 | 1063.4 / 42.1 |
| captain_tool | 2082 | 1329 | 0 | 0 | 133.0 / 347.8 | 4185.1 / 839.7 |
| change_substance | 5 | 3 | 5 | 0 | 125.0 / 386.4 | 3406.0 / 78.0 |
| custom:source_fidelity | 3 | 3 | 2 | 0 | 226.0 / 231.4 | 611.0 / 42.0 |
| failure_cause | 6 | 5 | 0 | 2 | 111.5 / 221.0 | 1406.5 / 77.8 |
| handoff_outcome | 37 | 26 | 0 | 12 | 132.0 / 224.4 | 1710.5 / 157.7 |
| inbox_triage | 721 | 239 | 129 | 0 | 232.0 / 414.0 | 411.2 / 75.2 |
| lint_finding | 8 | 7 | 6 | 2 | 144.5 / 191.7 | 2788.5 / 628.5 |
| memory_candidate | 143 | 24 | 0 | 0 | 128.0 / 440.0 | 336.8 / 69.7 |
| papercut_merge | 1036 | 168 | 31 | 1 | 115.0 / 232.0 | 452.5 / 21.0 |
| preflight | 31 | 10 | 10 | 0 | 191.0 / 297.0 | 2805.0 / 184.0 |
| prior_art | 41 | 19 | 9 | 0 | 175.0 / 270.0 | 5044.5 / 679.3 |
| refusal | 49 | 44 | 0 | 6 | 215.0 / 294.6 | 1441.5 / 73.0 |
| review_substance | 6 | 5 | 2 | 2 | 109.0 / 153.8 | 1864.7 / 60.7 |
| revision_kind | 4 | 4 | 0 | 0 | 212.5 / 306.1 | 3624.2 / 700.0 |
| runtime_failure | 3 | 2 | 2 | 0 | 218.0 / 221.6 | 559.7 / 90.0 |
| stage_necessity | 31 | 10 | 0 | 0 | 143.0 / 216.0 | 3639.9 / 744.4 |
| test_covers | 8 | 6 | 0 | 3 | 157.5 / 218.3 | 4709.4 / 473.1 |

Latency includes the unavailable paths. Token figures are recorded per-call fields, not a billing total; batched decisions can share provider measurements. An `applied` event records gate action, not proof that the action was correct.

No call events in this window were found for change_quality, corpus_prelabel, criteria_lint, dispatch_staleness, flake_score, followup_routing, leak_hunk, log_watch, memory_record, memory_review, owner_digest, or premise_check. Synthetic eval calls are separate from these production event types. No call does not mean a clean outcome. The four Off custom decisions also have no calls.

## Outcomes and baseline comparison

- Six rescue voyages were created in this window; zero have status Complete at this snapshot, versus the recorded older baseline of 78/245 (31.8%). This is not a causal comparison: the window is short, scheduling is paused, and no matched population or closed observation period exists. Rescue identity uses the autonomous mission marker or the legacy numbered/title prefix; the old baseline selection query is not included in the objective record.
- There are 25 papercut events in this window, versus the recorded historical baseline of 599. These have different time windows and do not show a reduction rate.
- Twenty-five typed-decision calls join an incident with nonempty RootCause text. This is not 25 truth labels: autonomous recovery assigns `RootCause = mission.FailureReason`, so these can repeat the original rule diagnosis. Audit provenance and verified resolution before using them.

## D1 action review

The six failure-cause call events contain five unique states, zero applied typed holds, and two content-egress exclusions. No D1 operator reversal is recorded. There is no evidence to move the 0.90 threshold or change the cause set. Rule hard blocks and the existing action limits remain authoritative. This report does not authorize new automatic actions.

## Work required for the final review

1. Wait until each decision has its required observation window. A disabled log screen has no running observation clock.
2. Join exact retained samples to independently verified outcomes. Exclude probes, separate redactor/question versions, and prevent repeated states from appearing in both development and held-out sets.
3. Audit every correction, including its incident, rerun, or operator evidence. Report unknown cases and abstentions explicitly.
4. For a gate proposal, satisfy the recorded requirement of at least 95% accuracy over at least 50 known-truth rows at the proposed threshold. Confidence alone is not evidence of accuracy.
5. Re-run the SQL for the full window and report comparable rescue and papercut populations.

The two observation objectives remain open. The interim query and report are ready; elapsed time and verified outcomes are still required.
