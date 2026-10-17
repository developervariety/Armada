namespace Test.Shared.Suites.E2E
{
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Armada.Core.Storage;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>Storage API access boundaries and typed asynchronous refresh contract.</summary>
    public sealed class StorageApiSuite : IArmadaTestSuite
    {
        /// <summary>Build storage REST tests.</summary>
        public TestSuiteDescriptor Build()
        {
            return new TestSuiteDescriptor("E2E.Storage", "Storage API", new List<TestCaseDescriptor>
            {
                new TestCaseDescriptor("E2E.Storage", "authorization", "Anonymous, member and tenant admin cannot inspect global storage", async ct =>
                {
                    E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                    E2ETenantUser tenant = await E2ETenantUser.CreateAsync(fx.AuthClient, "storage-admin", true).ConfigureAwait(false);
                    E2ETenantUser member = await E2ETenantUser.CreateAsync(fx.AuthClient, "storage-member", false, tenant.TenantId).ConfigureAwait(false);
                    foreach (E2ETenantUser user in new[] { tenant, member })
                    {
                        using (HttpClient client = user.CreateClient(fx.BaseUrl))
                        {
                            using (HttpResponseMessage get = await client.GetAsync("/api/v1/storage", ct).ConfigureAwait(false)) AssertStatusCode(HttpStatusCode.Forbidden, get);
                            using (HttpResponseMessage post = await client.PostAsync("/api/v1/storage/refresh", JsonHelper.ToJsonContent(new StorageRefreshRequest()), ct).ConfigureAwait(false)) AssertStatusCode(HttpStatusCode.Forbidden, post);
                        }
                    }
                    using (HttpResponseMessage get = await fx.UnauthClient.GetAsync("/api/v1/storage", ct).ConfigureAwait(false)) AssertStatusCode(HttpStatusCode.Unauthorized, get);
                    using (HttpResponseMessage post = await fx.UnauthClient.PostAsync("/api/v1/storage/refresh", null, ct).ConfigureAwait(false)) AssertStatusCode(HttpStatusCode.Unauthorized, post);
                }),
                new TestCaseDescriptor("E2E.Storage", "cached_read_and_refresh", "Global admin reads snapshots and refresh returns 202", async ct =>
                {
                    E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                    using (HttpResponseMessage get = await fx.AuthClient.GetAsync("/api/v1/storage", ct).ConfigureAwait(false))
                    {
                        AssertStatusCode(HttpStatusCode.OK, get);
                        StorageSnapshot snapshot = await JsonHelper.DeserializeAsync<StorageSnapshot>(get).ConfigureAwait(false);
                        AssertNotNull(snapshot.Database);
                        AssertTrue(snapshot.IntervalSeconds >= 30);
                    }
                    using (HttpResponseMessage post = await fx.AuthClient.PostAsync("/api/v1/storage/refresh", JsonHelper.ToJsonContent(new StorageRefreshRequest { ExactCounts = true }), ct).ConfigureAwait(false)) AssertStatusCode(HttpStatusCode.Accepted, post);
                    using (HttpResponseMessage bad = await fx.AuthClient.PostAsync("/api/v1/storage/refresh", new StringContent("{\"exactCounts\":\"yes\"}"), ct).ConfigureAwait(false)) AssertStatusCode(HttpStatusCode.BadRequest, bad);
                })
            });
        }
    }
}
