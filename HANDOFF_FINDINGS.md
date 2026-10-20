# Prompt and handoff reliability findings

## Test setup — 2026-10-20

This is an observed acceptance test, not a claim of deterministic agent execution.

- Armada revision: `2bfd2297b7a9f80349b9139ff2bebde704ca9493`; live server restarted at `2026-10-20T18:23:56Z`, version 1.1.0.
- Live Architect, Worker, and Judge templates were updated by API and read back exactly before the rollout.
- Previous acceptance run: 21 missions and two voyages archived locally, then deleted at the user's request.
- Wilson acceptance branch reset from `45ce2db544a6c40664790908eb438065b82f3a9f` to pre-update baseline `8c3e8b5975cbe96e5700529fdfcfe0512d627ec2` (PolyPrompt 3.1.0). Previous state retained on a local archive branch. Production Wilson main and image tags were not reset.
- Fresh voyage: `vyg_mvh0jgzv_8StF48Imx3k`.
- Pipeline: WilsonArchitectReviewed; Claude Code Architect → DeepSeek API-endpoint Worker → Codex Judge. Captain assignments are preferences with configured fallback; actual captain use must be checked from mission records.
- Monitoring uses authenticated REST because Armada MCP tools are unavailable in this session. No dashboard/browser polling.
- Evidence directory: `/tmp/armada-bounded-slices-20261020` (machine-local, not a portable artifact).

## Baseline and hypothesis

The preceding run eventually passed, but needed six automatic reworks, a corrective voyage, and one manual Judge restart. Findings included credential exposure, health snapshot association, stale probes, and editor-state preservation. Zero runtime redispatches did not mean zero review retries or operator intervention.

The new briefs assign design and acceptance-definition responsibility to the Architect. Each slice must define one behavioral outcome, components, approach, exclusions, invariants, stable acceptance IDs, negative cases, validation commands, dependencies, and uncertainties. Workers execute that bounded contract; Judges evaluate the same contract and report all evidenced findings together. A final integration slice must wait for implementation pipelines to settle.

These are prompt requirements, not enforced structured-schema fields or a guarantee that an Architect follows them. The plan parser remains backward compatible. Judge checks are independent verification, not proof of exhaustive coverage.

## Observations

- The test request itself previously said “prefer a small coherent plan.” That competed with the new instruction to split independent behaviors. The new request removes this wording and explicitly invokes the bounded slice contract.
- New built-in briefs remain short (Architect 1,775 characters; Worker 1,573; Judge 1,634). Startup migrates exact prior defaults and preserves operator-customized prompts. Targeted tests passed 59/59 on .NET 10 and 59/59 on .NET 8.
- Architect mission `msn_mvh0jh0e_Lc5gzvenZVf` ran on Claude Code and produced eight slices: package-only upgrade; metadata/validation; inference/discovery/embeddings; tool protocols; health lifecycle; dashboard editing; API/SDK/docs; final integration. The final slice has a voyage-wide settlement barrier. This improves behavioral separation over the prior three-slice plan, but the inference slice still combines chat, streaming, discovery, embeddings validation, and error redaction. Eight slices is not itself an enforceable scope bound. Require an explicit execution boundary and split independent request paths rather than allowing broad umbrella outcomes. The output is 43,122 characters; slice descriptions range from 2,102 to 7,521 characters, so smaller implementation scope has not yet made planning/handoff text smaller. Planning took about eight minutes versus about 90 seconds in the previous run; the completed run did not demonstrate a speed improvement.
- The first Worker received a 15,312-character instruction file for a 2,102-character slice. Its first reported inference already contained 35,385 prompt tokens, growing beyond 49,000 by handoff. This is an observed context-efficiency concern, not a measured attribution: the model's prompt count also includes system/tool definitions and conversation history. Inspect and measure those separately before shortening only persona prose. The instruction file includes the overall objective and the full plan title inventory alongside the assigned slice.
- The first Worker stayed within the package-only outcome and reported baseline/post-upgrade builds plus all 42 shared cases. The first Judge independently restored and built both revisions. The first Judge accepted this slice on its first review; no rework or runtime redispatch was needed. Its handoff description is 5,164 characters versus roughly 28,000 for the previous broad backend review.
- Worker commands visibly used `dotnet build ... | tail` and `dotnet test ... | tail`. Subsequent source inspection confirmed Armada API `run_process` already selects Bash with `-o pipefail` on this Unix host, so absence of a visible inline setting is not evidence of masked failures here. Explicit nested shells/argument-vector execution remain responsible for their own pipeline semantics. Preserve original exit status and prefer tool-layer truncation; no hidden build failure was established.
- The live instruction snapshot contains the new exclusions, stable-ID acceptance, and final-integration guidance. Judge handoffs retain bounded prior evidence and do not reintroduce the legacy inline cumulative diff.

