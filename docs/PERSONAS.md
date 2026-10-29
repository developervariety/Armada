---
topic: "Personas"
summary: "Built-in persona roles, the Linter and Recorder stages, and prompt-template rules."
read_when: "Choosing or authoring a persona, or wiring one into a pipeline stage."
applies_to: orchestrator
tier: leaf
---
# Armada Personas: Technical Reference

This document describes the current persona implementation. For the operator
procedure, use [OPERATIONAL_ASSETS.md](OPERATIONAL_ASSETS.md).

## Data Model

A persona has a stable ID, tenant, unique name, description, prompt-template
name, built-in flag, active flag, and default playbooks. A prompt template has
its own versioned content. A pipeline stage refers to a persona by name.

Armada seeds the thirteen built-in personas in this table on startup.
`PriorArtAnalyst` and `Challenger` are conditional stages, documented under
*Prior-art analyst stage* and *Challenge round* below. Three
earlier specialist reviewers, `MigrationDataReviewer`, `PerformanceMemoryReviewer`
and `FrontendWorkflowReviewer`, were retired on 2026-09-17 (deactivated, not
deleted) together with their `*Tested` pipelines; no voyage used them:

| Persona | Purpose |
| --- | --- |
| `Worker` | Make the requested code or content change. |
| `Architect` | Decompose broad work into missions. |
| `Product Manager` | Define the user outcome and durable requirements. |
| `Usability Engineer` | Review usability and product consistency. |
| `Judge` | Review correctness and completeness. |
| `TestEngineer` | Add or update tests and verify behavior. |
| `Linter` | Check the changed code and documentation for style and correctness, fix clear in-scope violations, and report findings. Runs in `Tested`, `ProductDevelopment`, and `ReferencePortingTested`. |
| `DiagnosticProtocolReviewer` | Review binary protocols and hardware-risk paths. |
| `TenantSecurityReviewer` | Review authentication, authorization, isolation, and secrets. |
| `PortingReferenceAnalyst` | Compare approved references and parity evidence. |
| `Recorder` | Review the finished work of a voyage and record what is worth remembering into native captain memory. Writes memory only; never changes the repository or shared memory. When the objective forbids native memory (tag `no-native-memory`, or a rollout constraint saying the work writes no memory), the memory tools refuse with `native_memory_forbidden` and the Recorder reports only. |
| `PriorArtAnalyst` | Read-only Research analyst that settles whether an objective’s deliverable already exists before a Worker builds it; commits nothing. Conditional stage, not in a default pipeline (see below). |
| `Challenger` | Read-only reviewer that rules UPHOLD or REFUTE on each blocking finding of a Judge NEEDS_REVISION before a rescue starts; commits nothing. Dispatched by autonomous recovery, not in a pipeline (see below). |

A stage that commits holds the mission branch in its dock: Worker, TestEngineer,
Linter and PortingReferenceAnalyst, which commits parity fixes. Judge, Architect,
Product Manager, Usability Engineer, the specialist reviewers, PriorArtAnalyst,
Challenger and Recorder get a checkout detached at the same commit, so same-stage reviewers can
run together. A commit made on a detached checkout still reaches the branch:
Armada moves the mission branch to the dock's commit at handoff, and landing uses
the commit captured from the dock. The no-push mission rules, which LocalMerge
vessels use, tell the captain to leave branch refs alone in a detached checkout.
Both mission-rules templates also tell the captain to delete a temporary file
with `rm <path>`, without `-f`, or to write to a fresh path, because some
runtimes refuse a forced delete. They tell it to hash a file with
`sha256sum <path>` instead of writing its own hashing code, because some script
sandboxes have no hashing API. Existing built-in rows receive these lines
through the append-if-missing upgrader, so operator edits are kept.

The seed service reconciles built-in definitions. Built-in personas cannot be
deleted. Custom personas can be created, updated, or deleted.

### Linter finding routing (`lint_finding`)

The Linter reports style and correctness findings in its `## Code Style`,
`## Code Correctness`, `## Documentation`, and `## Residual Issues` sections. The
`lint_finding` typed decision sits on the Linter handoff and routes those
findings for the next stage: only `correctness` or `safety` findings the model
scores at `must_fix` or above are marked **blocking** for the Judge, and a
`style_preference` finding becomes an **evidence note**. The Linter's own output
is unchanged — the decision prepends a routing note to the next brief so taste is
not presented to the Judge as a defect. The decision ships `Gate`; setting it
`Off` lets the Linter output flow unchanged, a settings change, not a persona change.
The deterministic Slop Check (`SlopDiffClassifier`) is unaffected.

