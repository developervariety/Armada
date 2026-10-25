namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Models;
    using Armada.Server;
    using Test.Shared.Infrastructure;

    internal static class AskThreadApiTestClients
    {
        internal static async Task<HttpClient> CreateOtherAdminAsync(E2EServerFixture fx)
        {
            string label = Guid.NewGuid().ToString("N").Substring(0, 10);
            HttpResponseMessage response = await fx.AuthClient.PostAsync("/api/v1/users", JsonHelper.ToJsonContent(new UserUpsertRequest
            {
                TenantId = Constants.DefaultTenantId,
                Email = "ask-admin-" + label + "@armada.test",
                Password = Guid.NewGuid().ToString("N"),
                IsAdmin = true,
                IsTenantAdmin = true,
                Active = true
            })).ConfigureAwait(false);
            UserMaster user = await ReadCreatedUserAsync(response).ConfigureAwait(false);
            return await CreateCredentialClientAsync(fx, user.Id, "ask-admin-credential-" + label).ConfigureAwait(false);
        }

        internal static async Task<HttpClient> CreateBearerUserAsync(E2EServerFixture fx, bool tenantAdmin)
        {
            string label = Guid.NewGuid().ToString("N").Substring(0, 10);
            HttpResponseMessage response = await fx.AuthClient.PostAsync("/api/v1/users", JsonHelper.ToJsonContent(new UserUpsertRequest
            {
                TenantId = Constants.DefaultTenantId,
                Email = "ask-user-" + label + "@armada.test",
                Password = Guid.NewGuid().ToString("N"),
                IsTenantAdmin = tenantAdmin,
                Active = true
            })).ConfigureAwait(false);
            UserMaster user = await ReadCreatedUserAsync(response).ConfigureAwait(false);
            return await CreateCredentialClientAsync(fx, user.Id, "ask-user-credential-" + label).ConfigureAwait(false);
        }

        internal static HttpClient CreateBearerClient(string baseUrl, string token)
        {
            HttpClient client = new HttpClient { BaseAddress = new Uri(baseUrl) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }

        private static async Task<UserMaster> ReadCreatedUserAsync(HttpResponseMessage response)
        {
            using (response)
            {
                AssertCreated(response);
                return await JsonHelper.DeserializeAsync<UserMaster>(response).ConfigureAwait(false);
            }
        }

        private static async Task<HttpClient> CreateCredentialClientAsync(E2EServerFixture fx, string userId, string name)
        {
            HttpResponseMessage response = await fx.AuthClient.PostAsync("/api/v1/credentials", JsonHelper.ToJsonContent(new Credential
            {
                TenantId = Constants.DefaultTenantId,
                UserId = userId,
                Name = name
            })).ConfigureAwait(false);
            using (response)
            {
                AssertCreated(response);
                Credential credential = await JsonHelper.DeserializeAsync<Credential>(response).ConfigureAwait(false);
                return CreateBearerClient(fx.BaseUrl, credential.BearerToken);
            }
        }

        private static void AssertCreated(HttpResponseMessage response)
        {
            if (response.StatusCode != System.Net.HttpStatusCode.Created)
                throw new InvalidOperationException("Test identity creation failed with HTTP " + (int)response.StatusCode + ".");
        }
    }
}
