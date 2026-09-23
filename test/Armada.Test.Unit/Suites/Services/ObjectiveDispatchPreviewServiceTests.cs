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
    using Armada.Core.Services.Interfaces;
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
                    harness.Git.RevisionCommitShaResult = "0123456789abcdef0123456789abcdef01234567";

                    Objective objective = harness.CreateReadyObjective("busy-capacity-preview");
                    objective.StartFromRef = "accepted-tip";
                    await testDb.Driver.Objectives.CreateAsync(objective).ConfigureAwait(false);

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);

                    AssertTrue(result.IsReady, "A busy captain is configured role coverage. Idle state is capacity, not readiness.");
                    AssertEqual("WorkerOnly", result.PipelineName, "No configured pipeline uses the Worker-only path.");
                    AssertEqual("0123456789abcdef0123456789abcdef01234567", result.ResolvedStartCommit, "Full start-ref resolution is reported.");
                    AssertTrue(harness.Git.RevisionCommitShaCalls.Contains(harness.RepositoryDirectory + "|accepted-tip"), "Objective preview uses strict commit resolution.");
                    AssertEqual(1, result.RequiredRoles.Count, "Worker-only dispatch has one required role.");
                    AssertEqual(0, result.RequiredRoles[0].IdleEligibleCount, "The eligible captain is busy.");
                    AssertTrue(result.Issues.Any(issue => issue.Code == "required_role_has_no_idle_captain"),
                        "No idle capacity is a warning.");
                    AssertEqual(0, result.ErrorCount, "Busy capacity does not add a blocking finding.");
                    AssertEqual(1, result.WarningCount, "Busy capacity is summarized as one warning.");
                    AssertEqual(2, result.RequiredChecks.Count, "Build and UnitTest are both required by arming settings.");
                }
            }).ConfigureAwait(false);

            await RunTest("A readable native library does not make prepared research ready when the captain cannot load it", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    // The dependency file is readable, which is all a host-path check proves. The captain
                    // environment runs a different operating system and has no loader for the format, so the
                    // work cannot run there and dispatch preview must say so for each missing requirement.
                    const string libraryPath = "/opt/example/example-native-library.bin";
                    FakeCaptainExecutionEnvironmentProbe environment = new FakeCaptainExecutionEnvironmentProbe
                    {
                        OperatingSystem = "Linux",
                        Architecture = "X64"
                    };
                    environment.Paths.Add(libraryPath);
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true, executionEnvironment: environment).ConfigureAwait(false);

                    Objective objective = harness.CreateReadyObjective("native-library-preview");
                    objective.Preparation.ExecutionRequirements = new ObjectiveExecutionRequirements
                    {
                        OperatingSystem = "Windows",
                        Architecture = "X64",
                        Executables = new List<string> { "example-format-loader" },
                        DependencyPaths = new List<string> { libraryPath },
                        LicensedContext = "example-license"
                    };
                    await testDb.Driver.Objectives.CreateAsync(objective).ConfigureAwait(false);

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    List<ObjectiveDispatchPreviewIssue> execution = result.Issues.Where(issue => issue.Area == "execution").ToList();

                    AssertFalse(result.IsReady, "a readable dependency alone must not make the objective ready");
                    AssertTrue(execution.Any(issue => issue.Code == "execution_operating_system_unavailable"), "the operating system mismatch is named");
                    AssertTrue(execution.Any(issue => issue.Code == "execution_executable_unavailable" && issue.RelatedValue == "example-format-loader"), "the missing loader is named");
                    AssertTrue(execution.Any(issue => issue.Code == "execution_licensed_context_unavailable"), "the unavailable licensed context is named");
                    AssertFalse(execution.Any(issue => issue.Code == "execution_architecture_unavailable"), "a matching architecture is not reported");
                    AssertFalse(execution.Any(issue => issue.Code == "execution_dependency_unavailable"), "the readable dependency is not reported");
                    AssertTrue(execution.All(issue => issue.Severity == ReadinessSeverityEnum.Error), "every unavailable requirement is blocking");
                    AssertContains("### Execution Environment Requirements", result.RenderedBrief, "the declared requirements reach the brief");
                }
            }).ConfigureAwait(false);

            await RunTest("Satisfied execution requirements add no finding", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FakeCaptainExecutionEnvironmentProbe environment = new FakeCaptainExecutionEnvironmentProbe
                    {
                        OperatingSystem = "Windows",
                        Architecture = "Arm64",
                        IsContainer = true
                    };
                    environment.Executables.Add("example-format-loader");
                    environment.Paths.Add("/opt/example/data");
                    environment.AvailableLicensedContexts.Add("example-license");
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true, executionEnvironment: environment).ConfigureAwait(false);

                    Objective objective = harness.CreateReadyObjective("satisfied-execution-preview");
                    objective.Preparation.ExecutionRequirements = new ObjectiveExecutionRequirements
                    {
                        OperatingSystem = "windows",
                        Architecture = "arm64",
                        Executables = new List<string> { "example-format-loader" },
                        DependencyPaths = new List<string> { "/opt/example/data" },
                        IsolationBoundary = "Container",
                        LicensedContext = "EXAMPLE-LICENSE"
                    };
                    await testDb.Driver.Objectives.CreateAsync(objective).ConfigureAwait(false);

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);

                    AssertFalse(result.Issues.Any(issue => issue.Area == "execution"), "a satisfied environment adds no execution finding");
                    AssertTrue(result.IsReady, "the objective stays ready");
                }
            }).ConfigureAwait(false);

            await RunTest("The captain environment probe resolves executables without running them", () =>
            {
                string directory = Path.Combine(Path.GetTempPath(), "armada-probe-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                try
                {
                    string marker = Path.Combine(directory, "ran.marker");
                    string executable = Path.Combine(directory, "example-format-loader");
                    File.WriteAllText(executable, "#!/bin/sh\ntouch '" + marker + "'\n");

                    ArmadaSettings settings = new ArmadaSettings();
                    settings.AvailableLicensedContexts.Add("example-license");
                    LocalCaptainExecutionEnvironmentProbe probe = new LocalCaptainExecutionEnvironmentProbe(settings, directory);

                    AssertEqual(executable, probe.ResolveExecutable("example-format-loader"), "an executable on the captain PATH resolves");
                    AssertNull(probe.ResolveExecutable("missing-loader"), "a missing executable does not resolve");
                    AssertFalse(File.Exists(marker), "resolving an executable never runs it");
                    AssertTrue(probe.PathExists(executable), "an existing path is reported");
                    AssertFalse(probe.PathExists(Path.Combine(directory, "absent")), "an absent path is not reported");
                    AssertTrue(probe.LicensedContexts.Contains("example-license"), "licensed contexts come from settings by name");
                }
                finally
                {
                    try { Directory.Delete(directory, true); } catch { }
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

            await RunTest("Required preparation blocks missing anchors and evidence-backed kinds", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    Objective objective = harness.CreateReadyObjective("required-preparation-preview");
                    objective.Preparation = new ObjectivePreparation
                    {
                        RequiredForDispatch = true,
                        RequiredClaimKinds = new List<ObjectivePreparationClaimKindEnum>
                        {
                            ObjectivePreparationClaimKindEnum.SourcePath,
                            ObjectivePreparationClaimKindEnum.ProvisioningRequirement
                        },
                        Claims = new List<ObjectivePreparationClaim>
                        {
                            new ObjectivePreparationClaim
                            {
                                Id = "unverified-source",
                                Kind = ObjectivePreparationClaimKindEnum.SourcePath,
                                Text = "A path without evidence or verification time is not ready.",
                                State = ObjectivePreparationClaimStateEnum.Verified
                            }
                        }
                    };
                    objective = await testDb.Driver.Objectives.CreateAsync(objective).ConfigureAwait(false);

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertFalse(result.IsReady);
                    AssertTrue(result.Issues.Any(issue => issue.Code == "preparation_source_required"));
                    AssertTrue(result.Issues.Any(issue => issue.Code == "preparation_target_required"));
                    AssertEqual(2, result.Issues.Count(issue => issue.Code == "preparation_required_kind_missing"));
                }
            }).ConfigureAwait(false);

            await RunTest("Required preparation rejects empty kinds and incomplete claims", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    Objective objective = harness.CreateReadyObjective("incomplete-required-preparation");
                    objective.Preparation = new ObjectivePreparation
                    {
                        RequiredForDispatch = true,
                        Source = new ObjectivePreparationAnchor(),
                        Target = new ObjectivePreparationAnchor(),
                        Claims = new List<ObjectivePreparationClaim>
                        {
                            new ObjectivePreparationClaim
                            {
                                Id = "incomplete",
                                Text = "Unverified evidence-free preparation",
                                State = ObjectivePreparationClaimStateEnum.Verified
                            }
                        }
                    };

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);

                    AssertFalse(result.IsReady);
                    AssertTrue(result.Issues.Any(issue => issue.Code == "preparation_required_kinds_missing"));
                    AssertTrue(result.Issues.Any(issue => issue.Code == "preparation_claim_verification_time_missing"));
                    AssertTrue(result.Issues.Any(issue => issue.Code == "preparation_claim_evidence_missing"));
                }
            }).ConfigureAwait(false);

            await RunTest("Required sibling inputs must match vessel declarations and artifacts", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    Vessel requiredSource = await testDb.Driver.Vessels.CreateAsync(new Vessel("ReferenceSource", "https://example.test/reference-source.git")
                    {
                        LocalPath = harness.RepositoryDirectory,
                        WorkingDirectory = harness.RepositoryDirectory
                    }).ConfigureAwait(false);
                    Objective objective = harness.CreateReadyObjective("required-sibling-preview");
                    objective.Preparation.RequiredSiblingInputs.Add(new ObjectivePreparationSiblingInput
                    {
                        VesselRef = requiredSource.Id,
                        RelativePath = "../ReferenceSource",
                        RequiredArtifactPaths = new List<string> { "output/extracted-artifacts" }
                    });

                    ObjectiveDispatchPreview missing = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertTrue(missing.Issues.Any(issue => issue.Code == "required_sibling_not_declared"));

                    harness.Vessel.SiblingRepos = JsonSerializer.Serialize(new List<SiblingRepo>
                    {
                        new SiblingRepo { VesselRef = requiredSource.Name, RelativePath = "..\\ReferenceSource", RepoUrl = "https://example.test/reference-source.git" }
                    });
                    await testDb.Driver.Vessels.UpdateAsync(harness.Vessel).ConfigureAwait(false);
                    ObjectiveDispatchPreview artifactMissing = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertTrue(artifactMissing.Issues.Any(issue => issue.Code == "required_sibling_artifact_not_declared"));
                }
            }).ConfigureAwait(false);

            await RunTest("Three catalogue sibling inputs are provisionable and reach the brief", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    string artifactPath = "output/extracted-artifacts";
                    async Task<Vessel> CreateSiblingAsync(string name, bool withArtifacts)
                    {
                        string workingDirectory = Path.Combine(harness.RepositoryDirectory, name);
                        Directory.CreateDirectory(workingDirectory);
                        if (withArtifacts) Directory.CreateDirectory(Path.Combine(workingDirectory, artifactPath));
                        return await testDb.Driver.Vessels.CreateAsync(new Vessel(name, "https://example.test/" + name + ".git")
                        {
                            LocalPath = workingDirectory,
                            WorkingDirectory = workingDirectory
                        }).ConfigureAwait(false);
                    }

                    Vessel sourceA = await CreateSiblingAsync("CatalogueSourceA", true).ConfigureAwait(false);
                    Vessel sourceB = await CreateSiblingAsync("CatalogueSourceB", true).ConfigureAwait(false);
                    Vessel consumerC = await CreateSiblingAsync("CatalogueConsumerC", false).ConfigureAwait(false);
                    harness.Vessel.SiblingRepos = JsonSerializer.Serialize(new List<SiblingRepo>
                    {
                        new SiblingRepo { VesselRef = sourceA.Name, RelativePath = "../CatalogueSourceA", ExtractionArtifactPaths = new List<string> { artifactPath } },
                        new SiblingRepo { VesselRef = sourceB.Id, RelativePath = "../CatalogueSourceB", ExtractionArtifactPaths = new List<string> { artifactPath } },
                        new SiblingRepo { VesselRef = consumerC.Name, RelativePath = "../CatalogueConsumerC" }
                    });
                    await testDb.Driver.Vessels.UpdateAsync(harness.Vessel).ConfigureAwait(false);

                    Objective objective = harness.CreateReadyObjective("example-siblings");
                    objective.Preparation.RequiredSiblingInputs = new List<ObjectivePreparationSiblingInput>
                    {
                        new ObjectivePreparationSiblingInput { VesselRef = sourceA.Id, RelativePath = "..\\CatalogueSourceA", RequiredArtifactPaths = new List<string> { artifactPath } },
                        new ObjectivePreparationSiblingInput { VesselRef = sourceB.Name, RelativePath = "../CatalogueSourceB", RequiredArtifactPaths = new List<string> { artifactPath } },
                        new ObjectivePreparationSiblingInput { VesselRef = consumerC.Id, RelativePath = "../CatalogueConsumerC" }
                    };

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);

                    AssertTrue(result.IsReady, "All three declared source inputs and their artifacts are ready.");
                    AssertContains("### Required Sibling Inputs", result.RenderedBrief);
                    AssertContains("CatalogueSourceA", result.RenderedBrief);
                    AssertContains("CatalogueSourceB", result.RenderedBrief);
                    AssertContains("CatalogueConsumerC", result.RenderedBrief);
                }
            }).ConfigureAwait(false);

            await RunTest("Preparation anchors require exact full commits", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    const string fullCommit = "0123456789abcdef0123456789abcdef01234567";
                    harness.Git.RevisionCommitShas[harness.RepositoryDirectory + "|main"] = fullCommit;
                    Objective objective = harness.CreateReadyObjective("exact-preparation-anchor");
                    objective.Preparation.Target = new ObjectivePreparationAnchor
                    {
                        VesselId = harness.Vessel.Id,
                        Ref = "main",
                        ResolvedCommit = fullCommit.Substring(0, 7)
                    };

                    ObjectiveDispatchPreview prefix = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertTrue(prefix.Issues.Any(issue => issue.Code == "preparation_target_revision_changed"),
                        "A matching short prefix is not an immutable prepared commit.");

                    objective.Preparation.Target.ResolvedCommit = fullCommit;
                    ObjectiveDispatchPreview exact = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertFalse(exact.Issues.Any(issue => issue.Code == "preparation_target_revision_changed"),
                        "An exact full commit remains current.");
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
                    harness.Git.RevisionCommitShas[harness.RepositoryDirectory + "|known-tip"] = "1111111111111111111111111111111111111111";
                    harness.Git.RevisionCommitShas[harness.RepositoryDirectory + "|missing-tip"] = null;
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
                    AssertEqual("1111111111111111111111111111111111111111", result.MissionStartRefs[0].ResolvedCommit, "The known mission ref resolves.");
                    AssertTrue(harness.Git.RevisionCommitShaCalls.Contains(harness.RepositoryDirectory + "|known-tip"), "Per-mission preview uses strict commit resolution.");
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

            await RunTest("A wildcard captain assignment applies to every role", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    harness.Captain.Tier = CaptainTierEnum.Standard;
                    await testDb.Driver.Captains.UpdateAsync(harness.Captain).ConfigureAwait(false);
                    Objective objective = harness.CreateReadyObjective("wildcard-assignment-preview");

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(
                        harness.Auth,
                        objective,
                        null,
                        null,
                        new List<CaptainAssignmentOverride>
                        {
                            new CaptainAssignmentOverride("*", null, CaptainTierEnum.Premium)
                        },
                        new List<MissionDescription>
                        {
                            new MissionDescription("Implement", "Change code") { Mode = "Implementation" }
                        }).ConfigureAwait(false);

                    AssertFalse(result.IsReady, "The wildcard assignment's Premium fallback tier applies to the Worker role.");
                    AssertEqual(0, result.RequiredRoles.Single().EligibleConfiguredCaptainIds.Count,
                        "A Standard captain does not cover the wildcard Premium fallback requirement.");
                }
            }).ConfigureAwait(false);

            await RunTest("An exact persona assignment wins over a wildcard assignment", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    harness.Captain.Tier = CaptainTierEnum.Standard;
                    await testDb.Driver.Captains.UpdateAsync(harness.Captain).ConfigureAwait(false);
                    Objective objective = harness.CreateReadyObjective("exact-over-wildcard-preview");

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(
                        harness.Auth,
                        objective,
                        null,
                        null,
                        new List<CaptainAssignmentOverride>
                        {
                            new CaptainAssignmentOverride("*", null, CaptainTierEnum.Premium),
                            new CaptainAssignmentOverride("Worker", null, null)
                        },
                        new List<MissionDescription>
                        {
                            new MissionDescription("Implement", "Change code") { Mode = "Implementation" }
                        }).ConfigureAwait(false);

                    AssertEqual(1, result.RequiredRoles.Single().EligibleConfiguredCaptainIds.Count,
                        "The exact Worker assignment has no fallback tier, so the Standard captain covers the role.");
                }
            }).ConfigureAwait(false);

            await RunTest("Preview keeps an inherited high Worker request and counts only captains at or above Premium", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings routingSettings = global::Test.Shared.Infrastructure.FleetRoutingSettings.CreateArmadaSettings();
                    PreviewHarness harness = await PreviewHarness.CreateAsync(
                        testDb,
                        includeUnitTestCommand: true,
                        settings: routingSettings).ConfigureAwait(false);
                    harness.Captain.Model = "gpt-5.6-luna";
                    harness.Captain.Tier = CaptainTierEnum.Premium;
                    harness.Captain.State = CaptainStateEnum.Idle;
                    await testDb.Driver.Captains.UpdateAsync(harness.Captain).ConfigureAwait(false);
                    await testDb.Driver.Captains.CreateAsync(new Captain("preview-standard")
                    {
                        Model = "claude-fable-5",
                        Tier = CaptainTierEnum.Standard,
                        State = CaptainStateEnum.Idle
                    }).ConfigureAwait(false);

                    Pipeline pipeline = new Pipeline("ReviewedPreviewParity");
                    pipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker"),
                        new PipelineStage(2, "Judge")
                    };
                    pipeline = await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);

                    Objective objective = harness.CreateReadyObjective("preview-high-tier-cap");
                    objective.SuggestedPipelineId = pipeline.Id;
                    await testDb.Driver.Objectives.CreateAsync(objective).ConfigureAwait(false);

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(
                        harness.Auth,
                        objective,
                        harness.Vessel.Id,
                        pipeline.Id,
                        null,
                        new List<MissionDescription>
                        {
                            new MissionDescription("Implement feature", "Mission requests high tier routing")
                            {
                                PreferredModel = "high"
                            }
                        }).ConfigureAwait(false);

                    ObjectiveDispatchRole workerRole = result.RequiredRoles.Single(role => role.Persona == "Worker");
                    ObjectiveDispatchRole judgeRole = result.RequiredRoles.Single(role => role.Persona == "Judge");

                    AssertEqual(
                        PreferredModelTierSelector.ResolveEffectivePreferredModel(null, "high", "Worker", routingSettings.ModelTier),
                        workerRole.PreferredModel,
                        "preview reports the tier dispatch persists for an inherited high Worker request");
                    AssertEqual("high", workerRole.PreferredModel, "an inherited high Worker request stays high");
                    AssertEqual("high", judgeRole.PreferredModel, "a Judge stage reports high when the mission requests high");
                    AssertEqual(1, workerRole.IdleEligibleCount,
                        "only the Premium captain meets the high floor; the Standard captain is not Worker coverage");
                    AssertTrue(result.IsReady, "the Premium captain covers the Worker role");
                }
            }).ConfigureAwait(false);

            await RunTest("Preview treats a literal model no captain runs as its tier floor without rewriting the pin", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(
                        testDb,
                        includeUnitTestCommand: true,
                        settings: global::Test.Shared.Infrastructure.FleetRoutingSettings.CreateArmadaSettings()).ConfigureAwait(false);
                    harness.Captain.Model = "gpt-5.6-luna";
                    harness.Captain.Tier = CaptainTierEnum.Premium;
                    harness.Captain.State = CaptainStateEnum.Idle;
                    await testDb.Driver.Captains.UpdateAsync(harness.Captain).ConfigureAwait(false);

                    Pipeline pipeline = new Pipeline("LiteralPinPreview");
                    pipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker")
                    };
                    pipeline = await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);

                    Objective objective = harness.CreateReadyObjective("literal-pin-preview");
                    objective.SuggestedPipelineId = pipeline.Id;
                    await testDb.Driver.Objectives.CreateAsync(objective).ConfigureAwait(false);
                    List<MissionDescription> missions = new List<MissionDescription>
                    {
                        new MissionDescription("Implement feature", "Use the requested model")
                        {
                            PreferredModel = "gpt-5.6-sol"
                        }
                    };

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(
                        harness.Auth, objective, harness.Vessel.Id, pipeline.Id, null, missions).ConfigureAwait(false);

                    ObjectiveDispatchRole workerRole = result.RequiredRoles.Single(role => role.Persona == "Worker");
                    AssertEqual("gpt-5.6-sol", workerRole.PreferredModel,
                        "preview must retain the literal model value that dispatch persists");
                    AssertTrue(workerRole.EligibleConfiguredCaptainIds.Contains(harness.Captain.Id),
                        "a captain at or above the pinned model's tier covers a pin no captain runs, as assignment does");
                    AssertTrue(result.IsReady,
                        "preview must be ready when assignment would fall back to the pinned model's tier floor");

                    harness.Captain.Tier = CaptainTierEnum.Economy;
                    await testDb.Driver.Captains.UpdateAsync(harness.Captain).ConfigureAwait(false);
                    ObjectiveDispatchPreview belowFloor = await harness.Service.PreviewAsync(
                        harness.Auth, objective, harness.Vessel.Id, pipeline.Id, null, missions).ConfigureAwait(false);
                    AssertEqual(0, belowFloor.RequiredRoles.Single(role => role.Persona == "Worker").EligibleConfiguredCaptainIds.Count,
                        "a captain below the pinned model's tier does not cover the pin");
                    AssertTrue(belowFloor.Issues.Any(issue => issue.Code == "required_role_has_no_captain"),
                        "an uncovered pin floor reports the standard missing-role error");
                }
            }).ConfigureAwait(false);

            await RunTest("Preview evaluates distinct mission model pins independently", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(
                        testDb,
                        includeUnitTestCommand: true,
                        settings: global::Test.Shared.Infrastructure.FleetRoutingSettings.CreateArmadaSettings()).ConfigureAwait(false);
                    harness.Captain.Model = "gpt-5.6-luna";
                    harness.Captain.State = CaptainStateEnum.Idle;
                    await testDb.Driver.Captains.UpdateAsync(harness.Captain).ConfigureAwait(false);
                    Captain secondModel = await testDb.Driver.Captains.CreateAsync(new Captain("preview-sol-worker")
                    {
                        Model = "gpt-5.6-sol",
                        State = CaptainStateEnum.Idle
                    }).ConfigureAwait(false);

                    Pipeline pipeline = new Pipeline("MultiPinPreview");
                    pipeline.Stages = new List<PipelineStage> { new PipelineStage(1, "Worker") };
                    pipeline = await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);

                    Objective objective = harness.CreateReadyObjective("multi-pin-preview");
                    objective.SuggestedPipelineId = pipeline.Id;
                    await testDb.Driver.Objectives.CreateAsync(objective).ConfigureAwait(false);
                    List<MissionDescription> missions = new List<MissionDescription>
                    {
                        new MissionDescription("Luna work", "Use Luna") { PreferredModel = "gpt-5.6-luna" },
                        new MissionDescription("Sol work", "Use Sol") { PreferredModel = "gpt-5.6-sol" }
                    };

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(
                        harness.Auth, objective, harness.Vessel.Id, pipeline.Id, null, missions).ConfigureAwait(false);

                    List<ObjectiveDispatchRole> workerRoles = result.RequiredRoles
                        .Where(role => role.Persona == "Worker")
                        .OrderBy(role => role.PreferredModel, StringComparer.Ordinal)
                        .ToList();
                    AssertEqual(2, workerRoles.Count,
                        "each distinct generated mission pin must have its own preview requirement");
                    AssertTrue(workerRoles.Any(role => role.PreferredModel == "gpt-5.6-luna"
                        && role.EligibleConfiguredCaptainIds.Contains(harness.Captain.Id)),
                        "the Luna requirement must use the Luna captain");
                    AssertTrue(workerRoles.Any(role => role.PreferredModel == "gpt-5.6-sol"
                        && role.EligibleConfiguredCaptainIds.Contains(secondModel.Id)),
                        "the Sol requirement must use the Sol captain");
                    AssertTrue(result.IsReady,
                        "separate captains may satisfy separate generated missions for one persona");

                    ObjectiveDispatchPreview overridden = await harness.Service.PreviewAsync(
                        harness.Auth,
                        objective,
                        harness.Vessel.Id,
                        pipeline.Id,
                        new List<CaptainAssignmentOverride>
                        {
                            new CaptainAssignmentOverride("Worker", harness.Captain.Id, null)
                        },
                        missions).ConfigureAwait(false);
                    AssertTrue(overridden.IsReady,
                        "a named captain is the operator's explicit choice of model, so it covers a mission pinned to another model");
                    AssertFalse(overridden.Issues.Any(issue => issue.Code == "assigned_captain_ineligible"),
                        "a model pin does not make an otherwise eligible override ineligible");

                    harness.Captain.AllowedPersonas = "[\"Judge\"]";
                    await testDb.Driver.Captains.UpdateAsync(harness.Captain).ConfigureAwait(false);
                    ObjectiveDispatchPreview outsideAllowList = await harness.Service.PreviewAsync(
                        harness.Auth,
                        objective,
                        harness.Vessel.Id,
                        pipeline.Id,
                        new List<CaptainAssignmentOverride>
                        {
                            new CaptainAssignmentOverride("Worker", harness.Captain.Id, null)
                        },
                        missions).ConfigureAwait(false);
                    AssertFalse(outsideAllowList.IsReady,
                        "an override whose persona allow-list excludes the role is never an eligible choice");
                    AssertTrue(outsideAllowList.Issues.Any(issue => issue.Code == "assigned_captain_ineligible"
                        && issue.Message.Contains("persona", StringComparison.Ordinal)),
                        "the ineligible override is named with its reason");
                }
            }).ConfigureAwait(false);

            await RunTest("A benched persona default captain floors role coverage at its tier", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true, settings: CoverageSettings()).ConfigureAwait(false);
                    harness.Captain.Tier = CaptainTierEnum.Standard;
                    await testDb.Driver.Captains.UpdateAsync(harness.Captain).ConfigureAwait(false);
                    Captain premium = await testDb.Driver.Captains.CreateAsync(new Captain("benched-default")
                    {
                        State = CaptainStateEnum.Benched,
                        Tier = CaptainTierEnum.Premium
                    }).ConfigureAwait(false);
                    await SetPersonaDefaultCaptainAsync(testDb, "Worker", premium.Id).ConfigureAwait(false);
                    Objective objective = harness.CreateReadyObjective("benched-default-preview");

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);

                    AssertEqual(0, result.RequiredRoles.Single().EligibleConfiguredCaptainIds.Count,
                        "assignment makes the default captain the requested captain and floors its fallback at Premium, so the Standard captain never takes the role");
                    AssertFalse(result.IsReady, "a role no captain can take is not ready");
                }
            }).ConfigureAwait(false);

            await RunTest("An override captain with no stored tier floors the fallback at its own tier", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true, settings: CoverageSettings()).ConfigureAwait(false);
                    harness.Captain.Tier = CaptainTierEnum.Standard;
                    await testDb.Driver.Captains.UpdateAsync(harness.Captain).ConfigureAwait(false);
                    Captain premium = await testDb.Driver.Captains.CreateAsync(new Captain("busy-override")
                    {
                        State = CaptainStateEnum.Working,
                        Tier = CaptainTierEnum.Premium
                    }).ConfigureAwait(false);
                    Objective objective = harness.CreateReadyObjective("override-floor-preview");

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(
                        harness.Auth,
                        objective,
                        null,
                        null,
                        new List<CaptainAssignmentOverride> { new CaptainAssignmentOverride("Worker", premium.Id, null) },
                        null).ConfigureAwait(false);

                    ObjectiveDispatchRole role = result.RequiredRoles.Single();
                    AssertEqual(1, role.EligibleConfiguredCaptainIds.Count,
                        "only the override captain can take the role: the fallback floor is its Premium tier");
                    AssertEqual(premium.Id, role.EligibleConfiguredCaptainIds[0], "the override captain covers the role");
                    AssertEqual(0, role.IdleEligibleCount, "the idle Standard captain is below the floor, so no idle capacity is reported");
                }
            }).ConfigureAwait(false);

            await RunTest("Role coverage counts only captains of the vessel's tenant for an admin caller", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true, settings: CoverageSettings()).ConfigureAwait(false);
                    TenantMetadata other = await testDb.Driver.Tenants.CreateAsync(new TenantMetadata("Other preview tenant")).ConfigureAwait(false);
                    harness.Captain.TenantId = other.Id;
                    await testDb.Driver.Captains.UpdateAsync(harness.Captain).ConfigureAwait(false);
                    Objective objective = harness.CreateReadyObjective("tenant-preview");

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);

                    AssertEqual(0, result.RequiredRoles.Single().EligibleConfiguredCaptainIds.Count,
                        "a captain of another tenant never works the vessel's missions");
                    AssertTrue(result.Issues.Any(issue => issue.Code == "required_role_has_no_captain"),
                        "the uncovered role reports the standard missing-role error");
                }
            }).ConfigureAwait(false);

            await RunTest("Smart Routing persona routes that admit no captain leave the role uncovered", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CoverageSettings();
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true, settings: settings).ConfigureAwait(false);
                    harness.Captain.Model = "claude-sonnet-4-6";
                    await testDb.Driver.Captains.UpdateAsync(harness.Captain).ConfigureAwait(false);
                    ApplyRoutes(settings, new List<string> { harness.Captain.Id }, new List<string> { "unused-route-model" });
                    Objective objective = harness.CreateReadyObjective("routes-preview");

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertEqual(0, result.RequiredRoles.Single().EligibleConfiguredCaptainIds.Count,
                        "a route that names only a model no captain runs admits no captain");

                    ApplyRoutes(settings, new List<string> { harness.Captain.Id }, new List<string>());
                    ObjectiveDispatchPreview admitted = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertEqual(1, admitted.RequiredRoles.Single().EligibleConfiguredCaptainIds.Count,
                        "a route that names the captain's account and no model list admits it");
                }
            }).ConfigureAwait(false);

            await RunTest("A captain under a timed quarantine is not role coverage until the quarantine ends", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true, settings: CoverageSettings()).ConfigureAwait(false);
                    harness.Captain.QuarantineUntilUtc = DateTime.UtcNow.AddHours(1);
                    await testDb.Driver.Captains.UpdateAsync(harness.Captain).ConfigureAwait(false);
                    Objective objective = harness.CreateReadyObjective("quarantine-preview");

                    ObjectiveDispatchPreview held = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertEqual(0, held.RequiredRoles.Single().EligibleConfiguredCaptainIds.Count,
                        "an Idle captain whose quarantine deadline is in the future is quarantined, as assignment reads it");

                    harness.Captain.QuarantineUntilUtc = DateTime.UtcNow.AddHours(-1);
                    await testDb.Driver.Captains.UpdateAsync(harness.Captain).ConfigureAwait(false);
                    ObjectiveDispatchPreview released = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertEqual(1, released.RequiredRoles.Single().EligibleConfiguredCaptainIds.Count,
                        "an expired quarantine deadline does not hold the captain");
                }
            }).ConfigureAwait(false);

            await RunTest("Preview role coverage agrees with assignment across the captain routing domain", async () =>
            {
                List<CoverageParityCase> domain = CoverageParityCase.Domain();
                List<string> disagreements = new List<string>();
                int assigned = 0;
                int refused = 0;
                foreach (CoverageParityCase parityCase in domain)
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        CoverageParityVerdict verdict = await EvaluateCoverageParityAsync(testDb, parityCase).ConfigureAwait(false);
                        if (verdict.DispatchAssigns) assigned++;
                        else refused++;
                        if (verdict.PreviewCoverable != verdict.DispatchAssigns)
                        {
                            disagreements.Add(parityCase.Label
                                + ": preview " + (verdict.PreviewCoverable ? "covered" : "uncovered")
                                + ", assignment " + (verdict.DispatchAssigns ? "assigned" : "never assigned"));
                        }
                    }
                }

                foreach (string disagreement in disagreements) Console.WriteLine("  coverage disagreement: " + disagreement);
                AssertTrue(assigned > 0, "the domain holds cases assignment serves");
                AssertTrue(refused > 0, "the domain holds cases assignment refuses, so the comparison is not only yes against yes");
                AssertEqual(0, disagreements.Count,
                    disagreements.Count + " of " + domain.Count + " cases disagree:" + Environment.NewLine
                    + String.Join(Environment.NewLine, disagreements.Take(40)));
            }).ConfigureAwait(false);

            await RunTest("An objective with no preflight block is refused as preflight-incomplete", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    // Before this rule, an otherwise-ready objective with no recorded preflight was
                    // dispatchable. It must now be refused: every question is unanswered, so the preview
                    // is not ready and names the blocking issue.
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    Objective objective = harness.CreateReadyObjective("no-preflight-preview");
                    objective.Preparation.Preflight = new ObjectivePreflight();

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);

                    AssertFalse(result.IsReady, "an objective with no recorded preflight must not be ready");
                    AssertTrue(result.Issues.Any(issue => issue.Code == "objective_preflight_incomplete"
                        && issue.Severity == ReadinessSeverityEnum.Error), "the incomplete preflight is a blocking finding");
                    AssertFalse(result.Preflight.IsComplete, "no recorded answer leaves the preflight incomplete");
                    AssertEqual(ObjectivePreflight.QuestionCount, result.Preflight.IncompleteQuestions.Count,
                        "every unanswered question blocks dispatch");
                }
            }).ConfigureAwait(false);

            await RunTest("A complete preflight admits dispatch", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    Objective objective = harness.CreateReadyObjective("complete-preflight-preview");

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);

                    AssertTrue(result.Preflight.IsComplete, "1-12 yes and 13 no admit dispatch");
                    AssertEqual(0, result.Preflight.IncompleteQuestions.Count, "a complete preflight blocks no question");
                    AssertFalse(result.Issues.Any(issue => issue.Code == "objective_preflight_incomplete"),
                        "a complete preflight adds no blocking finding");
                    AssertTrue(result.IsReady, "an otherwise-ready objective with a complete preflight is ready");
                }
            }).ConfigureAwait(false);

            await RunTest("A no on a required question and a yes on the owner question each block dispatch", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    Objective objective = harness.CreateReadyObjective("blocking-answers-preview");
                    SetAnswer(objective.Preparation.Preflight, 5, ObjectivePreflightAnswerEnum.No);
                    SetAnswer(objective.Preparation.Preflight, ObjectivePreflight.OwnerQuestionNumber, ObjectivePreflightAnswerEnum.Yes);

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);

                    AssertFalse(result.Preflight.IsComplete, "a no on a required question and a yes on the owner question block dispatch");
                    AssertTrue(result.Preflight.IncompleteQuestions.Contains(5), "a no on question 5 blocks it");
                    AssertTrue(result.Preflight.IncompleteQuestions.Contains(ObjectivePreflight.OwnerQuestionNumber),
                        "a yes on the owner question blocks it");
                    AssertEqual(2, result.Preflight.IncompleteQuestions.Count, "no other question blocks");
                }
            }).ConfigureAwait(false);

            await RunTest("Preflight facts compute vessel count and deliverable kind", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    Objective objective = harness.CreateReadyObjective("facts-count-kind-preview");
                    objective.Description = "Commit a census document under the ledger.";
                    objective.Kind = ObjectiveKindEnum.Chore;

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);

                    ObjectiveDispatchPreflightFact vesselFact = result.Preflight.Facts.Single(fact => fact.QuestionNumber == 3);
                    AssertEqual(PreflightFactStatusEnum.Pass, vesselFact.Status, "one target vessel passes question 3");
                    ObjectiveDispatchPreflightFact kindFact = result.Preflight.Facts.Single(fact => fact.QuestionNumber == 2);
                    AssertEqual(PreflightFactStatusEnum.Pass, kindFact.Status, "a committed document under a non-Research kind passes question 2");

                    objective.Description = "Produce a report to the owner.";
                    objective.Kind = ObjectiveKindEnum.Feature;
                    ObjectiveDispatchPreview reportResult = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    ObjectiveDispatchPreflightFact reportKindFact = reportResult.Preflight.Facts.Single(fact => fact.QuestionNumber == 2);
                    AssertEqual(PreflightFactStatusEnum.Fail, reportKindFact.Status, "a report-only deliverable that is not Research fails question 2");
                }
            }).ConfigureAwait(false);

            await RunTest("Preflight facts resolve recover refs and citations at the target tip", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    Objective objective = harness.CreateReadyObjective("facts-citation-preview");
                    objective.Description = "Continue from recover/widget-x-fix at "
                        + "0123456789abcdef0123456789abcdef01234567; see Services/Foo.cs:10 and the type `WidgetDecoderX`.";
                    harness.Git.RevisionCommitShas[harness.RepositoryDirectory + "|recover/widget-x-fix"] =
                        "0123456789abcdef0123456789abcdef01234567";
                    harness.Git.PathsOnRevision.Add("Services/Foo.cs");
                    harness.Git.FoundTermsOnRevision.Add("WidgetDecoderX");

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);

                    AssertEqual(PreflightFactStatusEnum.Pass, result.Preflight.Facts.Single(fact => fact.QuestionNumber == 10).Status,
                        "a resolvable recover ref with a recorded SHA passes question 10");
                    AssertEqual(PreflightFactStatusEnum.Pass, result.Preflight.Facts.Single(fact => fact.QuestionNumber == 1).Status,
                        "a resolvable path and identifier pass the question 1 grep half");

                    // Remove the identifier and the recover ref resolution: both facts must fail.
                    harness.Git.FoundTermsOnRevision.Clear();
                    harness.Git.RevisionCommitShas.Clear();
                    ObjectiveDispatchPreview failResult = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertEqual(PreflightFactStatusEnum.Fail, failResult.Preflight.Facts.Single(fact => fact.QuestionNumber == 10).Status,
                        "an unresolvable recover ref fails question 10");
                    AssertEqual(PreflightFactStatusEnum.Fail, failResult.Preflight.Facts.Single(fact => fact.QuestionNumber == 1).Status,
                        "an unresolvable identifier fails the question 1 grep half");
                }
            }).ConfigureAwait(false);

            await RunTest("A preflight fact compares the declared sibling tip against a cited commit", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    Vessel sibling = await testDb.Driver.Vessels.CreateAsync(new Vessel("preview-sibling", "https://example.test/sibling.git")
                    {
                        LocalPath = harness.RepositoryDirectory,
                        WorkingDirectory = harness.RepositoryDirectory
                    }).ConfigureAwait(false);
                    harness.Vessel.SiblingRepos = JsonSerializer.Serialize(new List<SiblingRepo>
                    {
                        new SiblingRepo { VesselRef = sibling.Id, RelativePath = "../Sibling" }
                    });
                    await testDb.Driver.Vessels.UpdateAsync(harness.Vessel).ConfigureAwait(false);

                    Objective objective = harness.CreateReadyObjective("facts-sibling-preview");
                    objective.Description = "The sibling must be at or after aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.";
                    harness.Git.IsAncestorResult = true;

                    ObjectiveDispatchPreview passResult = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertEqual(PreflightFactStatusEnum.Pass, passResult.Preflight.Facts.Single(fact => fact.QuestionNumber == 11).Status,
                        "a sibling tip that contains the cited commit passes question 11");

                    harness.Git.IsAncestorResult = false;
                    ObjectiveDispatchPreview failResult = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertEqual(PreflightFactStatusEnum.Fail, failResult.Preflight.Facts.Single(fact => fact.QuestionNumber == 11).Status,
                        "a sibling tip behind the cited commit fails question 11");
                }
            }).ConfigureAwait(false);

            await RunTest("The D5 preflight model runs after the deterministic block and adds a blocking flag", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    Objective objective = harness.CreateReadyObjective("d5-preflight-model-preview");

                    // Before: no adapter wired, the preview is fully deterministic and ready.
                    ObjectiveDispatchPreview before = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertTrue(before.IsReady, "the ready objective passes the deterministic block");
                    AssertFalse(before.Issues.Any(issue => issue.Code == PreflightTextAdapter.ModelFlagIssueCode),
                        "no model flag exists before the adapter is wired");
                    AssertTrue(before.Preflight.Facts.Count > 0, "the deterministic block computed its facts first");

                    // After: wire the D5 adapter with a model that flags Q1 at the threshold.
                    Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
                    {
                        ["q1"] = new TypedAnswer { Type = "noul", Noul = 0.96, Confidence = 0.96 }
                    };
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(
                        new TypedDecisionResult { Available = true, Answers = answers, InputTokens = 10, OutputTokens = 4, LatencyMs = 9 });
                    TypedDecisionSettings tdSettings = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
                    tdSettings.Decisions[PreflightTextAdapter.DecisionPoint].Mode = TypedDecisionModeEnum.Gate;
                    tdSettings.Decisions[PreflightTextAdapter.DecisionPoint].GateThreshold = 0.80;
                    TypedDecisionRecorder recorder = new TypedDecisionRecorder(testDb.Driver, new LoggingModule());
                    harness.Service.PreflightAdapter = new PreflightTextAdapter(
                        tdSettings, client, recorder, new FakeOwnerDecisionNotePoster(), new LoggingModule());

                    ObjectiveDispatchPreview after = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);

                    AssertEqual(1, client.Calls, "the preview consulted the model once");
                    AssertTrue(after.Issues.Any(issue => issue.Code == PreflightTextAdapter.ModelFlagIssueCode
                        && issue.Severity == ReadinessSeverityEnum.Error), "the model flag is a blocking Error issue in the preview");
                    AssertEqual(before.ErrorCount + 1, after.ErrorCount, "the model added exactly one blocking finding");
                    AssertFalse(after.IsReady, "a model flag makes the preview not ready, so the scheduler skips it");
                }
            }).ConfigureAwait(false);

            await RunTest("Preview lists effective stages after a stored confirmed skip using PipelineStageSkip", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    Pipeline pipeline = new Pipeline("SkipPreview")
                    {
                        Stages = new List<PipelineStage>
                        {
                            new PipelineStage(1, "Worker"),
                            new PipelineStage(2, "TestEngineer"),
                            new PipelineStage(3, "Judge")
                        }
                    };
                    pipeline = await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);

                    StageSkipRequest skip = new StageSkipRequest
                    {
                        Stages = new List<string> { "TestEngineer" },
                        Reason = "doc-only",
                        ConfirmedBy = "UnitTest",
                        ConfirmedUtc = DateTime.UtcNow
                    };
                    Objective objective = harness.CreateReadyObjective("stored-skip-preview");
                    objective.Preparation.StageSkip = skip;
                    await testDb.Driver.Objectives.CreateAsync(objective).ConfigureAwait(false);

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(
                        harness.Auth, objective, null, pipeline.Id).ConfigureAwait(false);

                    PipelineStageSkipResult applied = PipelineStageSkip.Apply(pipeline, skip);
                    List<string> expected = applied.Pipeline!.Stages.Select(stage => stage.PersonaName).ToList();
                    AssertTrue(result.StageSkipConfirmed, "a stored confirmer is reported");
                    AssertTrue(result.EffectivePipelineStages.SequenceEqual(expected),
                        "preview and dispatch agree on the effective stages");
                    AssertTrue(result.SkippedPipelineStages.SequenceEqual(new[] { "TestEngineer" }),
                        "the dropped persona is listed");
                    AssertTrue(result.RequiredRoles.Select(role => role.Persona).SequenceEqual(expected),
                        "captain coverage follows the skipped pipeline");
                    AssertEqual((string?)null, result.StageSkipRefusalCode, "a legal skip is not a refusal");
                }
            }).ConfigureAwait(false);

            await RunTest("Preview reports an unconfirmed skip without dropping stages", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    Pipeline pipeline = new Pipeline("UnconfirmedSkipPreview")
                    {
                        Stages = new List<PipelineStage>
                        {
                            new PipelineStage(1, "Worker"),
                            new PipelineStage(2, "TestEngineer"),
                            new PipelineStage(3, "Judge")
                        }
                    };
                    pipeline = await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);
                    Objective objective = harness.CreateReadyObjective("unconfirmed-skip-preview");
                    objective.Preparation.StageSkip = new StageSkipRequest
                    {
                        Stages = new List<string> { "TestEngineer" },
                        Reason = "not yet confirmed"
                    };
                    await testDb.Driver.Objectives.CreateAsync(objective).ConfigureAwait(false);

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(
                        harness.Auth, objective, null, pipeline.Id).ConfigureAwait(false);

                    AssertFalse(result.StageSkipConfirmed, "no confirmer means unconfirmed");
                    AssertTrue(result.EffectivePipelineStages.SequenceEqual(new[] { "Worker", "TestEngineer", "Judge" }),
                        "an unconfirmed skip does not drop stages");
                    AssertEqual(0, result.SkippedPipelineStages.Count, "no stage is listed as skipped");
                    AssertTrue(result.Issues.Any(issue => issue.Code == "stage_skip_unconfirmed"),
                        "the unconfirmed skip is named");
                }
            }).ConfigureAwait(false);

            await RunTest("Preview reports the named refusal a stored skip would hit", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    Pipeline pipeline = new Pipeline("JudgeSkipPreview")
                    {
                        Stages = new List<PipelineStage>
                        {
                            new PipelineStage(1, "Worker"),
                            new PipelineStage(2, "Judge")
                        }
                    };
                    pipeline = await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);
                    Objective objective = harness.CreateReadyObjective("judge-skip-preview");
                    objective.Preparation.StageSkip = new StageSkipRequest
                    {
                        Stages = new List<string> { "Judge" },
                        ConfirmedBy = "UnitTest"
                    };
                    await testDb.Driver.Objectives.CreateAsync(objective).ConfigureAwait(false);

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(
                        harness.Auth, objective, null, pipeline.Id).ConfigureAwait(false);

                    AssertEqual(PipelineStageSkip.JudgeRefusedCode, result.StageSkipRefusalCode,
                        "preview names the same refusal dispatch would hit");
                    AssertTrue(result.Issues.Any(issue => issue.Code == PipelineStageSkip.JudgeRefusedCode),
                        "the refusal is a blocking preview issue");
                    AssertTrue(result.EffectivePipelineStages.SequenceEqual(new[] { "Worker", "Judge" }),
                        "a refused skip leaves the pipeline unchanged");
                }
            }).ConfigureAwait(false);
        }

        private static ArmadaSettings CoverageSettings()
        {
            string id = Guid.NewGuid().ToString("N");
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_coverage_docks_" + id);
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_coverage_repos_" + id);
            settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_coverage_logs_" + id);
            return settings;
        }

        private static void ApplyRoutes(ArmadaSettings settings, List<string> accountCaptainIds, List<string> routeModels)
        {
            settings.ModelTier.UsageRouting = new UsageRoutingSettings
            {
                Enabled = true,
                Accounts = new List<UsageAccountSettings>
                {
                    new UsageAccountSettings { Id = "coverage-account", CaptainIds = new List<string>(accountCaptainIds) }
                },
                PersonaRoutes = new Dictionary<string, List<UsageRouteSettings>>
                {
                    ["Worker"] = new List<UsageRouteSettings>
                    {
                        new UsageRouteSettings { AccountId = "coverage-account", Models = new List<string>(routeModels) }
                    }
                }
            };
        }

        private static async Task SetPersonaDefaultCaptainAsync(TestDatabase testDb, string personaName, string captainId)
        {
            Persona? persona = await testDb.Driver.Personas.ReadByNameAsync(personaName).ConfigureAwait(false);
            if (persona == null)
            {
                persona = new Persona(personaName, "persona.worker");
                persona.DefaultCaptainId = captainId;
                await testDb.Driver.Personas.CreateAsync(persona).ConfigureAwait(false);
                return;
            }

            persona.DefaultCaptainId = captainId;
            await testDb.Driver.Personas.UpdateAsync(persona).ConfigureAwait(false);
        }

        // Builds one case's fleet, asks the preview whether the Worker role is covered, then asks real
        // assignment whether it assigns the mission. Working captains are made Idle first: the preview
        // counts a busy captain as coverage, so the question is whether assignment would ever choose one.
        private static async Task<CoverageParityVerdict> EvaluateCoverageParityAsync(TestDatabase testDb, CoverageParityCase parityCase)
        {
            ArmadaSettings settings = CoverageSettings();
            PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true, settings: settings).ConfigureAwait(false);
            await testDb.Driver.Captains.DeleteAsync(harness.Captain.Id).ConfigureAwait(false);
            // Assignment refuses a vessel whose working directory is its repository, so the oracle vessel gets its own.
            harness.Vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_coverage_work_" + Guid.NewGuid().ToString("N"));
            await testDb.Driver.Vessels.UpdateAsync(harness.Vessel).ConfigureAwait(false);

            Captain high = new Captain("parity-high")
            {
                Tier = CaptainTierEnum.Premium,
                Model = "claude-opus-4-7",
                State = parityCase.HighState == "working" ? CaptainStateEnum.Working
                    : parityCase.HighState == "benched" ? CaptainStateEnum.Benched
                    : CaptainStateEnum.Idle
            };
            if (parityCase.HighState == "quarantined-until") high.QuarantineUntilUtc = DateTime.UtcNow.AddHours(1);
            if (parityCase.HighState == "quarantine-expired") high.QuarantineUntilUtc = DateTime.UtcNow.AddHours(-1);
            high = await testDb.Driver.Captains.CreateAsync(high).ConfigureAwait(false);

            Captain low = new Captain("parity-low")
            {
                Tier = CaptainTierEnum.Standard,
                Model = "claude-sonnet-4-6",
                State = CaptainStateEnum.Idle
            };
            if (parityCase.Low == "other-tenant")
            {
                TenantMetadata other = await testDb.Driver.Tenants.CreateAsync(new TenantMetadata("Parity other tenant")).ConfigureAwait(false);
                low.TenantId = other.Id;
            }
            if (parityCase.Low == "locked") low.AllowedPersonas = "[\"Architect\"]";
            low = await testDb.Driver.Captains.CreateAsync(low).ConfigureAwait(false);

            if (parityCase.Routing == "admit-none")
                ApplyRoutes(settings, new List<string> { high.Id, low.Id }, new List<string> { "unused-route-model" });
            else if (parityCase.Routing == "admit-high")
                ApplyRoutes(settings, new List<string> { high.Id }, new List<string>());

            List<CaptainAssignmentOverride>? overrides = null;
            if (parityCase.Request == "override-high")
                overrides = new List<CaptainAssignmentOverride> { new CaptainAssignmentOverride("Worker", high.Id, null) };
            else if (parityCase.Request == "override-low")
                overrides = new List<CaptainAssignmentOverride> { new CaptainAssignmentOverride("Worker", low.Id, null) };
            else if (parityCase.Request == "override-tier-premium")
                overrides = new List<CaptainAssignmentOverride> { new CaptainAssignmentOverride("Worker", null, CaptainTierEnum.Premium) };
            else if (parityCase.Request == "default-high")
                await SetPersonaDefaultCaptainAsync(testDb, "Worker", high.Id).ConfigureAwait(false);
            else if (parityCase.Request == "default-low")
                await SetPersonaDefaultCaptainAsync(testDb, "Worker", low.Id).ConfigureAwait(false);

            Objective objective = harness.CreateReadyObjective("coverage-parity");
            ObjectiveDispatchPreview preview = await harness.Service.PreviewAsync(
                harness.Auth,
                objective,
                harness.Vessel.Id,
                null,
                overrides,
                new List<MissionDescription>
                {
                    new MissionDescription("Implement", "Change code") { Mode = "Implementation", PreferredModel = parityCase.Pin }
                }).ConfigureAwait(false);
            bool previewCoverable = preview.RequiredRoles.Single(role => role.Persona == "Worker").EligibleConfiguredCaptainIds.Count > 0;

            Voyage voyage = new Voyage("coverage parity", "assignment oracle");
            voyage.TenantId = harness.Vessel.TenantId;
            voyage.UserId = harness.Vessel.UserId;
            voyage.CaptainOverridesJson = MissionService.SerializeCaptainOverrides(overrides);
            voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

            Mission mission = new Mission("coverage parity", "assignment oracle");
            mission.TenantId = harness.Vessel.TenantId;
            mission.UserId = harness.Vessel.UserId;
            mission.VesselId = harness.Vessel.Id;
            mission.VoyageId = voyage.Id;
            mission.Persona = "Worker";
            mission.PreferredModel = PreferredModelTierSelector.ResolveEffectivePreferredModel(
                null, parityCase.Pin, settings.ModelTier.MinimumTierForPersona("Worker"));
            mission.Status = MissionStatusEnum.Pending;
            mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

            if (high.State == CaptainStateEnum.Working)
            {
                high.State = CaptainStateEnum.Idle;
                await testDb.Driver.Captains.UpdateAsync(high).ConfigureAwait(false);
            }

            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            StubGitService git = new StubGitService();
            IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
            CaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
            captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(64101);
            MissionService missions = new MissionService(logging, testDb.Driver, settings, dockService, captainService,
                resourcePressureAdmission: global::Test.Shared.Infrastructure.TestResourcePressure.Unconstrained(settings));
            await missions.TryAssignAsync(mission, harness.Vessel).ConfigureAwait(false);
            Mission? stored = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);

            return new CoverageParityVerdict
            {
                PreviewCoverable = previewCoverable,
                DispatchAssigns = !String.IsNullOrEmpty(stored?.CaptainId)
            };
        }

        private sealed class CoverageParityVerdict
        {
            public bool PreviewCoverable { get; set; }
            public bool DispatchAssigns { get; set; }
        }

        private sealed class CoverageParityCase
        {
            public string HighState { get; set; } = "idle";
            public string Low { get; set; } = "own";
            public string Request { get; set; } = "none";
            public string? Pin { get; set; } = null;
            public string Routing { get; set; } = "off";

            public string Label => "high=" + HighState + " low=" + Low + " request=" + Request
                + " pin=" + (Pin ?? "none") + " routing=" + Routing;

            // A Premium captain whose availability varies, a Standard captain whose tenant or persona lock
            // varies, every way a mission requests a captain or tier, a pin one captain runs and a pin no
            // captain runs, and Smart Routing persona routes that admit nobody or only the Premium captain.
            public static List<CoverageParityCase> Domain()
            {
                List<CoverageParityCase> cases = new List<CoverageParityCase>();
                string[] highStates = { "idle", "working", "benched", "quarantined-until", "quarantine-expired" };
                string[] lows = { "own", "other-tenant", "locked" };
                string[] requests = { "none", "override-high", "override-low", "override-tier-premium", "default-high", "default-low" };
                string?[] pins = { null, "claude-opus-4-7", "claude-sonnet-5" };
                foreach (string highState in highStates)
                    foreach (string low in lows)
                        foreach (string request in requests)
                            foreach (string? pin in pins)
                                cases.Add(new CoverageParityCase { HighState = highState, Low = low, Request = request, Pin = pin });

                foreach (string routing in new[] { "admit-none", "admit-high" })
                    foreach (string highState in new[] { "idle", "benched" })
                        foreach (string request in new[] { "none", "override-low", "default-high", "default-low" })
                            cases.Add(new CoverageParityCase { HighState = highState, Request = request, Routing = routing });
                return cases;
            }
        }

        private static void SetAnswer(ObjectivePreflight preflight, int number, ObjectivePreflightAnswerEnum answer)
        {
            ObjectivePreflightAnswer? existing = preflight.Questions.FirstOrDefault(question => question.Number == number);
            if (existing != null) existing.Answer = answer;
            else preflight.Questions.Add(new ObjectivePreflightAnswer { Number = number, Answer = answer });
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

            public static async Task<PreviewHarness> CreateAsync(
                TestDatabase testDb,
                bool includeUnitTestCommand,
                ArmadaSettings? settings = null,
                ICaptainExecutionEnvironmentProbe? executionEnvironment = null)
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
                ArmadaSettings effectiveSettings = settings ?? new ArmadaSettings();
                WorkflowProfileService profiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, profiles, logging);
                result.Service = new ObjectiveDispatchPreviewService(testDb.Driver, profiles, readiness, result.Git, effectiveSettings, executionEnvironment);
                return result;
            }

            public Objective CreateReadyObjective(string title)
            {
                Objective objective = new Objective
                {
                    Title = title,
                    Description = "Change only the named dispatch behavior.",
                    Status = ObjectiveStatusEnum.Planned,
                    VesselIds = new List<string> { Vessel.Id },
                    RefinementSummary = "Reuse the existing shared dispatch services.",
                    AcceptanceCriteria = new List<string> { "The focused behavior is verified." }
                };
                objective.Preparation.Preflight = PreflightTestData.Complete();
                return objective;
            }
        }
    }
}