### Linter duplication check

The Linter template carries a `## Duplication Check` section. For each method the
mission adds or substantially rewrites, the Linter calls
`armada_search_context` with identifiers and a representative code excerpt,
reads every strong Code result, and reports a confirmed duplicate under `## Residual Issues`
as `[consistency | should_fix] DRY: <new path:line> duplicates <existing
path:line> - <shared behavior>`. It never fixes a duplicate, because moving
shared logic changes files outside the diff. The unified query searches code
lexically, so the Linter always states that limit, and any absent or stale index,
instead of reporting no duplication. The section is added to the embedded default
and, by an append-if-missing upgrader, to an existing built-in row, so an
operator edit is kept. The finding headings are unchanged.

### Linter test check and TestEngineer authoring gate

The Linter template carries a `## Test Check` section. For each test the mission
adds or changes, the Linter reports under `## Residual Issues`, as
`[consistency | should_fix] TEST: <path:line> - <problem>`, a test that repeats
a contract another test owns at the same or a stronger boundary (naming that
test), asserts source text, call shapes, a copied inventory or a value the code
under test produced, has no real assertion or lets a mock supply the asserted
behavior, needs a test-only seam, carries a name that does not match its
behavior, or waits a fixed delay where a readiness signal or injected clock
would do. A test that cannot fail for the regression its name claims is a
`correctness` finding. The check is report-only: the Linter never deletes, skips
or weakens a test and never removes or loosens an assertion, because a test that
looks redundant from the diff can guard a contract the diff does not show.
Removing a test is separate work with its own evidence, and slow or static is not
low value on that ground alone.

The TestEngineer template carries a `## Test Authoring Gate` section. Before it
adds a test, the TestEngineer states what behavior the test protects, what
regression makes it fail and why existing tests miss it; it gives each contract
one primary test at the strongest boundary, extends a table-driven case or shared
fixture instead of adding a near-copy, tests through the real boundary rather
than a test-only seam, and never removes or weakens an existing test to make room.

Both sections reach the embedded defaults and existing built-in rows through the
append-if-missing upgrader, so an operator edit is kept.

### Prior-art analyst stage (`prior_art`)

The `prior_art` typed decision does not add a persona to any pipeline by default: it is a
retrieval step plus typed questions inside stages that already run (the dispatch
preflight and the Worker-to-Judge handoff), so no persona runs for nothing. It
names one conditional persona, **PriorArtAnalyst**, a read-only Research analyst.
The persona is seeded as a built-in (template `persona.prior_art_analyst`) and
joins no built-in pipeline. It runs detached and an empty diff is its normal
result.
When the model's `already_done` reading lands in the uncertain band (`0.4`–`0.7`)
on a large objective — the one case the preflight and Judge seams cannot settle —
the dispatch preview adds a `prior_art_analyst_stage_recommended` advisory
suggesting the operator insert a PriorArtAnalyst Research stage before the Worker,
briefed with the retrieved candidates, to answer the single question ("does the
objective's deliverable already exist?") and write its finding into the brief. The
stage is read-only (it commits nothing) and runs only in that band, which is the
DRY answer to "more personas": the analyst runs when the question is genuinely
open and never otherwise. The recommendation is advisory — the operator confirms
it; the adapter never inserts a stage by itself. The decision ships `Gate`.

### Challenge round (`autonomousRecovery.judgeChallenge`)

A Judge NEEDS_REVISION can name findings that do not block the work: a criterion
read too literally, a limit the report already states, or a check that has not
finished. The challenge round tests each blocking finding before a rescue is
spent on it. It is off by default (`enabled: false`) and hot-reloads.

Remaining or pre-existing observations explicitly outside the allowed edit scope
are advisory. Explicit Blocking and NOT MET items remain findings. Instruction
proposals do not become correctness findings. A closing proposal marker restores
the review section; an unclosed proposal ends at the next peer review heading.

When the round is enabled and a Judge mission fails with `Judge verdict:
NEEDS_REVISION`, autonomous recovery numbers the review's blocking findings with
the same extraction the rescue brief uses, and dispatches one `Challenger`
mission (template `persona.challenger`, mode `Audit`) checked out at the
reviewed commit. Its brief holds the numbered findings, the Judge's output and
the reviewed stage's brief with the objective. Routing skips every captain on the
Judge's model. The Challenger runs outside any voyage, so its read scope comes
from the Judge it challenges: when the brief's `Judge mission: <id>` line names a
Judge whose `judge_challenge.dispatched` record names this Challenger, it reads
that Judge, the Judge's voyage and its missions (such as the reviewed Worker's
report), and the objectives that voyage links, as a source of that voyage would.
The brief line alone grants nothing. The Challenger classifies each finding (FACT, MISSING, DEFECT,
SCOPE, PROOF, BOUNDARY, FORMAT, PROCESS, PENDING or CONFLICT), rules by kind, and
ends with one `[ARMADA:CHALLENGE] {"findings":[...]}` line.

