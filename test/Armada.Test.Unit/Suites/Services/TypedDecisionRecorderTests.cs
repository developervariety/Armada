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
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
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
                return new TypedDecisionResult { Available = false, UnavailableReason = unavailable, UnavailableDetail = "provider said no" };

            Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>
            {
                ["cause"] = new TypedAnswer
                {
                    Type = "choice",
                    Choice = "environmental",
                    Confidence = 0.94,
                    Probabilities = new Dictionary<string, double> { ["environmental"] = 0.96, ["work_defect"] = 0.04 }
                }
            };
            return new TypedDecisionResult
            {
                Available = true,
                Answers = answers,
                Model = "jev-1.13.0",
                BatchSize = 3,
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
                AssertEqual("jev-1.13.0", payload.Model);
                AssertEqual(3, payload.BatchSize);
                AssertEqual(0.96, payload.Answers!["cause"].Probabilities!["environmental"]);
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
                AssertEqual("provider said no", payload.UnavailableDetail);
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

            await RunTest("A batch of three records estimated costs that split the request's cost by token share", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                TypedDecisionSettings settings = PricedSettings();
                TypedDecisionRecorder recorder = new TypedDecisionRecorder(testDb.Driver, new LoggingModule(), null, () => settings);

                SharedBatchClient client = new SharedBatchClient(itemCount: 3, inputTokens: 1001, outputTokens: 7);
                List<TypedDecisionBatchItem> items = new List<TypedDecisionBatchItem>();
                for (int i = 0; i < 3; i++)
                {
                    items.Add(new TypedDecisionBatchItem(
                        DecisionStateRedactor.RedactState(new Dictionary<string, object?> { ["item"] = i }, 1000),
                        new Dictionary<string, TypedQuestion> { ["cause"] = new NoulQuestion("Is it environmental?") }));
                }
                List<TypedDecisionResult> results = await TypedDecisionBatcher.DecideAllAsync(client, "failure_cause", items, 60000, CancellationToken.None).ConfigureAwait(false);
                AssertEqual(1, client.Calls, "the three items share one provider request");

                double total = 0;
                foreach (TypedDecisionResult result in results)
                {
                    ArmadaEvent? evt = await recorder.RecordShadowAsync(new TypedDecisionEventContext
                    {
                        DecisionPoint = "failure_cause",
                        RuleVerdict = "Infra",
                        Result = result,
                        RedactedState = "batched item"
                    }, "below_threshold", CancellationToken.None).ConfigureAwait(false);
                    CostPayload payload = System.Text.Json.JsonSerializer.Deserialize<CostPayload>(evt!.Payload!)!;
                    AssertNotNull(payload.EstimatedCostUsd, "a priced model records a cost");
                    AssertTrue(payload.CostIsEstimate, "the cost is marked estimated");
                    AssertEqual("prices-test-1", payload.PriceTableVersion, "the event names the price table version");
                    AssertEqual(Math.Round((result.InputTokens * 2.0 + result.OutputTokens * 8.0) / 1_000_000.0, 8), payload.EstimatedCostUsd!.Value, "each item is priced on its token share");
                    total += payload.EstimatedCostUsd!.Value;
                }

                double requestCost = (1001 * 2.0 + 7 * 8.0) / 1_000_000.0;
                AssertTrue(Math.Abs(requestCost - total) < 0.0000001, "the item costs add up to the request's cost: " + total + " vs " + requestCost);
            });

            await RunTest("A model with no price records a null cost with a named reason, never zero", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                TypedDecisionSettings settings = PricedSettings();
                TypedDecisionRecorder recorder = new TypedDecisionRecorder(testDb.Driver, new LoggingModule(), null, () => settings);

                TypedDecisionResult unpriced = new TypedDecisionResult { Available = true, Model = "other-model-2", InputTokens = 500, OutputTokens = 10 };
                ArmadaEvent? evt = await recorder.RecordShadowAsync(new TypedDecisionEventContext
                {
                    DecisionPoint = "failure_cause",
                    RuleVerdict = "Infra",
                    Result = unpriced,
                    RedactedState = "state"
                }, "below_threshold", CancellationToken.None).ConfigureAwait(false);
                CostPayload payload = System.Text.Json.JsonSerializer.Deserialize<CostPayload>(evt!.Payload!)!;
                AssertNull(payload.EstimatedCostUsd, "an unpriced model records no cost");
                AssertEqual(TypedDecisionCostEstimator.NoPriceForModelReason, payload.CostUnavailableReason, "the missing price is named");
                AssertContains("\"estimated_cost_usd\":null", evt.Payload!, "the cost is null, not zero");

                ArmadaEvent? unavailable = await recorder.RecordUnavailableAsync(new TypedDecisionEventContext
                {
                    DecisionPoint = "failure_cause",
                    RuleVerdict = "Infra",
                    Result = SampleResult("http_402"),
                    RedactedState = "state"
                }, CancellationToken.None).ConfigureAwait(false);
                CostPayload unavailablePayload = System.Text.Json.JsonSerializer.Deserialize<CostPayload>(unavailable!.Payload!)!;
                AssertNull(unavailablePayload.EstimatedCostUsd, "an unavailable call records no cost");
                AssertEqual(TypedDecisionCostEstimator.CallUnavailableReason, unavailablePayload.CostUnavailableReason, "the reason names the unavailable call");

                TypedDecisionRecorder unconfigured = new TypedDecisionRecorder(testDb.Driver, new LoggingModule());
                ArmadaEvent? noTable = await unconfigured.RecordShadowAsync(new TypedDecisionEventContext
                {
                    DecisionPoint = "failure_cause",
                    RuleVerdict = "Infra",
                    Result = SampleResult(),
                    RedactedState = "state"
                }, "below_threshold", CancellationToken.None).ConfigureAwait(false);
                CostPayload noTablePayload = System.Text.Json.JsonSerializer.Deserialize<CostPayload>(noTable!.Payload!)!;
                AssertNull(noTablePayload.EstimatedCostUsd, "with no price table no cost is recorded");
                AssertEqual(TypedDecisionCostEstimator.NoPriceForModelReason, noTablePayload.CostUnavailableReason, "the empty table is a missing price");
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

        // Prices the jev-1 family by prefix: 2 USD per million input tokens, 8 per million output tokens.
        private static TypedDecisionSettings PricedSettings()
        {
            TypedDecisionSettings settings = new TypedDecisionSettings();
            settings.Pricing = new TypedDecisionPriceTable
            {
                Version = "prices-test-1",
                Models = new Dictionary<string, TypedDecisionModelPrice>
                {
                    ["jev-1"] = new TypedDecisionModelPrice { InputUsdPerMillionTokens = 2.0, OutputUsdPerMillionTokens = 8.0 }
                }
            };
            return settings;
        }

        // Answers a batched request with one shared result the way the provider does: every item's answers
        // under its item prefix, and the request's whole token usage.
        private sealed class SharedBatchClient : ITypedDecisionClient
        {
            private readonly int _ItemCount;
            private readonly int _InputTokens;
            private readonly int _OutputTokens;

            public SharedBatchClient(int itemCount, int inputTokens, int outputTokens)
            {
                _ItemCount = itemCount;
                _InputTokens = inputTokens;
                _OutputTokens = outputTokens;
            }

            public int Calls { get; private set; }

            public Task<TypedDecisionResult> DecideAsync(TypedDecisionRequest request, CancellationToken token)
            {
                Calls++;
                Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal);
                for (int i = 1; i <= _ItemCount; i++)
                    answers["item" + i + "__cause"] = new TypedAnswer { Type = "noul", Noul = 0.2, Confidence = 0.2 };
                return Task.FromResult(new TypedDecisionResult
                {
                    Available = true,
                    Answers = answers,
                    Model = "jev-1.13.0",
                    InputTokens = _InputTokens,
                    OutputTokens = _OutputTokens,
                    LatencyMs = 30
                });
            }
        }

        private sealed class CostPayload
        {
            [JsonPropertyName("estimated_cost_usd")]
            public double? EstimatedCostUsd { get; set; }

            [JsonPropertyName("cost_is_estimate")]
            public bool CostIsEstimate { get; set; }

            [JsonPropertyName("cost_unavailable_reason")]
            public string? CostUnavailableReason { get; set; }

            [JsonPropertyName("price_table_version")]
            public string? PriceTableVersion { get; set; }
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

            [JsonPropertyName("unavailable_detail")]
            public string? UnavailableDetail { get; set; }

            [JsonPropertyName("model")]
            public string? Model { get; set; }

            [JsonPropertyName("batch_size")]
            public int BatchSize { get; set; }

            [JsonPropertyName("answers")]
            public Dictionary<string, RecorderAnswer>? Answers { get; set; }
        }

        private sealed class RecorderAnswer
        {
            [JsonPropertyName("probabilities")]
            public Dictionary<string, double>? Probabilities { get; set; }
        }
    }
}
