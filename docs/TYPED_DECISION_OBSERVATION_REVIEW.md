# Typed-decision observation review — four weeks, 16 September to 14 October 2026

This is the four-week review of the typed decisions in production. It reports calls, actions, unavailable calls, latency, tokens and operator reversals for each decision, and gives one recommendation per decision. No setting or threshold is changed by this report; each recommendation needs an owner decision.

Accuracy is **not measured** for any decision. The gate rule for a threshold change is at least 95% accuracy over at least 50 rows with known truth. No decision has 50 independently verified rows, so no threshold change is proposed. Confidence-band accuracy (<0.5, 0.5-0.9, >=0.9) is not measured for the same reason.

## Window and method

Window: 2026-09-16 00:00:00 UTC through 2026-10-14 00:00:00 UTC, exclusive at the end. The report uses recorded typed-decision events. It includes operator calls and repeated states. Counts must not be called production accuracy or unique training examples.

Reproduce with [the read-only SQL](analysis/typed-decision-observation.sql). Replace `__SINCE__` and `__UNTIL__` with the stated ISO UTC times, then run against PostgreSQL with `ON_ERROR_STOP=1` in a read-only transaction. The query casts legacy text timestamps explicitly. No retained state text is exported. Reversals come from `typed_decision.reversed` events in the same window.

There are 75,453 call events across 28 decision IDs; 30,857 carry a mission. There are 1,475 unavailable calls (2.0%). Of these, 1,417 are content or vessel egress exclusions, which are expected policy enforcement, not model answers. The other 58 are provider faults: 33 HTTP 402 (account credit exhausted), plus HTTP 403/500/503/529, timeouts and two exceptions.

## Per-decision results

| Decision | Calls | Unique states | Applied | Unavailable (egress) | Latency p50 / p95 ms | Mean input / output tokens | Reversals |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| capacity_escalation | 3024 | 2168 | 220 | 66 (35) | 90 / 184 | 938 / 48 | 0 |
| captain_tool | 7697 | 4799 | 0 | 0 (0) | 101 / 248 | 2681 / 368 | 0 |
| change_quality | 1098 | 1040 | 1 | 0 (0) | 107 / 168 | 4151 / 244 | 0 |
| change_substance | 590 | 259 | 389 | 49 (49) | 100 / 151 | 4167 / 117 | 0 |
| custom:safety_step_present | 158 | 128 | 0 | 31 (31) | 124 / 204 | 7340 / 72 | 0 |
| custom:source_fidelity | 161 | 131 | 23 | 31 (31) | 117 / 197 | 7171 / 61 | 0 |
| dispatch_staleness | 106 | 53 | 2 | 0 (0) | 87 / 160 | 559 / 41 | 0 |
| failure_cause | 516 | 241 | 13 | 8 (8) | 90 / 141 | 2552 / 115 | 0 |
| flake_score | 15 | 15 | 0 | 0 (0) | 139 / 185 | 2887 / 59 | 0 |
| handoff_outcome | 3697 | 3655 | 1497 | 45 (43) | 96 / 157 | 2437 / 178 | 0 |
| inbox_triage | 13769 | 4326 | 414 | 12 (8) | 128 / 272 | 329 / 68 | 0 |
| leak_hunk | 2796 | 2401 | 0 | 25 (25) | 169 / 233 | 1486 / 148 | 0 |
| lint_finding | 719 | 713 | 152 | 7 (7) | 101 / 153 | 3362 / 761 | 0 |
| memory_candidate | 689 | 117 | 0 | 2 (0) | 108 / 194 | 362 / 70 | 0 |
| memory_record | 117 | 117 | 0 | 0 (0) | 91 / 136 | 723 / 99 | 0 |
| memory_relevance | 5190 | 4725 | 994 | 176 (174) | 128 / 192 | 9481 / 494 | 0 |
| memory_review | 95 | 95 | 0 | 0 (0) | 119 / 165 | 2856 / 219 | 0 |
| owner_digest | 13 | 2 | 0 | 12 (12) | 0 / 70 | 40 / 3 | 0 |
| papercut_merge | 4962 | 1399 | 162 | 76 (73) | 88 / 166 | 445 / 21 | 0 |
| preflight | 7585 | 1461 | 1005 | 46 (44) | 99 / 171 | 2426 / 183 | 14 |
| premise_check | 698 | 697 | 0 | 0 (0) | 92 / 148 | 794 / 111 | 0 |
| prior_art | 7852 | 2262 | 59 | 757 (754) | 106 / 164 | 4537 / 612 | 0 |
| refusal | 4879 | 4833 | 0 | 50 (47) | 101 / 172 | 1863 / 101 | 0 |
| review_substance | 281 | 274 | 249 | 10 (8) | 98 / 149 | 3244 / 88 | 0 |
| revision_kind | 611 | 606 | 0 | 6 (6) | 95 / 148 | 2479 / 503 | 0 |
| runtime_failure | 258 | 118 | 133 | 2 (0) | 92 / 172 | 587 / 89 | 0 |
| stage_necessity | 7574 | 1335 | 76 | 46 (44) | 95 / 146 | 2749 / 442 | 0 |
| test_covers | 303 | 251 | 91 | 18 (18) | 117 / 216 | 7589 / 754 | 0 |

