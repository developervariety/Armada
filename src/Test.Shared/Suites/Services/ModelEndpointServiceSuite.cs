namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="ModelEndpointService"/>: CRUD over a live SQLite store, provider/kind
    /// capability guards, API-key preservation on update, and independent base-URL health probes. Positive
    /// cases assert creation, scoped enumeration, key preservation, and URL normalization; negative cases
    /// assert rejection of the unsupported Anthropic-embedding and Voyage-inference combinations, the
    /// missing-base-URL path, and the audited null-id / not-found paths.
    /// </summary>
    public sealed class ModelEndpointServiceSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the ModelEndpointService suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("create_creates_embedding_endpoint", "CreateAsync creates an embedding endpoint", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ModelEndpointService service = new ModelEndpointService(testDb.Driver, CreateLogging());
                AuthContext auth = AuthContext.Authenticated("ten_mep", "usr_mep", false, true, "UnitTest");

                ModelEndpoint endpoint = new ModelEndpoint
                {
                    Name = "OpenAI Embeddings",
                    Kind = ModelEndpointKindEnum.Embedding,
                    Provider = ModelProviderEnum.OpenAI,
                    BaseUrl = "https://api.openai.com",
                    Model = "text-embedding-3-small"
                };
                endpoint.ApiKey = "sk-secret-value";

                ModelEndpoint created = await service.CreateAsync(auth, endpoint).ConfigureAwait(false);

                AssertStartsWith("mep_", created.Id);
                AssertEqual(ModelEndpointKindEnum.Embedding, created.Kind);
                AssertEqual(ModelProviderEnum.OpenAI, created.Provider);
                AssertEqual("ten_mep", created.TenantId);
                AssertTrue(created.HasApiKey, "Expected HasApiKey to be true after creating with a key.");
                AssertEqual(EndpointHealthStatusEnum.Unknown, created.HealthStatus);
            }));

            cases.Add(CaseAsync("enumerate_returns_tenant_scoped_endpoints", "EnumerateAsync returns tenant-scoped endpoints", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ModelEndpointService service = new ModelEndpointService(testDb.Driver, CreateLogging());
                AuthContext auth = AuthContext.Authenticated("ten_mep_enum", "usr_mep_enum", false, true, "UnitTest");

                await service.CreateAsync(auth, NewInference("Gpt", "https://api.openai.com")).ConfigureAwait(false);
                await service.CreateAsync(auth, NewInference("Local", "http://localhost:11434")).ConfigureAwait(false);

                List<ModelEndpoint> endpoints = await service.EnumerateAsync(auth).ConfigureAwait(false);
                AssertEqual(2, endpoints.Count);
            }));

            cases.Add(CaseAsync("update_preserves_api_key_when_not_supplied", "UpdateAsync preserves the API key when none supplied", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ModelEndpointService service = new ModelEndpointService(testDb.Driver, CreateLogging());
                AuthContext auth = AuthContext.Authenticated("ten_mep_key", "usr_mep_key", false, true, "UnitTest");

                ModelEndpoint endpoint = NewInference("Keyed", "https://api.openai.com");
                endpoint.ApiKey = "sk-original";
                ModelEndpoint created = await service.CreateAsync(auth, endpoint).ConfigureAwait(false);

                // Build an update object that does NOT supply an API key (ApiKeySpecified stays false).
                ModelEndpoint edit = new ModelEndpoint
                {
                    Id = created.Id,
                    Name = "Renamed",
                    Kind = created.Kind,
                    Provider = created.Provider,
                    BaseUrl = created.BaseUrl,
                    Model = created.Model
                };
                await service.UpdateAsync(auth, edit).ConfigureAwait(false);

                ModelEndpoint? reloaded = await testDb.Driver.ModelEndpoints.ReadAsync(created.Id).ConfigureAwait(false);
                AssertNotNull(reloaded, "Expected endpoint to reload.");
                AssertEqual("Renamed", reloaded!.Name);
                AssertEqual("sk-original", reloaded.ApiKey);
            }));

            cases.Add(CaseAsync("update_replaces_api_key_when_supplied", "UpdateAsync replaces the API key when supplied", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ModelEndpointService service = new ModelEndpointService(testDb.Driver, CreateLogging());
                AuthContext auth = AuthContext.Authenticated("ten_mep_key2", "usr_mep_key2", false, true, "UnitTest");

                ModelEndpoint endpoint = NewInference("Keyed", "https://api.openai.com");
                endpoint.ApiKey = "sk-original";
                ModelEndpoint created = await service.CreateAsync(auth, endpoint).ConfigureAwait(false);

                ModelEndpoint edit = new ModelEndpoint
                {
                    Id = created.Id,
                    Name = created.Name,
                    Kind = created.Kind,
                    Provider = created.Provider,
                    BaseUrl = created.BaseUrl
                };
                edit.ApiKeyInput = "sk-rotated";
                await service.UpdateAsync(auth, edit).ConfigureAwait(false);

                ModelEndpoint? reloaded = await testDb.Driver.ModelEndpoints.ReadAsync(created.Id).ConfigureAwait(false);
                AssertNotNull(reloaded, "Expected endpoint to reload.");
                AssertEqual("sk-rotated", reloaded!.ApiKey);
            }));

            cases.Add(CaseAsync("api_key_is_masked_and_explicit_clear_works", "API keys are masked and can be explicitly cleared", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ModelEndpointService service = new ModelEndpointService(testDb.Driver, CreateLogging());
                AuthContext auth = AuthContext.Authenticated("ten_mep_secret", "usr_mep_secret", false, true, "UnitTest");
                ModelEndpoint endpoint = NewInference("Secret", "http://127.0.0.1:1");
                endpoint.ApiKey = "secret-value";
                ModelEndpoint created = await service.CreateAsync(auth, endpoint).ConfigureAwait(false);
                string json = System.Text.Json.JsonSerializer.Serialize(created);
                AssertFalse(json.Contains("secret-value", StringComparison.Ordinal), "Serialized endpoint must not contain the API key.");
                AssertTrue(json.Contains("HasApiKey", StringComparison.Ordinal), "Serialized endpoint must expose only key presence.");

                ModelEndpoint clear = NewInference("Secret", created.BaseUrl);
                clear.Id = created.Id;
                clear.ApiKeyInput = null;
                await service.UpdateAsync(auth, clear).ConfigureAwait(false);
                ModelEndpoint? reloaded = await testDb.Driver.ModelEndpoints.ReadAsync(created.Id).ConfigureAwait(false);
                AssertNotNull(reloaded, "Expected endpoint to reload after explicit clear.");
                AssertNull(reloaded!.ApiKey, "Explicit null input must clear the stored key.");
                AssertFalse(reloaded.HasApiKey, "Explicit null input must clear key presence.");
            }));

            cases.Add(CaseAsync("health_probes_each_endpoint_with_own_credentials", "Health probes are per endpoint", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                List<string> seen = new List<string>();
                Func<ModelEndpoint, CancellationToken, Task<ModelEndpointProbeResult>> probe = (endpoint, token) =>
                {
                    seen.Add(endpoint.Id + ":" + endpoint.ApiKey);
                    return Task.FromResult(new ModelEndpointProbeResult { Success = true, BaseUrl = endpoint.BaseUrl });
                };
                ModelEndpointService service = new ModelEndpointService(testDb.Driver, CreateLogging(), probe);
                AuthContext auth = AuthContext.Authenticated("ten_mep_health", "usr_mep_health", false, true, "UnitTest");
                ModelEndpoint first = NewInference("First", "http://same.example");
                first.ApiKey = "first-key";
                first.Enabled = true;
                ModelEndpoint second = NewInference("Second", "http://same.example");
                second.ApiKey = "second-key";
                second.Enabled = true;
                await service.CreateAsync(auth, first).ConfigureAwait(false);
                await service.CreateAsync(auth, second).ConfigureAwait(false);
                int count = await service.CheckHealthAllAsync().ConfigureAwait(false);
                AssertEqual(2, count);
                AssertEqual(2, seen.Count);
                AssertTrue(seen.Any(value => value.EndsWith(":first-key", StringComparison.Ordinal)), "First credentials must be used for first probe.");
                AssertTrue(seen.Any(value => value.EndsWith(":second-key", StringComparison.Ordinal)), "Second credentials must be used for second probe.");
            }));

            cases.Add(CaseAsync("non_admin_health_sweep_is_rejected", "Non-admin health sweeps are rejected", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                int probes = 0;
                ModelEndpointService service = new ModelEndpointService(
                    testDb.Driver,
                    CreateLogging(),
                    probe: (endpoint, token) =>
                    {
                        probes++;
                        return Task.FromResult(new ModelEndpointProbeResult { Success = true });
                    });
                AuthContext tenantAdmin = AuthContext.Authenticated("ten_mep_sweep", "usr_mep_sweep", false, true, "UnitTest");
                ModelEndpoint endpoint = NewInference("Sweep", "http://127.0.0.1:1");
                endpoint.Enabled = true;
                await service.CreateAsync(tenantAdmin, endpoint).ConfigureAwait(false);
                await AssertThrowsAsync<UnauthorizedAccessException>(() => service.CheckHealthAllAsync(tenantAdmin));
                AssertEqual(0, probes);
            }));

            cases.Add(CaseAsync("health_write_preserves_concurrent_configuration", "Health writes preserve concurrent configuration", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                bool changed = false;
                Func<ModelEndpoint, CancellationToken, Task<ModelEndpointProbeResult>> probe = async (endpoint, token) =>
                {
                    if (!changed)
                    {
                        ModelEndpoint concurrent = (await testDb.Driver.ModelEndpoints.ReadAsync(endpoint.Id, token).ConfigureAwait(false))!;
                        concurrent.Name = "Concurrent edit";
                        await testDb.Driver.ModelEndpoints.UpdateAsync(concurrent, token).ConfigureAwait(false);
                        changed = true;
                    }
                    return new ModelEndpointProbeResult { Success = true, BaseUrl = endpoint.BaseUrl };
                };
                ModelEndpointService service = new ModelEndpointService(testDb.Driver, CreateLogging(), probe);
                AuthContext auth = AuthContext.Authenticated("ten_mep_race", "usr_mep_race", false, true, "UnitTest");
                ModelEndpoint endpoint = NewInference("Original", "http://127.0.0.1:1");
                endpoint.Enabled = true;
                ModelEndpoint created = await service.CreateAsync(auth, endpoint).ConfigureAwait(false);
                await service.CheckHealthAllAsync().ConfigureAwait(false);
                ModelEndpoint? persisted = await testDb.Driver.ModelEndpoints.ReadAsync(created.Id).ConfigureAwait(false);
                AssertNotNull(persisted, "Expected endpoint after health update.");
                AssertEqual("Concurrent edit", persisted!.Name, "Health update must preserve a concurrent configuration edit.");
                AssertEqual(EndpointHealthStatusEnum.Unknown, persisted.HealthStatus, "A probe for the old configuration must be discarded.");
                AssertNull(persisted.LastHealthCheckUtc, "A stale probe must not stamp the new configuration.");
            }));

            cases.Add(CaseAsync("health_cancellation_is_propagated", "Health cancellation is propagated", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ModelEndpointService service = new ModelEndpointService(
                    testDb.Driver,
                    CreateLogging(),
                    probe: (endpoint, token) => throw new OperationCanceledException(token));
                AuthContext auth = AuthContext.Authenticated("ten_mep_cancel", "usr_mep_cancel", false, true, "UnitTest");
                ModelEndpoint endpoint = NewInference("Cancel", "http://127.0.0.1:1");
                endpoint.Enabled = true;
                await service.CreateAsync(auth, endpoint).ConfigureAwait(false);
                using CancellationTokenSource cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                await AssertThrowsAsync<OperationCanceledException>(() => service.CheckHealthAllAsync(cancellation.Token));
            }));

            cases.Add(CaseAsync("older_health_result_cannot_overwrite_newer", "Older health result cannot overwrite newer result", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DateTime newer = DateTime.UtcNow;
                int call = 0;
                ModelEndpointService service = new ModelEndpointService(
                    testDb.Driver,
                    CreateLogging(),
                    probe: (endpoint, token) =>
                    {
                        call++;
                        return Task.FromResult(new ModelEndpointProbeResult
                        {
                            Success = call == 1,
                            TimestampUtc = call == 1 ? newer : newer.AddMinutes(-1)
                        });
                    });
                AuthContext auth = AuthContext.Authenticated("ten_mep_order", "usr_mep_order", false, true, "UnitTest");
                ModelEndpoint endpoint = await service.CreateAsync(auth, NewInference("Order", "http://127.0.0.1:1")).ConfigureAwait(false);
                await service.ValidateAsync(auth, endpoint.Id).ConfigureAwait(false);
                await service.ValidateAsync(auth, endpoint.Id).ConfigureAwait(false);
                ModelEndpoint persisted = (await testDb.Driver.ModelEndpoints.ReadAsync(endpoint.Id).ConfigureAwait(false))!;
                AssertEqual(EndpointHealthStatusEnum.Healthy, persisted.HealthStatus, "The older failure must be discarded.");
                AssertEqual(newer, persisted.LastHealthCheckUtc!.Value, "The newer timestamp must remain recorded.");
            }));

            cases.Add(CaseAsync("local_probe_uses_provider_header_without_redirect", "Local probes use one provider header and no redirect", TestTags.Positive, async () =>
            {
                TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Task<string> requestTask = Task.Run(async () =>
                {
                    using TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    using NetworkStream stream = client.GetStream();
                    byte[] buffer = new byte[4096];
                    int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length)).ConfigureAwait(false);
                    string request = Encoding.ASCII.GetString(buffer, 0, read);
                    byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:" + port + "/redirected\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(response).ConfigureAwait(false);
                    return request;
                });
                try
                {
                    using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                    ModelEndpointService service = new ModelEndpointService(testDb.Driver, CreateLogging());
                    AuthContext auth = AuthContext.Authenticated("ten_mep_http", "usr_mep_http", false, true, "UnitTest");
                    ModelEndpoint endpoint = NewInference("Local", "http://127.0.0.1:" + port);
                    endpoint.Provider = ModelProviderEnum.Anthropic;
                    endpoint.ApiKey = "local-secret";
                    ModelEndpoint created = await service.CreateAsync(auth, endpoint).ConfigureAwait(false);
                    ModelEndpointProbeResult result = await service.ValidateAsync(auth, created.Id).ConfigureAwait(false);
                    string request = await requestTask.ConfigureAwait(false);
                    await Task.Delay(100).ConfigureAwait(false);
                    AssertFalse(result.Success, "A redirect response must not be treated as a successful provider probe.");
                    AssertFalse(listener.Pending(), "Probe must not follow redirects with credentials.");
                    AssertTrue(request.Contains("x-api-key: local-secret", StringComparison.OrdinalIgnoreCase), "Anthropic must use x-api-key.");
                    AssertFalse(request.Contains("Authorization:", StringComparison.OrdinalIgnoreCase), "Anthropic must not receive a Bearer header.");
                }
                finally
                {
                    listener.Stop();
                }
            }));

            cases.Add(CaseAsync("model_validation_uses_provider_wire_contracts", "Model validation uses provider wire contracts", TestTags.Positive, async () =>
            {
                ValidationFixture[] fixtures = new[]
                {
                    new ValidationFixture(ModelProviderEnum.OpenAI, ModelEndpointKindEnum.Embedding, "/v1/embeddings", "Authorization: Bearer fixture-key"),
                    new ValidationFixture(ModelProviderEnum.OpenAI, ModelEndpointKindEnum.Inference, "/v1/chat/completions", "Authorization: Bearer fixture-key"),
                    new ValidationFixture(ModelProviderEnum.OpenAICompatible, ModelEndpointKindEnum.Embedding, "/v1/embeddings", "Authorization: Bearer fixture-key"),
                    new ValidationFixture(ModelProviderEnum.OpenAICompatible, ModelEndpointKindEnum.Inference, "/v1/chat/completions", "Authorization: Bearer fixture-key"),
                    new ValidationFixture(ModelProviderEnum.Anthropic, ModelEndpointKindEnum.Inference, "/v1/messages", "x-api-key: fixture-key"),
                    new ValidationFixture(ModelProviderEnum.Gemini, ModelEndpointKindEnum.Embedding, "/v1beta/models/fixture-model:embedContent", "x-goog-api-key: fixture-key"),
                    new ValidationFixture(ModelProviderEnum.Gemini, ModelEndpointKindEnum.Inference, "/v1beta/models/fixture-model:generateContent", "x-goog-api-key: fixture-key"),
                    new ValidationFixture(ModelProviderEnum.VoyageAI, ModelEndpointKindEnum.Embedding, "/v1/embeddings", "Authorization: Bearer fixture-key"),
                    new ValidationFixture(ModelProviderEnum.Ollama, ModelEndpointKindEnum.Embedding, "/api/embeddings", null),
                    new ValidationFixture(ModelProviderEnum.Ollama, ModelEndpointKindEnum.Inference, "/api/chat", null)
                };
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                AuthContext auth = AuthContext.Authenticated("ten_mep_wire", "usr_mep_wire", false, true, "UnitTest");
                foreach (ValidationFixture fixture in fixtures)
                {
                    TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                    listener.Start();
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    Task<CapturedHttpRequest> requestTask = CaptureAndRespondAsync(listener, fixture.Kind == ModelEndpointKindEnum.Embedding
                        ? fixture.Provider == ModelProviderEnum.Gemini
                            ? "{\"embedding\":{\"values\":[0.1,0.2]}}"
                            : fixture.Provider == ModelProviderEnum.Ollama
                                ? "{\"embedding\":[0.1,0.2]}"
                            : "{\"data\":[{\"embedding\":[0.1,0.2]}]}"
                        : fixture.Provider == ModelProviderEnum.Anthropic
                            ? "{\"content\":[{\"type\":\"text\",\"text\":\"pong\"}]}"
                            : fixture.Provider == ModelProviderEnum.Gemini
                                ? "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"pong\"}]}}]}"
                                : fixture.Provider == ModelProviderEnum.Ollama
                                    ? "{\"message\":{\"content\":\"pong\"}}"
                                    : "{\"choices\":[{\"message\":{\"content\":\"pong\"}}]}");
                    try
                    {
                        ModelEndpoint endpoint = new ModelEndpoint
                        {
                            Name = fixture.Provider + " " + fixture.Kind,
                            Provider = fixture.Provider,
                            Kind = fixture.Kind,
                            BaseUrl = "http://127.0.0.1:" + port,
                            Model = "fixture-model"
                        };
                        endpoint.ApiKey = "fixture-key";
                        ModelEndpoint created = await new ModelEndpointService(testDb.Driver, CreateLogging()).CreateAsync(auth, endpoint).ConfigureAwait(false);
                        ModelEndpointProbeResult result = await new ModelEndpointService(testDb.Driver, CreateLogging()).ValidateAsync(auth, created.Id).ConfigureAwait(false);
                        CapturedHttpRequest request = await requestTask.ConfigureAwait(false);
                        AssertTrue(result.Success, fixture.Provider + " validation must accept the fixture response.");
                        AssertEqual(fixture.Path, request.Target, fixture.Provider + " request path");
                        if (fixture.Provider == ModelProviderEnum.Gemini)
                        {
                            AssertContains("fixture-model", request.Target, "Gemini request model");
                            if (fixture.Kind == ModelEndpointKindEnum.Embedding)
                            {
                                AssertContains("\"model\":\"models/fixture-model\"", request.Body, "Gemini embedding model");
                                AssertContains("\"content\"", request.Body, "Gemini embedding content");
                            }
                        }
                        else
                            AssertContains("\"model\":\"fixture-model\"", request.Body, fixture.Provider + " request model");
                        if (fixture.Provider == ModelProviderEnum.Ollama && fixture.Kind == ModelEndpointKindEnum.Embedding)
                            AssertContains("\"prompt\":\"Armada model endpoint validation\"", request.Body, "Ollama embedding prompt");
                        if (fixture.Provider == ModelProviderEnum.Ollama && fixture.Kind == ModelEndpointKindEnum.Inference)
                        {
                            AssertContains("\"messages\"", request.Body, "Ollama chat messages");
                            AssertContains("\"stream\":false", request.Body, "Ollama non-streaming chat request");
                        }
                        if ((fixture.Provider == ModelProviderEnum.OpenAI || fixture.Provider == ModelProviderEnum.OpenAICompatible)
                            && fixture.Kind == ModelEndpointKindEnum.Embedding)
                            AssertContains("\"input\"", request.Body, fixture.Provider + " embedding input");
                        if (fixture.Provider == ModelProviderEnum.VoyageAI)
                            AssertContains("\"input\"", request.Body, "Voyage embedding input");
                        if (fixture.Provider == ModelProviderEnum.Anthropic)
                            AssertContains("\"messages\"", request.Body, "Anthropic messages");
                        if (fixture.Header == null)
                            AssertFalse(request.Headers.Contains("authorization:", StringComparison.OrdinalIgnoreCase), "Ollama must not receive an Authorization header.");
                        else
                            AssertContains(fixture.Header, request.Headers, fixture.Provider + " authentication header");
                        if (fixture.Provider == ModelProviderEnum.Anthropic)
                            AssertContains("anthropic-version: 2023-06-01", request.Headers, "Anthropic API version header");
                    }
                    finally
                    {
                        listener.Stop();
                    }
                }
            }));

            cases.Add(CaseAsync("model_validation_rejects_missing_or_invalid_response", "Model validation rejects missing model and invalid response", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ModelEndpointService service = new ModelEndpointService(testDb.Driver, CreateLogging());
                AuthContext auth = AuthContext.Authenticated("ten_mep_invalid", "usr_mep_invalid", false, true, "UnitTest");
                ModelEndpoint missing = NewInference("Missing model", "http://127.0.0.1:1");
                missing.Model = null;
                ModelEndpoint missingCreated = await service.CreateAsync(auth, missing).ConfigureAwait(false);
                ModelEndpointProbeResult missingResult = await service.ValidateAsync(auth, missingCreated.Id).ConfigureAwait(false);
                AssertFalse(missingResult.Success, "Missing model must fail validation.");

                TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Task<CapturedHttpRequest> requestTask = CaptureAndRespondAsync(listener, "{}");
                try
                {
                    ModelEndpoint invalid = NewInference("Invalid response", "http://127.0.0.1:" + port);
                    ModelEndpoint created = await service.CreateAsync(auth, invalid).ConfigureAwait(false);
                    ModelEndpointProbeResult invalidResult = await service.ValidateAsync(auth, created.Id).ConfigureAwait(false);
                    await requestTask.ConfigureAwait(false);
                    AssertFalse(invalidResult.Success, "A successful HTTP response without the provider result must fail validation.");
                    AssertContains("completion text", invalidResult.Error ?? String.Empty, "Invalid response error");
                }
                finally
                {
                    listener.Stop();
                }
            }));

            cases.Add(CaseAsync("model_validation_bounds_oversized_response_body", "Model validation rejects an oversized provider response", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AuthContext auth = AuthContext.Authenticated("ten_mep_oversize", "usr_mep_oversize", false, true, "UnitTest");
                    TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                    listener.Start();
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    string oversized = new String('x', 65537);
                    Task<CapturedHttpRequest> requestTask = CaptureAndRespondAsync(listener, oversized);
                    try
                    {
                        ModelEndpoint endpoint = NewInference("Oversized response", "http://127.0.0.1:" + port);
                        ModelEndpointService service = new ModelEndpointService(testDb.Driver, CreateLogging());
                        ModelEndpoint created = await service.CreateAsync(auth, endpoint).ConfigureAwait(false);
                        ModelEndpointProbeResult result = await service.ValidateAsync(auth, created.Id).ConfigureAwait(false);
                        await requestTask.ConfigureAwait(false);
                        AssertFalse(result.Success, "A response over the validation limit must fail.");
                        AssertContains("too large", result.Error ?? String.Empty, "Oversized response error");
                    }
                    finally
                    {
                        listener.Stop();
                    }
                }
            }));

            cases.Add(CaseAsync("model_validation_times_out_stalled_response_body", "Model validation times out while reading a stalled response body", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AuthContext auth = AuthContext.Authenticated("ten_mep_stalled", "usr_mep_stalled", false, true, "UnitTest");
                    TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                    listener.Start();
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    Task server = Task.Run(async () =>
                    {
                        using (TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false))
                        using (NetworkStream stream = client.GetStream())
                        {
                            byte[] headers = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 70000\r\nConnection: close\r\n\r\n{");
                            await stream.WriteAsync(headers).ConfigureAwait(false);
                            await stream.FlushAsync().ConfigureAwait(false);
                            await Task.Delay(2000).ConfigureAwait(false);
                        }
                    });
                    try
                    {
                        ModelEndpoint endpoint = NewInference("Stalled response", "http://127.0.0.1:" + port);
                        endpoint.TimeoutMs = 1000;
                        ModelEndpointService service = new ModelEndpointService(testDb.Driver, CreateLogging());
                        ModelEndpoint created = await service.CreateAsync(auth, endpoint).ConfigureAwait(false);
                        ModelEndpointProbeResult result = await service.ValidateAsync(auth, created.Id).ConfigureAwait(false);
                        AssertFalse(result.Success, "A stalled response body must fail at the endpoint timeout.");
                    }
                    finally
                    {
                        listener.Stop();
                        await server.ConfigureAwait(false);
                    }
                }
            }));

            cases.Add(CaseAsync("model_validation_timeout_and_cancellation_are_bounded", "Model validation honors timeout and cancellation", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                AuthContext auth = AuthContext.Authenticated("ten_mep_cancel", "usr_mep_cancel", false, true, "UnitTest");

                TcpListener timeoutListener = new TcpListener(IPAddress.Loopback, 0);
                timeoutListener.Start();
                int timeoutPort = ((IPEndPoint)timeoutListener.LocalEndpoint).Port;
                Task timeoutTask = Task.Run(async () =>
                {
                    using TcpClient client = await timeoutListener.AcceptTcpClientAsync().ConfigureAwait(false);
                    await Task.Delay(1500).ConfigureAwait(false);
                });
                try
                {
                    ModelEndpoint timeoutEndpoint = NewInference("Timeout", "http://127.0.0.1:" + timeoutPort);
                    timeoutEndpoint.TimeoutMs = 1000;
                    ModelEndpointService service = new ModelEndpointService(testDb.Driver, CreateLogging());
                    ModelEndpoint created = await service.CreateAsync(auth, timeoutEndpoint).ConfigureAwait(false);
                    ModelEndpointProbeResult result = await service.ValidateAsync(auth, created.Id).ConfigureAwait(false);
                    AssertFalse(result.Success, "A delayed provider must fail at the endpoint timeout.");
                }
                finally
                {
                    timeoutListener.Stop();
                    await timeoutTask.ConfigureAwait(false);
                }

                TcpListener cancelListener = new TcpListener(IPAddress.Loopback, 0);
                cancelListener.Start();
                int cancelPort = ((IPEndPoint)cancelListener.LocalEndpoint).Port;
                Task cancelTask = Task.Run(async () =>
                {
                    using TcpClient client = await cancelListener.AcceptTcpClientAsync().ConfigureAwait(false);
                    await Task.Delay(1500).ConfigureAwait(false);
                });
                try
                {
                    ModelEndpoint endpoint = NewInference("Cancel", "http://127.0.0.1:" + cancelPort);
                    using CancellationTokenSource cancellation = new CancellationTokenSource();
                    ModelEndpointService service = new ModelEndpointService(testDb.Driver, CreateLogging());
                    ModelEndpoint created = await service.CreateAsync(auth, endpoint).ConfigureAwait(false);
                    Task validation = service.ValidateAsync(auth, created.Id, cancellation.Token);
                    await Task.Delay(100).ConfigureAwait(false);
                    cancellation.Cancel();
                    await AssertThrowsAsync<OperationCanceledException>(() => validation, "Caller cancellation must propagate.");
                }
                finally
                {
                    cancelListener.Stop();
                    await cancelTask.ConfigureAwait(false);
                }
            }));

            cases.Add(CaseAsync("invalid_endpoint_urls_are_rejected", "Invalid endpoint URLs are rejected", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ModelEndpointService service = new ModelEndpointService(testDb.Driver, CreateLogging());
                AuthContext auth = AuthContext.Authenticated("ten_mep_url", "usr_mep_url", false, true, "UnitTest");
                await AssertThrowsAsync<ArgumentException>(() => service.CreateAsync(auth, NewInference("Userinfo", "https://user:pass@example.test")));
                await AssertThrowsAsync<ArgumentException>(() => service.CreateAsync(auth, NewInference("Relative", "/v1")));
            }));

            cases.Add(CaseAsync("delete_removes_endpoint", "DeleteAsync removes the endpoint", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ModelEndpointService service = new ModelEndpointService(testDb.Driver, CreateLogging());
                AuthContext auth = AuthContext.Authenticated("ten_mep_del", "usr_mep_del", false, true, "UnitTest");

                ModelEndpoint created = await service.CreateAsync(auth, NewInference("Doomed", "https://api.openai.com")).ConfigureAwait(false);
                await service.DeleteAsync(auth, created.Id).ConfigureAwait(false);

                ModelEndpoint? reloaded = await testDb.Driver.ModelEndpoints.ReadAsync(created.Id).ConfigureAwait(false);
                AssertNull(reloaded, "Expected endpoint to be deleted.");
            }));

            cases.Add(CaseAsync("delete_rejects_endpoint_in_use", "DeleteAsync rejects an endpoint in use", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ModelEndpointService service = new ModelEndpointService(
                    testDb.Driver,
                    CreateLogging(),
                    isInUse: (id, token) => Task.FromResult(true));
                AuthContext auth = AuthContext.Authenticated("ten_mep_inuse", "usr_mep_inuse", false, true, "UnitTest");
                ModelEndpoint created = await service.CreateAsync(auth, NewInference("In use", "http://127.0.0.1:1")).ConfigureAwait(false);
                await AssertThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(auth, created.Id));
                AssertNotNull(await testDb.Driver.ModelEndpoints.ReadAsync(created.Id).ConfigureAwait(false), "In-use endpoint must remain stored.");
            }));

            cases.Add(CaseAsync("delete_db_guard_rejects_link_created_after_advisory_check", "DeleteAsync keeps an endpoint when a captain link races the advisory check", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                AuthContext auth = AuthContext.Authenticated("ten_mep_race", "usr_mep_race", false, true, "UnitTest");
                ModelEndpointService? service = null;
                TaskCompletionSource<bool> checkedUse = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<bool> releaseDelete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                service = new ModelEndpointService(
                    testDb.Driver,
                    CreateLogging(),
                    isInUse: async (id, token) =>
                    {
                        checkedUse.TrySetResult(true);
                        await releaseDelete.Task.WaitAsync(token).ConfigureAwait(false);
                        return false;
                    });

                ModelEndpoint created = await service.CreateAsync(auth, NewInference("Racing link", "http://127.0.0.1:1")).ConfigureAwait(false);
                Task delete = service.DeleteAsync(auth, created.Id);
                await checkedUse.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                Captain linked = new Captain("Racing captain", AgentRuntimeEnum.ApiEndpoint)
                {
                    ModelEndpointId = created.Id,
                    Model = created.Model
                };
                await testDb.Driver.Captains.CreateAsync(linked).ConfigureAwait(false);
                releaseDelete.TrySetResult(true);

                await AssertThrowsAsync<InvalidOperationException>(() => delete, "The database link must reject the racing delete.");
                AssertNotNull(await testDb.Driver.ModelEndpoints.ReadAsync(created.Id).ConfigureAwait(false), "A linked endpoint must remain stored.");
            }));

            cases.Add(CaseAsync("normalize_base_url_dedups_equivalent_urls", "NormalizeBaseUrl treats equivalent URLs as one", TestTags.Positive, () =>
            {
                string a = ModelEndpointService.NormalizeBaseUrl("https://Host.Example.com:443/");
                string b = ModelEndpointService.NormalizeBaseUrl("https://host.example.com");
                AssertEqual(a, b);

                string c = ModelEndpointService.NormalizeBaseUrl("http://localhost:11434");
                AssertNotEqual(a, c);
                return Task.CompletedTask;
            }));

            // Negative: Anthropic cannot serve embeddings.

            cases.Add(CaseAsync("create_rejects_anthropic_embedding", "CreateAsync rejects Anthropic embedding", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ModelEndpointService service = new ModelEndpointService(testDb.Driver, CreateLogging());
                AuthContext auth = AuthContext.Authenticated("ten_mep_neg", "usr_mep_neg", false, true, "UnitTest");

                ModelEndpoint endpoint = new ModelEndpoint
                {
                    Name = "Bad",
                    Kind = ModelEndpointKindEnum.Embedding,
                    Provider = ModelProviderEnum.Anthropic,
                    BaseUrl = "https://api.anthropic.com"
                };
                await AssertThrowsAsync<ArgumentException>(() => service.CreateAsync(auth, endpoint));
            }));

            // Negative: Voyage AI cannot serve inference.

            cases.Add(CaseAsync("create_rejects_voyageai_inference", "CreateAsync rejects Voyage AI inference", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ModelEndpointService service = new ModelEndpointService(testDb.Driver, CreateLogging());
                AuthContext auth = AuthContext.Authenticated("ten_mep_neg2", "usr_mep_neg2", false, true, "UnitTest");

                ModelEndpoint endpoint = new ModelEndpoint
                {
                    Name = "Bad",
                    Kind = ModelEndpointKindEnum.Inference,
                    Provider = ModelProviderEnum.VoyageAI,
                    BaseUrl = "https://api.voyageai.com"
                };
                await AssertThrowsAsync<ArgumentException>(() => service.CreateAsync(auth, endpoint));
            }));

            // Negative: base URL is required.

            cases.Add(CaseAsync("create_rejects_missing_base_url", "CreateAsync rejects a missing base URL", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ModelEndpointService service = new ModelEndpointService(testDb.Driver, CreateLogging());
                AuthContext auth = AuthContext.Authenticated("ten_mep_neg3", "usr_mep_neg3", false, true, "UnitTest");

                ModelEndpoint endpoint = new ModelEndpoint
                {
                    Name = "No URL",
                    Kind = ModelEndpointKindEnum.Inference,
                    Provider = ModelProviderEnum.OpenAI,
                    BaseUrl = ""
                };
                await AssertThrowsAsync<ArgumentException>(() => service.CreateAsync(auth, endpoint));
            }));

            // Negative: reading with a null id throws.

            cases.Add(CaseAsync("read_null_id_throws", "ReadAsync NullId Throws", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ModelEndpointService service = new ModelEndpointService(testDb.Driver, CreateLogging());
                AuthContext auth = AuthContext.Authenticated("ten_mep_null", "usr_mep_null", false, true, "UnitTest");
                await AssertThrowsAsync<ArgumentNullException>(() => service.ReadAsync(auth, null!));
            }));

            // Negative: updating an unknown endpoint throws not-found.

            cases.Add(CaseAsync("update_unknown_id_throws", "UpdateAsync UnknownId Throws", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ModelEndpointService service = new ModelEndpointService(testDb.Driver, CreateLogging());
                AuthContext auth = AuthContext.Authenticated("ten_mep_missing", "usr_mep_missing", false, true, "UnitTest");

                ModelEndpoint edit = NewInference("Ghost", "https://api.openai.com");
                edit.Id = "mep_does_not_exist";
                await AssertThrowsAsync<KeyNotFoundException>(() => service.UpdateAsync(auth, edit));
            }));

            // Negative: validating an unreachable endpoint yields an Unhealthy, persisted result (no throw).

            cases.Add(CaseAsync("validate_unreachable_endpoint_marks_unhealthy", "ValidateAsync marks an unreachable endpoint unhealthy", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ModelEndpointService service = new ModelEndpointService(testDb.Driver, CreateLogging());
                AuthContext auth = AuthContext.Authenticated("ten_mep_val", "usr_mep_val", false, true, "UnitTest");

                ModelEndpoint endpoint = new ModelEndpoint
                {
                    Name = "Unreachable",
                    Kind = ModelEndpointKindEnum.Embedding,
                    Provider = ModelProviderEnum.OpenAI,
                    BaseUrl = "http://127.0.0.1:1",
                    Model = "text-embedding-3-small",
                    TimeoutMs = 2000
                };
                ModelEndpoint created = await service.CreateAsync(auth, endpoint).ConfigureAwait(false);

                ModelEndpointProbeResult result = await service.ValidateAsync(auth, created.Id).ConfigureAwait(false);
                AssertFalse(result.Success, "Expected validation against an unreachable endpoint to fail.");

                ModelEndpoint? reloaded = await testDb.Driver.ModelEndpoints.ReadAsync(created.Id).ConfigureAwait(false);
                AssertNotNull(reloaded, "Expected endpoint to reload.");
                AssertEqual(EndpointHealthStatusEnum.Unhealthy, reloaded!.HealthStatus);
            }));

            cases.Add(CaseAsync("missing_authentication_is_rejected", "Missing authentication is rejected", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ModelEndpointService service = new ModelEndpointService(testDb.Driver, CreateLogging());
                await AssertThrowsAsync<UnauthorizedAccessException>(() => service.EnumerateAsync(new AuthContext()));
            }));

            cases.Add(CaseAsync("cross_tenant_read_is_hidden", "Cross-tenant reads are hidden", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ModelEndpointService service = new ModelEndpointService(testDb.Driver, CreateLogging());
                AuthContext owner = AuthContext.Authenticated("ten_mep_scope", "usr_mep_owner", false, false, "UnitTest");
                ModelEndpoint endpoint = NewInference("Private", "http://127.0.0.1:1");
                endpoint.Scope = ScopeEnum.UserSpecific;
                ModelEndpoint created = await service.CreateAsync(owner, endpoint).ConfigureAwait(false);

                AuthContext otherTenant = AuthContext.Authenticated("ten_other", "usr_other", false, false, "UnitTest");
                ModelEndpoint? hidden = await service.ReadAsync(otherTenant, created.Id).ConfigureAwait(false);
                AssertNull(hidden, "A different tenant must not read the endpoint.");
                List<ModelEndpoint> listed = await service.EnumerateAsync(otherTenant).ConfigureAwait(false);
                AssertEqual(0, listed.Count);
            }));

            cases.Add(CaseAsync("private_endpoint_is_owner_scoped", "Private endpoints are owner scoped", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ModelEndpointService service = new ModelEndpointService(testDb.Driver, CreateLogging());
                AuthContext owner = AuthContext.Authenticated("ten_mep_scope2", "usr_mep_owner", false, false, "UnitTest");
                ModelEndpoint endpoint = NewInference("Private", "http://127.0.0.1:1");
                endpoint.Scope = ScopeEnum.UserSpecific;
                ModelEndpoint created = await service.CreateAsync(owner, endpoint).ConfigureAwait(false);

                AuthContext peer = AuthContext.Authenticated("ten_mep_scope2", "usr_mep_peer", false, false, "UnitTest");
                AssertNull(await service.ReadAsync(peer, created.Id).ConfigureAwait(false), "A peer must not read a private endpoint.");
                await AssertThrowsAsync<UnauthorizedAccessException>(() => service.UpdateAsync(peer, new ModelEndpoint
                {
                    Id = created.Id,
                    Name = "Changed",
                    BaseUrl = created.BaseUrl,
                    Provider = created.Provider,
                    Kind = created.Kind,
                    Scope = ScopeEnum.UserSpecific
                }));
                ModelEndpoint? ownerRead = await service.ReadAsync(owner, created.Id).ConfigureAwait(false);
                AssertNotNull(ownerRead, "The owner must read the private endpoint.");
            }));

            cases.Add(CaseAsync("regular_user_cannot_create_tenant_endpoint", "Regular users cannot create tenant endpoints", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ModelEndpointService service = new ModelEndpointService(testDb.Driver, CreateLogging());
                AuthContext user = AuthContext.Authenticated("ten_mep_scope3", "usr_mep_user", false, false, "UnitTest");
                await AssertThrowsAsync<UnauthorizedAccessException>(() => service.CreateAsync(user, NewInference("Shared", "http://127.0.0.1:1")));
            }));

            cases.Add(CaseAsync("global_admin_can_read_all_tenants", "Global admins can read all tenants", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ModelEndpointService service = new ModelEndpointService(testDb.Driver, CreateLogging());
                AuthContext owner = AuthContext.Authenticated("ten_mep_scope4", "usr_mep_owner", false, false, "UnitTest");
                ModelEndpoint created = await service.CreateAsync(owner, NewPrivateInference("Private"));
                AuthContext global = AuthContext.Authenticated("", "", true, true, "UnitTest");
                ModelEndpoint? read = await service.ReadAsync(global, created.Id).ConfigureAwait(false);
                AssertNotNull(read, "A global admin must read endpoints across tenants.");
                AssertTrue((await service.EnumerateAsync(global).ConfigureAwait(false)).Count >= 1, "A global admin must enumerate all endpoints.");
            }));

            return new TestSuiteDescriptor(
                suiteId: "Services.ModelEndpointService",
                displayName: "Model Endpoint Service",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static async Task<CapturedHttpRequest> CaptureAndRespondAsync(TcpListener listener, string responseBody)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            using NetworkStream stream = client.GetStream();
            using StreamReader reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true);
            string requestLine = await reader.ReadLineAsync().ConfigureAwait(false) ?? String.Empty;
            List<string> headers = new List<string>();
            int contentLength = 0;
            while (true)
            {
                string line = await reader.ReadLineAsync().ConfigureAwait(false) ?? String.Empty;
                if (line.Length == 0) break;
                headers.Add(line);
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    Int32.TryParse(line.Substring("Content-Length:".Length).Trim(), out contentLength);
            }
            char[] body = new char[contentLength];
            int offset = 0;
            while (offset < body.Length)
            {
                int read = await reader.ReadAsync(body, offset, body.Length - offset).ConfigureAwait(false);
                if (read == 0) break;
                offset += read;
            }
            byte[] response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + Encoding.UTF8.GetByteCount(responseBody) + "\r\nConnection: close\r\n\r\n" + responseBody);
            await stream.WriteAsync(response).ConfigureAwait(false);
            return new CapturedHttpRequest
            {
                Target = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 1
                    ? requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]
                    : String.Empty,
                Headers = String.Join("\n", headers),
                Body = new String(body, 0, offset)
            };
        }

        private sealed class ValidationFixture
        {
            public ValidationFixture(ModelProviderEnum provider, ModelEndpointKindEnum kind, string path, string? header)
            {
                Provider = provider;
                Kind = kind;
                Path = path;
                Header = header;
            }

            public ModelProviderEnum Provider { get; }
            public ModelEndpointKindEnum Kind { get; }
            public string Path { get; }
            public string? Header { get; }
        }

        private sealed class CapturedHttpRequest
        {
            public string Target { get; set; } = String.Empty;
            public string Headers { get; set; } = String.Empty;
            public string Body { get; set; } = String.Empty;
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static ModelEndpoint NewInference(string name, string baseUrl)
        {
            return new ModelEndpoint
            {
                Name = name,
                Kind = ModelEndpointKindEnum.Inference,
                Provider = ModelProviderEnum.OpenAI,
                BaseUrl = baseUrl,
                Model = "gpt-4o-mini"
            };
        }

        private static ModelEndpoint NewPrivateInference(string name)
        {
            ModelEndpoint endpoint = NewInference(name, "http://127.0.0.1:1");
            endpoint.Scope = ScopeEnum.UserSpecific;
            return endpoint;
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.ModelEndpointService",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
