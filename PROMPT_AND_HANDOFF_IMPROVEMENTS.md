# Prompt and Handoff Improvements

## Objective and authorized scope

Implement every recommendation scoring 18 or higher (R01-R15). Preserve existing persona output signals, headings, mission parsing, operator customizations, and scope boundaries. R16-R27 are planned follow-ups, not authorized implementation in this pass. Scores are subjective: value estimates usefulness; safety estimates confidence that an additive change will not disrupt workflows.

Replace `docs/PERSONA_PROMPT_ASSESSMENT.md` with this plan. Keep existing persona-editor work intact. Do not increment versions without another explicit version request. Update the specified deployment through authenticated template APIs, with no credentials or session tokens in the repository. Record exact changed template names and content hashes so another deployment can receive the same changes.

## Ranked recommendations

Use `[ ]` pending, `[-]` in progress, `[x]` verified complete. Developers may append dates, commits, test evidence, or blockers in the Evidence column. Deferred rows remain unchecked.

| ID | Recommended change | Value | Safety | Total | Status | Evidence |
| --- | --- | ---: | ---: | ---: | --- | --- |
| R01 | Architect: self-contained mission descriptions with relevant upstream decisions and evidence | 10 | 9 | **19** | [x] | Canonical additions; 220 focused tests per framework; live read-back verified |
| R02 | Architect: executable acceptance checks, expected outcomes, and exact commands | 10 | 9 | **19** | [x] | Canonical additions; 220 focused tests per framework; live read-back verified |
| R03 | Product Manager: distinguish evidence, inference, assumptions, and unresolved questions | 9 | 10 | **19** | [x] | Canonical additions; 220 focused tests per framework; live read-back verified |
| R04 | All personas: report unavailable tools and unperformed checks | 9 | 10 | **19** | [x] | Canonical additions; 220 focused tests per framework; live read-back verified |
| R05 | Worker: changed files, behavior, validation results, and residual risks in handoffs | 9 | 10 | **19** | [x] | Canonical additions; 220 focused tests per framework; live read-back verified |
| R06 | Test Engineer: exact commands, results, skips, and untested paths | 9 | 10 | **19** | [x] | Canonical additions; 220 focused tests per framework; live read-back verified |
| R07 | Product Manager: measurable acceptance criteria, boundaries, and non-goals | 9 | 9 | **18** | [x] | Canonical additions; 220 focused tests per framework; live read-back verified |
| R08 | Architect: verified paths/symbols, interfaces, ordered steps, and examples | 10 | 8 | **18** | [x] | Canonical additions; 220 focused tests per framework; live read-back verified |
| R09 | Judge: expected/actual behavior, reproduction, and smallest corrective action | 9 | 9 | **18** | [x] | Canonical additions; 220 focused tests per framework; live read-back verified |
| R10 | Test Engineer: acceptance-criterion-to-test mapping and uncovered criteria | 9 | 9 | **18** | [x] | Canonical additions; 220 focused tests per framework; live read-back verified |
| R11 | Worker: no invented requirements/APIs/evidence; bounded escalation conditions | 10 | 8 | **18** | [x] | Canonical additions; 220 focused tests per framework; live read-back verified |
| R12 | Product Manager: dated sources for research-dependent claims, or supplied research | 9 | 9 | **18** | [x] | Canonical additions; 220 focused tests per framework; live read-back verified |
| R13 | Linter: repository commands, file/line findings, command results | 8 | 10 | **18** | [x] | Canonical additions; 220 focused tests per framework; live read-back verified |
| R14 | Test Engineer: report production defects without weakening assertions or editing production | 9 | 9 | **18** | [x] | Canonical additions; 220 focused tests per framework; live read-back verified |
| R15 | Paired evaluations: same downstream model/tasks with baseline versus improved handoffs | 9 | 9 | **18** | [x] | Ten pinned-model calls; measured results and manual review recorded below |
| R16 | Shared handoff packet for objectives, evidence, decisions, steps, dependencies, checks, questions | 10 | 7 | **17** | [ ] | Deferred; outside this execution scope |
| R17 | Critical packet before narrative/logs, with complete source artifact references | 9 | 8 | **17** | [ ] | Deferred; outside this execution scope |
| R18 | Usability Engineer: reproducible interactions, precise changes, and acceptance checks | 9 | 8 | **17** | [ ] | Deferred; outside this execution scope |
| R19 | Judge: executed checks versus reasoning, escalation when evidence is inaccessible | 9 | 8 | **17** | [ ] | Deferred; outside this execution scope |
| R20 | Recorder: verified paths/procedures, provenance, confidence, correction rules | 8 | 9 | **17** | [ ] | Deferred; outside this execution scope |
| R21 | Recorder: unavailable stores, failed writes, unpersisted items | 8 | 9 | **17** | [ ] | Deferred; outside this execution scope |
| R22 | Linter: separate behavioral findings from mechanical fixes with repair reproductions | 8 | 9 | **17** | [ ] | Deferred; outside this execution scope |
| R23 | Required capabilities independent of captain preferences with task overrides | 10 | 6 | **16** | [ ] | Deferred; outside this execution scope |
| R24 | Capability preflight for availability/authorization with configurable escalation/wait | 10 | 6 | **16** | [ ] | Deferred; outside this execution scope |
| R25 | Usability Engineer: viewports/themes/keyboard/accessibility inspection when relevant | 9 | 7 | **16** | [ ] | Deferred; outside this execution scope |
| R26 | Compact handoffs and retrievable artifacts instead of context duplication/blind truncation | 9 | 6 | **15** | [ ] | Deferred; outside this execution scope |
| R27 | Handoff completeness validation before stage advancement with configurable enforcement | 9 | 6 | **15** | [ ] | Deferred; outside this execution scope |

