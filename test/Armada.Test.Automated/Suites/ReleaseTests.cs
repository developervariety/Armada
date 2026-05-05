namespace Armada.Test.Automated.Suites
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;

    /// <summary>
    /// REST-level coverage for release CRUD and derivation flows.
    /// </summary>
    public class ReleaseTests : TestSuite
    {
        private readonly HttpClient _AuthClient;
        private readonly HttpClient _UnauthClient;

        /// <inheritdoc />
        public override string Name => "Releases";

        /// <summary>
        /// Instantiate the suite.
        /// </summary>
        public ReleaseTests(HttpClient authClient, HttpClient unauthClient)
        {
            _AuthClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
            _UnauthClient = unauthClient ?? throw new ArgumentNullException(nameof(unauthClient));
        }

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
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
                await RunTest("Releases_CreateListReadUpdateRefreshAndDelete", async () =>
                {
                    HttpResponseMessage vesselResponse = await _AuthClient.PostAsync("/api/v1/vessels",
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

                    HttpResponseMessage workflowProfileResponse = await _AuthClient.PostAsync("/api/v1/workflow-profiles",
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

                    Voyage voyage = await CreateVoyageAsync(vesselId, "Release Voyage", 2).ConfigureAwait(false);
                    voyageId = voyage.Id;

                    HttpResponseMessage checkRunResponse = await _AuthClient.PostAsync("/api/v1/check-runs",
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

                    HttpResponseMessage createReleaseResponse = await _AuthClient.PostAsync("/api/v1/releases",
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

                    HttpResponseMessage getReleaseResponse = await _AuthClient.GetAsync("/api/v1/releases/" + releaseId).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, getReleaseResponse.StatusCode);
                    Release loadedRelease = await JsonHelper.DeserializeAsync<Release>(getReleaseResponse).ConfigureAwait(false);
                    AssertEqual(releaseId, loadedRelease.Id);

                    HttpResponseMessage listResponse = await _AuthClient.GetAsync(
                        "/api/v1/releases?status=Candidate&checkRunId=" + Uri.EscapeDataString(checkRunId) + "&pageSize=100").ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, listResponse.StatusCode);
                    EnumerationResult<Release> releases = await JsonHelper.DeserializeAsync<EnumerationResult<Release>>(listResponse).ConfigureAwait(false);
                    AssertTrue(releases.Objects.Exists(current => current.Id == releaseId), "Expected release to appear in filtered list.");

                    HttpResponseMessage updateReleaseResponse = await _AuthClient.PutAsync("/api/v1/releases/" + releaseId,
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

                    HttpResponseMessage refreshResponse = await _AuthClient.PostAsync("/api/v1/releases/" + releaseId + "/refresh", null).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, refreshResponse.StatusCode);
                    Release refreshedRelease = await JsonHelper.DeserializeAsync<Release>(refreshResponse).ConfigureAwait(false);
                    AssertEqual(releaseId, refreshedRelease.Id);
                    AssertTrue(refreshedRelease.Artifacts.Count == 1, "Expected refreshed release to keep derived artifact metadata.");

                    HttpResponseMessage deleteReleaseResponse = await _AuthClient.DeleteAsync("/api/v1/releases/" + releaseId).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.NoContent, deleteReleaseResponse.StatusCode);
                    releaseId = String.Empty;

                    HttpResponseMessage deletedReleaseResponse = await _AuthClient.GetAsync("/api/v1/releases/" + refreshedRelease.Id).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.NotFound, deletedReleaseResponse.StatusCode);
                }).ConfigureAwait(false);

                await RunTest("Releases_CreateWithoutAuthReturns401", async () =>
                {
                    HttpResponseMessage response = await _UnauthClient.PostAsync("/api/v1/releases",
                        JsonHelper.ToJsonContent(new
                        {
                            Title = "Unauthorized Release"
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Unauthorized, response.StatusCode);
                }).ConfigureAwait(false);
            }
            finally
            {
                if (!String.IsNullOrWhiteSpace(releaseId))
                {
                    try { await _AuthClient.DeleteAsync("/api/v1/releases/" + releaseId).ConfigureAwait(false); } catch { }
                }
                if (!String.IsNullOrWhiteSpace(checkRunId))
                {
                    try { await _AuthClient.DeleteAsync("/api/v1/check-runs/" + checkRunId).ConfigureAwait(false); } catch { }
                }
                if (!String.IsNullOrWhiteSpace(voyageId))
                {
                    try { await _AuthClient.DeleteAsync("/api/v1/voyages/" + voyageId).ConfigureAwait(false); } catch { }
                    try { await _AuthClient.DeleteAsync("/api/v1/voyages/" + voyageId + "/purge").ConfigureAwait(false); } catch { }
                }
                if (!String.IsNullOrWhiteSpace(workflowProfileId))
                {
                    try { await _AuthClient.DeleteAsync("/api/v1/workflow-profiles/" + workflowProfileId).ConfigureAwait(false); } catch { }
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

        private async Task<Voyage> CreateVoyageAsync(string vesselId, string title, int missionCount)
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

            HttpResponseMessage response = await _AuthClient.PostAsync("/api/v1/voyages",
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
    }
}