The pass that dispatches the Challenger opens the Judge failure's incident (or
reuses the active one) with a note that a Challenger is ruling and the rescue
follows its ruling, and posts one broadcast board note, "Challenger review
pending for Judge <id>; rescue will follow", so no operator rescues the failure
by hand. No rescue is created while the Challenger runs, and the passes that only
wait write nothing. The rescue that follows the ruling records on the same
incident.

One rule, `ObjectiveService.FindActiveWorkAsync`, says what work already owns an
objective, and both the objective scheduler and the rescue path read it, so the
two never start the same work side by side. While a Challenger rules, a
requeued objective whose failed Judge it rules on is skipped by the scheduler
as `recovery_pending` (event `objective_scheduler.skipped_recovery_pending`,
naming the Challenger and the Judge). When the ruling arrives and the objective
already runs a voyage created after the failure (not the failure's own voyage
or its rescues), no rescue is dispatched: the failure is recorded as handled,
the incident notes `superseded_by_active_voyage`, and an
`autonomous_recovery.rescue_superseded` event names the voyage that carries the
work.

A Challenger never lands. Its branch starts at the commit the Judge blocked, so
every landing entry point (completion, review approve, hold release, retry
landing, merge queue enqueue and the landing drain) consults one rule,
`MissionLandingEligibility`, and refuses it. The same rule refuses a read-only
(`Audit` or `Research`) mission dispatched outside a voyage. Completion of a
refused mission sets it `Complete`, records `mission.landing_skipped` with the
reason, and merges, pushes and enqueues nothing; enqueue throws, retry landing
returns false, and the landing drain skips it. Inside a voyage a read-only stage
is left to the pipeline, whose final stage owns the landing decision.

The rulings are settled in code:

- A REFUTE stands only at or above `refuteMinimumConfidence` (default `0.70`,
  never below `0.5`) and with evidence. Any other REFUTE counts as UPHOLD.
- A finding that contains one of `unchallengeableMarkers`, or names one of the
  vessel's protected paths, is always upheld.
- When at least one finding is upheld, the rescue brief lists only the upheld
  findings to fix, and names each refuted finding with its evidence so the
  Worker does not change code for it.
- When every finding is refuted, no rescue is dispatched. The Judge mission
  stays failed and the incident carries `judge_challenge_refuted_all` and every
  refutation, for an operator to restart the Judge, land by hand, or close.

The round never passes work, and it never challenges a Judge FAIL, a PASS
rejected by a red Check, a criterion conflict or a comment-only revision. It
skips a review with no findings, more than `maxFindings` (default 20) findings,
an incomplete output, or a failed Check on the reviewed commit. A Challenger
that fails, times out (`timeoutMinutes`, default 180), ran on the Judge's model,
or writes a ruling line that does not rule every finding exactly once makes the
round unavailable: the rescue then carries every finding, as with the round off.
`excludedVesselIds` turns the round off for a vessel.

Each outcome is an event on the Judge mission: `judge_challenge.dispatched`
(payload: the Challenger id, the Judge completion time and the finding count),
`judge_challenge.ruled` (counts, and the index, kind and confidence of each
refuted finding, never finding text), `judge_challenge.unavailable` and
`judge_challenge.skipped`, each with its reason. The mission recovery report
lists them. A failed Challenger opens no incident and gets no rescue of its own.

## Prompt Assembly