## Execution checklist

- [x] 1. Inspect default prompts, seeding/upgrades, stage handoff, testing architecture, API contracts, and current deployment. Preserve existing work and secrets. Evidence: source review; authentication uses an external mode-600 session file.
- [x] 2. Add canonical, additive prompt guidance for R01-R14. Apply tool/evidence honesty to all eight personas; preserve role-specific signals. Add strict, idempotent default upgrades that recognize untouched legacy templates and retain operator-edited templates.
- [x] 3. Add regression tests for seeding, rendering, reset, legacy upgrades, customization preservation, idempotency, output contracts, and Architect fan-out descriptions.
- [x] 4. Build paired evaluation fixtures and an executable runner for R15. Hold model, tools, objective, and decoding settings constant across baseline/improved runs. Record acceptance results, unsupported claims, missing evidence, extra discovery, and residual risks. Separate deterministic harness checks from real model measurements.
- [x] 5. Run relevant backend suites on both frameworks, meaningful full regression checks, and evaluation harness checks. Repair failures; record exact outcomes.
- [x] 6. Update persona/operator/API documentation and link this plan. Remove the superseded assessment. Prepare a reproducible deployment script with dry-run, preflight, external backup, content verification, and rollback support.
- [x] 7. Inspect deployment templates and compare the concrete changes. Apply only the authorized template improvements, read back and verify, and check backend/dashboard availability. Record sanitized receipt and reproduction commands. Never print or commit credentials.
- [x] 8. Review the diff, mark R01-R15 and execution steps with evidence, and deliver implementation/deployment outcomes plus remaining limits.

## Design and compatibility

Canonical additions live in `src/Armada.Core/Prompts/persona-improvements.json`, embedded by Core and consumed by the replication script. The existing persona instructions remain the base; additions clarify deliverables without introducing a new required parser format. Architect must place all per-mission detail within its existing JSON `description`; do not introduce new JSON fields or require prose outside its plan block. No file counts, dependencies, or branching semantics change.

Untouched previous defaults can upgrade automatically. Operator edits remain intact during startup upgrades. The explicit deployment operation appends the canonical guidance to existing authorized templates while preserving their original contents and metadata. Repeated application must be a no-op; conflicting existing guidance must be reported for review. The deployment uses live template editing rather than requiring a binary replacement or service restart. In-flight agents keep instructions already generated; newly generated briefs use the updated templates.

R15 needs two layers of evidence: deterministic tests proving the evaluation runner/scoring works and measured runs from an actual model if an authorized reachable endpoint is available. A simulator or prompt-string assertion cannot establish that a smaller model is sufficient. Keep any unexecuted model measurements visible as a limitation rather than marking them measured.

## Assessment carried forward

The original embedded prompts were approximately Architect 464 words, Judge 431, Linter 426, Product Manager 200, Recorder 606, Test Engineer 394, Usability Engineer 182, and Worker 42, excluding shared mission instructions. Linter was conditionally suitable for mechanical work; the other roles left substantial evidence gathering, reasoning, or verification implicit. Worker depended heavily on upstream quality.

Ordinary handoff keeps the first 8,000 characters of prior agent output. The diff injected into the mission description is uncapped there; `{Diff}` is capped at 20,000 and `{PreviousStageOutput}` at 8,000. Architect fan-out bypasses ordinary handoff and creates Workers from parsed titles/descriptions, so necessary product context must be copied into every generated description. Full instructions also contain shared rules, repository/captain context, skills, and playbooks; the runtime launch prompt asks the model to read its instruction file.

