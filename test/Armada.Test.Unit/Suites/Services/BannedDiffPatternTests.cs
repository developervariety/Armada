namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    /// <summary>
    /// The config-driven banned-diff guard: it fails a change whose added lines match an
    /// operator-configured pattern, ships as a no-op with no rules, skips comments and non-added
    /// lines, and never throws on a bad pattern. It carries no domain of its own; the patterns are
    /// configuration.
    /// </summary>
    public class BannedDiffPatternTests : TestSuite
    {
        public override string Name => "Banned Diff Pattern Guard";

        private static List<BannedDiffPatternRule> Rules(string pattern)
        {
            return new List<BannedDiffPatternRule> { new BannedDiffPatternRule { Name = "example", Pattern = pattern, Description = "example ban" } };
        }

        private const string _Diff =
            "diff --git a/src/X.cs b/src/X.cs\n" +
            "--- a/src/X.cs\n" +
            "+++ b/src/X.cs\n" +
            "@@ -1,2 +1,4 @@\n" +
            " context line with BannedThing\n" +
            "-removed BannedThing line\n" +
            "+            var ok = DoBannedThing();\n" +
            "+            // BannedThing in a comment is fine\n";

        protected override Task RunTestsAsync()
        {
            RunTest("NoRules_IsNoOp", () =>
            {
                BannedDiffPatternResult r = BannedDiffPatternClassifier.Classify(_Diff, new List<BannedDiffPatternRule>());
                AssertFalse(r.HasBanned, "no rules means no guard");
            });

            RunTest("NullRules_IsNoOp", () =>
            {
                BannedDiffPatternResult r = BannedDiffPatternClassifier.Classify(_Diff, null);
                AssertFalse(r.HasBanned, "null rules means no guard");
            });

            RunTest("AddedCodeLine_Matches", () =>
            {
                BannedDiffPatternResult r = BannedDiffPatternClassifier.Classify(_Diff, Rules("BannedThing"));
                AssertTrue(r.HasBanned, "an added code line matching the pattern is a finding");
                AssertEqual(1, r.Findings.Count, "the comment and the removed/context lines do not match");
                AssertEqual("example", r.Findings[0].RuleName, "the finding names the rule");
            });

            RunTest("CommentAndNonAddedLines_DoNotMatch", () =>
            {
                // Only the one added code line matches; the context line, the removed line, and the
                // added comment line are all excluded.
                BannedDiffPatternResult r = BannedDiffPatternClassifier.Classify(_Diff, Rules("BannedThing"));
                AssertEqual(1, r.Findings.Count, "exactly one added non-comment line matches");
            });

            RunTest("InvalidRegex_IsSkippedNotThrown", () =>
            {
                BannedDiffPatternResult r = BannedDiffPatternClassifier.Classify(_Diff, Rules("("));
                AssertFalse(r.HasBanned, "a bad pattern matches nothing");
                AssertEqual(1, r.SkippedRules.Count, "and is reported as skipped");
            });

            RunTest("HotReload_CarriesBannedPatterns", () =>
            {
                ArmadaSettings current = new ArmadaSettings();
                AssertEqual(0, current.BannedDiffPatterns.Count, "empty by default");
                ArmadaSettings incoming = new ArmadaSettings();
                incoming.BannedDiffPatterns = new List<BannedDiffPatternRule> { new BannedDiffPatternRule { Name = "x", Pattern = "y", Description = "z" } };
                current.ApplyHotReloadableFrom(incoming);
                AssertEqual(1, current.BannedDiffPatterns.Count, "a hot reload carries the patterns");
                AssertEqual("x", current.BannedDiffPatterns[0].Name, "with their fields");
            });

            return Task.CompletedTask;
        }
    }
}