Latency includes the unavailable paths. Token figures are recorded per-call fields, not a billing total; batched decisions (inbox_triage, leak_hunk, memory_candidate, memory_review) share provider measurements. An `applied` event records a gate action, not proof that the action was correct. Per-call cost is not recorded anywhere, so spend cannot be stated in money.

No call events in the window were found for corpus_prelabel, criteria_lint, followup_routing, log_watch, citation_resolves, consumer_break_direction or hang_class. No call does not mean a clean outcome; the captain-log screen that feeds log_watch ships disabled.

## Reversals

All 14 recorded reversals are on `preflight`. The smoke-test reversal from the deploy day is a `captain_tool` retention test and is excluded. Each reversal has a line in the operator decision corpus; two lines were backfilled during this review because the event existed without one.

- **13 reverse an applied hold.** The operator overturned a preflight flag and dispatched. The corpus outcome is `correct` for 9 (the dispatched work passed review or met its premise) and `unknown` for 4. So at least 9 of the 1,005 applied preflight holds were wrong.
- **1 reverses a below-threshold shadow verdict.** The model flagged an unobservable premise below the gate threshold; the operator later confirmed the flag was right and cancelled the voyage. This is one known false negative of the threshold.

Causes, by preflight question:

| Question | Reversals | Cause |
| --- | ---: | --- |
| q4 (premise not observable from a dock) | 7 | The model cannot see sibling checkouts, provisioned tools or fixture-only measurement named in the brief. Three of the seven rest on provisioned sibling repositories. |
| q13 (open repository fact or owner choice) | 5 | The model reads report-only research facts as missing prerequisites. Three overlap with a q4 reversal. |
| q9 (redundant or conflated guard) | 3 | One state flagged three times; the model conflated two independent defect guards. |
| q1 (cited path does not resolve) | 1 | Rule-side: the citation scanner resolved a declared sibling path against the target. The scanner now resolves a declared sibling citation in its sibling repository. |

## Recommendations

Each row is one recommendation. **Keep** means keep the current mode and threshold. A **switch off** or **throttle** row is a proposal for the owner, given because the decision spends tokens and rarely changes an outcome; it is not applied.

| Decision | Recommendation | Reason |
| --- | --- | --- |
| preflight | Keep threshold; dedupe calls by state hash | 7,585 calls over 1,461 states. 9 of 1,005 holds are verified wrong and 1 shadow verdict was a missed hold, so neither direction supports a threshold move. Give q4 the sibling and provisioning facts it lacks. |
| handoff_outcome | Keep | 1,497 applied; detection is predictive but no verified label set exists. |
| change_substance, review_substance, lint_finding, test_covers, runtime_failure, failure_cause, capacity_escalation, custom:source_fidelity | Keep | Gated actions with no reversal and no verified labels. |
| memory_relevance | Keep | 994 applied; highest mean input tokens (9,481). Review cost once per-call cost is recorded. |
| refusal, leak_hunk, custom:safety_step_present | Keep | Safety screens. Zero applied because nothing qualified, not because the screen failed. |
| captain_tool, premise_check, change_quality, dispatch_staleness | Keep | Tool or advisory paths with few or no gate actions. |
| inbox_triage | Throttle or switch off (owner) | 13,769 calls, 3.0% applied, labels only. |
| stage_necessity | Throttle or switch off (owner) | 7,574 calls over 1,335 states, 1.0% applied. |
| prior_art | Throttle or switch off (owner) | 7,852 calls, 0.8% applied, 754 egress exclusions. |
| revision_kind, memory_record, memory_review, memory_candidate, flake_score | Switch off (owner) unless a consumer is planned | Zero applied in four weeks. |
| owner_digest | Switch off or fix its egress scope (owner) | 12 of 13 calls excluded by egress policy. |
| Decisions with no calls | Keep configuration; no evidence either way | See the list above. |

## Baselines

- Rescue voyages: 61 of 244 created in the window are Complete (25.0%), against the recorded baseline of 78/245 (31.8%). Rescue identity uses the autonomous mission marker or the legacy numbered/title prefix. The windows differ, mission volume roughly doubled, and the population is not matched, so this is not a causal comparison.
- Papercuts: 1,713 papercut events in the window, against the recorded baseline of 599. The baseline covers a different period and a smaller fleet, so this does not show a rate change.
- 6,565 calls join an incident with nonempty RootCause text. These are not truth labels: autonomous recovery assigns `RootCause = mission.FailureReason`, so they can repeat the original rule diagnosis.

## What a threshold change still needs

1. At least 50 independently verified rows for the decision, with exact retained states and development/held-out separation.
2. At least 95% accuracy at the proposed threshold, reported per confidence band.
3. A gated action the rule could not take (hold, escalate, flag).

Reversals are the strongest label source. Preflight is the only decision with enough reversals to start a labelled set.
