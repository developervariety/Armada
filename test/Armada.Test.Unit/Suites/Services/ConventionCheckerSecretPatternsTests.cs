namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for <see cref="ConventionChecker.BuiltInSecretPatternStrings"/>, the surface
    /// that feeds the dock boundary hook config with CORE_RULE_5 secret patterns. Verifies
    /// the exported strings are usable regexes, actually detect known secrets, and agree
    /// with the server-side <see cref="ConventionChecker.CheckSecretLine"/> gate while
    /// excluding non-secret convention rules.
    /// </summary>
    public sealed class ConventionCheckerSecretPatternsTests : TestSuite
    {
        private const string _RsaHeader = "-----BEGIN RSA PRIVATE KEY-----";

        /// <summary>Suite name.</summary>
        public override string Name => "Convention Checker Secret Patterns";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("BuiltInSecretPatternStrings is non-empty", () =>
            {
                IReadOnlyList<string> patterns = ConventionChecker.BuiltInSecretPatternStrings;
                AssertNotNull(patterns, "Exported secret patterns must not be null");
                AssertTrue(patterns.Count > 0, "Exported secret patterns must not be empty");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Every exported secret pattern is a compilable regex", () =>
            {
                foreach (string pattern in ConventionChecker.BuiltInSecretPatternStrings)
                {
                    AssertTrue(pattern.Length > 0, "Exported pattern must be a non-empty string");
                    // Will throw if the exported string is not a valid regex.
                    Regex compiled = new Regex(pattern);
                    AssertNotNull(compiled, "Exported pattern must compile to a Regex");
                }
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("At least one exported pattern matches a known RSA private key header", () =>
            {
                bool anyMatch = false;
                foreach (string pattern in ConventionChecker.BuiltInSecretPatternStrings)
                {
                    if (new Regex(pattern).IsMatch(_RsaHeader)) { anyMatch = true; break; }
                }
                AssertTrue(anyMatch, "Exported patterns must detect a known private-key header so the hook config blocks it");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Exported patterns agree with CheckSecretLine on a secret line", () =>
            {
                // The server-side gate (CheckSecretLine) and the exported hook patterns must
                // agree that the same line is a secret, otherwise the dock hook and the gate diverge.
                IReadOnlyList<string> serverFired = ConventionChecker.CheckSecretLine(_RsaHeader);
                AssertTrue(serverFired.Count > 0, "CheckSecretLine must flag the RSA header as a secret");

                bool exportedMatch = false;
                foreach (string pattern in ConventionChecker.BuiltInSecretPatternStrings)
                {
                    if (new Regex(pattern).IsMatch(_RsaHeader)) { exportedMatch = true; break; }
                }
                AssertTrue(exportedMatch, "Exported hook patterns must flag the same line the server gate flags");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Exported patterns exclude non-secret convention rules (using Moq)", () =>
            {
                // CORE_RULE_2 (mocking-lib) is a convention rule, not a secret rule, and must not
                // leak into the secret-only hook config. A benign mocking-import line must match none.
                string mockingLine = "using Moq;";
                foreach (string pattern in ConventionChecker.BuiltInSecretPatternStrings)
                {
                    AssertFalse(new Regex(pattern).IsMatch(mockingLine),
                        "Non-secret convention rules must not appear among exported secret patterns");
                }
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }
}
