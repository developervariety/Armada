namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Table-driven tests for the D3 <c>runtime_failure</c> adapter: Off keeps the rule with no call;
    /// unavailable and shadow/below-threshold keep the rule and record an event; at or above threshold
    /// only a bare Crash is upgraded to the more conservative UsageLimit or AuthFailure, a recognised
    /// signature is never downgraded, a crash is never downgraded to Clean, and the caller token
    /// reaches the client.
    /// </summary>
    public class TypedRuntimeFailureAdapterTests : TestSuite
    {
        public override string Name => "Typed Runtime Failure Adapter (D3)";

        private const string _Decision = "runtime_failure";

        private static TypedDecisionSettings BuildSettings(TypedDecisionModeEnum decisionMode, double threshold = 0.90)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
            settings.Decisions[_Decision] = new TypedDecisionRuleSettings { Mode = decisionMode, GateThreshold = threshold };
            return settings;
        }

        private static RuntimeFailureDecisionInput BuildInput()
        {
            return new RuntimeFailureDecisionInput
            {
                Mission = new Mission { Id = "msn_test", VesselId = "vsl_test", Title = "run a frame decode" },
                ExitCode = 1,
                Tail = "process ended non-zero",
                Runtime = "ClaudeCode",
                ModelId = "claude-fable-5"
            };
        }

        private static RuntimeFailureDecisionInput BuildInput(Captain captain)
        {
            return new RuntimeFailureDecisionInput
            {
                Mission = new Mission { Id = "msn_test", VesselId = "vsl_test", Title = "run a protocol decode" },
                ExitCode = 1,
                Tail = "process ended non-zero",
                Runtime = captain.Runtime.ToString(),
                ModelId = "claude-fable-5",
                KeyFamily = TypedRuntimeFailureAdapter.KeyFamilyOf(captain)
            };
        }

        private static TypedDecisionResult KindResult(string kind, double confidence, double fleetWide = 0.0)
        {
            return new TypedDecisionResult
            {
                Available = true,
                Answers = new Dictionary<string, TypedAnswer>
                {
                    ["kind"] = new TypedAnswer { Type = "choice", Choice = kind, Confidence = confidence },
                    ["fleet_wide"] = new TypedAnswer { Type = "noul", Noul = fleetWide }
                }
            };
        }

        private static async Task<int> CountEventsAsync(TestDatabase db, string eventType)
        {
            EnumerationResult<ArmadaEvent> events = await db.Driver.Events
                .EnumerateAsync(new EnumerationQuery { EventType = eventType, PageNumber = 1, PageSize = 100 })
                .ConfigureAwait(false);
            return events.Objects.Count;
        }

        private static TypedRuntimeFailureAdapter BuildAdapter(TestDatabase db, FakeTypedDecisionClient client, TypedDecisionSettings settings)
        {
            return new TypedRuntimeFailureAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings, new LoggingModule());
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("Off_ReturnsRule_NoCall", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(KindResult("usage_limit", 0.99));
                TypedRuntimeFailureAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Off));

                RuntimeFailureKindEnum result = await adapter.DecideAsync(BuildInput(), RuntimeFailureKindEnum.Crash, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(RuntimeFailureKindEnum.Crash, result, "Off must keep the rule");
                AssertEqual(0, client.CallCount);
            }).ConfigureAwait(false);

            await RunTest("Unavailable_ReturnsRule_RecordsUnavailable", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(new TypedDecisionResult { Available = false, UnavailableReason = "parse" });
                TypedRuntimeFailureAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                RuntimeFailureKindEnum result = await adapter.DecideAsync(BuildInput(), RuntimeFailureKindEnum.Crash, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(RuntimeFailureKindEnum.Crash, result, "unavailable must keep the rule");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("ShadowMode_ReturnsRule_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(KindResult("usage_limit", 0.99));
                TypedRuntimeFailureAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Shadow));

                RuntimeFailureKindEnum result = await adapter.DecideAsync(BuildInput(), RuntimeFailureKindEnum.Crash, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(RuntimeFailureKindEnum.Crash, result, "Shadow must keep the rule even at high confidence");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("GateBelowThreshold_ReturnsRule_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(KindResult("usage_limit", 0.50));
                TypedRuntimeFailureAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                RuntimeFailureKindEnum result = await adapter.DecideAsync(BuildInput(), RuntimeFailureKindEnum.Crash, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(RuntimeFailureKindEnum.Crash, result, "below threshold must keep the rule");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("GateAbove_UpgradesCrashToUsageLimit_RecordsGated", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(KindResult("usage_limit", 0.95, fleetWide: 0.95));
                TypedRuntimeFailureAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                RuntimeFailureKindEnum result = await adapter.DecideAsync(BuildInput(), RuntimeFailureKindEnum.Crash, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(RuntimeFailureKindEnum.UsageLimit, result, "a bare Crash reading as usage_limit must upgrade to UsageLimit");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("GateAbove_BillingHoldUpgradesCrashToUsageLimit", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(KindResult("billing_hold", 0.97));
                TypedRuntimeFailureAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                RuntimeFailureKindEnum result = await adapter.DecideAsync(BuildInput(), RuntimeFailureKindEnum.Crash, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(RuntimeFailureKindEnum.UsageLimit, result, "billing_hold must upgrade a Crash to UsageLimit");
            }).ConfigureAwait(false);

            await RunTest("GateAbove_UpgradesCrashToAuthFailure", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(KindResult("auth", 0.98));
                TypedRuntimeFailureAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                RuntimeFailureKindEnum result = await adapter.DecideAsync(BuildInput(), RuntimeFailureKindEnum.Crash, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(RuntimeFailureKindEnum.AuthFailure, result, "auth must upgrade a Crash to AuthFailure");
            }).ConfigureAwait(false);

            await RunTest("GateAbove_RecognisedUsageLimit_NotDowngraded", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(KindResult("clean", 0.99));
                TypedRuntimeFailureAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                RuntimeFailureKindEnum result = await adapter.DecideAsync(BuildInput(), RuntimeFailureKindEnum.UsageLimit, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(RuntimeFailureKindEnum.UsageLimit, result, "a recognised UsageLimit is authoritative and must not be downgraded");
            }).ConfigureAwait(false);

            await RunTest("GateAbove_CrashReadAsClean_StaysCrash", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(KindResult("clean", 0.99));
                TypedRuntimeFailureAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                RuntimeFailureKindEnum result = await adapter.DecideAsync(BuildInput(), RuntimeFailureKindEnum.Crash, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(RuntimeFailureKindEnum.Crash, result, "the model must never downgrade a Crash to Clean");
            }).ConfigureAwait(false);

            await RunTest("CallerTokenReachesClient", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(KindResult("usage_limit", 0.95));
                TypedRuntimeFailureAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                using CancellationTokenSource cts = new CancellationTokenSource();
                await adapter.DecideAsync(BuildInput(), RuntimeFailureKindEnum.Crash, cts.Token).ConfigureAwait(false);

                AssertTrue(client.LastToken == cts.Token, "adapter must forward the caller token");
                AssertEqual(_Decision, client.LastRequest!.DecisionPoint);
            }).ConfigureAwait(false);

            await RunTest("FleetWideAtThreshold_EmitsAccountFaultEventAndBroadcastNote_BenchesNothing", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Captain captain = await db.Driver.Captains.CreateAsync(new Captain("fleet-wide-captain") { ApiKey = "sk-should-never-appear-anywhere-0123456789" }).ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(KindResult("usage_limit", 0.95, fleetWide: 0.90));
                FakeBoardNotePoster poster = new FakeBoardNotePoster();
                TypedRuntimeFailureAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));
                adapter.NotePoster = poster;

                RuntimeFailureDecisionInput input = BuildInput(captain);
                RuntimeFailureKindEnum result = await adapter.DecideAsync(input, RuntimeFailureKindEnum.Crash, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(RuntimeFailureKindEnum.UsageLimit, result, "the verdict is still the combined kind; fleet-wide changes nothing about it");

                List<ArmadaEvent> faults = await db.Driver.Events.EnumerateByTypeAsync(TypedRuntimeFailureAdapter.AccountFaultSuspectedEventType, 10).ConfigureAwait(false);
                AssertEqual(1, faults.Count, "exactly one account-fault event");
                AssertContains("ClaudeCode/captain-key", faults[0].Message + faults[0].Payload, "the event names the key family");
                AssertFalse((faults[0].Message + faults[0].Payload).Contains("sk-should-never-appear", StringComparison.Ordinal), "the event never carries the key");

                AssertEqual(1, poster.Posts.Count, "exactly one broadcast board note");
                AssertContains("ClaudeCode/captain-key", poster.Posts[0], "the note names the key family");
                AssertFalse(poster.Posts[0].Contains("sk-should-never-appear", StringComparison.Ordinal), "the note never carries the key");

                Captain? after = await db.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                AssertEqual(CaptainStateEnum.Idle, after!.State, "no captain is benched from this path");
                AssertNull(after.QuarantineUntilUtc, "no quarantine is set from this path");
                AssertNull(after.QuarantineReason, "no quarantine reason is set from this path");
            }).ConfigureAwait(false);

            await RunTest("FleetWideBelowThreshold_EmitsNothing", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Captain captain = await db.Driver.Captains.CreateAsync(new Captain("local-fault-captain")).ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(KindResult("usage_limit", 0.95, fleetWide: 0.89));
                FakeBoardNotePoster poster = new FakeBoardNotePoster();
                TypedRuntimeFailureAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));
                adapter.NotePoster = poster;

                await adapter.DecideAsync(BuildInput(captain), RuntimeFailureKindEnum.Crash, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, await CountEventsAsync(db, TypedRuntimeFailureAdapter.AccountFaultSuspectedEventType).ConfigureAwait(false), "no account-fault event below 0.9");
                AssertEqual(0, poster.Posts.Count, "no board note below 0.9");
            }).ConfigureAwait(false);
        }
    }
}