Persona priorities select runtime/model/Mux endpoint/minimum tier, not verified tool capabilities. API captains have local file/search/process tools, excluding web tools, and may receive optional MCP tools. Prompt changes cannot grant absent tools or certify model suitability by parameter count. The deferred capability and context-budget work remains necessary for stronger guarantees.

## Validation and deployment log

Append commands, counts, fixture/model provenance, failures/fixes, content hashes, rollout status, and limitations here. Store secret-bearing backups and sessions outside the repository with restricted permissions. Repository artifacts must contain only templates, source, tests, and sanitized deployment/evaluation metadata.

### Current implementation evidence

R01-R14 are implemented as canonical additions in `src/Armada.Core/Prompts/persona-improvements.json`. The original role instructions and parser/output protocols remain. `PersonaPromptImprovements` loads the embedded resource; `PromptTemplateService` applies it to new defaults and upgrades recognized untouched previous defaults. Tests verify rendering/reset, idempotent startup, preserved operator edits, non-built-in exclusions, and acceptance/validation instructions surviving Architect fan-out descriptions.

- `dotnet build src/Armada.sln --no-restore`: both frameworks, zero warnings/errors (`/tmp/prompt-improvements-build-final.log`).
- Focused .NET 10 and .NET 8 prompt/pipeline/MCP/SDK suites: 220 passed each, zero failed/skipped. Logs: `/tmp/prompt-improvements-net10.0.log`, `/tmp/prompt-improvements-focused-net8-final.log`.
- Prompt/template suites against SQLite, PostgreSQL, MySQL, and SQL Server: 42 passed per provider, zero failed/skipped (`/tmp/prompt-improvements-db-parity.log`).
- `python3 -m unittest discover -s scripts/common/tests`: 15 passed. Covers preview, existing content and metadata preservation, idempotence, rollback/concurrent edits, restricted external backups, model drift detection, scoring limits, and fixed downstream inputs/model controls (`/tmp/prompt-python-tests-final.log`).

### Live rollout and replication

The backend target is `http://view.homedns.org:8710`; dashboard availability was verified at `http://view.homedns.org:9710/dashboard/`. All eight persona templates were updated through authenticated REST and verified by content hash and unchanged metadata. A second dry run reported all eight unchanged. See [sanitized receipt](docs/deployment/PROMPT_IMPROVEMENTS_RECEIPT.json). No account credentials, session tokens, or original template backups are in the repository. A restricted external backup is retained at `/tmp/armada-persona-prompt-backup-20261013.json` for this session's rollback; move it to a secure durable location if needed.

For another deployment, run the following commands and provide credentials interactively. Do not put passwords in shell command arguments, committed files, or receipts. The script also supports an externally supplied mode-600 session file or runtime environment credentials.

```sh
python3 scripts/common/apply-prompt-improvements.py --base <backend-url>
python3 scripts/common/apply-prompt-improvements.py --base <backend-url> --apply --backup <new-secure-path-outside-repo.json>
python3 scripts/common/apply-prompt-improvements.py --base <backend-url>
```

For rollback, use the original backend and its external backup:

```sh
python3 scripts/common/apply-prompt-improvements.py --base <backend-url> --rollback <secure-backup.json>
```

The script checks content and metadata before applying/restoring and refuses detected concurrent edits. REST has no atomic compare-and-swap contract, so these are read-before-write protections, not a transaction across all templates. Apply failures attempt verified rollback of templates already touched. Resolve reported conflicts before retrying rather than overwriting operator changes.

The live rollout changes stored template configuration only; no server binary or dashboard image was replaced and no service was restarted. Already-generated briefs retain their contents. The running binary's embedded reset defaults remain unchanged until a later source deployment, so do not reset the templates to defaults on the older binary without reapplying this guidance.

### Paired evaluation results (R15)

The deployment's selected CLI captain returned `RuntimeNotInstalled`; no mission was dispatched. Used the available local CLI instead, with explicit `gpt-6.1-sol`, ignored user config, read-only temporary workspaces, shell/web disabled, empty histories, and identical task inputs. Ten actual model calls completed: four baseline/improved persona pairs and an additional fixed-Worker pair consuming only the corresponding parsed Architect description. Every call reported zero tool calls. Runtime decoding defaults were unchanged but temperature/seed were not explicitly pinned. The model identifier is an explicit request, not a parameter-count certification.

