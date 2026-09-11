namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Covers the read-only objective dispatch preview and its blocking readiness findings.
    /// </summary>
    public sealed class ObjectiveDispatchPreviewServiceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Objective Dispatch Preview Service";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("A configured busy captain proves role readiness without idle capacity", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    harness.Captain.State = CaptainStateEnum.Working;
                    await testDb.Driver.Captains.UpdateAsync(harness.Captain).ConfigureAwait(false);
                    harness.Git.RevisionShaResult = "0123456789abcdef";

                    Objective objective = harness.CreateReadyObjective("busy-capacity-preview");
                    objective.StartFromRef = "accepted-tip";
                    await testDb.Driver.Objectives.CreateAsync(objective).ConfigureAwait(false);

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);

                    AssertTrue(result.IsReady, "A busy captain is configured role coverage. Idle state is capacity, not readiness.");
                    AssertEqual("WorkerOnly", result.PipelineName, "No configured pipeline uses the Worker-only path.");
                    AssertEqual("0123456789abcdef", result.ResolvedStartCommit, "Start ref resolution is reported.");
                    AssertEqual(1, result.RequiredRoles.Count, "Worker-only dispatch has one required role.");
                    AssertEqual(0, result.RequiredRoles[0].IdleEligibleCount, "The eligible captain is busy.");
                    AssertTrue(result.Issues.Any(issue => issue.Code == "required_role_has_no_idle_captain"),
                        "No idle capacity is a warning.");
                    AssertEqual(0, result.ErrorCount, "Busy capacity does not add a blocking finding.");
                    AssertEqual(1, result.WarningCount, "Busy capacity is summarized as one warning.");
                    AssertEqual(2, result.RequiredChecks.Count, "Build and UnitTest are both required by arming settings.");
                }
            }).ConfigureAwait(false);

            await RunTest("An operator vessel cannot hide a multi-vessel objective", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    Vessel second = new Vessel("preview-second", "https://example.test/second.git")
                    {
                        LocalPath = harness.RepositoryDirectory,
                        WorkingDirectory = harness.RepositoryDirectory
                    };
                    second = await testDb.Driver.Vessels.CreateAsync(second).ConfigureAwait(false);
                    Objective objective = harness.CreateReadyObjective("multi-target-preview");
                    objective.VesselIds.Add(second.Id);
                    await testDb.Driver.Objectives.CreateAsync(objective).ConfigureAwait(false);

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(
                        harness.Auth, objective, requestedVesselId: harness.Vessel.Id).ConfigureAwait(false);

                    AssertFalse(result.IsReady, "Exactly one objective target is required even when an operator supplies a vessel.");
                    AssertTrue(result.Issues.Any(issue => issue.Code == "target_vessel_count"),
                        "The target count failure has a stable code.");
                }
            }).ConfigureAwait(false);

            await RunTest("Only Build and UnitTest inputs affect Check readiness", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    harness.Profile.Environments.Add(new WorkflowEnvironmentProfile
                    {
                        EnvironmentName = "production",
                        DeployCommand = "echo deploy"
                    });
                    harness.Profile.RequiredInputs.Add(new WorkflowInputReference
                    {
                        Provider = WorkflowInputReferenceProviderEnum.EnvironmentVariable,
                        Key = "INTENTIONALLY_ABSENT_DEPLOY_INPUT",
                        EnvironmentName = "production"
                    });
                    await testDb.Driver.WorkflowProfiles.UpdateAsync(harness.Profile).ConfigureAwait(false);
                    Objective objective = harness.CreateReadyObjective("check-scope-preview");
                    await testDb.Driver.Objectives.CreateAsync(objective).ConfigureAwait(false);

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);

                    AssertTrue(result.IsReady, "A deploy-only input does not block Build or UnitTest dispatch Checks.");
                    AssertFalse(result.Issues.Any(issue => issue.Code == "required_input_missing"),
                        "The preview does not report an unrelated deploy input.");
                }
            }).ConfigureAwait(false);

            await RunTest("Missing role coverage, UnitTest command, stale preparation, and dependencies are all named", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: false).ConfigureAwait(false);
                    await testDb.Driver.Captains.DeleteAsync(harness.Captain.Id).ConfigureAwait(false);
                    Objective blocker = harness.CreateReadyObjective("preview-blocker");
                    blocker.Status = ObjectiveStatusEnum.Planned;
                    blocker = await testDb.Driver.Objectives.CreateAsync(blocker).ConfigureAwait(false);

                    Objective objective = harness.CreateReadyObjective("blocked-preview");
                    objective.BlockedByObjectiveIds.Add(blocker.Id);
                    objective.Preparation.Claims.Add(new ObjectivePreparationClaim
                    {
                        Id = "opc_stale_preview",
                        Kind = ObjectivePreparationClaimKindEnum.SourcePath,
                        Text = "The source path was checked before the target changed.",
                        State = ObjectivePreparationClaimStateEnum.NeedsRecheck
                    });
                    await testDb.Driver.Objectives.CreateAsync(objective).ConfigureAwait(false);

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);

                    AssertFalse(result.IsReady, "All four faults block dispatch readiness.");
                    AssertTrue(result.Issues.Any(issue => issue.Code == "objective_dependencies_incomplete"), "Dependency failure is named.");
                    AssertTrue(result.BlockingChains.Any(chain => chain.SequenceEqual(new[] { objective.Id, blocker.Id })),
                        "The complete blocking chain is included.");
                    AssertTrue(result.Issues.Any(issue => issue.Code == "preparation_claim_needs_recheck"), "Stale preparation is named.");
                    AssertTrue(result.Issues.Any(issue => issue.Code == "required_role_has_no_captain"), "Missing captain coverage is named.");
                    AssertTrue(result.Issues.Any(issue => issue.Code == "check_command_missing"
                        && issue.RelatedValue == CheckRunTypeEnum.UnitTest.ToString()), "Missing UnitTest command is named.");
                }
            }).ConfigureAwait(false);

            await RunTest("Read-only ReferencePortingTested preview requires TestEngineer coverage", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    harness.Captain.AllowedPersonas = "[\"Worker\",\"PortingReferenceAnalyst\",\"Judge\"]";
                    await testDb.Driver.Captains.UpdateAsync(harness.Captain).ConfigureAwait(false);

                    Pipeline pipeline = new Pipeline("ReferencePortingTested")
                    {
                        Stages = new List<PipelineStage>
                        {
                            new PipelineStage(1, "Worker"),
                            new PipelineStage(2, "PortingReferenceAnalyst"),
                            new PipelineStage(3, "TestEngineer"),
                            new PipelineStage(4, "Judge")
                        }
                    };
                    pipeline = await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);
                    Objective objective = harness.CreateReadyObjective("read-only-reference-preview");
                    objective.Kind = ObjectiveKindEnum.Research;
                    objective = await testDb.Driver.Objectives.CreateAsync(objective).ConfigureAwait(false);

                    MissionModeEnum[] readOnlyModes = new[] { MissionModeEnum.Audit, MissionModeEnum.Research };
                    foreach (MissionModeEnum mode in readOnlyModes)
                    {
                        ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(
                            harness.Auth,
                            objective,
                            null,
                            pipeline.Id,
                            null,
                            new List<MissionDescription>
                            {
                                new MissionDescription("Inspect through every stage", "Report findings only.")
                                {
                                    Mode = mode.ToString()
                                }
                            }).ConfigureAwait(false);

                        AssertFalse(result.IsReady, mode + " preview must fail when TestEngineer has no configured captain.");
                        AssertTrue(result.RequiredRoles.Select(role => role.Persona).SequenceEqual(
                            new[] { "Worker", "PortingReferenceAnalyst", "TestEngineer", "Judge" }),
                            mode + " preview must preserve every declared role in pipeline order.");
                        AssertTrue(result.Issues.Any(issue =>
                                issue.Code == "required_role_has_no_captain"
                                && issue.Message.Contains("TestEngineer", StringComparison.Ordinal)),
                            mode + " preview must name the missing TestEngineer coverage.");
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("Missing sibling artifacts block readiness without provisioning a dock", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    Vessel source = new Vessel("preview-source", "https://example.test/source.git")
                    {
                        LocalPath = harness.RepositoryDirectory,
                        WorkingDirectory = harness.RepositoryDirectory
                    };
                    source = await testDb.Driver.Vessels.CreateAsync(source).ConfigureAwait(false);
                    harness.Vessel.SiblingRepos = JsonSerializer.Serialize(new List<SiblingRepo>
                    {
                        new SiblingRepo
                        {
                            VesselRef = source.Id,
                            RelativePath = "../preview-source",
                            BuildParticipant = true,
                            ExtractionArtifactPaths = new List<string> { "missing-artifacts" }
                        }
                    });
                    await testDb.Driver.Vessels.UpdateAsync(harness.Vessel).ConfigureAwait(false);
                    Objective objective = harness.CreateReadyObjective("artifact-preview");
                    await testDb.Driver.Objectives.CreateAsync(objective).ConfigureAwait(false);

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);

                    AssertFalse(result.IsReady, "A declared artifact input must exist before dispatch.");
                    AssertTrue(result.Issues.Any(issue => issue.Code == "sibling_artifact_missing"),
                        "The missing artifact path has a stable code.");
                    AssertEqual(0, harness.Git.WorktreeCalls.Count, "Preview creates no worktree.");
                    AssertEqual(0, harness.Git.CloneCalls.Count, "Preview clones no repository.");
                }
            }).ConfigureAwait(false);

            await RunTest("A missing dependency has a distinct typed diagnostic", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    Objective objective = harness.CreateReadyObjective("missing-dependency-preview");
                    objective.BlockedByObjectiveIds.Add("missing-objective");

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);

                    AssertFalse(result.IsReady);
                    AssertTrue(result.Issues.Any(issue => issue.Code == "objective_dependency_missing"));
                    AssertTrue(result.DependencyAnalysis.BlockingNodes.Any(node =>
                        node.ObjectiveId == "missing-objective" && node.IsMissing));
                    AssertEqual(ObjectiveDependencyTerminalReasonEnum.Missing,
                        result.DependencyAnalysis.BlockingChains.Single().TerminalReason);
                }
            }).ConfigureAwait(false);

            await RunTest("Operator mission modes select the read-only pipeline path and every start ref is resolved", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    Pipeline defaultPipeline = new Pipeline("Preview default")
                    {
                        Stages = new List<PipelineStage>
                        {
                            new PipelineStage(1, "Worker"),
                            new PipelineStage(2, "Judge")
                        }
                    };
                    defaultPipeline = await testDb.Driver.Pipelines.CreateAsync(defaultPipeline).ConfigureAwait(false);
                    harness.Vessel.DefaultPipelineId = defaultPipeline.Id;
                    await testDb.Driver.Vessels.UpdateAsync(harness.Vessel).ConfigureAwait(false);
                    harness.Git.RevisionShas[harness.RepositoryDirectory + "|known-tip"] = "1111111111111111";
                    harness.Git.RevisionShas[harness.RepositoryDirectory + "|missing-tip"] = null;
                    Objective objective = harness.CreateReadyObjective("operator-mission-preview");
                    List<MissionDescription> missions = new List<MissionDescription>
                    {
                        new MissionDescription("Audit one", "Read only") { Mode = "Audit", StartFromRef = "known-tip" },
                        new MissionDescription("Research two", "Read only") { Mode = "Research", StartFromRef = "missing-tip" }
                    };

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(
                        harness.Auth, objective, null, null, null, missions).ConfigureAwait(false);

                    AssertEqual("WorkerOnly", result.PipelineName, "An all-read-only operator request does not inherit the default pipeline.");
                    AssertEqual("Mixed", result.DeliverableMode, "Mixed read-only modes are reported without losing either mode.");
                    AssertEqual(2, result.MissionStartRefs.Count, "Each operator mission has an effective start-ref result.");
                    AssertEqual("1111111111111111", result.MissionStartRefs[0].ResolvedCommit, "The known mission ref resolves.");
                    AssertTrue(result.Issues.Any(issue => issue.Code == "mission_start_from_ref_missing" && issue.RelatedValue == "missing-tip"),
                        "An unresolved per-mission ref blocks dispatch.");
                }
            }).ConfigureAwait(false);

            await RunTest("Captain fallback tier is part of configured role coverage", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    harness.Captain.Tier = CaptainTierEnum.Standard;
                    await testDb.Driver.Captains.UpdateAsync(harness.Captain).ConfigureAwait(false);
                    Objective objective = harness.CreateReadyObjective("fallback-tier-preview");

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(
                        harness.Auth,
                        objective,
                        null,
                        null,
                        new List<CaptainAssignmentOverride>
                        {
                            new CaptainAssignmentOverride("Worker", null, CaptainTierEnum.Premium)
                        },
                        new List<MissionDescription>
                        {
                            new MissionDescription("Implement", "Change code") { Mode = "Implementation" }
                        }).ConfigureAwait(false);

                    AssertFalse(result.IsReady, "A Standard captain does not cover a Premium fallback requirement.");
                    AssertEqual(0, result.RequiredRoles.Single().EligibleConfiguredCaptainIds.Count,
                        "Role coverage applies the fallback tier before it reports configured captains.");
                    AssertTrue(result.Issues.Any(issue => issue.Code == "required_role_has_no_captain"),
                        "The missing fallback-tier coverage has the standard role error.");
                }
            }).ConfigureAwait(false);
        }

        private sealed class PreviewHarness
        {
            public AuthContext Auth { get; private set; } = null!;
            public Vessel Vessel { get; private set; } = null!;
            public Captain Captain { get; private set; } = null!;
            public WorkflowProfile Profile { get; private set; } = null!;
            public StubGitService Git { get; private set; } = null!;
            public ObjectiveDispatchPreviewService Service { get; private set; } = null!;
            public string RepositoryDirectory { get; private set; } = String.Empty;

            public static async Task<PreviewHarness> CreateAsync(TestDatabase testDb, bool includeUnitTestCommand)
            {
                PreviewHarness result = new PreviewHarness();
                result.RepositoryDirectory = Path.Combine(Path.GetTempPath(), "armada-preview-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(result.RepositoryDirectory);
                result.Auth = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, true, true, "UnitTest");
                result.Vessel = new Vessel("preview-vessel", "https://example.test/preview.git")
                {
                    LocalPath = result.RepositoryDirectory,
                    WorkingDirectory = result.RepositoryDirectory
                };
                result.Vessel = await testDb.Driver.Vessels.CreateAsync(result.Vessel).ConfigureAwait(false);
                result.Captain = await testDb.Driver.Captains.CreateAsync(new Captain("preview-captain")
                {
                    State = CaptainStateEnum.Idle
                }).ConfigureAwait(false);
                result.Profile = new WorkflowProfile
                {
                    Name = "Preview workflow",
                    Scope = WorkflowProfileScopeEnum.Vessel,
                    VesselId = result.Vessel.Id,
                    BuildCommand = "echo build",
                    UnitTestCommand = includeUnitTestCommand ? "echo test" : null
                };
                result.Profile = await testDb.Driver.WorkflowProfiles.CreateAsync(result.Profile).ConfigureAwait(false);
                result.Git = new StubGitService();
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                ArmadaSettings settings = new ArmadaSettings();
                WorkflowProfileService profiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, profiles, logging);
                result.Service = new ObjectiveDispatchPreviewService(testDb.Driver, profiles, readiness, result.Git, settings);
                return result;
            }

            public Objective CreateReadyObjective(string title)
            {
                return new Objective
                {
                    Title = title,
                    Description = "Change only the named dispatch behavior.",
                    Status = ObjectiveStatusEnum.Planned,
                    VesselIds = new List<string> { Vessel.Id },
                    RefinementSummary = "Reuse the existing shared dispatch services.",
                    AcceptanceCriteria = new List<string> { "The focused behavior is verified." }
                };
            }
        }
    }
}
