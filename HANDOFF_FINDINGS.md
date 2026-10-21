# Prompt and handoff reliability findings

## Current full-pipeline acceptance retest

Voyage `vyg_mvhed41p_WAfNMv3i4AY`, started 2026-10-21 at about 00:51 UTC after operator-confirmed image deployment. API health reports server startup `2026-10-21T00:50:26.332359Z`, healthy version 1.1.0; Harbor Connected. All eight deployed persona briefs match current source exactly. Initial state: all five captains idle, no active voyages.

Archived and deleted previous failed voyage `vyg_mvh7i6hn_Z5VjIzrlpnA` and all 60 associated missions; confirmed zero acceptance-vessel missions before dispatch. Preserved rejected work head `c44094a7c2c879acec4eafd3156778d9ccfc2024` on local archive branch `archive/failed-full-pipeline-20261021`. Acceptance local/remote branch verified at baseline `8c3e8b5975cbe96e5700529fdfcfe0512d627ec2` (PolyPrompt 3.1.0), clean; production main remains `c9ff616fd1b5262fe7edfd9ac295b9f264ed9650`.

Same FullPipeline and strict assignments: Claude Product Manager/Architect/Usability/Test/Linter/Recorder, DeepSeek Worker, independent Codex Judge. Same objective with the known prefix/shifted credential-overlap regression made explicit at dispatch, as recorded in the prior checkpoint. No source implementation is copied from earlier attempts. API collector polls every 25 seconds, checking routing and duplicate launches and saving all API-visible transcripts/events; the HF-11 transcript completeness limit remains. Evidence: `/tmp/armada-full-pipeline-retest-20261021`.

Product Manager and Architect completed. The version-2 plan has eight slices, including a separate shared-redaction prerequisite, and uses scoped decision indexes. DeepSeek package Worker committed 72bf77c; Usability tightened its exception assertions as 745cd23. First Judge requested revision for six missing model-field assertions despite passing tests. DeepSeek added all six as eb17e7d and proved each via individual mutations. Repeated Usability is active; no Judge acceptance yet and no overall success or failure outcome. Routing remains correct, with no duplicate launches or runtime redispatches observed. Stale Recorder-written ProjectContext was discovered after launch and cleared before Architect dispatch (HF-23); the Product Manager had received that stale context and is instructed to verify against current source. Do not call this a perfectly uncontaminated context-reset control. No mission has been restarted.

## Open work and acceptance gates

| ID | Item | Status / next evidence |
| --- | --- | --- |
| HF-04 | Reduce missed failure-path coverage and contradictory design decisions | Measure first-pass acceptance/reworks; require observable evidence, not passing counts alone |
| HF-05 | Semantic plan validation | Structural validation cannot establish objective completeness, resolve contradictions, or prove tests ran; further validation remains open |
| HF-06 | Worker context/tool exposure | Initial prompt still ~35,400 tokens. One whole-file read added ~60,000 prompt tokens; bounded default reads/pagination are a concrete next fix |
| HF-07 | In-flight phase observability | Provider request/tool/validation phases, wall-clock deadlines and attempts remain insufficiently exposed |
| HF-08 | Wilson configuration/SDK contract boundaries | Recheck cloud health-auth silent rewriting and nested C# health enablement parity on the fresh implementation; do not copy prior product changes |
| HF-09 | Independent final workflow verification | Repeat local HTTP/redaction/persistence/validation and C#/Python/JavaScript SDK checks after the full pipeline passes; hosted provider inference remains separately unverified |
| HF-10 | Mobile native coverage limits | Android phone/tablet flows passed before strict-selection UI; iOS native-device testing remains unavailable on this host |
| HF-11 | Complete CLI interaction diagnostics through API | New: mission and captain APIs expose abbreviated tool commands and omit tool-result payloads; retain an access-controlled, appropriately redacted diagnostic transcript alongside readable logs |
| HF-12 | Dependency connectivity-probe documentation | Package routes now independently verified; external README omission remains. Current verification details are in the completed appendix |
| HF-13 | Slice breadth/context efficiency | Optional scoped decision references are deployed; verify new plan uses them and measure rendered context. Semantic scope review remains open |
| HF-14 | Validation command status masking | Wrapper reproduction confirms exit 0 despite prior failure; guidance deployed; arbitrary shell exit-status enforcement remains open. No hidden product failure established |
| HF-15 | Focused Wilson test selection | Low-priority Test Engineer papercut: Test.Automated has no case filter; quantify cost before adding unrelated tooling to this acceptance task |
| HF-18 | Universal validation rules need boundary combinations | Three metadata reviews; five findings corrected but shifted-overlap redaction still failed; budget exhausted |
| HF-20 | Staggered credential-span overlaps | Independently reproduced by monitor and final Judge in actual settings validation; unresolved, blocked this run |
| HF-22 | Harbor native startup with unavailable display | Restart reproduced Avalonia render-timer error -6661; waking display restored connection; unattended-start fallback remains open |
| HF-23 | Revision-aware vessel context | Recorder context survived Git reset with obsolete package/test facts; cleared before new Architect dispatch; automatic invalidation remains open |
| HF-24 | PolyPrompt Ollama Cloud incomplete-stream success | Test Engineer reproduced missing-terminal/malformed NDJSON returning Success=true in 3.4.1; consumer mitigation and upstream correction remain unverified |