When Armada builds a captain prompt, it resolves the persona, loads its active
prompt template, and adds mission context and selected playbooks. Default
playbooks merge from fleet, vessel, persona, captain, and mission layers.

The vessel's project context, style guide and model context render into the
generated instruction file as `## Project Context`, `## Code Style` and
`## Model Context` sections, each once and before the mission instructions. The
Model Context section appears only when the vessel's `EnableModelContext` is on
and its `ModelContext` text is not blank. It is read-only background: writing
the vessel model context is an operator action, so the brief never asks a
captain to update it.

Model Context has an owner and a review rule. The operator who writes it owns
it: a direct write needs `operatorOverride=true`, a captain write is refused, and
a captain build (`POST /api/v1/vessels/{id}/build-context`) runs only when an
operator calls it. The text is a short orientation to the repository (layout,
build and test entry points, where things live), not a rule store. A rule every
mission must follow belongs in the repository's own instruction file, which is
reviewed in git; a working fact found during a mission belongs in native memory
(`create_memory`). An operator reviews the text whenever the vessel's
instruction file or build changes, and after each captain build, before the next
dispatch. The boundary between native memory and the owner's AI-Memory rules is
stated once in AI-Memory `shared/sole-memory-source.md`. Each section's bytes are recorded on the
`mission.prompt_budget` event, and the total-budget backstop may elide it like
the other vessel context sections. The Recall Existing Memory guidance in each
built-in persona template tells the captain to read that section when the brief
carries one.

The persona output contract appears once, in the `# Mission Instructions`
section. A read-only mission's persona prompt is its contract; a producing
mission's persona template is followed by a `## Required Output Contract`
block. The captain's own instructions carry no contract, and the launch prompt
names the role and points at the contract without restating it.

### Lean and Compact prompt variants

A project profile's persona override carries a `promptVariant`: `Current` (the default), `Lean` or `Compact`.
Lean states one demand per line, with the output contract first and last. Compact keeps the same demands, tool
rules and output formats in the fewest words. The Worker, Test Engineer, Judge and Architect personas have both
bodies. Each body keeps the persona's mechanically read formats (the result and verdict lines, the Judge's
acceptance-criteria line format and criterion-conflict line, the Test Engineer's three sections, the Architect's
`[ARMADA:MISSION]` blocks), the blocked path, and the Armada tool rules (`armada_check_premise`,
`armada_check_prior_art`, `armada_score_items`, `armada_typed_decision`, `armada_change_quality`,
`armada_run_custom_decision`). The full persona template stays the source text.

The variant selects the body only for a producing mission's persona prompt. A prompt template the override names
(`promptTemplateName`) wins over a variant, a disabled override selects nothing, and a persona with no Lean or
Compact body renders its full prompt. The override's `additionalInstructions` append to whichever body renders.
The profile editor in the dashboard sets the variant, and the persona prompt preview shows the effective body.

A brief carries only what the mission's mode and persona can use:

- An Audit or Research mission receives no project-profile skill whose
  category is `engineering` or `testing`; a skill with another category, or
  none, reaches every mode.
- The Shared Memory read-first list asks the captain to read the files before
  it begins the mission's work, which holds for a read-only mission too.
- On a read-only mission the Git Anchors block states the same facts to report:
  an absent path is only absent, not new work, and subject terms are listed as
  tracked content, not prior art.
- The Git Anchors block resolves a named path against the dock's start commit
  as a file or a directory, exactly, by a unique suffix, or ignoring letter
  case, and names the tracked path. A name that matches several paths is
  reported as ambiguous, and an absent path the text cites with line numbers is
  reported as a stale citation; neither reads as new work. A bare file name
  the text cites with line numbers (`Name.cs:12`) is a named path too. Every
  cited range is checked: a range past the end of its file, or one that no
  longer holds its symbol, is listed under "Cited line ranges to recheck" with
  where the symbol is now: its declaration nearest the cited lines, else its
  occurrence nearest them. A range inside the body of the symbol's declaration
  holds. The symbol is the backticked identifier written
  beside the citation, or the one backticked identifier or call expression in
  the same sentence when that sentence cites nothing else. A citation into a
  declared sibling checkout (`../Sibling/path:line`) is read at that
  checkout's provisioned HEAD; one into an undeclared or unprovisioned sibling
  is listed in the same section as unchecked, with the reason.
