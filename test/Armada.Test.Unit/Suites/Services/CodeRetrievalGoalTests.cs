namespace Armada.Test.Unit.Suites.Services
{
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Guards against re-inlining the whole brief into the Code Index instructions. The mission
    /// description already appears verbatim under Mission Instructions; quoting it again as the
    /// context-pack goal duplicated the entire brief in every captain's prompt and diluted the
    /// retrieval query with acceptance criteria and non-goals.
    /// </summary>
    public class CodeRetrievalGoalTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Code Retrieval Goal";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("LongBrief_IsNotRepeatedWholesale", () =>
            {
                Mission mission = new Mission();
                mission.Title = "[Worker] Revise gap-4a";
                mission.Description = new string('x', 4000);

                string goal = MissionService.BuildCodeRetrievalGoal(mission);

                AssertTrue(goal.Length < 400, "a 4000-char brief must not be embedded whole; was " + goal.Length);
                AssertTrue(goal.StartsWith("[Worker] Revise gap-4a"), "the title must lead the retrieval goal");
            });

            await RunTest("ShortGoal_IsPreservedExactly", () =>
            {
                Mission mission = new Mission();
                mission.Title = "Fix seed-key signer";
                mission.Description = "Correct the Hino BitwiseNot index.";

                string goal = MissionService.BuildCodeRetrievalGoal(mission);

                AssertTrue(goal == "Fix seed-key signer -- Correct the Hino BitwiseNot index.",
                    "a goal within budget must pass through unchanged; was: " + goal);
            });

            await RunTest("GoalIsSingleLine", () =>
            {
                Mission mission = new Mission();
                mission.Title = "Multi";
                mission.Description = "line one\nline two\r\nline three";

                string goal = MissionService.BuildCodeRetrievalGoal(mission);

                AssertTrue(!goal.Contains("\n") && !goal.Contains("\r"),
                    "the goal is quoted inline in instructions and must stay single-line");
            });

            await RunTest("TitleAlone_StillProducesGoal", () =>
            {
                // Mission.Title rejects null/empty at the setter, so a description-only mission is not
                // constructible; only the missing-description fallback is reachable in practice.
                Mission emptyDescription = new Mission();
                emptyDescription.Title = "Only a title";
                emptyDescription.Description = "";
                AssertTrue(MissionService.BuildCodeRetrievalGoal(emptyDescription) == "Only a title",
                    "a mission with an empty description falls back to the title");

                Mission nullDescription = new Mission();
                nullDescription.Title = "Only a title";
                nullDescription.Description = null;
                AssertTrue(MissionService.BuildCodeRetrievalGoal(nullDescription) == "Only a title",
                    "a mission with a null description falls back to the title");
            });

            await RunTest("Truncation_PrefersWordBoundary", () =>
            {
                string goal = MissionService.TruncateRetrievalGoal("alpha beta gamma delta epsilon", 20);
                AssertTrue(goal.EndsWith(" ..."), "a truncated goal is marked with an ellipsis");
                AssertTrue(!goal.Contains("delt"), "truncation must not split a word mid-token; was: " + goal);

                // No space in the back half of the budget -- hard cut rather than losing everything.
                string hard = MissionService.TruncateRetrievalGoal(new string('y', 100), 20);
                AssertTrue(hard.StartsWith(new string('y', 20)), "an unbroken run is hard-cut at the budget");

                AssertTrue(MissionService.TruncateRetrievalGoal("short", 20) == "short",
                    "text within budget is returned unchanged");
                AssertTrue(MissionService.TruncateRetrievalGoal(null, 20) == "",
                    "null is normalized to empty");
            });
        }
    }
}