## Findings from this run

### HF-11 — API-visible CLI transcript is incomplete

Observed during Product Manager `msn_mvh7i6ia_rdjA0ADcCWh` on Claude Code. Both mission and captain log APIs return readable `> Running ...` lines whose longer commands end in ellipses; tool-result payloads are absent. Fetching all lines does not restore those details. This limits the requested review of every interaction: the monitor archives every API-visible log update, but cannot reconstruct omitted tool arguments/results. It is an observability gap, not evidence that the commands failed or caused a product defect. Recommended remediation: preserve a diagnostic structured transcript with existing access controls/redaction, independent of readable summaries, and expose paginated reads. No fix or new image is claimed yet.

### HF-12 — Dependency probe documentation gap

Product Manager emitted a MissingDoc papercut: installed PolyPrompt 3.4.1 README connectivity table does not specify xAI/OllamaCloud probe routes. This is a dependency-documentation gap, not a reproduced package failure. The next stage is explicitly responsible for verifying actual client behavior and recording supported routes/authentication; an unsupported hosted capability must remain reported as unverified. No external package fix is claimed.

### Current retest — scoped handoff improvement and remaining read amplification

The new Architect plan selects relevant decisions (14 total); first slice references only [1,9,14]. Worker description fell from 18,132 to 10,162 characters. Redaction is a separate prerequisite before metadata consumers, with explicit prefix/shifted overlaps and union-of-original-spans acceptance. Inference/discovery/tools/embeddings are still grouped into one broad slice; semantic scope review remains open.

Initial DeepSeek prompt was 35,385 tokens. After a single unrestricted `read_file` of `src/Test.Shared/WilsonSuites.cs`, the next inference increased by 60,036 tokens to 100,885. Later it reached 158,399 tokens. This identifies a concrete separate amplification path: the built-in reader defaults to the whole file, up to 1 MB. Introduce bounded default pages/output and explicit continuation, preserving complete instruction-file reading and small-file compatibility. No read-tool fix is implemented or deployed yet. Evidence: `package-context-growth.json` and API-visible package mission log. Smaller handoffs alone do not resolve it.

### HF-13 — Declared contracts remain broad and over-copied

Architect output is 53,885 characters, with 19 global decisions totaling 10,302 characters. Slice 3 still combines chat, streaming, tool protocols, embeddings validation and discovery despite the default prompt requesting separation. The structural gate accepts declared coverage; it cannot evaluate this semantic boundary. `DescribeMission` also copies every global decision into every slice, including unrelated decisions. First Worker description: 18,132 characters; instruction file: 28,560. Initial inference prompt: 35,398 tokens before reading the file; by iteration 27 it reached 96,087 tokens. These are observed sizes, not a measurement that attributes all growth to the decisions.

Remediation: support explicit per-slice decision references, keep full-plan artifacts retrievable, and render only relevant resolved decisions in each handoff. Add a plan scope/contradiction review before fan-out rather than treating schema validity as design approval. Retain acceptance assertions and failure cases; remove unrelated copied context first. A local fix is now implemented and under validation: optional per-slice `decisionIndexes` render only selected decisions, with range/duplicate checks; omitted indexes retain older plan behavior. New default prompts request explicit scoped indexes. This is not deployed to the current run. Semantic slice-boundary review remains open.

### HF-14 — A trailing echo can defeat pipefail

Worker `msn_mvh7i8d0_gflbLdMm6eM` ran `dotnet run ... | tail -60; echo EXIT:${PIPESTATUS[0]}`. Armada already enables Bash pipefail, but the final echo returns zero. A local negative reproduction, `/bin/bash -o pipefail -c 'false | tail -n 1; echo EXIT:${PIPESTATUS[0]}'`, prints EXIT:1 and exits 0. This is a command/evidence risk; no actual failed Wilson suite has yet been established as concealed. Prefer executable-plus-argument-vector validation through `run_process`, with tool-side output truncation and its original exit code. If a shell is necessary, save and explicitly return the validation status. Require independent verification rather than accepting the tool's success flag as the test verdict. A local prompt update now tells Worker and verification personas to prefer executable/argument-vector tools and explicitly return shell validation status; this is guidance, not enforcement over arbitrary CLI shell scripts, and is not deployed yet.

Recoverable Worker exploration errors so far: one mistyped absolute path (corrected next turn) and two ad-hoc reflection probes that lacked assembly-loading dependencies. They did not fail the mission or indicate a PolyPrompt transport/tool failure. Distinguish these from Judge rework and runtime redispatch.


