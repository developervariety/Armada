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
    /// Table-driven tests for the D6 papercut_merge adapter: Off, unavailable, below threshold, and
    /// above threshold, plus the rule-wins and bucketing guards. The deterministic behaviour (the
    /// plain grouping) is the fallback in every non-gate case.
    /// </summary>
    public class PapercutMergeAdapterTests : TestSuite
    {
        public override string Name => "Papercut Merge Adapter (D6)";

        private const string _Vessel = "vsl_example";

        protected override async Task RunTestsAsync()
        {
            // The table of scenarios exercised against a merge-able pair (same vessel, same category).
            List<MergeCase> cases = new List<MergeCase>
            {
                new MergeCase
                {
                    Name = "Off_ReturnsRuleUnmerged_NoCallNoEvent",
                    GlobalMode = TypedDecisionModeEnum.Off,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = FakeTypedDecisionClient.Noul("same_issue", 0.99),
                    ExpectMerged = false,
                    ExpectClientCalls = 0,
                    ExpectTypedEvent = null,
                    ExpectMergeProposed = false
                },
                new MergeCase
                {
                    Name = "Unavailable_ReturnsRuleUnmerged_UnavailableEvent",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = FakeTypedDecisionClient.Unavailable("http_429"),
                    ExpectMerged = false,
                    ExpectClientCalls = 1,
                    ExpectTypedEvent = TypedDecisionRecorder.EventTypeUnavailable,
                    ExpectMergeProposed = false
                },
                new MergeCase
                {
                    Name = "GateBelowThreshold_ReturnsRuleUnmerged_ShadowEvent",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = FakeTypedDecisionClient.Noul("same_issue", 0.50),
                    ExpectMerged = false,
                    ExpectClientCalls = 1,
                    ExpectTypedEvent = TypedDecisionRecorder.EventTypeShadow,
                    ExpectMergeProposed = false
                },
                new MergeCase
                {
                    Name = "GateAboveThreshold_MergesInListing_GatedAndProposedEvents",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = FakeTypedDecisionClient.Noul("same_issue", 0.97),
                    ExpectMerged = true,
                    ExpectClientCalls = 1,
                    ExpectTypedEvent = TypedDecisionRecorder.EventTypeGated,
                    ExpectMergeProposed = true
                },
                new MergeCase
                {
                    Name = "GateConfidenceWithoutNoul_ReturnsRuleUnmerged_ShadowEvent",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = FakeTypedDecisionClient.NoulConfidenceOnly("same_issue", 0.97),
                    ExpectMerged = false,
                    ExpectClientCalls = 1,
                    ExpectTypedEvent = TypedDecisionRecorder.EventTypeShadow,
                    ExpectMergeProposed = false
                },
                new MergeCase
                {
                    Name = "ShadowAboveThreshold_DoesNotMerge_ProposedButNotApplied",
                    GlobalMode = TypedDecisionModeEnum.Shadow,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = FakeTypedDecisionClient.Noul("same_issue", 0.97),
                    ExpectMerged = false,
                    ExpectClientCalls = 1,
                    ExpectTypedEvent = TypedDecisionRecorder.EventTypeShadow,
                    ExpectMergeProposed = true
                }
            };

            foreach (MergeCase testCase in cases)
            {
                await RunTest("Merge_" + testCase.Name, async () =>
                {
                    using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(testCase.Result);
                    PapercutMergeAdapter adapter = BuildAdapter(testDb.Driver, client, testCase.GlobalMode, testCase.DecisionMode);

                    List<PapercutGroup> input = new List<PapercutGroup>
                    {
                        Group("a", "stale sibling not found", 5),
                        Group("b", "sibling missing on run", 3)
                    };

                    List<PapercutGroup> output = await adapter.MergeAsync(input, CancellationToken.None).ConfigureAwait(false);

                    AssertEqual(testCase.ExpectClientCalls, client.Calls, "client call count");

                    if (testCase.ExpectMerged)
                    {
                        AssertEqual(1, output.Count, "the two groups merge into one row");
                        AssertEqual(8, output[0].Count, "merged count is the sum");
                        AssertTrue(output[0].MergedGroupKeys.Count >= 1, "the merged group records the folded key");
                    }
                    else
                    {
                        AssertEqual(2, output.Count, "the rule stands: two rows, unmerged");
                    }

                    List<ArmadaEvent> typed = await AllTypedDecisionEventsAsync(testDb.Driver).ConfigureAwait(false);
                    if (testCase.ExpectTypedEvent == null)
                    {
                        AssertEqual(0, typed.Count, "no typed-decision event when the decision is off");
                    }
                    else
                    {
                        AssertEqual(1, typed.Count, "exactly one typed-decision event for the pair");
                        AssertEqual(testCase.ExpectTypedEvent, typed[0].EventType, "typed-decision event type");
                    }

                    List<ArmadaEvent> proposed = await testDb.Driver.Events
                        .EnumerateByTypeAsync(PapercutMergeAdapter.MergeProposedEventType, 50)
                        .ConfigureAwait(false);
                    AssertEqual(testCase.ExpectMergeProposed ? 1 : 0, proposed.Count, "merge_proposed event count");
                });
            }

            await RunTest("Merge_DifferentCategories_NeverMerged", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("same_issue", 0.99));
                PapercutMergeAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                PapercutGroup one = Group("a", "same words here", 5);
                one.Category = PapercutCategoryEnum.BriefContradiction;
                PapercutGroup two = Group("b", "same words here", 4);
                two.Category = PapercutCategoryEnum.ToolFailure;

                List<PapercutGroup> output = await adapter.MergeAsync(new List<PapercutGroup> { one, two }, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(2, output.Count, "groups of different categories are in different buckets");
                AssertEqual(0, client.Calls, "no pair is even compared across categories");
            });

            await RunTest("Merge_DifferentVessels_NeverMerged", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("same_issue", 0.99));
                PapercutMergeAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                PapercutGroup one = Group("a", "same words here", 5);
                one.VesselId = "vsl_one";
                PapercutGroup two = Group("b", "same words here", 4);
                two.VesselId = "vsl_two";

                List<PapercutGroup> output = await adapter.MergeAsync(new List<PapercutGroup> { one, two }, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(2, output.Count, "groups of different vessels are never merged");
                AssertEqual(0, client.Calls, "no cross-vessel comparison");
            });

            await RunTest("Merge_ForwardsCallerTokenToClient", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("same_issue", 0.20));
                PapercutMergeAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                using CancellationTokenSource cts = new CancellationTokenSource();
                await adapter.MergeAsync(new List<PapercutGroup> { Group("a", "x y z", 2), Group("b", "x y z", 1) }, cts.Token).ConfigureAwait(false);

                AssertTrue(client.Calls >= 1, "the client was called");
                AssertTrue(client.LastToken.Equals(cts.Token), "the adapter forwards the caller's token so the client's timeout applies");
            });

            await RunTest("Merge_ThrowingClient_FailsClosedToRule_RecordsUnavailable_NoThrow", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = FakeTypedDecisionClient.Throwing(new InvalidOperationException("provider exploded"));
                PapercutMergeAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                List<PapercutGroup> output = new List<PapercutGroup>();
                Exception? escaped = null;
                try
                {
                    output = await adapter.MergeAsync(new List<PapercutGroup> { Group("a", "x y z", 2), Group("b", "x y z", 1) }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    escaped = ex;
                }

                AssertNull(escaped, "a throwing client never escapes the adapter");
                AssertEqual(2, output.Count, "the rule stands: two rows, unmerged");
                AssertEqual(1, client.Calls, "the client was consulted once, then the pass stopped");
                List<ArmadaEvent> unavailable = await testDb.Driver.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeUnavailable, 10).ConfigureAwait(false);
                AssertEqual(1, unavailable.Count, "one typed_decision.unavailable event");
                AssertEqual(0, (await testDb.Driver.Events.EnumerateByTypeAsync(PapercutMergeAdapter.MergeProposedEventType, 10).ConfigureAwait(false)).Count, "no merge proposed");
            });

            await RunTest("Merge_ThenUnavailable_ReturnsTheOriginalGroupsUnchanged", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                int call = 0;
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(_ =>
                {
                    call++;
                    return call == 1
                        ? FakeTypedDecisionClient.Noul("same_issue", 0.99)
                        : FakeTypedDecisionClient.Unavailable("http_429");
                });
                PapercutMergeAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                PapercutGroup largest = Group("a", "same words here", 5);
                PapercutGroup middle = Group("b", "same words here", 4);
                PapercutGroup smallest = Group("c", "other words here", 3);
                List<PapercutGroup> output = await adapter.MergeAsync(
                    new List<PapercutGroup> { largest, middle, smallest },
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(2, client.Calls, "the first comparison merged and the second was unavailable");
                AssertEqual(3, output.Count, "the unavailable fallback lists every original group");
                AssertEqual(12, output.Sum(g => g.Count), "the fallback reports each papercut once");
                PapercutGroup listedLargest = output.Single(g => g.Key == largest.Key);
                AssertEqual(5, listedLargest.Count, "the fallback carries the original count, not the partial merge");
                AssertEqual(0, listedLargest.MergedGroupKeys.Count, "the fallback carries no merge annotation");
                AssertEqual(5, largest.Count, "the caller's group is never modified");
                AssertEqual(0, largest.MergedGroupKeys.Count, "the caller's group gains no merge annotation");
            });

            await RunTest("Merge_Applied_LeavesTheCallersGroupsUnchanged", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("same_issue", 0.99));
                PapercutMergeAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                PapercutGroup larger = Group("a", "same words here", 5);
                PapercutGroup smaller = Group("b", "same words here", 4);
                List<PapercutGroup> output = await adapter.MergeAsync(
                    new List<PapercutGroup> { larger, smaller },
                    CancellationToken.None).ConfigureAwait(false);

                AssertEqual(1, output.Count, "the pair is merged");
                AssertEqual(9, output[0].Count, "the merged group carries both counts");
                AssertEqual(5, larger.Count, "the caller's group keeps its own count");
                AssertEqual(0, larger.MergedGroupKeys.Count, "the caller's group gains no merge annotation");
            });

            await RunTest("ListTool_PassesItsOwnTokenToTheMergeDecision", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("same_issue", 0.20));
                PapercutMergeAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                foreach (string title in new[] { "the sibling checkout is missing", "a completely different friction report" })
                {
                    Papercut papercut = new Papercut
                    {
                        Category = PapercutCategoryEnum.BriefContradiction,
                        Severity = PapercutSeverityEnum.Medium,
                        Title = title,
                        CaptainId = "cpt_example",
                        MissionId = "msn_example",
                        VesselId = _Vessel,
                        ReportedUtc = DateTime.UtcNow
                    };
                    await testDb.Driver.Events.CreateAsync(PapercutService.ToEvent(papercut)).ConfigureAwait(false);
                }

                using CancellationTokenSource toolCts = new CancellationTokenSource();
                await Armada.Server.Mcp.Tools.McpPapercutTools.ListAsync(null, testDb.Driver, adapter, toolCts.Token).ConfigureAwait(false);

                AssertTrue(client.Calls >= 1, "the listing consulted the merge decision");
                AssertTrue(client.LastToken.Equals(toolCts.Token), "the tool's own token reaches the client");
            });

            await RunTest("Merge_SingleGroup_ReturnsUnchanged_NoCall", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Noul("same_issue", 0.99));
                PapercutMergeAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                List<PapercutGroup> output = await adapter.MergeAsync(new List<PapercutGroup> { Group("a", "only one", 3) }, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(1, output.Count);
                AssertEqual(0, client.Calls, "a lone group has nothing to compare against");
            });
        }

        private static PapercutMergeAdapter BuildAdapter(DatabaseDriver database, FakeTypedDecisionClient client, TypedDecisionModeEnum globalMode, TypedDecisionModeEnum decisionMode)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = globalMode };
            settings.Decisions[PapercutMergeAdapter.DecisionPoint].Mode = decisionMode;
            settings.Decisions[PapercutMergeAdapter.DecisionPoint].GateThreshold = 0.90;
            TypedDecisionRecorder recorder = new TypedDecisionRecorder(database, new LoggingModule());
            return new PapercutMergeAdapter(settings, client, recorder, database, new LoggingModule());
        }

        private static PapercutGroup Group(string suffix, string title, int count)
        {
            return new PapercutGroup
            {
                Key = _Vessel + "|BriefContradiction|" + suffix,
                VesselId = _Vessel,
                Category = PapercutCategoryEnum.BriefContradiction,
                HighestSeverity = PapercutSeverityEnum.Medium,
                SampleTitle = title,
                SampleDetail = "detail " + suffix,
                Count = count,
                DistinctCaptainCount = count,
                FirstSeenUtc = DateTime.UtcNow.AddHours(-count),
                LastSeenUtc = DateTime.UtcNow.AddMinutes(-count)
            };
        }

        private static async Task<List<ArmadaEvent>> AllTypedDecisionEventsAsync(DatabaseDriver database)
        {
            List<ArmadaEvent> all = new List<ArmadaEvent>();
            all.AddRange(await database.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeGated, 50).ConfigureAwait(false));
            all.AddRange(await database.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeShadow, 50).ConfigureAwait(false));
            all.AddRange(await database.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeUnavailable, 50).ConfigureAwait(false));
            return all;
        }

        private sealed class MergeCase
        {
            public required string Name { get; init; }
            public required TypedDecisionModeEnum GlobalMode { get; init; }
            public required TypedDecisionModeEnum DecisionMode { get; init; }
            public required TypedDecisionResult Result { get; init; }
            public required bool ExpectMerged { get; init; }
            public required int ExpectClientCalls { get; init; }
            public required string? ExpectTypedEvent { get; init; }
            public required bool ExpectMergeProposed { get; init; }
        }
    }
}
