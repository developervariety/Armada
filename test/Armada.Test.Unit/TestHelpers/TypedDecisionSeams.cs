namespace Armada.Test.Unit.TestHelpers
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using SyslogLogging;

    /// <summary>
    /// One driver per seam that calls the typed-decision recorder, for the suites that check a rule every
    /// decision must keep. Each driver runs the real seam once with a known objective id, mission id and vessel,
    /// so a suite can read back what the seam recorded and what it sent.
    /// </summary>
    public static class TypedDecisionSeams
    {
        /// <summary>The objective id a seam that knows its objective is given.</summary>
        public const string ObjectiveId = "obj_subject_link";

        /// <summary>The mission id a seam that knows its mission is given.</summary>
        public const string MissionId = "msn_subject_link";

        /// <summary>The vessel a seam concerns unless the suite names another.</summary>
        public const string AllowedVesselId = "vsl_subject_link";

        /// <summary>One seam that calls the typed-decision recorder, and the objective id it knows.</summary>
        public sealed class SeamDriver
        {
            /// <summary>Create a driver.</summary>
            /// <param name="decisionPoint">The decision the seam records.</param>
            /// <param name="seam">A short name for the seam.</param>
            /// <param name="objectiveId">The objective id the seam knows, or null.</param>
            /// <param name="drive">Runs the seam once and returns the mission id it knew.</param>
            public SeamDriver(string decisionPoint, string seam, string? objectiveId, Func<SeamContext, Task<string?>> drive)
            {
                DecisionPoint = decisionPoint;
                Seam = seam;
                ExpectedObjectiveId = objectiveId;
                Drive = drive;
            }

            /// <summary>The decision the seam records.</summary>
            public string DecisionPoint { get; }
            /// <summary>A short name for the seam.</summary>
            public string Seam { get; }
            /// <summary>The objective id the seam knows, or null.</summary>
            public string? ExpectedObjectiveId { get; }

            /// <summary>Drives the seam once and returns the mission id the seam knew (null when it knew none).</summary>
            public Func<SeamContext, Task<string?>> Drive { get; }
        }

        /// <summary>What a driven seam is given: the database, settings, provider, recorder and the vessel it concerns.</summary>
        public sealed class SeamContext
        {
            /// <summary>The database.</summary>
            public required DatabaseDriver Database { get; init; }
            /// <summary>The settings.</summary>
            public required ArmadaSettings Settings { get; init; }
            /// <summary>The provider client.</summary>
            public required ITypedDecisionClient Client { get; init; }
            /// <summary>The recorder.</summary>
            public required TypedDecisionRecorder Recorder { get; init; }

            /// <summary>The vessel the seam's subject belongs to.</summary>
            public string VesselId { get; init; } = AllowedVesselId;

            /// <summary>The typed-decision settings.</summary>
            public TypedDecisionSettings Typed => Settings.TypedDecisions;
        }

        private sealed class NoCandidateRouter : IFollowUpRouter
        {
            public Task<IReadOnlyList<FollowUpDuplicateCandidate>> GetDuplicateCandidatesAsync(string? vesselId, int limit, CancellationToken token)
                => Task.FromResult<IReadOnlyList<FollowUpDuplicateCandidate>>(new List<FollowUpDuplicateCandidate>());
            public Task<string?> CreateTriagedObjectiveAsync(FollowUpRouteRequest request, CancellationToken token) => Task.FromResult<string?>(null);
            public Task AppendEvidenceNoteAsync(FollowUpRouteRequest request, CancellationToken token) => Task.CompletedTask;
            public Task LinkDuplicateAsync(FollowUpRouteRequest request, string existingObjectiveId, CancellationToken token) => Task.CompletedTask;
            public Task FlagBlockingForOperatorAsync(FollowUpRouteRequest request, CancellationToken token) => Task.CompletedTask;
        }

        /// <summary>A mission on the given vessel with the known mission id.</summary>
        public static Mission LinkMission(string vesselId, string persona = "Worker")
        {
            return new Mission { Id = MissionId, VesselId = vesselId, VoyageId = "vyg_subject_link", Title = "port a decoder", Persona = persona };
        }

        /// <summary>An objective on the given vessel with the known objective id.</summary>
        public static Objective LinkObjective(string vesselId)
        {
            return new Objective
            {
                Id = ObjectiveId,
                Title = "Port the decoder",
                Description = "Port the frame decoder and prove it with a failing test.",
                AcceptanceCriteria = new List<string> { "The decoder reproduces the source frame." },
                VesselIds = new List<string> { vesselId }
            };
        }

        /// <summary>A vessel record with the given id.</summary>
        public static Vessel LinkVessel(string vesselId)
        {
            return new Vessel { Id = vesselId, Name = "ExampleVessel", LocalPath = "/repo", DefaultBranch = "main" };
        }

        /// <summary>A one-file diff with one added line.</summary>
        public const string Diff = "diff --git a/src/Decoder.cs b/src/Decoder.cs\n--- a/src/Decoder.cs\n+++ b/src/Decoder.cs\n@@ -1 +1,2 @@\n a\n+int checksum = frame.Sum();\n";

        /// <summary>A stage_necessity input for the given objective and vessel.</summary>
        public static StageNecessityDecisionInput StageInput(string? objectiveId, string vesselId)
        {
            return new StageNecessityDecisionInput
            {
                ObjectiveId = objectiveId,
                VesselIds = new List<string> { vesselId },
                Title = "Port a token decoder",
                Description = "One-file protocol port; no UI, no new tests in scope.",
                AcceptanceCriteria = new List<string> { "The decoder reproduces the source frame." },
                Kind = "Chore",
                Stages = new List<StageNecessityStageResult>
                {
                    TypedStageNecessityAdapter.Stage(1, "Worker"),
                    TypedStageNecessityAdapter.Stage(2, "TestEngineer"),
                    TypedStageNecessityAdapter.Stage(3, "Judge")
                }
            };
        }

        /// <summary>A log_watch input that holds only the mission id.</summary>
        public static LogWatchDecisionInput LogWatchInput(string missionId)
        {
            return new LogWatchDecisionInput { MissionId = missionId, LogTail = "reading src/Decoder.cs\nrunning the unit tests" };
        }

        /// <summary>
        /// One row per seam that calls the recorder. A row drives the real seam with the ids and vessel it would
        /// hold in production and returns the mission id it held.
        /// </summary>
        public static List<SeamDriver> All()
        {
            return new List<SeamDriver>
            {
                // Objective-scoped seams: the dispatch preview, the refinement summary, the owner digest, and dispatch.
                new SeamDriver("preflight", "dispatch_preview", ObjectiveId, async (SeamContext ctx) =>
                {
                    PreflightTextAdapter adapter = new PreflightTextAdapter(ctx.Typed, ctx.Client, ctx.Recorder, new FakeOwnerDecisionNotePoster(), Quiet());
                    await adapter.EvaluateAsync(LinkObjective(ctx.VesselId), LinkVessel(ctx.VesselId), null, new ObjectiveDispatchPreview { VesselId = ctx.VesselId }, CancellationToken.None).ConfigureAwait(false);
                    return null;
                }),
                new SeamDriver("prior_art", "preflight", ObjectiveId, async (SeamContext ctx) =>
                {
                    TypedPriorArtAdapter adapter = new TypedPriorArtAdapter(ctx.Client, ctx.Recorder, ctx.Typed,
                        new FakePriorArtRetriever(FakePriorArtRetriever.RetrievalOf(FakePriorArtRetriever.Candidate(PriorArtWhereEnum.Landed, "src/Existing.cs:12", "class Decoder"))), Quiet());
                    await adapter.EvaluatePreflightAsync(LinkObjective(ctx.VesselId), LinkVessel(ctx.VesselId), new ObjectiveDispatchPreview { VesselId = ctx.VesselId }, CancellationToken.None).ConfigureAwait(false);
                    return null;
                }),
                new SeamDriver("criteria_lint", "refinement_summary", ObjectiveId, async (SeamContext ctx) =>
                {
                    CriteriaLintAdapter adapter = new CriteriaLintAdapter(ctx.Typed, ctx.Client, ctx.Recorder, Quiet());
                    await adapter.EvaluateAsync(new ObjectiveRefinementSummaryResponse
                    {
                        Summary = "Port the decoder.",
                        AcceptanceCriteria = new List<string> { "The decoder reproduces the source frame." }
                    }, ObjectiveKindEnum.Feature, CancellationToken.None, new List<string> { ctx.VesselId }, ObjectiveId).ConfigureAwait(false);
                    return null;
                }),
                new SeamDriver("stage_necessity", "dispatch_preview", ObjectiveId, async (SeamContext ctx) =>
                {
                    TypedStageNecessityAdapter adapter = new TypedStageNecessityAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet());
                    StageNecessityDecisionInput input = StageInput(ObjectiveId, ctx.VesselId);
                    await adapter.DecideAsync(input, StageNecessityVerdict.Rule(input.Stages), CancellationToken.None).ConfigureAwait(false);
                    return null;
                }),
                new SeamDriver("owner_digest", "digest", ObjectiveId, async (SeamContext ctx) =>
                {
                    TypedOwnerDigestAdapter adapter = new TypedOwnerDigestAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet());
                    OwnerDigestCandidate candidate = new OwnerDigestCandidate
                    {
                        QuestionText = "Should the batch size reduction be approved?",
                        BlockedRow = "row one",
                        ChainCount = 1,
                        AgeHours = 2.0,
                        ProposedDefault = "hold the row",
                        Source = "owner_decision_recheck",
                        VesselId = ctx.VesselId,
                        ObjectiveId = ObjectiveId
                    };
                    await adapter.DecideAsync(candidate, TypedOwnerDigestAdapter.DeterministicRule(candidate), CancellationToken.None).ConfigureAwait(false);
                    return null;
                }),
                new SeamDriver("dispatch_staleness", "dispatch", ObjectiveId, async (SeamContext ctx) =>
                {
                    TypedDispatchStalenessAdapter adapter = new TypedDispatchStalenessAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet());
                    await adapter.DecideAsync(new DispatchStalenessInput
                    {
                        VesselId = ctx.VesselId,
                        ObjectiveId = ObjectiveId,
                        Policy = CodeIndexDispatchStalenessPolicyEnum.Proceed,
                        Relevance = new CodeIndexStalenessRelevance { IsStale = true, IsRelevant = true, ChangedSourceFileCount = 1 },
                        Title = "Fix the encoder",
                        Description = "Edit src/FrameEncoder.cs"
                    }, CodeIndexDispatchStalenessPolicyEnum.Proceed, CancellationToken.None).ConfigureAwait(false);
                    return null;
                }),

                // Mission-scoped seams that hold the mission record.
                new SeamDriver("prior_art", "judge", null, async (SeamContext ctx) =>
                {
                    TypedPriorArtAdapter adapter = new TypedPriorArtAdapter(ctx.Client, ctx.Recorder, ctx.Typed,
                        new FakePriorArtRetriever(FakePriorArtRetriever.RetrievalOf(FakePriorArtRetriever.Candidate(PriorArtWhereEnum.Landed, "src/Existing.cs:12", "class Decoder"))), Quiet());
                    await adapter.EvaluateJudgeInstructionAsync(LinkMission(ctx.VesselId, "Judge"), LinkVessel(ctx.VesselId), "class Decoder", CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SeamDriver("failure_cause", "recovery", null, async (SeamContext ctx) =>
                {
                    await new TypedFailureCauseAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet()).DecideAsync(new FailureCauseDecisionInput
                    {
                        Mission = LinkMission(ctx.VesselId),
                        FailureReason = "definition-of-done gate failed",
                        AgentOutputTail = "some captain output",
                        DodClass = "TestFail",
                        Persona = "Worker",
                        MissionMode = "Implementation",
                        Checks = new List<FailureCauseCheckFact>
                        {
                            new FailureCauseCheckFact { Label = "unit", Type = "UnitTest", Status = "Failed", CommitMatchesJudge = true, ExitCode = 1, Diagnostics = "1 failed" }
                        }
                    }, TypedRecoveryVerdict.Rescue("recoverable mission failure"), CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SeamDriver("refusal", "completion", null, async (SeamContext ctx) =>
                {
                    await new TypedRefusalAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet()).DecideAsync(new RefusalDecisionInput
                    {
                        Mission = LinkMission(ctx.VesselId),
                        AgentOutputTail = "the closing statement",
                        MissionTitle = "read a token exchange",
                        MarkerPresent = false
                    }, new CaptainRefusal(), CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SeamDriver("runtime_failure", "exit", null, async (SeamContext ctx) =>
                {
                    await new TypedRuntimeFailureAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet()).DecideAsync(new RuntimeFailureDecisionInput
                    {
                        Mission = LinkMission(ctx.VesselId),
                        ExitCode = 1,
                        Tail = "process ended non-zero",
                        Runtime = "ClaudeCode",
                        ModelId = "claude-fable-5"
                    }, RuntimeFailureKindEnum.Crash, CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SeamDriver("review_substance", "judge_pass", null, async (SeamContext ctx) =>
                {
                    await new TypedReviewSubstanceAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet()).DecideAsync(new ReviewSubstanceDecisionInput
                    {
                        Mission = LinkMission(ctx.VesselId, "Judge"),
                        Narrative = "The review covers completeness, correctness, tests, and failure modes with specifics.",
                        RequiredSections = new List<string> { "Completeness", "Correctness", "Tests", "Failure Modes" },
                        DiffStat = "3 files, +40/-8",
                        CheckSummary = "Build:Build:Passed; UnitTest:UnitTest:Passed"
                    }, ReviewSubstanceVerdict.Rule(false, ReviewSubstanceRuleCategory.MissingSections, "Judge PASS verdict missing required review sections: Tests"),
                        CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SeamDriver("leak_hunk", "landing", null, async (SeamContext ctx) =>
                {
                    await new LeakHunkAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet())
                        .EvaluateAsync(Diff, "ExampleVessel", LinkMission(ctx.VesselId), new DockBoundaryScanResult(), CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SeamDriver("capacity_escalation", "routing", null, async (SeamContext ctx) =>
                {
                    await new TypedCapacityEscalationAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet()).DecideAsync(new CapacityEscalationDecisionInput
                    {
                        Mission = LinkMission(ctx.VesselId),
                        Persona = "Worker",
                        Title = "Port the decoder",
                        Description = "Port the frame decoder.",
                        DefaultModels = new List<string> { "model-default" },
                        LighterModels = new List<string> { "model-light" },
                        StrongerModels = new List<string> { "model-strong" }
                    }, CapacityChoiceEnum.Default, CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SeamDriver("change_substance", "rescue", null, async (SeamContext ctx) =>
                {
                    await new TypedChangeSubstanceAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet()).DecideAsync(new ChangeSubstanceDecisionInput
                    {
                        Mission = LinkMission(ctx.VesselId),
                        ChangedPaths = new List<string> { "docs/notes.md" },
                        UnifiedDiff = "+++ b/docs/notes.md\n@@\n+some prose\n+++ b/src/Decoder.cs\n@@\n+if (guard) Disable();",
                        VesselPublicName = "ExampleVessel"
                    }, ChangeSubstanceVerdict.Rule(ChangeSubstanceEnum.DocumentationOnly), CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SeamDriver("handoff_outcome", "handoff", null, async (SeamContext ctx) =>
                {
                    await new TypedHandoffOutcomeAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet()).DecideAsync(new HandoffOutcomeDecisionInput
                    {
                        Mission = LinkMission(ctx.VesselId),
                        OutputTail = "The catalogue the port needs is not provisioned in this dock.",
                        DiffStat = "0 files, +0/-0",
                        AcceptanceCriteria = new List<string> { "The decoder reproduces the source frame." },
                        Persona = "Worker",
                        MarkerPresent = true
                    }, HandoffOutcomeVerdict.Proceed(), CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SeamDriver("revision_kind", "judge_revision", null, async (SeamContext ctx) =>
                {
                    await new TypedRevisionKindAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet()).DecideAsync(new RevisionKindDecisionInput
                    {
                        Mission = LinkMission(ctx.VesselId, "Judge"),
                        RevisionItems = new List<string> { "Reword the doc comment to name the rule.", "Fix a typo in the summary." },
                        Symptom = "The decoder truncates the counter."
                    }, RevisionKindVerdict.Proceed(), CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SeamDriver("test_covers", "test_engineer", null, async (SeamContext ctx) =>
                {
                    await new TypedTestCoversAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet()).DecideAsync(new TestCoversDecisionInput
                    {
                        Mission = LinkMission(ctx.VesselId, "TestEngineer"),
                        AddedTests = new List<TestCoversMethod> { new TestCoversMethod("Decode_TruncatesCounter_ReturnsByte", "AssertEqual(0x08, decoder.Decode(frame));") },
                        Symptom = "The decoder truncates the counter."
                    }, TestCoversVerdict.NoInstructions(), CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SeamDriver("lint_finding", "linter", null, async (SeamContext ctx) =>
                {
                    await new TypedLintFindingAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet()).DecideAsync(new LintFindingDecisionInput
                    {
                        Mission = LinkMission(ctx.VesselId, "Linter"),
                        Findings = new List<string> { "The guard is missing on the fail path." }
                    }, LintFindingVerdict.Unrouted(), CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SeamDriver("flake_score", "definition_of_done", null, async (SeamContext ctx) =>
                {
                    await new TypedFlakeScoreAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet()).DecideAsync(new FlakeScoreDecisionInput
                    {
                        Mission = LinkMission(ctx.VesselId),
                        FailingTestNames = new List<string> { "Example.Core.Tests.SequenceRunnerTests.StepPauseMs_50" },
                        AssertionLines = "Expected: Success, Actual: Timeout",
                        TouchedFiles = new List<string> { "src/Example.Core/Decoder.cs" },
                        SameTestFailedElsewhere24h = true,
                        RuleClass = DefinitionOfDoneFailureClassEnum.TestFail
                    }, FlakeScoreVerdict.NoRerun(), CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SeamDriver("memory_review", "recorder", null, async (SeamContext ctx) =>
                {
                    Mission mission = LinkMission(ctx.VesselId, "Recorder");
                    mission.TenantId = Constants.DefaultTenantId;
                    await ctx.Database.Memories.CreateAsync(new Memory
                    {
                        TenantId = Constants.DefaultTenantId,
                        Type = MemoryTypeEnum.Semantic,
                        Topic = "build",
                        Summary = "How the decoder is read",
                        Content = "The decoder reads frames in order.",
                        Salience = 0.9,
                        SourceMissionId = mission.Id,
                        SourceKind = MemorySourceKindEnum.Mission,
                        VesselId = ctx.VesselId
                    }).ConfigureAwait(false);
                    RecorderMemoryReviewAdapter adapter = new RecorderMemoryReviewAdapter(ctx.Typed, ctx.Client, ctx.Recorder,
                        new DatabaseMemoryCandidateProposalWriter(ctx.Database, Quiet()), ctx.Database, Quiet());
                    await adapter.ReviewAsync(mission, CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),

                // Seams that hold only the mission id.
                new SeamDriver("leak_hunk", "merge_queue", null, async (SeamContext ctx) =>
                {
                    await new LeakHunkAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet())
                        .EvaluateAsync(Diff, "ExampleVessel", null, new DockBoundaryScanResult(), CancellationToken.None, MissionId, ctx.VesselId).ConfigureAwait(false);
                    return MissionId;
                }),
                new SeamDriver("log_watch", "screen", null, async (SeamContext ctx) =>
                {
                    await new LogWatchScreenPass(new TypedLogWatchAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet())).EvaluateAsync(new LogScreenContext
                    {
                        MissionId = MissionId,
                        VesselId = ctx.VesselId,
                        Tail = "reading src/Decoder.cs\nrunning the unit tests"
                    }, CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SeamDriver("change_quality", "review_and_route", null, async (SeamContext ctx) =>
                {
                    TypedChangeQualityAdapter adapter = new TypedChangeQualityAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet());
                    await ChangeQualityGate.ReviewAndRouteAsync(Diff, ctx.VesselId, MissionId, false, adapter, null, Quiet(), CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SeamDriver("followup_routing", "judge_follow_up", null, async (SeamContext ctx) =>
                {
                    FollowUpRoutingAdapter adapter = new FollowUpRoutingAdapter(ctx.Typed, ctx.Client, ctx.Recorder, new NoCandidateRouter(), Quiet());
                    await adapter.RouteAsync(new JudgeFollowUp
                    {
                        JudgeMissionId = "msn_subject_judge",
                        ReviewedMissionId = MissionId,
                        VesselId = ctx.VesselId,
                        JudgeVerdict = "PASS",
                        SuggestedFollowUps = "- Add a fuzz test for the decoder."
                    }, null, CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SeamDriver("inbox_triage", "inbox", null, async (SeamContext ctx) =>
                {
                    InboxTriageAdapter adapter = new InboxTriageAdapter(ctx.Typed, ctx.Client, ctx.Recorder, Quiet());
                    await adapter.TriageInboxAsync(new List<InboxItem>
                    {
                        new InboxItem { Kind = "mission_failed", Title = "Decoder mission failed", Detail = "The build failed.", EntityType = "mission", EntityId = MissionId, VesselIds = new List<string> { ctx.VesselId } }
                    }, CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SeamDriver("inbox_triage", "board_notes", null, async (SeamContext ctx) =>
                {
                    InboxTriageAdapter adapter = new InboxTriageAdapter(ctx.Typed, ctx.Client, ctx.Recorder, Quiet());
                    await adapter.TriageBoardNotesAsync(new List<BoardNoteTriageInput>
                    {
                        new BoardNoteTriageInput { Id = "note_subject_link", AuthorType = "captain", Content = "The decoder port is blocked on a fixture.", MissionId = MissionId, VesselIds = new List<string> { ctx.VesselId } }
                    }, CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),

                // Captain helper tools: the mission the captain names, resolved from the database.
                CaptainToolDriver("premise_check", McpTypedDecisionTools.CheckPremiseToolName, missionId => new { restatement = "I will port the decoder.", missionId }),
                CaptainToolDriver("memory_record", McpTypedDecisionTools.MemoryTriageToolName, missionId => new { candidate = "The decoder reads frames in order.", missionId }),
                CaptainToolDriver("corpus_prelabel", McpTypedDecisionTools.CorpusPrelabelToolName, missionId => new { record = "A captain asked for the owner's ruling on the batch size.", missionId })
            };
        }

        private static SeamDriver CaptainToolDriver(string decisionPoint, string toolName, Func<string, object> args)
        {
            return new SeamDriver(decisionPoint, "captain_tool", null, async (SeamContext ctx) =>
            {
                Vessel vessel = await ctx.Database.Vessels.CreateAsync(new Vessel("ExampleVessel", "https://example.invalid/repo.git") { Id = ctx.VesselId }).ConfigureAwait(false);
                Mission created = await ctx.Database.Missions.CreateAsync(new Mission
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VesselId = vessel.Id,
                    Title = "port a decoder"
                }).ConfigureAwait(false);

                Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>(StringComparer.Ordinal);
                McpTypedDecisionTools.Register((name, _, _, handler) => { handlers[name] = handler; }, ctx.Database, ctx.Client, ctx.Recorder, ctx.Settings, Quiet());
                AuthContext caller = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, false, "Bearer");
                using (McpCallerContext.Begin(caller))
                    await handlers[toolName](JsonSerializer.SerializeToElement(args(created.Id))).ConfigureAwait(false);
                return created.Id;
            });
        }

        /// <summary>A logging module that writes nothing.</summary>
        public static LoggingModule Quiet()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

    }
}
