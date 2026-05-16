namespace Armada.Test.Unit.Suites.Models
{
    using System.Text.Json;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;

    public class ObjectiveModelTests : TestSuite
    {
        public override string Name => "Objective Model";

        protected override async Task RunTestsAsync()
        {
            await RunTest("Objective DefaultConstructor GeneratesIdWithPrefixAndBacklogDefaults", () =>
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
            });

            await RunTest("Objective IdAndTitle TrimWhitespace", () =>
            {
                Objective objective = new Objective
                {
                    Id = "  obj_trimmed  ",
                    Title = "  Backlog item title  "
                };

                AssertEqual("obj_trimmed", objective.Id);
                AssertEqual("Backlog item title", objective.Title);
            });

            await RunTest("Objective IdAndTitle RejectWhitespace", () =>
            {
                Objective objective = new Objective();
                AssertThrows<ArgumentNullException>(() => objective.Id = "   ");
                AssertThrows<ArgumentNullException>(() => objective.Title = "\t");
            });

            await RunTest("Objective Serialization RoundTripsBacklogFields", () =>
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
            });
        }
    }
}
