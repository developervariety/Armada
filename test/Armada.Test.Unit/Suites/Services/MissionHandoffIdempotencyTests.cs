namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Covers the pipeline-handoff idempotency guards. A handoff that runs twice for the same upstream
    /// mission (batch path plus the lazy self-heal path, or a rescue re-prepare) previously appended the
    /// whole prior-stage block a second time, which produced a 106,750-byte captain brief in which the
    /// persona preamble and the prior-stage block each appeared twice.
    /// </summary>
    public class MissionHandoffIdempotencyTests : TestSuite
    {
        public override string Name => "Mission Handoff Idempotency";

        private const string _UpstreamId = "msn_upstream_one";
        private const string _OtherUpstreamId = "msn_upstream_two";

        private const string _Preamble =
            "## Your Role: TestEngineer (Write Tests)\n\nYou are writing tests for code changes made by the Worker.\n\n";

        /// <summary>
        /// Composes a description exactly as the handoff does: strip any prior block for this upstream,
        /// prepend the persona preamble only when absent, then append a fresh block.
        /// </summary>
        private string ApplyHandoff(string description, string upstreamId, string blockBody)
        {
            string stripped = MissionService.StripHandoffBlock(description, upstreamId);
            string block = "\n\n---\n" + MissionService.BuildHandoffMarker(upstreamId) + "\n" + blockBody;

            return MissionService.ContainsPersonaPreamble(stripped, _Preamble)
                ? stripped + block
                : _Preamble + stripped + block;
        }

        private int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            int index = 0;
            while (true)
            {
                index = haystack.IndexOf(needle, index, StringComparison.Ordinal);
                if (index < 0) break;
                count++;
                index += needle.Length;
            }
            return count;
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("Repeated handoff for the same upstream produces one prior-stage block", async () =>
            {
                string first = ApplyHandoff("Base brief.", _UpstreamId, "## Prior Stage Output\nrun one\n");
                string second = ApplyHandoff(first, _UpstreamId, "## Prior Stage Output\nrun two\n");

                AssertEqual(1, CountOccurrences(second, MissionService.BuildHandoffMarker(_UpstreamId)), "handoff marker must appear once");
                AssertEqual(1, CountOccurrences(second, "## Prior Stage Output"), "prior-stage heading must appear once");
                AssertEqual(1, CountOccurrences(second, "## Your Role: TestEngineer (Write Tests)"), "persona preamble must appear once");
                AssertContains("run two", second, "the newest block must win");
                AssertFalse(second.Contains("run one", StringComparison.Ordinal), "the superseded block must be gone");
                AssertContains("Base brief.", second, "the base brief must survive");

                await Task.CompletedTask;
            });

            await RunTest("Handoff from a second upstream keeps both blocks", async () =>
            {
                string first = ApplyHandoff("Base brief.", _UpstreamId, "## Prior Stage Output\nfrom one\n");
                string both = ApplyHandoff(first, _OtherUpstreamId, "## Prior Stage Output\nfrom two\n");

                AssertEqual(1, CountOccurrences(both, MissionService.BuildHandoffMarker(_UpstreamId)), "first upstream block must remain");
                AssertEqual(1, CountOccurrences(both, MissionService.BuildHandoffMarker(_OtherUpstreamId)), "second upstream block must be added");
                AssertContains("from one", both, "first upstream content must remain");
                AssertContains("from two", both, "second upstream content must be present");

                await Task.CompletedTask;
            });

            await RunTest("Replacing the first of two blocks leaves the later block intact", async () =>
            {
                string first = ApplyHandoff("Base brief.", _UpstreamId, "## Prior Stage Output\nfrom one\n");
                string both = ApplyHandoff(first, _OtherUpstreamId, "## Prior Stage Output\nfrom two\n");
                string replaced = ApplyHandoff(both, _UpstreamId, "## Prior Stage Output\nfrom one again\n");

                AssertEqual(1, CountOccurrences(replaced, MissionService.BuildHandoffMarker(_UpstreamId)), "replaced marker must appear once");
                AssertEqual(1, CountOccurrences(replaced, MissionService.BuildHandoffMarker(_OtherUpstreamId)), "untouched marker must survive");
                AssertContains("from one again", replaced, "replacement content must be present");
                AssertContains("from two", replaced, "the other upstream block must survive");
                AssertFalse(replaced.Contains("from one\n", StringComparison.Ordinal), "the superseded first block must be gone");

                await Task.CompletedTask;
            });

            await RunTest("StripHandoffBlock leaves a description without the marker unchanged", async () =>
            {
                string description = "Base brief with no handoff block.";
                AssertEqual(description, MissionService.StripHandoffBlock(description, _UpstreamId), "description must be untouched");
                AssertEqual("", MissionService.StripHandoffBlock("", _UpstreamId), "empty description must stay empty");
                AssertEqual("", MissionService.StripHandoffBlock(null, _UpstreamId), "null description must become empty");
                AssertEqual(description, MissionService.StripHandoffBlock(description, ""), "an empty upstream id must strip nothing");

                await Task.CompletedTask;
            });

            await RunTest("ContainsPersonaPreamble matches on the heading line only", async () =>
            {
                AssertTrue(MissionService.ContainsPersonaPreamble(_Preamble + "rest", _Preamble), "identical preamble must match");
                AssertTrue(
                    MissionService.ContainsPersonaPreamble("## Your Role: TestEngineer (Write Tests)\n\nreworded body\n", _Preamble),
                    "a reworded body must still match on the heading");
                AssertFalse(MissionService.ContainsPersonaPreamble("Base brief.", _Preamble), "unrelated description must not match");
                AssertFalse(MissionService.ContainsPersonaPreamble(null, _Preamble), "null description must not match");
                AssertFalse(MissionService.ContainsPersonaPreamble(_Preamble, null), "null preamble must not match");

                await Task.CompletedTask;
            });

            await RunTest("TruncateMissionDescription bounds an over-budget brief and notes the cut", async () =>
            {
                string oversized = new string('x', 500);
                string bounded = MissionService.TruncateMissionDescription(oversized, 200);

                AssertTrue(bounded.Length <= 200, "bounded description must fit the budget");
                AssertContains("brief truncated to fit the mission description budget", bounded, "the cut must be visible to the captain");

                string small = "short brief";
                AssertEqual(small, MissionService.TruncateMissionDescription(small, 200), "a brief under budget must be unchanged");
                AssertEqual("", MissionService.TruncateMissionDescription("", 200), "an empty brief must stay empty");

                await Task.CompletedTask;
            });
        }
    }
}
