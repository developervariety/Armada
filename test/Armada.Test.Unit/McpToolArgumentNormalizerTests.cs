namespace Armada.Test.Unit
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for <see cref="McpToolArgumentNormalizer"/>: the schema-keyed tolerance applied once
    /// at the transport seam so a model-driven client's empty-string omissions and string-spelled
    /// booleans and numbers do not fail the whole call.
    /// </summary>
    public sealed class McpToolArgumentNormalizerTests : TestSuite
    {
        private static readonly JsonSerializerOptions _JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        private static readonly object _Schema = new
        {
            type = "object",
            properties = new
            {
                subjectId = new { type = "string" },
                afterUtc = new { type = "string", format = "date-time" },
                label = new { type = "string" },
                status = new { type = "string", @enum = new[] { "Open", "Closed" } },
                includeFullContent = new { type = "boolean" },
                limit = new { type = "integer" },
                ratio = new { type = "number" },
                nullableFlag = new { type = new[] { "boolean", "null" } },
                tags = new { type = "array", items = new { type = "string" } }
            },
            required = new[] { "subjectId" }
        };

        /// <summary>Suite name.</summary>
        public override string Name => "McpToolArgumentNormalizer";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("EmptyString_ForStringPropertyDeclaringEmptyStringClears_IsKept", () =>
            {
                object schema = new
                {
                    type = "object",
                    properties = new
                    {
                        secretOverride = new { type = "string", emptyStringClears = true },
                        plainOptional = new { type = "string" }
                    }
                };
                JsonElement args = JsonSerializer.SerializeToElement(new { secretOverride = "", plainOptional = "" }, _JsonOpts);

                JsonElement? result = McpToolArgumentNormalizer.Normalize(args, schema, _JsonOpts);
                AssertNotNull(result, "result present");
                JsonElement r = result!.Value;
                AssertTrue(r.TryGetProperty("secretOverride", out JsonElement kept), "an empty value for a property declaring emptyStringClears is kept");
                AssertEqual(string.Empty, kept.GetString(), "the kept value is still the empty string");
                AssertFalse(r.TryGetProperty("plainOptional", out _), "an optional string without the keyword is still treated as omitted");
            });

            await RunTest("EmptyString_ForDateTimeEnumBooleanIntegerArray_IsDropped", () =>
            {
                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    subjectId = "example-lead",
                    afterUtc = "",
                    status = "",
                    includeFullContent = "",
                    limit = "",
                    tags = ""
                }, _JsonOpts);

                JsonElement? result = McpToolArgumentNormalizer.Normalize(args, _Schema, _JsonOpts);
                AssertNotNull(result, "result present");
                JsonElement r = result!.Value;
                AssertTrue(r.TryGetProperty("subjectId", out _), "a real string value is kept");
                AssertFalse(r.TryGetProperty("afterUtc", out _), "empty date-time string is dropped");
                AssertFalse(r.TryGetProperty("status", out _), "empty enum-constrained string is dropped");
                AssertFalse(r.TryGetProperty("includeFullContent", out _), "empty boolean string is dropped");
                AssertFalse(r.TryGetProperty("limit", out _), "empty integer string is dropped");
                AssertFalse(r.TryGetProperty("tags", out _), "empty array string is dropped");
                return Task.CompletedTask;
            });

            await RunTest("EmptyString_ForOptionalPlainString_IsDropped_ForRequiredString_IsKept", () =>
            {
                // The live failure shape: afterUtc and label are plain optional strings with no
                // format, sent as "" by a client that meant to omit them.
                JsonElement args = JsonSerializer.SerializeToElement(new { subjectId = "", label = "" }, _JsonOpts);
                JsonElement? result = McpToolArgumentNormalizer.Normalize(args, _Schema, _JsonOpts);
                AssertFalse(result!.Value.TryGetProperty("label", out _), "an optional plain string sent as \"\" is omitted");
                AssertTrue(result!.Value.TryGetProperty("subjectId", out JsonElement key), "a REQUIRED string keeps its empty value for the handler to reject");
                AssertEqual("", key.GetString(), "required value unchanged");
                return Task.CompletedTask;
            });

            await RunTest("StringSpelledBooleanAndNumbers_AreConverted", () =>
            {
                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    subjectId = "k",
                    includeFullContent = "true",
                    limit = "12",
                    ratio = "0.5",
                    nullableFlag = "false"
                }, _JsonOpts);
                JsonElement r = McpToolArgumentNormalizer.Normalize(args, _Schema, _JsonOpts)!.Value;
                AssertEqual(JsonValueKind.True, r.GetProperty("includeFullContent").ValueKind, "\"true\" becomes boolean true");
                AssertEqual(12L, r.GetProperty("limit").GetInt64(), "\"12\" becomes integer 12");
                AssertEqual(0.5, r.GetProperty("ratio").GetDouble(), "\"0.5\" becomes number 0.5");
                AssertEqual(JsonValueKind.False, r.GetProperty("nullableFlag").ValueKind, "[\"boolean\",\"null\"] type is read as boolean");
                return Task.CompletedTask;
            });

            await RunTest("UnconvertibleString_IsLeftForTheHandlerToReject", () =>
            {
                JsonElement args = JsonSerializer.SerializeToElement(new { subjectId = "k", limit = "many", includeFullContent = "yes" }, _JsonOpts);
                JsonElement r = McpToolArgumentNormalizer.Normalize(args, _Schema, _JsonOpts)!.Value;
                AssertEqual("many", r.GetProperty("limit").GetString(), "a non-numeric string stays a string");
                AssertEqual("yes", r.GetProperty("includeFullContent").GetString(), "a non-boolean string stays a string");
                return Task.CompletedTask;
            });

            await RunTest("TypedValuesAndUnknownProperties_PassThroughUntouched", () =>
            {
                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    subjectId = "k",
                    includeFullContent = true,
                    limit = 3,
                    afterUtc = "2026-08-25T00:00:00Z",
                    somethingElse = ""
                }, _JsonOpts);
                JsonElement? result = McpToolArgumentNormalizer.Normalize(args, _Schema, _JsonOpts);
                AssertTrue(result.HasValue, "result present");
                JsonElement r = result!.Value;
                AssertEqual(JsonValueKind.True, r.GetProperty("includeFullContent").ValueKind, "typed boolean unchanged");
                AssertEqual(3L, r.GetProperty("limit").GetInt64(), "typed integer unchanged");
                AssertEqual("2026-08-25T00:00:00Z", r.GetProperty("afterUtc").GetString(), "real date-time unchanged");
                AssertEqual("", r.GetProperty("somethingElse").GetString(), "a property the schema does not know is passed through");
                AssertTrue(ReferenceEqualsOrSameJson(args, r), "an unchanged document is returned as-is");
                return Task.CompletedTask;
            });

            await RunTest("NullArgumentsOrNoSchema_ReturnInputUnchanged", () =>
            {
                AssertTrue(McpToolArgumentNormalizer.Normalize(null, _Schema, _JsonOpts) == null, "null arguments stay null");
                JsonElement args = JsonSerializer.SerializeToElement(new { afterUtc = "" }, _JsonOpts);
                JsonElement r = McpToolArgumentNormalizer.Normalize(args, (object?)null, _JsonOpts)!.Value;
                AssertTrue(r.TryGetProperty("afterUtc", out _), "without a schema nothing is dropped");
                return Task.CompletedTask;
            });

            await RunTest("EmptyString_ForPropertyDeclaringEmptyStringClears_IsKept", () =>
            {
                object schema = new
                {
                    type = "object",
                    properties = new
                    {
                        clearable = new { type = "string", emptyStringClears = true },
                        plain = new { type = "string" }
                    }
                };
                JsonElement args = JsonSerializer.SerializeToElement(new { clearable = "", plain = "" }, _JsonOpts);
                JsonElement r = McpToolArgumentNormalizer.Normalize(args, schema, _JsonOpts)!.Value;
                AssertTrue(r.TryGetProperty("clearable", out JsonElement kept), "\"\" reaches the handler when the schema says it clears");
                AssertEqual("", kept.GetString(), "the clearing value is unchanged");
                AssertFalse(r.TryGetProperty("plain", out _), "an optional string without the keyword is still omitted");
                return Task.CompletedTask;
            });

            await RunTest("UpdateCaptainSchema_EmptyStringForDocumentedClearingFields_IsKept", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    object? schema = null;
                    McpCaptainTools.Register(
                        (name, _, inputSchema, _) => { if (name == "armada_update_captain") schema = inputSchema; },
                        testDb.Driver,
                        null!,
                        null);
                    AssertNotNull(schema, "armada_update_captain must be registered with a schema");

                    JsonElement args = JsonSerializer.SerializeToElement(new
                    {
                        captainId = "cpt_example",
                        apiKey = "",
                        apiBaseUrl = "",
                        muxConfigDirectory = "",
                        muxEndpoint = "",
                        muxBaseUrl = "",
                        muxAdapterType = "",
                        muxSystemPromptPath = "",
                        muxApprovalPolicy = "",
                        reasoningEffort = "",
                        preferredPersona = ""
                    }, _JsonOpts);
                    JsonElement r = McpToolArgumentNormalizer.Normalize(args, schema, _JsonOpts)!.Value;
                    foreach (string clearing in new[] { "apiKey", "apiBaseUrl", "muxConfigDirectory", "muxEndpoint", "muxBaseUrl", "muxAdapterType", "muxSystemPromptPath", "muxApprovalPolicy", "reasoningEffort" })
                    {
                        AssertTrue(r.TryGetProperty(clearing, out JsonElement value), clearing + " sent as \"\" must reach the handler, whose schema says \"\" clears it");
                        AssertEqual("", value.GetString(), clearing + " keeps its clearing value");
                    }
                    AssertFalse(r.TryGetProperty("preferredPersona", out _), "a field whose schema gives \"\" no meaning is still omitted");
                }
            });

            await RunTest("CaptainWriteSchemas_ExposeBoundedMuxMaxTurns", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Dictionary<string, JsonElement> schemas = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    McpCaptainTools.Register(
                        (name, _, inputSchema, _) =>
                        {
                            if (name == "armada_create_captain" || name == "armada_update_captain")
                                schemas[name] = JsonSerializer.SerializeToElement(inputSchema, _JsonOpts);
                        },
                        testDb.Driver,
                        null!,
                        null);

                    foreach (string toolName in new[] { "armada_create_captain", "armada_update_captain" })
                    {
                        AssertTrue(schemas.TryGetValue(toolName, out JsonElement schema), toolName + " must be registered");
                        AssertTrue(schema.GetProperty("properties").TryGetProperty("muxMaxTurns", out JsonElement turns), toolName + " exposes muxMaxTurns");
                        AssertEqual(1, turns.GetProperty("minimum").GetInt32(), toolName + " publishes the minimum turn cap");
                        AssertEqual(100, turns.GetProperty("maximum").GetInt32(), toolName + " publishes the maximum turn cap");
                    }
                }
            });

            await RunTest("SnakeOrKebabCaseArgumentName_IsRenamedToSchemaProperty", () =>
            {
                object schema = new
                {
                    type = "object",
                    properties = new
                    {
                        missionId = new { type = "string" },
                        includeDescription = new { type = "boolean" }
                    },
                    required = new[] { "missionId" }
                };
                JsonElement args = JsonSerializer.SerializeToElement(new Dictionary<string, object>
                {
                    ["mission_id"] = "msn_example",
                    ["include-description"] = "true"
                });

                JsonElement? result = McpToolArgumentNormalizer.Normalize(args, schema, _JsonOpts);
                AssertNotNull(result, "result present");
                JsonElement r = result!.Value;
                AssertTrue(r.TryGetProperty("missionId", out JsonElement missionId), "mission_id is renamed to missionId");
                AssertEqual("msn_example", missionId.GetString(), "the renamed value is kept");
                AssertFalse(r.TryGetProperty("mission_id", out _), "the snake_case name is not passed through as well");
                AssertTrue(r.TryGetProperty("includeDescription", out JsonElement include), "include-description is renamed to includeDescription");
                AssertEqual(JsonValueKind.True, include.ValueKind, "the renamed value is still normalised by its schema type");

                MissionStatusArgs parsed = JsonSerializer.Deserialize<MissionStatusArgs>(r, _JsonOpts)!;
                AssertEqual("msn_example", parsed.MissionId, "the handler's typed arguments receive the mission id");
            });

            await RunTest("SnakeCaseArgumentName_DoesNotOverrideSchemaNameAlsoSupplied", () =>
            {
                object schema = new
                {
                    type = "object",
                    properties = new { missionId = new { type = "string" } }
                };
                JsonElement args = JsonSerializer.SerializeToElement(new Dictionary<string, object>
                {
                    ["missionId"] = "msn_primary",
                    ["mission_id"] = "msn_other"
                });

                JsonElement? result = McpToolArgumentNormalizer.Normalize(args, schema, _JsonOpts);
                JsonElement r = result!.Value;
                AssertEqual("msn_primary", r.GetProperty("missionId").GetString(), "the schema-named value wins");
                AssertTrue(r.TryGetProperty("mission_id", out _), "the unmatched snake_case value passes through untouched");
            });
        }

        private static bool ReferenceEqualsOrSameJson(JsonElement left, JsonElement right)
        {
            return left.GetRawText() == right.GetRawText();
        }
    }
}