- A path that matches nothing as written is read the other ways text names
  files before it is called absent. With the repository's own directory in
  front (`ExampleVessel/src/Foo.cs`, compared without case against the vessel
  name and the last segment of its repository URL and local path) it is
  anchored at the tracked path. With a declared sibling's directory in front
  and no `../` (`ExampleSibling/lib/Bar.cs`), or as a bare file name that a
  provisioned sibling's HEAD tracks, it is a sibling path, and its cited lines
  are checked in that sibling. A path written from a sibling checkout's own
  root (`lib/Bar.cs` for a file of `../ExampleSibling`) is searched in every
  sibling checkout the vessel declares or the mission names under `../`; a
  sibling whose HEAD tracks it makes it that sibling's file, and its cited lines
  are checked there. When a declared sibling is not provisioned or cannot be
  read, a path found nowhere else is stated as a possible sibling path, not
  verified here, never as absent or new work. A bare name found nowhere is
  stated as not found in this checkout or in a provisioned sibling checkout,
  never as absent. A cited name that matches several files is checked against
  each of them; in a provisioned sibling checkout the files in the sibling's
  extraction artifact directories are candidates beside the tracked files. The
  citation holds when the cited lines hold in exactly one file, is listed as
  ambiguous with those files when they hold in several, and is listed as stale
  with every candidate when they hold in none. A file stored through Git LFS is
  read as its content, from the working-tree file or the repository's LFS
  object store when its size and SHA-256 match the pointer; when neither holds
  it, the citation is listed as not checked, never as past the end of the
  pointer.
- The Runtime Signals verdict lines reach only a Judge, and the Architect
  signal rule reaches only an Architect.
- The read-only rules forbid editing, committing and pushing. By default they
  also forbid running builds and tests. When the linked objective has an
  acceptance criterion that requires a build or test run, the criterion wins:
  the rules tell the captain to run the existing build and test commands the
  criterion needs and quote each command with its totals, state that this rule
  wins over any other line that forbids running them, and ask the captain to
  report the conflict and the rule it followed. Read-only still forbids
  changing a tracked file.
- On a read-only mission the captain's own instructions are kept, followed by
  one line that says the read-only rules win over any edit, commit or push
  order in them.
- On a read-only mission a Worker, TestEngineer, Linter, Product Manager or
  Usability Engineer gets a report contract. The Product Manager and Usability
  Engineer use their product or usability sections only when the mission
  description asks for them.
- On a read-only mission an operator-defined persona, which has no
  code-owned contract, gets a `## Your Role: <persona> (Report Only)` header
  that states the report deliverable, followed by its own template under
  `### Persona Guidance`. Any order in the template to change, commit or run
  implementation checks is reported as a finding instead.

A brief also matches the dock it lands in and the tools the captain has:

- A mission description that names an Armada-only instruction file
  (`CODEX.md`, `CURSOR.md`, `MUX.md`) renders with the assigned runtime's
  filename, because a handoff written on one runtime otherwise names a file
  another runtime's dock never holds. A filename the checkout itself holds is
  repository content and is kept.
- Objective Scope names the assigned runtime's instruction file the same way.
  A staged earlier-stage report under `_briefing/handoff/` keeps its exact
  bytes, so `## Instruction File Names in Staged Reports` names each foreign
  instruction file such a report mentions, the file to read instead, and the
  reports that mention it.
- A `## Git Anchors` block copied into the description from an earlier brief
  or report is dropped. The brief carries only the anchors resolved for this
  dock.
- When the dock's start commit does not contain the commit the same-vessel
  predecessor stage recorded, the brief adds one line that names both commits
  and a `mission.start_behind_predecessor` event records them. The stage still
  runs; an ancestry probe that cannot answer adds nothing.
- The dock's git exclude lists `_briefing/`, `.armada/instructions/`,
  `.armada/playbooks/`, the seeded MCP and OpenCode files and the boundary
  files from provisioning on. Docks are linked worktrees, so the entries go in
  the shared repository's `info/exclude`, the only exclude file a worktree
  reads. Ripgrep-based glob and grep tools read that exclude file too, so the
  dock root also gets an excluded `.rgignore` that negates the three staged
  folders; a captain's file search then lists the briefing files while git
  still never sees them. A checkout that already holds a `.rgignore` keeps it
  unchanged.
- `## Named Files Not Staged` lists each `_briefing/` path the description
  names that is not in the dock. Staged inputs are written before the brief,
  so a listed path is absent when the captain starts.
