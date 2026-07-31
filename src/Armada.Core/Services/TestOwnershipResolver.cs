namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Resolves who owns tests for a mission, and turns that into the directive injected into the
    /// captain brief.
    ///
    /// The rule this exists to enforce: never defer work to a pipeline stage that the resolved
    /// pipeline does not contain. Prompt text naming specific pipelines cannot know which stages were
    /// actually created, so a single-stage dispatch left nobody owning tests while the Judge still
    /// required them. Ownership is a fact about the dispatch, so it is resolved from the dispatch.
    /// </summary>
    public static class TestOwnershipResolver
    {
        #region Public-Methods

        /// <summary>
        /// Resolves ownership from the sibling missions a voyage actually created.
        /// </summary>
        /// <param name="mission">Mission whose ownership is being resolved.</param>
        /// <param name="voyageMissions">All missions in the same voyage, or null when unknown.</param>
        /// <returns>The resolved ownership.</returns>
        public static TestOwnershipEnum Resolve(Mission mission, IReadOnlyList<Mission>? voyageMissions)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));

            // PersonaCatalog.Matches, not string equality: seeded pipelines carry the legacy unspaced
            // "TestEngineer" while the canonical constant is "Test Engineer".
            if (PersonaCatalog.Matches(mission.Persona, PersonaCatalog.TestEngineer)) return TestOwnershipEnum.TestEngineerIsMe;

            if (voyageMissions == null || voyageMissions.Count == 0) return TestOwnershipEnum.Unknown;

            List<Mission> testStages = new List<Mission>();
            foreach (Mission candidate in voyageMissions)
            {
                if (PersonaCatalog.Matches(candidate.Persona, PersonaCatalog.TestEngineer)) testStages.Add(candidate);
            }

            if (testStages.Count == 0) return TestOwnershipEnum.SoleTestOwner;

            if (mission.StageOrder.HasValue)
            {
                foreach (Mission stage in testStages)
                {
                    if (stage.StageOrder.HasValue && stage.StageOrder.Value > mission.StageOrder.Value)
                        return TestOwnershipEnum.TestEngineerFollows;
                }

                foreach (Mission stage in testStages)
                {
                    if (stage.StageOrder.HasValue && stage.StageOrder.Value < mission.StageOrder.Value)
                        return TestOwnershipEnum.TestEngineerPreceded;
                }
            }

            // Stage order is not always stamped. A test stage that depends on this mission runs after
            // it; anything else in the voyage is treated as following, which is the safe reading for a
            // producing persona because it never tells a captain that tests are somebody else's job.
            foreach (Mission stage in testStages)
            {
                if (!String.IsNullOrEmpty(stage.DependsOnMissionId) &&
                    String.Equals(stage.DependsOnMissionId, mission.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return TestOwnershipEnum.TestEngineerFollows;
                }
            }

            return TestOwnershipEnum.TestEngineerFollows;
        }

        /// <summary>
        /// Resolves ownership from a pipeline definition, for the path where no voyage siblings exist.
        /// </summary>
        /// <param name="mission">Mission whose ownership is being resolved.</param>
        /// <param name="pipeline">Resolved pipeline, or null when unknown.</param>
        /// <returns>The resolved ownership.</returns>
        public static TestOwnershipEnum Resolve(Mission mission, Pipeline? pipeline)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));

            if (PersonaCatalog.Matches(mission.Persona, PersonaCatalog.TestEngineer)) return TestOwnershipEnum.TestEngineerIsMe;

            if (pipeline == null || pipeline.Stages == null || pipeline.Stages.Count == 0) return TestOwnershipEnum.SoleTestOwner;

            List<PipelineStage> testStages = new List<PipelineStage>();
            foreach (PipelineStage stage in pipeline.Stages)
            {
                if (PersonaCatalog.Matches(stage.PersonaName, PersonaCatalog.TestEngineer)) testStages.Add(stage);
            }

            if (testStages.Count == 0) return TestOwnershipEnum.SoleTestOwner;

            if (mission.StageOrder.HasValue)
            {
                foreach (PipelineStage stage in testStages)
                {
                    if (stage.Order > mission.StageOrder.Value) return TestOwnershipEnum.TestEngineerFollows;
                }

                foreach (PipelineStage stage in testStages)
                {
                    if (stage.Order < mission.StageOrder.Value) return TestOwnershipEnum.TestEngineerPreceded;
                }
            }

            return TestOwnershipEnum.TestEngineerFollows;
        }

        /// <summary>
        /// Builds the directive injected into the brief as the TestOwnership template value. Returns an
        /// empty string for personas that neither produce code nor judge it, so no brief carries a
        /// sentence about a role it does not hold.
        /// </summary>
        /// <param name="persona">Mission persona.</param>
        /// <param name="ownership">Resolved ownership.</param>
        /// <returns>The directive text, or an empty string.</returns>
        public static string BuildDirective(string? persona, TestOwnershipEnum ownership)
        {
            // A mission dispatched without an explicit persona still renders the worker template, the
            // same default GetPersonaTemplateName applies. Treating an absent persona as "not a
            // producing role" would silently drop the directive from exactly those missions.
            string normalized = String.IsNullOrWhiteSpace(persona)
                ? PersonaCatalog.Worker
                : PersonaCatalog.NormalizeName(persona);

            if (String.Equals(normalized, PersonaCatalog.Worker, StringComparison.Ordinal))
            {
                if (ownership == TestOwnershipEnum.SoleTestOwner || ownership == TestOwnershipEnum.Unknown)
                {
                    return "No Test Engineer stage runs for this mission. You own the tests for this change: " +
                        "add or update the tests that cover the behaviour you changed, including the negative paths " +
                        "for any validation, timeout, cancellation, retry, cleanup, or error-handling branch you " +
                        "touched, and run them before you commit.";
                }

                return "A Test Engineer stage runs after you. Write the tests that belong with your change and run " +
                    "them; the Test Engineer stage adds the broader coverage. Do not skip testing on the assumption " +
                    "that a later stage will do all of it.";
            }

            if (String.Equals(normalized, PersonaCatalog.TestEngineer, StringComparison.Ordinal))
            {
                return "A Worker stage ran before you and may already have added tests. Read the diff first: your job " +
                    "is gap coverage, not first coverage. Do not duplicate a test that already exists. You do not " +
                    "modify production code -- commit test files only. When you find a defect in the production code, " +
                    "do not fix it: describe it under `## Residual Risks` with the exact inputs or state that " +
                    "reproduce it, so the following stage routes the fix back to a Worker. When no stage follows you, " +
                    "that section is the escalation record.";
            }

            if (String.Equals(normalized, PersonaCatalog.Judge, StringComparison.Ordinal))
            {
                if (ownership == TestOwnershipEnum.SoleTestOwner || ownership == TestOwnershipEnum.Unknown)
                {
                    return "No Test Engineer stage ran for this mission. Judge test adequacy against what the Worker " +
                        "delivered, and do not withhold a PASS for the absence of a separate test stage. You may run " +
                        "focused smoke verification to confirm the change behaves as described, but you do not write " +
                        "tests or modify production code.";
                }

                return "A Test Engineer stage ran before you. Review its output as part of your assessment and judge " +
                    "whether the delivered coverage matches the changed behaviour. You do not write tests or modify " +
                    "production code.";
            }

            return String.Empty;
        }

        #endregion
    }
}