### HF-18 — Passing cases omitted combinations implied by universal rules

Metadata Judge `msn_mvh7xiak_1iLTFnRfXYS` independently rebuilt and ran 62/62 shared cases plus both adapters, then reproduced two registry-level violations of existing D-3: `ApiType="Custom", ToolCallingApiFormat="XAIResponses"` returned no error because unknown-provider compatibility returned early; `https://user:pass@ollama.com` passed the fixed cloud endpoint check. The Judge did not claim HTTP reproduction. These are implementation/test omissions under an existing rule, not missing model tool capability. Strengthen applicable test matrices for universal prohibitions (including unknown values) and URI components, rather than adding only examples for known providers. Automatic rework 1 ran all five verification roles without operator restart, and the next slice stayed blocked behind Recorder. The replacement Judge independently verified that both findings were fixed, then requested rework 2 for three additional reproduced defects:

- Non-admin runner status leaked another configured runner's credential through an invalid format value. PUT validated against all submitted keys, but ConfigurationError only masked its own runner's key.
- `Redact("abcdef", ["abcd", "abcdef"])` returned `[REDACTED]ef`: sequential substring replacement left a credential suffix. Aggregate redaction must operate on complete secret spans; earlier per-runner masking must not defeat it.
- Stored `ApiType=" Ollama"` acquired canonical Ollama defaults; baseline did not recognize that padded value. This violates the plan's legacy compatibility rule, regardless of whether canonical behavior might otherwise seem preferable.

The second review independently passed 65 shared cases and both adapters before these probes. Passing counts still did not establish the universal credential/compatibility rules. The same classes were already discussed in historical findings; default guidance requesting credential collisions and adjacent cases was insufficient. Require an applicable field × credential-source matrix (same/other/overlapping key) and representative baseline normalization inputs, with observable assertions. No fixed prose or one run proves exhaustive coverage. Record sequential Judge discovery as a verification limitation rather than blaming all rework solely on DeepSeek.

### HF-20 — Prefix-overlap fix is not complete overlap masking

Independent execution of the exact `CredentialRedactor` source retrieved through the rework-2 mission diff reproduced `Redact("abcdefgh", ["abcde", "defgh"]) == "[REDACTED]fgh"` in either key order. The helper skips an entire matched prefix and therefore misses a second credential beginning inside that span. The prior prefix example now passes. Evidence: `independent-redactor-probe.log` and API diff snapshot in the private run directory. No Wilson product file was manually edited.

Operator intervention: appended AC-SEC-OVERLAP to all six pending redaction-stage contracts and all six final-integration contracts by authenticated API, then read back the result. It clarifies existing D-16/R-SEC-1: compute the union of matching spans on the original text, preserve surrounding non-secret text and short-key/header behavior, and prove the regression at helper and caller-visible boundaries. This is an external acceptance finding; do not describe eventual success as wholly unassisted. Keep it open until agents implement the fix and independent checks pass.


### HF-22 — Harbor GUI restart can fail before connecting

