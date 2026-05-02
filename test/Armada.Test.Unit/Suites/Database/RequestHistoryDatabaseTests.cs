namespace Armada.Test.Unit.Suites.Database
{
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    public class RequestHistoryDatabaseTests : TestSuite
    {
        public override string Name => "RequestHistory Database";

        protected override async Task RunTestsAsync()
        {
            await RunTest("CreateAsync and ReadAsync persist entry detail", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;

                    RequestHistoryEntry entry = BuildEntry("GET", "/api/v1/fleets", 200, "usr_one");
                    RequestHistoryDetail detail = BuildDetail(entry.Id, "{\"route\":\"/api/v1/fleets\"}");

                    await db.RequestHistory.CreateAsync(entry, detail).ConfigureAwait(false);
                    RequestHistoryRecord? result = await db.RequestHistory.ReadAsync(entry.Id).ConfigureAwait(false);

                    AssertNotNull(result);
                    AssertEqual(entry.Id, result!.Entry.Id);
                    AssertEqual("/api/v1/fleets", result.Entry.Route);
                    AssertEqual(detail.RequestBodyText, result.Detail!.RequestBodyText);
                }
            });

            await RunTest("EnumerateAsync filters by route and success state", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;

                    RequestHistoryEntry successEntry = BuildEntry("GET", "/api/v1/fleets", 200, "usr_one");
                    RequestHistoryEntry failureEntry = BuildEntry("POST", "/api/v1/missions", 500, "usr_two");
                    await db.RequestHistory.CreateAsync(successEntry, BuildDetail(successEntry.Id, "{\"ok\":true}")).ConfigureAwait(false);
                    await db.RequestHistory.CreateAsync(failureEntry, BuildDetail(failureEntry.Id, "{\"ok\":false}")).ConfigureAwait(false);

                    EnumerationResult<RequestHistoryEntry> routeFiltered = await db.RequestHistory.EnumerateAsync(new RequestHistoryQuery
                    {
                        Route = "/api/v1/missions",
                        PageNumber = 1,
                        PageSize = 25
                    }).ConfigureAwait(false);
                    AssertEqual(1, routeFiltered.Objects.Count);
                    AssertEqual(failureEntry.Id, routeFiltered.Objects[0].Id);

                    EnumerationResult<RequestHistoryEntry> successFiltered = await db.RequestHistory.EnumerateAsync(new RequestHistoryQuery
                    {
                        IsSuccess = true,
                        PageNumber = 1,
                        PageSize = 25
                    }).ConfigureAwait(false);
                    AssertEqual(1, successFiltered.Objects.Count);
                    AssertEqual(successEntry.Id, successFiltered.Objects[0].Id);
                }
            });

            await RunTest("DeleteByFilterAsync removes matching request-history rows", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;

                    RequestHistoryEntry oldEntry = BuildEntry("GET", "/api/v1/status", 200, "usr_old");
                    oldEntry.CreatedUtc = DateTime.UtcNow.AddDays(-10);
                    RequestHistoryEntry newEntry = BuildEntry("GET", "/api/v1/status", 200, "usr_new");
                    newEntry.CreatedUtc = DateTime.UtcNow;

                    await db.RequestHistory.CreateAsync(oldEntry, BuildDetail(oldEntry.Id, "{\"age\":\"old\"}")).ConfigureAwait(false);
                    await db.RequestHistory.CreateAsync(newEntry, BuildDetail(newEntry.Id, "{\"age\":\"new\"}")).ConfigureAwait(false);

                    int deleted = await db.RequestHistory.DeleteByFilterAsync(new RequestHistoryQuery
                    {
                        ToUtc = DateTime.UtcNow.AddDays(-5)
                    }).ConfigureAwait(false);

                    AssertEqual(1, deleted);
                    AssertNull(await db.RequestHistory.ReadAsync(oldEntry.Id).ConfigureAwait(false));
                    AssertNotNull(await db.RequestHistory.ReadAsync(newEntry.Id).ConfigureAwait(false));
                }
            });
        }

        private static RequestHistoryEntry BuildEntry(string method, string route, int statusCode, string userId)
        {
            return new RequestHistoryEntry
            {
                TenantId = "ten_request_history",
                UserId = userId,
                PrincipalDisplay = userId + "@armada",
                AuthMethod = "Session",
                Method = method,
                Route = route,
                RouteTemplate = route,
                QueryString = null,
                StatusCode = statusCode,
                DurationMs = 12.34,
                RequestSizeBytes = 42,
                ResponseSizeBytes = 84,
                RequestContentType = "application/json",
                ResponseContentType = "application/json",
                IsSuccess = statusCode >= 200 && statusCode < 400,
                CreatedUtc = DateTime.UtcNow
            };
        }

        private static RequestHistoryDetail BuildDetail(string requestHistoryId, string requestBody)
        {
            return new RequestHistoryDetail
            {
                RequestHistoryId = requestHistoryId,
                RequestHeadersJson = "{\"X-Test\":\"true\"}",
                ResponseHeadersJson = "{\"Content-Type\":\"application/json\"}",
                RequestBodyText = requestBody,
                ResponseBodyText = "{\"status\":\"ok\"}"
            };
        }
    }
}
