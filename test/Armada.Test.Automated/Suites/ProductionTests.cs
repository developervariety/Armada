namespace Armada.Test.Automated.Suites
{
    using System;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;

    /// <summary>
    /// REST tests for the verified production summary.
    /// </summary>
    public sealed class ProductionTests : TestSuite
    {
        private readonly HttpClient _AuthClient;
        private readonly HttpClient _UnauthClient;

        /// <inheritdoc />
        public override string Name => "Production Routes";

        /// <summary>
        /// Instantiate.
        /// </summary>
        public ProductionTests(HttpClient authClient, HttpClient unauthClient)
        {
            _AuthClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
            _UnauthClient = unauthClient ?? throw new ArgumentNullException(nameof(unauthClient));
        }

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("SummaryRequiresAuthentication", async () =>
            {
                HttpResponseMessage response = await _UnauthClient
                    .GetAsync("/api/v1/production/summary")
                    .ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            }).ConfigureAwait(false);

            await RunTest("SummaryReturnsRequestedWindowAndCoverage", async () =>
            {
                HttpResponseMessage response = await _AuthClient.GetAsync(
                    "/api/v1/production/summary?fromUtc=2026-01-01T00%3A00%3A00Z&toUtc=2026-01-08T00%3A00%3A00Z&sourceFamily=ecu&workType=Feature")
                    .ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                ProductionSummaryResult result = await JsonHelper
                    .DeserializeAsync<ProductionSummaryResult>(response)
                    .ConfigureAwait(false);
                AssertEqual(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), result.FromUtc);
                AssertEqual(new DateTime(2026, 1, 8, 0, 0, 0, DateTimeKind.Utc), result.ToUtc);
                AssertNotNull(result.Groups);
                AssertNotNull(result.Warnings);
                AssertNotNull(result.RegressionCoverage);
            }).ConfigureAwait(false);

            await RunTest("TokenUsageSummaryHonoursPercentEncodedTimestamps", async () =>
            {
                HttpResponseMessage response = await _AuthClient.GetAsync(
                    "/api/v1/token-usage/summary?fromUtc=" + Uri.EscapeDataString("2026-09-14T00:00:00.000Z")
                    + "&toUtc=" + Uri.EscapeDataString("2026-09-15T00:00:00.000Z")
                    + "&bucketMinutes=60")
                    .ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                TokenUsageSummaryResult result = await JsonHelper
                    .DeserializeAsync<TokenUsageSummaryResult>(response)
                    .ConfigureAwait(false);
                AssertNotNull(result.FromUtc, "Echoed fromUtc");
                AssertNotNull(result.ToUtc, "Echoed toUtc");
                AssertEqual(new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc), result.FromUtc!.Value.ToUniversalTime(), "an encoded fromUtc is parsed, not replaced by the default window");
                AssertEqual(new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc), result.ToUtc!.Value.ToUniversalTime(), "an encoded toUtc is parsed, not replaced by the default window");
            }).ConfigureAwait(false);

            await RunTest("RegressionLinksRoundTripThroughIncidentAndCheckRoutes", async () =>
            {
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-regression-links-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);
                string? vesselId = null;
                string? incidentId = null;
                string? checkRunId = null;
                try
                {
                    HttpResponseMessage vesselResponse = await _AuthClient.PostAsync("/api/v1/vessels", JsonHelper.ToJsonContent(new
                    {
                        Name = "Regression Link Vessel",
                        RepoUrl = "file:///tmp/regression-link-vessel.git",
                        LocalPath = workingDirectory,
                        WorkingDirectory = workingDirectory,
                        DefaultBranch = "main"
                    })).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.Created, vesselResponse).ConfigureAwait(false);
                    vesselId = (await JsonHelper.DeserializeAsync<Vessel>(vesselResponse).ConfigureAwait(false)).Id;

                    HttpResponseMessage importResponse = await _AuthClient.PostAsync("/api/v1/check-runs/import", JsonHelper.ToJsonContent(new
                    {
                        VesselId = vesselId,
                        Type = CheckRunTypeEnum.UnitTest,
                        Status = CheckRunStatusEnum.Failed,
                        Command = "dotnet test consumer",
                        RegressionPurpose = RegressionPurposeEnum.Consumer,
                        RegressionObjectiveId = "obj_rest_origin",
                        RegressionLandedCommit = "ABCDEF123456"
                    })).ConfigureAwait(false);
                    AssertTrue(importResponse.IsSuccessStatusCode, "Check import with regression links succeeds: " + importResponse.StatusCode);
                    CheckRun imported = await JsonHelper.DeserializeAsync<CheckRun>(importResponse).ConfigureAwait(false);
                    checkRunId = imported.Id;

                    HttpResponseMessage checkResponse = await _AuthClient.GetAsync("/api/v1/check-runs/" + imported.Id).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.OK, checkResponse).ConfigureAwait(false);
                    CheckRun storedCheck = await JsonHelper.DeserializeAsync<CheckRun>(checkResponse).ConfigureAwait(false);
                    AssertEqual(RegressionPurposeEnum.Consumer, storedCheck.RegressionPurpose);
                    AssertEqual("obj_rest_origin", storedCheck.RegressionObjectiveId);
                    AssertEqual("abcdef123456", storedCheck.RegressionLandedCommit);

                    HttpResponseMessage badImport = await _AuthClient.PostAsync("/api/v1/check-runs/import", JsonHelper.ToJsonContent(new
                    {
                        VesselId = vesselId,
                        Type = CheckRunTypeEnum.UnitTest,
                        Status = CheckRunStatusEnum.Failed,
                        RegressionObjectiveId = "obj_rest_origin"
                    })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.BadRequest, badImport.StatusCode, "A Check link without a purpose is rejected");

                    HttpResponseMessage createResponse = await _AuthClient.PostAsync("/api/v1/incidents", JsonHelper.ToJsonContent(new
                    {
                        Title = "Consumer regression",
                        VesselId = vesselId,
                        CheckRunId = imported.Id,
                        RegressionPurpose = RegressionPurposeEnum.Consumer,
                        RegressionCause = RegressionCauseEnum.LandedChange,
                        RegressionObjectiveId = "obj_rest_origin",
                        RegressionLandedCommit = "abcdef123456"
                    })).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.Created, createResponse).ConfigureAwait(false);
                    Incident created = await JsonHelper.DeserializeAsync<Incident>(createResponse).ConfigureAwait(false);
                    incidentId = created.Id;
                    AssertEqual(RegressionCauseEnum.LandedChange, created.RegressionCause);

                    HttpResponseMessage updateResponse = await _AuthClient.PutAsync("/api/v1/incidents/" + incidentId, JsonHelper.ToJsonContent(new
                    {
                        RegressionCause = RegressionCauseEnum.Environment
                    })).ConfigureAwait(false);
                    await AssertStatusCodeAsync(HttpStatusCode.OK, updateResponse).ConfigureAwait(false);
                    HttpResponseMessage readResponse = await _AuthClient.GetAsync("/api/v1/incidents/" + incidentId).ConfigureAwait(false);
                    Incident read = await JsonHelper.DeserializeAsync<Incident>(readResponse).ConfigureAwait(false);
                    AssertEqual(RegressionCauseEnum.Environment, read.RegressionCause, "The update persists the cause");
                    AssertEqual("obj_rest_origin", read.RegressionObjectiveId, "An omitted link is preserved");

                    HttpResponseMessage badIncident = await _AuthClient.PostAsync("/api/v1/incidents", JsonHelper.ToJsonContent(new
                    {
                        Title = "Link without purpose",
                        RegressionObjectiveId = "obj_rest_origin"
                    })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.BadRequest, badIncident.StatusCode, "An incident link without a purpose is rejected");
                }
                finally
                {
                    if (incidentId != null) await _AuthClient.DeleteAsync("/api/v1/incidents/" + incidentId).ConfigureAwait(false);
                    if (checkRunId != null) await _AuthClient.DeleteAsync("/api/v1/check-runs/" + checkRunId).ConfigureAwait(false);
                    if (vesselId != null) await _AuthClient.DeleteAsync("/api/v1/vessels/" + vesselId).ConfigureAwait(false);
                    if (Directory.Exists(workingDirectory))
                    {
                        try { Directory.Delete(workingDirectory, true); }
                        catch (IOException ex) { Console.WriteLine("Regression link test left " + workingDirectory + ": " + ex.Message); }
                    }
                }
            }).ConfigureAwait(false);
        }
    }
}
