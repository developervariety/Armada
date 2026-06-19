namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using Armada.Core.Memory;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Tests that the canonical learned-facts file is protected by the built-in
    /// protected-paths gate and that the coaching message uses the correct proposal marker.
    /// </summary>
    public class ProtectedPathsLearnedFileTests : TestSuite
    {
        public override string Name => "Protected Paths Learned File";

        protected override async Task RunTestsAsync()
        {
            await RunTest("Root LEARNED.md trips built-in violation", () =>
            {
                List<string> changed = new List<string> { ".armada/LEARNED.md" };
                string? offending = ProtectedPathsValidator.FindFirstBuiltInOrConfiguredViolation(changed, null);
                AssertNotNull(offending, "Root LEARNED.md must be blocked");
                AssertEqual(".armada/LEARNED.md", offending);
            });

            await RunTest("Nested LEARNED.md trips built-in violation", () =>
            {
                List<string> changed = new List<string> { "sub/.armada/LEARNED.md" };
                string? offending = ProtectedPathsValidator.FindFirstBuiltInOrConfiguredViolation(changed, null);
                AssertNotNull(offending, "Nested LEARNED.md must be blocked");
                AssertEqual("sub/.armada/LEARNED.md", offending);
            });

            await RunTest("Mixed diff with LEARNED.md is blocked and path is named", () =>
            {
                List<string> changed = new List<string> { "src/Foo.cs", "sub/.armada/LEARNED.md", "docs/README.md" };
                string? offending = ProtectedPathsValidator.FindFirstBuiltInOrConfiguredViolation(changed, null);
                AssertNotNull(offending, "LEARNED.md must be the violation");
                AssertEqual("sub/.armada/LEARNED.md", offending);
            });

            await RunTest("Unrelated source file is not a built-in violation", () =>
            {
                List<string> changed = new List<string> { "src/Foo.cs" };
                string? offending = ProtectedPathsValidator.FindFirstBuiltInOrConfiguredViolation(changed, null);
                AssertNull(offending, "src/Foo.cs should not be protected");
            });

            await RunTest("FailureReason for LEARNED.md teaches [LEARNED-FACT-PROPOSAL]", () =>
            {
                string reason = ProtectedPathsValidator.FormatFailureReason(".armada/LEARNED.md", "demo-vessel");
                AssertContains(LearnedFactsFile.ProposalMarker, reason);
                AssertFalse(reason.Contains("[CLAUDE.MD-PROPOSAL]"), "LEARNED.md reason must not contain CLAUDE.MD marker");
                AssertContains("demo-vessel", reason);
                AssertContains(".armada/LEARNED.md", reason);
            });

            await RunTest("FailureReason for nested LEARNED.md teaches [LEARNED-FACT-PROPOSAL]", () =>
            {
                string reason = ProtectedPathsValidator.FormatFailureReason("sub/.armada/LEARNED.md", "demo-vessel");
                AssertContains(LearnedFactsFile.ProposalMarker, reason);
                AssertFalse(reason.Contains("[CLAUDE.MD-PROPOSAL]"), "Nested LEARNED.md reason must not contain CLAUDE.MD marker");
            });

            await RunTest("FailureReason for CLAUDE.md keeps [CLAUDE.MD-PROPOSAL]", () =>
            {
                string reason = ProtectedPathsValidator.FormatFailureReason("CLAUDE.md", "demo-vessel");
                AssertContains("[CLAUDE.MD-PROPOSAL]", reason);
                AssertFalse(reason.Contains(LearnedFactsFile.ProposalMarker), "CLAUDE.md reason must not contain learned marker");
            });

            await RunTest("Backslash-separated LEARNED.md is normalized and blocked", () =>
            {
                List<string> changed = new List<string> { "sub\\.armada\\LEARNED.md" };
                string? offending = ProtectedPathsValidator.FindFirstBuiltInOrConfiguredViolation(changed, null);
                AssertNotNull(offending, "Windows-separator LEARNED.md must be blocked");
                AssertEqual("sub/.armada/LEARNED.md", offending);
            });

            await RunTest("Leading ./ LEARNED.md is normalized and blocked", () =>
            {
                List<string> changed = new List<string> { "./.armada/LEARNED.md" };
                string? offending = ProtectedPathsValidator.FindFirstBuiltInOrConfiguredViolation(changed, null);
                AssertNotNull(offending, "Dot-slash prefixed LEARNED.md must be blocked");
                AssertEqual(".armada/LEARNED.md", offending);
            });

            await RunTest("Diff snapshot touching LEARNED.md extracts the path and trips the gate", () =>
            {
                // Realistic end-to-end: the orchestrator extracts changed files from a
                // captured unified diff, then feeds them to the protected-path gate.
                string diff =
                    "diff --git a/src/Foo.cs b/src/Foo.cs\n" +
                    "index 1111111..2222222 100644\n" +
                    "--- a/src/Foo.cs\n" +
                    "+++ b/src/Foo.cs\n" +
                    "@@ -1 +1 @@\n" +
                    "-old\n+new\n" +
                    "diff --git a/.armada/LEARNED.md b/.armada/LEARNED.md\n" +
                    "index 0000000..3333333 100644\n" +
                    "--- a/.armada/LEARNED.md\n" +
                    "+++ b/.armada/LEARNED.md\n" +
                    "@@ -1 +1 @@\n" +
                    "-x\n+y\n";

                IReadOnlyList<string> extracted = ProtectedPathsValidator.ExtractChangedFilesFromDiff(diff);
                AssertTrue(extracted.Contains(".armada/LEARNED.md"), "Diff extraction must surface the learned path");

                string? offending = ProtectedPathsValidator.FindFirstBuiltInOrConfiguredViolation(extracted, null);
                AssertNotNull(offending, "Extracted LEARNED.md must trip the gate");
                AssertEqual(".armada/LEARNED.md", offending);
            });

            await RunTest("Case-insensitive learned path still teaches [LEARNED-FACT-PROPOSAL]", () =>
            {
                // IsLearnedFactsPath uses an ordinal case-insensitive EndsWith, so a
                // lower/upper-cased path must still select the learned marker.
                string lower = ProtectedPathsValidator.FormatFailureReason(".armada/learned.md", "demo-vessel");
                AssertContains(LearnedFactsFile.ProposalMarker, lower);
                AssertFalse(lower.Contains("[CLAUDE.MD-PROPOSAL]"), "Lowercase learned path must not get CLAUDE marker");

                string upper = ProtectedPathsValidator.FormatFailureReason("SUB/.ARMADA/LEARNED.MD", "demo-vessel");
                AssertContains(LearnedFactsFile.ProposalMarker, upper);
                AssertFalse(upper.Contains("[CLAUDE.MD-PROPOSAL]"), "Uppercase learned path must not get CLAUDE marker");
            });

            await RunTest("Configured vessel paths coexist with built-in LEARNED.md protection", () =>
            {
                List<string> configured = new List<string> { "docs/secret/**" };

                // A vessel-configured path is still enforced.
                List<string> changedConfigured = new List<string> { "docs/secret/keys.txt" };
                string? configuredHit = ProtectedPathsValidator.FindFirstBuiltInOrConfiguredViolation(changedConfigured, configured);
                AssertEqual("docs/secret/keys.txt", configuredHit);

                // The built-in LEARNED.md is NOT clobbered by supplying a configured list.
                List<string> changedLearned = new List<string> { ".armada/LEARNED.md" };
                string? learnedHit = ProtectedPathsValidator.FindFirstBuiltInOrConfiguredViolation(changedLearned, configured);
                AssertEqual(".armada/LEARNED.md", learnedHit);

                // An unrelated path with both lists present is still clean.
                List<string> changedClean = new List<string> { "src/Foo.cs" };
                string? cleanHit = ProtectedPathsValidator.FindFirstBuiltInOrConfiguredViolation(changedClean, configured);
                AssertNull(cleanHit, "Unrelated path must pass even with configured + built-in lists");
            });
        }
    }
}