### First metadata review: prompt guidance still leaves specification gaps

Codex rejected metadata commit `9ff3da8` despite all 47 shared tests passing. Two concrete findings were returned together:

1. A malformed endpoint equal to the API key appeared verbatim in an HTTP validation error. The Architect specified INV-SECRET but SECRET-1 did not enumerate credential collisions in every string field. Recommendation: the Architect should define a negative-case table/property test covering key-as-ID, name, type, URL, format, and upstream error body, rather than leave each worker/reviewer to discover those permutations serially.
2. Normalization corrected an existing OpenAI `/v1/v1/models` URL to `/v1/models`. The Architect's INV-COMPAT explicitly required identical normalized values, so the Judge correctly enforced the written contract, even though it preserves a malformed URL. Recommendation: distinguish preservation of working behavior from byte-for-byte preservation of incidental or broken defaults; name intended bug fixes and exclusions explicitly. This was a specification issue as well as a worker scope issue.

Independent real HTTP testing reproduced a contract mismatch: submitting `healthCheckUseAuth=false` for OllamaCloud persists `true`. The Architect explicitly required forced cloud authentication while the overall request required override preservation. If authentication is mandatory, explain it in the editor and reject incompatible values explicitly; otherwise preserve the override. Silently rewriting an accepted setting is confusing.

Automatic metadata rework was scheduled without operator intervention. The metadata rework was accepted on its next review (commit `1de738b`). No manual mission restart has been used in this run.

### Observability gap

The inference Worker `msn_mvh0tprt_8f8tmvzjK3c` started at 18:44:17 UTC, logged one successful inference and a successful instruction-file read, then had a multi-minute interval with no new result. The API still reported InProgress and zero redispatches, without a failure reason. This does not establish whether provider queueing, transport, SDK retries, or execution scheduling caused the delay. At 18:48:55 UTC the Worker had resumed and reached inference iteration 13, with no recorded failed response or redispatch. The provider-reported compute durations did not explain the multi-minute wall-clock gap. Per-result provider timing does not explain an in-flight wait. Recommend exposing request/tool/validation phase, phase start time, wall-clock duration, timeout deadline, and attempt count in mission details and telemetry, rather than inferring a stall from silence.

### Inference slice: unresolved dependency contracts reached the Worker

DeepSeek completed the inference slice as `e1b5287`, reporting 55/55 shared cases plus both adapter tests. Its recorded loop reached 98 successful responses and approximately 193,000 prompt tokens at peak. Codex subsequently reproduced a discovery failure: HTTP 401 from both xAI capability catalogs becomes an empty sequence, and Wilson reports Connected/Online=true without invoking the required `/v1/models` fallback. The unchanged branch still passed all 55 cases; the success-path fixture did not cover this failure. Two automatic reworks were needed: first to handle failed catalogs/fallback, then to enforce endpoint validation before sending requests. The final inference review passed. This is a materially larger worker effort than the package slice; the title still groups several independent behaviors.

The Worker emitted two concrete papercuts: the plan's proposed `MaxCallDetails=0` did not suppress raw provider-body logging, so a logging seam was needed to satisfy secrecy; and model-list clients return empty sequences on HTTP failures rather than naturally surfacing a provider error in status. These dependency semantics should have been verified in the Architect's contract research or a dedicated investigation slice. They are not evidence that the model cannot call tools. The Worker verified actual HTTP routes with local capturing handlers; real hosted-provider connectivity remains unverified.

