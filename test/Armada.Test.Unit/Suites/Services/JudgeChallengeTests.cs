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
