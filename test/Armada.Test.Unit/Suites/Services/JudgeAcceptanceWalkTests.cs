namespace Armada.Test.Unit.Suites.Services
{
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Pins the Judge acceptance-criteria walk: a PASS must mark every brief criterion MET,
    /// and a NOT MET line forbids PASS. The walk is a real ground, not a heading-form miss.
    /// </summary>
    public sealed class JudgeAcceptanceWalkTests : TestSuite
    {
        public override string Name => "Judge Acceptance Walk";

        private const string BriefTwo =
            "# Objective Brief\n" +
            "## Acceptance Criteria\n" +
            "- Gate stays green\n" +
            "- CHANGELOG names the behaviour\n" +
            "## Scope\nHuge scope that truncation would otherwise keep instead of the criteria.\n";

        private const string PassReview =
            "## Completeness\nThe diff delivers both criteria.\n" +
            "## Correctness\nLogic is sound and the walk cites evidence.\n" +
            "## Tests\nUnit tests cover the walk and the truncation pin.\n" +
            "## Failure Modes\nA missing walk refuses PASS rather than silently shipping.\n" +
            "## Acceptance Criteria\n" +
            "- Gate stays green: MET (JudgeAcceptanceWalk.cs:40)\n" +
            "- CHANGELOG names the behaviour: MET (CHANGELOG.md:12)\n" +
            "## Verdict\nPASS\n";

        protected override async Task RunTestsAsync()
        {
            await RunTest("ExtractCriteria reads bullets under the brief heading", () =>
            {
                System.Collections.Generic.List<string> items = JudgeAcceptanceWalk.ExtractCriteria(BriefTwo);
                AssertEqual(2, items.Count);
                AssertEqual("Gate stays green", items[0]);
                AssertEqual("CHANGELOG names the behaviour", items[1]);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("ValidatePass is silent when the brief lists no criteria", () =>
            {
                AssertNull(JudgeAcceptanceWalk.ValidatePass(PassReview, "## Scope\nNo criteria heading.\n"));
                AssertNull(JudgeAcceptanceWalk.ValidatePass(PassReview, null));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("ValidatePass accepts one MET line per criterion", () =>
            {
                AssertNull(JudgeAcceptanceWalk.ValidatePass(PassReview, BriefTwo));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("ValidatePass refuses a missing Acceptance Criteria section", () =>
            {
                string output = PassReview.Replace("## Acceptance Criteria\n- Gate stays green: MET (JudgeAcceptanceWalk.cs:40)\n- CHANGELOG names the behaviour: MET (CHANGELOG.md:12)\n", "");
                string? reason = JudgeAcceptanceWalk.ValidatePass(output, BriefTwo);
                AssertNotNull(reason);
                AssertContains("Acceptance Criteria", reason!);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("ValidatePass refuses a NOT MET line even when every criterion is listed", () =>
            {
                string output = PassReview.Replace(
                    "- CHANGELOG names the behaviour: MET (CHANGELOG.md:12)",
                    "- CHANGELOG names the behaviour: NOT MET");
                string? reason = JudgeAcceptanceWalk.ValidatePass(output, BriefTwo);
                AssertEqual("Judge PASS names a NOT MET acceptance criterion", reason);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("ValidatePass refuses an incomplete walk", () =>
            {
                string output = PassReview.Replace(
                    "- Gate stays green: MET (JudgeAcceptanceWalk.cs:40)\n- CHANGELOG names the behaviour: MET (CHANGELOG.md:12)\n",
                    "- Gate stays green: MET (JudgeAcceptanceWalk.cs:40)\n");
                string? reason = JudgeAcceptanceWalk.ValidatePass(output, BriefTwo);
                AssertEqual("Judge PASS does not list every acceptance criterion from the brief", reason);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("ValidatePass refuses duplicate and unrelated criterion claims", () =>
            {
                string duplicate = PassReview.Replace("CHANGELOG names the behaviour: MET (CHANGELOG.md:12)", "Gate stays green: MET (JudgeAcceptanceWalk.cs:40)");
                AssertNotNull(JudgeAcceptanceWalk.ValidatePass(duplicate, BriefTwo));
                string unrelated = PassReview.Replace("CHANGELOG names the behaviour", "Unrequested cleanup is complete");
                AssertNotNull(JudgeAcceptanceWalk.ValidatePass(unrelated, BriefTwo));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("ValidatePass requires evidence for each MET claim", () =>
            {
                string unsupported = PassReview.Replace("MET (CHANGELOG.md:12)", "MET because it looks fine");
                AssertNotNull(JudgeAcceptanceWalk.ValidatePass(unsupported, BriefTwo));
                string command = PassReview.Replace("MET (CHANGELOG.md:12)", "MET (command: `git diff -- CHANGELOG.md`; output includes the new behavior)");
                AssertNull(JudgeAcceptanceWalk.ValidatePass(command, BriefTwo));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("ValidatePass refuses a similarly named section", () =>
            {
                AssertNotNull(JudgeAcceptanceWalk.ValidatePass(PassReview.Replace("## Acceptance Criteria", "## Acceptance Criteria Notes"), BriefTwo));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("ValidatePass accepts MET in criterion text and extensionless file evidence", () =>
            {
                AssertNull(JudgeAcceptanceWalk.ValidatePass(
                    "## Acceptance Criteria\n- Requirements are met: MET (Dockerfile:12)\n",
                    "## Acceptance Criteria\n- Requirements are met\n"));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await Task.CompletedTask;
        }
    }
}