Timing comparisons are observational: mobile native builds/tests ran concurrently on the Harbor host, and provider/transport wall time is not decomposed by current telemetry. Do not attribute total elapsed time solely to prompts or model compute.

## Why the eight reworks happened, and how to reduce them

The dominant pattern was incomplete behavioral verification, not inability to execute tools. Workers often delivered passing tests that omitted the failure path the Judge subsequently reproduced. Two of eight slices passed first review; six needed rework, and inference and dashboard each needed a second revision. These are review-driven revisions, not eight infrastructure retries. Neither the records nor this single mixed-captain run establish an intrinsic DeepSeek capability limit.

Three causes overlap; they should not be assigned mutually exclusive percentages:

- **Implementation omissions:** credential redaction missed response/validation surfaces; incomplete SSE was treated as complete; asynchronous probe cancellation/identity missed configuration changes; editor normalization lost explicit choices and case-sensitive custom paths.
- **Specification and dependency gaps:** the Architect required exact preservation of a malformed legacy URL, conflicted on mandatory cloud auth versus explicit overrides, and proposed dependency behavior that had not been established. PolyPrompt model-list failures becoming empty results and logging configuration not suppressing raw bodies reached implementation as discoveries rather than resolved design facts.
- **Verification gaps:** tests proved selected happy paths instead of the stated invariant. The clearest example was final integration: 83 backend cases passed, but PUT exposed the key, request capture was attached after invalid-input checks, and every inventory check disabled live status. A zero count from an unattached handler proves nothing about outbound requests.

The final integration review was especially avoidable: its contract already explicitly limited the credential exception to administrator GET, required capture of invalid-input traffic, and required live-status listing. This was a Worker implementation/evidence failure, not missing instructions. Likewise, “incomplete streamed function call is not executed” was already stated; the missing EOF case needed an explicit fixture and self-check. More prose alone would not fix either failure.

### Concrete prevention gates

| Gate | Required evidence before advancing | Reworks it targets |
| --- | --- | --- |
| Architect plan acceptance | Trace every overall acceptance criterion to an owned slice/test; resolve defaults versus explicit values, intended compatibility bugs, and cross-slice response/serialization rules | Contradictory metadata/auth requirements and omitted boundaries |
| Dependency investigation | Capture actual success, HTTP failure and empty-result behavior; verify logging and URL composition before prescribing their implementation | Discovery/fallback and redaction assumptions |
| Worker completion | For every acceptance ID, name the executable case, assertion, observed result and any skipped path; inherited test counts are not new evidence | Passing suites with missing guarantees |
| Test validity | Attach capture before the action; prove it observes a known request; make targeted regressions fail when the relevant guard is removed or the fixture is deliberately violated | False zero-request checks and vacuous success |
| Rework completion | Reproduce every prior finding, retain its regression, then inspect adjacent cases in that behavior before resubmitting | Serial second revisions in inference/dashboard |
| Integration | Exercise real local HTTP through configure → persist → reload → live inventory → inference/tools → health, with response/log leak scanning and SDK deserialization | Cross-surface gaps deferred to final Judge |

Use a reusable failure checklist rather than lengthening every persona: 401/403/404/5xx, empty success versus failed request, invalid settings before any HTTP, stream EOF without terminal completion, configuration removal/change during a queued or in-flight probe, and explicit false/empty/custom/case-sensitive values across save/reload. The Architect selects applicable rows and expected behavior; the Worker supplies executable evidence; the Judge independently challenges those claims. Avoid inventing expected behavior merely to fill the matrix.

Split the broad inference slice into discovery/status and inference execution, with tool streaming already separate. Preserve a final integration gate and serialize edits to shared files. More slices add overhead, so judge the change by first-pass acceptance and total elapsed time, not slice count. Do not split already successful package-only work further.

Prioritize contract checks and trustworthy failure-path tests ahead of additional prompt tuning. Captain pinning and phase telemetry are also needed for reproducible evaluation and diagnosis, but neither is proven to have caused these defects. Keep the Judge independent: reducing revisions by weakening its checks would hide the exact failures it caught here.

## Mobile/tablet review

The React Native app shares the dashboard API client, types, and pure logic. Review found real gaps despite that sharing:

