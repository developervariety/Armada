namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// The file list a mission description scopes a mission to, and the path forms the scope check compares.
    /// </summary>
    public sealed class MissionScopedFilesTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Mission Scoped Files";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Parse_DirectivePaths_DropLeadingDotSlashAndTrailingPeriod", () =>
            {
                HashSet<string> files = MissionScopedFiles.Parse("Touch only ./src/Alpha/Beta.cs, src\\Gamma\\Delta.cs.\nOther text.");
                AssertEqual(2, files.Count, "both directive paths are scoped");
                AssertTrue(files.Contains("src/Alpha/Beta.cs"), "a leading ./ is dropped");
                AssertTrue(files.Contains("SRC/gamma/delta.cs"), "a trailing period is dropped and backslashes become slashes, ignoring case");
                return Task.CompletedTask;
            });

            await RunTest("Parse_NoDirectiveOrEmptyDescription_ScopesNothing", () =>
            {
                AssertEqual(0, MissionScopedFiles.Parse("Implement the feature.").Count, "no directive means no scope");
                AssertEqual(0, MissionScopedFiles.Parse(null).Count, "a missing description means no scope");
                AssertEqual(0, MissionScopedFiles.Parse("Touch only ./.").Count, "a path with nothing left is skipped");
                return Task.CompletedTask;
            });

            await RunTest("NormalizePath_EmptyPath_ComparesAsEmpty", () =>
            {
                AssertEqual(String.Empty, MissionScopedFiles.NormalizePath("  "), "a blank changed path is empty, so the scope check skips it");
                AssertEqual("a/b.cs", MissionScopedFiles.NormalizePath(" a\\b.cs "), "a changed path is trimmed and uses forward slashes");
                return Task.CompletedTask;
            });
        }
    }
}