After this voyage was terminal and API status confirmed all five captains idle/no active voyages, two local Harbor processes were stopped and consolidated into one current Debug/net10 binary. The first relaunch exited 134 with `Avalonia.Native was not able to start the RenderTimer. Native error code is: -6661`, before Harbor connected. Waking the display (`caffeinate -u -t 5`) then relaunching succeeded; API confirmed Connected at 23:01:27 UTC. This matches the upstream [Avalonia no-display startup issue](https://github.com/AvaloniaUI/Avalonia/issues/18895), but no broader device/display matrix was tested. No active mission was interrupted, and this did not cause the completed acceptance failure. A supported unattended/headless fallback or actionable startup recovery remains open; no product fix is claimed.

### HF-23 — Resetting source does not invalidate Recorder facts

The next dispatch inherited ProjectContext saying PolyPrompt was now 3.4.1 with 53 cases and a new contract-test file, although the acceptance repository was reset to 3.1.0 baseline. EnableModelContext=false did not prevent this separate ProjectContext injection. Archived then cleared only the Recorder-generated ProjectContext through the API while Product Manager was running, before Architect dispatch. Product Manager had already received it, so source inspection must take precedence; no restart or claim of uncontaminated context is appropriate. The objective already prohibits reusing old implementation/memory. Durable context should carry source revision/provenance and be invalidated or clearly labeled historical after repository reset. The Product Manager explicitly noticed that project context did not match the actual baseline during source inspection, which mitigates this instance. This is an observed handoff-risk gap, not evidence that it caused a failed mission.

### Current retest — first review revision and dependency stream finding

First Judge `msn_mvhed60p_r0BXr4fnALf` independently restored/built commit fc1d4c2 (zero warnings/errors), passed 43 shared cases and both adapters, then found six missing `model` assertions in streaming/tool payload captures. This was an explicit UPG-2 guarantee omitted by Worker and intervening verification, not a transport failure. Rework Worker eb17e7d added all six and mutated each expectation independently: each mutant failed 42/43 and restored code passed 43/43. Full verification chain repeats. API audit at 01:18 UTC: nine launches, no duplicate launches, no routing mismatch, no runtime redispatches.

### HF-24 — Incomplete Ollama Cloud streams are reported successful

Test Engineer fc1d4c2 captured PolyPrompt 3.4.1 OllamaCloudCompletionClient.ChatStreamingAsync with an injected handler returning either valid NDJSON ending after done=false (no done=true), or NDJSON cut mid-JSON record. After enumerating Chunks, Success remained true, with partial/empty text and no error. The executable `ollamacloud-stream-incomplete` checks pin this observed behavior; they do not certify it as correct. xAI's valid stream without its terminal response event correctly reports failure. No hosted provider was contacted. Later inference work must prevent incomplete output from being reported as completed; upstream correction and Wilson mitigation remain unverified. Evidence: package Test Engineer output and captured source in the private retest directory.

A rework Worker papercut also claimed a branch-header/anchor mismatch. Independent checks found the persisted instructions, repository anchors, mission BranchName and original launch-log header all naming the same inherited branch. The claimed mismatch is **not reproduced** and is not queued as a confirmed Armada bug. Agent reports require evidence checks.

Record further issues with mission/stage/captain, violated requirement, reproduction/evidence, remediation, and verification status. Keep unresolved findings here; move verified fixes and finished work into the appendix.

## Appendix — completed work and historical evidence

### Previous full-pipeline run — failed at metadata review

Started 2026-10-20 at 21:39:53 UTC on `view.homedns.org`: voyage `vyg_mvh7i6hn_Z5VjIzrlpnA`. The deployed server started at 21:38:13 UTC; all eight persona briefs matched the deployed `00f45ea4` defaults exactly at dispatch. Harbor was connected. Hosted CI for revision `7d820c4b` passed, including the new pipeline regression suites.

The previous Wilson acceptance voyage and its 33 completed missions were archived and deleted through the API. Confirmed zero remaining acceptance missions before dispatch. Acceptance branch reset from `49ac2ccc4e0c7f3af3364d7bd4c5848161616c2e` to pre-update baseline `8c3e8b5975cbe96e5700529fdfcfe0512d627ec2` (PolyPrompt 3.1.0), guarded by a remote force-with-lease. Prior source retained as `archive/pre-full-pipeline-retest-20261020`. Production Wilson main remains `c9ff616fd1b5262fe7edfd9ac295b9f264ed9650`.

FullPipeline: Product Manager → Architect → Worker → Usability Engineer → Test Engineer → Linter → Judge → Recorder. Claude Code handles planning and verification contributors, DeepSeek handles Workers, and Codex is the independent Judge. All eight assignments persist `StrictCaptain=true`; no implicit model substitution or escalation is permitted. The objective and acceptance baseline were unchanged from the preceding test; the later HF-20 addition is explicitly documented below.

Authenticated API polling every 25 seconds archives full API-visible logs, mission records, instruction snapshots, status transitions, and requested/actual captains. It captures terminal records even when a stage finishes between polls. Server log rendering omits protocol noise; this is not raw provider-token capture. Full log snapshots are retained when rendered content changes, avoiding an assumption that streamed lines are append-only. Evidence: `/tmp/armada-full-pipeline-20261020` (private local artifacts, not portable repository evidence).

Outcome: **the run failed**, ending at about 22:56 UTC after 76 minutes. First package slice passed on its first review. Metadata failed its third Judge review after two automatic reworks: broader overlapping credential spans still exposed a suffix in the helper and actual settings validation. The prior five findings were resolved. Strict DeepSeek selection correctly prevented escalation to a different model; downstream work was cancelled. API audit: 60 mission records (22 WorkProduced, 1 Failed, 37 Cancelled), 23 launches, zero duplicate launches, zero runtime redispatches and zero captain mismatches. Six implementation slices never started. No end-to-end or hosted Wilson provider success is claimed.

Terminal Judge: `msn_mvh9poa0_XHLiQnHeMz1`. It independently restored/built the branch (zero warnings/errors), ran 68 shared cases and both adapters, then reproduced `Redact("abcdefgh", ["abcdef", "cdefgh"]) == "[REDACTED]gh"` against the built assembly and the actual settings validator. This agrees with the separately observed HF-20 defect. The operator-added redaction/integration criteria were still pending when the pipeline stopped. No manual mission restart, model substitution or Wilson source edit was used.



Local remediation prepared during this run: per-slice `decisionIndexes`, range/duplicate validation, compatibility fallback for older plans, latest-brief migration preserving operator edits, and validation-status guidance. The final 155 targeted service tests pass on .NET 8 and 10, including migration and rendered-prompt checks. Every new slice also retains a full-plan mission/API reference. This code has not been published as images or applied to this voyage. HF-13 semantic scope review and HF-14 shell-status enforcement remain open.

Current-run first slice milestones: Worker `87aad329` upgraded the package and added five capture cases; Usability independently validated a route mutation; Test Engineer `cc99532` added six cases and five fixture mutations; Linter `5c3f9f2` fixed explicit-type conventions/dead code without weakening assertions; independent Codex Judge passed 53/53 plus both adapters. Confirmed package xAI connectivity GET `/v1/models`, Cloud connectivity GET `/api/tags`, model-list errors returning empty lists with failure information in CallDetails, and raw Bearer retention in CallDetails unless recording is disabled. These facts were explicitly carried from Test Engineer through Linter and retrieved by Judge. Actual provider connectivity is still unverified. Recorder completed and stored the verified dependency facts; prior memories were marked as historical rather than treated as current evidence.

Current-run milestones: HF-01 deployment fan-out verified: all eight slices have six downstream stages, and every acceptance ID plus verification text is retained in all 48 descriptions (`fanout-contract-check.json`). Rejection behavior remains locally tested, not deliberately triggered on production. Cleanup/reset verified; strict assignments persisted; Product Manager `msn_mvh7i6ia_rdjA0ADcCWh` produced stable R-/AC-/U- requirements with no source changes or unperformed-test claims. Planning completed; the package slice is accepted, while later product implementation remains under review.

Completed items: prior acceptance cleanup/reset and autonomous 33-mission run; prompt migration and bounded-slice guidance; mobile parity fixes and Android native flows; versioned plan validation, full-chain rework, contributor exclusion, strict dispatch selection, local regression checks, CI, image publication and verified deployment. Those mechanisms are implemented; their effect on this new run is still under the open acceptance gates above. Historical recommendations below are evidence, not a claim that every recommendation has been implemented.


### HF-02 — Full verification chain preserved during live rework

First metadata rework: DeepSeek Worker `msn_mvh99zuo_cyX0HaRfjyj` → Claude Usability `msn_mvh99zvl_lcazzeHrQri` → Claude Test Engineer `msn_mvh99zwh_sU7SffWihuO` → Claude Linter `msn_mvh99zx7_AGaMHPBf48b` → independent Codex Judge `msn_mvh99zxw_OIZs95OlVNF`. Each completed and used its requested voyage captain. Recorder was reparented to the replacement Judge; dependent inference work remained blocked. The review correctly requested another revision instead of accepting green counts. Second rework also traversed all five stages on the correct captains. Its final Judge rejected the remaining defect, then the bounded policy cancelled descendants. Overall product acceptance failed.

### Timing evidence for the failed full-pipeline run

Launch-to-process-exit event totals by role: Product Manager 2.3 minutes; Architect 9.5; Workers 13.9; Usability 8.2; Test Engineer 25.3; Linter 7.5; Judges 5.2; Recorder 1.3. These are observed role wall times, not isolated model-compute attribution. The run spent more time in independent verification than Worker implementation. Local Services tests ran concurrently for part of the run, so host contention is a possible timing confounder. No transport failure was observed. Two review loops, repeated full-suite/mutation checks, and broad copied context explain work performed; phase telemetry is still insufficient to allocate every wait.

### HF-17 — A stale stage could rewind the shared branch

Code inspection found unconditional `git update-ref` during stage hardening. An isolated real-Git test failed against the existing implementation: an older completed stage moved a branch back behind newer committed work. This is a reproduced product bug, **not an observed cause in the current serialized voyage**. The local fix keeps already-contained commits, permits only compare-and-swap fast-forwards, and preserves divergent stage commits under `refs/armada/stage-commits/<SHA>`. An explicitly refused handoff becomes a failed mission with the commit, branch and remediation visible in mission details; downstream stages are cancelled, including during recovery redrive. Transport exceptions retain the existing best-effort behavior and remain a limitation. The guarded Git implementation must reach Harbor as well as the server; server-side ancestry checking additionally protects common stale handoffs with older Harbors. Regression tests cover normal advancement, stale heads, divergence, downstream cancellation and recovery. The fix is deployed to the restarted server and updated Harbor for the current retest; no unsafe production ref update was deliberately injected.


### HF-21 — Review failure detail omitted the actual budget

The deployed terminal reason said strict selection prevented escalation and suggested captain/persona configuration fixes, but did not state that this slice had used both permitted reworks. Local remediation distinguishes an exhausted budget (`2 of 2 rounds`) from unchanged rejected commits, explicitly states that no substitution occurred, and directs the user to correct the Judge's identified code or configuration defects using the preserved branch and an independent Judge. The bounded limit and strict routing policy are unchanged. A regression reproduces the exhausted strict chain, asserts the numeric reason and remediation, verifies descendant cancellation, and proves no extra Worker is dispatched. Separate stagnation coverage prevents mislabeling unchanged work as budget exhaustion. The fix is deployed for the current retest; a live bounded-stop message will be checked only if one naturally occurs.


### HF-03 — Strict routing verified throughout this run

All eight roles and both five-stage rework chains used the selected captains: DeepSeek Workers, Claude planning/contributing verification, Codex independent Judge. All 23 launches match the voyage overrides; no fallback, duplicate launch or runtime redispatch. At the per-slice limit, strict mode stopped rather than silently substituting another Worker model. This establishes routing behavior for this failed run, not full product correctness.

### HF-19 — Broader prompt consistency checks

The initial broader local Services run finished 2040/2042 green; after corrections all 2042 existing cases passed. The final .NET 8 regression set passes 212/212, including the new budget case; the final broader .NET 10 Services run passes 2043/2043. The final solution build has zero warnings/errors. One failure expected the embedded Architect brief to require legacy mission markers; the new default requires a version-2 plan. The test was updated to assert the typed contract while retaining fallback/launch output-signal restrictions. The brief now explicitly forbids result/verdict lines as the launch instructions already did. The other failure showed that shortened memory guidance omitted the discoverable `search_memory` name; restore that name with availability/mission restrictions intact. Both suites were added to the standard CI pipeline checks. The Architect brief also names field × same/other-key and prefix/shifted-overlap negative cases, plus verification of shared safety helpers before consumers, remaining under 2400 characters. This is guidance, not proof of exhaustive verification, and is not applied to the running voyage.

### HF-16 — Metadata defaults were not covered by the Worker fixtures

Usability `msn_mvh7xi7g_3hWohuQigDK` reviewed Worker commit `29c3ce4` and produced fix `254b04b0a6fc28edc24c07c8749068ff6b80f72b`:

1. An OllamaCloud request omitting endpoint inherited `http://localhost:11434` from the settings class and was incorrectly rejected instead of resolving to the cloud default (D-5).
2. Existing Ollama endpoints with slash/case/whitespace/empty variations were rewritten, also changing health URLs and violating compatibility.
3. A non-xAI runner using XAIResponses received duplicate errors for the same field.

The new `provider-default-endpoint-edges` case failed before the fix; after changes Usability reports 59/59 shared cases, both adapters passing and a warning-free build. Test Engineer expanded the suite to 62 cases and corrected a fixture that mislabeled an administrator token as non-admin; real non-admin access is now exercised. Codex independently passed those cases and requested two separate boundary fixes (HF-18). These are **pre-Judge corrections**, not automatic Judge rework loops. Track them separately so zero reworks does not imply a defect-free Worker delivery.

Cause: fixtures primarily exercised explicit, fully populated values rather than minimal deserialized requests and legacy value variations. The design already specified D-5 and compatibility; this was an implementation/verification omission, not lack of a written requirement. Local prompt guidance now explicitly requests omitted/null/empty/whitespace/inherited-default vectors, and verified component symbol owners (the Usability test initially called a static helper on the wrong class). This remains guidance; it does not prove fixture completeness and is not deployed to the current voyage.


### Test setup — 2026-10-20

This is an observed acceptance test, not a claim of deterministic agent execution.

- Armada revision: `2bfd2297b7a9f80349b9139ff2bebde704ca9493`; live server restarted at `2026-10-20T18:23:56Z`, version 1.1.0.
- Live Architect, Worker, and Judge templates were updated by API and read back exactly before the rollout.
- Previous acceptance run: 21 missions and two voyages archived locally, then deleted at the user's request.
- Wilson acceptance branch reset from `45ce2db544a6c40664790908eb438065b82f3a9f` to pre-update baseline `8c3e8b5975cbe96e5700529fdfcfe0512d627ec2` (PolyPrompt 3.1.0). Previous state retained on a local archive branch. Production Wilson main and image tags were not reset.
- Fresh voyage: `vyg_mvh0jgzv_8StF48Imx3k`.
- Pipeline: WilsonArchitectReviewed; Claude Code Architect → DeepSeek API-endpoint Worker → Codex Judge. Captain assignments are preferences with configured fallback; actual captain use must be checked from mission records.
- Monitoring uses authenticated REST because Armada MCP tools are unavailable in this session. No dashboard/browser polling.
- Evidence directory: `/tmp/armada-bounded-slices-20261020` (machine-local, not a portable artifact).

### Baseline and hypothesis

The preceding run eventually passed, but needed six automatic reworks, a corrective voyage, and one manual Judge restart. Findings included credential exposure, health snapshot association, stale probes, and editor-state preservation. Zero runtime redispatches did not mean zero review retries or operator intervention.

The new briefs assign design and acceptance-definition responsibility to the Architect. Each slice must define one behavioral outcome, components, approach, exclusions, invariants, stable acceptance IDs, negative cases, validation commands, dependencies, and uncertainties. Workers execute that bounded contract; Judges evaluate the same contract and report all evidenced findings together. A final integration slice must wait for implementation pipelines to settle.

These are prompt requirements, not enforced structured-schema fields or a guarantee that an Architect follows them. The plan parser remains backward compatible. Judge checks are independent verification, not proof of exhaustive coverage.

### Observations

- The test request itself previously said “prefer a small coherent plan.” That competed with the new instruction to split independent behaviors. The new request removes this wording and explicitly invokes the bounded slice contract.
- New built-in briefs remain short (Architect 1,775 characters; Worker 1,573; Judge 1,634). Startup migrates exact prior defaults and preserves operator-customized prompts. Targeted tests passed 59/59 on .NET 10 and 59/59 on .NET 8.
- Architect mission `msn_mvh0jh0e_Lc5gzvenZVf` ran on Claude Code and produced eight slices: package-only upgrade; metadata/validation; inference/discovery/embeddings; tool protocols; health lifecycle; dashboard editing; API/SDK/docs; final integration. The final slice has a voyage-wide settlement barrier. This improves behavioral separation over the prior three-slice plan, but the inference slice still combines chat, streaming, discovery, embeddings validation, and error redaction. Eight slices is not itself an enforceable scope bound. Require an explicit execution boundary and split independent request paths rather than allowing broad umbrella outcomes. The output is 43,122 characters; slice descriptions range from 2,102 to 7,521 characters, so smaller implementation scope has not yet made planning/handoff text smaller. Planning took about eight minutes versus about 90 seconds in the previous run; the completed run did not demonstrate a speed improvement.
- The first Worker received a 15,312-character instruction file for a 2,102-character slice. Its first reported inference already contained 35,385 prompt tokens, growing beyond 49,000 by handoff. This is an observed context-efficiency concern, not a measured attribution: the model's prompt count also includes system/tool definitions and conversation history. Inspect and measure those separately before shortening only persona prose. The instruction file includes the overall objective and the full plan title inventory alongside the assigned slice.
- The first Worker stayed within the package-only outcome and reported baseline/post-upgrade builds plus all 42 shared cases. The first Judge independently restored and built both revisions. The first Judge accepted this slice on its first review; no rework or runtime redispatch was needed. Its handoff description is 5,164 characters versus roughly 28,000 for the previous broad backend review.
- Worker commands visibly used `dotnet build ... | tail` and `dotnet test ... | tail`. Subsequent source inspection confirmed Armada API `run_process` already selects Bash with `-o pipefail` on this Unix host, so absence of a visible inline setting is not evidence of masked failures here. Explicit nested shells/argument-vector execution remain responsible for their own pipeline semantics. Preserve original exit status and prefer tool-layer truncation; no hidden build failure was established.
- The live instruction snapshot contains the new exclusions, stable-ID acceptance, and final-integration guidance. Judge handoffs retain bounded prior evidence and do not reintroduce the legacy inline cumulative diff.

#### First metadata review: prompt guidance still leaves specification gaps

Codex rejected metadata commit `9ff3da8` despite all 47 shared tests passing. Two concrete findings were returned together:

1. A malformed endpoint equal to the API key appeared verbatim in an HTTP validation error. The Architect specified INV-SECRET but SECRET-1 did not enumerate credential collisions in every string field. Recommendation: the Architect should define a negative-case table/property test covering key-as-ID, name, type, URL, format, and upstream error body, rather than leave each worker/reviewer to discover those permutations serially.
2. Normalization corrected an existing OpenAI `/v1/v1/models` URL to `/v1/models`. The Architect's INV-COMPAT explicitly required identical normalized values, so the Judge correctly enforced the written contract, even though it preserves a malformed URL. Recommendation: distinguish preservation of working behavior from byte-for-byte preservation of incidental or broken defaults; name intended bug fixes and exclusions explicitly. This was a specification issue as well as a worker scope issue.

Independent real HTTP testing reproduced a contract mismatch: submitting `healthCheckUseAuth=false` for OllamaCloud persists `true`. The Architect explicitly required forced cloud authentication while the overall request required override preservation. If authentication is mandatory, explain it in the editor and reject incompatible values explicitly; otherwise preserve the override. Silently rewriting an accepted setting is confusing.

Automatic metadata rework was scheduled without operator intervention. The metadata rework was accepted on its next review (commit `1de738b`). No manual mission restart has been used in this run.

#### Observability gap

The inference Worker `msn_mvh0tprt_8f8tmvzjK3c` started at 18:44:17 UTC, logged one successful inference and a successful instruction-file read, then had a multi-minute interval with no new result. The API still reported InProgress and zero redispatches, without a failure reason. This does not establish whether provider queueing, transport, SDK retries, or execution scheduling caused the delay. At 18:48:55 UTC the Worker had resumed and reached inference iteration 13, with no recorded failed response or redispatch. The provider-reported compute durations did not explain the multi-minute wall-clock gap. Per-result provider timing does not explain an in-flight wait. Recommend exposing request/tool/validation phase, phase start time, wall-clock duration, timeout deadline, and attempt count in mission details and telemetry, rather than inferring a stall from silence.

#### Inference slice: unresolved dependency contracts reached the Worker

DeepSeek completed the inference slice as `e1b5287`, reporting 55/55 shared cases plus both adapter tests. Its recorded loop reached 98 successful responses and approximately 193,000 prompt tokens at peak. Codex subsequently reproduced a discovery failure: HTTP 401 from both xAI capability catalogs becomes an empty sequence, and Wilson reports Connected/Online=true without invoking the required `/v1/models` fallback. The unchanged branch still passed all 55 cases; the success-path fixture did not cover this failure. Two automatic reworks were needed: first to handle failed catalogs/fallback, then to enforce endpoint validation before sending requests. The final inference review passed. This is a materially larger worker effort than the package slice; the title still groups several independent behaviors.

The Worker emitted two concrete papercuts: the plan's proposed `MaxCallDetails=0` did not suppress raw provider-body logging, so a logging seam was needed to satisfy secrecy; and model-list clients return empty sequences on HTTP failures rather than naturally surfacing a provider error in status. These dependency semantics should have been verified in the Architect's contract research or a dedicated investigation slice. They are not evidence that the model cannot call tools. The Worker verified actual HTTP routes with local capturing handlers; real hosted-provider connectivity remains unverified.

Timing comparisons are observational: mobile native builds/tests ran concurrently on the Harbor host, and provider/transport wall time is not decomposed by current telemetry. Do not attribute total elapsed time solely to prompts or model compute.

### Why the eight reworks happened, and how to reduce them

The dominant pattern was incomplete behavioral verification, not inability to execute tools. Workers often delivered passing tests that omitted the failure path the Judge subsequently reproduced. Two of eight slices passed first review; six needed rework, and inference and dashboard each needed a second revision. These are review-driven revisions, not eight infrastructure retries. Neither the records nor this single mixed-captain run establish an intrinsic DeepSeek capability limit.

Three causes overlap; they should not be assigned mutually exclusive percentages:

- **Implementation omissions:** credential redaction missed response/validation surfaces; incomplete SSE was treated as complete; asynchronous probe cancellation/identity missed configuration changes; editor normalization lost explicit choices and case-sensitive custom paths.
- **Specification and dependency gaps:** the Architect required exact preservation of a malformed legacy URL, conflicted on mandatory cloud auth versus explicit overrides, and proposed dependency behavior that had not been established. PolyPrompt model-list failures becoming empty results and logging configuration not suppressing raw bodies reached implementation as discoveries rather than resolved design facts.
- **Verification gaps:** tests proved selected happy paths instead of the stated invariant. The clearest example was final integration: 83 backend cases passed, but PUT exposed the key, request capture was attached after invalid-input checks, and every inventory check disabled live status. A zero count from an unattached handler proves nothing about outbound requests.

The final integration review was especially avoidable: its contract already explicitly limited the credential exception to administrator GET, required capture of invalid-input traffic, and required live-status listing. This was a Worker implementation/evidence failure, not missing instructions. Likewise, “incomplete streamed function call is not executed” was already stated; the missing EOF case needed an explicit fixture and self-check. More prose alone would not fix either failure.

#### Concrete prevention gates

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

### Mobile/tablet review

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

### Outcome

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

### Implementation round after the completed acceptance run

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

That deployment checkpoint is complete: rollout prompts were verified, the 33-mission voyage was archived/deleted, the repository was reset, and the full-pipeline run was launched. Hosted CI for `7d820c4b` passed. Current open work and progress are maintained above, rather than in this historical appendix.


### Completed publication checkpoint — 2026-10-20 23:07 UTC

Product revision `8c2fd584215947c0852e4d9e3fd15db229847f26`: warning-free solution build, 2043/2043 .NET 10 Services tests, 212/212 targeted .NET 8 tests, and successful [GitHub CI](https://github.com/jchristn/Armada/actions/runs/39512438792). `./build-all.sh v1.1.0` exited 0, pushing and pulling all three images. Registry inspection confirms Linux AMD64/ARM64 and equal `v1.1.0`/`latest` manifests:

| Image (`jchristn77/`) | Manifest digest |
| --- | --- |
| armada-server | `sha256:4679ead5404ac1f3ddd3013357e1846ab21420edc4b6200d15765cc244d87228` |
| armada-dashboard | `sha256:dd694d9e8d9c4c5f30f52c4f0bbc393e140ec81b0b91353c2b321341c97b597d` |
| armada-proxy | `sha256:13fbe46539155b24b5d1b6a2f6c266fc635007af43118d03cfbd3ad3415695e4` |

This publication completes image preparation; it does not establish that the server has been recreated or that Wilson passed end to end. The server update was requested. No acceptance voyage is active.