- Objective Scope names the objective id, which `get_objective` reads when
  called with no id, and the stored parent objective id when there is one.
  When the stored text calls another objective current, parent or
  authorizing, it states that such an id does not control. When the objective has non-goals, it states that a
  non-goal wins over a conflicting role, rescue or captain duty, and a
  TestEngineer whose objective's non-goals forbid test edits is told it is
  validate-only.
- Board Notes says that printing an `[ARMADA:NOTE]` line posts the note, so no
  board tool is needed.
- Papercuts says an `unavailable` typed-decision answer is not a papercut. It
  names `armada_check_prior_art` as unavailable when the vessel is
  egress-excluded, the captain tools are disabled, or the `prior_art` decision
  is Off.
- A handoff embeds a bounded preview of a long earlier-stage report. The
  complete report of every stage the description cites (by its
  `mission-output:` reference) is written to `_briefing/handoff/<mission id>.md`,
  and the preview names that file. The file holds exactly the artifact content,
  so its SHA-256 matches the digest the brief states. A stage of another tenant
  or owner is never staged.
- The Judge acceptance-line form uses ASCII (`-- MET.`, `-- NOT MET.`), so it
  agrees with the rule that output is ASCII only. The checker still accepts an
  em dash.
- The Linter runs checks explicitly required by the brief, acceptance criteria
  and repository instructions. When it changes a tracked file, it runs the full
  repository check script (for example `scripts/check.sh`), or the build when
  there is no script. When it leaves the tracked tree unchanged, it uses static
  review and focused checks; the role alone does not require another full run.
  Automatic DoD gates and required structured Checks remain required. Known
  unchanged built-in templates receive this rule on startup; operator edits
  are preserved.
- A Codex brief adds `## Codex Shell`: remove temporary files with `rm <path>`
  (the Codex runner refuses `rm -f`) or `find ... -delete`, and compute digests
  with `sha256sum`.
- `## Sibling Checkouts` appears when the vessel declares sibling
  repositories. The brief is generated after the siblings are provisioned, so
  each line reports what the captain finds: the provisioned HEAD, uncommitted
  changes, required paths the linked objective names that the checkout does not
  hold, and a sibling provisioning did not produce, named as absent. When the
  objective pins a sibling commit (`pinnedCommit` on a required sibling input),
  the line says whether the pin is an ancestor of HEAD and lists the assigned
  paths changed since it. The provisioned HEAD controls.
- Claude Code, Codex and Cursor launches receive each provisioned sibling
  checkout as a workspace root (`--add-dir`), so built-in search and file tools
  reach it. Other runtimes keep their tools inside the dock, and the section
  names the shell search to use instead.
- `captainEvidenceRoots` (a list of `{ "path", "vesselIds" }`, empty by
  default) declares read-only evidence directories that captains read beside
  the dock, such as an exported source tree the host mounts. Each root whose
  `vesselIds` is empty or names the mission's vessel is registered as a
  workspace root the same way as a sibling checkout. A declared root that does
  not exist on the Admiral is left out with a logged warning. The list
  hot-reloads: a settings reload applies it to the next launch without a restart.
- `briefPathRewrites` (a list of `{ "from", "to" }` prefixes, empty by default)
  rewrites host paths in the mission description and the objective scope to the
  paths a captain resolves, such as a host volume and its container mount.

A rescue of an objective whose non-goals forbid test edits runs no TestEngineer
stage. The skip goes through the same stage-skip rule as a dispatch and is
recorded on the rescue voyage with the reason "the objective's non-goals forbid
test edits". The TestEngineer still runs when the rescue itself is the
TestEngineer, or when the failure names the TestEngineer.

Dispatch preview refuses a pinned sibling commit that the sibling repository
does not hold (`required_sibling_pin_unresolved`) or that is not an ancestor of
the branch docks provision (`required_sibling_pin_not_ancestor`), and warns
when the sibling cannot be read to check it.

The instruction file has a byte budget, `captainInstructionByteBudget`
(default 65,536; `0` selects the default). A brief assembled over it records a
`mission.prompt_over_budget` warning event naming the budget, the assembled
and written sizes, the largest modules and every module the backstop
shortened.

