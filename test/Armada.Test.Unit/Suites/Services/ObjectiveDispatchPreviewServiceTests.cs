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
    using Microsoft.Data.Sqlite;
    using SyslogLogging;

    /// <summary>
    /// Covers the read-only objective dispatch preview and its blocking readiness findings.
    /// </summary>
    public sealed class ObjectiveDispatchPreviewServiceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Objective Dispatch Preview Service";

        /// <inheritdoc />
        protected override Task AfterTestAsync()
        {
            foreach (string directory in PreviewHarness.CreatedDirectories)
            {
                try
                {
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
            PreviewHarness.CreatedDirectories.Clear();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Repeated rejected frozen-source chains stop only their objective", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    const string source = "0123456789abcdef0123456789abcdef01234567";
                    harness.Git.RevisionCommitShaResult = source;
                    Objective objective = harness.CreateReadyObjective("repeat-review");
                    objective.StartFromRef = source;
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Rejected chain")
                        { TenantId = objective.TenantId, UserId = objective.UserId, Status = VoyageStatusEnum.Failed }).ConfigureAwait(false);
                        objective.VoyageIds.Add(voyage.Id);
                        Mission worker = await testDb.Driver.Missions.CreateAsync(new Mission("Worker", "report")
                        { TenantId = objective.TenantId, UserId = objective.UserId, VoyageId = voyage.Id,
                            Persona = "Worker", StartFromRef = source, Status = MissionStatusEnum.Complete,
                            Mode = MissionModeEnum.Research, AgentOutput = "Reviewed report " + attempt }).ConfigureAwait(false);
                        await testDb.Driver.Missions.CreateAsync(new Mission("Judge", "review")
                        { TenantId = objective.TenantId, UserId = objective.UserId, VoyageId = voyage.Id,
                            Persona = "Judge", Status = MissionStatusEnum.Failed, Mode = MissionModeEnum.Research,
                            DependsOnMissionId = worker.Id,
                            FailureReason = "Judge verdict: NEEDS_REVISION", CompletedUtc = DateTime.UtcNow.AddMinutes(attempt) }).ConfigureAwait(false);
                    }
                    await testDb.Driver.Objectives.CreateAsync(objective).ConfigureAwait(false);
                    ObjectiveDispatchPreview stopped = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertTrue(stopped.Issues.Any(item => item.Code == "objective_repeat_review_stop"), "a third full-chain reissue is refused");
                    AssertEqual(PreflightGateOutcomeEnum.BlockedByOther, ObjectivePreflightGate.Classify(stopped, true), "forcePreflight cannot bypass the review stop");
                    ObjectiveDispatchPreviewIssue stop = stopped.Issues.Single(item => item.Code == "objective_repeat_review_stop");
                    string reportHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes("Reviewed report 1"))).ToLowerInvariant();
                    AssertContains(reportHash, stop.Message, "research preserves the exact reviewed report, not only its source commit");
                    objective.Preparation!.Preflight.ReviewRetry = new ObjectiveReviewRetry
                    {
                        JudgeMissionId = stop.RelatedValue!, CandidateHash = reportHash,
                        InputHash = ObjectiveReviewRepeatGuard.InputHash(objective, source),
                        ChangedInput = "Claimed correction", FindingsEvidence = "Every named finding addressed", AllFindingsAddressed = true
                    };
                    ObjectiveDispatchPreview unchanged = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertTrue(unchanged.Issues.Any(item => item.Code == "objective_repeat_review_stop"), "an attestation alone cannot retry unchanged input");
                    const string nextSource = "abcdef0123456789abcdef0123456789abcdef01";
                    objective.StartFromRef = nextSource;
                    harness.Git.RevisionCommitShaResult = nextSource;
                    objective.Preparation.Preflight.ReviewRetry.InputHash = ObjectiveReviewRepeatGuard.InputHash(objective, nextSource);
                    objective.Preparation.Preflight.ReviewRetry.CandidateHash = source;
                    ObjectiveDispatchPreview wrongCandidate = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertTrue(wrongCandidate.Issues.Any(item => item.Code == "objective_repeat_review_stop"), "a changed input cannot lose the rejected report hash");
                    objective.Preparation.Preflight.ReviewRetry.CandidateHash = reportHash;
                    ObjectiveDispatchPreview retry = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertFalse(retry.Issues.Any(item => item.Code == "objective_repeat_review_stop"), "changed input and the full receipt admit a fresh required review");
                    Objective other = harness.CreateReadyObjective("unrelated-review");
                    ObjectiveDispatchPreview open = await harness.Service.PreviewAsync(harness.Auth, other).ConfigureAwait(false);
                    AssertFalse(open.Issues.Any(item => item.Code == "objective_repeat_review_stop"), "another objective is unaffected");
                }
            }).ConfigureAwait(false);

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

            await RunTest("The preflight text half receives the dock's sibling checkouts, tools and evidence roots", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    // The model judges dock observability, so it must see what a dock holds: a sibling checkout
                    // beside the dock, the tools on the captain PATH, the mounted evidence, and the claims.
                    FakeCaptainExecutionEnvironmentProbe environment = new FakeCaptainExecutionEnvironmentProbe { OperatingSystem = "Linux", IsContainer = true };
                    environment.Executables.Add("git");
                    environment.Executables.Add("python3");
                    environment.Paths.Add("/srv/example/evidence-drop");
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.CaptainEvidenceRoots.Add(new CaptainEvidenceRoot { Path = "/srv/example/evidence-drop" });
                    settings.CaptainEvidenceRoots.Add(new CaptainEvidenceRoot { Path = "/srv/example/absent-drop" });
                    settings.CaptainEvidenceRoots.Add(new CaptainEvidenceRoot { Path = "/srv/example/other-vessel-drop", VesselIds = new List<string> { "vsl_other" } });
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true, settings: settings, executionEnvironment: environment).ConfigureAwait(false);
                    harness.Vessel.SiblingRepos = JsonSerializer.Serialize(new List<SiblingRepo>
                    {
                        new SiblingRepo { RelativePath = "../SharedSource", RepoUrl = "https://example.test/shared-source.git" }
                    });
                    await testDb.Driver.Vessels.UpdateAsync(harness.Vessel).ConfigureAwait(false);

                    TypedDecisionSettings typed = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
                    typed.Decisions[PreflightTextAdapter.DecisionPoint].Mode = TypedDecisionModeEnum.Gate;
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(new TypedDecisionResult
                    {
                        Available = true,
                        Answers = new Dictionary<string, TypedAnswer> { ["q4"] = new TypedAnswer { Type = "noul", Noul = 0.05 } }
                    });
                    harness.Service.PreflightAdapter = new PreflightTextAdapter(typed, client, new TypedDecisionRecorder(testDb.Driver, new LoggingModule()), new LoggingModule());

                    Objective objective = harness.CreateReadyObjective("dock-context-preview");
                    objective.EvidenceLinks = new List<string> { "evidence-drop/capture-summary" };
                    objective.Preparation.ExecutionRequirements = new ObjectiveExecutionRequirements { Executables = new List<string> { "example-format-loader" } };
                    objective.Preparation.Claims.Add(new ObjectivePreparationClaim
                    {
                        Kind = ObjectivePreparationClaimKindEnum.SourcePath,
                        Text = "The reference capture sits in the mounted evidence drop.",
                        EvidenceLinks = new List<string> { "evidence-drop/reference-capture" }
                    });
                    await testDb.Driver.Objectives.CreateAsync(objective).ConfigureAwait(false);

                    await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);

                    AssertEqual(1, client.Calls, "the preflight text half was consulted");
                    string state = FakeTypedDecisionClient.StateText(client.LastRequest);
                    AssertContains("\"measured\":true", state, "the dock was measured");
                    AssertContains("../SharedSource", state, "the provisioned sibling checkout is listed");
                    AssertContains("\"available_tools\":[\"git\",\"python3\"]", state, "the tools on the captain PATH are listed");
                    AssertContains("\"missing_tools\":[\"bash\",\"node\",\"npm\",\"dotnet\",\"rg\",\"jq\",\"curl\",\"example-format-loader\"]", state,
                        "every probed tool that does not resolve, the required executable included, is listed as missing");
                    AssertContains("\"evidence_roots\":[\"evidence-drop\"]", state, "the mounted evidence root is listed by name");
                    AssertContains("\"missing_evidence_roots\":[\"absent-drop\"]", state, "a configured root that does not exist is listed as missing");
                    AssertFalse(state.Contains("other-vessel-drop", StringComparison.Ordinal), "a root for another vessel is not listed");
                    AssertFalse(state.Contains("/srv/example", StringComparison.Ordinal), "no host path reaches the model");
                    AssertContains("The reference capture sits in the mounted evidence drop.", state, "the claim text is in the state");
                    AssertContains("evidence-drop/capture-summary", state, "the objective's evidence links are in the state");
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

            await RunTest("A pinned sibling commit off the provisioned branch refuses dispatch with a named reason", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    string siblingDirectory = Path.Combine(harness.RepositoryDirectory, "pinned-source");
                    Directory.CreateDirectory(siblingDirectory);
                    Vessel requiredSource = await testDb.Driver.Vessels.CreateAsync(new Vessel("PinnedSource", "https://example.test/pinned-source.git")
                    {
                        LocalPath = siblingDirectory,
                        WorkingDirectory = siblingDirectory
                    }).ConfigureAwait(false);
                    harness.Vessel.SiblingRepos = JsonSerializer.Serialize(new List<SiblingRepo>
                    {
                        new SiblingRepo { VesselRef = requiredSource.Id, RelativePath = "../PinnedSource", DefaultBranch = "main" }
                    });
                    await testDb.Driver.Vessels.UpdateAsync(harness.Vessel).ConfigureAwait(false);
                    const string pin = "1234567890abcdef1234567890abcdef12345678";
                    Objective objective = harness.CreateReadyObjective("pinned-sibling-preview");
                    objective.Preparation.RequiredSiblingInputs.Add(new ObjectivePreparationSiblingInput
                    {
                        VesselRef = requiredSource.Id,
                        RelativePath = "../PinnedSource",
                        PinnedCommit = pin
                    });
                    harness.Git.RevisionCommitShas[siblingDirectory + "|" + pin] = pin;
                    harness.Git.RevisionCommitShas[siblingDirectory + "|refs/heads/main"] = "fedcba0987654321fedcba0987654321fedcba09";

                    harness.Git.IsAncestorResult = false;
                    ObjectiveDispatchPreview divergent = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    ObjectiveDispatchPreviewIssue? refused = divergent.Issues.FirstOrDefault(issue => issue.Code == "required_sibling_pin_not_ancestor");
                    AssertNotNull(refused, "a pin that is not an ancestor of the provisioned branch is reported");
                    AssertEqual(ReadinessSeverityEnum.Error, refused!.Severity, "a pin off the provisioned branch refuses dispatch");
                    AssertContains("main", refused.Message, "the refusal names the provisioned branch");
                    AssertFalse(divergent.IsReady);

                    harness.Git.IsAncestorResult = true;
                    ObjectiveDispatchPreview ancestor = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertFalse(ancestor.Issues.Any(issue => issue.Code.StartsWith("required_sibling_pin", StringComparison.Ordinal)),
                        "a pin on the provisioned branch raises no pin issue");

                    harness.Git.RevisionCommitShas[siblingDirectory + "|" + pin] = null;
                    ObjectiveDispatchPreview unresolved = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertTrue(unresolved.Issues.Any(issue => issue.Code == "required_sibling_pin_unresolved" && issue.Severity == ReadinessSeverityEnum.Error),
                        "a pin the sibling repository does not hold refuses dispatch");
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

            await RunTest("A moved prepared branch blocks dispatch only when the move touches a path the objective depends on", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                using (CommitPairFixture fixture = await CommitPairFixture.CreateAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    harness.Vessel.LocalPath = fixture.BarePath;
                    await testDb.Driver.Vessels.UpdateAsync(harness.Vessel).ConfigureAwait(false);
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    harness.Git.CommitPairRead = new GitService(logging).ReadChangedPathStatusesBetweenCommitsAsync;
                    harness.Git.RevisionCommitShas[fixture.BarePath + "|main"] = fixture.BulkCommit;

                    // The claim names new/name.txt. Stage to Bulk adds only bulk/ files; Base to Bulk also renames old/name.txt to new/name.txt.
                    Objective objective = harness.CreateReadyObjective("moved-branch-anchor");
                    objective.Preparation.Claims.Add(new ObjectivePreparationClaim
                    {
                        Kind = ObjectivePreparationClaimKindEnum.SourcePath,
                        Text = "The handler lives in new/name.txt and is reused as is.",
                        DependsOn = ObjectivePreparationDependencyEnum.Target,
                        State = ObjectivePreparationClaimStateEnum.Verified,
                        VerifiedUtc = DateTime.UtcNow
                    });
                    objective.Preparation.Target = new ObjectivePreparationAnchor
                    {
                        VesselId = harness.Vessel.Id,
                        Ref = "main",
                        ResolvedCommit = fixture.StageCommit
                    };

                    ObjectiveDispatchPreview unrelated = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    ObjectiveDispatchPreviewIssue? moved = unrelated.Issues.FirstOrDefault(issue => issue.Code == "preparation_target_revision_moved");
                    AssertNotNull(moved, "A move that touched only unrelated files is reported as a move.");
                    AssertEqual(ReadinessSeverityEnum.Warning, moved!.Severity, "An unrelated move is a warning.");
                    AssertContains(fixture.StageCommit.Substring(0, 12), moved.Message, "The warning names the recorded commit.");
                    AssertContains(fixture.BulkCommit.Substring(0, 12), moved.Message, "The warning names the current tip.");
                    AssertContains(CommitPairFixture.BulkFileCount + " file(s) changed", moved.Message, "The warning names the changed-file count.");
                    AssertFalse(unrelated.Issues.Any(issue => issue.Code == "preparation_target_revision_changed"),
                        "An unrelated move is not a revision change.");
                    AssertTrue(unrelated.IsReady, "The objective stays ready: " + String.Join("; ", unrelated.Issues
                        .Where(issue => issue.Severity == ReadinessSeverityEnum.Error).Select(issue => issue.Code)));
                    AssertContains("commit `" + fixture.BulkCommit + "`", unrelated.RenderedBrief, "The dispatch brief names the current tip.");

                    objective.Preparation.Target.ResolvedCommit = fixture.BaseCommit;
                    ObjectiveDispatchPreview related = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    ObjectiveDispatchPreviewIssue? changed = related.Issues.FirstOrDefault(issue => issue.Code == "preparation_target_revision_changed");
                    AssertNotNull(changed, "A move that edited a claimed file is a revision change.");
                    AssertEqual(ReadinessSeverityEnum.Error, changed!.Severity, "A move that touches a claimed file blocks dispatch.");
                    AssertContains("`new/name.txt`", changed.Message, "The error names the file to recheck.");
                    AssertFalse(related.IsReady, "The objective is not ready until the claim is rechecked.");
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

            await RunTest("A blocker whose stored row cannot be read is named as unreadable, not as missing", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    Objective blocker = harness.CreateReadyObjective("unreadable-blocker");
                    blocker.Status = ObjectiveStatusEnum.Completed;
                    blocker = await testDb.Driver.Objectives.CreateAsync(blocker).ConfigureAwait(false);
                    using (SqliteConnection connection = new SqliteConnection(testDb.ConnectionString))
                    {
                        await connection.OpenAsync().ConfigureAwait(false);
                        using (SqliteCommand command = connection.CreateCommand())
                        {
                            command.CommandText = "UPDATE objectives SET backlog_state = 'NoSuchState' WHERE id = @id;";
                            command.Parameters.AddWithValue("@id", blocker.Id);
                            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                    }

                    Objective objective = harness.CreateReadyObjective("blocked-by-unreadable");
                    objective.BlockedByObjectiveIds.Add(blocker.Id);
                    await testDb.Driver.Objectives.CreateAsync(objective).ConfigureAwait(false);

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);

                    AssertFalse(result.IsReady, "A blocker whose state cannot be read still blocks dispatch.");
                    ObjectiveDispatchPreviewIssue? unreadable = result.Issues.FirstOrDefault(issue => issue.Code == "objective_dependency_unreadable");
                    AssertNotNull(unreadable, "The unreadable blocker has its own code.");
                    AssertContains(blocker.Id, unreadable!.Message, "The issue names the unreadable blocker.");
                    AssertContains("backlog_state", unreadable.Message, "The issue names the field to repair.");
                    AssertFalse(result.Issues.Any(issue => issue.Code == "objective_dependency_missing"),
                        "A blocker that exists is not reported as missing.");
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

            await RunTest("A tenant administrator's override naming another tenant's captain reads exactly like a missing captain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true, settings: CoverageSettings()).ConfigureAwait(false);
                    TenantMetadata other = await testDb.Driver.Tenants.CreateAsync(new TenantMetadata("Other override tenant")).ConfigureAwait(false);
                    Captain foreign = await testDb.Driver.Captains.CreateAsync(new Captain("foreign-override")
                    {
                        TenantId = other.Id,
                        State = CaptainStateEnum.Idle,
                        AllowedPersonas = "[\"Judge\"]"
                    }).ConfigureAwait(false);
                    AuthContext tenantAdmin = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, true, "UnitTest");
                    await OwnHarnessInDefaultTenantAsync(testDb, harness).ConfigureAwait(false);
                    Objective objective = harness.CreateReadyObjective("foreign-override-preview");

                    ObjectiveDispatchPreview foreignResult = await harness.Service.PreviewAsync(
                        tenantAdmin, objective, null, null,
                        new List<CaptainAssignmentOverride> { new CaptainAssignmentOverride("Worker", foreign.Id, null) }, null).ConfigureAwait(false);
                    ObjectiveDispatchPreview missingResult = await harness.Service.PreviewAsync(
                        tenantAdmin, objective, null, null,
                        new List<CaptainAssignmentOverride> { new CaptainAssignmentOverride("Worker", "cpt_absent", null) }, null).ConfigureAwait(false);

                    ObjectiveDispatchPreviewIssue foreignIssue = foreignResult.Issues.Single(issue => issue.Code == "assigned_captain_ineligible");
                    ObjectiveDispatchPreviewIssue missingIssue = missingResult.Issues.Single(issue => issue.Code == "assigned_captain_ineligible");
                    AssertEqual(missingIssue.Message, foreignIssue.Message, "another tenant's captain is described exactly like a missing captain");
                    AssertFalse(foreignResult.RequiredRoles.Any(role => role.EligibleConfiguredCaptainIds.Contains(foreign.Id)),
                        "another tenant's captain never covers a role");

                    ObjectiveDispatchPreview adminResult = await harness.Service.PreviewAsync(
                        harness.Auth, objective, null, null,
                        new List<CaptainAssignmentOverride> { new CaptainAssignmentOverride("Worker", foreign.Id, null) }, null).ConfigureAwait(false);
                    AssertTrue(adminResult.Issues.Single(issue => issue.Code == "assigned_captain_ineligible").Message.Contains("persona", StringComparison.Ordinal),
                        "a global administrator still sees why another tenant's captain cannot take the role");
                }
            }).ConfigureAwait(false);

            await RunTest("A tenant administrator's pipeline override naming another tenant's pipeline reads exactly like a missing pipeline", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true, settings: CoverageSettings()).ConfigureAwait(false);
                    TenantMetadata other = await testDb.Driver.Tenants.CreateAsync(new TenantMetadata("Other pipeline tenant")).ConfigureAwait(false);
                    Pipeline foreign = new Pipeline("ForeignPreviewPipeline") { TenantId = other.Id };
                    foreign.Stages = new List<PipelineStage> { new PipelineStage(1, "Worker") };
                    foreign = await testDb.Driver.Pipelines.CreateAsync(foreign).ConfigureAwait(false);
                    AuthContext tenantAdmin = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, true, "UnitTest");
                    await OwnHarnessInDefaultTenantAsync(testDb, harness).ConfigureAwait(false);
                    Objective objective = harness.CreateReadyObjective("foreign-pipeline-preview");

                    foreach ((string foreignRequest, string missingRequest) in new[] { (foreign.Id, "ppl_missing_preview"), (foreign.Name, "MissingPreviewPipeline") })
                    {
                        ObjectiveDispatchPreview foreignResult = await harness.Service.PreviewAsync(tenantAdmin, objective, null, foreignRequest).ConfigureAwait(false);
                        ObjectiveDispatchPreview missingResult = await harness.Service.PreviewAsync(tenantAdmin, objective, null, missingRequest).ConfigureAwait(false);
                        ObjectiveDispatchPreviewIssue foreignIssue = foreignResult.Issues.Single(issue => issue.Area == "pipeline");
                        ObjectiveDispatchPreviewIssue missingIssue = missingResult.Issues.Single(issue => issue.Area == "pipeline");
                        AssertEqual(missingIssue.Code, foreignIssue.Code, "another tenant's pipeline is refused with the missing-pipeline code");
                        AssertEqual(missingIssue.Message, foreignIssue.Message, "another tenant's pipeline is described exactly like a missing pipeline");
                    }

                    ObjectiveDispatchPreview adminResult = await harness.Service.PreviewAsync(harness.Auth, objective, null, foreign.Id).ConfigureAwait(false);
                    AssertTrue(adminResult.Issues.Any(issue => issue.Code == "pipeline_not_usable"),
                        "a global administrator still learns that the pipeline belongs to another owner");
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

                foreach (CoverageParityCase parityCase in new[]
                {
                    new CoverageParityCase { ModelList = "listed-high" },
                    new CoverageParityCase { ModelList = "listed-low" },
                    new CoverageParityCase { ModelList = "all-unlisted" },
                    new CoverageParityCase { ModelList = "empty-lists" }
                })
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        CoverageParityVerdict verdict = await EvaluateCoverageParityAsync(testDb, parityCase).ConfigureAwait(false);
                        AssertEqual(verdict.PreviewCoverable, verdict.DispatchAssigns,
                            parityCase.ModelList + " preview coverage and live assignment agree");
                        if (parityCase.ModelList == "listed-high")
                        {
                            AssertTrue(verdict.PreviewEligibleCaptainIds.SequenceEqual(new[] { verdict.HighCaptainId }),
                                "the preview admits the listed captain and excludes the unlisted Idle captain");
                            AssertEqual(verdict.HighCaptainId, verdict.AssignedCaptainId,
                                "live assignment selects the listed captain");
                        }
                        else if (parityCase.ModelList == "listed-low")
                        {
                            AssertTrue(verdict.PreviewEligibleCaptainIds.SequenceEqual(new[] { verdict.LowCaptainId }),
                                "the preview admits the listed Standard captain and excludes the unlisted Idle captain");
                            AssertEqual(verdict.LowCaptainId, verdict.AssignedCaptainId,
                                "live assignment selects the listed Standard captain");
                        }
                        else if (parityCase.ModelList == "all-unlisted")
                        {
                            AssertEqual(2, verdict.PreviewEligibleCaptainIds.Count,
                                "the lists name only a model no captain runs, so they fall back to every admitted captain");
                            AssertEqual(null, verdict.PreviewIssueCode, "the fallback role is covered");
                            AssertTrue(verdict.AssignedCaptainId == verdict.HighCaptainId || verdict.AssignedCaptainId == verdict.LowCaptainId,
                                "live assignment falls back instead of waiting without end");
                        }
                        else
                        {
                            AssertEqual(2, verdict.PreviewEligibleCaptainIds.Count,
                                "empty lists retain the legacy coverage behavior");
                            AssertTrue(verdict.AssignedCaptainId == verdict.HighCaptainId || verdict.AssignedCaptainId == verdict.LowCaptainId,
                                "live assignment retains at least one legacy candidate");
                        }
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("Preview idle capacity agrees with assignment when account usage removes idle captains", async () =>
            {
                foreach (CoverageParityCase parityCase in new[]
                {
                    new CoverageParityCase { Usage = "exhausted-all" },
                    new CoverageParityCase { Usage = "exhausted-high", ModelList = "listed-high" },
                    new CoverageParityCase { Usage = "exhausted-high" },
                    new CoverageParityCase { Usage = "normal", ModelList = "listed-high" }
                })
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        CoverageParityVerdict verdict = await EvaluateCoverageParityAsync(testDb, parityCase).ConfigureAwait(false);
                        AssertEqual(verdict.DispatchAssigns, verdict.PreviewIdleEligibleCount > 0,
                            parityCase.Usage + "/" + parityCase.ModelList + ": the preview reports idle capacity exactly when assignment can take a captain now (idle "
                            + verdict.PreviewIdleEligibleCount + ", assigned " + (verdict.AssignedCaptainId ?? "none") + ")");
                    }
                }
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

                    Dictionary<string, PreflightFactStatusEnum> deliveryCases = new Dictionary<string, PreflightFactStatusEnum>
                    {
                        ["Deliver committed tests, not a report-only audit."] = PreflightFactStatusEnum.Pass,
                        ["Deliver a report-only audit."] = PreflightFactStatusEnum.Fail,
                        ["Review uncommitted tests, not a report-only audit."] = PreflightFactStatusEnum.Unknown,
                        ["Deliver committed tests, not a report-only audit. Produce a report to the owner as well."] = PreflightFactStatusEnum.Unknown
                    };
                    foreach (KeyValuePair<string, PreflightFactStatusEnum> delivery in deliveryCases)
                    {
                        objective.Description = delivery.Key;
                        ObjectiveDispatchPreview deliveryResult = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                        AssertEqual(delivery.Value, deliveryResult.Preflight.Facts.Single(fact => fact.QuestionNumber == 2).Status,
                            "delivery negation keeps positive obligations and conflicting evidence distinct: " + delivery.Key);
                    }
                    objective.Kind = ObjectiveKindEnum.Research;
                    objective.Description = "Deliver committed tests, not a report-only audit.";
                    ObjectiveDispatchPreview committedResearch = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertEqual(PreflightFactStatusEnum.Fail, committedResearch.Preflight.Facts.Single(fact => fact.QuestionNumber == 2).Status,
                        "a negated report phrase does not hide a committed-test duty from the Research kind check");

                    objective.Description = "Produce a report to the owner.";
                    objective.Kind = ObjectiveKindEnum.Feature;
                    ObjectiveDispatchPreview reportResult = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    ObjectiveDispatchPreflightFact reportKindFact = reportResult.Preflight.Facts.Single(fact => fact.QuestionNumber == 2);
                    AssertEqual(PreflightFactStatusEnum.Fail, reportKindFact.Status, "a report-only deliverable that is not Research fails question 2");
                    objective.Kind = ObjectiveKindEnum.Refactor;
                    objective.Description = "Delete the two report-only measurement benchmarks from the suite.";
                    ObjectiveDispatchPreview codePhrase = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertNotEqual(PreflightFactStatusEnum.Fail, codePhrase.Preflight.Facts.Single(fact => fact.QuestionNumber == 2).Status,
                        "a bare report-only token that names the code under change does not fail question 2 as Kind Research");
                    objective.Kind = ObjectiveKindEnum.Research;
                    Dictionary<string, PreflightFactStatusEnum> deniedTests = new Dictionary<string, PreflightFactStatusEnum>
                    {
                        ["Produce a report to the owner. No committed tests."] = PreflightFactStatusEnum.Pass,
                        ["No committed tests; deliver a report-only audit."] = PreflightFactStatusEnum.Pass,
                        ["Produce a report to the owner. Do not deliver any committed tests."] = PreflightFactStatusEnum.Pass,
                        ["Produce a report to the owner. No committed tests. Deliver committed tests."] = PreflightFactStatusEnum.Unknown
                    };
                    foreach (KeyValuePair<string, PreflightFactStatusEnum> denied in deniedTests)
                    {
                        objective.Description = denied.Key;
                        ObjectiveDispatchPreview deniedResult = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                        AssertEqual(denied.Value, deniedResult.Preflight.Facts.Single(fact => fact.QuestionNumber == 2).Status,
                            "a denied test commitment does not erase another positive obligation: " + denied.Key);
                    }
                    foreach (var sample in new[]
                    {
                        (Description: "Inspect the existing export and report exact counts. Documentation writes, if needed, are separate committed document work after review.", Expected: PreflightFactStatusEnum.Unknown),
                        (Description: "Produce a report to the owner. Documentation writes, if needed, are separate committed document work after review.", Expected: PreflightFactStatusEnum.Pass),
                        (Description: "Commit the final document at docs/result.md. Optional documentation, if needed, is separate work after review.", Expected: PreflightFactStatusEnum.Fail),
                        (Description: "Commit the final document at docs/result.md; documentation writes, if needed, are separate committed document work after review.", Expected: PreflightFactStatusEnum.Fail),
                        (Description: "Commit the final document and documentation writes, if needed, are separate committed document work after review.", Expected: PreflightFactStatusEnum.Fail)
                    })
                    {
                        objective.Description = sample.Description;
                        ObjectiveDispatchPreview deferred = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                        AssertEqual(sample.Expected, deferred.Preflight.Facts.Single(fact => fact.QuestionNumber == 2).Status,
                            "a separate future clause does not replace the current deliverable: " + sample.Description);
                    }
                    objective.Description = "Produce a report to the owner. Documentation writes, if needed, are separate committed document work after review.";
                    objective.RefinementSummary = null;
                    objective.Preparation.Preflight = new ObjectivePreflight();
                    ObjectiveDispatchPreview incomplete = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertFalse(incomplete.IsReady, "a delivery fact does not waive missing preparation");
                    AssertTrue(incomplete.Issues.Any(issue => issue.Code == "brief_method_missing"));
                    AssertEqual(14, incomplete.Preflight.IncompleteQuestions.Count, "unanswered preflight remains incomplete");

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

            await RunTest("Preflight citations name a moved symbol at its declaration, warn on small drift, skip record ids and flag a case-only path miss", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    Objective objective = harness.CreateReadyObjective("facts-citation-range-preview");
                    harness.Git.PathsOnRevision.Add("Services/Foo.cs");
                    harness.Git.FoundTermsOnRevision.Add("ParseFrame");
                    // A call site near the top and the declaration at line 40: the call is the first occurrence.
                    List<string> fooLines = new List<string> { "class Foo {", "    void Run() { if (Ready) ParseFrame(); }" };
                    for (int line = 3; line < 40; line++) fooLines.Add("    void Filler" + line + "() { }");
                    fooLines.Add("    void ParseFrame() { }");
                    fooLines.Add("}");
                    harness.Git.FileLinesOnRevision["Services/Foo.cs"] = fooLines;

                    objective.Description = "`ParseFrame` (`Services/Foo.cs:40`) reads the frame.";
                    ObjectiveDispatchPreview good = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertEqual(PreflightFactStatusEnum.Pass, good.Preflight.Facts.Single(fact => fact.QuestionNumber == 1).Status,
                        "a range that holds its symbol passes");
                    AssertFalse(good.Issues.Any(issue => issue.Code == ObjectiveDispatchPreviewService.CitationDriftIssueCode),
                        "a citation that holds raises no drift warning");

                    objective.Description = "`ParseFrame` (`Services/Foo.cs:3-4`) reads the frame; see also `Services/Foo.cs:90`.";
                    ObjectiveDispatchPreview stale = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    ObjectiveDispatchPreflightFact staleFact = stale.Preflight.Facts.Single(fact => fact.QuestionNumber == 1);
                    AssertEqual(PreflightFactStatusEnum.Fail, staleFact.Status, "a symbol that moved far from its range fails question 1");
                    AssertContains("`ParseFrame` is not within `Services/Foo.cs:3-4`; it is declared at line 40.", staleFact.Detail,
                        "the moved symbol is named at its declaration, not at the earlier call site");
                    AssertContains("`Services/Foo.cs:90` is past the end of the file (41 lines).", staleFact.Detail,
                        "a range past the end of its file is named");

                    objective.Description = "`ParseFrame` (`Services/Foo.cs:35-36`) reads the frame.";
                    ObjectiveDispatchPreview drifted = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertEqual(PreflightFactStatusEnum.Pass, drifted.Preflight.Facts.Single(fact => fact.QuestionNumber == 1).Status,
                        "a symbol a few lines from its range still names the right code, so question 1 passes");
                    ObjectiveDispatchPreviewIssue drift = drifted.Issues.Single(issue => issue.Code == ObjectiveDispatchPreviewService.CitationDriftIssueCode);
                    AssertEqual(ReadinessSeverityEnum.Warning, drift.Severity, "line drift is a warning, never a dispatch block");
                    AssertContains("it is declared at line 40", drift.Message, "the warning gives the corrected line");

                    // Built from parts so no committed line has the shape of a real record id.
                    string recordId = "obj" + "_abcdefgh" + "_Xy12Zw34Qr";
                    objective.Description = "`ParseFrame` (`Services/Foo.cs:40`) reads the frame; the earlier attempt is `" + recordId + "`.";
                    ObjectiveDispatchPreview withId = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    ObjectiveDispatchPreflightFact idFact = withId.Preflight.Facts.Single(fact => fact.QuestionNumber == 1);
                    AssertEqual(PreflightFactStatusEnum.Pass, idFact.Status, "a backticked record id is not searched for as code. Detail: " + idFact.Detail);

                    objective.Description = "`ParseFrame` (`Services/Foo.cs:40`) reads the frame through `load_settings`.";
                    ObjectiveDispatchPreview snake = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertContains("load_settings", snake.Preflight.Facts.Single(fact => fact.QuestionNumber == 1).Detail,
                        "a snake_case identifier of the short record-id shape, without a record prefix, is still searched for");

                    harness.Git.FileLinesOnRevision.Clear();
                    harness.Git.PathsOnRevision.Clear();
                    harness.Git.TrackedPathSuffixes["services/foo.cs"] = "Services/Foo.cs";
                    objective.Description = "Change services/foo.cs:2 to read the frame.";
                    ObjectiveDispatchPreview misspelled = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    ObjectiveDispatchPreflightFact caseFact = misspelled.Preflight.Facts.Single(fact => fact.QuestionNumber == 1);
                    AssertEqual(PreflightFactStatusEnum.Fail, caseFact.Status, "a path spelled in the wrong case fails question 1");
                    AssertContains("`services/foo.cs` is tracked as `Services/Foo.cs`", caseFact.Detail,
                        "the case-only miss is named with the real path");
                }
            }).ConfigureAwait(false);

            await RunTest("Preflight citations from a start ref off the target branch pass when they resolve at the branch tip, and the fact names the ref", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    const string recoveryCommit = "1111111111111111111111111111111111111111";
                    const string branchTip = "2222222222222222222222222222222222222222";
                    harness.Git.RevisionCommitShas[harness.RepositoryDirectory + "|" + recoveryCommit] = recoveryCommit;
                    harness.Git.RevisionCommitShas[harness.RepositoryDirectory + "|refs/heads/main"] = branchTip;
                    harness.Git.AncestorResults[recoveryCommit + "|" + branchTip] = false;
                    harness.Git.FoundTermsOnRevision.Add("ProbeAccess");

                    // The recovery commit moved the declaration to line 30; the branch holds it at line 10, and
                    // only the branch has the operations document the brief cites.
                    List<string> ProbeLines(int declarationLine)
                    {
                        List<string> lines = new List<string> { "class Probe {" };
                        for (int line = 2; line < declarationLine; line++) lines.Add("    void Filler" + line + "() { }");
                        lines.Add("    bool ProbeAccess() { return true; }");
                        lines.Add("}");
                        return lines;
                    }
                    harness.Git.PathsAtRevision.Add(recoveryCommit + "|src/Probe.cs");
                    harness.Git.PathsAtRevision.Add(branchTip + "|src/Probe.cs");
                    harness.Git.PathsAtRevision.Add(branchTip + "|docs/ops.md");
                    harness.Git.FileLinesAtRevision[recoveryCommit + "|src/Probe.cs"] = ProbeLines(30);
                    harness.Git.FileLinesAtRevision[branchTip + "|src/Probe.cs"] = ProbeLines(10);

                    Objective objective = harness.CreateReadyObjective("facts-recovery-start-citation-preview");
                    objective.StartFromRef = recoveryCommit;
                    objective.Description = "Merge main first. `ProbeAccess` (`src/Probe.cs:10`) checkpoints on close; document it in docs/ops.md:3.";

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertEqual(recoveryCommit, result.ResolvedStartCommit, "the preview starts from the recovery commit");
                    ObjectiveDispatchPreflightFact fact = result.Preflight.Facts.Single(item => item.QuestionNumber == 1);
                    AssertEqual(PreflightFactStatusEnum.Pass, fact.Status,
                        "citations that resolve at the target branch tip pass for a start ref off the branch. Detail: " + fact.Detail);
                    AssertContains("`src/Probe.cs` resolves at branch main (" + branchTip.Substring(0, 12) + ")", fact.Detail,
                        "the fact names the ref where the moved citation resolves");
                    AssertContains("`docs/ops.md` resolves at branch main", fact.Detail, "the fact names the ref for a file only the branch holds");

                    // A citation that holds at neither revision still fails, and the fact names both refs.
                    objective.Description = "Merge main first. `ProbeAccess` (`src/Probe.cs:10`) is documented in docs/gone.md:3.";
                    ObjectiveDispatchPreview missing = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    ObjectiveDispatchPreflightFact missingFact = missing.Preflight.Facts.Single(item => item.QuestionNumber == 1);
                    AssertEqual(PreflightFactStatusEnum.Fail, missingFact.Status, "a file neither revision holds fails question 1");
                    AssertContains("docs/gone.md", missingFact.Detail, "the miss names the file");
                    AssertContains("Read at the start ref " + recoveryCommit.Substring(0, 12) + " and at branch main", missingFact.Detail,
                        "the failure names both refs it read");

                    // A start commit on the target branch is read at the start commit only.
                    harness.Git.AncestorResults[recoveryCommit + "|" + branchTip] = true;
                    objective.Description = "`ProbeAccess` (`src/Probe.cs:10`) checkpoints on close.";
                    ObjectiveDispatchPreview onBranch = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    ObjectiveDispatchPreflightFact onBranchFact = onBranch.Preflight.Facts.Single(item => item.QuestionNumber == 1);
                    AssertEqual(PreflightFactStatusEnum.Fail, onBranchFact.Status,
                        "a start commit on the branch is judged at the start commit, where the citation does not hold");
                    AssertContains("Read at commit " + recoveryCommit.Substring(0, 12) + ".", onBranchFact.Detail, "the fact names the commit it read");
                }
            }).ConfigureAwait(false);

            await RunTest("Preflight citations pass a range inside the body of the declaration they name", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    Objective objective = harness.CreateReadyObjective("facts-citation-body-preview");

                    // Size spans lines 2-31 and Other spans 33-37; Format's body holds closing braces in strings,
                    // a character literal and comments, and closes at line 60.
                    List<string> book = new List<string> { "class Book {", "    public int Size(int lots)", "    {" };
                    for (int line = 4; line <= 30; line++) book.Add("        total += lots * " + line + ";");
                    book.Add("    }");
                    book.Add("");
                    book.Add("    public int Other()");
                    book.Add("    {");
                    for (int line = 35; line <= 36; line++) book.Add("        total -= " + line + ";");
                    book.Add("    }");
                    book.Add("");
                    book.Add("    public string Format()");
                    book.Add("    {");
                    book.Add("        string close = \"}\";");
                    book.Add("        string escaped = \"\\\"}\";");
                    book.Add("        string verbatim = @\"a\"\"}\";");
                    book.Add("        char brace = '}';");
                    book.Add("        // }");
                    book.Add("        /* } */");
                    for (int line = 47; line <= 59; line++) book.Add("        close += " + line + ";");
                    book.Add("    }");
                    book.Add("}");
                    AssertEqual("    }", book[59], "the fixture closes Format at line 60");

                    List<string> report = new List<string> { "def build(rows):", "    total = 0" };
                    for (int line = 3; line <= 19; line++) report.Add("    total += rows[" + line + "]  # {");
                    report.Add("    return total");
                    report.Add("");
                    report.Add("def other():");
                    report.Add("    return 1");

                    harness.Git.PathsOnRevision.Add("Services/Book.cs");
                    harness.Git.PathsOnRevision.Add("tools/report.py");
                    harness.Git.FoundTermsOnRevision.Add("Size");
                    harness.Git.FoundTermsOnRevision.Add("Format");
                    harness.Git.FoundTermsOnRevision.Add("build");
                    harness.Git.FileLinesOnRevision["Services/Book.cs"] = book;
                    harness.Git.FileLinesOnRevision["tools/report.py"] = report;

                    List<CitationCase> cases = new List<CitationCase>
                    {
                        new CitationCase("a range inside a method body", "`Size` (`Services/Book.cs:20-22`) sums the lots.", PreflightFactStatusEnum.Pass),
                        new CitationCase("a range that starts at the declaration", "`Size` (`Services/Book.cs:2-25`) sums the lots.", PreflightFactStatusEnum.Pass),
                        new CitationCase("a range that crosses into the next method", "`Size` (`Services/Book.cs:28-35`) sums the lots.", PreflightFactStatusEnum.Fail,
                            "`Size` is not within `Services/Book.cs:28-35`; it is declared at line 2. Reason: outside the declaration span."),
                        new CitationCase("a range past braces held in strings and comments", "`Format` (`Services/Book.cs:56-58`) builds the text.", PreflightFactStatusEnum.Pass),
                        new CitationCase("a range inside a Python indent block", "`build` (`tools/report.py:15-17`) sums the rows.", PreflightFactStatusEnum.Pass),
                        new CitationCase("a range past the end of a Python indent block", "`build` (`tools/report.py:18-23`) sums the rows.", PreflightFactStatusEnum.Fail),
                        new CitationCase("a range past the end of the file", "`Size` (`Services/Book.cs:90`) sums the lots.", PreflightFactStatusEnum.Fail,
                            "Reason: line out of range."),
                        new CitationCase("a missing file", "Change `Services/Gone.cs:4` to sum the lots.", PreflightFactStatusEnum.Fail,
                            "`Services/Gone.cs:4` does not resolve: file missing.")
                    };
                    foreach (CitationCase testCase in cases)
                    {
                        objective.Description = testCase.Description;
                        ObjectiveDispatchPreview preview = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                        ObjectiveDispatchPreflightFact fact = preview.Preflight.Facts.Single(item => item.QuestionNumber == 1);
                        AssertEqual(testCase.Expected, fact.Status, testCase.Name + " gives question 1 " + testCase.Expected + ". Detail: " + fact.Detail);
                        if (testCase.DetailContains != null)
                            AssertContains(testCase.DetailContains, fact.Detail, testCase.Name + " is named as written with its reason");
                    }
                }
            }).ConfigureAwait(false);

            await RunTest("Preflight citations resolve a bare file name that matches several files by the file whose lines hold it", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    Objective objective = harness.CreateReadyObjective("facts-citation-bare-name-preview");

                    // The application file is short; the test file of the same name holds the cited line.
                    List<string> app = new List<string> { "class Program {", "    static void Main() { }", "}" };
                    List<string> tests = new List<string> { "class Program {" };
                    for (int line = 2; line <= 199; line++) tests.Add("    void Case" + line + "() { }");
                    tests.Add("}");
                    harness.Git.FileLinesOnRevision["src/App/Program.cs"] = app;
                    harness.Git.FileLinesOnRevision["tests/App.Tests/Program.cs"] = tests;
                    harness.Git.TrackedPathCandidates["Program.cs"] = new List<string> { "src/App/Program.cs", "tests/App.Tests/Program.cs" };

                    objective.Description = "Fix the case at Program.cs:150.";
                    ObjectiveDispatchPreview one = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    ObjectiveDispatchPreflightFact oneFact = one.Preflight.Facts.Single(fact => fact.QuestionNumber == 1);
                    AssertEqual(PreflightFactStatusEnum.Pass, oneFact.Status, "one candidate holds the line, so question 1 passes. Detail: " + oneFact.Detail);
                    AssertContains("`Program.cs` names `tests/App.Tests/Program.cs`", oneFact.Detail, "the chosen file is named");

                    objective.Description = "Fix the class at Program.cs:1.";
                    ObjectiveDispatchPreview several = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertEqual(PreflightFactStatusEnum.Pass, several.Preflight.Facts.Single(fact => fact.QuestionNumber == 1).Status,
                        "a line that holds in both candidates passes question 1");
                    ObjectiveDispatchPreviewIssue ambiguous = several.Issues.Single(issue => issue.Code == ObjectiveDispatchPreviewService.CitationAmbiguousIssueCode);
                    AssertEqual(ReadinessSeverityEnum.Warning, ambiguous.Severity, "an ambiguous file name is a warning, never a dispatch block");
                    AssertContains("`src/App/Program.cs`, `tests/App.Tests/Program.cs`", ambiguous.Message, "the warning lists the candidates");

                    objective.Description = "Fix the case at Program.cs:900.";
                    ObjectiveDispatchPreview none = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    ObjectiveDispatchPreflightFact noneFact = none.Preflight.Facts.Single(fact => fact.QuestionNumber == 1);
                    AssertEqual(PreflightFactStatusEnum.Fail, noneFact.Status, "no candidate holds the line, so question 1 fails");
                    AssertContains("`Program.cs:900` does not resolve: ambiguous file name, and no candidate holds the cited lines (`src/App/Program.cs`, `tests/App.Tests/Program.cs`).",
                        noneFact.Detail, "the failure names the citation as written and lists the candidates");
                }
            }).ConfigureAwait(false);

            await RunTest("Preview warns about stored text that names another current objective or a conflicting criterion count", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    Objective objective = harness.CreateReadyObjective("text-lint-preview");
                    objective.ParentObjectiveId = "obj_bbbbbbbb_Par";
                    objective.AcceptanceCriteria = new List<string> { "The first criterion holds.", "The second criterion holds." };
                    objective.Description = "Its parent is obj_bbbbbbbb. Check the widget.";

                    ObjectiveDispatchPreview clean = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertFalse(clean.Issues.Any(issue => issue.Code.StartsWith("objective_text_", StringComparison.Ordinal)),
                        "text that agrees with the record raises no lint warning");

                    objective.Description = "Its parent is obj_bbbbbbbb. The current authorizing objective is obj_cccccccc_Older. "
                        + "This objective has three criteria. Check the widget.";
                    objective.RefinementSummary = "Correction: the objective now has two criteria.";
                    ObjectiveDispatchPreview linted = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);

                    ObjectiveDispatchPreviewIssue ids = linted.Issues.Single(issue => issue.Code == "objective_text_conflicting_ids");
                    AssertEqual(ReadinessSeverityEnum.Warning, ids.Severity, "a conflicting id is a warning, not a dispatch block");
                    AssertContains("The current authorizing objective is obj_cccccccc_Older.", ids.Message,
                        "the warning quotes the sentence that names another current objective");
                    AssertFalse(ids.Message.Contains("Its parent is obj_bbbbbbbb."), "a sentence naming the stored parent is not quoted");

                    ObjectiveDispatchPreviewIssue counts = linted.Issues.Single(issue => issue.Code == "objective_text_conflicting_criterion_counts");
                    AssertEqual(ReadinessSeverityEnum.Warning, counts.Severity);
                    AssertContains("This objective has three criteria.", counts.Message, "the stale count is quoted");
                    AssertContains("Correction: the objective now has two criteria.", counts.Message, "the correction is quoted beside it");
                    AssertTrue(linted.IsReady, "lint warnings never make a ready objective unready");
                }
            }).ConfigureAwait(false);

            await RunTest("Preview flags a stage duty that conflicts with read-only mode or a non-goal and names the rule that wins", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);

                    Objective research = harness.CreateReadyObjective("read-only-run-preview");
                    research.Kind = ObjectiveKindEnum.Research;
                    research.AcceptanceCriteria = new List<string> { "The report cites every branch it read." };
                    ObjectiveDispatchPreview clean = await harness.Service.PreviewAsync(harness.Auth, research).ConfigureAwait(false);
                    AssertFalse(clean.Issues.Any(issue => issue.Code == ObjectiveDispatchPreviewService.StageDutyConflictIssueCode),
                        "a read-only objective whose criteria need no run raises no duty conflict");

                    research.AcceptanceCriteria = new List<string>
                    {
                        "The report cites every branch it read.",
                        "The report quotes 20 class runs and a green solution gate."
                    };
                    ObjectiveDispatchPreview readOnly = await harness.Service.PreviewAsync(harness.Auth, research).ConfigureAwait(false);
                    ObjectiveDispatchPreviewIssue runConflict = readOnly.Issues.Single(issue => issue.Code == ObjectiveDispatchPreviewService.StageDutyConflictIssueCode);
                    AssertEqual(ReadinessSeverityEnum.Warning, runConflict.Severity, "a conflict with a stated winner does not block dispatch");
                    AssertContains("20 class runs", runConflict.Message, "the warning quotes the criterion that needs a run");
                    AssertContains("Rule that wins: the criterion.", runConflict.Message, "the warning records which rule wins");

                    Pipeline pipeline = await testDb.Driver.Pipelines.CreateAsync(new Pipeline("DutyConflictPreview")
                    {
                        Stages = new List<PipelineStage>
                        {
                            new PipelineStage(1, "Worker"),
                            new PipelineStage(2, "TestEngineer"),
                            new PipelineStage(3, "Judge")
                        }
                    }).ConfigureAwait(false);
                    Objective implementation = harness.CreateReadyObjective("non-goal-duty-preview");
                    implementation.NonGoals = new List<string> { "No new tests; the report is the only changed file." };
                    ObjectiveDispatchPreview nonGoal = await harness.Service.PreviewAsync(
                        harness.Auth, implementation, null, pipeline.Id).ConfigureAwait(false);
                    ObjectiveDispatchPreviewIssue testConflict = nonGoal.Issues.Single(issue => issue.Code == ObjectiveDispatchPreviewService.StageDutyConflictIssueCode);
                    AssertContains("TestEngineer", testConflict.Message, "the warning names the conflicting stage");
                    AssertContains("Rule that wins: the non-goal.", testConflict.Message, "the warning records which rule wins");

                    implementation.NonGoals = new List<string> { "Do not change the public reader interface." };
                    ObjectiveDispatchPreview agreed = await harness.Service.PreviewAsync(
                        harness.Auth, implementation, null, pipeline.Id).ConfigureAwait(false);
                    AssertFalse(agreed.Issues.Any(issue => issue.Code == ObjectiveDispatchPreviewService.StageDutyConflictIssueCode),
                        "a TestEngineer stage on an objective that allows test edits raises no duty conflict");
                }
            }).ConfigureAwait(false);

            await RunTest("Preview warns when the planned missions name none of the profile's declared gate inputs", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    Objective objective = harness.CreateReadyObjective("gate-input-preview");
                    Pipeline architectPipeline = await testDb.Driver.Pipelines.CreateAsync(new Pipeline("GateInputArchitect")
                    {
                        Stages = new List<PipelineStage>
                        {
                            new PipelineStage(1, "Architect"),
                            new PipelineStage(2, "Worker"),
                            new PipelineStage(3, "Judge")
                        }
                    }).ConfigureAwait(false);

                    ObjectiveDispatchPreview undeclared = await harness.Service.PreviewAsync(
                        harness.Auth, objective, null, architectPipeline.Id).ConfigureAwait(false);
                    AssertFalse(undeclared.Issues.Any(issue => issue.Code == ObjectiveDispatchPreviewService.GateInputsUnplannedIssueCode),
                        "a profile that declares no gate inputs raises no gate-input warning");

                    harness.Profile.GateInputPaths = new List<string> { "docs/data-dictionary.md", "schema/**/*.sql" };
                    await testDb.Driver.WorkflowProfiles.UpdateAsync(harness.Profile).ConfigureAwait(false);

                    ObjectiveDispatchPreview architect = await harness.Service.PreviewAsync(
                        harness.Auth, objective, null, architectPipeline.Id).ConfigureAwait(false);
                    ObjectiveDispatchPreviewIssue planned = architect.Issues.Single(issue => issue.Code == ObjectiveDispatchPreviewService.GateInputsUnplannedIssueCode);
                    AssertEqual(ReadinessSeverityEnum.Warning, planned.Severity, "the gate-input warning never blocks dispatch");
                    AssertContains("docs/data-dictionary.md", planned.Message, "the warning names each declared gate input");
                    AssertContains("schema/**/*.sql", planned.Message, "the warning names a declared glob as written");
                    AssertContains("Architect", planned.Message, "the warning says the Architect plans the file lists");

                    objective.RolloutConstraints = new List<string> { "No src/ or docs/ file changes." };
                    ObjectiveDispatchPreview docsExcluded = await harness.Service.PreviewAsync(
                        harness.Auth, objective, null, architectPipeline.Id).ConfigureAwait(false);
                    ObjectiveDispatchPreviewIssue narrowed = docsExcluded.Issues.Single(issue => issue.Code == ObjectiveDispatchPreviewService.GateInputsUnplannedIssueCode);
                    AssertEqual("schema/**/*.sql", narrowed.RelatedValue, "a gate input under a directory the constraints forbid is not listed");
                    AssertContains("keep docs/data-dictionary.md out of the change", narrowed.Message, "the warning names the input the constraints exclude");

                    objective.RolloutConstraints = new List<string> { "Change only tests/ExportTests.cs and tests/WriterTests.cs." };
                    ObjectiveDispatchPreview allowList = await harness.Service.PreviewAsync(
                        harness.Auth, objective, null, architectPipeline.Id).ConfigureAwait(false);
                    AssertFalse(allowList.Issues.Any(issue => issue.Code == ObjectiveDispatchPreviewService.GateInputsUnplannedIssueCode),
                        "an allow-list that names no gate input leaves nothing to warn about");
                    objective.RolloutConstraints = new List<string>();

                    List<MissionDescription> withoutInputs = new List<MissionDescription>
                    {
                        new MissionDescription { Title = "Add the export column", Description = "Edit src/Export/Writer.cs and its tests." }
                    };
                    foreach (string? pipelineId in new string?[] { architectPipeline.Id, null })
                    {
                        objective.NonGoals = new List<string> { "Changing src/ or docs/data-dictionary.md." };
                        ObjectiveDispatchPreview nonGoalExcluded = await harness.Service.PreviewAsync(
                            harness.Auth, objective, null, pipelineId, null, withoutInputs).ConfigureAwait(false);
                        ObjectiveDispatchPreviewIssue nonGoalWarning = nonGoalExcluded.Issues.Single(
                            issue => issue.Code == ObjectiveDispatchPreviewService.GateInputsUnplannedIssueCode);
                        AssertEqual("schema/**/*.sql", nonGoalWarning.RelatedValue,
                            "a change named as a non-goal is excluded for both Architect and supplied plans");
                        AssertContains("keep docs/data-dictionary.md out of the change", nonGoalWarning.Message);
                        AssertEqual(ReadinessSeverityEnum.Warning, nonGoalWarning.Severity);

                        objective.NonGoals.Clear();
                        objective.RolloutConstraints = new List<string> { "Changing src/ or docs/data-dictionary.md." };
                        ObjectiveDispatchPreview positiveRollout = await harness.Service.PreviewAsync(
                            harness.Auth, objective, null, pipelineId, null, withoutInputs).ConfigureAwait(false);
                        ObjectiveDispatchPreviewIssue positiveWarning = positiveRollout.Issues.Single(
                            issue => issue.Code == ObjectiveDispatchPreviewService.GateInputsUnplannedIssueCode);
                        AssertContains("docs/data-dictionary.md", positiveWarning.RelatedValue!,
                            "a positive rollout statement does not exclude the files it names");
                        objective.RolloutConstraints.Clear();

                        objective.RolloutConstraints = new List<string> { "No stage commits outside docs/operations.md, src/Reader.cs (comments only), tests/ReaderTests.cs (description and message only), schema/**/*.sql and CHANGELOG.md." };
                        ObjectiveDispatchPreview exclusive = await harness.Service.PreviewAsync(
                            harness.Auth, objective, null, pipelineId, null, withoutInputs).ConfigureAwait(false);
                        ObjectiveDispatchPreviewIssue exclusiveWarning = exclusive.Issues.Single(issue => issue.Code == ObjectiveDispatchPreviewService.GateInputsUnplannedIssueCode);
                        AssertEqual("schema/**/*.sql", exclusiveWarning.RelatedValue, "an exclusive commit allow-list excludes only paths outside its scope");
                        AssertEqual(ReadinessSeverityEnum.Warning, exclusiveWarning.Severity);
                        AssertEqual(nonGoalExcluded.IsReady, exclusive.IsReady, "the warning does not change readiness");

                        objective.RolloutConstraints = new List<string> { "No stage commits outside docs/data-dictionary.md, schema/**/*.sql and CHANGELOG.md." };
                        ObjectiveDispatchPreview included = await harness.Service.PreviewAsync(
                            harness.Auth, objective, null, pipelineId, null, withoutInputs).ConfigureAwait(false);
                        AssertContains("docs/data-dictionary.md", included.Issues.Single(issue => issue.Code == ObjectiveDispatchPreviewService.GateInputsUnplannedIssueCode).RelatedValue!,
                            "an included gate input is not accidentally forbidden by CHANGELOG's name");

                        objective.RolloutConstraints = new List<string> { "No stage commits outside tests/OnlyTests.cs unless the operator approves another path." };
                        ObjectiveDispatchPreview conditional = await harness.Service.PreviewAsync(
                            harness.Auth, objective, null, pipelineId, null, withoutInputs).ConfigureAwait(false);
                        AssertContains("docs/data-dictionary.md", conditional.Issues.Single(issue => issue.Code == ObjectiveDispatchPreviewService.GateInputsUnplannedIssueCode).RelatedValue!,
                            "conditional prose is not the narrow exclusive path-list grammar");

                        objective.RolloutConstraints = new List<string> { "Do not edit docs/data-dictionary.md." };
                        ObjectiveDispatchPreview forbidden = await harness.Service.PreviewAsync(
                            harness.Auth, objective, null, pipelineId, null, withoutInputs).ConfigureAwait(false);
                        AssertEqual("schema/**/*.sql", forbidden.Issues.Single(issue => issue.Code == ObjectiveDispatchPreviewService.GateInputsUnplannedIssueCode).RelatedValue,
                            "explicit forbidding constraints still apply");
                        objective.RolloutConstraints.Clear();
                    }

                    ObjectiveDispatchPreview unplanned = await harness.Service.PreviewAsync(
                        harness.Auth, objective, null, null, null, withoutInputs).ConfigureAwait(false);
                    ObjectiveDispatchPreviewIssue missing = unplanned.Issues.Single(issue => issue.Code == ObjectiveDispatchPreviewService.GateInputsUnplannedIssueCode);
                    AssertContains("docs/data-dictionary.md", missing.Message, "an unnamed gate input is listed");
                    AssertContains("schema/**/*.sql", missing.Message, "an unmatched glob is listed");
                    AssertContains("Paths named in supplied mission descriptions (may include references or exclusions): src/Export/Writer.cs", missing.Message,
                        "the warning names the supplied plan path, rather than only the gate inputs");
                    AssertContains("does not declare which planned paths affect each input", missing.Message,
                        "the warning does not invent a dependency from a source filename to a gate input");
                    ObjectiveDispatchPreview architectWithPlan = await harness.Service.PreviewAsync(
                        harness.Auth, objective, null, architectPipeline.Id, null, withoutInputs).ConfigureAwait(false);
                    AssertContains("Paths named in supplied mission descriptions (may include references or exclusions): src/Export/Writer.cs", architectWithPlan.Issues.Single(
                        issue => issue.Code == ObjectiveDispatchPreviewService.GateInputsUnplannedIssueCode).Message,
                        "an Architect pipeline also retains the concrete supplied plan paths");
                    AssertContains("No planned file paths have been supplied", planned.Message,
                        "an absent Architect plan is identified rather than represented by an invented path");

                    objective.Preparation.GateInputSources = new List<ObjectiveGateInputSources>
                    {
                        new ObjectiveGateInputSources { InputPath = "docs/data-dictionary.md", SourcePaths = new List<string> { "src/Export/**/*.cs" } },
                        new ObjectiveGateInputSources { InputPath = "schema/**/*.sql", SourcePaths = new List<string> { "src/Schema/**/*.cs" } }
                    };
                    ObjectiveDispatchPreview mapped = await harness.Service.PreviewAsync(
                        harness.Auth, objective, null, null, null, withoutInputs).ConfigureAwait(false);
                    ObjectiveDispatchPreviewIssue mappedWarning = mapped.Issues.Single(issue => issue.Code == ObjectiveDispatchPreviewService.GateInputsUnplannedIssueCode);
                    AssertEqual("docs/data-dictionary.md,schema/**/*.sql", mappedWarning.RelatedValue,
                        "path mentions do not prove that another mapped input is unaffected");
                    AssertContains("src/Export/Writer.cs -> docs/data-dictionary.md", mappedWarning.Message,
                        "the warning identifies the planned path and its explicit gate-input dependency");
                    AssertFalse(mappedWarning.Message.Contains("does not declare which", StringComparison.Ordinal),
                        "a mapped warning does not misstate that its dependency is unknown");
                    ObjectiveDispatchPreview mappedArchitect = await harness.Service.PreviewAsync(
                        harness.Auth, objective, null, architectPipeline.Id, null, withoutInputs).ConfigureAwait(false);
                    AssertContains("src/Export/Writer.cs -> docs/data-dictionary.md", mappedArchitect.Issues.Single(
                        issue => issue.Code == ObjectiveDispatchPreviewService.GateInputsUnplannedIssueCode).Message);
                    List<MissionDescription> unrelatedPaths = new List<MissionDescription>
                    {
                        new MissionDescription("Unrelated change", "Edit tests/OtherTests.cs.")
                    };
                    ObjectiveDispatchPreview unrelated = await harness.Service.PreviewAsync(
                        harness.Auth, objective, null, null, null, unrelatedPaths).ConfigureAwait(false);
                    AssertTrue(unrelated.Issues.Any(issue => issue.Code == ObjectiveDispatchPreviewService.GateInputsUnplannedIssueCode),
                        "path mentions are not a complete edit plan and cannot suppress another input");
                    foreach (string ambiguous in new[] { "Edit src/Export", "Edit src/Export/Writer?.cs", "Compare tests/OtherTests.cs", "Investigate export behavior. Do not edit tests/OtherTests.cs.", "Edit `src/Export/Writer With Spaces.cs`." })
                    {
                        ObjectiveDispatchPreview unknownPlan = await harness.Service.PreviewAsync(
                            harness.Auth, objective, null, null, null,
                            new List<MissionDescription> { new MissionDescription("Unknown plan", ambiguous) }).ConfigureAwait(false);
                        AssertTrue(unknownPlan.Issues.Any(issue => issue.Code == ObjectiveDispatchPreviewService.GateInputsUnplannedIssueCode),
                            "ambiguous path mentions retain the planning warning");
                    }
                    objective.Preparation.GateInputSources[0].SourcePaths = new List<string> { "../private" };
                    ObjectiveDispatchPreview invalidMapping = await harness.Service.PreviewAsync(
                        harness.Auth, objective, null, null, null, withoutInputs).ConfigureAwait(false);
                    AssertTrue(invalidMapping.Issues.Any(issue => issue.Code == "gate_input_sources_invalid" && issue.Severity == ReadinessSeverityEnum.Error),
                        "an invalid dependency mapping is rejected rather than suppressing warnings");
                    objective.Preparation.GateInputSources.Clear();

                    List<MissionDescription> withInputs = new List<MissionDescription>
                    {
                        new MissionDescription
                        {
                            Title = "Add the export column",
                            Description = "Edit src/Export/Writer.cs, schema/export/columns.sql and `docs/data-dictionary.md`."
                        }
                    };
                    ObjectiveDispatchPreview covered = await harness.Service.PreviewAsync(
                        harness.Auth, objective, null, null, null, withInputs).ConfigureAwait(false);
                    AssertFalse(covered.Issues.Any(issue => issue.Code == ObjectiveDispatchPreviewService.GateInputsUnplannedIssueCode),
                        "mission descriptions that name every gate input, one through its glob, raise no warning");
                }
            }).ConfigureAwait(false);

            await RunTest("Preflight citations resolve a path under a declared sibling's directory name and a bare name the sibling tracks, so a q1 model flag does not block", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    string siblingDirectory = Path.Combine(harness.RepositoryDirectory, "reference-tree");
                    Directory.CreateDirectory(siblingDirectory);
                    Vessel sibling = await testDb.Driver.Vessels.CreateAsync(new Vessel("ReferenceTree", "https://example.test/reference-tree.git")
                    {
                        LocalPath = siblingDirectory,
                        WorkingDirectory = siblingDirectory
                    }).ConfigureAwait(false);
                    harness.Vessel.SiblingRepos = JsonSerializer.Serialize(new List<SiblingRepo>
                    {
                        new SiblingRepo { VesselRef = sibling.Id, RelativePath = "../ReferenceTree" }
                    });
                    await testDb.Driver.Vessels.UpdateAsync(harness.Vessel).ConfigureAwait(false);

                    // The sibling is declared on the vessel only; the objective names no required sibling input.
                    const string siblingTip = "89abcdef0123456789abcdef0123456789abcdef";
                    harness.Git.RevisionCommitShas[siblingDirectory + "|HEAD"] = siblingTip;
                    harness.Git.PathsOnRevision.Add("output/src/Commands.cs");
                    harness.Git.TrackedPathSuffixesByRepository[siblingDirectory + "|Commands.cs"] = "output/src/Commands.cs";
                    harness.Git.PathsOnRevision.Add("Services/Foo.cs");

                    Objective objective = harness.CreateReadyObjective("facts-sibling-prefix-citation-preview");
                    objective.Description = "Services/Foo.cs:10 says the source scales by 1/128; the source divides by 1.0/128.0 "
                        + "(sibling tree path ReferenceTree/output/src/Commands.cs:912). A committed comment cites Commands.cs:953.";

                    Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
                    {
                        ["q1"] = new TypedAnswer { Type = "noul", Noul = 0.96, Confidence = 0.96 }
                    };
                    TypedDecisionSettings tdSettings = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
                    tdSettings.Decisions[PreflightTextAdapter.DecisionPoint].Mode = TypedDecisionModeEnum.Gate;
                    tdSettings.Decisions[PreflightTextAdapter.DecisionPoint].GateThreshold = 0.80;
                    harness.Service.PreflightAdapter = new PreflightTextAdapter(tdSettings,
                        new FakeTypedDecisionClient(new TypedDecisionResult { Available = true, Answers = answers }),
                        new TypedDecisionRecorder(testDb.Driver, new LoggingModule()), new LoggingModule());

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    ObjectiveDispatchPreflightFact fact = result.Preflight.Facts.Single(item => item.QuestionNumber == 1);
                    AssertEqual(PreflightFactStatusEnum.Pass, fact.Status,
                        "the sibling-prefixed path and the bare sibling name both resolve in the declared sibling. Detail: " + fact.Detail);
                    AssertTrue(harness.Git.PathExistsOnRevisionCalls.Contains(siblingDirectory + "|" + siblingTip + "|output/src/Commands.cs"),
                        "the prefixed citation is read inside the sibling repository at its tip");
                    AssertFalse(result.Issues.Any(issue => issue.Code == PreflightTextAdapter.ModelFlagIssueCode),
                        "a q1 model reading does not block a premise whose citations all resolve");
                    AssertFalse(result.Issues.Any(issue => issue.RelatedValue == "q1"
                        && (issue.Code == PreflightTextAdapter.ModelFlagIssueCode || issue.Code == "preflight_q1_model")),
                        "a q1 model flag is not raised when the citation fact passes and the model names no disputed claim");
                    AssertTrue(result.IsReady, "the preview is ready");

                    // A citation under the sibling's directory that the sibling does not hold still fails, named
                    // against the sibling, and a q1 model reading on it alone stays a warning.
                    objective.Description = "Services/Foo.cs:10 cites ReferenceTree/output/src/Gone.cs:5.";
                    ObjectiveDispatchPreview missing = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    ObjectiveDispatchPreflightFact missingFact = missing.Preflight.Facts.Single(item => item.QuestionNumber == 1);
                    AssertEqual(PreflightFactStatusEnum.Fail, missingFact.Status, "a path the declared sibling does not hold fails question 1");
                    AssertContains("file missing in declared sibling `../ReferenceTree`", missingFact.Detail, "the miss names the sibling");
                    AssertTrue(missingFact.FailsOnlyOutsideTarget, "the only failure lies outside the target repository");
                    AssertFalse(missing.Issues.Any(issue => issue.Code == PreflightTextAdapter.ModelFlagIssueCode),
                        "a q1 model reading does not block on a citation outside the target repository");

                    // A bare name that matches several of the sibling's files resolves to the one whose lines hold the
                    // citation, as it does in the target repository, never "file missing".
                    harness.Git.TrackedPathCandidatesByRepository[siblingDirectory + "|Handlers.cs"] =
                        new List<string> { "output/src/Handlers.cs", "tools/Handlers.cs" };
                    List<string> longHandlers = Enumerable.Range(1, 1000).Select(line => "    int Step" + line + "() { return " + line + "; }").ToList();
                    harness.Git.FileLinesOnRevision["output/src/Handlers.cs"] = longHandlers;
                    harness.Git.FileLinesOnRevision["tools/Handlers.cs"] = longHandlers.Take(58).ToList();
                    objective.Description = "Services/Foo.cs:10 mirrors the source handler at Handlers.cs:912.";
                    ObjectiveDispatchPreview several = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    ObjectiveDispatchPreflightFact severalFact = several.Preflight.Facts.Single(item => item.QuestionNumber == 1);
                    AssertEqual(PreflightFactStatusEnum.Pass, severalFact.Status,
                        "a bare name resolves to the one sibling file whose lines hold the citation. Detail: " + severalFact.Detail);
                    AssertContains("as `../ReferenceTree/output/src/Handlers.cs`", severalFact.Detail, "the fact names the file that holds the line");

                    objective.Description = "Services/Foo.cs:10 mirrors the source handler at Handlers.cs:5000.";
                    ObjectiveDispatchPreview noneHold = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    ObjectiveDispatchPreflightFact noneFact = noneHold.Preflight.Facts.Single(item => item.QuestionNumber == 1);
                    AssertEqual(PreflightFactStatusEnum.Fail, noneFact.Status, "no sibling candidate holds the cited line");
                    AssertContains("ambiguous file name in declared sibling `../ReferenceTree`", noneFact.Detail, "the miss is ambiguous, not a missing file");
                    AssertContains("`../ReferenceTree/tools/Handlers.cs`", noneFact.Detail, "the miss lists the candidates");

                    // A target-repository citation that does not resolve keeps the model gate.
                    objective.Description = "Change Services/Gone.cs:4 to sum the lots.";
                    ObjectiveDispatchPreview target = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertFalse(target.Preflight.Facts.Single(item => item.QuestionNumber == 1).FailsOnlyOutsideTarget,
                        "a missing target file is a target failure");
                    AssertTrue(target.Issues.Any(issue => issue.Code == PreflightTextAdapter.ModelFlagIssueCode && issue.Severity == ReadinessSeverityEnum.Error),
                        "a q1 model reading still blocks a target-repository contradiction");
                }
            }).ConfigureAwait(false);

            await RunTest("Preflight citations resolve declared sibling paths at the sibling tip", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    PreviewHarness harness = await PreviewHarness.CreateAsync(testDb, includeUnitTestCommand: true).ConfigureAwait(false);
                    harness.Vessel.TenantId = harness.Auth.TenantId;
                    harness.Vessel.UserId = harness.Auth.UserId;
                    await testDb.Driver.Vessels.UpdateAsync(harness.Vessel).ConfigureAwait(false);
                    string siblingDirectory = Path.Combine(harness.RepositoryDirectory, "reference-source");
                    Directory.CreateDirectory(siblingDirectory);
                    Vessel sibling = await testDb.Driver.Vessels.CreateAsync(new Vessel("ReferenceSource", "https://example.test/reference-source.git")
                    {
                        LocalPath = siblingDirectory,
                        WorkingDirectory = siblingDirectory,
                        TenantId = harness.Auth.TenantId,
                        UserId = harness.Auth.UserId
                    }).ConfigureAwait(false);
                    harness.Vessel.SiblingRepos = JsonSerializer.Serialize(new List<SiblingRepo>
                    {
                        new SiblingRepo { VesselRef = sibling.Id, RelativePath = "../ReferenceSource" }
                    });
                    await testDb.Driver.Vessels.UpdateAsync(harness.Vessel).ConfigureAwait(false);

                    Objective objective = harness.CreateReadyObjective("facts-sibling-citation-preview");
                    objective.Description = "See ../ReferenceSource/Parser/Generated.cs:7-16 and Services/Foo.cs:10.";
                    objective.Preparation.RequiredSiblingInputs.Add(new ObjectivePreparationSiblingInput
                    {
                        VesselRef = sibling.Id,
                        RelativePath = "../ReferenceSource"
                    });
                    const string siblingTip = "0123456789abcdef0123456789abcdef01234567";
                    harness.Git.RevisionCommitShas[siblingDirectory + "|HEAD"] = siblingTip;
                    harness.Git.PathsOnRevision.Add("Parser/Generated.cs");
                    harness.Git.PathsOnRevision.Add("Services/Foo.cs");

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);

                    AssertEqual(PreflightFactStatusEnum.Pass, result.Preflight.Facts.Single(fact => fact.QuestionNumber == 1).Status,
                        "a sibling citation resolves under its declared sibling vessel while the target citation still resolves at the target tip");
                    AssertTrue(harness.Git.PathExistsOnRevisionCalls.Contains(
                        siblingDirectory + "|" + siblingTip + "|Parser/Generated.cs"),
                        "the sibling path check uses the registered sibling root and its resolved commit, not the target repository or a symbolic ref");
                    AssertTrue(harness.Git.PathExistsOnRevisionCalls.Contains(
                        harness.RepositoryDirectory + "|HEAD|Services/Foo.cs"),
                        "ordinary citations continue to use the target repository revision");

                    objective.Preparation.RequiredSiblingInputs.Clear();
                    ObjectiveDispatchPreview declaredOnly = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertEqual(PreflightFactStatusEnum.Pass, declaredOnly.Preflight.Facts.Single(fact => fact.QuestionNumber == 1).Status,
                        "a parent-relative citation resolves through the vessel's declared sibling, which every dock provisions");

                    objective.Preparation.RequiredSiblingInputs.Add(new ObjectivePreparationSiblingInput
                    {
                        VesselRef = sibling.Id,
                        RelativePath = "../ReferenceSource"
                    });
                    harness.Vessel.SiblingRepos = JsonSerializer.Serialize(new List<SiblingRepo>
                    {
                        new SiblingRepo { VesselRef = sibling.Id, RelativePath = "../DifferentSibling" }
                    });
                    await testDb.Driver.Vessels.UpdateAsync(harness.Vessel).ConfigureAwait(false);
                    ObjectiveDispatchPreview mismatched = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertEqual(PreflightFactStatusEnum.Fail, mismatched.Preflight.Facts.Single(fact => fact.QuestionNumber == 1).Status,
                        "a required input does not authorize a different declared sibling path");

                    harness.Vessel.SiblingRepos = JsonSerializer.Serialize(new List<SiblingRepo>
                    {
                        new SiblingRepo { VesselRef = sibling.Id, RelativePath = "../ReferenceSource" }
                    });
                    await testDb.Driver.Vessels.UpdateAsync(harness.Vessel).ConfigureAwait(false);
                    objective.Description = "See ../ReferenceSource/../escape/Parser.cs:7 and Services/Foo.cs:10.";
                    ObjectiveDispatchPreview traversal = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertEqual(PreflightFactStatusEnum.Fail, traversal.Preflight.Facts.Single(fact => fact.QuestionNumber == 1).Status,
                        "a citation cannot traverse above the resolved sibling root");

                    objective.Description = "See ../ReferenceSource/Parser/Generated.cs:7-16 and Services/Foo.cs:10.";
                    harness.Git.RevisionCommitShas.Remove(siblingDirectory + "|HEAD");
                    harness.Git.RevisionCommitShaResult = null;
                    harness.Git.LandingCommitShaResult = null;
                    ObjectiveDispatchPreview missingTip = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertEqual(PreflightFactStatusEnum.Fail, missingTip.Preflight.Facts.Single(fact => fact.QuestionNumber == 1).Status,
                        "a sibling citation stays unresolved when the sibling tip cannot be verified");

                    AuthContext scopedCaller = AuthContext.Authenticated(
                        harness.Auth.TenantId, harness.Auth.UserId, false, false, "UnitTest");
                    harness.Git.RevisionCommitShas[siblingDirectory + "|HEAD"] = siblingTip;
                    ObjectiveDispatchPreview scopedVisible = await harness.Service.PreviewAsync(scopedCaller, objective).ConfigureAwait(false);
                    AssertEqual(PreflightFactStatusEnum.Pass, scopedVisible.Preflight.Facts.Single(fact => fact.QuestionNumber == 1).Status,
                        "an ordinary owner resolves its visible required sibling before ownership changes");
                    UserMaster anotherOwner = await testDb.Driver.Users.CreateAsync(
                        new UserMaster(harness.Auth.TenantId, "sibling-other-owner@example.test", "password")).ConfigureAwait(false);
                    sibling.UserId = anotherOwner.Id;
                    await testDb.Driver.Vessels.UpdateAsync(sibling).ConfigureAwait(false);
                    harness.Git.RevisionCommitShas[siblingDirectory + "|HEAD"] = siblingTip;
                    ObjectiveDispatchPreview foreignOwner = await harness.Service.PreviewAsync(scopedCaller, objective).ConfigureAwait(false);
                    AssertEqual(PreflightFactStatusEnum.Fail, foreignOwner.Preflight.Facts.Single(fact => fact.QuestionNumber == 1).Status,
                        "a sibling owned by another user is not used to resolve a citation");

                    sibling.UserId = harness.Auth.UserId;
                    TenantMetadata foreignTenant = await testDb.Driver.Tenants.CreateAsync(new TenantMetadata("Other sibling tenant")).ConfigureAwait(false);
                    UserMaster otherTenantOwner = await testDb.Driver.Users.CreateAsync(
                        new UserMaster(foreignTenant.Id, "sibling-other-tenant@example.test", "password")).ConfigureAwait(false);
                    sibling.TenantId = foreignTenant.Id;
                    sibling.UserId = otherTenantOwner.Id;
                    await testDb.Driver.Vessels.UpdateAsync(sibling).ConfigureAwait(false);
                    ObjectiveDispatchPreview foreignTenantPreview = await harness.Service.PreviewAsync(scopedCaller, objective).ConfigureAwait(false);
                    AssertEqual(PreflightFactStatusEnum.Fail, foreignTenantPreview.Preflight.Facts.Single(fact => fact.QuestionNumber == 1).Status,
                        "a sibling outside the caller's tenant is not used to resolve a citation");
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
                    harness.Git.RevisionCommitShas[harness.RepositoryDirectory + "|aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"] = new string('a', 40);
                    harness.Git.IsAncestorResult = true;

                    ObjectiveDispatchPreview passResult = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertEqual(PreflightFactStatusEnum.Pass, passResult.Preflight.Facts.Single(fact => fact.QuestionNumber == 11).Status,
                        "a sibling tip that contains the cited commit passes question 11");

                    harness.Git.IsAncestorResult = false;
                    ObjectiveDispatchPreview failResult = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertEqual(PreflightFactStatusEnum.Fail, failResult.Preflight.Facts.Single(fact => fact.QuestionNumber == 11).Status,
                        "a sibling tip behind the cited commit fails question 11");

                    // A commit the sibling does not hold is the vessel's own (a start commit, a preserved
                    // tip), never evidence of a stale sibling.
                    objective.Description = "Continue from bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb, the preserved tip.";
                    ObjectiveDispatchPreview ownCommitResult = await harness.Service.PreviewAsync(harness.Auth, objective).ConfigureAwait(false);
                    AssertEqual(PreflightFactStatusEnum.Unknown, ownCommitResult.Preflight.Facts.Single(fact => fact.QuestionNumber == 11).Status,
                        "a cited commit no sibling holds does not fail question 11");
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
                        tdSettings, client, recorder, new LoggingModule());

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
                        ConfirmedUtc = DateTime.UtcNow,
                        OperatorConfirmationId = Guid.NewGuid().ToString("N")
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
                        Reason = "legacy skip without server proof",
                        ConfirmedBy = "forged-legacy-operator",
                        ConfirmedUtc = DateTime.UtcNow
                    };
                    await testDb.Driver.Objectives.CreateAsync(objective).ConfigureAwait(false);

                    ObjectiveDispatchPreview result = await harness.Service.PreviewAsync(
                        harness.Auth, objective, null, pipeline.Id).ConfigureAwait(false);

                    AssertFalse(result.StageSkipConfirmed, "legacy confirmer text without server proof remains unconfirmed");
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
                        ConfirmedBy = "UnitTest",
                        ConfirmedUtc = DateTime.UtcNow,
                        OperatorConfirmationId = Guid.NewGuid().ToString("N")
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

        private static UsageAccountSettings UsageAccount(string id, string captainId, double remainingPercent)
        {
            return new UsageAccountSettings
            {
                Id = id,
                CaptainIds = new List<string> { captainId },
                ManualSnapshot = new ProviderUsageSnapshot
                {
                    ObservedUtc = DateTime.UtcNow,
                    Source = "test",
                    Windows = new List<ProviderUsageWindow>
                    {
                        new ProviderUsageWindow { Name = "weekly", RemainingPercent = remainingPercent, ResetsUtc = DateTime.UtcNow.AddDays(1) }
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

            if (parityCase.Usage != "off")
            {
                double highRemaining = parityCase.Usage == "normal" ? 80 : 0;
                double lowRemaining = parityCase.Usage == "exhausted-all" ? 0 : 80;
                settings.ModelTier.UsageRouting = new UsageRoutingSettings
                {
                    Enabled = true,
                    Accounts = new List<UsageAccountSettings>
                    {
                        UsageAccount("coverage-high", high.Id, highRemaining),
                        UsageAccount("coverage-low", low.Id, lowRemaining)
                    }
                };
            }

            if (parityCase.ModelList != "off")
            {
                if (parityCase.Usage == "off") ApplyRoutes(settings, new List<string> { high.Id, low.Id }, new List<string>());
                PersonaModelSettings models = new PersonaModelSettings();
                if (parityCase.ModelList == "listed-high") models.Default.Add(high.Model!);
                else if (parityCase.ModelList == "listed-low") models.Default.Add(low.Model!);
                else if (parityCase.ModelList == "all-unlisted") models.Default.Add("unused-persona-model");
                else if (parityCase.ModelList != "empty-lists") throw new InvalidOperationException("Unknown persona model list case: " + parityCase.ModelList);
                settings.ModelTier.UsageRouting.PersonaModels["Worker"] = models;
            }

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
            int previewIdleEligibleCount = preview.RequiredRoles.Single(role => role.Persona == "Worker").IdleEligibleCount;
            List<string> previewEligibleCaptainIds = preview.RequiredRoles.Single(role => role.Persona == "Worker").EligibleConfiguredCaptainIds;
            string? previewIssueCode = preview.Issues.FirstOrDefault(issue => issue.Code == "required_role_has_no_captain")?.Code;

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
                DispatchAssigns = !String.IsNullOrEmpty(stored?.CaptainId),
                PreviewEligibleCaptainIds = previewEligibleCaptainIds,
                PreviewIdleEligibleCount = previewIdleEligibleCount,
                PreviewIssueCode = previewIssueCode,
                AssignedCaptainId = stored?.CaptainId,
                HighCaptainId = high.Id,
                LowCaptainId = low.Id
            };
        }

        private sealed class CoverageParityVerdict
        {
            public bool PreviewCoverable { get; set; }
            public bool DispatchAssigns { get; set; }
            /// <summary>Configured captain IDs the actual dispatch preview admits for the role.</summary>
            public List<string> PreviewEligibleCaptainIds { get; set; } = new List<string>();
            /// <summary>Idle capacity the actual dispatch preview reports for the role.</summary>
            public int PreviewIdleEligibleCount { get; set; }
            /// <summary>Blocking role issue returned by the actual dispatch preview, when present.</summary>
            public string? PreviewIssueCode { get; set; }
            /// <summary>Captain ID stored by the real assignment service, or null when it assigns none.</summary>
            public string? AssignedCaptainId { get; set; }
            /// <summary>Premium captain ID in the parity fixture.</summary>
            public string HighCaptainId { get; set; } = String.Empty;
            /// <summary>Standard captain ID in the parity fixture.</summary>
            public string LowCaptainId { get; set; } = String.Empty;
        }

        private sealed class CitationCase
        {
            public CitationCase(string name, string description, PreflightFactStatusEnum expected, string? detailContains = null)
            {
                Name = name;
                Description = description;
                Expected = expected;
                DetailContains = detailContains;
            }

            public string Name { get; }
            public string Description { get; }
            public PreflightFactStatusEnum Expected { get; }
            public string? DetailContains { get; }
        }

        private sealed class CoverageParityCase
        {
            public string HighState { get; set; } = "idle";
            public string Low { get; set; } = "own";
            public string Request { get; set; } = "none";
            public string? Pin { get; set; } = null;
            public string Routing { get; set; } = "off";
            /// <summary>
            /// Persona model-list shape: off, listed-high, listed-low, all-unlisted, or empty-lists.
            /// The all-unlisted shape names a model that no configured captain runs.
            /// </summary>
            public string ModelList { get; set; } = "off";
            /// <summary>
            /// Account usage shape: off, normal, exhausted-high (the Premium captain's account has no allowance
            /// left), or exhausted-all (neither account has any).
            /// </summary>
            public string Usage { get; set; } = "off";

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

        private static async Task OwnHarnessInDefaultTenantAsync(TestDatabase testDb, PreviewHarness harness)
        {
            harness.Vessel.TenantId = Constants.DefaultTenantId;
            harness.Vessel.UserId = Constants.DefaultUserId;
            await testDb.Driver.Vessels.UpdateAsync(harness.Vessel).ConfigureAwait(false);
            harness.Captain.TenantId = Constants.DefaultTenantId;
            harness.Captain.UserId = Constants.DefaultUserId;
            await testDb.Driver.Captains.UpdateAsync(harness.Captain).ConfigureAwait(false);
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

            /// <summary>Temporary repository directories created by harnesses and not yet removed.</summary>
            public static List<string> CreatedDirectories { get; } = new List<string>();

            public static async Task<PreviewHarness> CreateAsync(
                TestDatabase testDb,
                bool includeUnitTestCommand,
                ArmadaSettings? settings = null,
                ICaptainExecutionEnvironmentProbe? executionEnvironment = null)
            {
                PreviewHarness result = new PreviewHarness();
                result.RepositoryDirectory = Path.Combine(Path.GetTempPath(), "armada-preview-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(result.RepositoryDirectory);
                CreatedDirectories.Add(result.RepositoryDirectory);
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
