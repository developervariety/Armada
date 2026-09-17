namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
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
    /// Table-driven tests for the D18 memory_candidate adapter: Off, unavailable, below threshold,
    /// above threshold, and the not_memory and shadow guards. The deterministic behaviour (no
    /// nomination, no memory written) is the fallback in every non-gate case; the model never writes
    /// memory, only a proposal.
    /// </summary>
    public class MemoryCandidateAdapterTests : TestSuite
    {
        public override string Name => "Memory Candidate Adapter (D18)";

        protected override async Task RunTestsAsync()
        {
            List<CandidateCase> cases = new List<CandidateCase>
            {
                new CandidateCase
                {
                    Name = "Off_NoNomination_NoCallNoEvent",
                    GlobalMode = TypedDecisionModeEnum.Off,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = FakeTypedDecisionClient.NoulAndChoice("durable_lesson", 0.99, "scope", "shared"),
                    ExpectNominated = false,
                    ExpectClientCalls = 0,
                    ExpectTypedEvent = null,
                    ExpectWritten = false
                },
                new CandidateCase
                {
                    Name = "Unavailable_NoNomination_UnavailableEvent",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = FakeTypedDecisionClient.Unavailable("timeout"),
                    ExpectNominated = false,
                    ExpectClientCalls = 1,
                    ExpectTypedEvent = TypedDecisionRecorder.EventTypeUnavailable,
                    ExpectWritten = false
                },
                new CandidateCase
                {
                    Name = "GateBelowThreshold_NoNomination_ShadowEvent",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = FakeTypedDecisionClient.NoulAndChoice("durable_lesson", 0.40, "scope", "shared"),
                    ExpectNominated = false,
                    ExpectClientCalls = 1,
                    ExpectTypedEvent = TypedDecisionRecorder.EventTypeShadow,
                    ExpectWritten = false
                },
                new CandidateCase
                {
                    Name = "GateDurableButNotMemory_NoNomination_ShadowEvent",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = FakeTypedDecisionClient.NoulAndChoice("durable_lesson", 0.99, "scope", "not_memory"),
                    ExpectNominated = false,
                    ExpectClientCalls = 1,
                    ExpectTypedEvent = TypedDecisionRecorder.EventTypeShadow,
                    ExpectWritten = false
                },
                new CandidateCase
                {
                    Name = "GateAboveThreshold_Nominates_GatedEventAndProposalWritten",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = FakeTypedDecisionClient.NoulAndChoice("durable_lesson", 0.95, "scope", "shared"),
                    ExpectNominated = true,
                    ExpectClientCalls = 1,
                    ExpectTypedEvent = TypedDecisionRecorder.EventTypeGated,
                    ExpectWritten = true
                },
                new CandidateCase
                {
                    Name = "GateConfidenceWithoutNoul_NoNomination_ShadowEvent",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = DurableConfidenceOnly(0.95),
                    ExpectNominated = false,
                    ExpectClientCalls = 1,
                    ExpectTypedEvent = TypedDecisionRecorder.EventTypeShadow,
                    ExpectWritten = false
                },
                new CandidateCase
                {
                    Name = "ShadowAboveThreshold_NoNomination_NoProposal",
                    GlobalMode = TypedDecisionModeEnum.Shadow,
                    DecisionMode = TypedDecisionModeEnum.Shadow,
                    Result = FakeTypedDecisionClient.NoulAndChoice("durable_lesson", 0.95, "scope", "shared"),
                    ExpectNominated = false,
                    ExpectClientCalls = 1,
                    ExpectTypedEvent = TypedDecisionRecorder.EventTypeShadow,
                    ExpectWritten = false
                }
            };

            foreach (CandidateCase testCase in cases)
            {
                await RunTest("Nominate_" + testCase.Name, async () =>
                {
                    using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(testCase.Result);
                    RecordingProposalWriter writer = new RecordingProposalWriter();
                    MemoryCandidateAdapter adapter = BuildAdapter(testDb.Driver, client, writer, testCase.GlobalMode, testCase.DecisionMode);

                    List<PapercutGroup> input = new List<PapercutGroup> { Group("k1", "a recurring cross-tool lesson", 9) };

                    List<MemoryCandidateProposal> nominated = await adapter.NominateAsync(input, CancellationToken.None).ConfigureAwait(false);

                    AssertEqual(testCase.ExpectClientCalls, client.Calls, "client call count");
                    AssertEqual(testCase.ExpectNominated ? 1 : 0, nominated.Count, "nomination count");
                    AssertEqual(testCase.ExpectWritten ? 1 : 0, writer.Written.Count, "proposal writer call count");

                    if (testCase.ExpectWritten)
                    {
                        AssertEqual("shared", writer.Written[0].Scope, "scope carried to proposal");
                        AssertEqual(9, writer.Written[0].Count, "count carried to proposal");
                    }

                    List<ArmadaEvent> typed = await AllTypedDecisionEventsAsync(testDb.Driver).ConfigureAwait(false);
                    if (testCase.ExpectTypedEvent == null)
                    {
                        AssertEqual(0, typed.Count, "no typed-decision event when the decision is off");
                    }
                    else
                    {
                        AssertEqual(1, typed.Count, "exactly one typed-decision event");
                        AssertEqual(testCase.ExpectTypedEvent, typed[0].EventType, "typed-decision event type");
                    }
                });
            }

            await RunTest("Nominate_ProposalTextIsRedacted", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.NoulAndChoice("durable_lesson", 0.99, "scope", "shared"));
                RecordingProposalWriter writer = new RecordingProposalWriter();
                MemoryCandidateAdapter adapter = BuildAdapter(testDb.Driver, client, writer, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                PapercutGroup group = Group("k2", "mission msn_abc123 failed at /srv/example/dock", 4);
                await adapter.NominateAsync(new List<PapercutGroup> { group }, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(1, writer.Written.Count);
                MemoryCandidateProposal proposal = writer.Written[0];
                AssertFalse(proposal.Title.Contains("msn_abc123", StringComparison.Ordinal), "the Armada id is redacted from the proposal");
                AssertFalse(proposal.Title.Contains("/srv/example", StringComparison.Ordinal), "the path is redacted from the proposal");
            });

            await RunTest("Nominate_UnavailableStopsThePass", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Unavailable("http_529"));
                RecordingProposalWriter writer = new RecordingProposalWriter();
                MemoryCandidateAdapter adapter = BuildAdapter(testDb.Driver, client, writer, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                List<PapercutGroup> input = new List<PapercutGroup> { Group("k1", "one", 3), Group("k2", "two", 2), Group("k3", "three", 1) };
                List<MemoryCandidateProposal> nominated = await adapter.NominateAsync(input, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, nominated.Count, "no nomination when unavailable");
                AssertEqual(1, client.Calls, "the pass stops after the first unavailable, not one call per group");
            });

            await RunTest("Nominate_ForwardsCallerTokenToClient", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.NoulAndChoice("durable_lesson", 0.10, "scope", "shared"));
                RecordingProposalWriter writer = new RecordingProposalWriter();
                MemoryCandidateAdapter adapter = BuildAdapter(testDb.Driver, client, writer, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                using CancellationTokenSource cts = new CancellationTokenSource();
                await adapter.NominateAsync(new List<PapercutGroup> { Group("k1", "x", 2) }, cts.Token).ConfigureAwait(false);

                AssertEqual(1, client.Calls);
                AssertTrue(client.LastToken.Equals(cts.Token), "the adapter forwards the caller's token so the client's timeout applies");
            });
        }

        private static MemoryCandidateAdapter BuildAdapter(DatabaseDriver database, FakeTypedDecisionClient client, IMemoryCandidateProposalWriter writer, TypedDecisionModeEnum globalMode, TypedDecisionModeEnum decisionMode)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = globalMode };
            settings.Decisions[MemoryCandidateAdapter.DecisionPoint].Mode = decisionMode;
            settings.Decisions[MemoryCandidateAdapter.DecisionPoint].GateThreshold = 0.90;
            TypedDecisionRecorder recorder = new TypedDecisionRecorder(database, new LoggingModule());
            return new MemoryCandidateAdapter(settings, client, recorder, writer, new LoggingModule());
        }

        private static PapercutGroup Group(string suffix, string title, int count)
        {
            return new PapercutGroup
            {
                Key = "vsl_example|BriefContradiction|" + suffix,
                VesselId = "vsl_example",
                Category = PapercutCategoryEnum.BriefContradiction,
                HighestSeverity = PapercutSeverityEnum.High,
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

        private sealed class RecordingProposalWriter : IMemoryCandidateProposalWriter
        {
            public List<MemoryCandidateProposal> Written { get; } = new List<MemoryCandidateProposal>();

            public Task<string?> WriteAsync(MemoryCandidateProposal proposal, CancellationToken token)
            {
                Written.Add(proposal);
                return Task.FromResult<string?>("/proposals/" + Written.Count + ".md");
            }
        }

        private static TypedDecisionResult DurableConfidenceOnly(double confidence)
        {
            Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
            {
                ["durable_lesson"] = new TypedAnswer { Type = "noul", Confidence = confidence },
                ["scope"] = new TypedAnswer { Type = "choice", Choice = "shared", Confidence = confidence }
            };
            return new TypedDecisionResult { Available = true, Answers = answers, InputTokens = 10, OutputTokens = 5, LatencyMs = 12 };
        }

        private sealed class CandidateCase
        {
            public required string Name { get; init; }
            public required TypedDecisionModeEnum GlobalMode { get; init; }
            public required TypedDecisionModeEnum DecisionMode { get; init; }
            public required TypedDecisionResult Result { get; init; }
            public required bool ExpectNominated { get; init; }
            public required int ExpectClientCalls { get; init; }
            public required string? ExpectTypedEvent { get; init; }
            public required bool ExpectWritten { get; init; }
        }
    }
}
