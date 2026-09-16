namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    public class TypedDecisionRecorderTests : TestSuite
    {
        public override string Name => "Typed Decision Recorder";

        private const string _StateSentinel = "sentinel-state-body-XYZ-never-store-me";

        private static TypedDecisionResult SampleResult(string? unavailable = null)
        {
            if (unavailable != null)
                return new TypedDecisionResult { Available = false, UnavailableReason = unavailable };

            Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>
            {
                ["cause"] = new TypedAnswer { Type = "choice", Choice = "environmental", Confidence = 0.94 }
            };
            return new TypedDecisionResult
            {
                Available = true,
                Answers = answers,
                InputTokens = 100,
                OutputTokens = 20,
                LatencyMs = 55
            };
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("RecordGatedAsync_WritesGatedEventWithHashNotState", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                TypedDecisionRecorder recorder = new TypedDecisionRecorder(testDb.Driver, new LoggingModule());

                TypedDecisionEventContext context = new TypedDecisionEventContext
                {
                    DecisionPoint = "failure_cause",
                    RuleVerdict = "Infra",
                    ModelVerdict = "environmental",
                    Confidence = 0.94,
                    Result = SampleResult(),
                    RedactedState = _StateSentinel
                };

                ArmadaEvent? evt = await recorder.RecordGatedAsync(context, CancellationToken.None).ConfigureAwait(false);

                AssertNotNull(evt);
                AssertEqual(TypedDecisionRecorder.EventTypeGated, evt!.EventType);
                AssertStartsWith("decision=failure_cause", evt.Message);
                AssertContains("model=environmental", evt.Message);

                AssertNotNull(evt.Payload);
                // The state itself must NEVER appear in the payload.
                AssertFalse(evt.Payload!.Contains(_StateSentinel, StringComparison.Ordinal), "raw state leaked into payload");

                RecorderPayload? payload = System.Text.Json.JsonSerializer.Deserialize<RecorderPayload>(evt.Payload!);
                AssertNotNull(payload);
                AssertEqual("failure_cause", payload!.Decision);
                AssertEqual("Infra", payload.RuleVerdict);
                AssertEqual("applied", payload.GateOutcome);
                AssertEqual(ExpectedSha(_StateSentinel), payload.StateSha256);
                AssertEqual(Encoding.UTF8.GetByteCount(_StateSentinel), payload.StateBytes);
                AssertEqual(100, payload.InputTokens);
                AssertEqual(20, payload.OutputTokens);
                AssertEqual(55, payload.LatencyMs);
            });

            await RunTest("RecordShadowAsync_BelowThreshold_WritesShadowEvent", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                TypedDecisionRecorder recorder = new TypedDecisionRecorder(testDb.Driver, new LoggingModule());

                TypedDecisionEventContext context = new TypedDecisionEventContext
                {
                    DecisionPoint = "refusal",
                    RuleVerdict = "refused_policy",
                    Result = SampleResult(),
                    RedactedState = "state"
                };

                ArmadaEvent? evt = await recorder.RecordShadowAsync(context, "below_threshold", CancellationToken.None).ConfigureAwait(false);

                AssertNotNull(evt);
                AssertEqual(TypedDecisionRecorder.EventTypeShadow, evt!.EventType);
                RecorderPayload? payload = System.Text.Json.JsonSerializer.Deserialize<RecorderPayload>(evt.Payload!);
                AssertEqual("below_threshold", payload!.GateOutcome);
            });

            await RunTest("RecordUnavailableAsync_WritesUnavailableEventWithReason", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                TypedDecisionRecorder recorder = new TypedDecisionRecorder(testDb.Driver, new LoggingModule());

                TypedDecisionEventContext context = new TypedDecisionEventContext
                {
                    DecisionPoint = "runtime_failure",
                    RuleVerdict = "Crash",
                    Result = SampleResult("timeout"),
                    RedactedState = "state"
                };

                ArmadaEvent? evt = await recorder.RecordUnavailableAsync(context, CancellationToken.None).ConfigureAwait(false);

                AssertNotNull(evt);
                AssertEqual(TypedDecisionRecorder.EventTypeUnavailable, evt!.EventType);
                AssertContains("unavailable=timeout", evt.Message);
                RecorderPayload? payload = System.Text.Json.JsonSerializer.Deserialize<RecorderPayload>(evt.Payload!);
                AssertEqual("unavailable", payload!.GateOutcome);
                AssertEqual("timeout", payload.UnavailableReason);
            });

            await RunTest("Recorder_WithMission_ScopesEventToMission", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                TypedDecisionRecorder recorder = new TypedDecisionRecorder(testDb.Driver, new LoggingModule());

                // tenant_id and user_id carry a foreign key to seeded tenants/users; use the
                // seeded defaults so the insert succeeds. vessel_id and voyage_id are plain columns.
                Mission mission = new Mission
                {
                    TenantId = Armada.Core.Constants.DefaultTenantId,
                    UserId = Armada.Core.Constants.DefaultUserId,
                    VesselId = "vsl_test",
                    VoyageId = "vyg_test"
                };

                TypedDecisionEventContext context = new TypedDecisionEventContext
                {
                    DecisionPoint = "failure_cause",
                    RuleVerdict = "Infra",
                    Result = SampleResult(),
                    RedactedState = "state",
                    Mission = mission
                };

                ArmadaEvent? evt = await recorder.RecordGatedAsync(context, CancellationToken.None).ConfigureAwait(false);

                AssertNotNull(evt);
                AssertEqual(mission.Id, evt!.MissionId);
                AssertEqual("vsl_test", evt.VesselId);
                AssertEqual("vyg_test", evt.VoyageId);
                AssertEqual(Armada.Core.Constants.DefaultTenantId, evt.TenantId);
            });

            await RunTest("Constructor_NullDatabase_Throws", () =>
            {
                AssertThrows<ArgumentNullException>(() =>
                {
                    TypedDecisionRecorder ignored = new TypedDecisionRecorder(null!, new LoggingModule());
                    GC.KeepAlive(ignored);
                });
            });
        }

        private static string ExpectedSha(string text)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private sealed class RecorderPayload
        {
            [JsonPropertyName("decision")]
            public string? Decision { get; set; }

            [JsonPropertyName("rule_verdict")]
            public string? RuleVerdict { get; set; }

            [JsonPropertyName("gate_outcome")]
            public string? GateOutcome { get; set; }

            [JsonPropertyName("state_sha256")]
            public string? StateSha256 { get; set; }

            [JsonPropertyName("state_bytes")]
            public int StateBytes { get; set; }

            [JsonPropertyName("input_tokens")]
            public int InputTokens { get; set; }

            [JsonPropertyName("output_tokens")]
            public int OutputTokens { get; set; }

            [JsonPropertyName("latency_ms")]
            public long LatencyMs { get; set; }

            [JsonPropertyName("unavailable_reason")]
            public string? UnavailableReason { get; set; }
        }
    }
}
