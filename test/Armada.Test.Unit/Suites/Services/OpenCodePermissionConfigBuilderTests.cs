namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for OpenCodePermissionConfigBuilder: scoped external-directory grants,
    /// blanket-grant rejection, normalization/dedup/ordering, and valid JSON output.
    /// </summary>
    public class OpenCodePermissionConfigBuilderTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "OpenCode Permission Config Builder";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Build_EachSuppliedRoot_AppearsInPermissionJson", () =>
            {
                List<string> roots = new List<string> { "/work/repo-a", "/work/repo-b" };
                string json = OpenCodePermissionConfigBuilder.Build(roots);

                AssertContains("external_directory", json, "Output should carry the external_directory permission map");
                AssertContains("/work/repo-a", json, "First root should appear in the emitted JSON");
                AssertContains("/work/repo-b", json, "Second root should appear in the emitted JSON");
                AssertContains("\"allow\"", json, "Granted roots should map to the allow value");
                return Task.CompletedTask;
            });

            await RunTest("Build_NoBlanketWholeFilesystemAllowToken", () =>
            {
                // Even when callers pass blanket tokens, the builder must never emit
                // a whole-filesystem or whole-drive grant.
                List<string> roots = new List<string> { "*", "**", "/", "C:\\", "/work/repo" };
                string json = OpenCodePermissionConfigBuilder.Build(roots);

                AssertFalse(json.Contains("\"*\""), "Output must not contain a bare * allow token");
                AssertFalse(json.Contains("\"**\""), "Output must not contain a bare ** allow token");
                AssertFalse(json.Contains("\"/\""), "Output must not contain a filesystem-root allow token");
                AssertFalse(json.Contains("\"/**\""), "Output must not contain a filesystem-root subtree allow token");
                AssertFalse(json.Contains("\"C:\""), "Output must not contain a whole-drive allow token");
                AssertContains("/work/repo", json, "The genuine scoped root should still be granted");
                return Task.CompletedTask;
            });

            await RunTest("Build_NullEmptyDuplicateRoots_DroppedAndOrderingDeterministic", () =>
            {
                List<string> roots = new List<string>
                {
                    "/work/zeta",
                    null!,
                    "   ",
                    "/work/alpha",
                    "/work/zeta",
                    "/work/alpha/"
                };

                string first = OpenCodePermissionConfigBuilder.Build(roots);
                string second = OpenCodePermissionConfigBuilder.Build(roots);

                AssertEqual(first, second, "Build output must be deterministic for identical input");

                // alpha sorts before zeta (Ordinal); the trailing-slash duplicate of
                // alpha must collapse onto the same normalized root.
                int alphaIndex = first.IndexOf("/work/alpha", System.StringComparison.Ordinal);
                int zetaIndex = first.IndexOf("/work/zeta", System.StringComparison.Ordinal);
                AssertTrue(alphaIndex >= 0 && zetaIndex >= 0, "Both surviving roots should appear");
                AssertTrue(alphaIndex < zetaIndex, "Roots should be emitted in deterministic sorted order");

                // /work/alpha appears once as the literal root and once as the subtree
                // glob -- the duplicate "/work/alpha/" input must not add more.
                int literalCount = CountOccurrences(first, "\"/work/alpha\":");
                AssertEqual(1, literalCount, "Duplicate roots should collapse to a single literal entry");
                return Task.CompletedTask;
            });

            await RunTest("Build_EmptyInput_YieldsValidNoExtraGrantConfig", () =>
            {
                string fromEmpty = OpenCodePermissionConfigBuilder.Build(new List<string>());
                string fromNull = OpenCodePermissionConfigBuilder.Build(null!);

                AssertContains("external_directory", fromEmpty, "Empty input should still emit the permission scaffold");
                AssertFalse(fromEmpty.Contains("\"allow\""), "Empty input should grant nothing");
                AssertContains("external_directory", fromNull, "Null input should still emit the permission scaffold");
                AssertFalse(fromNull.Contains("\"allow\""), "Null input should grant nothing");
                return Task.CompletedTask;
            });

            await RunTest("Build_EmittedString_ParsesAsValidJson", () =>
            {
                List<string> roots = new List<string> { "/work/repo", "C:\\Users\\dev\\proj" };
                string json = OpenCodePermissionConfigBuilder.Build(roots);

                bool parsed = true;
                try
                {
                    using (JsonDocument.Parse(json)) { }
                }
                catch (JsonException)
                {
                    parsed = false;
                }

                AssertTrue(parsed, "Emitted opencode.json content must parse as valid JSON");
                AssertFalse(json.Contains("\r\n"), "Emitted JSON must use LF line endings");
                return Task.CompletedTask;
            });
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            int index = 0;
            while (true)
            {
                int found = haystack.IndexOf(needle, index, System.StringComparison.Ordinal);
                if (found < 0) break;
                count++;
                index = found + needle.Length;
            }
            return count;
        }
    }
}
