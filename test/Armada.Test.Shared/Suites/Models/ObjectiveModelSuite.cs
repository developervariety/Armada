namespace Armada.Test.Shared.Suites.Models
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Armada.Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="Objective"/>: backlog defaults, Id/Title trimming and
    /// whitespace rejection, and JSON round-tripping of backlog metadata. Ported from the
    /// retired unit suite plus added explicit null-rejection negatives.
    /// </summary>
    public sealed class ObjectiveModelSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Objective model suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("default_constructor_generates_id_with_prefix_and_backlog_defaults", "Objective default constructor generates id with prefix and backlog defaults", TestTags.Positive, () =>
            {
                Objective objective = new Objective();

                AssertStartsWith(Constants.ObjectiveIdPrefix, objective.Id);
                AssertEqual("Objective", objective.Title);
                AssertEqual(ObjectiveStatusEnum.Draft, objective.Status);
                AssertEqual(ObjectiveKindEnum.Feature, objective.Kind);
                AssertEqual(ObjectivePriorityEnum.P2, objective.Priority);
                AssertEqual(0, objective.Rank);
                AssertEqual(ObjectiveBacklogStateEnum.Inbox, objective.BacklogState);
                AssertEqual(ObjectiveEffortEnum.M, objective.Effort);
                AssertEqual(0, objective.BlockedByObjectiveIds.Count);
                AssertEqual(0, objective.RefinementSessionIds.Count);
                AssertEqual(0, objective.AcceptanceCriteria.Count);
            }));

            cases.Add(Case("id_and_title_trim_whitespace", "Objective id and title trim whitespace", TestTags.Positive, () =>
            {
                Objective objective = new Objective
                {
                    Id = "  obj_trimmed  ",
                    Title = "  Backlog item title  "
                };

                AssertEqual("obj_trimmed", objective.Id);
                AssertEqual("Backlog item title", objective.Title);
            }));

            cases.Add(Case("id_and_title_reject_whitespace", "Objective id and title reject whitespace", TestTags.Negative, () =>
            {
                Objective objective = new Objective();
                AssertThrows<ArgumentNullException>(() => objective.Id = "   ");
                AssertThrows<ArgumentNullException>(() => objective.Title = "\t");
            }));

            // Added audit coverage: the setters also reject null, which the legacy suite only exercised with whitespace.
            cases.Add(Case("id_rejects_null", "Objective id rejects null", TestTags.Negative, () =>
            {
                Objective objective = new Objective();
                AssertThrows<ArgumentNullException>(() => objective.Id = null!);
            }));

            cases.Add(Case("title_rejects_null", "Objective title rejects null", TestTags.Negative, () =>
            {
                Objective objective = new Objective();
                AssertThrows<ArgumentNullException>(() => objective.Title = null!);
            }));

            cases.Add(Case("serialization_round_trips_backlog_fields", "Objective serialization round trips backlog fields", TestTags.Positive, () =>
            {
                Objective objective = new Objective
                {
                    Id = "obj_roundtrip",
                    Title = "Backlog roundtrip",
                    Description = "Roundtrip backlog metadata.",
                    Status = ObjectiveStatusEnum.Scoped,
                    Kind = ObjectiveKindEnum.Bug,
                    Category = "API",
                    Priority = ObjectivePriorityEnum.P1,
                    Rank = 7,
                    BacklogState = ObjectiveBacklogStateEnum.ReadyForPlanning,
                    Effort = ObjectiveEffortEnum.L,
                    Owner = "captain",
                    TargetVersion = "0.8.0",
                    ParentObjectiveId = "obj_parent",
                    RefinementSummary = "Summarized by captain.",
                    SuggestedPipelineId = "pipe_default",
                    SourceProvider = "github",
                    SourceType = "issue",
                    SourceId = "owner/repo#123",
                    SourceUrl = "https://example.test/issues/123"
                };
                objective.BlockedByObjectiveIds.Add("obj_blocker");
                objective.RefinementSessionIds.Add("ors_123");
                objective.AcceptanceCriteria.Add("Ship backlog detail");
                objective.NonGoals.Add("No workflow changes");
                objective.RolloutConstraints.Add("Needs staged rollout");
                objective.EvidenceLinks.Add("https://example.test/spec");
                objective.VesselIds.Add("ves_123");
                objective.ReleaseIds.Add("rel_123");

                string json = JsonSerializer.Serialize(objective);
                Objective deserialized = JsonSerializer.Deserialize<Objective>(json)!;

                AssertEqual(objective.Id, deserialized.Id);
                AssertEqual(objective.Title, deserialized.Title);
                AssertEqual(objective.Kind, deserialized.Kind);
                AssertEqual(objective.Priority, deserialized.Priority);
                AssertEqual(objective.Rank, deserialized.Rank);
                AssertEqual(objective.BacklogState, deserialized.BacklogState);
                AssertEqual(objective.TargetVersion, deserialized.TargetVersion);
                AssertEqual(objective.ParentObjectiveId, deserialized.ParentObjectiveId);
                AssertEqual(objective.RefinementSummary, deserialized.RefinementSummary);
                AssertEqual(objective.SuggestedPipelineId, deserialized.SuggestedPipelineId);
                AssertEqual(objective.BlockedByObjectiveIds[0], deserialized.BlockedByObjectiveIds[0]);
                AssertEqual(objective.RefinementSessionIds[0], deserialized.RefinementSessionIds[0]);
                AssertEqual(objective.AcceptanceCriteria[0], deserialized.AcceptanceCriteria[0]);
                AssertEqual(objective.NonGoals[0], deserialized.NonGoals[0]);
                AssertEqual(objective.RolloutConstraints[0], deserialized.RolloutConstraints[0]);
                AssertEqual(objective.EvidenceLinks[0], deserialized.EvidenceLinks[0]);
                AssertEqual(objective.VesselIds[0], deserialized.VesselIds[0]);
                AssertEqual(objective.ReleaseIds[0], deserialized.ReleaseIds[0]);
            }));

            return new TestSuiteDescriptor(
                suiteId: "Models.ObjectiveModel",
                displayName: "Objective Model",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Models.ObjectiveModel",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) =>
                {
                    body();
                    return Task.CompletedTask;
                },
                tags: new List<string> { tag });
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "Models.ObjectiveModel",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
