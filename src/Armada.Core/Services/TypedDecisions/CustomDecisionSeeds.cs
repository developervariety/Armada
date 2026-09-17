namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;
    using Armada.Core.Settings;

    /// <summary>
    /// Built-in example custom decisions an operator can install and then edit. They are the
    /// registry-friendly members of the repo-specific decision queue (see
    /// <c>AI-Memory/typed-decision-eval/PROPOSED_DECISIONS.md</c>): source fidelity, safety-step
    /// removal, and citation resolution. They ship Off and unbound, so installing them changes
    /// nothing until the operator turns them on. They are not auto-installed, so deleting one does
    /// not resurrect it.
    /// </summary>
    public static class CustomDecisionSeeds
    {
        #region Public-Methods

        /// <summary>The seed definitions, keyed by name.</summary>
        /// <returns>A fresh map each call.</returns>
        public static Dictionary<string, CustomTypedDecisionSettings> Build()
        {
            Dictionary<string, CustomTypedDecisionSettings> seeds = new Dictionary<string, CustomTypedDecisionSettings>(StringComparer.Ordinal);

            seeds["source_fidelity"] = new CustomTypedDecisionSettings
            {
                Description = "A port reproduces its cited deobfuscated source, not a silent improvement or an invented construct.",
                Surface = CustomDecisionSurfaceEnum.MissionDiff,
                Binding = CustomDecisionSeamEnum.MissionDiffFlag,
                Mode = TypedDecisionModeEnum.Off,
                GateThreshold = 0.9,
                StateFields = new List<string> { "title", "diff", "output_tail" },
                Questions = new List<CustomTypedQuestionSettings>
                {
                    Noul("reproduces_source", "The port matches the cited source form byte-for-byte (truncation, printable-byte filter, bit math), rather than 'fixing' it.", "matches the source form", "diverges from the source form"),
                    Noul("silent_improvement", "The port corrects a defect present in the source (an always-zero shift, a wrong mask) instead of reproducing it and pinning it.", "silently improves the source", "reproduces the source defect"),
                    Noul("invents_behaviour", "The port adds a construct the cited source does not contain.", "invents behaviour", "adds nothing beyond the source")
                }
            };

            seeds["safety_step_present"] = new CustomTypedDecisionSettings
            {
                Description = "A change keeps the source's terminating safety step (RF disable, session exit, injector restore, fail-closed return).",
                Surface = CustomDecisionSurfaceEnum.MissionDiff,
                Binding = CustomDecisionSeamEnum.MissionDiffFlag,
                Mode = TypedDecisionModeEnum.Off,
                GateThreshold = 0.9,
                StateFields = new List<string> { "title", "diff", "output_tail" },
                Questions = new List<CustomTypedQuestionSettings>
                {
                    Noul("removes_safety_step", "The change removes or fails to reproduce a terminating SAFETY step present in the source (an ECU disable, a session exit, an enable-all-injectors restore, a fail-closed return).", "removes a safety step", "keeps every safety step"),
                    Choice("step_kind", "Which terminating safety step, if any, is at risk.", new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["none"] = "no safety step is affected",
                        ["rf_disable"] = "an RF test-mode disable",
                        ["session_exit"] = "a diagnostic session exit",
                        ["injector_restore"] = "an enable-all-injectors or actuator restore",
                        ["fail_closed_return"] = "a fail-closed early return"
                    })
                }
            };

            seeds["citation_resolves"] = new CustomTypedDecisionSettings
            {
                Description = "A glossary or discovery citation points at a real source location and its claim matches the excerpt.",
                Surface = CustomDecisionSurfaceEnum.CaptainTool,
                Binding = CustomDecisionSeamEnum.None,
                Mode = TypedDecisionModeEnum.Off,
                GateThreshold = 0.9,
                StateFields = new List<string> { "citation", "excerpt", "claim" },
                Questions = new List<CustomTypedQuestionSettings>
                {
                    Noul("citation_resolves", "The cited path exists in the resolved tree and contains the claimed content.", "the citation resolves", "the citation points at nothing"),
                    Noul("claim_matches_excerpt", "The entry's claim is supported by the excerpt at the cited location.", "the claim matches the excerpt", "the claim is not in the excerpt")
                }
            };

            return seeds;
        }

        #endregion

        #region Private-Methods

        private static CustomTypedQuestionSettings Noul(string id, string instructions, string trueMeaning, string falseMeaning)
        {
            return new CustomTypedQuestionSettings { Id = id, Type = "noul", Instructions = instructions, TrueMeaning = trueMeaning, FalseMeaning = falseMeaning };
        }

        private static CustomTypedQuestionSettings Choice(string id, string instructions, Dictionary<string, string> options)
        {
            return new CustomTypedQuestionSettings { Id = id, Type = "choice", Instructions = instructions, Options = options };
        }

        #endregion
    }
}
