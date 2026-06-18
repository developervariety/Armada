namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;
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

            await RunTest("Build_GenuineRoot_EmitsLiteralAndSubtreeGlobGrants", () =>
            {
                // Reasonable-trust scope means each root must grant both the literal
                // directory AND its subtree (<root>/**) -- read+build access to the
                // directory and everything beneath it, but nothing broader.
                List<string> roots = new List<string> { "/work/repo" };
                OpenCodeDocument document = Deserialize(OpenCodePermissionConfigBuilder.Build(roots));

                AssertNotNull(document.Permission, "permission section should be present");
                AssertNotNull(document.Permission!.ExternalDirectory, "external_directory map should be present");
                AssertTrue(document.Permission.ExternalDirectory!.ContainsKey("/work/repo"), "literal root must be granted");
                AssertTrue(document.Permission.ExternalDirectory.ContainsKey("/work/repo/**"), "subtree glob must be granted");
                AssertEqual(2, document.Permission.ExternalDirectory.Count, "exactly the literal + subtree entries should be emitted for one root");
                return Task.CompletedTask;
            });

            await RunTest("Build_AllGrantValues_AreAllow_NoDenyOrAsk", () =>
            {
                // Every emitted grant must be "allow"; the builder must never leak a
                // "deny"/"ask" value or any other permission token.
                List<string> roots = new List<string> { "/work/a", "/work/b", "/work/c" };
                OpenCodeDocument document = Deserialize(OpenCodePermissionConfigBuilder.Build(roots));

                AssertEqual(6, document.Permission!.ExternalDirectory!.Count, "three roots should yield six entries (literal + glob each)");
                foreach (KeyValuePair<string, string> entry in document.Permission.ExternalDirectory)
                {
                    AssertEqual("allow", entry.Value, "every grant value must be allow for key " + entry.Key);
                }
                return Task.CompletedTask;
            });

            await RunTest("Build_WindowsBackslashRoot_NormalizedToForwardSlashes", () =>
            {
                // A Windows-style path must be normalized to forward slashes so the
                // emitted keys are byte-stable across operating systems.
                List<string> roots = new List<string> { "C:\\Users\\dev\\proj" };
                string json = OpenCodePermissionConfigBuilder.Build(roots);
                OpenCodeDocument document = Deserialize(json);

                AssertFalse(json.Contains("\\\\"), "emitted JSON must not contain backslash path separators");
                AssertTrue(document.Permission!.ExternalDirectory!.ContainsKey("C:/Users/dev/proj"), "backslashes should be normalized to forward slashes");
                AssertTrue(document.Permission.ExternalDirectory.ContainsKey("C:/Users/dev/proj/**"), "normalized subtree glob should be granted");
                return Task.CompletedTask;
            });

            await RunTest("Build_MixedSeparatorDuplicates_CollapseToSingleRoot", () =>
            {
                // The same directory expressed with back- and forward-slashes must
                // dedupe to a single normalized root rather than two distinct grants.
                List<string> roots = new List<string> { "C:\\foo", "C:/foo" };
                OpenCodeDocument document = Deserialize(OpenCodePermissionConfigBuilder.Build(roots));

                AssertEqual(2, document.Permission!.ExternalDirectory!.Count, "mixed-separator duplicates must collapse to one root (literal + glob)");
                AssertTrue(document.Permission.ExternalDirectory.ContainsKey("C:/foo"), "collapsed literal root should be present");
                return Task.CompletedTask;
            });

            await RunTest("Build_SurroundingWhitespace_TrimmedFromRoot", () =>
            {
                // Leading/trailing whitespace must be trimmed; the inner path is kept.
                List<string> roots = new List<string> { "  /work/repo  " };
                OpenCodeDocument document = Deserialize(OpenCodePermissionConfigBuilder.Build(roots));

                AssertTrue(document.Permission!.ExternalDirectory!.ContainsKey("/work/repo"), "surrounding whitespace should be trimmed");
                AssertFalse(document.Permission.ExternalDirectory.ContainsKey("  /work/repo  "), "untrimmed key must not appear");
                AssertEqual(2, document.Permission.ExternalDirectory.Count, "trimmed root should yield exactly two entries");
                return Task.CompletedTask;
            });

            await RunTest("Build_DriveRootVariants_Dropped_ButSubdirKept", () =>
            {
                // A bare drive root (with or without a trailing separator) grants the
                // whole drive and must be dropped; a real subdirectory survives.
                List<string> roots = new List<string> { "C:\\", "C:/", "C:", "C:/foo" };
                OpenCodeDocument document = Deserialize(OpenCodePermissionConfigBuilder.Build(roots));

                AssertFalse(document.Permission!.ExternalDirectory!.ContainsKey("C:"), "bare drive root must not be granted");
                AssertFalse(document.Permission.ExternalDirectory.ContainsKey("C:/**"), "whole-drive subtree must not be granted");
                AssertTrue(document.Permission.ExternalDirectory.ContainsKey("C:/foo"), "a genuine subdirectory should still be granted");
                AssertEqual(2, document.Permission.ExternalDirectory.Count, "only the genuine subdirectory survives (literal + glob)");
                return Task.CompletedTask;
            });

            await RunTest("Build_SchemaField_EmittedWithOpenCodeUrl", () =>
            {
                // The document must carry the $schema reference OpenCode tooling expects.
                OpenCodeDocument document = Deserialize(OpenCodePermissionConfigBuilder.Build(new List<string> { "/work/repo" }));

                AssertEqual("https://opencode.ai/config.json", document.Schema, "$schema must point at the OpenCode config schema");
                return Task.CompletedTask;
            });

            await RunTest("Build_OnlyBlanketRoots_YieldEmptyGrantMap", () =>
            {
                // When every supplied root is a blanket token, the structural result
                // must be an empty grant map -- not merely free of the literal tokens.
                List<string> roots = new List<string> { "*", "**", "/", "/**", "C:\\", "  ", null! };
                OpenCodeDocument document = Deserialize(OpenCodePermissionConfigBuilder.Build(roots));

                AssertNotNull(document.Permission, "permission scaffold should still be emitted");
                AssertNotNull(document.Permission!.ExternalDirectory, "external_directory map should still be emitted");
                AssertEqual(0, document.Permission.ExternalDirectory!.Count, "no grants should survive when all roots are blanket");
                return Task.CompletedTask;
            });
        }

        private static OpenCodeDocument Deserialize(string json)
        {
            OpenCodeDocument? document = JsonSerializer.Deserialize<OpenCodeDocument>(json);
            if (document == null) throw new System.Exception("Emitted opencode.json deserialized to null");
            return document;
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

        /// <summary>
        /// Strongly-typed mirror of the emitted opencode.json document, used so the
        /// tests assert structure via deserialization rather than raw JsonElement
        /// access (per repo code style).
        /// </summary>
        private sealed class OpenCodeDocument
        {
            /// <summary>JSON schema reference emitted by the builder.</summary>
            [JsonPropertyName("$schema")]
            public string? Schema { get; set; }

            /// <summary>Permission section carrying the external-directory grants.</summary>
            [JsonPropertyName("permission")]
            public OpenCodePermission? Permission { get; set; }
        }

        /// <summary>
        /// Strongly-typed mirror of the permission section of the emitted document.
        /// </summary>
        private sealed class OpenCodePermission
        {
            /// <summary>Map of granted directory (or subtree glob) to permission value.</summary>
            [JsonPropertyName("external_directory")]
            public Dictionary<string, string>? ExternalDirectory { get; set; }
        }
    }
}