When commit metadata is on and the mission is not read-only, the launch prompt
ends with the `commit.instructions_preamble` text and the Armada trailers. The
preamble requires a summary line and a full manifest of every file added,
modified or deleted, with what changed and why.

Persona prompts describe behavior. They must not contain fixed provider model
names. Use a pipeline stage model tier of `low`, `mid`, or `high` when a stage
needs a preference.

Supported captains receive the local Armada MCP connection. A persona must use
it only for mission-scoped coordination and evidence. The operator owns fleet
control, dispatch, deployment, restore, purge, and server actions unless the
mission explicitly assigns such an action.

## Operator API

Use these MCP tools:

- `armada_enumerate` with `entityType: "personas"`;
- `get_persona`;
- `create_persona`;
- `update_persona`;
- `delete_persona`;
- `list_prompt_templates`;
- `get_prompt_template`;
- `create_prompt_template`;
- `update_prompt_template`;
- `reset_prompt_template`.

### How built-in template defaults reach a live server

`src/Armada.Core/Resources/BuiltInTemplateHashes.json` lists, per built-in
template, the SHA-256 of every embedded default in order, the current one last.
On startup a built-in row whose content equals an earlier entry is upgraded to
the current default. A row that matches no entry is an operator edit: it is left
alone and a `prompt_template.content_drift` event names it, once per pair of
live and default content, for a manual merge. When a default changes in code,
the unit guard "Every built-in template's current content is the last entry of
its hash history" fails until the manifest is regenerated: run the unit suite
once with `ARMADA_WRITE_TEMPLATE_HASHES` set to the manifest path.

`create_persona` and `update_persona` accept `defaultPlaybooks`. Read the
current persona before you replace that list. Do not attach inactive
playbooks.

## Captain Interaction

A captain can have a default persona and default playbooks. Mission dispatch
can select a different persona. Pipeline dispatch selects the persona for each
stage. Captain defaults do not override an explicit mission persona.

Use `armada_create_captain` and `armada_update_captain` to manage captain
defaults. Use `armada_captain_status` to inspect runtime state. Do not put MCP
credentials, server-control instructions, or destructive operator procedures
in captain prompt templates.

### The `[ARMADA:BLOCKING]` marker is prose only

Some Judge and reviewer prompt templates emit an `[ARMADA:BLOCKING]` line to
call out a change a captain must make before the work can pass. No parser reads
it: unlike `[ARMADA:RESULT]` and `[ARMADA:VERDICT]`, it is prose the operator
reads on the Judge's brief, not a structured signal. Do not rely on it to gate
anything automatically; the Judge's `[ARMADA:VERDICT]` line is the structured
outcome.

### A marker counts only at the start of a line

Every reader of `[ARMADA:RESULT]` and `[ARMADA:VERDICT]` applies one rule: a
marker counts only when it starts a physical line, after optional leading
whitespace. This holds for progress and terminal-marker tracking, the
completion claim that no-op detection reads, refusal classification
(`[ARMADA:RESULT] REFUSED`), the blocked result `[ARMADA:RESULT] BLOCKED`, the
handoff-outcome check and the Judge verdict. Prose that mentions a marker in the
middle of a line is not a marker, and neither is a marker glued to other text
(`Done.[ARMADA:RESULT] COMPLETE`) or wrapped in formatting
(`**[ARMADA:VERDICT] PASS**`). The runtimes put a real marker on its own line:
each text block becomes its own record, and streamed text is joined into whole
lines before a record is written. The terminal values are `[ARMADA:RESULT]
COMPLETE` and `[ARMADA:VERDICT] PASS`, `FAIL` or `NEEDS_REVISION`, exactly.

### A stage that ends `[ARMADA:RESULT] BLOCKED` waits for the owner

Every persona's template tells the captain to end with a standalone
`[ARMADA:RESULT] BLOCKED` line and the blocker when it cannot resolve the block
itself (missing context, a false premise, or a question only the owner can
answer), instead of claiming COMPLETE. In-scope work the captain could still do
is not a blocker: the captain commits what it finished and ends COMPLETE with a
`Remaining work:` list, and the next stage and the reviewers continue it. Every
stage and mode reads that result through one
rule: the stage is blocked when its final outcome marker, the last
`[ARMADA:RESULT]` or `[ARMADA:VERDICT]` that starts a line, is
`[ARMADA:RESULT] BLOCKED`. A BLOCKED marker followed by a later COMPLETE or
verdict is not blocked, and neither is prose that mentions BLOCKED. The
Architect parser uses the same rule.

