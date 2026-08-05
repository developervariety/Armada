namespace Armada.Core.Services
{
    using System.Text.RegularExpressions;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Shared mission prompt/context builder used by both CLAUDE.md generation
    /// and direct runtime launch prompts.
    /// </summary>
    public static class MissionPromptBuilder
    {
        private const int MaxLaunchPromptChars = 6000;
        private const int MaxPersonaSummaryChars = 320;
        private const int MaxCaptainInstructionChars = 800;
        private const int MaxMissionDescriptionChars = 3500;

        /// <summary>
        /// Resolve the runtime-specific mission instructions filename. Every runtime in
        /// AgentRuntimeEnum must be listed explicitly: OpenCode was previously absent and fell through
        /// to CLAUDE.md, which is why no OPENCODE-named instruction snapshot has ever existed.
        /// OpenCode is mapped to AGENTS.md, the file it loads natively, so the mission brief arrives
        /// without a separate read step.
        /// </summary>
        public static string GetInstructionsFileName(string? runtime)
        {
            if (String.IsNullOrWhiteSpace(runtime)) return "CLAUDE.md";

            return runtime.Trim() switch
            {
                "ClaudeCode" => "CLAUDE.md",
                "Codex" => "CODEX.md",
                "Cursor" => "CURSOR.md",
                "Gemini" => "GEMINI.md",
                "Mux" => "MUX.md",
                "OpenCode" => "AGENTS.md",
                _ => "CLAUDE.md"
            };
        }

        /// <summary>
        /// Reports whether the runtime loads its Armada instruction filename by itself, with no prompt
        /// instruction to read it. True only when the filename Armada writes is also the runtime's own
        /// convention: CLAUDE.md for Claude Code, AGENTS.md for OpenCode, GEMINI.md for Gemini.
        ///
        /// It is false for Cursor and Mux, whose Armada filenames (CURSOR.md, MUX.md) are Armada
        /// conventions that no runtime reads on its own, and false for Codex, which writes CODEX.md
        /// while Codex natively reads AGENTS.md. Those cases still need the read instruction, and
        /// still need an existing root file inlined, because nothing else would surface it.
        ///
        /// Callers use this to avoid paying twice for the same text: when the runtime already
        /// auto-loads the root file, inlining that file into the generated brief delivers it twice.
        /// </summary>
        /// <param name="runtime">Runtime name.</param>
        /// <returns>True when the runtime auto-loads the file Armada names for it.</returns>
        public static bool RuntimeAutoLoadsInstructionsFile(string? runtime)
        {
            if (String.IsNullOrWhiteSpace(runtime)) return false;

            return runtime.Trim() switch
            {
                "ClaudeCode" => true,
                "OpenCode" => true,
                "Gemini" => true,
                _ => false
            };
        }

        /// <summary>
        /// Resolve the ignored Armada-owned path used when the repository already
        /// has a root runtime instruction file.
        /// </summary>
        public static string GetGeneratedInstructionsRelativePath(string? runtime)
        {
            return ".armada/instructions/" + GetInstructionsFileName(runtime);
        }

        /// <summary>
        /// Build a consistent template parameter dictionary for mission prompt rendering.
        /// </summary>
        public static Dictionary<string, string> BuildTemplateParams(
            Mission mission,
            Vessel vessel,
            Captain? captain = null,
            Dock? dock = null,
            TestOwnershipEnum ownership = TestOwnershipEnum.Unknown,
            string? judgePrimaryLens = null)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));

            return new Dictionary<string, string>
            {
                ["MissionId"] = mission.Id,
                ["MissionTitle"] = mission.Title,
                ["MissionDescription"] = mission.Description ?? "No additional description provided.",
                ["MissionPersona"] = PersonaCatalog.NormalizeName(mission.Persona ?? PersonaCatalog.Worker),
                ["VoyageId"] = mission.VoyageId ?? "",
                ["VesselId"] = vessel.Id,
                ["VesselName"] = vessel.Name,
                ["DefaultBranch"] = vessel.DefaultBranch,
                ["BranchName"] = dock?.BranchName ?? mission.BranchName ?? "unknown",
                ["FleetId"] = vessel.FleetId ?? "",
                ["ProjectContext"] = vessel.ProjectContext ?? "",
                ["StyleGuide"] = vessel.StyleGuide ?? "",
                ["ModelContext"] = vessel.EnableModelContext ? vessel.ModelContext ?? "" : "",
                ["SelectedPlaybooksMarkdown"] = "",
                ["CaptainId"] = captain?.Id ?? "",
                ["CaptainName"] = captain?.Name ?? "",
                ["CaptainInstructions"] = BuildCaptainInstructions(captain?.SystemInstructions, mission.Persona, mission.Mode, judgePrimaryLens),
                // Always written, empty string included: RenderAsync substitutes only keys present in
                // the dictionary and would otherwise leave a literal {TestOwnership} in the brief.
                //
                // A read-only mission writes no tests, so it gets no ownership directive. Emitting one
                // would tell an audit captain to run tests and commit them, contradicting the read-only
                // rules in the same brief.
                ["TestOwnership"] = mission.IsReadOnlyMode
                    ? String.Empty
                    : TestOwnershipResolver.BuildDirective(mission.Persona, ownership),
                ["Timestamp"] = DateTime.UtcNow.ToString("o")
            };
        }

        /// <summary>
        /// Normalize a persona name into the template naming convention.
        /// e.g. Test Engineer -> persona.test_engineer
        /// </summary>
        public static string GetPersonaTemplateName(string? persona)
        {
            if (String.IsNullOrEmpty(persona)) return "persona.worker";
            string normalizedPersona = PersonaCatalog.NormalizeName(persona);
            if (String.IsNullOrEmpty(normalizedPersona)) normalizedPersona = persona.Trim();
            string normalized = Regex.Replace(normalizedPersona, "([a-z0-9])([A-Z])", "$1_$2");
            normalized = Regex.Replace(normalized, "[\\s\\-]+", "_");
            normalized = Regex.Replace(normalized, "_+", "_").ToLowerInvariant();
            return "persona." + normalized;
        }

        /// <summary>
        /// Resolve the persona prompt for the mission.
        /// </summary>
        public static async Task<string> ResolvePersonaPromptAsync(
            string? persona,
            Dictionary<string, string> templateParams,
            IPromptTemplateService? promptTemplates,
            CancellationToken token = default)
        {
            if (templateParams == null) throw new ArgumentNullException(nameof(templateParams));

            string templateName = GetPersonaTemplateName(persona);

            if (promptTemplates != null)
            {
                string rendered = await promptTemplates.RenderAsync(templateName, templateParams, token).ConfigureAwait(false);
                if (!String.IsNullOrEmpty(rendered))
                    return rendered;
            }

            // The fallback path must carry the same ownership directive as the template path, or a
            // mission whose template is missing silently loses the rule.
            string fallback = GetPersonaPromptFallback(persona);
            string? ownershipDirective;
            if (templateParams.TryGetValue("TestOwnership", out ownershipDirective) &&
                !String.IsNullOrEmpty(ownershipDirective))
            {
                fallback = fallback + "\n\n" + ownershipDirective;
            }

            return fallback;
        }

        /// <summary>
        /// Build the direct runtime launch prompt from the same shared context used by mission instructions.
        /// </summary>
        public static async Task<string> BuildLaunchPromptAsync(
            Mission mission,
            Vessel vessel,
            Captain captain,
            Dock dock,
            IPromptTemplateService? promptTemplates,
            CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            if (dock == null) throw new ArgumentNullException(nameof(dock));

            string instructionsFileName = GetInstructionsFileName(captain.Runtime.ToString());
            string generatedInstructionsPath = GetGeneratedInstructionsRelativePath(captain.Runtime.ToString());

            List<string> sections = new List<string>();
            sections.Add("Role: " + BuildBootstrapRoleSummary(mission.Persona));
            sections.Add("Mission: " + mission.Title);
            sections.Add("Branch: " + (dock.BranchName ?? mission.BranchName ?? vessel.DefaultBranch ?? "main"));

            string instructionDirective = BuildInstructionDirective(
                dock.WorktreePath,
                instructionsFileName,
                generatedInstructionsPath);

            if (String.Equals(mission.Persona, "Architect", StringComparison.OrdinalIgnoreCase))
            {
                sections.Add(
                    instructionDirective + " " +
                    "It contains the objective, repository context, and mission-format requirements. " +
                    "Do not ask for more input. Read the file immediately and respond only with real [ARMADA:MISSION] blocks derived from that file.");
            }
            else
            {
                sections.Add(
                    instructionDirective + " " +
                    "It contains the full mission objective, repository context, style guide, model context, and execution rules. Do not ask for more input. Read the file immediately and follow it exactly. After reading it, perform the mission now; do not stop after acknowledging or summarizing the instructions. For an Implementation mission, a standalone COMPLETE line is valid only after the requested work is complete and the required changes are saved.");
            }

            string prompt = String.Join(" ", sections.Select(s => s.Replace("\r", " ").Replace("\n", " ").Trim())).Trim();
            if (prompt.Length <= MaxLaunchPromptChars)
                return prompt;

            string overflowMessage = "\n\n" + instructionDirective + " contains the remaining context. Keep working from that file if this launch prompt was truncated.";
            int allowed = Math.Max(256, MaxLaunchPromptChars - overflowMessage.Length);
            return prompt.Substring(0, allowed).TrimEnd() + overflowMessage;
        }

        private static string BuildInstructionDirective(
            string? worktreePath,
            string instructionsFileName,
            string generatedInstructionsPath)
        {
            if (!String.IsNullOrWhiteSpace(worktreePath))
            {
                string rootPath = Path.Combine(worktreePath, instructionsFileName);
                string generatedPath = Path.Combine(worktreePath, generatedInstructionsPath);

                if (File.Exists(generatedPath))
                    return "Read `" + generatedInstructionsPath + "` immediately.";

                if (File.Exists(rootPath))
                    return "Read `" + instructionsFileName + "` in the working directory immediately.";

                return "The mission instruction file is missing. Do not claim completion; report the missing file before doing other work.";
            }

            // Unit callers may construct a Dock without a worktree path. The real launch path always
            // supplies one, so retain a deterministic path for those callers without inventing a fallback.
            return "Read `" + generatedInstructionsPath + "` immediately.";
        }

        private static string BuildBootstrapRoleSummary(string? persona)
        {
            return PersonaCatalog.NormalizeName(persona) switch
            {
                PersonaCatalog.Architect => "You are an Armada architect agent. Respond only with real [ARMADA:MISSION] blocks. Do not emit [ARMADA:RESULT] or [ARMADA:VERDICT] lines.",
                PersonaCatalog.ProductManager => "You are an Armada product manager agent. Include `## Product Vision`, `## Use Cases`, `## Experience Requirements`, `## Validation`, and `## Future Readiness` sections before a standalone [ARMADA:RESULT] COMPLETE line.",
                PersonaCatalog.UsabilityEngineer => "You are an Armada usability engineer agent. Include `## Usability`, `## Consistency`, `## Edge Cases`, and `## Residual Risks` sections before a standalone [ARMADA:RESULT] COMPLETE line.",
                PersonaCatalog.Worker => "You are an Armada worker agent. End with a standalone [ARMADA:RESULT] COMPLETE line followed by a brief plain-text summary.",
                PersonaCatalog.TestEngineer => "You are an Armada test engineer agent. Include `## Coverage Added`, `## Negative Paths`, and `## Residual Risks` sections before a standalone [ARMADA:RESULT] COMPLETE line.",
                PersonaCatalog.Judge => "You are an Armada judge agent. Include `## Completeness`, `## Correctness`, `## Tests`, `## Failure Modes`, and `## Verdict` sections, and end with exactly one standalone [ARMADA:VERDICT] PASS, [ARMADA:VERDICT] FAIL, or [ARMADA:VERDICT] NEEDS_REVISION line. Emit that verdict line before any completion signal or exit; a review without it is discarded and re-run.",
                _ => "You are an Armada captain executing a mission."
            };
        }

        private static string SummarizeText(string? input, int maxChars)
        {
            if (String.IsNullOrWhiteSpace(input)) return "";

            string compact = Regex.Replace(input, "\\s+", " ").Trim();
            if (compact.Length <= maxChars) return compact;
            if (maxChars <= 3) return compact.Substring(0, maxChars);
            return compact.Substring(0, maxChars - 3).TrimEnd() + "...";
        }

        private static string BuildRoleSummary(string? persona, string personaSummary)
        {
            if (PersonaCatalog.Matches(persona, PersonaCatalog.Architect))
            {
                return "You are an Armada architect agent. Analyze the objective and decompose it into right-sized missions using [ARMADA:MISSION] markers. Do not emit [ARMADA:RESULT] or [ARMADA:VERDICT] lines.";
            }

            if (!String.IsNullOrEmpty(personaSummary))
                return personaSummary;

            return GetPersonaPromptFallback(persona);
        }

        private static string BuildCaptainInstructions(string? existingInstructions, string? persona, MissionModeEnum mode, string? judgePrimaryLens = null)
        {
            string existing = existingInstructions?.Trim() ?? String.Empty;
            string outputContract = GetPersonaOutputContract(persona, mode, judgePrimaryLens);

            if (String.IsNullOrEmpty(outputContract))
                return existing;

            if (String.IsNullOrEmpty(existing))
                return outputContract;

            return existing + "\n\n## Required Output Contract\n" + outputContract;
        }

        /// <summary>
        /// The three distinct review lenses a Judge pool can be split across. When a voyage runs
        /// multiple Judges in parallel, each Judge is assigned ONE primary lens (round-robin over
        /// this list) so the pool covers different failure modes instead of sharing a blind spot.
        /// </summary>
        internal static readonly string[] JudgeLensNames = new string[]
        {
            "CORRECTNESS",
            "SAFETY & BLAST-RADIUS",
            "SOURCE-FIDELITY"
        };

        /// <summary>
        /// Anti-Goodhart guidance appended to every Judge prompt: review through three DISTINCT lenses
        /// (redundant identical verifiers share a blind spot), and only block on a real, corpus-present
        /// affected case (hypothetical patterns that cannot manifest are follow-ups, not blockers -- this
        /// is what prevents an adversarial Judge treadmill of ever-more-exotic non-occurring defects).
        /// </summary>
        internal const string JudgeLensAndBoundedRule =
            " Review through THREE distinct lenses, not one identical pass: (1) CORRECTNESS -- does it do" +
            " what was asked, with hidden bugs surfaced; (2) SAFETY & BLAST-RADIUS -- what breaks if this" +
            " is wrong (weigh terminal/command/write frames, seed-key, and cross-tenant/secret exposure" +
            " highest); (3) SOURCE-FIDELITY -- ported values, frames, and test vectors must be corroborated" +
            " to real source, never synthetic. BOUNDED-JUDGE RULE: to BLOCK (FAIL or NEEDS_REVISION) you" +
            " must exhibit a REAL, corpus-present affected case -- concrete inputs or state where the defect" +
            " actually manifests. A hypothetical pattern that cannot occur in the actual code/corpus is a" +
            " tracked follow-up note in your review, NOT a blocker.";

        /// <summary>
        /// Build the Judge guidance for a mission. When the voyage runs parallel Judges, each Judge
        /// receives a DISTINCT primary lens so the pool does not share one blind spot; the bounded-judge
        /// rule is always included. A null lens keeps the combined three-lens instruction.
        /// </summary>
        /// <param name="primaryLens">The Judge's assigned primary lens, or null for the combined form.</param>
        /// <returns>Judge guidance text.</returns>
        internal static string BuildJudgeLensDirective(string? primaryLens)
        {
            if (String.IsNullOrWhiteSpace(primaryLens))
            {
                return JudgeLensAndBoundedRule;
            }

            return " Your PRIMARY lens for this review is " + primaryLens.Trim() +
                " -- lead with it and weigh your verdict toward it. Run the other two lenses as secondary" +
                " passes so nothing falls between the pool. BOUNDED-JUDGE RULE: to BLOCK (FAIL or" +
                " NEEDS_REVISION) you must exhibit a REAL, corpus-present affected case -- concrete inputs" +
                " or state where the defect actually manifests. A hypothetical pattern that cannot occur" +
                " in the actual code/corpus is a tracked follow-up note in your review, NOT a blocker.";
        }

        internal static string GetPersonaOutputContract(string? persona)
        {
            return GetPersonaOutputContract(persona, MissionModeEnum.Implementation, null);
        }

        /// <summary>
        /// Resolve the persona output contract for a mission mode. In Audit and Research modes the
        /// deliverable is a report, so a producing persona must not be told to make changes: the
        /// Worker contract would otherwise instruct a read-only captain to "make the requested
        /// changes", directly contradicting its own brief.
        /// </summary>
        /// <param name="persona">Mission persona.</param>
        /// <param name="mode">Mission mode.</param>
        /// <returns>The output contract text.</returns>
        internal static string GetPersonaOutputContract(string? persona, MissionModeEnum mode)
        {
            return GetPersonaOutputContract(persona, mode, null);
        }

        /// <summary>
        /// Resolve the persona output contract for a mission mode, with an optional distinct primary
        /// review lens for a Judge mission that runs alongside other Judges (perspective-diverse pool).
        /// </summary>
        /// <param name="persona">Mission persona.</param>
        /// <param name="mode">Mission mode.</param>
        /// <param name="judgePrimaryLens">Assigned primary Judge lens, or null for the combined form.</param>
        /// <returns>The output contract text.</returns>
        internal static string GetPersonaOutputContract(string? persona, MissionModeEnum mode, string? judgePrimaryLens)
        {
            if (mode == MissionModeEnum.Audit || mode == MissionModeEnum.Research)
            {
                string normalizedForMode = PersonaCatalog.NormalizeName(persona);

                // Reviewer personas already have report-shaped contracts that do not ask for changes,
                // so they are left alone. Only the producing personas need the read-only wording.
                if (normalizedForMode == PersonaCatalog.Worker ||
                    normalizedForMode == PersonaCatalog.TestEngineer)
                {
                    return
                        "This is a " + mode + " mission: your deliverable is a report, not a code change. " +
                        "Do not edit, commit, or push. Report exact evidence for every claim, and state plainly " +
                        "when the evidence does not settle a question. End with a standalone line " +
                        "`[ARMADA:RESULT] COMPLETE` followed by a brief plain-text summary of what you found.";
                }
            }

            return PersonaCatalog.NormalizeName(persona) switch
            {
                PersonaCatalog.Architect =>
                    "Respond only with real [ARMADA:MISSION] blocks. Do not emit [ARMADA:RESULT] or [ARMADA:VERDICT] lines.",
                PersonaCatalog.ProductManager =>
                    "Before your result line, include `## Product Vision`, `## Use Cases`, `## Experience Requirements`, `## Validation`, and `## Future Readiness` sections. End with a standalone line `[ARMADA:RESULT] COMPLETE` followed by a brief plain-text summary.",
                PersonaCatalog.UsabilityEngineer =>
                    "Before your result line, include `## Usability`, `## Consistency`, `## Edge Cases`, and `## Residual Risks` sections. End with a standalone line `[ARMADA:RESULT] COMPLETE` followed by a brief plain-text summary.",
                PersonaCatalog.Worker =>
                    "Stay within scope, make the requested changes, and end with a standalone line `[ARMADA:RESULT] COMPLETE` followed by a brief plain-text summary.",
                PersonaCatalog.TestEngineer =>
                    "Before your result line, include short `## Coverage Added`, `## Negative Paths`, and `## Residual Risks` sections. End with a standalone line `[ARMADA:RESULT] COMPLETE` followed by a brief plain-text summary.",
                PersonaCatalog.Judge =>
                    "Your response must contain these exact section headings: `## Completeness`, `## Correctness`, `## Tests`, `## Failure Modes`, and `## Verdict`. Do not reply with only a verdict line or brief summary. Run the test suite in the FOREGROUND and wait for it to finish before reaching a verdict -- never launch tests as a background task and schedule a wakeup, and never terminate before the verdict is emitted. Emit your verdict synchronously: the very last thing you do must be to print exactly one standalone line `[ARMADA:VERDICT] PASS`, `[ARMADA:VERDICT] FAIL`, or `[ARMADA:VERDICT] NEEDS_REVISION`. Before you exit, verify that your response already contains the standalone verdict line. If it does not, emit it immediately. A review without that line is discarded and re-run." + BuildJudgeLensDirective(judgePrimaryLens),
                _ => String.Empty
            };
        }

        private static string GetPersonaPromptFallback(string? persona)
        {
            return PersonaCatalog.NormalizeName(persona) switch
            {
                PersonaCatalog.Architect => "You are an Armada architect agent. Analyze the codebase and decompose the objective into right-sized missions using [ARMADA:MISSION] markers. Do not emit [ARMADA:RESULT] or [ARMADA:VERDICT] lines.",
                PersonaCatalog.ProductManager => "You are an Armada product manager agent. Clarify the whole product picture, define user value and experience requirements, include `## Product Vision`, `## Use Cases`, `## Experience Requirements`, `## Validation`, and `## Future Readiness` sections, and end with a standalone [ARMADA:RESULT] COMPLETE line.",
                PersonaCatalog.UsabilityEngineer => "You are an Armada usability engineer agent. Improve the work through the lens of usability, consistency, and edge-case handling, include `## Usability`, `## Consistency`, `## Edge Cases`, and `## Residual Risks` sections, and end with a standalone [ARMADA:RESULT] COMPLETE line.",
                PersonaCatalog.Worker => "You are an Armada worker agent. Implement the requested code changes carefully, stay within scope, and end with a standalone [ARMADA:RESULT] COMPLETE line.",
                PersonaCatalog.TestEngineer => "You are an Armada test engineer agent. Write tests for the current mission scope, cover negative and edge paths for validation, timeout, cancellation, retry, cleanup, and error-handling changes when applicable, include `## Coverage Added`, `## Negative Paths`, and `## Residual Risks` sections, and end with a standalone [ARMADA:RESULT] COMPLETE line.",
                PersonaCatalog.Judge => "You are an Armada judge agent. Review the completed work for completeness, correctness, test adequacy, and failure modes. Assume there may be a hidden bug. Use `## Completeness`, `## Correctness`, `## Tests`, `## Failure Modes`, and `## Verdict` sections, and end with exactly one standalone [ARMADA:VERDICT] PASS, [ARMADA:VERDICT] FAIL, or [ARMADA:VERDICT] NEEDS_REVISION line. Emit that verdict line before any completion signal or exit; a review without it is discarded and re-run." + JudgeLensAndBoundedRule,
                _ => "You are an Armada captain executing a mission. Follow these instructions carefully."
            };
        }
    }
}
