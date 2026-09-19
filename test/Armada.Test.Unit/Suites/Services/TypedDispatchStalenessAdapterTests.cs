namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for the dispatch_staleness adapter and Combine clamp. Off makes no call and returns the
    /// rule; unavailable and below-threshold keep the rule; a gate at or above threshold may only
    /// demote a wait the rule already allowed. A Block rule always wins, and the model cannot Block
    /// when the policy is Proceed.
    /// </summary>
    public class TypedDispatchStalenessAdapterTests : TestSuite
    {
        public override string Name => "Typed Dispatch Staleness Adapter";

        private const double _Threshold = 0.90;

        protected override async Task RunTestsAsync()
        {
            await RunTest("Combine_BlockRuleAlwaysWins", () =>
            {
                AssertEqual(CodeIndexDispatchStalenessPolicyEnum.Block,
                    DispatchStalenessRules.Combine(CodeIndexDispatchStalenessPolicyEnum.Block, CodeIndexDispatchStalenessPolicyEnum.Proceed),
                    "a Block rule is never lifted");
                AssertEqual(CodeIndexDispatchStalenessPolicyEnum.Block,
                    DispatchStalenessRules.Combine(CodeIndexDispatchStalenessPolicyEnum.Block, CodeIndexDispatchStalenessPolicyEnum.RefreshInline),
                    "a Block rule is never demoted to RefreshInline");
                return Task.CompletedTask;
            });

            await RunTest("Combine_ModelCannotIntroduceAWaitTheRuleWouldNot", () =>
            {
                AssertEqual(CodeIndexDispatchStalenessPolicyEnum.Proceed,
                    DispatchStalenessRules.Combine(CodeIndexDispatchStalenessPolicyEnum.Proceed, CodeIndexDispatchStalenessPolicyEnum.Block),
                    "model Block is discarded when the policy is Proceed");
                AssertEqual(CodeIndexDispatchStalenessPolicyEnum.Proceed,
                    DispatchStalenessRules.Combine(CodeIndexDispatchStalenessPolicyEnum.Proceed, CodeIndexDispatchStalenessPolicyEnum.RefreshInline),
                    "model RefreshInline is discarded when the policy is Proceed");
                AssertEqual(CodeIndexDispatchStalenessPolicyEnum.RefreshInline,
                    DispatchStalenessRules.Combine(CodeIndexDispatchStalenessPolicyEnum.RefreshInline, CodeIndexDispatchStalenessPolicyEnum.Block),
                    "model Block is discarded when the policy is RefreshInline");
                AssertEqual(CodeIndexDispatchStalenessPolicyEnum.Proceed,
                    DispatchStalenessRules.Combine(CodeIndexDispatchStalenessPolicyEnum.RefreshInline, CodeIndexDispatchStalenessPolicyEnum.Proceed),
                    "RefreshInline may demote to Proceed");
                return Task.CompletedTask;
            });

            await RunTest("RuleVerdict_NotRelevantAlwaysProceeds", () =>
            {
                CodeIndexStalenessRelevance docsOnly = new CodeIndexStalenessRelevance
                {
                    IsStale = true,
                    IsRelevant = false,
                    DiffUnavailable = false,
                    ChangedFileCount = 2,
                    ChangedSourceFileCount = 0
                };
                AssertEqual(CodeIndexDispatchStalenessPolicyEnum.Proceed,
                    DispatchStalenessRules.RuleVerdict(CodeIndexDispatchStalenessPolicyEnum.Block, docsOnly, false),
                    "docs-only staleness Proceeds even under Block");
                return Task.CompletedTask;
            });

            await RunTest("Off makes no call and returns the rule verdict", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Reaction("block", 0.99));
                TypedDispatchStalenessAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Off, TypedDecisionModeEnum.Gate);

                CodeIndexDispatchStalenessPolicyEnum outcome = await adapter.DecideAsync(
                    Input(), CodeIndexDispatchStalenessPolicyEnum.Proceed, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, client.CallCount, "Off never calls the model");
                AssertEqual(CodeIndexDispatchStalenessPolicyEnum.Proceed, outcome, "the rule verdict is returned unchanged");
            });

            await RunTest("Unavailable keeps the rule verdict and records an unavailable event", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(TypedDecisionResult.Exception());
                TypedDispatchStalenessAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                CodeIndexDispatchStalenessPolicyEnum outcome = await adapter.DecideAsync(
                    Input(), CodeIndexDispatchStalenessPolicyEnum.RefreshInline, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(CodeIndexDispatchStalenessPolicyEnum.RefreshInline, outcome, "unavailable falls back to the rule verdict");
                List<ArmadaEvent> events = await TypedEventsAsync(testDb.Driver).ConfigureAwait(false);
                AssertTrue(events.Any(e => e.EventType == TypedDecisionRecorder.EventTypeUnavailable), "an unavailable event is recorded");
            });

            await RunTest("Below threshold records a shadow event and keeps the rule", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Reaction("proceed", 0.40));
                TypedDispatchStalenessAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                CodeIndexDispatchStalenessPolicyEnum outcome = await adapter.DecideAsync(
                    Input(), CodeIndexDispatchStalenessPolicyEnum.RefreshInline, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(CodeIndexDispatchStalenessPolicyEnum.RefreshInline, outcome, "below threshold, the rule stands");
                List<ArmadaEvent> events = await TypedEventsAsync(testDb.Driver).ConfigureAwait(false);
                AssertTrue(events.Any(e => e.EventType == TypedDecisionRecorder.EventTypeShadow), "a shadow event is recorded below threshold");
            });

            await RunTest("Gate at threshold demotes RefreshInline to Proceed and never lifts Block", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient proceedClient = new FakeTypedDecisionClient(Reaction("proceed", 0.95));
                TypedDispatchStalenessAdapter proceedAdapter = BuildAdapter(testDb.Driver, proceedClient, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);
                CodeIndexDispatchStalenessPolicyEnum demoted = await proceedAdapter.DecideAsync(
                    Input(), CodeIndexDispatchStalenessPolicyEnum.RefreshInline, CancellationToken.None).ConfigureAwait(false);
                AssertEqual(CodeIndexDispatchStalenessPolicyEnum.Proceed, demoted, "a confident Proceed may demote RefreshInline");

                FakeTypedDecisionClient blockClient = new FakeTypedDecisionClient(Reaction("proceed", 0.99));
                TypedDispatchStalenessAdapter blockAdapter = BuildAdapter(testDb.Driver, blockClient, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);
                CodeIndexDispatchStalenessPolicyEnum held = await blockAdapter.DecideAsync(
                    Input(), CodeIndexDispatchStalenessPolicyEnum.Block, CancellationToken.None).ConfigureAwait(false);
                AssertEqual(CodeIndexDispatchStalenessPolicyEnum.Block, held, "a Block rule always wins");

                FakeTypedDecisionClient escalateClient = new FakeTypedDecisionClient(Reaction("block", 0.99));
                TypedDispatchStalenessAdapter escalateAdapter = BuildAdapter(testDb.Driver, escalateClient, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);
                CodeIndexDispatchStalenessPolicyEnum clamped = await escalateAdapter.DecideAsync(
                    Input(), CodeIndexDispatchStalenessPolicyEnum.Proceed, CancellationToken.None).ConfigureAwait(false);
                AssertEqual(CodeIndexDispatchStalenessPolicyEnum.Proceed, clamped, "the model cannot Block when the policy is Proceed");

                List<ArmadaEvent> events = await TypedEventsAsync(testDb.Driver).ConfigureAwait(false);
                AssertTrue(events.Any(e => e.EventType == TypedDecisionRecorder.EventTypeGated), "a gated event is recorded");
            });

            await RunTest("State carries policy, relevance counts, title, and a description head, never hunks", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Reaction("proceed", 0.95));
                TypedDispatchStalenessAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);
                DispatchStalenessInput input = Input();
                input = new DispatchStalenessInput
                {
                    Policy = CodeIndexDispatchStalenessPolicyEnum.RefreshInline,
                    UpdateInProgress = false,
                    Relevance = new CodeIndexStalenessRelevance { IsStale = true, IsRelevant = true, ChangedFileCount = 4, ChangedSourceFileCount = 3 },
                    Title = "Rewrite the encoder checksum",
                    Description = "Search src/FrameEncoder.cs. " + new string('x', 80)
                };
                await adapter.DecideAsync(input, CodeIndexDispatchStalenessPolicyEnum.RefreshInline, CancellationToken.None).ConfigureAwait(false);
                string state = FakeTypedDecisionClient.StateText(client.LastRequest);
                AssertTrue(state.Contains("refresh_inline", StringComparison.Ordinal), "state names the policy");
                AssertTrue(state.Contains("changed_source_file_count", StringComparison.Ordinal), "state names the source-change count");
                AssertTrue(state.Contains("Rewrite the encoder checksum", StringComparison.Ordinal), "state carries the title");
                AssertFalse(state.Contains("diff --git", StringComparison.Ordinal), "state carries no hunks");
            });
        }

        private static DispatchStalenessInput Input()
            => new DispatchStalenessInput
            {
                Policy = CodeIndexDispatchStalenessPolicyEnum.Proceed,
                Relevance = new CodeIndexStalenessRelevance { IsStale = true, IsRelevant = true, ChangedSourceFileCount = 1 },
                Title = "Fix the encoder",
                Description = "Edit src/FrameEncoder.cs"
            };

        private static TypedDecisionResult Reaction(string choice, double confidence)
        {
            return new TypedDecisionResult
            {
                Available = true,
                Answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
                {
                    [TypedDispatchStalenessAdapter.QuestionId] = new TypedAnswer { Type = "choice", Choice = choice, Confidence = confidence }
                }
            };
        }

        private static TypedDispatchStalenessAdapter BuildAdapter(DatabaseDriver database, FakeTypedDecisionClient client, TypedDecisionModeEnum globalMode, TypedDecisionModeEnum decisionMode)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = globalMode };
            settings.Decisions["dispatch_staleness"].Mode = decisionMode;
            settings.Decisions["dispatch_staleness"].GateThreshold = _Threshold;
            TypedDecisionRecorder recorder = new TypedDecisionRecorder(database, new LoggingModule());
            return new TypedDispatchStalenessAdapter(client, recorder, settings, new LoggingModule());
        }

        private static async Task<List<ArmadaEvent>> TypedEventsAsync(DatabaseDriver database)
        {
            List<ArmadaEvent> all = new List<ArmadaEvent>();
            all.AddRange(await database.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeGated, 50).ConfigureAwait(false));
            all.AddRange(await database.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeShadow, 50).ConfigureAwait(false));
            all.AddRange(await database.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeUnavailable, 50).ConfigureAwait(false));
            return all;
        }
    }
}
