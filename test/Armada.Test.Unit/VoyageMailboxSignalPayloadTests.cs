namespace Armada.Test.Unit
{
    using System.Text.Json;
    using Armada.Core.Models;
    using Armada.Test.Common;

    /// <summary>
    /// Tests for <see cref="VoyageMailboxSignalPayload"/> serialization and round-trip behavior.
    /// </summary>
    public sealed class VoyageMailboxSignalPayloadTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "VoyageMailboxSignalPayload";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Serializes_AllFields_ToExpectedCamelCaseJson", () =>
            {
                VoyageMailboxSignalPayload payload = new VoyageMailboxSignalPayload
                {
                    MissionId = "msn_abc",
                    VoyageId = "vyg_xyz",
                    Message = "hello world",
                    CreatedBy = "system"
                };

                JsonSerializerOptions opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                string json = JsonSerializer.Serialize(payload, opts);
                AssertContains("\"missionId\"", json, "MissionId should serialize as camelCase");
                AssertContains("\"voyageId\"", json, "VoyageId should serialize as camelCase");
                AssertContains("\"message\"", json, "Message should serialize as camelCase");
                AssertContains("\"createdBy\"", json, "CreatedBy should serialize as camelCase");
                AssertContains("\"msn_abc\"", json);
                AssertContains("\"vyg_xyz\"", json);
                AssertContains("\"hello world\"", json);
                AssertContains("\"system\"", json);
                return Task.CompletedTask;
            });

            await RunTest("Deserializes_FromCamelCaseJson_PreservesAllFields", () =>
            {
                string json = "{\"missionId\":\"msn_123\",\"voyageId\":\"vyg_456\",\"message\":\"test note\",\"createdBy\":\"user_789\"}";
                JsonSerializerOptions opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                VoyageMailboxSignalPayload? payload = JsonSerializer.Deserialize<VoyageMailboxSignalPayload>(json, opts);
                AssertNotNull(payload);
                AssertEqual("msn_123", payload!.MissionId);
                AssertEqual("vyg_456", payload.VoyageId);
                AssertEqual("test note", payload.Message);
                AssertEqual("user_789", payload.CreatedBy);
                return Task.CompletedTask;
            });

            await RunTest("Deserializes_WithNullOptionalFields_DoesNotThrow", () =>
            {
                string json = "{\"message\":\"only message\"}";
                JsonSerializerOptions opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                VoyageMailboxSignalPayload? payload = JsonSerializer.Deserialize<VoyageMailboxSignalPayload>(json, opts);
                AssertNotNull(payload);
                AssertNull(payload!.MissionId, "MissionId should be null when absent");
                AssertNull(payload.VoyageId, "VoyageId should be null when absent");
                AssertEqual("only message", payload.Message);
                AssertNull(payload.CreatedBy, "CreatedBy should be null when absent");
                return Task.CompletedTask;
            });

            await RunTest("RoundTrip_ThroughJsonString_PreservesAllFields", () =>
            {
                VoyageMailboxSignalPayload original = new VoyageMailboxSignalPayload
                {
                    MissionId = null,
                    VoyageId = "vyg_roundtrip",
                    Message = "voyage note",
                    CreatedBy = null
                };

                JsonSerializerOptions opts = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    PropertyNameCaseInsensitive = true
                };
                string json = JsonSerializer.Serialize(original, opts);
                VoyageMailboxSignalPayload? restored = JsonSerializer.Deserialize<VoyageMailboxSignalPayload>(json, opts);
                AssertNotNull(restored);
                AssertNull(restored!.MissionId);
                AssertEqual("vyg_roundtrip", restored.VoyageId);
                AssertEqual("voyage note", restored.Message);
                return Task.CompletedTask;
            });
        }
    }
}
