namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;
    using Armada.Core.Settings;

    /// <summary>
    /// Built-in EXAMPLE custom decisions an operator can install and then edit, to see the shape of a
    /// user-defined decision. They are deliberately generic software-engineering checks with no
    /// project- or domain-specific content — a deployment's own decisions are created in the
    /// dashboard or imported as configuration, never shipped in this source. The examples ship Off
    /// and unbound, so installing them changes nothing until an operator turns one on, and they are
    /// not auto-installed, so deleting one does not resurrect it.
    /// </summary>
    public static class CustomDecisionSeeds
    {
        #region Public-Methods

        /// <summary>The example definitions, keyed by name.</summary>
        /// <returns>A fresh map each call.</returns>
        public static Dictionary<string, CustomTypedDecisionSettings> Build()
        {
            Dictionary<string, CustomTypedDecisionSettings> seeds = new Dictionary<string, CustomTypedDecisionSettings>(StringComparer.Ordinal);

            seeds["reuse_before_duplicate"] = new CustomTypedDecisionSettings
            {
                Description = "A change reuses an existing capability through a seam rather than duplicating logic that already exists.",
                Surface = CustomDecisionSurfaceEnum.MissionDiff,
                Binding = CustomDecisionSeamEnum.MissionDiffFlag,
                Mode = TypedDecisionModeEnum.Off,
                GateThreshold = 0.9,
                StateFields = new List<string> { "title", "diff" },
                Questions = new List<CustomTypedQuestionSettings>
                {
                    Noul("duplicates_existing", "The change reimplements logic an existing type already provides, rather than reusing it.", "duplicates existing logic", "reuses or extends existing logic")
                }
            };

            seeds["test_covers_symptom"] = new CustomTypedDecisionSettings
            {
                Description = "An added test exercises the reported symptom and would fail before the change.",
                Surface = CustomDecisionSurfaceEnum.MissionDiff,
                Binding = CustomDecisionSeamEnum.MissionDiffFlag,
                Mode = TypedDecisionModeEnum.Off,
                GateThreshold = 0.9,
                StateFields = new List<string> { "title", "diff" },
                Questions = new List<CustomTypedQuestionSettings>
                {
                    Noul("covers_symptom", "An added test exercises the change's reported symptom, not an unrelated behaviour.", "covers the symptom", "does not cover the symptom"),
                    Noul("would_fail_before", "The added test would fail against the code before the change and pass after it.", "distinguishes the fix", "would pass even before the fix")
                }
            };

            seeds["docs_match_change"] = new CustomTypedDecisionSettings
            {
                Description = "A change updates the documentation it affects, rather than leaving it stale.",
                Surface = CustomDecisionSurfaceEnum.CaptainTool,
                Binding = CustomDecisionSeamEnum.None,
                Mode = TypedDecisionModeEnum.Off,
                GateThreshold = 0.9,
                StateFields = new List<string> { "diff", "docs" },
                Questions = new List<CustomTypedQuestionSettings>
                {
                    Noul("docs_stale", "The change alters behaviour a document describes without updating that document.", "leaves documentation stale", "keeps documentation in step")
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

        #endregion
    }
}
