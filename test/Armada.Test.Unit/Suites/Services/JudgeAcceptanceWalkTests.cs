namespace Armada.Test.Unit.Suites.Services
{
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Pins the Judge acceptance-criteria walk: a PASS must mark every brief criterion MET,
    /// and a NOT MET verdict for a required criterion forbids PASS.
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
            await RunTest("A Judge that writes exactly the headings its brief names passes the section and walk checks", () =>
            {
                // Every Judge brief variant, report-only and implementation, against the same rule.
                System.Collections.Generic.Dictionary<string, string> briefs = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["report-only role"] = MissionService.BuildPersonaPreamble("Judge", MissionModeEnum.Research, 0),
                    ["revised-report role"] = MissionService.BuildPersonaPreamble("Judge", MissionModeEnum.Research, 2),
                    ["implementation role"] = MissionService.BuildPersonaPreamble("Judge", MissionModeEnum.Implementation, 0),
                    ["report-only output contract"] = MissionPromptBuilder.BuildReportOnlyJudgeOutputContract(MissionModeEnum.Research, null)
                };

                foreach (System.Collections.Generic.KeyValuePair<string, string> entry in briefs)
                {
                    bool reportOnly = !entry.Key.StartsWith("implementation", System.StringComparison.Ordinal);
                    System.Collections.Generic.List<string> named = new System.Collections.Generic.List<string>();
                    foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(entry.Value, @"`## ([^`]+)`"))
                    {
                        if (!named.Contains(match.Groups[1].Value)) named.Add(match.Groups[1].Value);
                    }

                    System.Text.StringBuilder review = new System.Text.StringBuilder();
                    foreach (string heading in named)
                    {
                        review.Append("## ").Append(heading).Append('\n');
                        if (heading == JudgeAcceptanceWalk.SectionName)
                        {
                            review.Append("- Gate stays green -- MET. src/Gate.cs:10 returns green.\n");
                            review.Append("- CHANGELOG names the behaviour -- MET. CHANGELOG.md:12 names it.\n");
                        }
                        else
                        {
                            review.Append("Reviewed in full against the brief.\n");
                        }
                    }

                    string text = review.ToString();
                    foreach (string section in Armada.Core.JudgeReviewSections.Required(reportOnly))
                        AssertTrue(named.Contains(section), entry.Key + " names the required section " + section);
                    AssertNull(JudgeAcceptanceWalk.ValidatePass(text, entry.Value + "\n\n" + BriefTwo),
                        entry.Key + " names every section the PASS check requires: " + string.Join(", ", named));
                }

                return Task.CompletedTask;
            }).ConfigureAwait(false);

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

            await RunTest("ValidatePass measures the live criteria, not the dispatch-time snapshot, when they are supplied", () =>
            {
                // The dispatch-time brief batched four extra criteria that an operator later removed from the
                // objective, leaving two in scope. The Judge PASS walks exactly the two current criteria.
                string snapshotBrief =
                    "# Objective Brief\n" +
                    "## Acceptance Criteria\n" +
                    "- Gate stays green\n" +
                    "- CHANGELOG names the behaviour\n" +
                    "- Batched step added metrics export\n" +
                    "- Batched step added a retry backoff\n" +
                    "- Batched step added a dashboard panel\n" +
                    "- Batched step added an audit log\n" +
                    "## Scope\nThe snapshot still lists the removed criteria.\n";

                System.Collections.Generic.List<string> narrowed = new System.Collections.Generic.List<string>
                {
                    "Gate stays green",
                    "CHANGELOG names the behaviour"
                };

                // The symptom: against the dispatch snapshot the PASS is refused because the removed criteria
                // are not walked, even though it covers every current criterion.
                AssertEqual("Judge PASS does not list every acceptance criterion from the brief",
                    JudgeAcceptanceWalk.ValidatePass(PassReview, snapshotBrief));

                // The fix: with the live criteria supplied the PASS is accepted; the stale snapshot does not count.
                AssertNull(JudgeAcceptanceWalk.ValidatePass(PassReview, snapshotBrief, narrowed),
                    "a PASS that covers the current criteria is not refused on criteria removed after dispatch");

                // An empty live set means the objective now lists no criteria: no walk is required.
                AssertNull(JudgeAcceptanceWalk.ValidatePass(
                    PassReview.Replace("## Acceptance Criteria\n- Gate stays green: MET (JudgeAcceptanceWalk.cs:40)\n- CHANGELOG names the behaviour: MET (CHANGELOG.md:12)\n", ""),
                    snapshotBrief,
                    new System.Collections.Generic.List<string>()),
                    "an empty current criteria set requires no walk");

                // A live criterion the PASS never walks is still refused, so the walk is not weakened.
                System.Collections.Generic.List<string> withExtra = new System.Collections.Generic.List<string>(narrowed)
                {
                    "A live criterion the Judge never walked"
                };
                AssertEqual("Judge PASS does not list every acceptance criterion from the brief",
                    JudgeAcceptanceWalk.ValidatePass(PassReview, snapshotBrief, withExtra));

                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("ValidatePass covers a criterion the brief lists twice with one MET line", () =>
            {
                string brief = BriefTwo.Replace("- CHANGELOG names the behaviour\n", "- CHANGELOG names the behaviour\n- Gate stays green\n");
                AssertNull(JudgeAcceptanceWalk.ValidatePass(PassReview, brief));
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

            await RunTest("ValidatePass reads the verdict after criterion text containing NOT MET", () =>
            {
                string criterion = "Report each result with honest MET/NOT MET and its evidence";
                string brief = "## Acceptance Criteria\n- " + criterion + "\n";
                string review = "## Acceptance Criteria\n- " + criterion + ": MET (README.md:12)\n";
                AssertNull(JudgeAcceptanceWalk.ValidatePass(review, brief));
                AssertEqual("Judge PASS names a NOT MET acceptance criterion",
                    JudgeAcceptanceWalk.ValidatePass(review.Replace(": MET (", ": NOT MET ("), brief));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("ValidatePass ignores NOT MET in evidence but rejects a contradictory criterion verdict", () =>
            {
                string review = PassReview.Replace("MET (CHANGELOG.md:12)",
                    "MET (command: `check`; output verifies NOT MET is rejected)");
                AssertNull(JudgeAcceptanceWalk.ValidatePass(review, BriefTwo));
                string contradiction = review.Replace("## Verdict", "- Gate stays green: NOT MET (README.md:12)\n## Verdict");
                AssertEqual("Judge PASS names a NOT MET acceptance criterion",
                    JudgeAcceptanceWalk.ValidatePass(contradiction, BriefTwo));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await Task.CompletedTask;
        }
    }
}
