namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for workflow-profile resolution/validation and structured check execution:
    /// validation rejections (no commands, cross-tenant vessel scope, inputs scoped to unknown
    /// environments), command previews, vessel/fleet/global resolution and preview, check-run
    /// execution with artifact collection, retry, structured test/coverage parsing, readiness
    /// evaluation (missing inputs, toolchains/environments/checklist, environment-scoped inputs),
    /// landing preview gating, and fail-fast on missing required inputs.
    /// </summary>
    public sealed class WorkflowProfileCheckRunServiceSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.WorkflowProfileCheckRunService";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Workflow Profile and Check Run Services suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("validate_async_rejects_workflow_profile_with_no_commands", "ValidateAsync rejects workflow profile with no commands", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService service = new WorkflowProfileService(testDb.Driver, logging);

                WorkflowProfileValidationResult result = await service.ValidateAsync(new WorkflowProfile
                {
                    TenantId = "ten_profile",
                    UserId = "usr_profile",
                    Name = "Empty Workflow",
                    Scope = WorkflowProfileScopeEnum.Global
                }).ConfigureAwait(false);

                AssertFalse(result.IsValid);
                AssertContains("At least one build, test, release, deploy, verification, migration, security, or performance command is required.", String.Join(" ", result.Errors));
            }));

            cases.Add(CaseAsync("validate_async_returns_resolved_command_previews", "ValidateAsync returns resolved command previews", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService service = new WorkflowProfileService(testDb.Driver, logging);

                WorkflowProfileValidationResult result = await service.ValidateAsync(new WorkflowProfile
                {
                    TenantId = "ten_profile",
                    UserId = "usr_profile",
                    Name = "Preview Workflow",
                    Scope = WorkflowProfileScopeEnum.Global,
                    BuildCommand = "dotnet build",
                    UnitTestCommand = "dotnet test",
                    Environments = new List<WorkflowEnvironmentProfile>
                    {
                        new WorkflowEnvironmentProfile
                        {
                            EnvironmentName = "dev",
                            DeployCommand = "./deploy-dev.sh",
                            HealthCheckCommand = "./health-dev.sh"
                        }
                    }
                }).ConfigureAwait(false);

                AssertTrue(result.IsValid);
                AssertTrue(result.CommandPreviews.Count == 4, "Expected build, unit test, deploy, and health previews.");
                AssertTrue(result.CommandPreviews.Exists(command => command.CheckType == CheckRunTypeEnum.Build && command.Command == "dotnet build"));
                AssertTrue(result.CommandPreviews.Exists(command => command.CheckType == CheckRunTypeEnum.UnitTest && command.Command == "dotnet test"));
                AssertTrue(result.CommandPreviews.Exists(command => command.CheckType == CheckRunTypeEnum.Deploy && command.EnvironmentName == "dev" && command.Command == "./deploy-dev.sh"));
                AssertTrue(result.CommandPreviews.Exists(command => command.CheckType == CheckRunTypeEnum.HealthCheck && command.EnvironmentName == "dev" && command.Command == "./health-dev.sh"));
            }));

            cases.Add(CaseAsync("profile_save_refuses_an_unusable_failed_test_line_pattern", "Profile save refuses an invalid failed-test line pattern and one without a name group, and keeps a valid one", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService service = new WorkflowProfileService(testDb.Driver, logging);
                await EnsureTenantAndUserAsync(testDb, "ten_rerun_fields", "usr_rerun_fields").ConfigureAwait(false);
                AuthContext admin = AuthContext.Authenticated("ten_rerun_fields", "usr_rerun_fields", true, true, "UnitTest");

                WorkflowProfile Requested(string pattern) => new WorkflowProfile
                {
                    TenantId = "ten_rerun_fields",
                    Name = "Example Runner",
                    Scope = WorkflowProfileScopeEnum.Global,
                    UnitTestCommand = "./run-tests.sh",
                    FailedTestLinePattern = pattern,
                    IsolatedTestRerunCommand = "./run-tests.sh --only-from-env"
                };

                RecordWriteResult<WorkflowProfile> invalid = await service.CreateAsync(admin, Requested("^FAIL (?<name>.+?: ")).ConfigureAwait(false);
                AssertEqual(RecordWriteOutcomeEnum.Invalid, invalid.Outcome, "an invalid regular expression is refused");
                AssertContains("FailedTestLinePattern is not a valid regular expression", invalid.Message ?? "");

                RecordWriteResult<WorkflowProfile> unnamed = await service.CreateAsync(admin, Requested("^FAIL (.+?): ")).ConfigureAwait(false);
                AssertEqual(RecordWriteOutcomeEnum.Invalid, unnamed.Outcome, "a pattern without a name group is refused");
                AssertContains("FailedTestLinePattern has no named group 'name'", unnamed.Message ?? "");

                RecordWriteResult<WorkflowProfile> created = await service.CreateAsync(admin, Requested("^FAIL (?<name>.+?): ")).ConfigureAwait(false);
                AssertEqual(RecordWriteOutcomeEnum.Succeeded, created.Outcome, "a valid pattern is kept");
                WorkflowProfile? stored = await service.ReadForCallerAsync(admin, created.Record!.Id).ConfigureAwait(false);
                AssertEqual("^FAIL (?<name>.+?): ", stored!.FailedTestLinePattern, "the pattern persists");
                AssertEqual("./run-tests.sh --only-from-env", stored.IsolatedTestRerunCommand, "the isolated re-run command persists");

                RecordWriteResult<WorkflowProfile> badUpdate = await service.ReplaceAsync(admin, stored.Id, Requested("[unclosed")).ConfigureAwait(false);
                AssertEqual(RecordWriteOutcomeEnum.Invalid, badUpdate.Outcome, "an update with an invalid pattern is refused");
                WorkflowProfile? unchanged = await service.ReadForCallerAsync(admin, stored.Id).ConfigureAwait(false);
                AssertEqual("^FAIL (?<name>.+?): ", unchanged!.FailedTestLinePattern, "a refused update keeps the stored pattern");
            }));

            cases.Add(CaseAsync("abbreviated_unit_test_command_is_stored_and_is_not_the_unit_test_command", "Abbreviated unit-test command is stored and is not the unit-test command", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService service = new WorkflowProfileService(testDb.Driver, logging);
                await EnsureTenantAndUserAsync(testDb, "ten_abbrev", "usr_abbrev").ConfigureAwait(false);
                AuthContext admin = AuthContext.Authenticated("ten_abbrev", "usr_abbrev", true, true, "UnitTest");

                WorkflowProfile requested = new WorkflowProfile
                {
                    TenantId = "ten_abbrev",
                    Name = "Fast Subset",
                    Scope = WorkflowProfileScopeEnum.Global,
                    UnitTestCommand = "dotnet run --project test/full",
                    AbbreviatedUnitTestCommand = "  dotnet run --project test/short  "
                };

                RecordWriteResult<WorkflowProfile> created = await service.CreateAsync(admin, requested).ConfigureAwait(false);
                AssertEqual(RecordWriteOutcomeEnum.Succeeded, created.Outcome, "a profile with both commands is stored");
                WorkflowProfile? stored = await service.ReadForCallerAsync(admin, created.Record!.Id).ConfigureAwait(false);
                AssertEqual("dotnet run --project test/full", stored!.UnitTestCommand, "the full command is unchanged");
                AssertEqual("dotnet run --project test/short", stored.AbbreviatedUnitTestCommand, "the abbreviated command is trimmed and stored");
                AssertEqual("dotnet run --project test/full", service.ResolveCommand(stored, CheckRunTypeEnum.UnitTest), "unit-test resolution keeps the full command");
                AssertTrue(WorkflowProfileService.BuildCommandPreviews(stored).Exists(command => command.CheckType == CheckRunTypeEnum.UnitTest && command.Command == "dotnet run --project test/full"), "the preview shows the full command");
                AssertFalse(WorkflowProfileService.BuildCommandPreviews(stored).Exists(command => command.Command == "dotnet run --project test/short"), "the preview does not run the abbreviated command");

                stored.AbbreviatedUnitTestCommand = "   ";
                RecordWriteResult<WorkflowProfile> cleared = await service.ReplaceAsync(admin, stored.Id, stored).ConfigureAwait(false);
                AssertEqual(RecordWriteOutcomeEnum.Succeeded, cleared.Outcome, "clearing the abbreviated command is stored");
                WorkflowProfile? after = await service.ReadForCallerAsync(admin, stored.Id).ConfigureAwait(false);
                AssertNull(after!.AbbreviatedUnitTestCommand, "a blank abbreviated command is stored as empty");
                AssertEqual("dotnet run --project test/full", after.UnitTestCommand, "clearing the abbreviated command keeps the full command");
            }));

            cases.Add(CaseAsync("profile_save_normalizes_gate_inputs_and_refuses_paths_outside_the_repository", "Profile save keeps normalized gate-input paths and refuses a rooted or parent-relative one", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService service = new WorkflowProfileService(testDb.Driver, logging);
                await EnsureTenantAndUserAsync(testDb, "ten_gate_inputs", "usr_gate_inputs").ConfigureAwait(false);
                AuthContext admin = AuthContext.Authenticated("ten_gate_inputs", "usr_gate_inputs", true, true, "UnitTest");

                WorkflowProfile Requested(List<string> gateInputs) => new WorkflowProfile
                {
                    TenantId = "ten_gate_inputs",
                    Name = "Example Gate Inputs",
                    Scope = WorkflowProfileScopeEnum.Global,
                    BuildCommand = "./build.sh",
                    GateInputPaths = gateInputs
                };

                RecordWriteResult<WorkflowProfile> parent = await service.CreateAsync(admin, Requested(new List<string> { "../outside.md" })).ConfigureAwait(false);
                AssertEqual(RecordWriteOutcomeEnum.Invalid, parent.Outcome, "a parent-relative gate input is refused");
                AssertContains("must not contain a '..' segment", parent.Message ?? "");

                RecordWriteResult<WorkflowProfile> rooted = await service.CreateAsync(admin, Requested(new List<string> { "/etc/data.md" })).ConfigureAwait(false);
                AssertEqual(RecordWriteOutcomeEnum.Invalid, rooted.Outcome, "a rooted gate input is refused");
                AssertContains("must be repository-relative", rooted.Message ?? "");

                RecordWriteResult<WorkflowProfile> created = await service.CreateAsync(admin,
                    Requested(new List<string> { " ./docs/data-dictionary.md ", "schema\\**\\*.sql", "docs/data-dictionary.md", "  " })).ConfigureAwait(false);
                AssertEqual(RecordWriteOutcomeEnum.Succeeded, created.Outcome, "repository-relative gate inputs are kept");
                WorkflowProfile? stored = await service.ReadForCallerAsync(admin, created.Record!.Id).ConfigureAwait(false);
                AssertEqual("docs/data-dictionary.md|schema/**/*.sql", String.Join("|", stored!.GateInputPaths),
                    "the stored list is trimmed, forward-slashed, deduplicated and has no blanks");
            }));

            cases.Add(CaseAsync("validate_async_rejects_cross_tenant_vessel_scope", "ValidateAsync rejects cross-tenant vessel scope", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService service = new WorkflowProfileService(testDb.Driver, logging);

                await EnsureTenantAndUserAsync(testDb, "ten_source", "usr_source").ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb, "ten_other", "usr_other").ConfigureAwait(false);

                Vessel vessel = CreateVessel("ten_source", "usr_source", Path.Combine(Path.GetTempPath(), "armada-profile-cross-" + Guid.NewGuid().ToString("N")));
                await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                WorkflowProfileValidationResult result = await service.ValidateAsync(new WorkflowProfile
                {
                    TenantId = "ten_other",
                    UserId = "usr_other",
                    Name = "Cross Tenant Vessel Workflow",
                    Scope = WorkflowProfileScopeEnum.Vessel,
                    VesselId = vessel.Id,
                    BuildCommand = "dotnet --version"
                }).ConfigureAwait(false);

                AssertFalse(result.IsValid);
                AssertContains("Vessel does not belong to the workflow profile tenant.", String.Join(" ", result.Errors));
            }));

            cases.Add(CaseAsync("validate_async_rejects_required_inputs_scoped_to_unknown_environments", "ValidateAsync rejects required inputs scoped to unknown environments", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService service = new WorkflowProfileService(testDb.Driver, logging);

                WorkflowProfileValidationResult result = await service.ValidateAsync(new WorkflowProfile
                {
                    TenantId = "ten_profile_scope",
                    UserId = "usr_profile_scope",
                    Name = "Scoped Input Workflow",
                    Scope = WorkflowProfileScopeEnum.Global,
                    BuildCommand = "dotnet --version",
                    Environments = new List<WorkflowEnvironmentProfile>
                    {
                        new WorkflowEnvironmentProfile
                        {
                            EnvironmentName = "staging",
                            DeployCommand = "echo deploy"
                        }
                    },
                    RequiredInputs = new List<WorkflowInputReference>
                    {
                        new WorkflowInputReference
                        {
                            Provider = WorkflowInputReferenceProviderEnum.EnvironmentVariable,
                            Key = "MISSING_SCOPE_INPUT",
                            EnvironmentName = "production"
                        }
                    }
                }).ConfigureAwait(false);

                AssertFalse(result.IsValid);
                AssertContains("unknown environments", String.Join(" ", result.Errors));
                AssertContains("production", String.Join(" ", result.Errors));
            }));

            cases.Add(CaseAsync("resolve_for_vessel_async_prefers_vessel_over_fleet_over_global", "ResolveForVesselAsync prefers vessel over fleet over global", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService service = new WorkflowProfileService(testDb.Driver, logging);

                await EnsureTenantAndUserAsync(testDb, "ten_profile", "usr_profile").ConfigureAwait(false);

                Fleet fleet = new Fleet
                {
                    TenantId = "ten_profile",
                    UserId = "usr_profile",
                    Name = "Resolution Fleet"
                };
                await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-profile-resolve-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);
                try
                {
                    Vessel vessel = CreateVessel("ten_profile", "usr_profile", workingDirectory);
                    vessel.FleetId = fleet.Id;
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    WorkflowProfile global = new WorkflowProfile
                    {
                        TenantId = "ten_profile",
                        UserId = "usr_profile",
                        Name = "Global Workflow",
                        Scope = WorkflowProfileScopeEnum.Global,
                        BuildCommand = "global-build"
                    };
                    WorkflowProfile fleetProfile = new WorkflowProfile
                    {
                        TenantId = "ten_profile",
                        UserId = "usr_profile",
                        Name = "Fleet Workflow",
                        Scope = WorkflowProfileScopeEnum.Fleet,
                        FleetId = fleet.Id,
                        BuildCommand = "fleet-build"
                    };
                    WorkflowProfile vesselProfile = new WorkflowProfile
                    {
                        TenantId = "ten_profile",
                        UserId = "usr_profile",
                        Name = "Vessel Workflow",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id,
                        BuildCommand = "vessel-build"
                    };

                    await testDb.Driver.WorkflowProfiles.CreateAsync(global).ConfigureAwait(false);
                    await testDb.Driver.WorkflowProfiles.CreateAsync(fleetProfile).ConfigureAwait(false);
                    await testDb.Driver.WorkflowProfiles.CreateAsync(vesselProfile).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated("ten_profile", "usr_profile", false, false, "UnitTest");
                    WorkflowProfile? resolved = await service.ResolveForVesselAsync(auth, vessel).ConfigureAwait(false);

                    AssertNotNull(resolved);
                    AssertEqual(vesselProfile.Id, resolved!.Id);
                }
                finally
                {
                    TryDeleteDirectory(workingDirectory);
                }
            }));

            cases.Add(CaseAsync("preview_for_vessel_async_returns_resolution_mode_and_command_previews", "PreviewForVesselAsync returns resolution mode and command previews", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService service = new WorkflowProfileService(testDb.Driver, logging);

                await EnsureTenantAndUserAsync(testDb, "ten_preview", "usr_preview").ConfigureAwait(false);

                Fleet fleet = new Fleet
                {
                    TenantId = "ten_preview",
                    UserId = "usr_preview",
                    Name = "Preview Fleet"
                };
                await testDb.Driver.Fleets.CreateAsync(fleet).ConfigureAwait(false);

                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-profile-preview-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);
                try
                {
                    Vessel vessel = CreateVessel("ten_preview", "usr_preview", workingDirectory);
                    vessel.FleetId = fleet.Id;
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    WorkflowProfile global = new WorkflowProfile
                    {
                        TenantId = "ten_preview",
                        UserId = "usr_preview",
                        Name = "Global Preview Workflow",
                        Scope = WorkflowProfileScopeEnum.Global,
                        BuildCommand = "global-build"
                    };
                    WorkflowProfile fleetProfile = new WorkflowProfile
                    {
                        TenantId = "ten_preview",
                        UserId = "usr_preview",
                        Name = "Fleet Preview Workflow",
                        Scope = WorkflowProfileScopeEnum.Fleet,
                        FleetId = fleet.Id,
                        BuildCommand = "fleet-build",
                        Environments = new List<WorkflowEnvironmentProfile>
                        {
                            new WorkflowEnvironmentProfile
                            {
                                EnvironmentName = "staging",
                                DeployCommand = "fleet-deploy"
                            }
                        }
                    };

                    await testDb.Driver.WorkflowProfiles.CreateAsync(global).ConfigureAwait(false);
                    await testDb.Driver.WorkflowProfiles.CreateAsync(fleetProfile).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated("ten_preview", "usr_preview", false, false, "UnitTest");

                    WorkflowProfileResolutionPreviewResult? preview = await service.PreviewForVesselAsync(auth, vessel).ConfigureAwait(false);
                    AssertNotNull(preview);
                    AssertEqual(fleetProfile.Id, preview!.ResolvedProfile?.Id);
                    AssertEqual(WorkflowProfileResolutionModeEnum.Fleet, preview.ResolutionMode);
                    AssertTrue(preview.CommandPreviews.Exists(command => command.CheckType == CheckRunTypeEnum.Build && command.Command == "fleet-build"));
                    AssertTrue(preview.CommandPreviews.Exists(command => command.CheckType == CheckRunTypeEnum.Deploy && command.EnvironmentName == "staging" && command.Command == "fleet-deploy"));

                    WorkflowProfileResolutionPreviewResult? explicitPreview = await service.PreviewForVesselAsync(auth, vessel, global.Id).ConfigureAwait(false);
                    AssertNotNull(explicitPreview);
                    AssertEqual(global.Id, explicitPreview!.ResolvedProfile?.Id);
                    AssertEqual(WorkflowProfileResolutionModeEnum.Explicit, explicitPreview.ResolutionMode);
                }
                finally
                {
                    TryDeleteDirectory(workingDirectory);
                }
            }));

            cases.Add(CaseAsync("readiness_blocks_specific_check_when_a_required_environment_variable_is_missing", "Readiness blocks specific check when a required environment variable is missing", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);

                await EnsureTenantAndUserAsync(testDb, "ten_ready", "usr_ready").ConfigureAwait(false);

                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-readiness-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    Vessel vessel = CreateVessel("ten_ready", "usr_ready", workingDirectory);
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    string missingVariable = "ARMADA_TEST_INPUT_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
                    WorkflowProfile profile = new WorkflowProfile
                    {
                        TenantId = "ten_ready",
                        UserId = "usr_ready",
                        Name = "Readiness Workflow",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id,
                        BuildCommand = "dotnet --version",
                        RequiredInputs = new List<WorkflowInputReference>
                        {
                            new WorkflowInputReference
                            {
                                Provider = WorkflowInputReferenceProviderEnum.EnvironmentVariable,
                                Key = missingVariable
                            }
                        }
                    };
                    await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated("ten_ready", "usr_ready", false, false, "UnitTest");
                    VesselReadinessResult result = await readiness.EvaluateAsync(
                        auth,
                        vessel,
                        requestedCheckType: CheckRunTypeEnum.Build).ConfigureAwait(false);

                    AssertFalse(result.IsReady);
                    AssertTrue(result.ErrorCount >= 1, "Expected at least one blocking readiness error");
                    AssertContains(missingVariable, String.Join(" ", result.Issues.Select(issue => issue.Message)));
                }
                finally
                {
                    TryDeleteDirectory(workingDirectory);
                }
            }));

            cases.Add(CaseAsync("readiness_exposes_toolchains_environments_and_setup_checklist", "Readiness exposes toolchains, environments, and setup checklist", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);

                await EnsureTenantAndUserAsync(testDb, "ten_setup", "usr_setup").ConfigureAwait(false);

                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-readiness-setup-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);
                Directory.CreateDirectory(Path.Combine(workingDirectory, ".git"));

                try
                {
                    await File.WriteAllTextAsync(Path.Combine(workingDirectory, "Service.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>").ConfigureAwait(false);

                    Vessel vessel = CreateVessel("ten_setup", "usr_setup", workingDirectory);
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    WorkflowProfile profile = new WorkflowProfile
                    {
                        TenantId = "ten_setup",
                        UserId = "usr_setup",
                        Name = "Setup Workflow",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id,
                        BuildCommand = "dotnet --version",
                        Environments = new List<WorkflowEnvironmentProfile>
                        {
                            new WorkflowEnvironmentProfile
                            {
                                EnvironmentName = "staging",
                                DeployCommand = "echo deploy"
                            }
                        }
                    };
                    await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated("ten_setup", "usr_setup", false, false, "UnitTest");
                    VesselReadinessResult result = await readiness.EvaluateAsync(auth, vessel).ConfigureAwait(false);

                    AssertTrue(result.HasWorkingDirectory, "Expected working directory to be available.");
                    AssertTrue(result.DetectedToolchains.Contains("dotnet"), "Expected dotnet toolchain to be detected.");
                    AssertTrue(result.DeploymentEnvironments.Contains("staging"), "Expected staging environment to be surfaced.");
                    AssertTrue(result.SetupChecklist.Count >= 5, "Expected readiness setup checklist items.");
                    AssertTrue(result.SetupChecklist.Exists(item => item.Code == "workflow_profile" && item.IsSatisfied), "Expected workflow profile checklist item to be satisfied.");
                }
                finally
                {
                    TryDeleteDirectory(workingDirectory);
                }
            }));

            cases.Add(CaseAsync("readiness_scopes_required_inputs_by_environment_and_recognizes_provider_backed_references", "Readiness scopes required inputs by environment and recognizes provider-backed references", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);

                await EnsureTenantAndUserAsync(testDb, "ten_scoped", "usr_scoped").ConfigureAwait(false);

                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-readiness-scoped-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);
                Directory.CreateDirectory(Path.Combine(workingDirectory, ".git"));

                string buildVariable = "ARMADA_TEST_BUILD_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
                string deployVariable = "ARMADA_TEST_DEPLOY_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
                string? originalBuildVariable = Environment.GetEnvironmentVariable(buildVariable);
                string? originalDeployVariable = Environment.GetEnvironmentVariable(deployVariable);
                string? originalAwsRegion = Environment.GetEnvironmentVariable("AWS_REGION");
                string? originalAwsProfile = Environment.GetEnvironmentVariable("AWS_PROFILE");

                try
                {
                    Environment.SetEnvironmentVariable(buildVariable, "ready");
                    Environment.SetEnvironmentVariable("AWS_REGION", "us-east-1");
                    Environment.SetEnvironmentVariable("AWS_PROFILE", "armada-test");

                    Vessel vessel = CreateVessel("ten_scoped", "usr_scoped", workingDirectory);
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    WorkflowProfile profile = new WorkflowProfile
                    {
                        TenantId = "ten_scoped",
                        UserId = "usr_scoped",
                        Name = "Scoped Inputs Workflow",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id,
                        BuildCommand = "dotnet --version",
                        Environments = new List<WorkflowEnvironmentProfile>
                        {
                            new WorkflowEnvironmentProfile
                            {
                                EnvironmentName = "prod",
                                DeployCommand = "echo deploy"
                            }
                        },
                        RequiredInputs = new List<WorkflowInputReference>
                        {
                            new WorkflowInputReference
                            {
                                Provider = WorkflowInputReferenceProviderEnum.EnvironmentVariable,
                                Key = buildVariable
                            },
                            new WorkflowInputReference
                            {
                                Provider = WorkflowInputReferenceProviderEnum.EnvironmentVariable,
                                Key = deployVariable,
                                EnvironmentName = "prod"
                            },
                            new WorkflowInputReference
                            {
                                Provider = WorkflowInputReferenceProviderEnum.AwsSecretsManager,
                                Key = "armada/prod/deploy-token",
                                EnvironmentName = "prod",
                                Description = "Deploy token"
                            }
                        }
                    };
                    await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated("ten_scoped", "usr_scoped", false, false, "UnitTest");

                    VesselReadinessResult buildResult = await readiness.EvaluateAsync(
                        auth,
                        vessel,
                        requestedCheckType: CheckRunTypeEnum.Build).ConfigureAwait(false);
                    AssertTrue(buildResult.IsReady, "Build readiness should ignore environment-scoped deploy inputs.");
                    AssertFalse(buildResult.Issues.Any(issue => issue.Message.Contains(deployVariable, StringComparison.OrdinalIgnoreCase)));
                    AssertTrue(buildResult.SetupChecklistTotalCount >= 8, "Expected expanded onboarding checklist.");
                    AssertTrue(buildResult.SetupChecklistSatisfiedCount > 0, "Expected some onboarding checklist items to be satisfied.");

                    VesselReadinessResult deployBlockedResult = await readiness.EvaluateAsync(
                        auth,
                        vessel,
                        requestedCheckType: CheckRunTypeEnum.Deploy,
                        requestedEnvironmentName: "prod").ConfigureAwait(false);
                    AssertFalse(deployBlockedResult.IsReady, "Deploy readiness should block when the environment-scoped variable is missing.");
                    AssertTrue(deployBlockedResult.Issues.Any(issue => issue.Message.Contains(deployVariable, StringComparison.OrdinalIgnoreCase)));
                    AssertFalse(deployBlockedResult.Issues.Any(issue =>
                        issue.Code == "required_input_missing"
                        && issue.Message.Contains("armada/prod/deploy-token", StringComparison.OrdinalIgnoreCase)),
                        "AWS provider-backed reference should be satisfied by host credentials.");

                    Environment.SetEnvironmentVariable(deployVariable, "ready");

                    VesselReadinessResult deployReadyResult = await readiness.EvaluateAsync(
                        auth,
                        vessel,
                        requestedCheckType: CheckRunTypeEnum.Deploy,
                        requestedEnvironmentName: "prod").ConfigureAwait(false);
                    AssertTrue(deployReadyResult.IsReady, "Deploy readiness should pass once the prod-scoped input exists.");
                    AssertFalse(deployReadyResult.Issues.Any(issue => issue.Code == "required_input_missing"));
                }
                finally
                {
                    Environment.SetEnvironmentVariable(buildVariable, originalBuildVariable);
                    Environment.SetEnvironmentVariable(deployVariable, originalDeployVariable);
                    Environment.SetEnvironmentVariable("AWS_REGION", originalAwsRegion);
                    Environment.SetEnvironmentVariable("AWS_PROFILE", originalAwsProfile);
                    TryDeleteDirectory(workingDirectory);
                }
            }));

            cases.Add(CaseAsync("landing_preview_blocks_landing_when_passing_checks_are_required_and_none_exist", "Landing preview blocks landing when passing checks are required and none exist", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                LandingPreviewService landingPreview = new LandingPreviewService(testDb.Driver, logging);

                await EnsureTenantAndUserAsync(testDb, "ten_land", "usr_land").ConfigureAwait(false);

                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-landing-preview-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    Vessel vessel = CreateVessel("ten_land", "usr_land", workingDirectory);
                    vessel.RequirePassingChecksToLand = true;
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated("ten_land", "usr_land", false, false, "UnitTest");
                    LandingPreviewResult preview = await landingPreview.PreviewForVesselAsync(auth, vessel, "feature/work").ConfigureAwait(false);

                    AssertFalse(preview.IsReadyToLand, "Expected landing preview to block landing.");
                    AssertFalse(preview.HasPassingChecks, "Expected preview to report no passing checks.");
                    AssertTrue(preview.Issues.Exists(issue => issue.Code == "passing_checks_required"), "Expected passing_checks_required issue.");
                }
                finally
                {
                    TryDeleteDirectory(workingDirectory);
                }
            }));

            cases.Add(CaseAsync("discard_pending_races_with_queued_and_started_checks", "A terminal-voyage discard cancels queued checks and leaves a started check intact", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
                CheckRunService checkRuns = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);

                await EnsureTenantAndUserAsync(testDb, "ten_check_race", "usr_check_race").ConfigureAwait(false);

                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-check-race-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    Vessel vessel = CreateVessel("ten_check_race", "usr_check_race", workingDirectory);
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    WorkflowProfile profile = new WorkflowProfile
                    {
                        TenantId = "ten_check_race",
                        UserId = "usr_check_race",
                        Name = "Check Race Workflow",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id,
                        LintCommand = "dotnet --version"
                    };
                    await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);
                    AuthContext auth = AuthContext.Authenticated("ten_check_race", "usr_check_race", false, false, "UnitTest");

                    async Task<CheckRun> CreatePendingAsync(string label)
                    {
                        return await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                        {
                            TenantId = "ten_check_race",
                            UserId = "usr_check_race",
                            WorkflowProfileId = profile.Id,
                            VesselId = vessel.Id,
                            Label = label,
                            Type = CheckRunTypeEnum.Lint,
                            Source = CheckRunSourceEnum.Armada,
                            Status = CheckRunStatusEnum.Pending,
                            Command = "dotnet --version",
                            WorkingDirectory = workingDirectory,
                            CreatedUtc = DateTime.UtcNow,
                            LastUpdateUtc = DateTime.UtcNow
                        }).ConfigureAwait(false);
                    }

                    CheckRun queued = await CreatePendingAsync("queued-cancel").ConfigureAwait(false);
                    IDisposable slotHolder = await HostWideCommandLock.AcquireAsync("check-race-holder-" + Guid.NewGuid().ToString("N"), "check race test holder").ConfigureAwait(false);
                    Task<CheckRun> queuedExecution = Task.Run(() => checkRuns.RunPendingAsync(auth, queued.Id));
                    try
                    {
                        await WaitForHostSlotWaiterAsync(queued.Id).ConfigureAwait(false);
                        AssertTrue(await VoyageCheckDiscard.DiscardAsync(testDb.Driver, queued, VoyageCheckDiscard.VoyageFailedReason).ConfigureAwait(false));
                    }
                    catch
                    {
                        slotHolder.Dispose();
                        await queuedExecution.ConfigureAwait(false);
                        throw;
                    }
                    slotHolder.Dispose();

                    CheckRun canceledResult = await queuedExecution.ConfigureAwait(false);
                    AssertEqual(CheckRunStatusEnum.Canceled, canceledResult.Status, "A canceled host-slot waiter must not be restarted.");
                    AssertFalse(canceledResult.StartedUtc.HasValue, "A canceled host-slot waiter must not acquire a start time.");

                    CheckRun lateCompletion = new CheckRun
                    {
                        TenantId = queued.TenantId,
                        UserId = queued.UserId,
                        WorkflowProfileId = queued.WorkflowProfileId,
                        VesselId = queued.VesselId,
                        Label = queued.Label,
                        Type = queued.Type,
                        Source = CheckRunSourceEnum.Armada,
                        Status = CheckRunStatusEnum.Passed,
                        Command = queued.Command,
                        WorkingDirectory = queued.WorkingDirectory,
                        StartedUtc = DateTime.UtcNow.AddSeconds(-1),
                        CompletedUtc = DateTime.UtcNow,
                        ExitCode = 0,
                        Summary = "late completed result"
                    };
                    CheckRun recordedLateCompletion = await checkRuns.RecordCompletedAsync(lateCompletion).ConfigureAwait(false);
                    CheckRun canceledAfterLateCompletion = await testDb.Driver.CheckRuns.ReadAsync(queued.Id).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The canceled row disappeared after a late completion.");
                    AssertEqual(CheckRunStatusEnum.Canceled, canceledAfterLateCompletion.Status, "A late completed record must not replace a canceled pending row.");
                    AssertTrue(!String.Equals(queued.Id, recordedLateCompletion.Id, StringComparison.Ordinal), "A late completion after cancellation must use its own record.");

                    CheckRun completionTarget = await CreatePendingAsync("completed-record-reuse").ConfigureAwait(false);
                    CheckRun completedInput = new CheckRun
                    {
                        TenantId = completionTarget.TenantId,
                        UserId = completionTarget.UserId,
                        WorkflowProfileId = completionTarget.WorkflowProfileId,
                        VesselId = completionTarget.VesselId,
                        Label = completionTarget.Label,
                        Type = completionTarget.Type,
                        Source = CheckRunSourceEnum.Armada,
                        Status = CheckRunStatusEnum.Passed,
                        Command = "measured-command",
                        WorkingDirectory = "/measured/source",
                        BranchName = "measured-branch",
                        CommitHash = "measured-commit",
                        StartedUtc = DateTime.UtcNow.AddSeconds(-1),
                        CompletedUtc = DateTime.UtcNow,
                        ExitCode = 0,
                        Summary = "completed source provenance"
                    };
                    CheckRun reusedCompletion = await checkRuns.RecordCompletedAsync(completedInput).ConfigureAwait(false);
                    AssertEqual(completionTarget.Id, reusedCompletion.Id, "A matching pending row keeps its identity.");
                    AssertEqual("measured-command", reusedCompletion.Command, "Completed-record reuse must preserve the completed command.");
                    AssertEqual("/measured/source", reusedCompletion.WorkingDirectory, "Completed-record reuse must preserve the completed source path.");
                    AssertEqual("measured-branch", reusedCompletion.BranchName, "Completed-record reuse must preserve the completed branch.");
                    AssertEqual("measured-commit", reusedCompletion.CommitHash, "Completed-record reuse must preserve the completed commit.");

                    CheckRun started = await CreatePendingAsync("start-wins").ConfigureAwait(false);
                    started.BranchName = "execution-branch";
                    started.CommitHash = "execution-commit";
                    await testDb.Driver.CheckRuns.UpdateAsync(started).ConfigureAwait(false);
                    TaskCompletionSource<bool> runningSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using ManualResetEventSlim continueExecution = new ManualResetEventSlim(false);
                    checkRuns.OnCheckRunChanged = changed =>
                    {
                        if (changed.Id == started.Id && changed.Status == CheckRunStatusEnum.Running)
                        {
                            runningSignal.TrySetResult(true);
                            continueExecution.Wait();
                        }
                    };

                    Task<CheckRun> startedExecution = Task.Run(() => checkRuns.RunPendingAsync(auth, started.Id));
                    Task first = await Task.WhenAny(runningSignal.Task, startedExecution, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
                    if (first != runningSignal.Task)
                    {
                        continueExecution.Set();
                        await startedExecution.ConfigureAwait(false);
                        throw new InvalidOperationException("The pending check did not reach its Running claim.");
                    }

                    bool discardedStarted = false;
                    Exception? discardError = null;
                    try
                    {
                        discardedStarted = await VoyageCheckDiscard.DiscardAsync(testDb.Driver, started, VoyageCheckDiscard.VoyageFailedReason).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        discardError = ex;
                    }
                    finally
                    {
                        continueExecution.Set();
                    }

                    CheckRun? passedResult = null;
                    Exception? executionError = null;
                    try
                    {
                        passedResult = await startedExecution.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        executionError = ex;
                    }
                    if (discardError != null && executionError != null)
                        throw new AggregateException("Discard and the owned execution task both failed.", discardError, executionError);
                    if (discardError != null)
                        throw new InvalidOperationException("Discard failed after the execution callback was released.", discardError);
                    if (executionError != null)
                        throw new InvalidOperationException("The owned execution task failed after the callback was released.", executionError);
                    if (passedResult == null) throw new InvalidOperationException("The owned execution task returned no result.");

                    AssertFalse(discardedStarted, "A check that already claimed Running must not be discarded.");
                    AssertEqual(CheckRunStatusEnum.Passed, passedResult.Status, "A winning execution claim must complete normally.");
                    AssertTrue(passedResult.StartedUtc.HasValue, "The started check must retain its start time.");
                    AssertEqual("execution-branch", passedResult.BranchName, "Execution completion must preserve the claimed branch.");
                    AssertEqual("execution-commit", passedResult.CommitHash, "Execution completion must preserve the claimed commit.");
                }
                finally
                {
                    TryDeleteDirectory(workingDirectory);
                }
            }));

            cases.Add(CaseAsync("queued_source_stamp_matches_actual_isolated_checkout", "A queued source stamp cannot retag an already prepared checkout", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
                CheckRunService checkRuns = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);

                await EnsureTenantAndUserAsync(testDb, "ten_source_freeze", "usr_source_freeze").ConfigureAwait(false);

                string repositoryPath = Path.Combine(Path.GetTempPath(), "armada-source-freeze-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(repositoryPath);
                IDisposable? slotHolder = null;
                Task<CheckRun>? queuedExecution = null;
                string baseCommit = String.Empty;
                string reviewedCommit = String.Empty;
                CheckRun? measuredDefault = null;

                try
                {
                    RunGitForTest(repositoryPath, "init");
                    RunGitForTest(repositoryPath, "checkout", "-b", "main");
                    RunGitForTest(repositoryPath, "config", "user.name", "Armada test");
                    RunGitForTest(repositoryPath, "config", "user.email", "armada-test@example.invalid");
                    File.WriteAllText(Path.Combine(repositoryPath, "source.txt"), "base\n");
                    RunGitForTest(repositoryPath, "add", "source.txt");
                    RunGitForTest(repositoryPath, "commit", "-m", "base");
                    baseCommit = RunGitForTest(repositoryPath, "rev-parse", "HEAD").Trim();

                    RunGitForTest(repositoryPath, "checkout", "-b", "reviewed");
                    File.WriteAllText(Path.Combine(repositoryPath, "source.txt"), "reviewed\n");
                    RunGitForTest(repositoryPath, "commit", "-am", "reviewed");
                    reviewedCommit = RunGitForTest(repositoryPath, "rev-parse", "HEAD").Trim();
                    RunGitForTest(repositoryPath, "checkout", "main");

                    Vessel vessel = CreateVessel("ten_source_freeze", "usr_source_freeze", repositoryPath);
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    WorkflowProfile profile = new WorkflowProfile
                    {
                        TenantId = "ten_source_freeze",
                        UserId = "usr_source_freeze",
                        Name = "Source Freeze Workflow",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id,
                        UnitTestCommand = "git rev-parse HEAD"
                    };
                    await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);
                    AuthContext auth = AuthContext.Authenticated("ten_source_freeze", "usr_source_freeze", false, false, "UnitTest");

                    Voyage voyage = new Voyage("Completed source-freeze voyage")
                    {
                        TenantId = "ten_source_freeze",
                        UserId = "usr_source_freeze",
                        Status = VoyageStatusEnum.Complete,
                        CompletedUtc = DateTime.UtcNow
                    };
                    await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    CheckRun preStamped = await CreateArmedUnitCheckAsync(testDb, profile, vessel, voyage, "source-before-claim").ConfigureAwait(false);
                    CheckRun? stampedBeforeClaim = await CheckRunService.StampPendingSourceAsync(
                        testDb.Driver,
                        preStamped.Id,
                        "reviewed",
                        reviewedCommit).ConfigureAwait(false);
                    AssertEqual(reviewedCommit, stampedBeforeClaim?.CommitHash, "A valid source stamp before the slot claim must be retained.");
                    CheckRun measuredReviewed = await checkRuns.RunPendingAsync(auth, preStamped.Id).ConfigureAwait(false);
                    AssertEqual(CheckRunStatusEnum.Passed, measuredReviewed.Status, "The stamped check must complete.");
                    AssertContains(reviewedCommit, measuredReviewed.Output, "The command must report the exact reviewed checkout HEAD.");

                    CheckRun lateStamp = await CreateArmedUnitCheckAsync(testDb, profile, vessel, voyage, "source-during-queue").ConfigureAwait(false);
                    slotHolder = await HostWideCommandLock.AcquireAsync(
                        "source-freeze-holder-" + Guid.NewGuid().ToString("N"), "source freeze regression holder").ConfigureAwait(false);
                    Task<CheckRun> ownedExecution = Task.Run(() => checkRuns.RunPendingAsync(auth, lateStamp.Id));
                    queuedExecution = ownedExecution;
                    await WaitForHostSlotWaiterAsync(lateStamp.Id).ConfigureAwait(false);

                    CheckRun? stampedAfterClaim = await CheckRunService.StampPendingSourceAsync(
                        testDb.Driver,
                        lateStamp.Id,
                        "reviewed",
                        reviewedCommit).ConfigureAwait(false);
                    AssertTrue(stampedAfterClaim != null, "The queued check must still exist after the stamp attempt.");
                    AssertTrue(!String.Equals(reviewedCommit, stampedAfterClaim!.CommitHash, StringComparison.OrdinalIgnoreCase),
                        "A source stamp must be refused after slot/source preparation begins.");

                    slotHolder.Dispose();
                    slotHolder = null;
                    measuredDefault = await ownedExecution.ConfigureAwait(false);
                    queuedExecution = null;

                    AssertEqual(CheckRunStatusEnum.Passed, measuredDefault.Status, "The queued check must complete after the host slot is released.");
                    AssertContains(baseCommit, measuredDefault.Output, "The checkout must report the default-branch commit it actually executed.");
                    AssertTrue(!String.Equals(reviewedCommit, measuredDefault.CommitHash, StringComparison.OrdinalIgnoreCase),
                        "The result must not claim the later reviewed commit when its checkout ran the default branch.");
                }
                finally
                {
                    slotHolder?.Dispose();
                    try
                    {
                        if (queuedExecution != null) await queuedExecution.ConfigureAwait(false);
                    }
                    finally
                    {
                        TryDeleteDirectory(repositoryPath);
                    }
                }
            }));

            cases.Add(CaseAsync("unit_test_check_rerun_of_its_failing_tests_alone_decides_the_result", "A UnitTest check whose full run fails one test re-runs it alone: it passes and records the re-run when the re-run passes, and stays failed when the re-run fails", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
                CheckRunService checkRuns = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);
                await EnsureTenantAndUserAsync(testDb, "ten_check_rerun", "usr_check_rerun").ConfigureAwait(false);
                AuthContext auth = AuthContext.Authenticated("ten_check_rerun", "usr_check_rerun", false, false, "UnitTest");

                const string failingTest = "Sample.Tests.TimingFixture.Completes_within_bound";
                const string fullRun = "printf '  Failed " + failingTest + " [5 ms]\\nFailed!  - Failed:     1, Passed:     4, Skipped:     0, Total:     5\\n'; exit 1";

                // The re-run reads the failing names from the environment and proves it received them.
                List<IsolatedRerunRow> rows = new List<IsolatedRerunRow>
                {
                    new IsolatedRerunRow("re-run passes", "printf 'rerun-of:%s\\n' \"$" + IsolatedRerunPlan.NamesEnvironmentVariable + "\"; exit 0", CheckRunStatusEnum.Passed),
                    new IsolatedRerunRow("re-run fails", "printf 'rerun-of:%s\\n' \"$" + IsolatedRerunPlan.NamesEnvironmentVariable + "\"; exit 1", CheckRunStatusEnum.Failed),
                    new IsolatedRerunRow("re-run work stays inside the recorded duration", "sleep 0.25; printf 'rerun-of:%s\\n' \"$" + IsolatedRerunPlan.NamesEnvironmentVariable + "\"; exit 0", CheckRunStatusEnum.Passed, minimumDurationMs: 200)
                };

                foreach (IsolatedRerunRow row in rows)
                {
                    string repositoryPath = Path.Combine(Path.GetTempPath(), "armada-check-rerun-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(repositoryPath);
                    try
                    {
                        RunGitForTest(repositoryPath, "init");
                        RunGitForTest(repositoryPath, "checkout", "-b", "main");
                        RunGitForTest(repositoryPath, "config", "user.name", "Armada test");
                        RunGitForTest(repositoryPath, "config", "user.email", "armada-test@example.invalid");
                        File.WriteAllText(Path.Combine(repositoryPath, "TimingFixture.cs"), "base\n");
                        RunGitForTest(repositoryPath, "add", "TimingFixture.cs");
                        RunGitForTest(repositoryPath, "commit", "-m", "base");
                        string baseCommit = RunGitForTest(repositoryPath, "rev-parse", "HEAD").Trim();
                        File.WriteAllText(Path.Combine(repositoryPath, "Other.cs"), "change\n");
                        RunGitForTest(repositoryPath, "add", "Other.cs");
                        RunGitForTest(repositoryPath, "commit", "-m", "change");
                        string candidateCommit = RunGitForTest(repositoryPath, "rev-parse", "HEAD").Trim();

                        Vessel vessel = CreateVessel("ten_check_rerun", "usr_check_rerun", repositoryPath);
                        vessel.Name = "Check Rerun Vessel " + row.Name;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                        WorkflowProfile profile = new WorkflowProfile
                        {
                            TenantId = "ten_check_rerun",
                            UserId = "usr_check_rerun",
                            Name = "Check Rerun Workflow " + row.Name,
                            Scope = WorkflowProfileScopeEnum.Vessel,
                            VesselId = vessel.Id,
                            UnitTestCommand = fullRun,
                            IsolatedTestRerunCommand = row.RerunCommand
                        };
                        await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);

                        CheckRun run = await checkRuns.RunAsync(auth, new CheckRunRequest
                        {
                            VesselId = vessel.Id,
                            WorkflowProfileId = profile.Id,
                            Type = CheckRunTypeEnum.UnitTest,
                            CommitHash = candidateCommit,
                            DiffBaseCommit = baseCommit,
                            Label = "Candidate"
                        }).ConfigureAwait(false);

                        AssertEqual(row.Expected, run.Status, row.Name + ": the isolated re-run decides the check");
                        AssertContains("rerun-of:" + failingTest, run.Output ?? "", row.Name + ": the re-run ran with the failing name");
                        AssertContains("Isolated re-run of 1 failing test(s) (" + failingTest + ")", run.Summary ?? "", row.Name + ": the summary names the re-run tests");
                        AssertContains("TimingFixture file not in the change", run.Summary ?? "", row.Name + ": the summary says the failing test's file is outside the change");
                        if (row.MinimumDurationMs > 0)
                        {
                            AssertTrue((run.DurationMs ?? 0) >= row.MinimumDurationMs,
                                "The final run duration must include its isolated retry, not only the first failed command.");
                            CheckRun? stored = await testDb.Driver.CheckRuns.ReadAsync(run.Id).ConfigureAwait(false);
                            AssertEqual(run.CompletedUtc, stored?.CompletedUtc, "The stored completion is after the retry.");
                            AssertEqual(run.DurationMs, stored?.DurationMs, "The stored duration includes the retry.");
                        }
                        if (row.Expected == CheckRunStatusEnum.Passed)
                        {
                            AssertContains("Candidate passed on isolated re-run. The full run failed (exit 1", run.Summary ?? "", "a pass on re-run is recorded as one, with the full-run failure");
                            AssertContains(") passed.", run.Summary ?? "", "the summary records the re-run outcome");
                        }
                        else
                        {
                            AssertContains("also failed (exit 1)", run.Summary ?? "", "a failing re-run is recorded");
                        }
                    }
                    finally
                    {
                        TryDeleteDirectory(repositoryPath);
                    }
                }
            }));

            cases.Add(CaseAsync("run_async_fails_fast_when_required_workflow_input_is_missing", "RunAsync fails fast when required workflow input is missing", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                LoggingModule logging = CreateLogging();
                WorkflowProfileService workflowProfiles = new WorkflowProfileService(testDb.Driver, logging);
                VesselReadinessService readiness = new VesselReadinessService(testDb.Driver, workflowProfiles, logging);
                CheckRunService checkRuns = new CheckRunService(testDb.Driver, workflowProfiles, readiness, logging);

                await EnsureTenantAndUserAsync(testDb, "ten_blocked", "usr_blocked").ConfigureAwait(false);

                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-check-blocked-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    Vessel vessel = CreateVessel("ten_blocked", "usr_blocked", workingDirectory);
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    string missingVariable = "ARMADA_TEST_BLOCK_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
                    WorkflowProfile profile = new WorkflowProfile
                    {
                        TenantId = "ten_blocked",
                        UserId = "usr_blocked",
                        Name = "Blocked Workflow",
                        Scope = WorkflowProfileScopeEnum.Vessel,
                        VesselId = vessel.Id,
                        BuildCommand = "dotnet --version",
                        RequiredInputs = new List<WorkflowInputReference>
                        {
                            new WorkflowInputReference
                            {
                                Provider = WorkflowInputReferenceProviderEnum.EnvironmentVariable,
                                Key = missingVariable
                            }
                        }
                    };
                    await testDb.Driver.WorkflowProfiles.CreateAsync(profile).ConfigureAwait(false);

                    AuthContext auth = AuthContext.Authenticated("ten_blocked", "usr_blocked", false, false, "UnitTest");
                    await AssertThrowsAsync<InvalidOperationException>(async () =>
                    {
                        await checkRuns.RunAsync(auth, new CheckRunRequest
                        {
                            VesselId = vessel.Id,
                            Type = CheckRunTypeEnum.Build
                        }).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                }
                finally
                {
                    TryDeleteDirectory(workingDirectory);
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Workflow Profile and Check Run Services",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private sealed class IsolatedRerunRow
        {
            public IsolatedRerunRow(string name, string rerunCommand, CheckRunStatusEnum expected, long minimumDurationMs = 0)
            {
                Name = name;
                RerunCommand = rerunCommand;
                Expected = expected;
                MinimumDurationMs = minimumDurationMs;
            }

            public string Name { get; }

            public string RerunCommand { get; }

            public CheckRunStatusEnum Expected { get; }

            public long MinimumDurationMs { get; }
        }

        private static async Task WaitForHostSlotWaiterAsync(string checkRunId)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (HostWideCommandLock.Snapshot().FindWaiter(checkRunId) != null) return;
                await Task.Delay(10).ConfigureAwait(false);
            }

            throw new InvalidOperationException("The check run did not enter the host-slot queue.");
        }

        private static async Task<CheckRun> CreateArmedUnitCheckAsync(
            TestDatabase testDb,
            WorkflowProfile profile,
            Vessel vessel,
            Voyage voyage,
            string label)
        {
            return await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
            {
                TenantId = vessel.TenantId,
                UserId = vessel.UserId,
                WorkflowProfileId = profile.Id,
                VesselId = vessel.Id,
                VoyageId = voyage.Id,
                Label = label,
                Type = CheckRunTypeEnum.UnitTest,
                Source = CheckRunSourceEnum.Armada,
                Status = CheckRunStatusEnum.Pending,
                Command = CheckRunGateRules.PlaceholderCommand,
                WorkingDirectory = vessel.WorkingDirectory,
                CreatedUtc = DateTime.UtcNow,
                LastUpdateUtc = DateTime.UtcNow
            }).ConfigureAwait(false);
        }

        private static string RunGitForTest(string workingDirectory, params string[] arguments)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The local Git test command did not start.");
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException("The local Git test command failed: " + error);
            return output;
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static async Task EnsureTenantAndUserAsync(TestDatabase testDb, string tenantId, string userId)
        {
            TenantMetadata? existingTenant = await testDb.Driver.Tenants.ReadAsync(tenantId).ConfigureAwait(false);
            if (existingTenant == null)
            {
                await testDb.Driver.Tenants.CreateAsync(new TenantMetadata
                {
                    Id = tenantId,
                    Name = tenantId
                }).ConfigureAwait(false);
            }

            UserMaster? existingUser = await testDb.Driver.Users.ReadByIdAsync(userId).ConfigureAwait(false);
            if (existingUser == null)
            {
                await testDb.Driver.Users.CreateAsync(new UserMaster
                {
                    Id = userId,
                    TenantId = tenantId,
                    Email = userId + "@armada.test",
                    PasswordSha256 = UserMaster.ComputePasswordHash("password"),
                    IsTenantAdmin = true
                }).ConfigureAwait(false);
            }
        }

        private static Vessel CreateVessel(string tenantId, string userId, string workingDirectory)
        {
            return new Vessel
            {
                TenantId = tenantId,
                UserId = userId,
                Name = "Workflow Vessel",
                RepoUrl = "file:///tmp/armada-tests.git",
                LocalPath = workingDirectory,
                WorkingDirectory = workingDirectory,
                DefaultBranch = "main"
            };
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch
            {
            }
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
