namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// End-to-end REST coverage for release CRUD and derivation flows. Ported from the retired
    /// automated <c>ReleaseTests</c> suite; every case drives the real REST API exposed by the
    /// shared in-process server obtained through <see cref="E2EServerFixture"/>.
    /// </summary>
    public sealed class ReleaseSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the end-to-end Releases suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("create_list_read_update_refresh_and_delete", "Releases_CreateListReadUpdateRefreshAndDelete", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-releases-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(workingDirectory, "artifacts"));
                await File.WriteAllTextAsync(Path.Combine(workingDirectory, "version.txt"), "2.3.4").ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(workingDirectory, "artifacts", "app.zip"), "artifact").ConfigureAwait(false);

                string vesselId = String.Empty;
                string workflowProfileId = String.Empty;
                string voyageId = String.Empty;
                string checkRunId = String.Empty;
                string releaseId = String.Empty;

                try
                {
                    HttpResponseMessage vesselResponse = await authClient.PostAsync("/api/v1/vessels",
                        JsonHelper.ToJsonContent(new
                        {
                            Name = "Release Vessel",
                            RepoUrl = "file:///tmp/release-vessel.git",
                            LocalPath = workingDirectory,
                            WorkingDirectory = workingDirectory,
                            DefaultBranch = "main"
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, vesselResponse.StatusCode);

                    Vessel vessel = await JsonHelper.DeserializeAsync<Vessel>(vesselResponse).ConfigureAwait(false);
                    vesselId = vessel.Id;

                    HttpResponseMessage workflowProfileResponse = await authClient.PostAsync("/api/v1/workflow-profiles",
                        JsonHelper.ToJsonContent(new
                        {
                            Name = "Release Workflow",
                            Scope = WorkflowProfileScopeEnum.Vessel,
                            VesselId = vesselId,
                            IsDefault = true,
                            ReleaseVersioningCommand = BuildEmitFileCommand("version.txt"),
                            ExpectedArtifacts = new[] { "artifacts/app.zip" }
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, workflowProfileResponse.StatusCode);

                    WorkflowProfile workflowProfile = await JsonHelper.DeserializeAsync<WorkflowProfile>(workflowProfileResponse).ConfigureAwait(false);
                    workflowProfileId = workflowProfile.Id;

                    Voyage voyage = await CreateVoyageAsync(authClient, vesselId, "Release Voyage", 2).ConfigureAwait(false);
                    voyageId = voyage.Id;

                    HttpResponseMessage checkRunResponse = await authClient.PostAsync("/api/v1/check-runs",
                        JsonHelper.ToJsonContent(new
                        {
                            VesselId = vesselId,
                            WorkflowProfileId = workflowProfileId,
                            VoyageId = voyageId,
                            Type = CheckRunTypeEnum.ReleaseVersioning,
                            Label = "Version Check"
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, checkRunResponse.StatusCode);

                    CheckRun checkRun = await JsonHelper.DeserializeAsync<CheckRun>(checkRunResponse).ConfigureAwait(false);
                    checkRunId = checkRun.Id;
                    AssertEqual(CheckRunStatusEnum.Passed, checkRun.Status);

                    HttpResponseMessage createReleaseResponse = await authClient.PostAsync("/api/v1/releases",
                        JsonHelper.ToJsonContent(new
                        {
                            WorkflowProfileId = workflowProfileId,
                            VoyageIds = new[] { voyageId },
                            CheckRunIds = new[] { checkRunId },
                            Status = ReleaseStatusEnum.Candidate
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, createReleaseResponse.StatusCode);

                    Release release = await JsonHelper.DeserializeAsync<Release>(createReleaseResponse).ConfigureAwait(false);
                    releaseId = release.Id;
                    AssertStartsWith("rel_", releaseId);
                    AssertEqual("2.3.4", release.Version);
                    AssertEqual("v2.3.4", release.TagName);
                    AssertEqual(ReleaseStatusEnum.Candidate, release.Status);
                    AssertTrue(release.MissionIds.Count == 2, "Expected release to aggregate voyage missions.");
                    AssertTrue(release.Artifacts.Count == 1, "Expected one derived artifact.");

                    HttpResponseMessage getReleaseResponse = await authClient.GetAsync("/api/v1/releases/" + releaseId).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, getReleaseResponse.StatusCode);
                    Release loadedRelease = await JsonHelper.DeserializeAsync<Release>(getReleaseResponse).ConfigureAwait(false);
                    AssertEqual(releaseId, loadedRelease.Id);

                    HttpResponseMessage listResponse = await authClient.GetAsync(
                        "/api/v1/releases?status=Candidate&checkRunId=" + Uri.EscapeDataString(checkRunId) + "&pageSize=100").ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, listResponse.StatusCode);
                    EnumerationResult<Release> releases = await JsonHelper.DeserializeAsync<EnumerationResult<Release>>(listResponse).ConfigureAwait(false);
                    AssertTrue(releases.Objects.Exists(current => current.Id == releaseId), "Expected release to appear in filtered list.");

                    HttpResponseMessage updateReleaseResponse = await authClient.PutAsync("/api/v1/releases/" + releaseId,
                        JsonHelper.ToJsonContent(new
                        {
                            VesselId = vesselId,
                            WorkflowProfileId = workflowProfileId,
                            Title = "Release 2.3.4",
                            Version = "2.3.4",
                            TagName = "v2.3.4",
                            Summary = "Ready for shipment",
                            Notes = "Edited release notes",
                            Status = ReleaseStatusEnum.Shipped,
                            VoyageIds = new[] { voyageId },
                            MissionIds = release.MissionIds,
                            CheckRunIds = new[] { checkRunId }
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, updateReleaseResponse.StatusCode);

                    Release updatedRelease = await JsonHelper.DeserializeAsync<Release>(updateReleaseResponse).ConfigureAwait(false);
                    AssertEqual(ReleaseStatusEnum.Shipped, updatedRelease.Status);
                    AssertTrue(updatedRelease.PublishedUtc.HasValue, "Expected published timestamp when shipped.");
                    AssertEqual("Edited release notes", updatedRelease.Notes);

                    HttpResponseMessage refreshResponse = await authClient.PostAsync("/api/v1/releases/" + releaseId + "/refresh", null).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, refreshResponse.StatusCode);
                    Release refreshedRelease = await JsonHelper.DeserializeAsync<Release>(refreshResponse).ConfigureAwait(false);
                    AssertEqual(releaseId, refreshedRelease.Id);
                    AssertTrue(refreshedRelease.Artifacts.Count == 1, "Expected refreshed release to keep derived artifact metadata.");

                    HttpResponseMessage deleteReleaseResponse = await authClient.DeleteAsync("/api/v1/releases/" + releaseId).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.NoContent, deleteReleaseResponse.StatusCode);
                    releaseId = String.Empty;

                    HttpResponseMessage deletedReleaseResponse = await authClient.GetAsync("/api/v1/releases/" + refreshedRelease.Id).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.NotFound, deletedReleaseResponse.StatusCode);
                }
                finally
                {
                    if (!String.IsNullOrWhiteSpace(releaseId))
                    {
                        try { await authClient.DeleteAsync("/api/v1/releases/" + releaseId).ConfigureAwait(false); } catch { }
                    }
                    if (!String.IsNullOrWhiteSpace(checkRunId))
                    {
                        try { await authClient.DeleteAsync("/api/v1/check-runs/" + checkRunId).ConfigureAwait(false); } catch { }
                    }
                    if (!String.IsNullOrWhiteSpace(voyageId))
                    {
                        try { await authClient.DeleteAsync("/api/v1/voyages/" + voyageId).ConfigureAwait(false); } catch { }
                        try { await authClient.DeleteAsync("/api/v1/voyages/" + voyageId + "/purge").ConfigureAwait(false); } catch { }
                    }
                    if (!String.IsNullOrWhiteSpace(workflowProfileId))
                    {
                        try { await authClient.DeleteAsync("/api/v1/workflow-profiles/" + workflowProfileId).ConfigureAwait(false); } catch { }
                    }
                    if (!String.IsNullOrWhiteSpace(vesselId))
                    {
                        try { await authClient.DeleteAsync("/api/v1/vessels/" + vesselId).ConfigureAwait(false); } catch { }
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
            }));

            cases.Add(CaseAsync("create_without_auth_returns_401", "Releases_CreateWithoutAuthReturns401", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient unauthClient = fx.UnauthClient;

                HttpResponseMessage response = await unauthClient.PostAsync("/api/v1/releases",
                    JsonHelper.ToJsonContent(new
                    {
                        Title = "Unauthorized Release"
                    })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Releases",
                cases: cases);
        }

        #endregion

        #region Private-Members

        private const string SuiteId = "E2E.Release";

        #endregion

        #region Private-Methods

        private static async Task<Voyage> CreateVoyageAsync(HttpClient authClient, string vesselId, string title, int missionCount)
        {
            List<object> missions = new List<object>();
            for (int i = 1; i <= missionCount; i++)
            {
                missions.Add(new
                {
                    Title = "Release Mission " + i,
                    Description = "Release mission " + i
                });
            }

            HttpResponseMessage response = await authClient.PostAsync("/api/v1/voyages",
                JsonHelper.ToJsonContent(new
                {
                    Title = title,
                    Description = "Release voyage description",
                    VesselId = vesselId,
                    Missions = missions
                })).ConfigureAwait(false);
            AssertEqual(HttpStatusCode.Created, response.StatusCode);
            return await JsonHelper.DeserializeAsync<Voyage>(response).ConfigureAwait(false);
        }

        private static string BuildEmitFileCommand(string relativePath)
        {
            return OperatingSystem.IsWindows()
                ? "type .\\" + relativePath.Replace('/', '\\')
                : "cat \"" + relativePath + "\"";
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
