namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Tests deterministic campaign rotation within objective priority bands.
    /// </summary>
    public class ObjectiveFairShareOrderTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Objective Fair-Share Order";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Priority bands remain strict", () =>
            {
                Objective p0RootA = Campaign("root-p0-a");
                Objective p0RootB = Campaign("root-p0-b");
                Objective p1Root = Campaign("root-p1");
                Objective p0A = Candidate("p0-a", p0RootA.Id, ObjectivePriorityEnum.P0, 20);
                Objective p0B = Candidate("p0-b", p0RootB.Id, ObjectivePriorityEnum.P0, 30);
                Objective p1 = Candidate("p1-first-rank", p1Root.Id, ObjectivePriorityEnum.P1, 0);

                List<Objective> ordered = Apply(
                    new List<Objective> { p1, p0B, p0A },
                    new List<Objective> { p0RootA, p0RootB, p1Root, p0A, p0B, p1 });

                AssertSequence(new[] { "p0-a", "p0-b", "p1-first-rank" }, ordered);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Campaign roots are served round robin", () =>
            {
                Objective rootA = Campaign("root-a");
                Objective rootB = Campaign("root-b");
                Objective a1 = Candidate("a-1", rootA.Id, rank: 1);
                Objective a2 = Candidate("a-2", rootA.Id, rank: 2);
                Objective b1 = Candidate("b-1", rootB.Id, rank: 1);
                Objective b2 = Candidate("b-2", rootB.Id, rank: 2);

                List<Objective> ordered = Apply(
                    new List<Objective> { b2, a2, b1, a1 },
                    new List<Objective> { rootA, rootB, a1, a2, b1, b2 });

                AssertSequence(new[] { "a-1", "b-1", "a-2", "b-2" }, ordered);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Rank then ID order is retained inside a campaign", () =>
            {
                Objective root = Campaign("root-ranked");
                Objective rankTwo = Candidate("rank-two", root.Id, rank: 2);
                Objective rankOneZ = Candidate("rank-one-z", root.Id, rank: 1);
                Objective rankOneA = Candidate("rank-one-a", root.Id, rank: 1);

                List<Objective> ordered = Apply(
                    new List<Objective> { rankTwo, rankOneZ, rankOneA },
                    new List<Objective> { root, rankTwo, rankOneZ, rankOneA });

                AssertSequence(new[] { "rank-one-a", "rank-one-z", "rank-two" }, ordered);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Cursor starts after the last served campaign", () =>
            {
                Objective rootA = Campaign("root-cursor-a");
                Objective rootB = Campaign("root-cursor-b");
                Objective rootC = Campaign("root-cursor-c");
                Objective a = Candidate("cursor-a", rootA.Id, rank: 0);
                Objective b = Candidate("cursor-b", rootB.Id, rank: 0);
                Objective c = Candidate("cursor-c", rootC.Id, rank: 0);
                Dictionary<ObjectivePriorityEnum, string> cursor = new Dictionary<ObjectivePriorityEnum, string>
                {
                    [ObjectivePriorityEnum.P2] = "campaign:" + rootA.Id
                };

                List<Objective> ordered = Apply(
                    new List<Objective> { c, a, b },
                    new List<Objective> { rootA, rootB, rootC, a, b, c },
                    cursor);

                AssertSequence(new[] { "cursor-b", "cursor-c", "cursor-a" }, ordered);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Plain objectives retain rank then ID order", () =>
            {
                Objective rankThree = Candidate("plain-rank-three", null, rank: 3);
                Objective rankOneZ = Candidate("plain-rank-one-z", null, rank: 1);
                Objective rankOneA = Candidate("plain-rank-one-a", null, rank: 1);

                List<Objective> ordered = Apply(
                    new List<Objective> { rankThree, rankOneZ, rankOneA },
                    new List<Objective> { rankThree, rankOneZ, rankOneA });

                AssertSequence(new[] { "plain-rank-one-a", "plain-rank-one-z", "plain-rank-three" }, ordered);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("A descendant parent cycle terminates as plain work", () =>
            {
                Objective cycleA = Candidate("cycle-a", "cycle-b", rank: 10);
                Objective cycleB = Candidate("cycle-b", "cycle-a", rank: 11);
                Objective descendant = Candidate("cycle-descendant", cycleA.Id, rank: 1);
                Dictionary<string, string> campaignByObjectiveId;

                List<Objective> ordered = ObjectiveFairShareOrder.Apply(
                    new List<Objective> { descendant },
                    new List<Objective> { cycleA, cycleB, descendant },
                    new Dictionary<ObjectivePriorityEnum, string>(),
                    out campaignByObjectiveId);

                AssertEqual(1, ordered.Count);
                AssertEqual(descendant.Id, ordered[0].Id);
                AssertEqual("plain", campaignByObjectiveId[descendant.Id],
                    "A malformed parent cycle must terminate and must not invent a campaign root.");
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }

        private static Objective Campaign(string id)
        {
            return new Objective
            {
                Id = id,
                Title = id,
                Tags = new List<string> { "campaign:test" }
            };
        }

        private static Objective Candidate(
            string id,
            string? parentObjectiveId,
            ObjectivePriorityEnum priority = ObjectivePriorityEnum.P2,
            int rank = 0)
        {
            return new Objective
            {
                Id = id,
                Title = id,
                ParentObjectiveId = parentObjectiveId,
                Priority = priority,
                Rank = rank
            };
        }

        private static List<Objective> Apply(
            IReadOnlyList<Objective> candidates,
            IReadOnlyList<Objective> allObjectives,
            IReadOnlyDictionary<ObjectivePriorityEnum, string>? cursor = null)
        {
            Dictionary<string, string> campaignByObjectiveId;
            return ObjectiveFairShareOrder.Apply(
                candidates,
                allObjectives,
                cursor ?? new Dictionary<ObjectivePriorityEnum, string>(),
                out campaignByObjectiveId);
        }

        private void AssertSequence(IReadOnlyList<string> expected, IReadOnlyList<Objective> actual)
        {
            AssertEqual(expected.Count, actual.Count, "The ordered result count differs.");
            AssertEqual(
                string.Join(",", expected),
                string.Join(",", actual.Select(objective => objective.Id)),
                "The fair-share order differs.");
        }
    }
}