- A new dashboard-router-only duplication hook was treated as shared native code. Both lint and the shared-module test caught the missing `react-router-dom` dependency. It is now explicitly dashboard-only; the exception test recognizes router-bound code.
- Endpoint duplicate/edit/save was missing. Native flows now call the server duplication endpoint, retaining stored credentials without reading or displaying them, and allow editing before persistence.
- Native dispatch and fleet actions lacked per-run approval settings. They now expose Approve All, a 600-second/Deny default, configurable timeout and answer, administrator restrictions, and integer timeout validation. Fleet-action settings are sent under `Overrides.ApprovalOptions`, preserving other overrides.
- Native CLI permission settings omitted the timeout default. They now save Deny/Approve. Permission and mission-review inbox cards show countdowns and the known timeout decision; mission details show the review deadline countdown without inventing an unknown default.
- Endpoint validation now opens a modal immediately with progress and a result/error. Creating or duplicating an inference endpoint offers creation of an API Endpoint captain. Captain creation failure reports that the endpoint was saved instead of silently losing it.
- Voyage list deletion used “Purge” and lacked bulk deletion. It now uses Delete and Delete Multiple, retaining failed selections after partial success.
- The README still described most screens as placeholders; corrected to describe implemented native workflows and validation limits.

Mobile changes are committed in `88964f7c14d3dff26735af132e318bb88e02b76b`; the follow-up footer/flow changes are committed in `9ffcce82`. Strict TypeScript, lint, route coverage (86 dashboard plus eight mobile-only routes), and parity (573 entries, none planned) passed. Both platform Jest presets passed 78 suites / 1,247 tests. Tests cover credentials-preserving duplication, captain creation failures, immediate validation feedback, approval restrictions/defaults, partial deletion, platform-specific keyboard behavior, and validation errors outside the scrolling fields. Existing worklet warnings and asynchronous handles remain; Jest used forced exit. Production Hermes exports passed for both platforms. The Android Release APK built successfully; third-party Gradle deprecation warnings remain.

Native Android tablet coverage passed all four flows: sign-in, list/detail, sheets/Ask, and the added smoke test covering sidebar/rail, endpoint validation, and dispatch approval controls. A real long-form defect was found and fixed: validation errors previously appeared below scrolling fields while Save remained fixed. Errors now appear alongside the fixed actions. Screenshots confirmed the error is visible. Existing tablet flows were retained. The completed Android phone suite passed all ten native flows in 11m35s, including Build, Configuration, and sign-out. The tablet suite passed all four flows in 3m9s.

Native tooling limitation: iOS `simctl` hangs on this host. Maestro 2.11 enumerates iOS devices even with Android platform/device flags, confirmed by JVM thread dumps. A local-only runner makes Apple tool discovery fail fast for Maestro subprocesses only; it does not change global Xcode settings. Explicit `--platform` alone does not resolve this environment problem. Android tests use isolated temporary Admirals and synthetic records; they do not dispatch production jobs. No iOS simulator/device or App Store/Play publication is claimed. Phone operations also exposed an impossible page-edge centering assertion; the target was visibly present, and the flow now scrolls without requiring centering.

## Prioritized follow-up work

These are recommendations from observed evidence, not implemented fixes or proven causes of every delay:

1. **Make captain selection explicit.** One dashboard rework requested DeepSeek but actually ran on GLM. Add a strict captain/model pin option and show requested captain, actual captain, and fallback reason together. Existing preference/fallback behavior prevents a controlled model comparison.
2. **Validate the Architect contract before dispatch.** Check coverage of the overall objective, contradictions between defaults and explicit overrides, intended compatibility behavior, dependencies, and a final integration gate. Required prompt headings are not a structured schema or semantic validation. Reject an incomplete plan before workers spend time on it.
3. **Give each invariant an explicit negative-case matrix.** Credential secrecy needs field-collision and transport/provider-error cases; editor preservation needs explicit false/empty/custom values plus row removal and identity changes. Preserve the same acceptance IDs through implementation, review, and rework.
4. **Reduce default worker context and tool exposure.** Measure system/tool schema tokens separately from instructions and history. The runtime announced 175 MCP tools for a narrow implementation slice. Prefer a role-appropriate tool set, artifact references for full evidence, and only necessary dependency context. Do not shorten away acceptance cases to meet a prose budget.
5. **Preserve command exit status.** Prevent successful tail commands from masking failed builds/tests; return bounded output and the original exit code from the execution tool. Tests should distinguish command completion from successful validation.
6. **Expose in-flight phases.** Show provider request wall time, timeout/attempt information, tool execution, and validation state so silent periods have an actionable explanation.
7. **Measure total workflow cost.** Compare architecture time, source-changing worker time, review time, repeated validations, rework count, and operator intervention. More slices reduce scope but add dispatch, restore, and review overhead. First-pass acceptance and end-to-end acceptance are separate metrics.