Measured artifact: [sanitized evaluation report](scripts/common/prompt-evaluations/results/gpt-6.1-sol-20261013.json). Raw responses are retained outside the repository at `/tmp/prompt-live-pinned-evaluation.json`; the committed report includes hashes, metrics, scoring and manual review rather than raw model transcripts.

| Case | Baseline lexical evidence coverage | Improved lexical evidence coverage | Manual finding |
| --- | ---: | ---: | --- |
| architect_offline | 71% | 86% | Both plans correct; improved description adds explicit evidence, acceptance identifiers, expected exit code and stop conditions. |
| product_missing_research | 40% | 100% | Neither invents sources; improved brief separates evidence, assumptions and named decision criteria. |
| worker_missing_api | 20% | 80% | Both block without inventing APIs; improved reply gives missing inputs, unmet criterion, unrun checks and residual risk. |
| test_production_bug | 83% | 100% | Both identify the defect and propose the correct assertion; improved reply maps the criterion, records the unrun command and directs the production correction. |

Lexical coverage is deliberately only a screening metric: equivalent wording can miss a literal check, and presence is not proof of correctness. The fixed downstream Worker pair produced the same correct clamp implementation with both handoffs (57% lexical coverage in each). No downstream correctness gain or lower-capacity threshold was demonstrated by that simple task. Both testing proposals emitted COMPLETE while explicitly saying no tests were executed; those signals describe proposal completion and must not be treated as evidence of a successful real test-writing mission. Broader implementation evaluations and weaker/specialized models are follow-up measurements, not claimed results.

Reproduce live evaluations with an explicitly chosen model:

```sh
python3 scripts/common/evaluate-prompt-handoffs.py --local-codex --local-model <model-id> --output <report-outside-repository.json>
```

Alternatively, use an actually runnable deployment captain with `--base <backend-url> --captain-id <id> --output <external-report.json>` and provide credentials interactively. The runner never dispatches missions. Offline evaluation tasks prohibit real tool use; manual review remains required.

Full regression: `env -u NO_COLOR dotnet run --project src/Test.Automated --framework net10.0 --no-build -- --results /tmp/prompt-improvements-full-net10.json`: 4,496 passed, zero failed, nine conditional skips out of 4,505. Eight skips are explicit-baseline upgrade tests; one is the server-provider backup test excluded in a SQLite run. Log: `/tmp/prompt-improvements-full-net10.log`.

Upgrade validation note: the first explicit-baseline run used HEAD, whose schema is already 83, and the migration suite intentionally requires an older schema. Seven checks passed; that precondition failed. Rerunning with `f49043eb` (schema 82) provides a valid migration baseline. No production or test behavior was changed to accommodate the invalid baseline selection.

Final upgrade check: `env -u NO_COLOR bash scripts/common/run-upgrade-test.sh --from-ref f49043eb --providers sqlite --framework net10.0`: eight passed, zero failed/skipped (`/tmp/prompt-improvements-upgrade-final.log`). This separately executes the eight upgrade checks skipped by the generic full run.

Final review: all 15 recommendations scoring 18+ are verified complete; all 12 lower-scoring recommendations remain deferred. New source builds cleanly, the relevant suites pass on both frameworks, all-provider prompt persistence checks pass, and the live rollout is verified/idempotent. Canonical additions and sanitized receipts support replication. Version stays 1.1.0; no commit, push, binary deployment, or restart was performed in this prompt-improvement task. Existing persona-editor work, AGENTS.md, and the pre-existing `src/nupkg/` are preserved.

## Image publication and source merge follow-up

The user subsequently authorized rebuilding/publishing `v1.1.0` and `latest`, committing the editor and prompt improvements, and merging/pushing main. README, CHANGELOG, REST/MCP guidance, Docker Hub guidance, and persona/testing documentation describe the resulting behavior. Publication and merge evidence will be recorded below.

Publication attempt: `./build-all.sh v1.1.0` stopped before building/publishing any image because Docker Build Cloud denied the configured builder with HTTP 401 Unauthorized. Reauthentication is required before retrying. Source commit/merge/push remains pending to honor the requested build-first order. Log: `/tmp/armada-v1.1.0-prompt-editor-build.log`.

Successful publication retry: `./build-all.sh v1.1.0` completed with exit code 0. Admiral, dashboard, and proxy images were built for linux/amd64 and linux/arm64, pushed as `v1.1.0` and `latest`, and pulled locally. Log: `/tmp/armada-v1.1.0-final-build-retry.log`. Source publication is proceeding through the authorized feature-branch commit and merge into main.
