namespace Armada.Test.Unit
{
    using System.Text.Json;
    using Armada.Server.Mcp;
    using Armada.Test.Common;

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
                participantKey = new { type = "string" },
                afterUtc = new { type = "string", format = "date-time" },
                roomKey = new { type = "string" },
                status = new { type = "string", @enum = new[] { "Open", "Closed" } },
                includeFullContent = new { type = "boolean" },
                limit = new { type = "integer" },
                ratio = new { type = "number" },
                nullableFlag = new { type = new[] { "boolean", "null" } },
                tags = new { type = "array", items = new { type = "string" } }
            },
            required = new[] { "participantKey" }
        };

        /// <summary>Suite name.</summary>
        public override string Name => "McpToolArgumentNormalizer";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("EmptyString_ForDateTimeEnumBooleanIntegerArray_IsDropped", () =>
            {
                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    participantKey = "example-lead",
                    afterUtc = "",
                    status = "",
                    includeFullContent = "",
                    limit = "",
                    tags = ""
                }, _JsonOpts);

                JsonElement? result = McpToolArgumentNormalizer.Normalize(args, _Schema, _JsonOpts);
                AssertNotNull(result, "result present");
                JsonElement r = result!.Value;
                AssertTrue(r.TryGetProperty("participantKey", out _), "a real string value is kept");
                AssertFalse(r.TryGetProperty("afterUtc", out _), "empty date-time string is dropped");
                AssertFalse(r.TryGetProperty("status", out _), "empty enum-constrained string is dropped");
                AssertFalse(r.TryGetProperty("includeFullContent", out _), "empty boolean string is dropped");
                AssertFalse(r.TryGetProperty("limit", out _), "empty integer string is dropped");
                AssertFalse(r.TryGetProperty("tags", out _), "empty array string is dropped");
                return Task.CompletedTask;
            });

            await RunTest("EmptyString_ForOptionalPlainString_IsDropped_ForRequiredString_IsKept", () =>
            {
                // The live failure shape: afterUtc and roomKey are plain optional strings with no
                // format, sent as "" by a client that meant to omit them.
                JsonElement args = JsonSerializer.SerializeToElement(new { participantKey = "", roomKey = "" }, _JsonOpts);
                JsonElement? result = McpToolArgumentNormalizer.Normalize(args, _Schema, _JsonOpts);
                AssertFalse(result!.Value.TryGetProperty("roomKey", out _), "an optional plain string sent as \"\" is omitted");
                AssertTrue(result!.Value.TryGetProperty("participantKey", out JsonElement key), "a REQUIRED string keeps its empty value for the handler to reject");
                AssertEqual("", key.GetString(), "required value unchanged");
                return Task.CompletedTask;
            });

            await RunTest("StringSpelledBooleanAndNumbers_AreConverted", () =>
            {
                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    participantKey = "k",
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
                JsonElement args = JsonSerializer.SerializeToElement(new { participantKey = "k", limit = "many", includeFullContent = "yes" }, _JsonOpts);
                JsonElement r = McpToolArgumentNormalizer.Normalize(args, _Schema, _JsonOpts)!.Value;
                AssertEqual("many", r.GetProperty("limit").GetString(), "a non-numeric string stays a string");
                AssertEqual("yes", r.GetProperty("includeFullContent").GetString(), "a non-boolean string stays a string");
                return Task.CompletedTask;
            });

            await RunTest("TypedValuesAndUnknownProperties_PassThroughUntouched", () =>
            {
                JsonElement args = JsonSerializer.SerializeToElement(new
                {
                    participantKey = "k",
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
        }

        private static bool ReferenceEqualsOrSameJson(JsonElement left, JsonElement right)
        {
            return left.GetRawText() == right.GetRawText();
        }
    }
}
