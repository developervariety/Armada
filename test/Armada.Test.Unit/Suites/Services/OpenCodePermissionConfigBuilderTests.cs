namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for OpenCodePermissionConfigBuilder: the bare-string
    /// external_directory grant, schema presence, LF line endings, valid JSON,
    /// and regression guards that the broken path-keyed map / subtree glob shape
    /// is never re-emitted.
    /// </summary>
    public class OpenCodePermissionConfigBuilderTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "OpenCode Permission Config Builder";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Build_ExternalDirectory_IsBareStringAllow", () =>
            {
                List<string> roots = new List<string> { "/work/repo-a", "/work/repo-b" };
                string json = OpenCodePermissionConfigBuilder.Build(roots);

                AssertContains("external_directory", json, "Output should carry the external_directory permission");
                AssertContains("\"external_directory\": \"allow\"", json, "external_directory must be a bare STRING set to allow");
                return Task.CompletedTask;
            });

            await RunTest("Build_ExternalDirectory_DeserializesAsString", () =>
            {
                // Structural guard: external_directory must deserialize as a JSON
                // string, never as an object/map. A path-keyed map would fail to bind
                // to a string property.
                OpenCodeDocument document = Deserialize(OpenCodePermissionConfigBuilder.Build(new List<string> { "/work/repo" }));

                AssertNotNull(document.Permission, "permission section should be present");
                AssertEqual("allow", document.Permission!.ExternalDirectory, "external_directory must be the bare string 'allow'");
                return Task.CompletedTask;
            });

            await RunTest("Build_DoesNotEmitPathKeyedMapOrSubtreeGlob", () =>
            {
                // Regression guard against the prior broken shape: the output must
                // NOT contain a path-keyed external_directory object nor any '/**'
                // subtree glob, both of which mis-matched on Windows.
                List<string> roots = new List<string> { "/work/repo", "C:\\Users\\dev\\proj" };
                string json = OpenCodePermissionConfigBuilder.Build(roots);

                AssertFalse(json.Contains("/**"), "Output must not contain a subtree glob ('/**')");
                AssertFalse(json.Contains("\"external_directory\": {"), "external_directory must not be a path-keyed object/map");
                AssertFalse(json.Contains("/work/repo"), "Supplied roots must not leak into the document as path keys");
                return Task.CompletedTask;
            });

            await RunTest("Build_NullOrEmptyRoots_StillEmitsBareStringGrant", () =>
            {
                // The grant no longer depends on the supplied roots: a null or empty
                // list still yields the bare-string allow grant.
                string fromEmpty = OpenCodePermissionConfigBuilder.Build(new List<string>());
                string fromNull = OpenCodePermissionConfigBuilder.Build(null!);

                AssertContains("\"external_directory\": \"allow\"", fromEmpty, "Empty input must still emit the bare-string grant");
                AssertContains("\"external_directory\": \"allow\"", fromNull, "Null input must still emit the bare-string grant");
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

            await RunTest("Build_Output_IsDeterministic", () =>
            {
                List<string> roots = new List<string> { "/work/zeta", "/work/alpha" };
                string first = OpenCodePermissionConfigBuilder.Build(roots);
                string second = OpenCodePermissionConfigBuilder.Build(roots);

                AssertEqual(first, second, "Build output must be deterministic for identical input");
                return Task.CompletedTask;
            });

            await RunTest("Build_EmitsExactBareStringDocument", () =>
            {
                // Golden-document guard: the mission requires the emitted opencode.json to be
                // EXACTLY the two-key bare-string document (LF endings, 2-space indent, $schema
                // then a permission section whose only member is the bare string
                // external_directory = "allow"). A single literal pins shape + ordering + value +
                // line endings so any drift back toward a path-keyed map, an added key, a changed
                // indent, or CRLF fails loudly. roots are intentionally varied (incl. a Windows
                // path) to prove they never influence the output.
                List<string> roots = new List<string> { "/work/repo", "C:\\Users\\dev\\proj" };
                string expected =
                    "{\n" +
                    "  \"$schema\": \"https://opencode.ai/config.json\",\n" +
                    "  \"permission\": {\n" +
                    "    \"external_directory\": \"allow\"\n" +
                    "  }\n" +
                    "}";

                string json = OpenCodePermissionConfigBuilder.Build(roots);

                AssertEqual(expected, json, "Emitted document must match the exact bare-string opencode.json byte-for-byte");
                return Task.CompletedTask;
            });

            await RunTest("Build_SchemaField_EmittedWithOpenCodeUrl", () =>
            {
                // The document must carry the $schema reference OpenCode tooling expects.
                OpenCodeDocument document = Deserialize(OpenCodePermissionConfigBuilder.Build(new List<string> { "/work/repo" }));

                AssertEqual("https://opencode.ai/config.json", document.Schema, "$schema must point at the OpenCode config schema");
                return Task.CompletedTask;
            });
        }

        private static OpenCodeDocument Deserialize(string json)
        {
            OpenCodeDocument? document = JsonSerializer.Deserialize<OpenCodeDocument>(json);
            if (document == null) throw new System.Exception("Emitted opencode.json deserialized to null");
            return document;
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

            /// <summary>Permission section carrying the external-directory grant.</summary>
            [JsonPropertyName("permission")]
            public OpenCodePermission? Permission { get; set; }
        }

        /// <summary>
        /// Strongly-typed mirror of the permission section of the emitted document.
        /// </summary>
        private sealed class OpenCodePermission
        {
            /// <summary>Bare-string external-directory grant ('allow').</summary>
            [JsonPropertyName("external_directory")]
            public string? ExternalDirectory { get; set; }
        }
    }
}
