namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
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
    /// End-to-end checks for managed model endpoint REST routes. These cases use the live in-process server so
    /// authentication, route registration, service authorization, and response masking are tested together.
    /// </summary>
    public sealed class ModelEndpointSuite : IArmadaTestSuite
    {
        /// <summary>Build live REST route cases.</summary>
        /// <returns>The model endpoint REST test descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("health_sweep_requires_authentication", "ModelEndpointHealthSweep_RequiresAuthentication", TestTags.Negative, async () =>
            {
                E2EServerFixture fixture = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                using (HttpResponseMessage response = await fixture.UnauthClient.PostAsync("/api/v1/model-endpoints/health-check", null).ConfigureAwait(false))
                {
                    AssertEqual(HttpStatusCode.Unauthorized, response.StatusCode);
                }
            }));

            cases.Add(CaseAsync("authenticated_health_sweep_uses_registered_route", "ModelEndpointHealthSweep_UsesRegisteredRoute", TestTags.Positive, async () =>
            {
                E2EServerFixture fixture = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                AssertTrue(fixture.BaseUrl.StartsWith("http://127.0.0.1:", StringComparison.Ordinal), "Health sweep test requires the isolated loopback fixture.");
                AssertTrue(String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ARMADA_E2E_URL")), "Health sweep test refuses a configured remote E2E server.");
                using (HttpResponseMessage response = await fixture.AuthClient.PostAsync("/api/v1/model-endpoints/health-check", null).ConfigureAwait(false))
                {
                    AssertEqual(HttpStatusCode.OK, response.StatusCode);
                    ModelEndpointHealthSweepResponse summary = await JsonHelper.DeserializeAsync<ModelEndpointHealthSweepResponse>(response).ConfigureAwait(false);
                    AssertEqual(0, summary.EndpointsProbed, "The isolated fixture must have no enabled external endpoints.");
                }
            }));

            cases.Add(CaseAsync("cross_tenant_crud_and_validation_are_denied", "ModelEndpointRoutes_CrossTenantCrudAndValidationAreDenied", TestTags.Negative, async () =>
            {
                E2EServerFixture fixture = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                TenantMetadata? tenant = null;
                string? endpointId = null;
                try
                {
                    using (HttpResponseMessage tenantResponse = await fixture.AuthClient.PostAsync("/api/v1/tenants", JsonHelper.ToJsonContent(new TenantCreateRequest { Name = "endpoint-cross-" + Guid.NewGuid().ToString("N") })).ConfigureAwait(false))
                    {
                        AssertEqual(HttpStatusCode.Created, tenantResponse.StatusCode);
                        tenant = await JsonHelper.DeserializeAsync<TenantMetadata>(tenantResponse).ConfigureAwait(false);
                    }
                    UserMaster userRequest = new UserMaster
                    {
                        TenantId = tenant!.Id,
                        Email = "endpoint-cross-user-" + Guid.NewGuid().ToString("N") + "@example.test",
                        PasswordSha256 = UserMaster.ComputePasswordHash("route-test-password"),
                        IsTenantAdmin = false,
                        IsAdmin = false
                    };
                    using (HttpResponseMessage userResponse = await fixture.AuthClient.PostAsync("/api/v1/users", JsonHelper.ToJsonContent(userRequest)).ConfigureAwait(false))
                    {
                        AssertEqual(HttpStatusCode.Created, userResponse.StatusCode);
                        userRequest = await JsonHelper.DeserializeAsync<UserMaster>(userResponse).ConfigureAwait(false);
                    }
                    using (HttpResponseMessage credentialResponse = await fixture.AuthClient.PostAsync("/api/v1/credentials", JsonHelper.ToJsonContent(new CredentialCreateRequest { TenantId = tenant.Id, UserId = userRequest.Id, Name = "endpoint-cross-credential" })).ConfigureAwait(false))
                    {
                        AssertEqual(HttpStatusCode.Created, credentialResponse.StatusCode);
                        Credential credential = await JsonHelper.DeserializeAsync<Credential>(credentialResponse).ConfigureAwait(false);
                        using (HttpClient crossClient = new HttpClient { BaseAddress = new Uri(fixture.BaseUrl) })
                        {
                            crossClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential.BearerToken);
                            using (HttpResponseMessage createResponse = await fixture.AuthClient.PostAsync("/api/v1/model-endpoints", JsonHelper.ToJsonContent(new ModelEndpointCreateRequest
                            {
                                Name = "cross-tenant-owner",
                                Kind = Armada.Core.Enums.ModelEndpointKindEnum.Inference,
                                Scope = Armada.Core.Enums.ScopeEnum.UserSpecific,
                                Provider = Armada.Core.Enums.ModelProviderEnum.OpenAI,
                                BaseUrl = "http://127.0.0.1:1",
                                Model = "fixture-model"
                            })).ConfigureAwait(false))
                            {
                                AssertEqual(HttpStatusCode.Created, createResponse.StatusCode);
                                ModelEndpoint created = await JsonHelper.DeserializeAsync<ModelEndpoint>(createResponse).ConfigureAwait(false);
                                endpointId = created.Id;
                            }
                            using (HttpResponseMessage readResponse = await crossClient.GetAsync("/api/v1/model-endpoints/" + endpointId).ConfigureAwait(false))
                                AssertTenantDenied(readResponse);
                            using (HttpResponseMessage updateResponse = await crossClient.PutAsync("/api/v1/model-endpoints/" + endpointId, JsonHelper.ToJsonContent(new ModelEndpointCreateRequest
                            {
                                Name = "cross-update", Kind = Armada.Core.Enums.ModelEndpointKindEnum.Inference, Scope = Armada.Core.Enums.ScopeEnum.UserSpecific,
                                Provider = Armada.Core.Enums.ModelProviderEnum.OpenAI, BaseUrl = "http://127.0.0.1:1", Model = "fixture-model"
                            })).ConfigureAwait(false))
                                AssertTenantDenied(updateResponse);
                            using (HttpResponseMessage validateResponse = await crossClient.PostAsync("/api/v1/model-endpoints/" + endpointId + "/validate", null).ConfigureAwait(false))
                                AssertTenantDenied(validateResponse);
                            using (HttpResponseMessage deleteResponse = await crossClient.DeleteAsync("/api/v1/model-endpoints/" + endpointId).ConfigureAwait(false))
                                AssertTenantDenied(deleteResponse);
                        }
                    }
                }
                finally
                {
                    if (endpointId != null)
                    {
                        using (HttpResponseMessage _ = await fixture.AuthClient.DeleteAsync("/api/v1/model-endpoints/" + endpointId).ConfigureAwait(false)) { }
                    }
                    if (tenant != null)
                    {
                        using (HttpResponseMessage _ = await fixture.AuthClient.DeleteAsync("/api/v1/tenants/" + tenant.Id).ConfigureAwait(false)) { }
                    }
                }
            }));

            cases.Add(CaseAsync("malformed_endpoint_body_is_bad_request", "ModelEndpointRoutes_MalformedBodyIsBadRequest", TestTags.Negative, async () =>
            {
                E2EServerFixture fixture = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                using (StringContent content = new StringContent("{\"provider\":\"NotAProvider\"}", System.Text.Encoding.UTF8, "application/json"))
                using (HttpResponseMessage response = await fixture.AuthClient.PostAsync("/api/v1/model-endpoints", content).ConfigureAwait(false))
                    AssertEqual(HttpStatusCode.BadRequest, response.StatusCode);
                using (StringContent nullContent = new StringContent("null", System.Text.Encoding.UTF8, "application/json"))
                using (HttpResponseMessage nullResponse = await fixture.AuthClient.PostAsync("/api/v1/model-endpoints", nullContent).ConfigureAwait(false))
                    AssertEqual(HttpStatusCode.BadRequest, nullResponse.StatusCode);
                using (StringContent malformedUpdate = new StringContent("{\"kind\":\"NotAKind\"}", System.Text.Encoding.UTF8, "application/json"))
                using (HttpResponseMessage updateResponse = await fixture.AuthClient.PutAsync("/api/v1/model-endpoints/mep_missing", malformedUpdate).ConfigureAwait(false))
                    AssertEqual(HttpStatusCode.BadRequest, updateResponse.StatusCode);
            }));

            cases.Add(CaseAsync("captain_endpoint_linkage_enforces_scope_and_delete_guard", "Captain model endpoint linkage enforces scope and deletion guard", TestTags.Negative, async () =>
            {
                E2EServerFixture fixture = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                (TenantMetadata ownerTenant, UserMaster ownerUser, Credential ownerCredential) = await CreateRoutePrincipalAsync(fixture, "endpoint-link-owner").ConfigureAwait(false);
                (TenantMetadata otherTenant, UserMaster otherUser, Credential otherCredential) = await CreateRoutePrincipalAsync(fixture, "endpoint-link-other").ConfigureAwait(false);
                UserMaster sameTenantOtherUser = new UserMaster
                {
                    TenantId = ownerTenant.Id,
                    Email = "endpoint-link-same-tenant-" + Guid.NewGuid().ToString("N") + "@example.test",
                    PasswordSha256 = UserMaster.ComputePasswordHash("route-test-password"),
                    IsTenantAdmin = true,
                    IsAdmin = false
                };
                using (HttpResponseMessage sameTenantUserResponse = await fixture.AuthClient.PostAsync("/api/v1/users", JsonHelper.ToJsonContent(sameTenantOtherUser)).ConfigureAwait(false))
                {
                    AssertEqual(HttpStatusCode.Created, sameTenantUserResponse.StatusCode);
                    sameTenantOtherUser = await JsonHelper.DeserializeAsync<UserMaster>(sameTenantUserResponse).ConfigureAwait(false);
                }
                Credential sameTenantOtherCredential = await CreateCredentialAsync(fixture, ownerTenant.Id, sameTenantOtherUser.Id, "endpoint-link-same-tenant").ConfigureAwait(false);
                string? endpointId = null;
                string? captainId = null;
                using (HttpClient ownerClient = CreateBearerClient(fixture, ownerCredential.BearerToken))
                using (HttpClient otherClient = CreateBearerClient(fixture, otherCredential.BearerToken))
                using (HttpClient sameTenantOtherClient = CreateBearerClient(fixture, sameTenantOtherCredential.BearerToken))
                {
                    try
                    {
                        using (HttpResponseMessage createEndpointResponse = await ownerClient.PostAsync("/api/v1/model-endpoints", JsonHelper.ToJsonContent(new ModelEndpointCreateRequest
                        {
                            Name = "Scoped captain endpoint",
                            Kind = Armada.Core.Enums.ModelEndpointKindEnum.Inference,
                            Scope = Armada.Core.Enums.ScopeEnum.UserSpecific,
                            Provider = Armada.Core.Enums.ModelProviderEnum.OpenAI,
                            BaseUrl = "http://127.0.0.1:1",
                            Model = "scoped-model"
                        })).ConfigureAwait(false))
                        {
                            AssertEqual(HttpStatusCode.Created, createEndpointResponse.StatusCode);
                            ModelEndpoint endpoint = await JsonHelper.DeserializeAsync<ModelEndpoint>(createEndpointResponse).ConfigureAwait(false);
                            endpointId = endpoint.Id;
                            AssertEqual("Scoped captain endpoint", endpoint.Name);
                            AssertFalse(endpoint.Enabled, "Endpoint creation must default to disabled.");
                            AssertFalse(String.IsNullOrEmpty(endpoint.Id), "Created endpoint must return its server ID.");
                        }

                        using (HttpResponseMessage enableResponse = await ownerClient.PutAsync("/api/v1/model-endpoints/" + endpointId, JsonHelper.ToJsonContent(new ModelEndpointCreateRequest
                        {
                            Name = "Scoped captain endpoint",
                            Kind = Armada.Core.Enums.ModelEndpointKindEnum.Inference,
                            Scope = Armada.Core.Enums.ScopeEnum.UserSpecific,
                            Provider = Armada.Core.Enums.ModelProviderEnum.OpenAI,
                            BaseUrl = "http://127.0.0.1:1",
                            Model = "scoped-model",
                            Enabled = true
                        })).ConfigureAwait(false))
                            AssertEqual(HttpStatusCode.OK, enableResponse.StatusCode);

                        using (HttpResponseMessage validCaptainResponse = await ownerClient.PostAsync("/api/v1/captains", JsonHelper.ToJsonContent(new Captain("Scoped API captain", Armada.Core.Enums.AgentRuntimeEnum.ApiEndpoint)
                        {
                            Model = "scoped-model",
                            ModelEndpointId = endpointId
                        })).ConfigureAwait(false))
                        {
                            AssertEqual(HttpStatusCode.Created, validCaptainResponse.StatusCode);
                            Captain captain = await JsonHelper.DeserializeAsync<Captain>(validCaptainResponse).ConfigureAwait(false);
                            captainId = captain.Id;
                            AssertEqual(endpointId, captain.ModelEndpointId, "Valid captain must retain its endpoint link.");
                            AssertEqual("Scoped API captain", captain.Name, "Valid captain response must round-trip its name.");
                        }

                        using (HttpResponseMessage crossTenantCreate = await otherClient.PostAsync("/api/v1/captains", JsonHelper.ToJsonContent(new Captain("Cross tenant API captain", Armada.Core.Enums.AgentRuntimeEnum.ApiEndpoint)
                        {
                            Model = "scoped-model",
                            ModelEndpointId = endpointId
                        })).ConfigureAwait(false))
                            AssertEqual(HttpStatusCode.BadRequest, crossTenantCreate.StatusCode, "Cross-tenant endpoint admission must be rejected.");

                        using (HttpResponseMessage privateOwnerCreate = await sameTenantOtherClient.PostAsync("/api/v1/captains", JsonHelper.ToJsonContent(new Captain("Private owner mismatch", Armada.Core.Enums.AgentRuntimeEnum.ApiEndpoint)
                        {
                            Model = "scoped-model",
                            ModelEndpointId = endpointId
                        })).ConfigureAwait(false))
                            AssertEqual(HttpStatusCode.BadRequest, privateOwnerCreate.StatusCode, "Private endpoint admission by another user must be rejected.");

                        using (HttpResponseMessage crossTenantUpdate = await ownerClient.PutAsync("/api/v1/captains/" + captainId, JsonHelper.ToJsonContent(new Captain("Scoped API captain changed", Armada.Core.Enums.AgentRuntimeEnum.ApiEndpoint)
                        {
                            Model = "other-model",
                            ModelEndpointId = "mep_cross_tenant"
                        })).ConfigureAwait(false))
                            AssertEqual(HttpStatusCode.BadRequest, crossTenantUpdate.StatusCode, "Cross-tenant captain update must be rejected.");

                        using (HttpResponseMessage listResponse = await fixture.AuthClient.GetAsync("/api/v1/captains").ConfigureAwait(false))
                        {
                            string listJson = await listResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                            AssertContains("Scoped API captain", listJson, "Rejected update must leave the valid captain present.");
                            AssertFalse(listJson.Contains("Cross tenant API captain", StringComparison.Ordinal), "Rejected cross-tenant create must not create a captain.");
                            AssertFalse(listJson.Contains("Private owner mismatch", StringComparison.Ordinal), "Rejected private-owner create must not create a captain.");
                        }

                        using (HttpResponseMessage deleteInUse = await fixture.AuthClient.DeleteAsync("/api/v1/model-endpoints/" + endpointId).ConfigureAwait(false))
                            AssertEqual(HttpStatusCode.Conflict, deleteInUse.StatusCode, "A referenced endpoint must return HTTP 409 on deletion.");
                    }
                    finally
                    {
                        if (captainId != null)
                        {
                            using (HttpResponseMessage response = await ownerClient.DeleteAsync("/api/v1/captains/" + captainId).ConfigureAwait(false)) { }
                        }
                        if (endpointId != null)
                        {
                            using (HttpResponseMessage response = await fixture.AuthClient.DeleteAsync("/api/v1/model-endpoints/" + endpointId).ConfigureAwait(false)) { }
                        }
                        using (HttpResponseMessage response = await fixture.AuthClient.DeleteAsync("/api/v1/tenants/" + ownerTenant.Id).ConfigureAwait(false)) { }
                        using (HttpResponseMessage response = await fixture.AuthClient.DeleteAsync("/api/v1/tenants/" + otherTenant.Id).ConfigureAwait(false)) { }
                    }
                }
            }));

            cases.Add(CaseAsync("non_admin_health_sweep_is_forbidden", "ModelEndpointHealthSweep_RejectsAuthenticatedNonAdmin", TestTags.Negative, async () =>
            {
                E2EServerFixture fixture = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                TenantMetadata? tenant = null;
                using (HttpClient userClient = new HttpClient { BaseAddress = new Uri(fixture.BaseUrl) })
                {
                    string? ownerEndpointId = null;
                    try
                    {
                        using (HttpResponseMessage tenantResponse = await fixture.AuthClient.PostAsync("/api/v1/tenants", JsonHelper.ToJsonContent(new TenantCreateRequest { Name = "endpoint-route-tenant-" + Guid.NewGuid().ToString("N") })).ConfigureAwait(false))
                        {
                            AssertEqual(HttpStatusCode.Created, tenantResponse.StatusCode);
                            tenant = await JsonHelper.DeserializeAsync<TenantMetadata>(tenantResponse).ConfigureAwait(false);
                        }
                        UserMaster userRequest = new UserMaster
                        {
                            TenantId = tenant!.Id,
                            Email = "endpoint-route-user-" + Guid.NewGuid().ToString("N") + "@example.test",
                            PasswordSha256 = UserMaster.ComputePasswordHash("route-test-password"),
                            IsTenantAdmin = false,
                            IsAdmin = false
                        };
                        using (HttpResponseMessage userResponse = await fixture.AuthClient.PostAsync("/api/v1/users", JsonHelper.ToJsonContent(userRequest)).ConfigureAwait(false))
                        {
                            AssertEqual(HttpStatusCode.Created, userResponse.StatusCode);
                            userRequest = await JsonHelper.DeserializeAsync<UserMaster>(userResponse).ConfigureAwait(false);
                        }
                        using (HttpResponseMessage credentialResponse = await fixture.AuthClient.PostAsync("/api/v1/credentials", JsonHelper.ToJsonContent(new CredentialCreateRequest { TenantId = tenant!.Id, UserId = userRequest.Id, Name = "endpoint-route-credential" })).ConfigureAwait(false))
                        {
                            AssertEqual(HttpStatusCode.Created, credentialResponse.StatusCode);
                            Credential credential = await JsonHelper.DeserializeAsync<Credential>(credentialResponse).ConfigureAwait(false);
                            userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential.BearerToken);
                        }

                        using (HttpResponseMessage response = await userClient.PostAsync("/api/v1/model-endpoints/health-check", null).ConfigureAwait(false))
                        {
                            AssertEqual(HttpStatusCode.Forbidden, response.StatusCode);
                        }

                        using (HttpResponseMessage ownerCreateResponse = await userClient.PostAsync("/api/v1/model-endpoints", JsonHelper.ToJsonContent(new ModelEndpointCreateRequest
                        {
                            Name = "Private route endpoint",
                            Kind = Armada.Core.Enums.ModelEndpointKindEnum.Inference,
                            Scope = Armada.Core.Enums.ScopeEnum.UserSpecific,
                            Provider = Armada.Core.Enums.ModelProviderEnum.OpenAI,
                            BaseUrl = "http://127.0.0.1:1"
                        })).ConfigureAwait(false))
                        {
                            AssertEqual(HttpStatusCode.Created, ownerCreateResponse.StatusCode);
                            ModelEndpoint ownerEndpoint = await JsonHelper.DeserializeAsync<ModelEndpoint>(ownerCreateResponse).ConfigureAwait(false);
                            ownerEndpointId = ownerEndpoint.Id;
                            AssertEqual("Private route endpoint", ownerEndpoint.Name);
                            AssertFalse(ownerEndpoint.Enabled, "Created endpoints must be disabled by default.");
                            using (HttpResponseMessage ownerUpdateResponse = await userClient.PutAsync("/api/v1/model-endpoints/" + ownerEndpointId, JsonHelper.ToJsonContent(new ModelEndpointCreateRequest
                            {
                                Name = "Private route endpoint renamed",
                                Kind = Armada.Core.Enums.ModelEndpointKindEnum.Inference,
                                Scope = Armada.Core.Enums.ScopeEnum.UserSpecific,
                                Provider = Armada.Core.Enums.ModelProviderEnum.OpenAI,
                                BaseUrl = "http://127.0.0.1:1",
                                Model = "fixture-model"
                            })).ConfigureAwait(false))
                            {
                                AssertEqual(HttpStatusCode.OK, ownerUpdateResponse.StatusCode);
                                ModelEndpoint ownerUpdated = await JsonHelper.DeserializeAsync<ModelEndpoint>(ownerUpdateResponse).ConfigureAwait(false);
                                AssertEqual("Private route endpoint renamed", ownerUpdated.Name, "Same owner can update the private endpoint.");
                            }
                        }

                        UserMaster otherUserRequest = new UserMaster
                        {
                            TenantId = tenant.Id,
                            Email = "endpoint-route-other-" + Guid.NewGuid().ToString("N") + "@example.test",
                            PasswordSha256 = UserMaster.ComputePasswordHash("route-test-password"),
                            IsTenantAdmin = false,
                            IsAdmin = false
                        };
                        using (HttpResponseMessage otherUserResponse = await fixture.AuthClient.PostAsync("/api/v1/users", JsonHelper.ToJsonContent(otherUserRequest)).ConfigureAwait(false))
                        {
                            AssertEqual(HttpStatusCode.Created, otherUserResponse.StatusCode);
                            otherUserRequest = await JsonHelper.DeserializeAsync<UserMaster>(otherUserResponse).ConfigureAwait(false);
                        }
                        using (HttpResponseMessage otherCredentialResponse = await fixture.AuthClient.PostAsync("/api/v1/credentials", JsonHelper.ToJsonContent(new CredentialCreateRequest { TenantId = tenant.Id, UserId = otherUserRequest.Id, Name = "endpoint-route-other-credential" })).ConfigureAwait(false))
                        {
                            AssertEqual(HttpStatusCode.Created, otherCredentialResponse.StatusCode);
                            Credential otherCredential = await JsonHelper.DeserializeAsync<Credential>(otherCredentialResponse).ConfigureAwait(false);
                            using (HttpClient otherUserClient = new HttpClient { BaseAddress = new Uri(fixture.BaseUrl) })
                            {
                                otherUserClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", otherCredential.BearerToken);
                                using (HttpResponseMessage crossOwnerResponse = await otherUserClient.GetAsync("/api/v1/model-endpoints/" + ownerEndpointId).ConfigureAwait(false))
                                {
                                    AssertEqual(HttpStatusCode.NotFound, crossOwnerResponse.StatusCode);
                                }
                            }
                        }
                    }
                    finally
                    {
                        if (ownerEndpointId != null)
                        {
                            using (HttpResponseMessage _ = await fixture.AuthClient.DeleteAsync("/api/v1/model-endpoints/" + ownerEndpointId).ConfigureAwait(false)) { }
                        }
                        if (tenant != null)
                        {
                            using (HttpResponseMessage _ = await fixture.AuthClient.DeleteAsync("/api/v1/tenants/" + tenant.Id).ConfigureAwait(false)) { }
                        }
                    }
                }
            }));

            cases.Add(CaseAsync("create_get_delete_masks_api_key", "ModelEndpointRoutes_MaskApiKeyAcrossCrud", TestTags.Positive, async () =>
            {
                E2EServerFixture fixture = await E2EServerFixture.AcquireAsync(this).ConfigureAwait(false);
                string secret = "route-secret-" + Guid.NewGuid().ToString("N");
                using (StringContent content = JsonHelper.ToJsonContent(new ModelEndpointCreateRequest
                {
                    Name = "Route endpoint",
                    Kind = Armada.Core.Enums.ModelEndpointKindEnum.Inference,
                    Scope = Armada.Core.Enums.ScopeEnum.TenantWide,
                    Provider = Armada.Core.Enums.ModelProviderEnum.OpenAI,
                    BaseUrl = "http://127.0.0.1:1",
                    ApiKey = secret
                }))
                {
                    string? endpointId = null;
                    try
                    {
                        using (HttpResponseMessage createdResponse = await fixture.AuthClient.PostAsync("/api/v1/model-endpoints", content).ConfigureAwait(false))
                        {
                            AssertEqual(HttpStatusCode.Created, createdResponse.StatusCode);
                            string createdJson = await createdResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                            AssertFalse(createdJson.Contains(secret, StringComparison.Ordinal), "Create response must not contain the API key.");
                            ModelEndpoint created = JsonHelper.Deserialize<ModelEndpoint>(createdJson);
                            endpointId = created.Id;
                            AssertStartsWith("mep_", created.Id);
                            AssertEqual("Route endpoint", created.Name);
                            AssertFalse(created.Enabled, "Omitting enabled must create a disabled endpoint.");
                            AssertTrue(created.HasApiKey, "Create response must expose HasApiKey.");

                            using (HttpResponseMessage readResponse = await fixture.AuthClient.GetAsync("/api/v1/model-endpoints/" + created.Id).ConfigureAwait(false))
                            {
                                string readJson = await readResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                                AssertEqual(HttpStatusCode.OK, readResponse.StatusCode);
                                AssertFalse(readJson.Contains(secret, StringComparison.Ordinal), "Get response must not contain the API key.");
                            }
                        }
                    }
                    finally
                    {
                        if (endpointId != null)
                        {
                            using (HttpResponseMessage _ = await fixture.AuthClient.DeleteAsync("/api/v1/model-endpoints/" + endpointId).ConfigureAwait(false)) { }
                        }
                    }
                }
            }));

            return new TestSuiteDescriptor("E2E.ModelEndpoint", "Model Endpoint REST API", cases);
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "E2E.ModelEndpoint",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken _) => body(),
                tags: new List<string> { tag });
        }

        private static void AssertTenantDenied(HttpResponseMessage response)
        {
            if (response.StatusCode != HttpStatusCode.NotFound && response.StatusCode != HttpStatusCode.Forbidden)
                throw new AssertionException("Cross-tenant access must be denied, got " + response.StatusCode);
        }

        private sealed class ModelEndpointCreateRequest
        {
            /// <summary>Endpoint name.</summary>
            public string Name { get; set; } = String.Empty;
            /// <summary>Endpoint kind.</summary>
            public Armada.Core.Enums.ModelEndpointKindEnum Kind { get; set; }
            /// <summary>Endpoint ownership scope.</summary>
            public Armada.Core.Enums.ScopeEnum Scope { get; set; }
            /// <summary>Provider wire format.</summary>
            public Armada.Core.Enums.ModelProviderEnum Provider { get; set; }
            /// <summary>Provider base URL.</summary>
            public string BaseUrl { get; set; } = String.Empty;
            /// <summary>Provider model identifier.</summary>
            public string? Model { get; set; } = null;
            /// <summary>Write-only provider API key.</summary>
            [System.Text.Json.Serialization.JsonPropertyName("apiKey")]
            public string? ApiKey { get; set; } = null;
            /// <summary>Whether the endpoint is enabled for captain admission.</summary>
            public bool? Enabled { get; set; } = null;
        }

        private sealed class TenantCreateRequest
        {
            /// <summary>Tenant name.</summary>
            public string Name { get; set; } = String.Empty;
        }

        private sealed class CredentialCreateRequest
        {
            /// <summary>Tenant that receives the credential.</summary>
            public string TenantId { get; set; } = String.Empty;
            /// <summary>User that receives the credential.</summary>
            public string UserId { get; set; } = String.Empty;
            /// <summary>Credential display name.</summary>
            public string Name { get; set; } = String.Empty;
        }

        private static HttpClient CreateBearerClient(E2EServerFixture fixture, string bearerToken)
        {
            HttpClient client = new HttpClient { BaseAddress = new Uri(fixture.BaseUrl) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            return client;
        }

        private static async Task<(TenantMetadata Tenant, UserMaster User, Credential Credential)> CreateRoutePrincipalAsync(E2EServerFixture fixture, string name)
        {
            using (HttpResponseMessage tenantResponse = await fixture.AuthClient.PostAsync("/api/v1/tenants", JsonHelper.ToJsonContent(new TenantCreateRequest { Name = name + "-" + Guid.NewGuid().ToString("N") })).ConfigureAwait(false))
            {
                AssertEqual(HttpStatusCode.Created, tenantResponse.StatusCode);
                TenantMetadata tenant = await JsonHelper.DeserializeAsync<TenantMetadata>(tenantResponse).ConfigureAwait(false);
                UserMaster user = new UserMaster
                {
                    TenantId = tenant.Id,
                    Email = name + "-" + Guid.NewGuid().ToString("N") + "@example.test",
                    PasswordSha256 = UserMaster.ComputePasswordHash("route-test-password"),
                    IsTenantAdmin = true,
                    IsAdmin = false
                };
                using (HttpResponseMessage userResponse = await fixture.AuthClient.PostAsync("/api/v1/users", JsonHelper.ToJsonContent(user)).ConfigureAwait(false))
                {
                    AssertEqual(HttpStatusCode.Created, userResponse.StatusCode);
                    user = await JsonHelper.DeserializeAsync<UserMaster>(userResponse).ConfigureAwait(false);
                }
                Credential credential = await CreateCredentialAsync(fixture, tenant.Id, user.Id, name + "-credential").ConfigureAwait(false);
                return (tenant, user, credential);
            }
        }

        private static async Task<Credential> CreateCredentialAsync(E2EServerFixture fixture, string tenantId, string userId, string name)
        {
            using (HttpResponseMessage response = await fixture.AuthClient.PostAsync("/api/v1/credentials", JsonHelper.ToJsonContent(new CredentialCreateRequest
            {
                TenantId = tenantId,
                UserId = userId,
                Name = name
            })).ConfigureAwait(false))
            {
                AssertEqual(HttpStatusCode.Created, response.StatusCode);
                return await JsonHelper.DeserializeAsync<Credential>(response).ConfigureAwait(false);
            }
        }
    }
}