## Outcome

Voyage `vyg_mvh0jgzv_8StF48Imx3k` completed from 18:24:56 to 20:16:01 UTC on 2026-10-20: approximately 111 minutes. All 33 missions are Complete. The pipeline needed eight automatic reworks, with zero runtime redispatches and no manual mission restart or corrective voyage this round. Complete status on a superseded review does not mean its original verdict passed.

| Slice | Automatic reworks | Findings that drove rework |
| --- | ---: | --- |
| Package | 0 | Accepted on first review |
| Metadata/validation | 1 | Credential collision leak; compatibility contract preserved a malformed legacy URL |
| Inference/discovery | 2 | Catalog HTTP failures falsely reported connected; missing pre-request validation |
| Tool protocols | 1 | SSE tool-call EOF without terminal marker reported success |
| Health lifecycle | 1 | Removed queued probe still sent; enabled flag missing from result identity |
| Dashboard | 2 | Lost tool format/custom URL; leaked validation values; case-sensitive path preservation |
| API/SDK/docs | 0 | Accepted on first review |
| Final integration | 1 | PUT credential leak; capture attached after negative checks; no live-status discovery coverage |

Actual execution: one Claude Code Architect, 15 DeepSeek Workers, one GLM Worker, and 16 Codex Judges; no Mux. The GLM worker was `msn_mvh1yaup_Zgag8ewoNRH`, despite its requested captain being DeepSeek. Mission records establish the substitution, not its precise scheduling cause.

Final accepted source was `76bcc4eb78170ea20c2a4fb0017fb93b449039c2`; acceptance branch HEAD is `49ac2ccc4e0c7f3af3364d7bd4c5848161616c2e`. Final Judge `msn_mvh4ab3c_G3lnFcekR3D` reported 83 backend and 57 dashboard cases passing, adapter checks, SDK compilation/syntax checks, and Postman checks. Independent testing then built the clean final branch and exercised real local HTTP: live/non-live status redaction, redacted PUT response with stored credentials retained, invalid settings rejected without persistence, and explicit tool/health enablement false retained. Python, JavaScript, and C# SDKs successfully deserialized authenticated runner/health responses against that server. No real hosted xAI/OllamaCloud Wilson inference was tested. Armada's actual DeepSeek execution is separate evidence.

Remaining coverage gaps: dashboard reload-after-save was reviewed/built but lacks a component regression; static Ollama `/api/ps` was outside capture; the C# nested health DTO omits the server's `HealthCheckEnabled` field although its runner DTO exposes the flag. The independently reproduced cloud health-auth rewrite remains a contract/UI issue. Do not equate the final passing review with exhaustive SDK parity or live provider certification.

The previous run took roughly 71 minutes but required operator intervention. This run completed autonomously, with more automatic rework and greater total elapsed time. Better containment is promising; faster execution or deterministic model selection is not established. Concurrent mobile builds on the Harbor host also confound timing. Monitoring used API snapshots and retained logs; a gap between conversational turns was reconstructed from server timestamps rather than continuously observed.

## Implementation round after the completed acceptance run

The next rollout adds these prevention mechanisms; their effect on Wilson rework counts is not yet measured:

