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
                            DefaultBranch = "main"
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
                            ExpectedArtifacts = new[] { "artifacts/existing.txt" }
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, vesselProfileResponse.StatusCode);

                    WorkflowProfile vesselProfile = await JsonHelper.DeserializeAsync<WorkflowProfile>(vesselProfileResponse).ConfigureAwait(false);
                    vesselProfileId = vesselProfile.Id;

                    HttpResponseMessage resolveResponse = await _AuthClient.GetAsync("/api/v1/workflow-profiles/resolve/vessels/" + vesselId).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, resolveResponse.StatusCode);

                    WorkflowProfile resolved = await JsonHelper.DeserializeAsync<WorkflowProfile>(resolveResponse).ConfigureAwait(false);
                    AssertEqual(vesselProfileId, resolved.Id);

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
                            ExpectedArtifacts = new[] { "artifacts/existing.txt" }
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

                await RunTest("CheckRuns_RunReadRetryListAndDelete", async () =>
                {
                    HttpResponseMessage runResponse = await _AuthClient.PostAsync("/api/v1/check-runs",
                        JsonHelper.ToJsonContent(new
                        {
                            VesselId = vesselId,
                            WorkflowProfileId = vesselProfileId,
                            Type = CheckRunTypeEnum.Build,
                            Label = "Build Check",
                            BranchName = "main",
                            CommitHash = "abc123"
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, runResponse.StatusCode);

                    CheckRun firstRun = await JsonHelper.DeserializeAsync<CheckRun>(runResponse).ConfigureAwait(false);
                    firstRunId = firstRun.Id;
                    AssertStartsWith("chk_", firstRunId);
                    AssertEqual(CheckRunStatusEnum.Passed, firstRun.Status);
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

                    HttpResponseMessage deleteResponse = await _AuthClient.DeleteAsync("/api/v1/check-runs/" + firstRunId).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.NoContent, deleteResponse.StatusCode);

                    HttpResponseMessage deletedRead = await _AuthClient.GetAsync("/api/v1/check-runs/" + firstRunId).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.NotFound, deletedRead.StatusCode);
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
    }
}
