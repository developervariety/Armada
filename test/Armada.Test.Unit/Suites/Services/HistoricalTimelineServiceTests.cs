namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Unit coverage for historical timeline aggregation and filtering.
    /// </summary>
    public class HistoricalTimelineServiceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Historical Timeline Service";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("EnumerateAsync aggregates current Armada entities into one timeline", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                string tenantId = "ten_history";
                string userId = "usr_history";
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-history-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                    Vessel vessel = CreateVessel(tenantId, userId, workingDirectory);
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Voyage voyage = new Voyage
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        Title = "History Voyage",
                        Description = "Voyage description",
                        Status = VoyageStatusEnum.Open
                    };
                    await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Mission mission = new Mission
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        VesselId = vessel.Id,
                        VoyageId = voyage.Id,
                        Title = "History Mission",
                        Description = "Mission description",
                        Status = MissionStatusEnum.WorkProduced
                    };
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    CheckRun checkRun = new CheckRun
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        VesselId = vessel.Id,
                        MissionId = mission.Id,
                        VoyageId = voyage.Id,
                        Type = CheckRunTypeEnum.UnitTest,
                        Status = CheckRunStatusEnum.Passed,
                        Command = "dotnet test",
                        Summary = "10 passed",
                        Output = "Passed! 10 tests run.",
                        DurationMs = 1234,
                        StartedUtc = DateTime.UtcNow.AddMinutes(-2),
                        CompletedUtc = DateTime.UtcNow.AddMinutes(-1)
                    };
                    await testDb.Driver.CheckRuns.CreateAsync(checkRun).ConfigureAwait(false);

                    Release release = new Release
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        VesselId = vessel.Id,
                        Title = "History Release",
                        Version = "1.0.0",
                        TagName = "v1.0.0",
                        Summary = "Release summary",
                        Status = ReleaseStatusEnum.Candidate,
                        VoyageIds = new List<string> { voyage.Id },
                        MissionIds = new List<string> { mission.Id },
                        CheckRunIds = new List<string> { checkRun.Id }
                    };
                    await testDb.Driver.Releases.CreateAsync(release).ConfigureAwait(false);

                    RequestHistoryEntry requestEntry = new RequestHistoryEntry
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        PrincipalDisplay = userId + "@armada.test",
                        AuthMethod = "Session",
                        Method = "GET",
                        Route = "/api/v1/status",
                        RouteTemplate = "/api/v1/status",
                        QueryString = "trace=history",
                        StatusCode = 200,
                        DurationMs = 12.5,
                        RequestSizeBytes = 10,
                        ResponseSizeBytes = 20,
                        RequestContentType = "application/json",
                        ResponseContentType = "application/json",
                        IsSuccess = true,
                        CreatedUtc = DateTime.UtcNow
                    };
                    RequestHistoryDetail requestDetail = new RequestHistoryDetail
                    {
                        RequestHistoryId = requestEntry.Id,
                        RequestHeadersJson = "{\"X-Test\":\"history\"}",
                        ResponseHeadersJson = "{\"Content-Type\":\"application/json\"}",
                        RequestBodyText = "{\"trace\":\"history\"}",
                        ResponseBodyText = "{\"ok\":true}"
                    };
                    await testDb.Driver.RequestHistory.CreateAsync(requestEntry, requestDetail).ConfigureAwait(false);

                    HistoricalTimelineService service = new HistoricalTimelineService(testDb.Driver);
                    AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, true, "UnitTest");

                    EnumerationResult<HistoricalTimelineEntry> result = await service.EnumerateAsync(auth, new HistoricalTimelineQuery
                    {
                        PageNumber = 1,
                        PageSize = 50
                    }).ConfigureAwait(false);

                    AssertTrue(result.TotalRecords >= 5, "Expected voyage, mission, check run, release, and request history entries.");
                    AssertTrue(result.Objects.Exists(entry => entry.SourceType == "Voyage" && entry.SourceId == voyage.Id));
                    AssertTrue(result.Objects.Exists(entry => entry.SourceType == "Mission" && entry.SourceId == mission.Id));
                    AssertTrue(result.Objects.Exists(entry => entry.SourceType == "CheckRun" && entry.SourceId == checkRun.Id));
                    AssertTrue(result.Objects.Exists(entry => entry.SourceType == "Release" && entry.SourceId == release.Id));
                    AssertTrue(result.Objects.Exists(entry => entry.SourceType == "Request" && entry.SourceId == requestEntry.Id));
                }
                finally
                {
                    TryDeleteDirectory(workingDirectory);
                }
            }).ConfigureAwait(false);

            await RunTest("EnumerateAsync filters by vessel, actor, source type, and text", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                string tenantId = "ten_history_filter";
                string userId = "usr_history_filter";
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-history-filter-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    await EnsureTenantAndUserAsync(testDb, tenantId, userId).ConfigureAwait(false);

                    Vessel vessel = CreateVessel(tenantId, userId, workingDirectory);
                    vessel.Name = "Filter Vessel";
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    CheckRun matchingRun = new CheckRun
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        VesselId = vessel.Id,
                        Type = CheckRunTypeEnum.Build,
                        Status = CheckRunStatusEnum.Passed,
                        Command = "dotnet build",
                        Summary = "Filter summary"
                    };
                    await testDb.Driver.CheckRuns.CreateAsync(matchingRun).ConfigureAwait(false);

                    CheckRun otherRun = new CheckRun
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        VesselId = null,
                        Type = CheckRunTypeEnum.Build,
                        Status = CheckRunStatusEnum.Failed,
                        Command = "dotnet build",
                        Summary = "Other summary"
                    };
                    await testDb.Driver.CheckRuns.CreateAsync(otherRun).ConfigureAwait(false);

                    HistoricalTimelineService service = new HistoricalTimelineService(testDb.Driver);
                    AuthContext auth = AuthContext.Authenticated(tenantId, userId, false, false, "UnitTest");

                    EnumerationResult<HistoricalTimelineEntry> result = await service.EnumerateAsync(auth, new HistoricalTimelineQuery
                    {
                        PageNumber = 1,
                        PageSize = 25,
                        VesselId = vessel.Id,
                        Actor = userId,
                        Text = "Filter summary",
                        SourceTypes = new List<string> { "CheckRun" }
                    }).ConfigureAwait(false);

                    AssertEqual(1, result.Objects.Count);
                    AssertEqual(matchingRun.Id, result.Objects[0].SourceId);
                    AssertEqual("CheckRun", result.Objects[0].SourceType);
                }
                finally
                {
                    TryDeleteDirectory(workingDirectory);
                }
            }).ConfigureAwait(false);
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
                Name = "History Vessel",
                RepoUrl = "file:///tmp/armada-history.git",
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
    }
}
