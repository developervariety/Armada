namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Services.TypedDecisions;
    using Armada.Core.Settings;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Every typed-decision event and retained sample names the objective and mission it judged, wherever the
    /// calling seam knows them, so a retained state can be joined to the records it was about. The census drives
    /// each shipped decision through its real seam with known ids and reads back what was recorded: a behavioural
    /// check, not a source-text guard. A decision added later with no row here fails the completeness case until
    /// it records its subject or states why it has none. The ids are record links only, so the transmitted state,
    /// and therefore its hash, must not change with them.
    /// </summary>
    public sealed class TypedDecisionSubjectLinkTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Typed Decision Subject Links";

        private const string ObjectiveId = "obj_subject_link";
        private const string MissionId = "msn_subject_link";
        private const string VesselId = "vsl_subject_link";

        // The decisions whose subject is not one objective or one mission, and why. Each still records its call.
        private static readonly Dictionary<string, string> NoSubject = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["papercut_merge"] = "it compares two papercut groups, each gathered across many missions",
            ["memory_candidate"] = "it reads one papercut group gathered across many missions"
        };

        #region Drivers

        // What one driven seam recorded, and the ids the seam knew when it called the recorder.
        private sealed class SubjectDriver
        {
            public SubjectDriver(string decisionPoint, string seam, string? objectiveId, Func<DriveContext, Task<string?>> drive)
            {
                DecisionPoint = decisionPoint;
                Seam = seam;
                ExpectedObjectiveId = objectiveId;
                Drive = drive;
            }

            public string DecisionPoint { get; }
            public string Seam { get; }
            public string? ExpectedObjectiveId { get; }

            // Drives the seam once and returns the mission id the seam knew (null when it knew none).
            public Func<DriveContext, Task<string?>> Drive { get; }
        }

        private sealed class DriveContext
        {
            public required DatabaseDriver Database { get; init; }
            public required ArmadaSettings Settings { get; init; }
            public required ITypedDecisionClient Client { get; init; }
            public required TypedDecisionRecorder Recorder { get; init; }

            public TypedDecisionSettings Typed => Settings.TypedDecisions;
        }

        // The subject fields of one recorded decision event: the payload as written, plus the mission column.
        private sealed class RecordedSubject
        {
            [JsonPropertyName("decision")]
            public string? Decision { get; set; }

            [JsonPropertyName("objective_id")]
            public string? ObjectiveId { get; set; }

            [JsonPropertyName("mission_id")]
            public string? PayloadMissionId { get; set; }

            [JsonPropertyName("state_sha256")]
            public string? StateSha256 { get; set; }

            [JsonIgnore]
            public string? MissionColumn { get; set; }
        }

        // The subject fields of one retained sample line.
        private sealed class RetainedSubject
        {
            [JsonPropertyName("objective_id")]
            public string? ObjectiveId { get; set; }

            [JsonPropertyName("mission_id")]
            public string? MissionId { get; set; }
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

        private static Mission LinkMission(string persona = "Worker")
        {
            return new Mission { Id = MissionId, VesselId = VesselId, VoyageId = "vyg_subject_link", Title = "port a decoder", Persona = persona };
        }

        private static Objective LinkObjective()
        {
            return new Objective
            {
                Id = ObjectiveId,
                Title = "Port the decoder",
                Description = "Port the frame decoder and prove it with a failing test.",
                AcceptanceCriteria = new List<string> { "The decoder reproduces the source frame." },
                VesselIds = new List<string> { VesselId }
            };
        }

        private static Vessel LinkVessel()
        {
            return new Vessel { Id = VesselId, Name = "ExampleVessel", LocalPath = "/repo", DefaultBranch = "main" };
        }

        private const string Diff = "diff --git a/src/Decoder.cs b/src/Decoder.cs\n--- a/src/Decoder.cs\n+++ b/src/Decoder.cs\n@@ -1 +1,2 @@\n a\n+int checksum = frame.Sum();\n";

        private static StageNecessityDecisionInput StageInput(string? objectiveId)
        {
            return new StageNecessityDecisionInput
            {
                ObjectiveId = objectiveId,
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

        private static LogWatchDecisionInput LogWatchInput(string missionId)
        {
            return new LogWatchDecisionInput { MissionId = missionId, LogTail = "reading src/Decoder.cs\nrunning the unit tests" };
        }

        // One row per seam that calls the recorder. A row drives the real seam with the ids it would hold in
        // production and returns the mission id it held.
        private static List<SubjectDriver> Drivers()
        {
            return new List<SubjectDriver>
            {
                // Objective-scoped seams: the dispatch preview, the refinement summary, the owner digest, and dispatch.
                new SubjectDriver("preflight", "dispatch_preview", ObjectiveId, async ctx =>
                {
                    PreflightTextAdapter adapter = new PreflightTextAdapter(ctx.Typed, ctx.Client, ctx.Recorder, new FakeOwnerDecisionNotePoster(), Quiet());
                    await adapter.EvaluateAsync(LinkObjective(), LinkVessel(), null, new ObjectiveDispatchPreview { VesselId = VesselId }, CancellationToken.None).ConfigureAwait(false);
                    return null;
                }),
                new SubjectDriver("prior_art", "preflight", ObjectiveId, async ctx =>
                {
                    TypedPriorArtAdapter adapter = new TypedPriorArtAdapter(ctx.Client, ctx.Recorder, ctx.Typed,
                        new FakePriorArtRetriever(FakePriorArtRetriever.RetrievalOf(FakePriorArtRetriever.Candidate(PriorArtWhereEnum.Landed, "src/Existing.cs:12", "class Decoder"))), Quiet());
                    await adapter.EvaluatePreflightAsync(LinkObjective(), LinkVessel(), new ObjectiveDispatchPreview { VesselId = VesselId }, CancellationToken.None).ConfigureAwait(false);
                    return null;
                }),
                new SubjectDriver("criteria_lint", "refinement_summary", ObjectiveId, async ctx =>
                {
                    CriteriaLintAdapter adapter = new CriteriaLintAdapter(ctx.Typed, ctx.Client, ctx.Recorder, Quiet());
                    await adapter.EvaluateAsync(new ObjectiveRefinementSummaryResponse
                    {
                        Summary = "Port the decoder.",
                        AcceptanceCriteria = new List<string> { "The decoder reproduces the source frame." }
                    }, ObjectiveKindEnum.Feature, CancellationToken.None, new List<string> { VesselId }, ObjectiveId).ConfigureAwait(false);
                    return null;
                }),
                new SubjectDriver("stage_necessity", "dispatch_preview", ObjectiveId, async ctx =>
                {
                    TypedStageNecessityAdapter adapter = new TypedStageNecessityAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet());
                    StageNecessityDecisionInput input = StageInput(ObjectiveId);
                    await adapter.DecideAsync(input, StageNecessityVerdict.Rule(input.Stages), CancellationToken.None).ConfigureAwait(false);
                    return null;
                }),
                new SubjectDriver("owner_digest", "digest", ObjectiveId, async ctx =>
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
                        VesselId = VesselId,
                        ObjectiveId = ObjectiveId
                    };
                    await adapter.DecideAsync(candidate, TypedOwnerDigestAdapter.DeterministicRule(candidate), CancellationToken.None).ConfigureAwait(false);
                    return null;
                }),
                new SubjectDriver("dispatch_staleness", "dispatch", ObjectiveId, async ctx =>
                {
                    TypedDispatchStalenessAdapter adapter = new TypedDispatchStalenessAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet());
                    await adapter.DecideAsync(new DispatchStalenessInput
                    {
                        VesselId = VesselId,
                        ObjectiveId = ObjectiveId,
                        Policy = CodeIndexDispatchStalenessPolicyEnum.Proceed,
                        Relevance = new CodeIndexStalenessRelevance { IsStale = true, IsRelevant = true, ChangedSourceFileCount = 1 },
                        Title = "Fix the encoder",
                        Description = "Edit src/FrameEncoder.cs"
                    }, CodeIndexDispatchStalenessPolicyEnum.Proceed, CancellationToken.None).ConfigureAwait(false);
                    return null;
                }),

                // Mission-scoped seams that hold the mission record.
                new SubjectDriver("prior_art", "judge", null, async ctx =>
                {
                    TypedPriorArtAdapter adapter = new TypedPriorArtAdapter(ctx.Client, ctx.Recorder, ctx.Typed,
                        new FakePriorArtRetriever(FakePriorArtRetriever.RetrievalOf(FakePriorArtRetriever.Candidate(PriorArtWhereEnum.Landed, "src/Existing.cs:12", "class Decoder"))), Quiet());
                    await adapter.EvaluateJudgeInstructionAsync(LinkMission("Judge"), LinkVessel(), "class Decoder", CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SubjectDriver("failure_cause", "recovery", null, async ctx =>
                {
                    await new TypedFailureCauseAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet()).DecideAsync(new FailureCauseDecisionInput
                    {
                        Mission = LinkMission(),
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
                new SubjectDriver("refusal", "completion", null, async ctx =>
                {
                    await new TypedRefusalAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet()).DecideAsync(new RefusalDecisionInput
                    {
                        Mission = LinkMission(),
                        AgentOutputTail = "the closing statement",
                        MissionTitle = "read a token exchange",
                        MarkerPresent = false
                    }, new CaptainRefusal(), CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SubjectDriver("runtime_failure", "exit", null, async ctx =>
                {
                    await new TypedRuntimeFailureAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet()).DecideAsync(new RuntimeFailureDecisionInput
                    {
                        Mission = LinkMission(),
                        ExitCode = 1,
                        Tail = "process ended non-zero",
                        Runtime = "ClaudeCode",
                        ModelId = "claude-fable-5"
                    }, RuntimeFailureKindEnum.Crash, CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SubjectDriver("review_substance", "judge_pass", null, async ctx =>
                {
                    await new TypedReviewSubstanceAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet()).DecideAsync(new ReviewSubstanceDecisionInput
                    {
                        Mission = LinkMission("Judge"),
                        Narrative = "The review covers completeness, correctness, tests, and failure modes with specifics.",
                        RequiredSections = new List<string> { "Completeness", "Correctness", "Tests", "Failure Modes" },
                        DiffStat = "3 files, +40/-8",
                        CheckSummary = "Build:Build:Passed; UnitTest:UnitTest:Passed"
                    }, ReviewSubstanceVerdict.Rule(false, ReviewSubstanceRuleCategory.MissingSections, "Judge PASS verdict missing required review sections: Tests"),
                        CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SubjectDriver("leak_hunk", "landing", null, async ctx =>
                {
                    await new LeakHunkAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet())
                        .EvaluateAsync(Diff, "ExampleVessel", LinkMission(), new DockBoundaryScanResult(), CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SubjectDriver("capacity_escalation", "routing", null, async ctx =>
                {
                    await new TypedCapacityEscalationAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet()).DecideAsync(new CapacityEscalationDecisionInput
                    {
                        Mission = LinkMission(),
                        Persona = "Worker",
                        Title = "Port the decoder",
                        Description = "Port the frame decoder.",
                        DefaultModels = new List<string> { "model-default" },
                        LighterModels = new List<string> { "model-light" },
                        StrongerModels = new List<string> { "model-strong" }
                    }, CapacityChoiceEnum.Default, CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SubjectDriver("change_substance", "rescue", null, async ctx =>
                {
                    await new TypedChangeSubstanceAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet()).DecideAsync(new ChangeSubstanceDecisionInput
                    {
                        Mission = LinkMission(),
                        ChangedPaths = new List<string> { "docs/notes.md" },
                        UnifiedDiff = "+++ b/docs/notes.md\n@@\n+some prose\n+++ b/src/Decoder.cs\n@@\n+if (guard) Disable();",
                        VesselPublicName = "ExampleVessel"
                    }, ChangeSubstanceVerdict.Rule(ChangeSubstanceEnum.DocumentationOnly), CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SubjectDriver("handoff_outcome", "handoff", null, async ctx =>
                {
                    await new TypedHandoffOutcomeAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet()).DecideAsync(new HandoffOutcomeDecisionInput
                    {
                        Mission = LinkMission(),
                        OutputTail = "The catalogue the port needs is not provisioned in this dock.",
                        DiffStat = "0 files, +0/-0",
                        AcceptanceCriteria = new List<string> { "The decoder reproduces the source frame." },
                        Persona = "Worker",
                        MarkerPresent = true
                    }, HandoffOutcomeVerdict.Proceed(), CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SubjectDriver("revision_kind", "judge_revision", null, async ctx =>
                {
                    await new TypedRevisionKindAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet()).DecideAsync(new RevisionKindDecisionInput
                    {
                        Mission = LinkMission("Judge"),
                        RevisionItems = new List<string> { "Reword the doc comment to name the rule.", "Fix a typo in the summary." },
                        Symptom = "The decoder truncates the counter."
                    }, RevisionKindVerdict.Proceed(), CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SubjectDriver("test_covers", "test_engineer", null, async ctx =>
                {
                    await new TypedTestCoversAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet()).DecideAsync(new TestCoversDecisionInput
                    {
                        Mission = LinkMission("TestEngineer"),
                        AddedTests = new List<TestCoversMethod> { new TestCoversMethod("Decode_TruncatesCounter_ReturnsByte", "AssertEqual(0x08, decoder.Decode(frame));") },
                        Symptom = "The decoder truncates the counter."
                    }, TestCoversVerdict.NoInstructions(), CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SubjectDriver("lint_finding", "linter", null, async ctx =>
                {
                    await new TypedLintFindingAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet()).DecideAsync(new LintFindingDecisionInput
                    {
                        Mission = LinkMission("Linter"),
                        Findings = new List<string> { "The guard is missing on the fail path." }
                    }, LintFindingVerdict.Unrouted(), CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SubjectDriver("flake_score", "definition_of_done", null, async ctx =>
                {
                    await new TypedFlakeScoreAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet()).DecideAsync(new FlakeScoreDecisionInput
                    {
                        Mission = LinkMission(),
                        FailingTestNames = new List<string> { "Example.Core.Tests.SequenceRunnerTests.StepPauseMs_50" },
                        AssertionLines = "Expected: Success, Actual: Timeout",
                        TouchedFiles = new List<string> { "src/Example.Core/Decoder.cs" },
                        SameTestFailedElsewhere24h = true,
                        RuleClass = DefinitionOfDoneFailureClassEnum.TestFail
                    }, FlakeScoreVerdict.NoRerun(), CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SubjectDriver("memory_review", "recorder", null, async ctx =>
                {
                    Mission mission = LinkMission("Recorder");
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
                        VesselId = VesselId
                    }).ConfigureAwait(false);
                    RecorderMemoryReviewAdapter adapter = new RecorderMemoryReviewAdapter(ctx.Typed, ctx.Client, ctx.Recorder,
                        new DatabaseMemoryCandidateProposalWriter(ctx.Database, Quiet()), ctx.Database, Quiet());
                    await adapter.ReviewAsync(mission, CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),

                // Seams that hold only the mission id.
                new SubjectDriver("leak_hunk", "merge_queue", null, async ctx =>
                {
                    await new LeakHunkAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet())
                        .EvaluateAsync(Diff, "ExampleVessel", null, new DockBoundaryScanResult(), CancellationToken.None, MissionId).ConfigureAwait(false);
                    return MissionId;
                }),
                new SubjectDriver("log_watch", "screen", null, async ctx =>
                {
                    await new TypedLogWatchAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet())
                        .DecideAsync(LogWatchInput(MissionId), LogWatchVerdict.OnTrack(), CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SubjectDriver("change_quality", "review_and_route", null, async ctx =>
                {
                    TypedChangeQualityAdapter adapter = new TypedChangeQualityAdapter(ctx.Client, ctx.Recorder, ctx.Typed, Quiet());
                    await ChangeQualityGate.ReviewAndRouteAsync(Diff, VesselId, MissionId, false, adapter, null, Quiet(), CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SubjectDriver("followup_routing", "judge_follow_up", null, async ctx =>
                {
                    FollowUpRoutingAdapter adapter = new FollowUpRoutingAdapter(ctx.Typed, ctx.Client, ctx.Recorder, new NoCandidateRouter(), Quiet());
                    await adapter.RouteAsync(new JudgeFollowUp
                    {
                        JudgeMissionId = "msn_subject_judge",
                        ReviewedMissionId = MissionId,
                        VesselId = VesselId,
                        JudgeVerdict = "PASS",
                        SuggestedFollowUps = "- Add a fuzz test for the decoder."
                    }, null, CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SubjectDriver("inbox_triage", "inbox", null, async ctx =>
                {
                    InboxTriageAdapter adapter = new InboxTriageAdapter(ctx.Typed, ctx.Client, ctx.Recorder, Quiet());
                    await adapter.TriageInboxAsync(new List<InboxItem>
                    {
                        new InboxItem { Kind = "mission_failed", Title = "Decoder mission failed", Detail = "The build failed.", EntityType = "mission", EntityId = MissionId }
                    }, CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),
                new SubjectDriver("inbox_triage", "board_notes", null, async ctx =>
                {
                    InboxTriageAdapter adapter = new InboxTriageAdapter(ctx.Typed, ctx.Client, ctx.Recorder, Quiet());
                    await adapter.TriageBoardNotesAsync(new List<BoardNoteTriageInput>
                    {
                        new BoardNoteTriageInput { Id = "note_subject_link", AuthorType = "captain", Content = "The decoder port is blocked on a fixture.", MissionId = MissionId }
                    }, CancellationToken.None).ConfigureAwait(false);
                    return MissionId;
                }),

                // Captain helper tools: the mission the captain names, resolved from the database.
                CaptainToolDriver("premise_check", McpTypedDecisionTools.CheckPremiseToolName, missionId => new { restatement = "I will port the decoder.", missionId }),
                CaptainToolDriver("memory_record", McpTypedDecisionTools.MemoryTriageToolName, missionId => new { candidate = "The decoder reads frames in order.", missionId }),
                CaptainToolDriver("corpus_prelabel", McpTypedDecisionTools.CorpusPrelabelToolName, missionId => new { record = "A captain asked for the owner's ruling on the batch size.", missionId })
            };
        }

        private static SubjectDriver CaptainToolDriver(string decisionPoint, string toolName, Func<string, object> args)
        {
            return new SubjectDriver(decisionPoint, "captain_tool", null, async ctx =>
            {
                Vessel vessel = await ctx.Database.Vessels.CreateAsync(new Vessel("ExampleVessel", "https://example.invalid/repo.git")).ConfigureAwait(false);
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

        #endregion

        #region Helpers

        private static LoggingModule Quiet()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        // Every shipped decision is on and retains its state, so each call writes an event and a sample.
        private static ArmadaSettings RetainingSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.TypedDecisions.Mode = TypedDecisionModeEnum.Gate;
            settings.TypedDecisions.CaptainTool.Enabled = true;
            settings.TypedDecisions.Retention.Enabled = true;
            foreach (string name in TypedDecisionSettings.ShippedDecisionNames)
                settings.TypedDecisions.Decisions[name].RetainState = true;
            return settings;
        }

        private static readonly string[] _EventTypes =
        {
            TypedDecisionRecorder.EventTypeGated,
            TypedDecisionRecorder.EventTypeShadow,
            TypedDecisionRecorder.EventTypeUnavailable,
            TypedDecisionRecorder.EventTypeCaptain
        };

        private static async Task<List<RecordedSubject>> DecisionEventsAsync(DatabaseDriver database, string decisionPoint)
        {
            List<RecordedSubject> found = new List<RecordedSubject>();
            foreach (string type in _EventTypes)
            {
                foreach (ArmadaEvent evt in await database.Events.EnumerateByTypeAsync(type, 100).ConfigureAwait(false))
                {
                    RecordedSubject? subject = JsonSerializer.Deserialize<RecordedSubject>(evt.Payload ?? "{}");
                    if (subject == null || subject.Decision != decisionPoint) continue;
                    subject.MissionColumn = evt.MissionId;
                    found.Add(subject);
                }
            }
            return found;
        }

        // The retained lines for one decision, read field by field so a field the store never wrote reads as absent.
        private static List<RetainedSubject> RetainedSamples(TypedDecisionSampleStore store, string decisionPoint)
        {
            List<RetainedSubject> samples = new List<RetainedSubject>();
            string folder = Path.Combine(store.RootPath, decisionPoint);
            if (!Directory.Exists(folder)) return samples;
            foreach (string file in Directory.EnumerateFiles(folder, "*.jsonl").OrderBy(x => x, StringComparer.Ordinal))
            {
                foreach (string line in File.ReadAllLines(file))
                {
                    if (String.IsNullOrWhiteSpace(line)) continue;
                    RetainedSubject? sample = JsonSerializer.Deserialize<RetainedSubject>(line);
                    if (sample != null) samples.Add(sample);
                }
            }
            return samples;
        }

        private static string NewTempDir()
        {
            string path = Path.Combine(Path.GetTempPath(), "armada-subject-link-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void SafeDelete(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        // One call through a seam: the state the provider received, and what the event recorded.
        private sealed class CallRecord
        {
            public int Events { get; init; }
            public string StateText { get; init; } = String.Empty;
            public string? StateSha256 { get; init; }
            public string? ObjectiveId { get; init; }
            public string? MissionId { get; init; }
        }

        private static async Task<CallRecord> CallOnceAsync(string decisionPoint, Func<ITypedDecisionClient, TypedDecisionRecorder, TypedDecisionSettings, Task> call)
        {
            using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Unavailable("http_429"));
                ArmadaSettings settings = RetainingSettings();
                await call(client, new TypedDecisionRecorder(db.Driver, Quiet()), settings.TypedDecisions).ConfigureAwait(false);

                List<RecordedSubject> events = await DecisionEventsAsync(db.Driver, decisionPoint).ConfigureAwait(false);
                RecordedSubject? first = events.FirstOrDefault();
                return new CallRecord
                {
                    Events = events.Count,
                    StateText = FakeTypedDecisionClient.StateText(client.LastRequest),
                    StateSha256 = first?.StateSha256,
                    ObjectiveId = first?.ObjectiveId,
                    MissionId = first?.MissionColumn
                };
            }
        }

        #endregion

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("EveryShippedDecision_RecordsItsSubject_OrStatesWhyItHasNone", () =>
            {
                HashSet<string> covered = new HashSet<string>(Drivers().Select(driver => driver.DecisionPoint), StringComparer.Ordinal);
                List<string> missing = TypedDecisionSettings.ShippedDecisionNames
                    .Where(name => !covered.Contains(name) && !NoSubject.ContainsKey(name))
                    .ToList();
                AssertEqual(0, missing.Count, "decisions with no subject-link driver and no stated reason: " + String.Join(", ", missing));
                foreach (string name in NoSubject.Keys)
                    AssertFalse(covered.Contains(name), name + " is listed as having no subject, so it must not also have a driver");
            });

            foreach (SubjectDriver driver in Drivers())
            {
                string label = driver.DecisionPoint + "/" + driver.Seam;
                await RunTest("Subject_" + label + "_IsOnTheEventAndTheRetainedSample", async () =>
                {
                    using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        string dataDirectory = NewTempDir();
                        try
                        {
                            ArmadaSettings settings = RetainingSettings();
                            TypedDecisionSampleStore store = new TypedDecisionSampleStore(dataDirectory, Quiet());
                            TypedDecisionRecorder recorder = new TypedDecisionRecorder(db.Driver, Quiet(), store, () => settings.TypedDecisions);
                            // The provider is down, so every seam records exactly one unavailable call and keeps its rule.
                            FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Unavailable("http_429"));

                            string? expectedMission = await driver.Drive(new DriveContext
                            {
                                Database = db.Driver,
                                Settings = settings,
                                Client = client,
                                Recorder = recorder
                            }).ConfigureAwait(false);

                            List<RecordedSubject> events = await DecisionEventsAsync(db.Driver, driver.DecisionPoint).ConfigureAwait(false);
                            AssertTrue(events.Count >= 1, label + " recorded a decision event, so the checks below read a real call");
                            foreach (RecordedSubject subject in events)
                            {
                                AssertEqual(driver.ExpectedObjectiveId, subject.ObjectiveId, label + " event objective_id");
                                AssertEqual(expectedMission, subject.MissionColumn, label + " event mission column");
                                AssertEqual(expectedMission, subject.PayloadMissionId, label + " event payload mission_id");
                            }

                            List<RetainedSubject> samples = RetainedSamples(store, driver.DecisionPoint);
                            AssertTrue(samples.Count >= 1, label + " retained its state");
                            foreach (RetainedSubject sample in samples)
                            {
                                AssertEqual(driver.ExpectedObjectiveId, sample.ObjectiveId, label + " sample objective_id");
                                AssertEqual(expectedMission, sample.MissionId, label + " sample mission_id");
                            }
                        }
                        finally
                        {
                            SafeDelete(dataDirectory);
                        }
                    }
                }).ConfigureAwait(false);
            }

            await RunTest("TheSubjectIds_NeverChangeTheTransmittedState_OrItsHash", async () =>
            {
                // An objective-scoped seam: the same objective content under two ids sends the same state.
                Func<string, Func<ITypedDecisionClient, TypedDecisionRecorder, TypedDecisionSettings, Task>> preflight = id => (client, recorder, settings) =>
                {
                    Objective objective = LinkObjective();
                    objective.Id = id;
                    return new PreflightTextAdapter(settings, client, recorder, new FakeOwnerDecisionNotePoster(), Quiet())
                        .EvaluateAsync(objective, LinkVessel(), null, new ObjectiveDispatchPreview { VesselId = VesselId }, CancellationToken.None);
                };
                CallRecord first = await CallOnceAsync("preflight", preflight("obj_subject_one")).ConfigureAwait(false);
                CallRecord second = await CallOnceAsync("preflight", preflight("obj_subject_two")).ConfigureAwait(false);
                AssertTrue(first.Events >= 1 && second.Events >= 1, "both preflight calls were recorded");
                AssertEqual("obj_subject_one", first.ObjectiveId, "the first call names its objective");
                AssertEqual("obj_subject_two", second.ObjectiveId, "the second call names its own");
                AssertTrue(first.StateText.Length > 0, "the provider received a state");
                AssertEqual(first.StateText, second.StateText, "preflight sends the same state whatever the objective id");
                AssertEqual(first.StateSha256, second.StateSha256, "so its recorded state hash is the same");

                // An adapter-skeleton seam, with and without the objective id.
                CallRecord withObjective = await CallOnceAsync("stage_necessity", (client, recorder, settings) =>
                {
                    StageNecessityDecisionInput input = StageInput(ObjectiveId);
                    return new TypedStageNecessityAdapter(client, recorder, settings, Quiet()).DecideAsync(input, StageNecessityVerdict.Rule(input.Stages), CancellationToken.None);
                }).ConfigureAwait(false);
                CallRecord withoutObjective = await CallOnceAsync("stage_necessity", (client, recorder, settings) =>
                {
                    StageNecessityDecisionInput input = StageInput(null);
                    return new TypedStageNecessityAdapter(client, recorder, settings, Quiet()).DecideAsync(input, StageNecessityVerdict.Rule(input.Stages), CancellationToken.None);
                }).ConfigureAwait(false);
                AssertTrue(withObjective.Events >= 1 && withoutObjective.Events >= 1, "both stage_necessity calls were recorded");
                AssertEqual(ObjectiveId, withObjective.ObjectiveId, "the id is recorded when known");
                AssertNull(withoutObjective.ObjectiveId, "and absent when not");
                AssertEqual(withObjective.StateText, withoutObjective.StateText, "stage_necessity sends the same state with and without the objective id");
                AssertEqual(withObjective.StateSha256, withoutObjective.StateSha256, "so the state hash is identical");
                AssertFalse(withObjective.StateText.Contains(ObjectiveId, StringComparison.Ordinal), "the objective id never enters the state");

                // A seam that holds only the mission id, with and without it.
                CallRecord withMission = await CallOnceAsync("log_watch", (client, recorder, settings) =>
                    new TypedLogWatchAdapter(client, recorder, settings, Quiet()).DecideAsync(LogWatchInput(MissionId), LogWatchVerdict.OnTrack(), CancellationToken.None)).ConfigureAwait(false);
                CallRecord withoutMission = await CallOnceAsync("log_watch", (client, recorder, settings) =>
                    new TypedLogWatchAdapter(client, recorder, settings, Quiet()).DecideAsync(LogWatchInput(String.Empty), LogWatchVerdict.OnTrack(), CancellationToken.None)).ConfigureAwait(false);
                AssertTrue(withMission.Events >= 1 && withoutMission.Events >= 1, "both log_watch calls were recorded");
                AssertEqual(MissionId, withMission.MissionId, "the mission id is recorded when known");
                AssertNull(withoutMission.MissionId, "and absent when not");
                AssertEqual(withMission.StateText, withoutMission.StateText, "log_watch sends the same state with and without the mission id");
                AssertEqual(withMission.StateSha256, withoutMission.StateSha256, "so the state hash is identical");
                AssertFalse(withMission.StateText.Contains(MissionId, StringComparison.Ordinal), "the mission id never enters the state");
            }).ConfigureAwait(false);
        }
    }
}
