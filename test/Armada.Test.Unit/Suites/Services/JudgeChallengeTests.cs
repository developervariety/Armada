namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    /// <summary>
    /// The deterministic half of the challenge round: which Judge failures are challengeable, how the
    /// Challenger's ruling line is settled against the numbered findings, and which findings always stand.
    /// Output that cannot be settled is an error, never a partial result.
    /// </summary>
    public class JudgeChallengeTests : TestSuite
    {
        public override string Name => "Judge Challenge";

        private static readonly List<string> _Findings = new List<string>
        {
            "src/Parser.cs:12 -- returns null on empty input. NOT MET.",
            "src/Parser.cs:40 -- the report states 14 rows; the table has 12. NOT MET.",
            "The summary is not written in the house style. NOT MET."
        };

        private static string Line(string json) => "## Rulings\nnotes\n[ARMADA:CHALLENGE] " + json + "\n[ARMADA:RESULT] COMPLETE";

        private static string Ruling(int index, string ruling, double confidence, string evidence = "src/Parser.cs:12 shows the guard")
        {
            return "{\"index\":" + index + ",\"kind\":\"DEFECT\",\"ruling\":\"" + ruling + "\",\"confidence\":"
                + confidence.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"evidence\":\"" + evidence + "\"}";
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("Only a Judge NEEDS_REVISION without a conflict or comment-only marker is challengeable", () =>
            {
                Dictionary<string, bool> cases = new Dictionary<string, bool>
                {
                    ["Judge|Judge verdict: NEEDS_REVISION"] = true,
                    ["Judge|Judge verdict: FAIL"] = false,
                    ["Judge|Judge PASS rejected: an independent Check failed"] = false,
                    ["Judge|Judge verdict: NEEDS_REVISION " + JudgeCriterionConflicts.RescueBlockMarker] = false,
                    ["Judge|Judge verdict: NEEDS_REVISION " + MissionService.RevisionCommentOnlyRescueBlockMarker] = false,
                    ["Worker|Judge verdict: NEEDS_REVISION"] = false
                };
                foreach (KeyValuePair<string, bool> item in cases)
                {
                    string[] parts = item.Key.Split('|');
                    Mission mission = new Mission { Persona = parts[0], FailureReason = parts[1] };
                    AssertEqual(item.Value, JudgeChallenge.IsChallengeableFailure(mission), item.Key);
                }
            });

            await RunTest("Settle keeps upheld findings, refutes only at or above the floor with evidence, and keeps unchallengeable findings", () =>
            {
                string output = Line("{\"findings\":[" + Ruling(1, "UPHOLD", 0.9) + "," + Ruling(2, "REFUTE", 0.85)
                    + "," + Ruling(3, "REFUTE", 0.65) + "]}");
                JudgeChallengeResult result = JudgeChallenge.Settle(output, _Findings, 0.70, _ => false);
                AssertTrue(result.IsValid, "a complete ruling line settles");
                AssertEqual("1,3", String.Join(",", result.Upheld), "finding 1 upheld and finding 3 refuted below the floor stand");
                AssertEqual("3", String.Join(",", result.UpheldBelowFloor), "the below-floor refute is named");
                AssertEqual(1, result.Refuted.Count, "one finding is refuted");
                AssertEqual(2, result.Refuted[0].Index, "finding 2 is the refuted one");

                JudgeChallengeResult noEvidence = JudgeChallenge.Settle(
                    Line("{\"findings\":[" + Ruling(1, "REFUTE", 0.95, "") + "," + Ruling(2, "REFUTE", 0.95) + "," + Ruling(3, "UPHOLD", 0.9) + "]}"),
                    _Findings, 0.70, _ => false);
                AssertEqual("1,3", String.Join(",", noEvidence.Upheld), "a refute without evidence stands as upheld");

                JudgeChallengeResult guarded = JudgeChallenge.Settle(
                    Line("{\"findings\":[" + Ruling(1, "REFUTE", 0.95) + "," + Ruling(2, "REFUTE", 0.95) + "," + Ruling(3, "REFUTE", 0.95) + "]}"),
                    _Findings, 0.70, finding => finding.Contains("returns null", StringComparison.Ordinal));
                AssertEqual("1", String.Join(",", guarded.Upheld), "an unchallengeable finding stands whatever the ruling");
                AssertEqual("1", String.Join(",", guarded.UpheldUnchallengeable), "and is named as unchallengeable");
                AssertEqual(2, guarded.Refuted.Count, "the other two are refuted");
            });

            await RunTest("Settle rejects output that does not rule every finding exactly once", () =>
            {
                Dictionary<string, string> cases = new Dictionary<string, string>
                {
                    ["no_challenge_marker"] = "## Rulings\nAll upheld.\n[ARMADA:RESULT] COMPLETE",
                    ["invalid_challenge_json"] = Line("{\"findings\":[{\"index\":1,"),
                    ["empty_challenge"] = Line("{\"findings\":[]}"),
                    ["missing_ruling"] = Line("{\"findings\":[" + Ruling(1, "UPHOLD", 0.9) + "," + Ruling(2, "UPHOLD", 0.9) + "]}"),
                    ["duplicate_ruling"] = Line("{\"findings\":[" + Ruling(1, "UPHOLD", 0.9) + "," + Ruling(1, "REFUTE", 0.9) + "," + Ruling(3, "UPHOLD", 0.9) + "]}"),
                    ["finding_out_of_range"] = Line("{\"findings\":[" + Ruling(1, "UPHOLD", 0.9) + "," + Ruling(2, "UPHOLD", 0.9) + "," + Ruling(4, "UPHOLD", 0.9) + "]}"),
                    ["unknown_ruling"] = Line("{\"findings\":[" + Ruling(1, "UPHOLD", 0.9) + "," + Ruling(2, "MAYBE", 0.9) + "," + Ruling(3, "UPHOLD", 0.9) + "]}")
                };
                foreach (KeyValuePair<string, string> item in cases)
                {
                    JudgeChallengeResult result = JudgeChallenge.Settle(item.Value, _Findings, 0.70, _ => false);
                    AssertFalse(result.IsValid, item.Key + " is not a usable result");
                    AssertStartsWith(item.Key, result.Error ?? String.Empty, item.Key + " names its reason");
                    AssertEqual(0, result.Refuted.Count, item.Key + " refutes nothing");
                }

                string quoted = "The format is `[ARMADA:CHALLENGE] {\"findings\":[]}` on one line.\n[ARMADA:RESULT] COMPLETE";
                AssertStartsWith("no_challenge_marker", JudgeChallenge.Settle(quoted, _Findings, 0.70, _ => false).Error ?? String.Empty,
                    "a marker quoted inside prose is not a ruling line");
            });

            await RunTest("A finding is unchallengeable when it names a configured marker or a protected path", () =>
            {
                List<string> markers = new List<string> { "guard-marker" };
                List<string> protectedPaths = new List<string> { "**/CLAUDE.md", "deploy/secrets/**" };
                AssertTrue(JudgeChallenge.IsUnchallengeable("The diff deletes the GUARD-MARKER test.", markers, protectedPaths), "marker, any case");
                AssertTrue(JudgeChallenge.IsUnchallengeable("docs/CLAUDE.md:4 is edited.", markers, protectedPaths), "glob file name");
                AssertTrue(JudgeChallenge.IsUnchallengeable("deploy/secrets/key.txt changed.", markers, protectedPaths), "glob directory");
                AssertFalse(JudgeChallenge.IsUnchallengeable("src/Parser.cs:12 returns null.", markers, protectedPaths), "an ordinary finding");
                AssertFalse(JudgeChallenge.IsUnchallengeable("src/Parser.cs:12 returns null.", null, null), "no markers and no paths");
            });

            await RunTest("Findings are numbered from the Judge review in order, and an empty review has none", () =>
            {
                string review = "## Completeness\n"
                    + "- src/Parser.cs:12 -- empty input is handled. NOT MET.\n"
                    + "- src/Parser.cs:20 -- delivered behaviour. MET.\n"
                    + "- src/Parser.cs:40 -- the row count is reported. NOT MET.\n";
                List<string> findings = JudgeChallenge.ExtractFindings(review, out bool complete);
                AssertTrue(complete, "a short review is complete");
                AssertEqual(2, findings.Count, "only the NOT MET items are findings");
                AssertContains("src/Parser.cs:12", findings[0], "finding 1 is the first NOT MET item");
                AssertContains("src/Parser.cs:40", findings[1], "finding 2 is the second NOT MET item");
                AssertFalse(findings[0].StartsWith("- ", StringComparison.Ordinal), "the list bullet is removed");

                AssertEqual(0, JudgeChallenge.ExtractFindings(null, out bool _).Count, "no review, no findings");
            });

            await RunTest("Items under a lead-in that reports agreement are not findings, and every blocking and NOT MET item still is", () =>
            {
                string review = String.Join("\n", new[]
                {
                    "## Completeness",
                    "- The report covers every parser entry point.",
                    "",
                    "## Correctness",
                    "",
                    "**Blocking: the timeout finding is wrong.** The report says `Read` hangs, but `src/Reader.cs:40` returns after the retry limit.",
                    "",
                    "Three parts of the report need correcting:",
                    "- The hang claim in section 2.",
                    "- The retry count in the summary table.",
                    "",
                    "**Spot checks that agree with source:**",
                    "- **Pad:** the `5 + Max(data.Length, 6)` pad (`src/Frame.cs:68`, finding 1).",
                    "- **Length byte:** `1 + data.Length` (line 71).",
                    "- **Index check:** the check is `Length >= 3`, then index `[3]`, so lengths 3 and 5 can throw (finding 4 is correct).",
                    "- **Builder pad:** the second frame builder carries the pad, which the port's frame type lacks.",
                    "",
                    "## Acceptance Criteria",
                    "- Every entry point is listed -- MET.",
                    "- Every hang claim cites a line that blocks -- NOT MET.",
                    "",
                    "## Verdict",
                    "",
                    "NEEDS_REVISION. Correct the timeout finding and the two report sections.",
                    "",
                    "[ARMADA:VERDICT] NEEDS_REVISION"
                });

                List<string> findings = JudgeChallenge.ExtractFindings(review, out bool complete);
                string joined = String.Join("\n", findings);

                AssertTrue(complete, "the review fits the bound");
                AssertContains("the timeout finding is wrong", joined, "the explicit blocking item is a finding");
                AssertContains("The hang claim in section 2.", joined, "an item under a lead-in that asks for a correction is a finding");
                AssertContains("The retry count in the summary table.", joined, "every item under that lead-in is a finding");
                AssertContains("Every hang claim cites a line that blocks -- NOT MET.", joined, "the NOT MET criterion is a finding");
                AssertFalse(joined.Contains("Every entry point is listed", StringComparison.Ordinal), "the MET criterion is not a finding");
                foreach (string agreed in new[] { "**Pad:**", "**Length byte:**", "**Index check:**", "**Builder pad:**" })
                    AssertFalse(joined.Contains(agreed, StringComparison.Ordinal), agreed + " sits under a lead-in that reports agreement, so it is not a finding");
            });

            await RunTest("Counterexample steps and passing test counts are not findings of their own", () =>
            {
                string review = String.Join("\n", new[]
                {
                    "## Correctness",
                    "",
                    "**Required revision: order persistence does not take the resume lock.**",
                    "",
                    "`src/Desk/Operations.cs:639-658` holds the gate, but `src/Orders.cs:71` writes under a separate lock.",
                    "",
                    "Concrete counterexample using the existing `GuardOrder(Start, \"Unknown\")` fixture:",
                    "",
                    "1. Both halt states are set and no unresolved order exists.",
                    "2. Resume finishes its order check.",
                    "3. Another thread saves the Unknown order.",
                    "4. Resume clears both halt states without seeing that order.",
                    "",
                    "## Tests",
                    "",
                    "`sh scripts/check.sh` completed in the foreground with **exit code 0**:",
                    "",
                    "- C#: 1,389 passed, 0 failed.",
                    "- JavaScript: 138 passed, 0 failed.",
                    "- Python: 112 passed.",
                    "- Lint: 0 issues.",
                    "",
                    "The Test Engineer report names concurrent saves as untested. Its assumption that the gate provides exclusion is incorrect.",
                    "",
                    "## Verdict",
                    "",
                    "NEEDS_REVISION. Close the order-save race and add a deterministic regression test.",
                    "",
                    "[ARMADA:VERDICT] NEEDS_REVISION"
                });

                List<string> findings = JudgeChallenge.ExtractFindings(review, out bool complete);
                string joined = String.Join("\n", findings);

                AssertTrue(complete, "the review fits the bound");
                AssertContains("order persistence does not take the resume lock", joined, "the required revision is a finding");
                AssertContains("assumption that the gate provides exclusion is incorrect", joined, "the Tests paragraph that names a defect is a finding");
                AssertContains("Close the order-save race", joined, "the verdict's required change is a finding");
                foreach (string step in new[] { "Both halt states are set", "Resume finishes its order check", "Another thread saves", "Resume clears both halt states" })
                    AssertFalse(joined.Contains(step, StringComparison.Ordinal), "the counterexample step '" + step + "' is evidence for the finding, not a finding");
                foreach (string count in new[] { "1,389 passed", "138 passed", "112 passed", "0 issues" })
                    AssertFalse(joined.Contains(count, StringComparison.Ordinal), "the passing count '" + count + "' is not a finding");
                AssertEqual(3, findings.Count, "only the three real findings are numbered (findings: " + joined + ")");

                var samples = new[]
                {
                    (Expected: 7, Review: """
                        ## Correctness
                        **Required revision:** src/Reader.cs:20 rejects normal timeout before cleanup.

                        Source completeness:
                        - Request framing: ported at `src/Reader.cs:10` and `:24`.
                        - Pending wait and bounded retry: reused through `:30`.
                        - Physical action and cleanup: preserved at `:14`.
                        - Timeout completion: **NOT DELIVERED**, not deferred.
                        - Alternate paths: preserved.

                        ## Tests
                        Commands completed:
                        - `dotnet test tests/Reader.Tests`: 0 failed.
                        - `dotnet test Example.sln`: exit 1. Core failed PendingWithinBudget at tests/ReaderTests.cs:30, expected Ready, actual Timeout.
                        - `dotnet build Example.sln`: 0 errors.
                        - `dotnet build tools/Client.csproj`: 0 errors.

                        The timeout test pins the defect. Replace it with the required outcome and demonstrate failure before repair. The full-suite failure remains unverified.

                        ## Acceptance Criteria
                        - Timeout must clean up -- NOT MET. src/Reader.cs:20 rejects timeout.
                        - Matched negatives remain failures -- MET. src/Reader.cs:12.
                        - Correct the timeout assertion and pass full checks -- NOT MET. The old assertion remains; full test command exited 1.

                        ## Verdict
                        NEEDS_REVISION. Implement timeout cleanup, replace the wrong test and obtain passing full checks.
                        [ARMADA:VERDICT] NEEDS_REVISION
                        """, Keep: new[] { "rejects normal timeout", "**NOT DELIVERED**", "Core failed PendingWithinBudget", "Replace it with the required outcome", "Timeout must clean up -- NOT MET", "Correct the timeout assertion", "Implement timeout cleanup" },
                        Drop: new[] { "Request framing: ported", "bounded retry: reused", "Physical action and cleanup: preserved", "Alternate paths: preserved", "tests/Reader.Tests", "build Example.sln", "build tools/Client.csproj", "Matched negatives remain failures" }),
                    (Expected: 6, Review: """
                        ## Correctness
                        **Required revision: cancellation releases ownership before active work stops.** src/Runner.cs:10 returns early.

                        I reproduced this using the existing fixture:
                        1. The first task entered a bounded wait.
                        2. Cancellation ended its wrapper; ownership became free.
                        3. A second task entered while the first remained active.
                        4. The first task later completed.

                        Keep ownership until active work stops. Preserve exit propagation. Add a direct cancellation test.

                        ## Tests
                        - `python3 -m unittest tests.test_runner`: **21 passed**, 0.125 seconds.
                        - Exact-commit Check `check-example`: passed, 12.500 seconds.
                        - Foreground validation from a clone used:
                          `sh scripts/verify.sh`
                          Result: **exit 0**, 14.500 seconds, admission wait 0; **41 C# passed, 1 skipped; 12 Python passed**.

                        ## Acceptance Criteria
                        - Ownership survives cancellation -- NOT MET. src/Runner.cs:10 releases it early.
                        - Nested operation succeeds -- MET. The fixture passes.
                        - Concurrent work is serialized -- NOT MET. src/Runner.cs:10 permits overlap.
                        - All active work stops before release -- NOT MET. The first task survives.

                        ## Verdict
                        NEEDS_REVISION. Fix cancellation before ownership release.
                        [ARMADA:VERDICT] NEEDS_REVISION
                        """, Keep: new[] { "cancellation releases ownership", "Keep ownership until", "Ownership survives cancellation -- NOT MET", "Concurrent work is serialized -- NOT MET", "All active work stops before release -- NOT MET", "Fix cancellation before ownership release" },
                        Drop: new[] { "first task entered", "Cancellation ended its wrapper", "second task entered", "first task later completed", "21 passed", "check-example", "Foreground validation", "41 C# passed", "Nested operation succeeds" })
                };
                foreach (var sample in samples)
                {
                    List<string> actual = JudgeChallenge.ExtractFindings(sample.Review, out bool sampleComplete);
                    string extracted = String.Join("\n", actual);
                    AssertTrue(sampleComplete);
                    AssertEqual(sample.Expected, actual.Count, extracted);
                    foreach (string keep in sample.Keep) AssertContains(keep, extracted);
                    foreach (string drop in sample.Drop)
                        AssertFalse(extracted.Contains(drop, StringComparison.Ordinal), "passing evidence is not a finding: " + drop);
                }

                foreach (string failure in new[]
                {
                    "`dotnet test Example.sln`: exit 1.",
                    "`dotnet test Example.sln`: 1 failed.",
                    "`dotnet build Example.sln`: 1 error.",
                    "`dotnet test Example.sln`: 0 failed, but required coverage is missing.",
                    "Validation: passed.\n  Result: exit 1.",
                    "Foreground validation used:\n  `sh scripts/verify.sh`\n  Result: **exit 1**, 2 seconds."
                })
                {
                    List<string> actual = JudgeChallenge.ExtractFindings(
                        "## Correctness\n**Required revision:** src/Reader.cs:20 is incorrect.\n## Tests\nCommands completed:\n- " + failure,
                        out bool failureComplete);
                    AssertTrue(failureComplete);
                    AssertEqual(2, actual.Count, "a failed or qualified result must remain: " + failure);
                    AssertContains(System.Text.RegularExpressions.Regex.Replace(failure, @"\s+", " "), String.Join("\n", actual));
                }
                List<string> qualifiedSource = JudgeChallenge.ExtractFindings(
                    "## Correctness\n**Required revision:** src/Reader.cs:20 is incorrect.\nSource completeness:\n- Reader path: preserved, but cancellation is incorrect.", out bool sourceComplete);
                AssertTrue(sourceComplete);
                AssertEqual(2, qualifiedSource.Count, "qualified source agreement retains the defect");
                foreach (string defect in new[]
                {
                    "Parser: preserved; malformed input causes a crash.",
                    "Parser: preserved. Timeout remains unsafe.",
                    "Reader path: preserved; src/Reader.cs:20 still returns before cleanup."
                })
                {
                    List<string> actual = JudgeChallenge.ExtractFindings(
                        "## Correctness\n**Required revision:** src/Reader.cs:20 is incorrect.\nSource completeness:\n- " + defect,
                        out bool defectComplete);
                    AssertTrue(defectComplete);
                    AssertEqual(2, actual.Count, "a positive prefix cannot hide a defect: " + defect);
                    AssertContains(defect, String.Join("\n", actual));
                }
                List<string> indentedRevision = JudgeChallenge.ExtractFindings(
                    "## Tests\n- Passed baseline.\n  **Required revision:** cap the retry deadline.", out bool revisionComplete);
                AssertTrue(revisionComplete);
                AssertEqual(1, indentedRevision.Count);
                AssertContains("cap the retry deadline", String.Join("\n", indentedRevision));
                List<string> indentedDirective = JudgeChallenge.ExtractFindings(
                    "## Tests\n- Passed baseline.\n  Fix the retry deadline.", out bool directiveComplete);
                AssertTrue(directiveComplete);
                AssertEqual(1, indentedDirective.Count);
                AssertContains("Fix the retry deadline", String.Join("\n", indentedDirective));



            });

            await RunTest("The round is off by default, and an excluded vessel is never challenged", () =>
            {
                JudgeChallengeSettings settings = new JudgeChallengeSettings();
                AssertFalse(settings.AppliesTo("vsl_example"), "off by default");
                settings.Enabled = true;
                settings.ExcludedVesselIds = new List<string> { "vsl_excluded" };
                AssertTrue(settings.AppliesTo("vsl_example"), "an enabled round applies to other vessels");
                AssertFalse(settings.AppliesTo("vsl_excluded"), "an excluded vessel is skipped");
                AssertFalse(settings.AppliesTo(null), "a mission with no vessel is skipped");
                settings.RefuteMinimumConfidence = 0.2;
                AssertEqual(0.5, settings.RefuteMinimumConfidence, "the floor never drops below an even call");
            });

            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
}
