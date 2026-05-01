namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for the per-vessel protected-paths gate. Uses synthetic unified-diff
    /// strings as test doubles for git output -- the validator only consumes the
    /// changed-file path set, so no IGitService implementation is needed here.
    /// </summary>
    public class ProtectedPathsTests : TestSuite
    {
        public override string Name => "Protected Paths";

        protected override async Task RunTestsAsync()
        {
            await RunTest("Null ProtectedPaths -> any captain change lands (no violation)", () =>
            {
                string diff = MakeDiff("CLAUDE.md", "src/Foo.cs", "_briefing/notes.md");
                IReadOnlyList<string> changed = ProtectedPathsValidator.ExtractChangedFilesFromDiff(diff);
                AssertEqual(3, changed.Count);

                string? offending = ProtectedPathsValidator.FindFirstViolation(changed, null);
                AssertNull(offending, "Null protected list should never yield a violation");

                offending = ProtectedPathsValidator.FindFirstViolation(changed, new List<string>());
                AssertNull(offending, "Empty protected list should never yield a violation");
            });

            await RunTest("**/CLAUDE.md blocks a root-level CLAUDE.md change", () =>
            {
                string diff = MakeDiff("CLAUDE.md");
                IReadOnlyList<string> changed = ProtectedPathsValidator.ExtractChangedFilesFromDiff(diff);
                string? offending = ProtectedPathsValidator.FindFirstViolation(
                    changed, new List<string> { "**/CLAUDE.md" });
                AssertNotNull(offending, "Root CLAUDE.md must be blocked by **/CLAUDE.md");
                AssertEqual("CLAUDE.md", offending);
            });

            await RunTest("**/CLAUDE.md blocks a subdir/CLAUDE.md change", () =>
            {
                string diff = MakeDiff("subdir/CLAUDE.md");
                IReadOnlyList<string> changed = ProtectedPathsValidator.ExtractChangedFilesFromDiff(diff);
                string? offending = ProtectedPathsValidator.FindFirstViolation(
                    changed, new List<string> { "**/CLAUDE.md" });
                AssertNotNull(offending, "subdir/CLAUDE.md must be blocked by **/CLAUDE.md");
                AssertEqual("subdir/CLAUDE.md", offending);
            });

            await RunTest("**/CLAUDE.md ignores src/Foo.cs (no violation)", () =>
            {
                string diff = MakeDiff("src/Foo.cs");
                IReadOnlyList<string> changed = ProtectedPathsValidator.ExtractChangedFilesFromDiff(diff);
                string? offending = ProtectedPathsValidator.FindFirstViolation(
                    changed, new List<string> { "**/CLAUDE.md" });
                AssertNull(offending, "src/Foo.cs should not match **/CLAUDE.md");
            });

            await RunTest("_briefing/** and _skills/** block _briefing/foo.md", () =>
            {
                string diff = MakeDiff("_briefing/foo.md");
                IReadOnlyList<string> changed = ProtectedPathsValidator.ExtractChangedFilesFromDiff(diff);
                string? offending = ProtectedPathsValidator.FindFirstViolation(
                    changed, new List<string> { "_briefing/**", "_skills/**" });
                AssertNotNull(offending, "_briefing/foo.md must be blocked by _briefing/**");
                AssertEqual("_briefing/foo.md", offending);
            });

            await RunTest("Multi-file diff with one CLAUDE.md is blocked and the offending path is named", () =>
            {
                string diff = MakeDiff("src/Foo.cs", "src/Bar.cs", "CLAUDE.md", "tests/Baz.cs");
                IReadOnlyList<string> changed = ProtectedPathsValidator.ExtractChangedFilesFromDiff(diff);
                AssertEqual(4, changed.Count);

                string? offending = ProtectedPathsValidator.FindFirstViolation(
                    changed, new List<string> { "**/CLAUDE.md" });
                AssertNotNull(offending, "Mixed diff containing CLAUDE.md must be blocked");
                AssertEqual("CLAUDE.md", offending);

                string reason = ProtectedPathsValidator.FormatFailureReason(offending!, "demo-vessel");
                AssertContains("CLAUDE.md", reason);
                AssertContains("demo-vessel", reason);
            });

            await RunTest("FailureReason teaches the [CLAUDE.MD-PROPOSAL] convention", () =>
            {
                string reason = ProtectedPathsValidator.FormatFailureReason("CLAUDE.md", "demo-vessel");
                AssertContains("[CLAUDE.MD-PROPOSAL]", reason);
            });

            await RunTest("Built-in protected paths block CLAUDE.md without vessel configuration", () =>
            {
                string diff = MakeDiff("CLAUDE.md");
                IReadOnlyList<string> changed = ProtectedPathsValidator.ExtractChangedFilesFromDiff(diff);
                string? offending = ProtectedPathsValidator.FindFirstBuiltInOrConfiguredViolation(changed, null);
                AssertNotNull(offending, "Built-in protected paths should block CLAUDE.md");
                AssertEqual("CLAUDE.md", offending);
            });

            await RunTest("Built-in protected paths block _briefing without vessel configuration", () =>
            {
                string diff = MakeDiff("_briefing/spec.md");
                IReadOnlyList<string> changed = ProtectedPathsValidator.ExtractChangedFilesFromDiff(diff);
                string? offending = ProtectedPathsValidator.FindFirstBuiltInOrConfiguredViolation(changed, null);
                AssertNotNull(offending, "Built-in protected paths should block _briefing");
                AssertEqual("_briefing/spec.md", offending);
            });

            await RunTest("Vessel round-trips ProtectedPaths through SQLite", async () =>
            {
                using (TestHelpers.TestDatabase db = await TestHelpers.TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = new Vessel("rt-vessel", "https://example.invalid/repo.git");
                    vessel.ProtectedPaths = new List<string> { "**/CLAUDE.md", "_briefing/**" };
                    Vessel created = await db.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    AssertNotNull(created.ProtectedPaths);
                    AssertEqual(2, created.ProtectedPaths!.Count);

                    Vessel? read = await db.Driver.Vessels.ReadAsync(created.Id).ConfigureAwait(false);
                    AssertNotNull(read);
                    AssertNotNull(read!.ProtectedPaths);
                    AssertEqual(2, read.ProtectedPaths!.Count);
                    AssertEqual("**/CLAUDE.md", read.ProtectedPaths[0]);
                    AssertEqual("_briefing/**", read.ProtectedPaths[1]);
                }
            });

            await RunTest("Vessel stores null ProtectedPaths when not supplied", async () =>
            {
                using (TestHelpers.TestDatabase db = await TestHelpers.TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = new Vessel("nopp-vessel", "https://example.invalid/repo.git");
                    Vessel created = await db.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Vessel? read = await db.Driver.Vessels.ReadAsync(created.Id).ConfigureAwait(false);
                    AssertNotNull(read);
                    AssertNull(read!.ProtectedPaths, "ProtectedPaths should round-trip as null when not set");
                }
            });
        }

        /// <summary>
        /// Build a synthetic unified-diff blob containing one "diff --git" header
        /// per supplied path. The header text is the only thing the validator parses,
        /// so file content is omitted to keep tests focused on path detection.
        /// </summary>
        private static string MakeDiff(params string[] paths)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (string path in paths)
            {
                sb.Append("diff --git a/").Append(path).Append(" b/").Append(path).Append('\n');
                sb.Append("index 0000000..1111111 100644\n");
                sb.Append("--- a/").Append(path).Append('\n');
                sb.Append("+++ b/").Append(path).Append('\n');
                sb.Append("@@ -0,0 +1,1 @@\n");
                sb.Append("+placeholder\n");
            }
            return sb.ToString();
        }
    }
}
