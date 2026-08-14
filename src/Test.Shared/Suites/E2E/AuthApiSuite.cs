namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// End-to-end descriptors for authentication, whoami, tenant lookup, onboarding, and
    /// tenant/user/credential CRUD endpoints, ported 1:1 from the retired automated AuthApiTests
    /// suite. Cases run against the shared e2e server fixture.
    /// </summary>
    public sealed class AuthApiSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "E2E.AuthApi";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Auth API Tests suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("authenticate_with_bearer_default_returns_success_and_token", "Authenticate_WithBearerDefault_ReturnsSuccessAndToken", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                string baseUrl = fx.BaseUrl;

                HttpClient client = new HttpClient();
                client.BaseAddress = new Uri(baseUrl);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "default");

                HttpResponseMessage response = await client.PostAsync("/api/v1/authenticate", null).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                AuthenticateResult result = await JsonHelper.DeserializeAsync<AuthenticateResult>(response).ConfigureAwait(false);
                AssertTrue(result.Success, "Expected Success to be true");
                AssertNotNull(result.Token, "Expected Token to be non-null");
                AssertNotNull(result.ExpiresUtc, "Expected ExpiresUtc to be non-null");

                client.Dispose();
            }));

            cases.Add(CaseAsync("authenticate_with_email_password_returns_success_and_token", "Authenticate_WithEmailPassword_ReturnsSuccessAndToken", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient unauthClient = fx.UnauthClient;

                StringContent content = JsonHelper.ToJsonContent(new
                {
                    TenantId = "default",
                    Email = "admin@armada",
                    Password = "password"
                });

                HttpResponseMessage response = await unauthClient.PostAsync("/api/v1/authenticate", content).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                AuthenticateResult result = await JsonHelper.DeserializeAsync<AuthenticateResult>(response).ConfigureAwait(false);
                AssertTrue(result.Success, "Expected Success to be true");
                AssertNotNull(result.Token, "Expected Token to be non-null");
            }));

            cases.Add(CaseAsync("authenticate_with_invalid_credentials_returns_401", "Authenticate_WithInvalidCredentials_Returns401", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient unauthClient = fx.UnauthClient;

                StringContent content = JsonHelper.ToJsonContent(new
                {
                    TenantId = "default",
                    Email = "admin@armada",
                    Password = "wrongpassword"
                });

                HttpResponseMessage response = await unauthClient.PostAsync("/api/v1/authenticate", content).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Unauthorized, response.StatusCode);

                AuthenticateResult result = await JsonHelper.DeserializeAsync<AuthenticateResult>(response).ConfigureAwait(false);
                AssertFalse(result.Success, "Expected Success to be false");
            }));

            cases.Add(CaseAsync("authenticate_with_invalid_bearer_token_returns_401", "Authenticate_WithInvalidBearerToken_Returns401", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                string baseUrl = fx.BaseUrl;

                HttpClient client = new HttpClient();
                client.BaseAddress = new Uri(baseUrl);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-token-value");

                HttpResponseMessage response = await client.PostAsync("/api/v1/authenticate", null).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Unauthorized, response.StatusCode);

                AuthenticateResult result = await JsonHelper.DeserializeAsync<AuthenticateResult>(response).ConfigureAwait(false);
                AssertFalse(result.Success, "Expected Success to be false");

                client.Dispose();
            }));

            cases.Add(CaseAsync("authenticate_with_api_key_returns_success_and_token", "Authenticate_WithApiKey_ReturnsSuccessAndToken", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.PostAsync("/api/v1/authenticate", null).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                AuthenticateResult result = await JsonHelper.DeserializeAsync<AuthenticateResult>(response).ConfigureAwait(false);
                AssertTrue(result.Success, "Expected Success to be true");
                AssertNotNull(result.Token, "Expected Token to be non-null");
            }));

            cases.Add(CaseAsync("authenticate_with_no_auth_empty_body_returns_401", "Authenticate_WithNoAuth_EmptyBody_Returns401", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient unauthClient = fx.UnauthClient;

                HttpResponseMessage response = await unauthClient.PostAsync("/api/v1/authenticate", null).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            }));

            cases.Add(CaseAsync("whoami_with_bearer_default_returns_tenant_and_user", "WhoAmI_WithBearerDefault_ReturnsTenantAndUser", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                string baseUrl = fx.BaseUrl;

                HttpClient client = new HttpClient();
                client.BaseAddress = new Uri(baseUrl);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "default");

                HttpResponseMessage response = await client.GetAsync("/api/v1/whoami").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                WhoAmIResult result = await JsonHelper.DeserializeAsync<WhoAmIResult>(response).ConfigureAwait(false);
                AssertNotNull(result.Tenant, "Expected Tenant to be non-null");
                AssertNotNull(result.User, "Expected User to be non-null");
                AssertEqual("default", result.Tenant!.Id);
                AssertEqual("default", result.User!.Id);
                AssertEqual("admin@armada", result.User!.Email);

                client.Dispose();
            }));

            cases.Add(CaseAsync("whoami_with_api_key_returns_tenant_and_user", "WhoAmI_WithApiKey_ReturnsTenantAndUser", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/whoami").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                WhoAmIResult result = await JsonHelper.DeserializeAsync<WhoAmIResult>(response).ConfigureAwait(false);
                AssertNotNull(result.Tenant, "Expected Tenant to be non-null");
                AssertNotNull(result.User, "Expected User to be non-null");
            }));

            cases.Add(CaseAsync("whoami_without_auth_returns_401", "WhoAmI_WithoutAuth_Returns401", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient unauthClient = fx.UnauthClient;

                HttpResponseMessage response = await unauthClient.GetAsync("/api/v1/whoami").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            }));

            cases.Add(CaseAsync("whoami_user_password_is_redacted", "WhoAmI_UserPasswordIsRedacted", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                string baseUrl = fx.BaseUrl;

                HttpClient client = new HttpClient();
                client.BaseAddress = new Uri(baseUrl);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "default");

                HttpResponseMessage response = await client.GetAsync("/api/v1/whoami").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                WhoAmIResult result = await JsonHelper.DeserializeAsync<WhoAmIResult>(response).ConfigureAwait(false);
                AssertNotNull(result.User, "Expected User to be non-null");
                AssertEqual("********", result.User!.PasswordSha256);

                client.Dispose();
            }));

            cases.Add(CaseAsync("tenant_lookup_with_valid_email_returns_tenant_list", "TenantLookup_WithValidEmail_ReturnsTenantList", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient unauthClient = fx.UnauthClient;

                StringContent content = JsonHelper.ToJsonContent(new { Email = "admin@armada" });

                HttpResponseMessage response = await unauthClient.PostAsync("/api/v1/tenants/lookup", content).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                TenantLookupResult result = await JsonHelper.DeserializeAsync<TenantLookupResult>(response).ConfigureAwait(false);
                AssertNotNull(result.Tenants, "Expected Tenants to be non-null");
                Assert(result.Tenants.Count >= 1, "Expected at least one tenant for admin@armada");

                bool foundDefault = false;
                foreach (TenantListEntry entry in result.Tenants)
                {
                    if (entry.Id == "default")
                    {
                        foundDefault = true;
                        AssertEqual("Default Tenant", entry.Name);
                    }
                }
                AssertTrue(foundDefault, "Expected 'default' tenant in lookup results");
            }));

            cases.Add(CaseAsync("tenant_lookup_with_unknown_email_returns_empty_list", "TenantLookup_WithUnknownEmail_ReturnsEmptyList", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient unauthClient = fx.UnauthClient;

                StringContent content = JsonHelper.ToJsonContent(new { Email = "unknown-user-" + Guid.NewGuid().ToString("N") + "@nowhere.test" });

                HttpResponseMessage response = await unauthClient.PostAsync("/api/v1/tenants/lookup", content).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                TenantLookupResult result = await JsonHelper.DeserializeAsync<TenantLookupResult>(response).ConfigureAwait(false);
                AssertNotNull(result.Tenants, "Expected Tenants to be non-null");
                AssertEqual(0, result.Tenants.Count, "Expected empty tenant list for unknown email");
            }));

            cases.Add(CaseAsync("onboarding_creates_new_user_with_tenant_and_credential", "Onboarding_CreatesNewUserWithTenantAndCredential", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                HttpClient unauthClient = fx.UnauthClient;

                string email = "onboard-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "@test.armada";
                StringContent content = JsonHelper.ToJsonContent(new
                {
                    TenantId = "default",
                    Email = email,
                    Password = "testpass123",
                    FirstName = "Test",
                    LastName = "User"
                });

                HttpResponseMessage response = await unauthClient.PostAsync("/api/v1/onboarding", content).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                OnboardingResult result = await JsonHelper.DeserializeAsync<OnboardingResult>(response).ConfigureAwait(false);
                AssertTrue(result.Success, "Expected onboarding Success to be true");
                AssertNotNull(result.Tenant, "Expected Tenant to be non-null");
                AssertNotNull(result.User, "Expected User to be non-null");
                AssertNotNull(result.Credential, "Expected Credential to be non-null");

                AssertEqual("default", result.Tenant!.Id);
                AssertEqual(email, result.User!.Email);
                AssertEqual("Test", result.User!.FirstName);
                AssertEqual("User", result.User!.LastName);
                AssertNotNull(result.Credential!.BearerToken, "Expected BearerToken to be non-null");

                // Clean up: delete the created user and credential via admin
                await authClient.DeleteAsync("/api/v1/users/" + result.User.Id).ConfigureAwait(false);
                await authClient.DeleteAsync("/api/v1/credentials/" + result.Credential.Id).ConfigureAwait(false);
            }));

            cases.Add(CaseAsync("onboarding_duplicate_email_returns_409", "Onboarding_DuplicateEmail_Returns409", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                HttpClient unauthClient = fx.UnauthClient;

                string email = "dup-onboard-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "@test.armada";

                // First onboarding
                StringContent content1 = JsonHelper.ToJsonContent(new
                {
                    TenantId = "default",
                    Email = email,
                    Password = "testpass1"
                });
                HttpResponseMessage resp1 = await unauthClient.PostAsync("/api/v1/onboarding", content1).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, resp1.StatusCode);
                OnboardingResult result1 = await JsonHelper.DeserializeAsync<OnboardingResult>(resp1).ConfigureAwait(false);
                AssertTrue(result1.Success, "First onboarding should succeed");

                // Second onboarding with same email + tenant
                StringContent content2 = JsonHelper.ToJsonContent(new
                {
                    TenantId = "default",
                    Email = email,
                    Password = "testpass2"
                });
                HttpResponseMessage resp2 = await unauthClient.PostAsync("/api/v1/onboarding", content2).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Conflict, resp2.StatusCode);

                OnboardingResult result2 = await JsonHelper.DeserializeAsync<OnboardingResult>(resp2).ConfigureAwait(false);
                AssertFalse(result2.Success, "Duplicate onboarding should fail");

                // Clean up
                await authClient.DeleteAsync("/api/v1/users/" + result1.User!.Id).ConfigureAwait(false);
                await authClient.DeleteAsync("/api/v1/credentials/" + result1.Credential!.Id).ConfigureAwait(false);
            }));

            cases.Add(CaseAsync("onboarding_authenticate_with_new_credential", "Onboarding_AuthenticateWithNewCredential", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                HttpClient unauthClient = fx.UnauthClient;
                string baseUrl = fx.BaseUrl;

                string email = "auth-onboard-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "@test.armada";
                StringContent content = JsonHelper.ToJsonContent(new
                {
                    TenantId = "default",
                    Email = email,
                    Password = "newuserpass"
                });

                HttpResponseMessage onboardResp = await unauthClient.PostAsync("/api/v1/onboarding", content).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, onboardResp.StatusCode);
                OnboardingResult onboardResult = await JsonHelper.DeserializeAsync<OnboardingResult>(onboardResp).ConfigureAwait(false);
                AssertTrue(onboardResult.Success, "Onboarding should succeed");

                // Authenticate with the new bearer token
                HttpClient newClient = new HttpClient();
                newClient.BaseAddress = new Uri(baseUrl);
                newClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", onboardResult.Credential!.BearerToken);

                HttpResponseMessage whoamiResp = await newClient.GetAsync("/api/v1/whoami").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, whoamiResp.StatusCode);

                WhoAmIResult whoami = await JsonHelper.DeserializeAsync<WhoAmIResult>(whoamiResp).ConfigureAwait(false);
                AssertNotNull(whoami.User, "Expected User to be non-null");
                AssertEqual(email, whoami.User!.Email);

                newClient.Dispose();

                // Clean up
                await authClient.DeleteAsync("/api/v1/users/" + onboardResult.User!.Id).ConfigureAwait(false);
                await authClient.DeleteAsync("/api/v1/credentials/" + onboardResult.Credential!.Id).ConfigureAwait(false);
            }));

            cases.Add(CaseAsync("tenant_create_returns_201_with_properties", "Tenant_Create_Returns201WithProperties", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                string tenantName = "test-tenant-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                StringContent content = JsonHelper.ToJsonContent(new { Name = tenantName });

                HttpResponseMessage response = await authClient.PostAsync("/api/v1/tenants", content).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, response.StatusCode);

                TenantMetadata tenant = await JsonHelper.DeserializeAsync<TenantMetadata>(response).ConfigureAwait(false);
                AssertNotNull(tenant.Id, "Expected Id to be non-null");
                AssertEqual(tenantName, tenant.Name);
                AssertTrue(tenant.Active, "Expected new tenant to be active");

                // Clean up
                await authClient.DeleteAsync("/api/v1/tenants/" + tenant.Id).ConfigureAwait(false);
            }));

            cases.Add(CaseAsync("tenant_create_seeds_default_admin_user_and_credential", "Tenant_Create_SeedsDefaultAdminUserAndCredential", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                string tenantName = "seed-tenant-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                HttpResponseMessage createResp = await authClient.PostAsync("/api/v1/tenants",
                    JsonHelper.ToJsonContent(new { Name = tenantName })).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, createResp.StatusCode);

                TenantMetadata tenant = await JsonHelper.DeserializeAsync<TenantMetadata>(createResp).ConfigureAwait(false);

                HttpResponseMessage usersResp = await authClient.GetAsync("/api/v1/users").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, usersResp.StatusCode);
                EnumerationResult<UserMaster> users = await JsonHelper.DeserializeAsync<EnumerationResult<UserMaster>>(usersResp).ConfigureAwait(false);
                UserMaster? seededUser = users.Objects.FirstOrDefault(u => u.TenantId == tenant.Id && u.Email == "admin@armada");
                AssertNotNull(seededUser, "Expected tenant creation to seed admin@armada");
                AssertFalse(seededUser!.IsAdmin, "Expected tenant-seeded user to not be a global admin");
                AssertTrue(seededUser.IsTenantAdmin, "Expected tenant-seeded user to be a tenant admin");

                HttpResponseMessage credsResp = await authClient.GetAsync("/api/v1/credentials").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, credsResp.StatusCode);
                EnumerationResult<Credential> creds = await JsonHelper.DeserializeAsync<EnumerationResult<Credential>>(credsResp).ConfigureAwait(false);
                Credential? seededCred = creds.Objects.FirstOrDefault(c => c.TenantId == tenant.Id && c.UserId == seededUser.Id);
                AssertNotNull(seededCred, "Expected tenant creation to seed a credential for the default admin");
                AssertTrue(!string.IsNullOrEmpty(seededCred!.BearerToken), "Expected seeded credential bearer token");

                await authClient.DeleteAsync("/api/v1/tenants/" + tenant.Id).ConfigureAwait(false);
            }));

            cases.Add(CaseAsync("tenant_list_returns_enumeration_result", "Tenant_List_ReturnsEnumerationResult", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/tenants").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<TenantMetadata> result = await JsonHelper.DeserializeAsync<EnumerationResult<TenantMetadata>>(response).ConfigureAwait(false);
                AssertTrue(result.Success, "Expected Success to be true");
                Assert(result.TotalRecords >= 1, "Expected at least one tenant (default)");
                AssertNotNull(result.Objects, "Expected Objects to be non-null");
                Assert(result.Objects.Count >= 1, "Expected at least one tenant in Objects");
            }));

            cases.Add(CaseAsync("tenant_list_paginated", "Tenant_List_Paginated", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/tenants?pageSize=1&pageNumber=1").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<TenantMetadata> result = await JsonHelper.DeserializeAsync<EnumerationResult<TenantMetadata>>(response).ConfigureAwait(false);
                AssertTrue(result.Success, "Expected Success to be true");
                AssertEqual(1, result.PageSize, "Expected PageSize to be 1");
                AssertEqual(1, result.PageNumber, "Expected PageNumber to be 1");
                Assert(result.Objects.Count <= 1, "Expected at most 1 tenant per page");
            }));

            cases.Add(CaseAsync("tenant_read_returns_correct_tenant", "Tenant_Read_ReturnsCorrectTenant", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                // Create a tenant
                string tenantName = "read-tenant-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                StringContent content = JsonHelper.ToJsonContent(new { Name = tenantName });
                HttpResponseMessage createResp = await authClient.PostAsync("/api/v1/tenants", content).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, createResp.StatusCode);
                TenantMetadata created = await JsonHelper.DeserializeAsync<TenantMetadata>(createResp).ConfigureAwait(false);

                // Read it back
                HttpResponseMessage readResp = await authClient.GetAsync("/api/v1/tenants/" + created.Id).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, readResp.StatusCode);
                TenantMetadata readTenant = await JsonHelper.DeserializeAsync<TenantMetadata>(readResp).ConfigureAwait(false);
                AssertEqual(created.Id, readTenant.Id);
                AssertEqual(tenantName, readTenant.Name);

                // Clean up
                await authClient.DeleteAsync("/api/v1/tenants/" + created.Id).ConfigureAwait(false);
            }));

            cases.Add(CaseAsync("tenant_update_returns_updated_tenant", "Tenant_Update_ReturnsUpdatedTenant", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                // Create a tenant
                string originalName = "update-tenant-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                StringContent createContent = JsonHelper.ToJsonContent(new { Name = originalName });
                HttpResponseMessage createResp = await authClient.PostAsync("/api/v1/tenants", createContent).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, createResp.StatusCode);
                TenantMetadata created = await JsonHelper.DeserializeAsync<TenantMetadata>(createResp).ConfigureAwait(false);

                // Update it
                string updatedName = "updated-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                StringContent updateContent = JsonHelper.ToJsonContent(new { Name = updatedName });
                HttpResponseMessage updateResp = await authClient.PutAsync("/api/v1/tenants/" + created.Id, updateContent).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, updateResp.StatusCode);
                TenantMetadata updated = await JsonHelper.DeserializeAsync<TenantMetadata>(updateResp).ConfigureAwait(false);
                AssertEqual(updatedName, updated.Name);

                // Verify update persisted
                HttpResponseMessage readResp = await authClient.GetAsync("/api/v1/tenants/" + created.Id).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, readResp.StatusCode);
                TenantMetadata readTenant = await JsonHelper.DeserializeAsync<TenantMetadata>(readResp).ConfigureAwait(false);
                AssertEqual(updatedName, readTenant.Name);

                // Clean up
                await authClient.DeleteAsync("/api/v1/tenants/" + created.Id).ConfigureAwait(false);
            }));

            cases.Add(CaseAsync("tenant_delete_removes_tenant", "Tenant_Delete_RemovesTenant", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                // Create a tenant
                string tenantName = "delete-tenant-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                StringContent content = JsonHelper.ToJsonContent(new { Name = tenantName });
                HttpResponseMessage createResp = await authClient.PostAsync("/api/v1/tenants", content).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, createResp.StatusCode);
                TenantMetadata created = await JsonHelper.DeserializeAsync<TenantMetadata>(createResp).ConfigureAwait(false);

                // Delete it
                HttpResponseMessage deleteResp = await authClient.DeleteAsync("/api/v1/tenants/" + created.Id).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, deleteResp.StatusCode);

                // Verify deleted
                HttpResponseMessage readResp = await authClient.GetAsync("/api/v1/tenants/" + created.Id).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, readResp.StatusCode);
            }));

            cases.Add(CaseAsync("tenant_list_without_auth_returns_401", "Tenant_List_WithoutAuth_Returns401", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient unauthClient = fx.UnauthClient;

                HttpResponseMessage response = await unauthClient.GetAsync("/api/v1/tenants").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            }));

            cases.Add(CaseAsync("tenant_read_non_existent_returns_404", "Tenant_ReadNonExistent_Returns404", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/tenants/nonexistent-" + Guid.NewGuid().ToString("N")).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode);
            }));

            cases.Add(CaseAsync("user_create_returns_201_with_properties", "User_Create_Returns201WithProperties", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                string email = "crud-user-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "@test.armada";
                StringContent content = JsonHelper.ToJsonContent(new
                {
                    TenantId = "default",
                    Email = email,
                    PasswordSha256 = UserMaster.ComputePasswordHash("testpass"),
                    FirstName = "CrudTest",
                    LastName = "User"
                });

                HttpResponseMessage response = await authClient.PostAsync("/api/v1/users", content).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, response.StatusCode);

                UserMaster user = await JsonHelper.DeserializeAsync<UserMaster>(response).ConfigureAwait(false);
                AssertNotNull(user.Id, "Expected Id to be non-null");
                AssertEqual(email, user.Email);
                AssertEqual("CrudTest", user.FirstName);
                AssertEqual("********", user.PasswordSha256, "Expected password to be redacted");

                // Clean up
                await authClient.DeleteAsync("/api/v1/users/" + user.Id).ConfigureAwait(false);
            }));

            cases.Add(CaseAsync("user_list_returns_enumeration_result", "User_List_ReturnsEnumerationResult", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/users").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<UserMaster> result = await JsonHelper.DeserializeAsync<EnumerationResult<UserMaster>>(response).ConfigureAwait(false);
                AssertTrue(result.Success, "Expected Success to be true");
                Assert(result.TotalRecords >= 1, "Expected at least one user (default)");
                AssertNotNull(result.Objects, "Expected Objects to be non-null");
                Assert(result.Objects.Count >= 1, "Expected at least one user in Objects");
            }));

            cases.Add(CaseAsync("user_list_paginated", "User_List_Paginated", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/users?pageSize=1&pageNumber=1").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<UserMaster> result = await JsonHelper.DeserializeAsync<EnumerationResult<UserMaster>>(response).ConfigureAwait(false);
                AssertEqual(1, result.PageSize, "Expected PageSize to be 1");
                Assert(result.Objects.Count <= 1, "Expected at most 1 user per page");
            }));

            cases.Add(CaseAsync("user_read_returns_correct_user", "User_Read_ReturnsCorrectUser", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                // Create a user
                string email = "read-user-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "@test.armada";
                StringContent content = JsonHelper.ToJsonContent(new
                {
                    TenantId = "default",
                    Email = email,
                    PasswordSha256 = UserMaster.ComputePasswordHash("testpass")
                });
                HttpResponseMessage createResp = await authClient.PostAsync("/api/v1/users", content).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, createResp.StatusCode);
                UserMaster created = await JsonHelper.DeserializeAsync<UserMaster>(createResp).ConfigureAwait(false);

                // Read it back
                HttpResponseMessage readResp = await authClient.GetAsync("/api/v1/users/" + created.Id).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, readResp.StatusCode);
                UserMaster readUser = await JsonHelper.DeserializeAsync<UserMaster>(readResp).ConfigureAwait(false);
                AssertEqual(created.Id, readUser.Id);
                AssertEqual(email, readUser.Email);
                AssertEqual("********", readUser.PasswordSha256, "Expected password to be redacted");

                // Clean up
                await authClient.DeleteAsync("/api/v1/users/" + created.Id).ConfigureAwait(false);
            }));

            cases.Add(CaseAsync("user_update_returns_updated_user", "User_Update_ReturnsUpdatedUser", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                // Create a user
                string email = "update-user-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "@test.armada";
                StringContent content = JsonHelper.ToJsonContent(new
                {
                    TenantId = "default",
                    Email = email,
                    PasswordSha256 = UserMaster.ComputePasswordHash("testpass"),
                    FirstName = "Before"
                });
                HttpResponseMessage createResp = await authClient.PostAsync("/api/v1/users", content).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, createResp.StatusCode);
                UserMaster created = await JsonHelper.DeserializeAsync<UserMaster>(createResp).ConfigureAwait(false);

                // Update it
                StringContent updateContent = JsonHelper.ToJsonContent(new
                {
                    TenantId = "default",
                    Email = email,
                    PasswordSha256 = UserMaster.ComputePasswordHash("testpass"),
                    FirstName = "After",
                    LastName = "Updated"
                });
                HttpResponseMessage updateResp = await authClient.PutAsync("/api/v1/users/" + created.Id, updateContent).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, updateResp.StatusCode);
                UserMaster updated = await JsonHelper.DeserializeAsync<UserMaster>(updateResp).ConfigureAwait(false);
                AssertEqual("After", updated.FirstName);
                AssertEqual("Updated", updated.LastName);

                // Clean up
                await authClient.DeleteAsync("/api/v1/users/" + created.Id).ConfigureAwait(false);
            }));

            cases.Add(CaseAsync("user_delete_removes_user", "User_Delete_RemovesUser", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                // Create a user
                string email = "delete-user-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "@test.armada";
                StringContent content = JsonHelper.ToJsonContent(new
                {
                    TenantId = "default",
                    Email = email,
                    PasswordSha256 = UserMaster.ComputePasswordHash("testpass")
                });
                HttpResponseMessage createResp = await authClient.PostAsync("/api/v1/users", content).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, createResp.StatusCode);
                UserMaster created = await JsonHelper.DeserializeAsync<UserMaster>(createResp).ConfigureAwait(false);

                // Delete it
                HttpResponseMessage deleteResp = await authClient.DeleteAsync("/api/v1/users/" + created.Id).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, deleteResp.StatusCode);

                // Verify deleted
                HttpResponseMessage readResp = await authClient.GetAsync("/api/v1/users/" + created.Id).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, readResp.StatusCode);
            }));

            cases.Add(CaseAsync("user_list_without_auth_returns_401", "User_List_WithoutAuth_Returns401", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient unauthClient = fx.UnauthClient;

                HttpResponseMessage response = await unauthClient.GetAsync("/api/v1/users").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            }));

            cases.Add(CaseAsync("user_read_non_existent_returns_404", "User_ReadNonExistent_Returns404", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/users/nonexistent-" + Guid.NewGuid().ToString("N")).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, response.StatusCode);
            }));

            cases.Add(CaseAsync("credential_create_returns_201_with_properties", "Credential_Create_Returns201WithProperties", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                StringContent content = JsonHelper.ToJsonContent(new
                {
                    TenantId = "default",
                    UserId = "default",
                    Name = "test-cred-" + Guid.NewGuid().ToString("N").Substring(0, 8)
                });

                HttpResponseMessage response = await authClient.PostAsync("/api/v1/credentials", content).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, response.StatusCode);

                Credential cred = await JsonHelper.DeserializeAsync<Credential>(response).ConfigureAwait(false);
                AssertNotNull(cred.Id, "Expected Id to be non-null");
                AssertNotNull(cred.BearerToken, "Expected BearerToken to be non-null");
                Assert(cred.BearerToken.Length > 0, "Expected BearerToken to be non-empty");
                AssertEqual("default", cred.TenantId);
                AssertEqual("default", cred.UserId);
                AssertTrue(cred.Active, "Expected new credential to be active");

                // Clean up
                await authClient.DeleteAsync("/api/v1/credentials/" + cred.Id).ConfigureAwait(false);
            }));

            cases.Add(CaseAsync("credential_list_returns_enumeration_result", "Credential_List_ReturnsEnumerationResult", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                HttpResponseMessage response = await authClient.GetAsync("/api/v1/credentials").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, response.StatusCode);

                EnumerationResult<Credential> result = await JsonHelper.DeserializeAsync<EnumerationResult<Credential>>(response).ConfigureAwait(false);
                AssertNotNull(result.Objects, "Expected Objects to be non-null");
                Assert(result.Objects.Count >= 1, "Expected at least one credential (default)");
            }));

            cases.Add(CaseAsync("credential_delete_removes_credential", "Credential_Delete_RemovesCredential", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                // Create a credential
                StringContent content = JsonHelper.ToJsonContent(new
                {
                    TenantId = "default",
                    UserId = "default",
                    Name = "deletable-cred"
                });
                HttpResponseMessage createResp = await authClient.PostAsync("/api/v1/credentials", content).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, createResp.StatusCode);
                Credential created = await JsonHelper.DeserializeAsync<Credential>(createResp).ConfigureAwait(false);

                // Delete it
                HttpResponseMessage deleteResp = await authClient.DeleteAsync("/api/v1/credentials/" + created.Id).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, deleteResp.StatusCode);

                // Verify deleted
                HttpResponseMessage readResp = await authClient.GetAsync("/api/v1/credentials/" + created.Id).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.NotFound, readResp.StatusCode);
            }));

            cases.Add(CaseAsync("credential_list_without_auth_returns_401", "Credential_List_WithoutAuth_Returns401", TestTags.Negative, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient unauthClient = fx.UnauthClient;

                HttpResponseMessage response = await unauthClient.GetAsync("/api/v1/credentials").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            }));

            cases.Add(CaseAsync("credential_create_with_bearer_auth_sets_user_context", "Credential_Create_WithBearerAuth_SetsUserContext", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                string baseUrl = fx.BaseUrl;

                HttpClient bearerClient = new HttpClient();
                bearerClient.BaseAddress = new Uri(baseUrl);
                bearerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "default");

                StringContent content = JsonHelper.ToJsonContent(new
                {
                    Name = "bearer-cred-" + Guid.NewGuid().ToString("N").Substring(0, 8)
                });

                HttpResponseMessage response = await bearerClient.PostAsync("/api/v1/credentials", content).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, response.StatusCode);

                Credential cred = await JsonHelper.DeserializeAsync<Credential>(response).ConfigureAwait(false);
                AssertEqual("default", cred.TenantId, "Expected non-admin credential to be scoped to own tenant");
                AssertEqual("default", cred.UserId, "Expected non-admin credential to be scoped to own user");

                // Clean up
                await authClient.DeleteAsync("/api/v1/credentials/" + cred.Id).ConfigureAwait(false);
                bearerClient.Dispose();
            }));

            cases.Add(CaseAsync("credential_new_token_can_authenticate", "Credential_NewToken_CanAuthenticate", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;
                string baseUrl = fx.BaseUrl;

                // Create a credential
                StringContent content = JsonHelper.ToJsonContent(new
                {
                    TenantId = "default",
                    UserId = "default",
                    Name = "auth-test-cred"
                });
                HttpResponseMessage createResp = await authClient.PostAsync("/api/v1/credentials", content).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.Created, createResp.StatusCode);
                Credential cred = await JsonHelper.DeserializeAsync<Credential>(createResp).ConfigureAwait(false);

                // Use the new token to authenticate
                HttpClient tokenClient = new HttpClient();
                tokenClient.BaseAddress = new Uri(baseUrl);
                tokenClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cred.BearerToken);

                HttpResponseMessage whoamiResp = await tokenClient.GetAsync("/api/v1/whoami").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, whoamiResp.StatusCode);

                WhoAmIResult whoami = await JsonHelper.DeserializeAsync<WhoAmIResult>(whoamiResp).ConfigureAwait(false);
                AssertNotNull(whoami.User, "Expected User to be non-null");
                AssertEqual("default", whoami.User!.Id);

                tokenClient.Dispose();

                // Clean up
                await authClient.DeleteAsync("/api/v1/credentials/" + cred.Id).ConfigureAwait(false);
            }));

            cases.Add(CaseAsync("session_token_authenticate_then_whoami", "SessionToken_AuthenticateThenWhoAmI", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                string baseUrl = fx.BaseUrl;

                // Get a session token via authenticate
                HttpClient bearerClient = new HttpClient();
                bearerClient.BaseAddress = new Uri(baseUrl);
                bearerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "default");

                HttpResponseMessage authResp = await bearerClient.PostAsync("/api/v1/authenticate", null).ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, authResp.StatusCode);

                AuthenticateResult authResult = await JsonHelper.DeserializeAsync<AuthenticateResult>(authResp).ConfigureAwait(false);
                AssertTrue(authResult.Success, "Expected authentication to succeed");
                AssertNotNull(authResult.Token, "Expected session token");

                bearerClient.Dispose();

                // Use session token to call whoami
                HttpClient sessionClient = new HttpClient();
                sessionClient.BaseAddress = new Uri(baseUrl);
                sessionClient.DefaultRequestHeaders.Add("X-Token", authResult.Token);

                HttpResponseMessage whoamiResp = await sessionClient.GetAsync("/api/v1/whoami").ConfigureAwait(false);
                AssertEqual(HttpStatusCode.OK, whoamiResp.StatusCode);

                WhoAmIResult whoami = await JsonHelper.DeserializeAsync<WhoAmIResult>(whoamiResp).ConfigureAwait(false);
                AssertNotNull(whoami.Tenant, "Expected Tenant to be non-null");
                AssertNotNull(whoami.User, "Expected User to be non-null");
                AssertEqual("default", whoami.User!.Id);

                sessionClient.Dispose();
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Auth API Tests",
                cases: cases);
        }

        #endregion

        #region Private-Methods

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
