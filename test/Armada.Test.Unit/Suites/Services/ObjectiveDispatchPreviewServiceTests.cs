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

                    Objective objective = harness.CreateReadyObjective("source-glossary-siblings");
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

            await RunTest("Preview inherits mission tier and caps high to mid for Worker coverage", async () =>
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
                    await testDb.Driver.Captains.CreateAsync(new Captain("preview-judge")
                    {
                        Model = "claude-fable-5",
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

                    AssertEqual("mid", workerRole.PreferredModel,
                        "preview must report the same mid tier dispatch persists for an inherited high Worker request");
                    AssertEqual("high", judgeRole.PreferredModel,
                        "preview must still report high for a Judge stage when the mission requests high");
                    AssertEqual(2, workerRole.IdleEligibleCount,
                        "idle captains that satisfy the capped mid tier must count as eligible Worker coverage");
                    AssertTrue(result.IsReady,
                        "preview must be ready when idle mid-tier Workers cover the capped Worker role");
                }
            }).ConfigureAwait(false);

            await RunTest("Preview rejects an unavailable literal model without rewriting the pin", async () =>
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

                    Pipeline pipeline = new Pipeline("LiteralPinPreview");
                    pipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker")
                    };
                    pipeline = await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);

                    Objective objective = harness.CreateReadyObjective("literal-pin-preview");
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
                            new MissionDescription("Implement feature", "Use the requested model")
                            {
                                PreferredModel = "gpt-5.6-sol"
                            }
                        }).ConfigureAwait(false);

                    ObjectiveDispatchRole workerRole = result.RequiredRoles.Single(role => role.Persona == "Worker");
                    AssertEqual("gpt-5.6-sol", workerRole.PreferredModel,
                        "preview must retain the literal model value that dispatch persists");
                    AssertEqual(0, workerRole.EligibleConfiguredCaptainIds.Count,
                        "a different mid-tier model must not satisfy an exact literal pin");
                    AssertFalse(result.IsReady,
                        "preview must reject dispatch when no configured captain serves the literal model pin");
                    AssertTrue(result.Issues.Any(issue => issue.Code == "required_role_has_no_captain"),
                        "the unavailable literal pin must report the standard missing-role error");
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
                    AssertFalse(overridden.IsReady,
                        "one persona override must be eligible for every generated mission assigned to that persona");
                    AssertTrue(overridden.Issues.Any(issue => issue.Code == "assigned_captain_ineligible"),
                        "the incompatible Sol mission must identify the persona override as ineligible");
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
