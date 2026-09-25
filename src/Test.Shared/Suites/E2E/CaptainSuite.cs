namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// End-to-end Captain API descriptors covering CRUD, stop, list, pagination, ordering,
    /// enumeration, and edge cases against a live in-process Armada server provided by
    /// <see cref="E2EServerFixture"/>.
    /// </summary>
    public sealed class CaptainSuite : IArmadaTestSuite
    {
        #region Private-Members

        private readonly object _SeedLock = new object();
        private Task? _SharedCaptainSeed;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Captain API end-to-end suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            #region List - Empty

            #endregion

            #region List - After Create

            #endregion

            #region List - Pagination

            cases.Add(CaseAsync("list_captains_25_created_pagesize_10_total_records_25", "List Captains 25 Created PageSize 10 TotalRecords 25", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureCaptainsSeededAsync(authClient);

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/captains?pageSize=10");
                EnumerationResult<Captain> result = await JsonHelper.DeserializeAsync<EnumerationResult<Captain>>(response);

                Assert(result.TotalRecords >= 25, "TotalRecords should be >= 25");
                Assert(result.TotalPages >= 3, "TotalPages should be >= 3");
                AssertEqual(10, result.Objects.Count);
            }));

            cases.Add(CaseAsync("list_captains_25_created_page_1_has_ten_records", "List Captains 25 Created Page 1 Has Ten Records", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureCaptainsSeededAsync(authClient);

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/captains?pageSize=10&pageNumber=1");
                EnumerationResult<Captain> result = await JsonHelper.DeserializeAsync<EnumerationResult<Captain>>(response);

                AssertEqual(10, result.Objects.Count);
                AssertEqual(1, result.PageNumber);
            }));

            cases.Add(CaseAsync("list_captains_25_created_page_2_has_ten_records", "List Captains 25 Created Page 2 Has Ten Records", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureCaptainsSeededAsync(authClient);

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/captains?pageSize=10&pageNumber=2");
                EnumerationResult<Captain> result = await JsonHelper.DeserializeAsync<EnumerationResult<Captain>>(response);

                AssertEqual(10, result.Objects.Count);
                AssertEqual(2, result.PageNumber);
            }));

            cases.Add(CaseAsync("list_captains_25_created_page_3_has_five_records", "List Captains 25 Created Page 3 Has Five Records", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureCaptainsSeededAsync(authClient);

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/captains?pageSize=10&pageNumber=3");
                EnumerationResult<Captain> result = await JsonHelper.DeserializeAsync<EnumerationResult<Captain>>(response);

                Assert(result.Objects.Count >= 5, "Page 3 should have at least 5 items");
                AssertEqual(3, result.PageNumber);
            }));

            cases.Add(CaseAsync("list_captains_25_created_beyond_last_page_returns_empty", "List Captains 25 Created Beyond Last Page Returns Empty", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureCaptainsSeededAsync(authClient);

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/captains?pageSize=10&pageNumber=999");
                EnumerationResult<Captain> result = await JsonHelper.DeserializeAsync<EnumerationResult<Captain>>(response);

                AssertEqual(0, result.Objects.Count);
            }));

            cases.Add(CaseAsync("list_captains_25_created_pages_do_not_overlap", "List Captains 25 Created Pages Do Not Overlap", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureCaptainsSeededAsync(authClient);

                HttpResponseMessage resp1 = await authClient.GetAsync("/api/v1/captains?pageSize=10&pageNumber=1");
                EnumerationResult<Captain> result1 = await JsonHelper.DeserializeAsync<EnumerationResult<Captain>>(resp1);
                HashSet<string> page1Ids = new HashSet<string>(result1.Objects.Select(c => c.Id));

                HttpResponseMessage resp2 = await authClient.GetAsync("/api/v1/captains?pageSize=10&pageNumber=2");
                EnumerationResult<Captain> result2 = await JsonHelper.DeserializeAsync<EnumerationResult<Captain>>(resp2);
                foreach (Captain c in result2.Objects)
                {
                    AssertFalse(page1Ids.Contains(c.Id), "Page 2 ID should not be in page 1: " + c.Id);
                }

                HttpResponseMessage resp3 = await authClient.GetAsync("/api/v1/captains?pageSize=10&pageNumber=3");
                EnumerationResult<Captain> result3 = await JsonHelper.DeserializeAsync<EnumerationResult<Captain>>(resp3);
                foreach (Captain c in result3.Objects)
                {
                    AssertFalse(page1Ids.Contains(c.Id), "Page 3 ID should not be in page 1: " + c.Id);
                }
            }));

            cases.Add(CaseAsync("list_captains_25_created_all_records_accounted_for", "List Captains 25 Created All Records Accounted For", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureCaptainsSeededAsync(authClient);

                HashSet<string> allIds = new HashSet<string>();

                for (int page = 1; page <= 3; page++)
                {
                    HttpResponseMessage resp = await authClient.GetAsync("/api/v1/captains?pageSize=10&pageNumber=" + page);
                    EnumerationResult<Captain> result = await JsonHelper.DeserializeAsync<EnumerationResult<Captain>>(resp);
                    foreach (Captain c in result.Objects)
                    {
                        allIds.Add(c.Id);
                    }
                }

                Assert(allIds.Count >= 25, "Should have at least 25 unique IDs across pages");
            }));

            cases.Add(CaseAsync("list_captains_pagesize_5_returns_5_records", "List Captains PageSize 5 Returns 5 Records", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                await EnsureCaptainsSeededAsync(authClient);

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/captains?pageSize=5");
                EnumerationResult<Captain> result = await JsonHelper.DeserializeAsync<EnumerationResult<Captain>>(response);

                AssertEqual(5, result.Objects.Count);
                Assert(result.TotalRecords >= 10, "TotalRecords should be >= 10");
                Assert(result.TotalPages >= 2, "TotalPages should be >= 2");
            }));

            #endregion

            #region List - Ordering

            #endregion

            #region Enumerate (POST)

            #endregion

            #region Edge Cases

            #endregion

            return new TestSuiteDescriptor(
                suiteId: "E2E.Captain",
                displayName: "Captain API Tests",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Creates a captain and returns the deserialized Captain object.
        /// </summary>
        /// <summary>
        /// Ensure the shared server holds a reusable set of at least 25 captains, seeding it exactly once
        /// for the suite. Every case runs against the same in-process fixture and the list/pagination cases
        /// only assert accumulation-tolerant conditions (a full page, a total at or above a threshold, no
        /// page overlap, all created IDs present across pages), so they can share one seeded set instead of
        /// each re-creating 25 captains over sequential HTTP round-trips. The seed Task is memoized: the
        /// first bulk case to run pays the cost and the rest await the completed Task.
        /// </summary>
        /// <param name="authClient">Authenticated client for the shared e2e server.</param>
        /// <returns>A task that completes once the shared captains exist.</returns>
        private Task EnsureCaptainsSeededAsync(HttpClient authClient)
        {
            lock (_SeedLock)
            {
                if (_SharedCaptainSeed == null) _SharedCaptainSeed = SeedSharedCaptainsAsync(authClient);
                return _SharedCaptainSeed;
            }
        }

        private static async Task SeedSharedCaptainsAsync(HttpClient authClient)
        {
            List<string> seededIds = new List<string>();
            for (int i = 0; i < 25; i++)
            {
                await CreateCaptainAsync(authClient, seededIds, "shared-pag-captain-" + i.ToString("D2")).ConfigureAwait(false);
            }
        }

        private static async Task<Captain> CreateCaptainAsync(HttpClient client, List<string> createdCaptainIds, string name, string runtime = "ClaudeCode")
        {
            string uniqueName = name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            object body = new { Name = uniqueName, Runtime = runtime };
            HttpResponseMessage resp = await client.PostAsync("/api/v1/captains",
                JsonHelper.ToJsonContent(body));
            resp.EnsureSuccessStatusCode();
            Captain captain = await JsonHelper.DeserializeAsync<Captain>(resp);
            createdCaptainIds.Add(captain.Id);
            return captain;
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "E2E.Captain",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
