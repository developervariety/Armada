namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>Tests for AutonomousObjectiveSelector: eligibility gating, ordering, and chain unblocking.</summary>
    public class AutonomousObjectiveSelectorTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "AutonomousObjectiveSelector";

        private static Objective MakeObjective(
            string id,
            ObjectiveStatusEnum status = ObjectiveStatusEnum.Scoped,
            bool autoDispatch = true,
            ObjectivePriorityEnum priority = ObjectivePriorityEnum.P2,
            int rank = 0,
            List<string>? blockedBy = null,
            List<string>? voyageIds = null)
        {
            Objective obj = new Objective();
            // Override the generated Id via the property setter.
            obj.Id = id;
            obj.Title = "Test " + id;
            obj.Status = status;
            obj.AutoDispatchEnabled = autoDispatch;
            obj.Priority = priority;
            obj.Rank = rank;
            if (blockedBy != null) obj.BlockedByObjectiveIds = blockedBy;
            if (voyageIds != null) obj.VoyageIds = voyageIds;
            return obj;
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            // --- Eligibility gating ---

            await RunTest("SelectEligible_EmptyInput_ReturnsEmpty", () =>
            {
                List<Objective> result = AutonomousObjectiveSelector.SelectEligible(new List<Objective>());
                AssertEqual(0, result.Count, "Empty input should yield empty output");
                return Task.CompletedTask;
            });

            await RunTest("SelectEligible_NotOptedIn_Excluded", () =>
            {
                Objective obj = MakeObjective("obj-1", autoDispatch: false);
                List<Objective> result = AutonomousObjectiveSelector.SelectEligible(new List<Objective> { obj });
                AssertEqual(0, result.Count, "Non-opted-in objective must be excluded");
                return Task.CompletedTask;
            });

            await RunTest("SelectEligible_DraftStatus_Excluded", () =>
            {
                Objective obj = MakeObjective("obj-2", status: ObjectiveStatusEnum.Draft);
                List<Objective> result = AutonomousObjectiveSelector.SelectEligible(new List<Objective> { obj });
                AssertEqual(0, result.Count, "Draft status must be excluded");
                return Task.CompletedTask;
            });

            await RunTest("SelectEligible_InProgressStatus_Excluded", () =>
            {
                Objective obj = MakeObjective("obj-3", status: ObjectiveStatusEnum.InProgress);
                List<Objective> result = AutonomousObjectiveSelector.SelectEligible(new List<Objective> { obj });
                AssertEqual(0, result.Count, "InProgress status must be excluded");
                return Task.CompletedTask;
            });

            await RunTest("SelectEligible_CompletedStatus_Excluded", () =>
            {
                Objective obj = MakeObjective("obj-4", status: ObjectiveStatusEnum.Completed);
                List<Objective> result = AutonomousObjectiveSelector.SelectEligible(new List<Objective> { obj });
                AssertEqual(0, result.Count, "Completed status must be excluded");
                return Task.CompletedTask;
            });

            await RunTest("SelectEligible_BlockedStatus_Excluded", () =>
            {
                Objective obj = MakeObjective("obj-5", status: ObjectiveStatusEnum.Blocked);
                List<Objective> result = AutonomousObjectiveSelector.SelectEligible(new List<Objective> { obj });
                AssertEqual(0, result.Count, "Blocked status must be excluded");
                return Task.CompletedTask;
            });

            await RunTest("SelectEligible_CancelledStatus_Excluded", () =>
            {
                Objective obj = MakeObjective("obj-6", status: ObjectiveStatusEnum.Cancelled);
                List<Objective> result = AutonomousObjectiveSelector.SelectEligible(new List<Objective> { obj });
                AssertEqual(0, result.Count, "Cancelled status must be excluded");
                return Task.CompletedTask;
            });

            await RunTest("SelectEligible_ScopedStatus_Included", () =>
            {
                Objective obj = MakeObjective("obj-7", status: ObjectiveStatusEnum.Scoped);
                List<Objective> result = AutonomousObjectiveSelector.SelectEligible(new List<Objective> { obj });
                AssertEqual(1, result.Count, "Scoped status must be included");
                return Task.CompletedTask;
            });

            await RunTest("SelectEligible_PlannedStatus_Included", () =>
            {
                Objective obj = MakeObjective("obj-8", status: ObjectiveStatusEnum.Planned);
                List<Objective> result = AutonomousObjectiveSelector.SelectEligible(new List<Objective> { obj });
                AssertEqual(1, result.Count, "Planned status must be included");
                return Task.CompletedTask;
            });

            await RunTest("SelectEligible_MissingBlockerId_TreatedAsBlocked", () =>
            {
                Objective obj = MakeObjective("obj-9", blockedBy: new List<string> { "nonexistent-id" });
                List<Objective> result = AutonomousObjectiveSelector.SelectEligible(new List<Objective> { obj });
                AssertEqual(0, result.Count, "Missing blocker id must make objective ineligible");
                return Task.CompletedTask;
            });

            await RunTest("SelectEligible_BlockerNotCompleted_Excluded", () =>
            {
                // blocker is InProgress (not Scoped/Planned so not eligible itself),
                // and blocked cannot pass because its blocker is not Completed.
                Objective blocker = MakeObjective("blocker-1", status: ObjectiveStatusEnum.InProgress);
                Objective blocked = MakeObjective("blocked-1", blockedBy: new List<string> { "blocker-1" });
                List<Objective> result = AutonomousObjectiveSelector.SelectEligible(
                    new List<Objective> { blocker, blocked });
                AssertEqual(0, result.Count, "Both should be excluded: blocker is InProgress, blocked is blocked by non-Completed blocker");
                return Task.CompletedTask;
            });

            await RunTest("SelectEligible_BlockerCompleted_Unblocked", () =>
            {
                Objective blocker = MakeObjective("blocker-2", status: ObjectiveStatusEnum.Completed);
                Objective unlocked = MakeObjective("unlocked-1", blockedBy: new List<string> { "blocker-2" });
                List<Objective> result = AutonomousObjectiveSelector.SelectEligible(
                    new List<Objective> { blocker, unlocked });
                AssertEqual(1, result.Count, "Objective with completed blocker should be eligible");
                AssertEqual("unlocked-1", result[0].Id, "Unlocked objective should be in results");
                return Task.CompletedTask;
            });

            // --- Chain A->B->C tests ---

            await RunTest("SelectEligible_Chain_OnlyFirstLinkEligible", () =>
            {
                Objective objA = MakeObjective("chain-a");
                Objective objB = MakeObjective("chain-b", blockedBy: new List<string> { "chain-a" });
                Objective objC = MakeObjective("chain-c", blockedBy: new List<string> { "chain-b" });

                List<Objective> result = AutonomousObjectiveSelector.SelectEligible(
                    new List<Objective> { objA, objB, objC });

                AssertEqual(1, result.Count, "Only chain head A should be eligible");
                AssertEqual("chain-a", result[0].Id, "Chain head should be chain-a");
                return Task.CompletedTask;
            });

            await RunTest("SelectEligible_Chain_AfterACompleted_BEligible", () =>
            {
                Objective objA = MakeObjective("chain2-a", status: ObjectiveStatusEnum.Completed);
                Objective objB = MakeObjective("chain2-b", blockedBy: new List<string> { "chain2-a" });
                Objective objC = MakeObjective("chain2-c", blockedBy: new List<string> { "chain2-b" });

                List<Objective> result = AutonomousObjectiveSelector.SelectEligible(
                    new List<Objective> { objA, objB, objC });

                AssertEqual(1, result.Count, "Only B should be eligible after A completes");
                AssertEqual("chain2-b", result[0].Id, "chain2-b should be the eligible link");
                return Task.CompletedTask;
            });

            await RunTest("SelectEligible_Chain_AfterBCompleted_CEligible", () =>
            {
                Objective objA = MakeObjective("chain3-a", status: ObjectiveStatusEnum.Completed);
                Objective objB = MakeObjective("chain3-b", status: ObjectiveStatusEnum.Completed,
                    blockedBy: new List<string> { "chain3-a" });
                Objective objC = MakeObjective("chain3-c", blockedBy: new List<string> { "chain3-b" });

                List<Objective> result = AutonomousObjectiveSelector.SelectEligible(
                    new List<Objective> { objA, objB, objC });

                AssertEqual(1, result.Count, "Only C should be eligible after A and B complete");
                AssertEqual("chain3-c", result[0].Id, "chain3-c should be the eligible link");
                return Task.CompletedTask;
            });

            // --- Ordering tests ---

            await RunTest("SelectEligible_Ordering_P0BeforeP3", () =>
            {
                Objective p3 = MakeObjective("ord-p3", priority: ObjectivePriorityEnum.P3, rank: 0);
                Objective p0 = MakeObjective("ord-p0", priority: ObjectivePriorityEnum.P0, rank: 0);
                Objective p1 = MakeObjective("ord-p1", priority: ObjectivePriorityEnum.P1, rank: 0);

                List<Objective> result = AutonomousObjectiveSelector.SelectEligible(
                    new List<Objective> { p3, p0, p1 });

                AssertEqual(3, result.Count, "All three should be eligible");
                AssertEqual("ord-p0", result[0].Id, "P0 should be first");
                AssertEqual("ord-p1", result[1].Id, "P1 should be second");
                AssertEqual("ord-p3", result[2].Id, "P3 should be last");
                return Task.CompletedTask;
            });

            await RunTest("SelectEligible_Ordering_EqualPriorityOrdersByRank", () =>
            {
                Objective r5 = MakeObjective("ord-r5", priority: ObjectivePriorityEnum.P1, rank: 5);
                Objective r1 = MakeObjective("ord-r1", priority: ObjectivePriorityEnum.P1, rank: 1);
                Objective r3 = MakeObjective("ord-r3", priority: ObjectivePriorityEnum.P1, rank: 3);

                List<Objective> result = AutonomousObjectiveSelector.SelectEligible(
                    new List<Objective> { r5, r1, r3 });

                AssertEqual(3, result.Count, "All three should be eligible");
                AssertEqual("ord-r1", result[0].Id, "Rank 1 should be first");
                AssertEqual("ord-r3", result[1].Id, "Rank 3 should be second");
                AssertEqual("ord-r5", result[2].Id, "Rank 5 should be last");
                return Task.CompletedTask;
            });

            await RunTest("SelectEligible_Ordering_EqualPriorityAndRankOrdersById", () =>
            {
                Objective objZ = MakeObjective("obj-z", priority: ObjectivePriorityEnum.P0, rank: 0);
                Objective objA = MakeObjective("obj-a", priority: ObjectivePriorityEnum.P0, rank: 0);
                Objective objM = MakeObjective("obj-m", priority: ObjectivePriorityEnum.P0, rank: 0);

                List<Objective> result = AutonomousObjectiveSelector.SelectEligible(
                    new List<Objective> { objZ, objA, objM });

                AssertEqual(3, result.Count, "All three should be eligible");
                AssertEqual("obj-a", result[0].Id, "Ordinal-first Id should be first");
                AssertEqual("obj-m", result[1].Id, "Middle Id should be second");
                AssertEqual("obj-z", result[2].Id, "Ordinal-last Id should be last");
                return Task.CompletedTask;
            });

            await RunTest("SelectEligible_Ordering_MixedPriorityAndRank_FullOrder", () =>
            {
                // P0/rank1, P0/rank0, P1/rank0, P2/rank2, P2/rank1
                Objective p0r1 = MakeObjective("p0r1", priority: ObjectivePriorityEnum.P0, rank: 1);
                Objective p0r0 = MakeObjective("p0r0", priority: ObjectivePriorityEnum.P0, rank: 0);
                Objective p1r0 = MakeObjective("p1r0", priority: ObjectivePriorityEnum.P1, rank: 0);
                Objective p2r2 = MakeObjective("p2r2", priority: ObjectivePriorityEnum.P2, rank: 2);
                Objective p2r1 = MakeObjective("p2r1", priority: ObjectivePriorityEnum.P2, rank: 1);

                List<Objective> result = AutonomousObjectiveSelector.SelectEligible(
                    new List<Objective> { p0r1, p2r2, p1r0, p0r0, p2r1 });

                AssertEqual(5, result.Count, "All five should be eligible");
                AssertEqual("p0r0", result[0].Id, "P0/rank0 first");
                AssertEqual("p0r1", result[1].Id, "P0/rank1 second");
                AssertEqual("p1r0", result[2].Id, "P1/rank0 third");
                AssertEqual("p2r1", result[3].Id, "P2/rank1 fourth");
                AssertEqual("p2r2", result[4].Id, "P2/rank2 fifth");
                return Task.CompletedTask;
            });
        }
    }
}
