namespace Armada.Test.Automated.Suites
{
    using System;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;

    /// <summary>
    /// REST-level coverage for workflow profiles and structured check runs.
    /// </summary>
    public class WorkflowProfileCheckRunTests : TestSuite
    {
        private readonly HttpClient _AuthClient;
        private readonly HttpClient _UnauthClient;

        /// <inheritdoc />
        public override string Name => "Workflow Profiles and Checks";

        /// <summary>
        /// Instantiate the suite.
        /// </summary>
        public WorkflowProfileCheckRunTests(HttpClient authClient, HttpClient unauthClient)
        {
            _AuthClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
            _UnauthClient = unauthClient ?? throw new ArgumentNullException(nameof(unauthClient));
        }

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-workflow-checks-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(workingDirectory, "artifacts"));
            await File.WriteAllTextAsync(Path.Combine(workingDirectory, "artifacts", "existing.txt"), "artifact").ConfigureAwait(false);

            string vesselId = String.Empty;
            string globalProfileId = String.Empty;
            string vesselProfileId = String.Empty;
            string missingInputProfileId = String.Empty;
            string firstRunId = String.Empty;
            string retryRunId = String.Empty;

            try
            {
                await RunTest("WorkflowProfiles_CreateResolveUpdateAndEnumerate", async () =>
                {
                    HttpResponseMessage vesselResponse = await _AuthClient.PostAsync("/api/v1/vessels",
                        JsonHelper.ToJsonContent(new
                        {
                            Name = "Workflow Check Vessel",
                            RepoUrl = "file:///tmp/workflow-check-vessel.git",
                            LocalPath = workingDirectory,
                            WorkingDirectory = workingDirectory,
                            DefaultBranch = "main",
                            RequirePassingChecksToLand = true
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, vesselResponse.StatusCode);

                    Vessel vessel = await JsonHelper.DeserializeAsync<Vessel>(vesselResponse).ConfigureAwait(false);
                    vesselId = vessel.Id;
                    AssertStartsWith("vsl_", vesselId);

                    HttpResponseMessage globalProfileResponse = await _AuthClient.PostAsync("/api/v1/workflow-profiles",
                        JsonHelper.ToJsonContent(new
                        {
                            Name = "Global Workflow Profile",
                            Scope = WorkflowProfileScopeEnum.Global,
                            BuildCommand = "dotnet --version"
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, globalProfileResponse.StatusCode);

                    WorkflowProfile globalProfile = await JsonHelper.DeserializeAsync<WorkflowProfile>(globalProfileResponse).ConfigureAwait(false);
                    globalProfileId = globalProfile.Id;
                    AssertStartsWith("wfp_", globalProfileId);

                    HttpResponseMessage vesselProfileResponse = await _AuthClient.PostAsync("/api/v1/workflow-profiles",
                        JsonHelper.ToJsonContent(new
                        {
                            Name = "Vessel Workflow Profile",
                            Scope = WorkflowProfileScopeEnum.Vessel,
                            VesselId = vesselId,
                            IsDefault = true,
                            BuildCommand = "dotnet --version",
                            UnitTestCommand = "dotnet --version",
                            ExpectedArtifacts = new[] { "artifacts/existing.txt" },
                            Environments = new[]
                            {
                                new
                                {
                                    EnvironmentName = "staging",
                                    DeployCommand = "echo deploy-staging"
                                }
                            }
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, vesselProfileResponse.StatusCode);

                    WorkflowProfile vesselProfile = await JsonHelper.DeserializeAsync<WorkflowProfile>(vesselProfileResponse).ConfigureAwait(false);
                    vesselProfileId = vesselProfile.Id;

                    HttpResponseMessage resolveResponse = await _AuthClient.GetAsync("/api/v1/workflow-profiles/resolve/vessels/" + vesselId).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, resolveResponse.StatusCode);

                    WorkflowProfile resolved = await JsonHelper.DeserializeAsync<WorkflowProfile>(resolveResponse).ConfigureAwait(false);
                    AssertEqual(vesselProfileId, resolved.Id);

                    HttpResponseMessage previewResponse = await _AuthClient.GetAsync("/api/v1/workflow-profiles/preview/vessels/" + vesselId).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, previewResponse.StatusCode);

                    WorkflowProfileResolutionPreviewResult preview = await JsonHelper.DeserializeAsync<WorkflowProfileResolutionPreviewResult>(previewResponse).ConfigureAwait(false);
                    AssertNotNull(preview.ResolvedProfile);
                    AssertEqual(vesselProfileId, preview.ResolvedProfile!.Id);
                    AssertEqual(WorkflowProfileResolutionModeEnum.Vessel, preview.ResolutionMode);
                    AssertTrue(preview.AvailableCheckTypes.Exists(type => type == CheckRunTypeEnum.Build.ToString()), "Expected build check type in preview.");
                    AssertTrue(preview.CommandPreviews.Exists(command => command.CheckType == CheckRunTypeEnum.Build && command.Command == "dotnet --version"), "Expected build command preview.");
                    AssertTrue(preview.CommandPreviews.Exists(command => command.CheckType == CheckRunTypeEnum.Deploy && command.EnvironmentName == "staging" && command.Command == "echo deploy-staging"), "Expected staging deploy preview.");

                    HttpResponseMessage listResponse = await _AuthClient.GetAsync("/api/v1/workflow-profiles?vesselId=" + Uri.EscapeDataString(vesselId) + "&pageSize=100").ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, listResponse.StatusCode);
                    EnumerationResult<WorkflowProfile> profiles = await JsonHelper.DeserializeAsync<EnumerationResult<WorkflowProfile>>(listResponse).ConfigureAwait(false);
                    AssertTrue(profiles.Objects.Count >= 1);

                    HttpResponseMessage updateResponse = await _AuthClient.PutAsync("/api/v1/workflow-profiles/" + vesselProfileId,
                        JsonHelper.ToJsonContent(new
                        {
                            Id = vesselProfileId,
                            Name = "Vessel Workflow Profile Updated",
                            Scope = WorkflowProfileScopeEnum.Vessel,
                            VesselId = vesselId,
                            IsDefault = true,
                            BuildCommand = "dotnet --version",
                            UnitTestCommand = "dotnet --version",
                            ExpectedArtifacts = new[] { "artifacts/existing.txt" },
                            Environments = new[]
                            {
                                new
                                {
                                    EnvironmentName = "staging",
                                    DeployCommand = "echo deploy-staging"
                                }
                            }
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, updateResponse.StatusCode);

                    WorkflowProfile updated = await JsonHelper.DeserializeAsync<WorkflowProfile>(updateResponse).ConfigureAwait(false);
                    AssertEqual("Vessel Workflow Profile Updated", updated.Name);
                }).ConfigureAwait(false);

                await RunTest("WorkflowProfiles_ValidateRejectsEmptyProfile", async () =>
                {
                    HttpResponseMessage response = await _AuthClient.PostAsync("/api/v1/workflow-profiles/validate",
                        JsonHelper.ToJsonContent(new
                        {
                            Name = "Invalid Profile",
                            Scope = WorkflowProfileScopeEnum.Global
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, response.StatusCode);

                    WorkflowProfileValidationResult validation = await JsonHelper.DeserializeAsync<WorkflowProfileValidationResult>(response).ConfigureAwait(false);
                    AssertFalse(validation.IsValid);
                    AssertTrue(validation.Errors.Count >= 1);
                }).ConfigureAwait(false);

                await RunTest("VesselReadiness_And_CheckRun_Block_When_Required_Input_Is_Missing", async () =>
                {
                    string missingVariable = "ARMADA_AUTOMATED_INPUT_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
                    string stagingVariable = "ARMADA_AUTOMATED_STAGING_" + Guid.NewGuid().ToString("N").ToUpperInvariant();

                    HttpResponseMessage profileResponse = await _AuthClient.PostAsync("/api/v1/workflow-profiles",
                        JsonHelper.ToJsonContent(new
                        {
                            Name = "Missing Input Workflow Profile",
                            Scope = WorkflowProfileScopeEnum.Vessel,
                            VesselId = vesselId,
                            BuildCommand = "dotnet --version",
                            Environments = new[]
                            {
                                new
                                {
                                    EnvironmentName = "staging",
                                    DeployCommand = "echo deploy-staging"
                                }
                            },
                            RequiredInputs = new[]
                            {
                                new
                                {
                                    Provider = WorkflowInputReferenceProviderEnum.EnvironmentVariable,
                                    Key = missingVariable,
                                    EnvironmentName = (string?)null,
                                    Description = (string?)null
                                },
                                new
                                {
                                    Provider = WorkflowInputReferenceProviderEnum.EnvironmentVariable,
                                    Key = stagingVariable,
                                    EnvironmentName = (string?)"staging",
                                    Description = (string?)"Staging deploy token"
                                },
                                new
                                {
                                    Provider = WorkflowInputReferenceProviderEnum.OnePassword,
                                    Key = "op://armada/staging/deploy-token",
                                    EnvironmentName = (string?)"staging",
                                    Description = (string?)"Provider-backed deploy token"
                                }
                            }
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, profileResponse.StatusCode);

                    WorkflowProfile missingInputProfile = await JsonHelper.DeserializeAsync<WorkflowProfile>(profileResponse).ConfigureAwait(false);
                    missingInputProfileId = missingInputProfile.Id;
                    AssertTrue(missingInputProfile.RequiredInputs.Count == 3, "Expected required inputs to round-trip.");
                    AssertEqual(WorkflowInputReferenceProviderEnum.OnePassword, missingInputProfile.RequiredInputs[2].Provider);
                    AssertEqual("staging", missingInputProfile.RequiredInputs[2].EnvironmentName);
                    AssertEqual("Provider-backed deploy token", missingInputProfile.RequiredInputs[2].Description);

                    HttpResponseMessage readinessResponse = await _AuthClient.GetAsync(
                        "/api/v1/vessels/" + vesselId
                        + "/readiness?workflowProfileId=" + Uri.EscapeDataString(missingInputProfileId)
                        + "&checkType=Build").ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, readinessResponse.StatusCode);

                    VesselReadinessResult readiness = await JsonHelper.DeserializeAsync<VesselReadinessResult>(readinessResponse).ConfigureAwait(false);
                    AssertFalse(readiness.IsReady);
                    AssertTrue(readiness.ErrorCount >= 1);
                    AssertTrue(readiness.Issues.Exists(issue => issue.Code == "required_input_missing"), "Expected a required_input_missing readiness issue.");
                    AssertContains(missingVariable, String.Join(" ", readiness.Issues.ConvertAll(issue => issue.Message)));
                    AssertFalse(readiness.Issues.Exists(issue => issue.Message.Contains(stagingVariable, StringComparison.OrdinalIgnoreCase)), "Build readiness should ignore staging-scoped inputs.");

                    HttpResponseMessage deployReadinessResponse = await _AuthClient.GetAsync(
                        "/api/v1/vessels/" + vesselId
                        + "/readiness?workflowProfileId=" + Uri.EscapeDataString(missingInputProfileId)
                        + "&checkType=Deploy&environmentName=staging").ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, deployReadinessResponse.StatusCode);

                    VesselReadinessResult deployReadiness = await JsonHelper.DeserializeAsync<VesselReadinessResult>(deployReadinessResponse).ConfigureAwait(false);
                    AssertTrue(deployReadiness.Issues.Exists(issue => issue.Message.Contains(stagingVariable, StringComparison.OrdinalIgnoreCase)), "Deploy readiness should include staging-scoped inputs.");

                    HttpResponseMessage blockedRunResponse = await _AuthClient.PostAsync("/api/v1/check-runs",
                        JsonHelper.ToJsonContent(new
                        {
                            VesselId = vesselId,
                            WorkflowProfileId = missingInputProfileId,
                            Type = CheckRunTypeEnum.Build
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.BadRequest, blockedRunResponse.StatusCode);

                    string blockedBody = await blockedRunResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                    AssertContains(missingVariable, blockedBody);
                    AssertFalse(blockedBody.Contains(stagingVariable, StringComparison.OrdinalIgnoreCase), "Build check failure should not mention staging-scoped inputs.");
                }).ConfigureAwait(false);

                await RunTest("WorkflowProfiles_ValidateRejectsUnknownEnvironmentScopedInputs", async () =>
                {
                    HttpResponseMessage response = await _AuthClient.PostAsync("/api/v1/workflow-profiles/validate",
                        JsonHelper.ToJsonContent(new
                        {
                            Name = "Scoped Validate Profile",
                            Scope = WorkflowProfileScopeEnum.Vessel,
                            VesselId = vesselId,
                            BuildCommand = "dotnet --version",
                            Environments = new[]
                            {
                                new
                                {
                                    EnvironmentName = "dev",
                                    DeployCommand = "echo deploy-dev"
                                }
                            },
                            RequiredInputs = new[]
                            {
                                new
                                {
                                    Provider = WorkflowInputReferenceProviderEnum.EnvironmentVariable,
                                    Key = "ARMADA_UNKNOWN_ENV",
                                    EnvironmentName = "prod"
                                }
                            }
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, response.StatusCode);

                    WorkflowProfileValidationResult validation = await JsonHelper.DeserializeAsync<WorkflowProfileValidationResult>(response).ConfigureAwait(false);
                    AssertFalse(validation.IsValid);
                    AssertContains("unknown environments", String.Join(" ", validation.Errors));
                    AssertContains("prod", String.Join(" ", validation.Errors));
                }).ConfigureAwait(false);

                await RunTest("VesselReadiness_And_LandingPreview_Surface_Setup_Metadata", async () =>
                {
                    HttpResponseMessage readinessResponse = await _AuthClient.GetAsync(
                        "/api/v1/vessels/" + vesselId
                        + "/readiness?workflowProfileId=" + Uri.EscapeDataString(vesselProfileId)).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, readinessResponse.StatusCode);

                    VesselReadinessResult readiness = await JsonHelper.DeserializeAsync<VesselReadinessResult>(readinessResponse).ConfigureAwait(false);
                    AssertTrue(readiness.DeploymentEnvironments.Exists(name => name == "staging"), "Expected staging deployment environment.");
                    AssertTrue(readiness.SetupChecklist.Count >= 5, "Expected setup checklist items.");
                    AssertTrue(readiness.SetupChecklist.Exists(item => item.Code == "workflow_profile" && item.IsSatisfied), "Expected workflow profile checklist item.");

                    HttpResponseMessage previewResponse = await _AuthClient.GetAsync(
                        "/api/v1/vessels/" + vesselId + "/landing-preview?sourceBranch=" + Uri.EscapeDataString("feature/workflow-check")).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, previewResponse.StatusCode);

                    LandingPreviewResult preview = await JsonHelper.DeserializeAsync<LandingPreviewResult>(previewResponse).ConfigureAwait(false);
                    AssertFalse(preview.IsReadyToLand);
                    AssertTrue(preview.RequirePassingChecksToLand, "Expected landing preview to honor vessel setting.");
                    AssertTrue(preview.Issues.Exists(issue => issue.Code == "passing_checks_required"), "Expected passing_checks_required issue.");
                }).ConfigureAwait(false);

                await RunTest("CheckRuns_RunReadRetryListAndDelete", async () =>
                {
                    HttpResponseMessage runResponse = await _AuthClient.PostAsync("/api/v1/check-runs",
                        JsonHelper.ToJsonContent(new
                        {
                            VesselId = vesselId,
                            WorkflowProfileId = vesselProfileId,
                            Type = CheckRunTypeEnum.Build,
                            Label = "Build Check",
                            BranchName = "feature/workflow-check",
                            CommitHash = "abc123"
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, runResponse.StatusCode);

                    CheckRun firstRun = await JsonHelper.DeserializeAsync<CheckRun>(runResponse).ConfigureAwait(false);
                    firstRunId = firstRun.Id;
                    AssertStartsWith("chk_", firstRunId);
                    AssertEqual(CheckRunStatusEnum.Passed, firstRun.Status);
                    AssertEqual("feature/workflow-check", firstRun.BranchName);
                    AssertEqual(0, firstRun.ExitCode ?? -1);
                    AssertTrue(firstRun.Artifacts.Count == 1, "Expected one collected artifact");

                    HttpResponseMessage detailResponse = await _AuthClient.GetAsync("/api/v1/check-runs/" + firstRunId).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, detailResponse.StatusCode);
                    CheckRun detail = await JsonHelper.DeserializeAsync<CheckRun>(detailResponse).ConfigureAwait(false);
                    AssertEqual(firstRunId, detail.Id);
                    AssertEqual(vesselProfileId, detail.WorkflowProfileId);

                    HttpResponseMessage listResponse = await _AuthClient.GetAsync(
                        "/api/v1/check-runs?vesselId=" + Uri.EscapeDataString(vesselId)
                        + "&workflowProfileId=" + Uri.EscapeDataString(vesselProfileId)
                        + "&type=" + Uri.EscapeDataString(CheckRunTypeEnum.Build.ToString())
                        + "&pageSize=100").ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, listResponse.StatusCode);

                    EnumerationResult<CheckRun> list = await JsonHelper.DeserializeAsync<EnumerationResult<CheckRun>>(listResponse).ConfigureAwait(false);
                    AssertTrue(list.Objects.Exists(run => run.Id == firstRunId), "First run should be listed");

                    HttpResponseMessage retryResponse = await _AuthClient.PostAsync("/api/v1/check-runs/" + firstRunId + "/retry", null).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, retryResponse.StatusCode);

                    CheckRun retryRun = await JsonHelper.DeserializeAsync<CheckRun>(retryResponse).ConfigureAwait(false);
                    retryRunId = retryRun.Id;
                    AssertNotEqual(firstRunId, retryRunId);
                    AssertEqual(CheckRunStatusEnum.Passed, retryRun.Status);

                    HttpResponseMessage previewResponse = await _AuthClient.GetAsync(
                        "/api/v1/vessels/" + vesselId + "/landing-preview").ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, previewResponse.StatusCode);
                    LandingPreviewResult preview = await JsonHelper.DeserializeAsync<LandingPreviewResult>(previewResponse).ConfigureAwait(false);
                    AssertTrue(preview.HasPassingChecks, "Expected landing preview to detect passing checks.");
                    AssertFalse(preview.Issues.Exists(issue => issue.Code == "passing_checks_required"), "Did not expect passing_checks_required after a successful check.");
                    AssertTrue(preview.IsReadyToLand, "Expected landing preview to be ready after a passing check.");

                    HttpResponseMessage deleteResponse = await _AuthClient.DeleteAsync("/api/v1/check-runs/" + firstRunId).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.NoContent, deleteResponse.StatusCode);

                    HttpResponseMessage deletedRead = await _AuthClient.GetAsync("/api/v1/check-runs/" + firstRunId).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.NotFound, deletedRead.StatusCode);
                }).ConfigureAwait(false);

                await RunTest("CheckRuns_RunParsesStructuredSummaries", async () =>
                {
                    await File.WriteAllTextAsync(
                        Path.Combine(workingDirectory, "summary.txt"),
                        "Passed!  - Failed: 0, Passed: 5, Skipped: 0, Total: 5, Duration: 2 s").ConfigureAwait(false);
                    await File.WriteAllTextAsync(
                        Path.Combine(workingDirectory, "coverage.cobertura.xml"),
                        """
                        <coverage line-rate="0.8" branch-rate="0.5" lines-covered="8" lines-valid="10" branches-covered="2" branches-valid="4"></coverage>
                        """).ConfigureAwait(false);

                    HttpResponseMessage profileResponse = await _AuthClient.PostAsync("/api/v1/workflow-profiles",
                        JsonHelper.ToJsonContent(new
                        {
                            Name = "Structured Parse Profile",
                            Scope = WorkflowProfileScopeEnum.Vessel,
                            VesselId = vesselId,
                            UnitTestCommand = BuildEmitFileCommand("summary.txt"),
                            ExpectedArtifacts = new[] { "coverage.cobertura.xml" }
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, profileResponse.StatusCode);

                    WorkflowProfile parseProfile = await JsonHelper.DeserializeAsync<WorkflowProfile>(profileResponse).ConfigureAwait(false);

                    try
                    {
                        HttpResponseMessage runResponse = await _AuthClient.PostAsync("/api/v1/check-runs",
                            JsonHelper.ToJsonContent(new
                            {
                                VesselId = vesselId,
                                WorkflowProfileId = parseProfile.Id,
                                Type = CheckRunTypeEnum.UnitTest,
                                Label = "Structured Unit Tests"
                            })).ConfigureAwait(false);
                        AssertEqual(HttpStatusCode.Created, runResponse.StatusCode);

                        CheckRun parsedRun = await JsonHelper.DeserializeAsync<CheckRun>(runResponse).ConfigureAwait(false);
                        AssertNotNull(parsedRun.TestSummary);
                        AssertEqual(5, parsedRun.TestSummary!.Passed ?? -1);
                        AssertEqual(5, parsedRun.TestSummary.Total ?? -1);
                        AssertNotNull(parsedRun.CoverageSummary);
                        AssertEqual(80d, parsedRun.CoverageSummary!.Lines?.Percentage ?? -1d);
                    }
                    finally
                    {
                        try { await _AuthClient.DeleteAsync("/api/v1/workflow-profiles/" + parseProfile.Id).ConfigureAwait(false); } catch { }
                    }
                }).ConfigureAwait(false);

                await RunTest("CheckRuns_RunWithoutAuthReturns401", async () =>
                {
                    HttpResponseMessage response = await _UnauthClient.PostAsync("/api/v1/check-runs",
                        JsonHelper.ToJsonContent(new
                        {
                            VesselId = vesselId,
                            Type = CheckRunTypeEnum.Build
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Unauthorized, response.StatusCode);
                }).ConfigureAwait(false);
            }
            finally
            {
                if (!String.IsNullOrWhiteSpace(retryRunId))
                {
                    try { await _AuthClient.DeleteAsync("/api/v1/check-runs/" + retryRunId).ConfigureAwait(false); } catch { }
                }
                if (!String.IsNullOrWhiteSpace(firstRunId))
                {
                    try { await _AuthClient.DeleteAsync("/api/v1/check-runs/" + firstRunId).ConfigureAwait(false); } catch { }
                }
                if (!String.IsNullOrWhiteSpace(vesselProfileId))
                {
                    try { await _AuthClient.DeleteAsync("/api/v1/workflow-profiles/" + vesselProfileId).ConfigureAwait(false); } catch { }
                }
                if (!String.IsNullOrWhiteSpace(missingInputProfileId))
                {
                    try { await _AuthClient.DeleteAsync("/api/v1/workflow-profiles/" + missingInputProfileId).ConfigureAwait(false); } catch { }
                }
                if (!String.IsNullOrWhiteSpace(globalProfileId))
                {
                    try { await _AuthClient.DeleteAsync("/api/v1/workflow-profiles/" + globalProfileId).ConfigureAwait(false); } catch { }
                }
                if (!String.IsNullOrWhiteSpace(vesselId))
                {
                    try { await _AuthClient.DeleteAsync("/api/v1/vessels/" + vesselId).ConfigureAwait(false); } catch { }
                }

                try
                {
                    if (Directory.Exists(workingDirectory))
                        Directory.Delete(workingDirectory, true);
                }
                catch
                {
                }
            }
        }

        private static string BuildEmitFileCommand(string relativePath)
        {
            return OperatingSystem.IsWindows()
                ? "type .\\" + relativePath.Replace('/', '\\')
                : "cat \"" + relativePath + "\"";
        }
    }
}