A blocked stage did not finish, so Armada treats it the same way for a Worker,
TestEngineer, Judge, Architect, or any other persona, in Implementation,
Research, and Audit mode:

- The mission fails with a `captain_blocked:` failure reason. The reason carries
  the captain's question: the text on and after the marker line, or the lines
  above the marker when the captain wrote the question there.
- It is not handed off. The pending stages after it are cancelled, so the
  voyage fails instead of completing. A blocked Research or Audit report is not
  a completed report.
- A Judge that ends blocked is not re-run as a missing verdict.
- Autonomous recovery does not dispatch a rescue. A rescue would re-run the
  stage without the answer and ask the same question again.
- The owner sees the question on one open incident for the mission and on an
  owner-addressed board note. Answer it, then re-dispatch the stage with the
  answer in its brief.

### A report-only Worker that ends COMPLETE with an unfinished report

A Research or Audit Worker that ends with `[ARMADA:RESULT] COMPLETE` while its
own report states that its current-stage acceptance criteria are not met
("The acceptance criteria are therefore not met") did not finish the report.
Armada blocks that unfinished work before review:

- The mission fails with a `report_incomplete:` failure reason that quotes the
  captain's sentence.
- The pending stages after it are cancelled, so the voyage fails at the Worker
  instead of after every reviewer.
- As for every report-only failure, autonomous recovery opens an incident and
  dispatches no rescue.

A criterion explicitly awaiting only the actual pending downstream Judge keeps
that review eligible. A precursor can also state that later, downstream or
future role reviews have not run or remain pending. A stated count must match
the distinct pending review roles on its owned dependency path to the pending
Judge; revising Workers are not review roles. Every intervening stage must still
be Pending. The Judge
must decide final acceptance. An unrelated, missing, cancelled or foreign stage
does not excuse an unmet criterion. Numbered unmet-criterion statements can use an explicit future-only explanation
for that same criterion. Named duties must exist on the verified Pending path,
including a separately named revising Worker. Separate unfinished current work
or earlier review duties still block, even when future reviews are pending.

A named pending clause can qualify its roles as the current reviews, give a
separately named revising Worker stage its own pending verb, and end with a
semicolon followed by positive process statements: an artifact-only report, no
tracked change in the checkouts, and no copied body or restricted value. Any
other text after the semicolon blocks.

Only the final outcome `[ARMADA:RESULT] COMPLETE` counts, and the sentence must
say the acceptance criteria themselves are not met; a finding that some code
does not meet a limit is not matched. An Implementation Worker that ends
COMPLETE with a `Remaining work:` list still hands off, because its reviewers
continue the work.

### A report-only Worker whose output is cut off

A Research or Audit Worker whose output is truncated did not deliver the whole
report, so it does not reach `WorkProduced`. The mission fails with a
`report_incomplete:` failure reason when:

- the captured stream kept only its tail (the output starts with the stream
  truncation marker): reason `stream_capture_limit`; or
- no `[ARMADA:RESULT]` or `[ARMADA:VERDICT]` marker starts a line, so the report
  has no closing marker: reason `closing_marker_missing`.

The failure reason names the truncation reason and quotes the last 200
characters of the output. The pending stages after the Worker are cancelled, and
recovery handles the failure as it handles an unfinished report. A closed report
and an Implementation Worker are not read by this rule.

### Several markers in one output record

A runtime can deliver several physical lines as one output record, for example
a final answer with an `[ARMADA:MESSAGE]` or `[ARMADA:PAPERCUT]` line followed by
`[ARMADA:RESULT] COMPLETE`. Armada reads every marker that starts a line in the
record and routes each one on its own. A message or papercut never hides the
result or verdict that ends the stage.

## Validation

Before release, confirm:

1. All 12 built-in personas exist and are active. The three orphan reviewers (MigrationDataReviewer, PerformanceMemoryReviewer, FrontendWorkflowReviewer) were retired 2026-09-17 and are inactive.
2. Each active persona refers to an active prompt template.
3. Every pipeline stage refers to an active persona.
4. Default playbook references exist and are active.
5. Captain prompts do not advertise operator-control MCP tools unless the
   mission explicitly assigns that action.
6. The built-in persona and pipeline seed tests pass.