- Versioned Architect contracts (`contractVersion: 2`) require stable objective IDs, resolved decisions, named acceptance scenarios/expected outcomes/verification, failure cases or a non-applicability reason, validation commands, valid dependencies, and a final integration barrier. The same criteria and decisions are rendered into Worker and verification-stage descriptions. Invalid fenced contracts stop fan-out and expose remediation in mission details; they cannot silently use older blocks/prose. Legacy unversioned/customized templates remain supported. This validates declared structure/coverage, not semantic completeness or truth of test claims.
- Built-in Architect, Product Manager, Worker, Test Engineer and Judge briefs now emphasize resolved compatibility/default/override decisions, dependency failure semantics, capture before actions, observable acceptance evidence and adjacent rework cases. Exact previously shipped briefs migrate; operator edits remain intact. Briefs remain below 2,400 characters.
- FullPipeline Judge rework repeats the original intervening verification stages rather than replacing the chain with Worker → Judge. Routing/review settings and original acceptance criteria are retained, and Recorder waits for the replacement Judge.
- Judge selection excludes the reviewed Worker and intervening contributors, including when verification stages separate the Worker from its reviewer.
- Dashboard/mobile dispatch offer **Wait for selected captain**, persisted as `captainAssignments[].strictCaptain`. Strict assignments wait instead of silently substituting a captain. Stagnant/exhausted rework cannot automatically escalate to another model; it reports remediation. Default preferred-captain behavior remains compatible.

After deployment, repeat the same Wilson objective and validation checks on the pre-update acceptance baseline using built-in FullPipeline: Product Manager → Architect → Worker → Usability Engineer → Test Engineer → Linter → Judge → Recorder. Use Claude Code for planning/verification contributors, DeepSeek for Workers, and a separate Codex Judge, with strict captain assignments so actual model use is controlled. Delete only the Wilson acceptance test voyages/missions after archiving evidence; preserve production main/images and the previous accepted source on an archive branch. Compare first-pass acceptance, automatic revisions, wall time, actual captain use and unverified checks. Adding stages may increase total time; no speed improvement is assumed.

Still open: semantic plan contradiction detection, role-limited tool/context exposure, provider request phase telemetry and a new end-to-end result on the rollout. The Wilson cloud-auth rewrite and nested SDK health-field gap must be addressed by the fresh pipeline rather than copied from the accepted implementation. Retain the same overall behavior requirements; the new prompts should discover and handle these boundaries. Do not claim these recommendations are all resolved by structural checks.

Local validation for this implementation round: 153 targeted service cases passed on .NET 10 and the same 153 on .NET 8; the complete solution built with zero warnings/errors. Dashboard production build and 15 shared dispatch tests passed. Mobile type checking/lint passed, and seven dispatch interaction tests passed on each iOS/Android preset, including strict selection in the submitted request. The earlier full native mobile results are independent of this new switch; it has not been rerun on a device. A concurrent dashboard/solution build initially raced while generated assets were being replaced; the ordered rerun passed. A subsequent no-restore build required a full restore for Harbor's missing net8 assets; restored solution build passed. Neither was an observed product runtime failure.

The targeted pipeline contract/routing suites are also added to push/PR CI, so these regressions no longer depend solely on manually running the broader Services suites.

Rollout artifact: product changes committed as `00f45ea4`; `build-all.sh v1.1.0` completed successfully for server, dashboard and proxy, both architectures, pushed `v1.1.0`/`latest` and pulled them locally. Local tag identity was checked. Published manifest digests:

| Image | Digest |
| --- | --- |
| `jchristn77/armada-server` | `sha256:506c8eeb9d3a42254d07f9cc48dc74f5417099fd98fa7c444367841ec5a041cd` |
| `jchristn77/armada-dashboard` | `sha256:bfbcb1beeb952d0350a56ca79744a4501e05d394a7d10ddd38e333621b510c35` |
| `jchristn77/armada-proxy` | `sha256:17f872c50d443147b6c38b188a340f3bba7430845e09329fcb44266bae0d41b2` |

Awaiting operator deployment confirmation before cleanup/reset and the fresh full-pipeline run. The existing acceptance voyage has 33 Complete missions and no active jobs; its records and accepted source remain preserved until rollout verification. The prepared request retains the previous objective and uses strict assignments for every FullPipeline persona. Evidence and request: `/tmp/armada-full-pipeline-20261020` (local). CI adds the targeted suites; hosted CI results are not claimed as complete while the latest run is pending.
