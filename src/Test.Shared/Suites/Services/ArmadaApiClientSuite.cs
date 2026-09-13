namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Client;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>Contract tests for the typed client against registered fork routes.</summary>
    public sealed class ArmadaApiClientSuite : IArmadaTestSuite
    {
        /// <summary>Builds the typed client route contract cases.</summary>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();
            cases.Add(CaseAsync("get_readiness_uses_route_and_deserializes", "ArmadaApiClient Readiness Uses Route", TestTags.Positive, async () =>
            {
                RecordingHandler handler = new RecordingHandler(HttpStatusCode.OK, "{\"isReady\":true,\"vesselId\":\"vsl/a\"}");
                using (ArmadaApiClient client = CreateClient(handler))
                {
                VesselReadinessResult? result = await client.GetVesselReadinessAsync("vsl/a", environmentName: "test env");
                AssertNotNull(result);
                AssertTrue(result!.IsReady, "Readiness must deserialize IsReady.");
                AssertEqual("vsl/a", result.VesselId);
                AssertEqual("GET", handler.Method);
                AssertEqual("/api/v1/vessels/vsl%2Fa/readiness?environmentName=test%20env", handler.PathAndQuery);
                }
            }));
            cases.Add(CaseAsync("create_deployment_posts_typed_body", "ArmadaApiClient Deployment Posts Typed Body", TestTags.Positive, async () =>
            {
                RecordingHandler handler = new RecordingHandler(HttpStatusCode.OK, "{\"id\":\"dpl_response\",\"title\":\"typed deployment\"}");
                using (ArmadaApiClient client = CreateClient(handler))
                {
                await client.CreateDeploymentAsync(new DeploymentUpsertRequest { Title = "typed deployment" });
                AssertEqual("POST", handler.Method);
                AssertEqual("/api/v1/deployments", handler.PathAndQuery);
                AssertTrue(handler.Body.Contains("\"title\":\"typed deployment\"", StringComparison.OrdinalIgnoreCase), "The deployment title must be serialized.");
                }
            }));
            cases.Add(CaseAsync("release_id_is_encoded", "ArmadaApiClient Encodes Release ID", TestTags.Positive, async () =>
            {
                RecordingHandler handler = new RecordingHandler(HttpStatusCode.OK, "{\"id\":\"rel_response\",\"title\":\"typed release\"}");
                using (ArmadaApiClient client = CreateClient(handler))
                {
                await client.GetReleaseAsync("rel/a b");
                AssertEqual("/api/v1/releases/rel%2Fa%20b", handler.PathAndQuery);
                }
            }));
            cases.Add(CaseAsync("server_error_contains_response_body", "ArmadaApiClient Server Error Contains Body", TestTags.Negative, async () =>
            {
                RecordingHandler handler = new RecordingHandler(HttpStatusCode.BadRequest, "route contract failed");
                using (ArmadaApiClient client = CreateClient(handler))
                {
                try
                {
                    await client.RefreshReleaseAsync("rel_test");
                    throw new InvalidOperationException("Expected the client to reject the failed response.");
                }
                catch (HttpRequestException ex)
                {
                    AssertEqual(HttpStatusCode.BadRequest, ex.StatusCode);
                    AssertTrue(ex.Message.Contains("route contract failed", StringComparison.Ordinal), "The response body must be retained in the exception.");
                }
                }
            }));
            cases.Add(CaseAsync("cancellation_is_forwarded", "ArmadaApiClient Cancellation Is Forwarded", TestTags.Negative, async () =>
            {
                RecordingHandler handler = new RecordingHandler(new OperationCanceledException());
                using (ArmadaApiClient client = CreateClient(handler))
                using (CancellationTokenSource source = new CancellationTokenSource())
                {
                Task<Job?> request = client.GetJobAsync("job_test", source.Token);
                source.CancelAfter(20);
                await AssertThrowsAsync<OperationCanceledException>(() => request);
                AssertTrue(handler.CancellationObserved, "The handler must receive the cancellation token.");
                }
            }));
            AddContractCases(cases);
            return new TestSuiteDescriptor(
                suiteId: "Services.ArmadaApiClient",
                displayName: "Armada API Client",
                cases: cases);
        }

        private static void AddContractCases(List<TestCaseDescriptor> cases)
        {
            cases.Add(ContractCase("vessel_landing_preview", "GET", "/api/v1/vessels/vsl_test/landing-preview?sourceBranch=feature%2Ftest", (client, token) => client.GetVesselLandingPreviewAsync("vsl_test", "feature/test", token)));
            cases.Add(ContractCase("environment_get", "GET", "/api/v1/environments/env_test", (client, token) => client.GetEnvironmentAsync("env_test", token)));
            cases.Add(ContractCase("environment_create", "POST", "/api/v1/environments", (client, token) => client.CreateEnvironmentAsync(new DeploymentEnvironmentUpsertRequest { Name = "typed environment" }, token)));
            cases.Add(ContractCase("environment_update", "PUT", "/api/v1/environments/env_test", (client, token) => client.UpdateEnvironmentAsync("env_test", new DeploymentEnvironmentUpsertRequest { Name = "typed environment" }, token)));
            cases.Add(ContractCaseVoid("environment_delete", "DELETE", "/api/v1/environments/env_test", async (client, token) => { await client.DeleteEnvironmentAsync("env_test", token); }));
            cases.Add(ContractCase("deployment_get", "GET", "/api/v1/deployments/dpl_test", (client, token) => client.GetDeploymentAsync("dpl_test", token)));
            cases.Add(ContractCase("deployment_create", "POST", "/api/v1/deployments", (client, token) => client.CreateDeploymentAsync(new DeploymentUpsertRequest { Title = "typed deployment" }, token)));
            cases.Add(ContractCase("deployment_update", "PUT", "/api/v1/deployments/dpl_test", (client, token) => client.UpdateDeploymentAsync("dpl_test", new DeploymentUpsertRequest { Title = "typed deployment" }, token)));
            cases.Add(ContractCase("deployment_approve", "POST", "/api/v1/deployments/dpl_test/approve", (client, token) => client.ApproveDeploymentAsync("dpl_test", "approved", token)));
            cases.Add(ContractCase("deployment_deny", "POST", "/api/v1/deployments/dpl_test/deny", (client, token) => client.DenyDeploymentAsync("dpl_test", "denied", token)));
            cases.Add(ContractCase("deployment_verify", "POST", "/api/v1/deployments/dpl_test/verify", (client, token) => client.VerifyDeploymentAsync("dpl_test", token)));
            cases.Add(ContractCase("deployment_rollback", "POST", "/api/v1/deployments/dpl_test/rollback", (client, token) => client.RollbackDeploymentAsync("dpl_test", token)));
            cases.Add(ContractCaseVoid("deployment_delete", "DELETE", "/api/v1/deployments/dpl_test", async (client, token) => { await client.DeleteDeploymentAsync("dpl_test", token); }));
            cases.Add(ContractCase("mission_landing_preview", "GET", "/api/v1/missions/msn_test/landing-preview", (client, token) => client.GetMissionLandingPreviewAsync("msn_test", token)));
            cases.Add(ContractCase("mission_github_pull_request", "GET", "/api/v1/missions/msn_test/github/pull-request", (client, token) => client.GetMissionGitHubPullRequestAsync("msn_test", token)));
            cases.Add(ContractCase("token_usage_summary", "GET", "/api/v1/token-usage/summary?model=model%2Ftest", (client, token) => client.GetTokenUsageSummaryAsync(model: "model/test", token: token)));
            cases.Add(ContractCase("release_get", "GET", "/api/v1/releases/rel_test", (client, token) => client.GetReleaseAsync("rel_test", token)));
            cases.Add(ContractCase("release_create", "POST", "/api/v1/releases", (client, token) => client.CreateReleaseAsync(new ReleaseUpsertRequest { Title = "typed release" }, token)));
            cases.Add(ContractCase("release_update", "PUT", "/api/v1/releases/rel_test", (client, token) => client.UpdateReleaseAsync("rel_test", new ReleaseUpsertRequest { Title = "typed release" }, token)));
            cases.Add(ContractCase("release_refresh", "POST", "/api/v1/releases/rel_test/refresh", (client, token) => client.RefreshReleaseAsync("rel_test", token)));
            cases.Add(ContractCaseVoid("release_delete", "DELETE", "/api/v1/releases/rel_test", async (client, token) => { await client.DeleteReleaseAsync("rel_test", token); }));
            cases.Add(ContractCase("check_get", "GET", "/api/v1/check-runs/chk_test", (client, token) => client.GetCheckRunAsync("chk_test", token)));
            cases.Add(ContractCase("check_run", "POST", "/api/v1/check-runs", (client, token) => client.RunCheckAsync(new CheckRunRequest { VesselId = "vsl_test", Label = "typed check" }, token)));
            cases.Add(ContractCase("github_actions_sync", "POST", "/api/v1/check-runs/sync/github-actions", (client, token) => client.SyncGitHubActionsAsync(new GitHubActionsSyncRequest { VesselId = "vsl_test", WorkflowName = "typed workflow" }, token)));
            cases.Add(ContractCase("check_retry", "POST", "/api/v1/check-runs/chk_test/retry", (client, token) => client.RetryCheckRunAsync("chk_test", token)));
            cases.Add(ContractCaseVoid("check_delete", "DELETE", "/api/v1/check-runs/chk_test", async (client, token) => { await client.DeleteCheckRunAsync("chk_test", token); }));
            cases.Add(ContractCase("job_get", "GET", "/api/v1/jobs/job_test", (client, token) => client.GetJobAsync("job_test", token)));
            cases.Add(ContractCase("job_cancel", "POST", "/api/v1/jobs/job_test/cancel", (client, token) => client.CancelJobAsync("job_test", token)));
        }

        private static TestCaseDescriptor ContractCase<T>(string id, string method, string path, Func<ArmadaApiClient, CancellationToken, Task<T?>> call)
        {
            return CaseAsync(id, "ArmadaApiClient " + id + " contract", TestTags.Positive, async () =>
            {
                RecordingHandler handler = new RecordingHandler(HttpStatusCode.OK, ResponseBody<T>());
                using (ArmadaApiClient client = CreateClient(handler))
                {
                T? result = await call(client, CancellationToken.None);
                AssertEqual(method, handler.Method);
                AssertEqual(path, handler.PathAndQuery);
                if (method != "DELETE") AssertTypedResponse(result);
                if (method == "POST" || method == "PUT")
                    AssertRequestBody(id, handler.Body);
                }
            });
        }

        private static void AssertRequestBody(string id, string body)
        {
            if (id == "environment_create" || id == "environment_update")
            {
                DeploymentEnvironmentUpsertRequest request = JsonSerializer.Deserialize<DeploymentEnvironmentUpsertRequest>(body)!;
                AssertEqual("typed environment", request.Name);
            }
            else if (id == "deployment_create" || id == "deployment_update")
            {
                DeploymentUpsertRequest request = JsonSerializer.Deserialize<DeploymentUpsertRequest>(body)!;
                AssertEqual("typed deployment", request.Title);
            }
            else if (id == "release_create" || id == "release_update")
            {
                ReleaseUpsertRequest request = JsonSerializer.Deserialize<ReleaseUpsertRequest>(body)!;
                AssertEqual("typed release", request.Title);
            }
            else if (id == "check_run")
            {
                CheckRunRequest request = JsonSerializer.Deserialize<CheckRunRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } })!;
                AssertEqual("vsl_test", request.VesselId);
                AssertEqual("typed check", request.Label);
                AssertEqual("Build", request.Type.ToString());
            }
            else if (id == "github_actions_sync")
            {
                GitHubActionsSyncRequest request = JsonSerializer.Deserialize<GitHubActionsSyncRequest>(body)!;
                AssertEqual("vsl_test", request.VesselId);
                AssertEqual("typed workflow", request.WorkflowName);
            }
            else if (id == "deployment_approve" || id == "deployment_deny")
            {
                Dictionary<string, string> request = JsonSerializer.Deserialize<Dictionary<string, string>>(body)!;
                string comment = request.TryGetValue("Comment", out string? pascalComment) ? pascalComment : request["comment"];
                AssertEqual(id.EndsWith("approve", StringComparison.Ordinal) ? "approved" : "denied", comment);
            }
            else
            {
                Dictionary<string, object> request = JsonSerializer.Deserialize<Dictionary<string, object>>(body)!;
                AssertEqual(0, request.Count, "Action requests without fields must be empty JSON objects.");
            }
        }

        private static string ResponseBody<T>()
        {
            Type type = typeof(T);
            if (type == typeof(VesselReadinessResult)) return "{\"isReady\":true,\"vesselId\":\"vsl_response\"}";
            if (type == typeof(LandingPreviewResult)) return "{\"isReadyToLand\":true,\"targetBranch\":\"main\"}";
            if (type == typeof(GitHubPullRequestDetail)) return "{\"number\":42,\"headSha\":\"abc123\",\"title\":\"typed PR\"}";
            if (type == typeof(TokenUsageSummaryResult)) return "{\"totalTokens\":123}";
            if (type == typeof(GitHubActionsSyncResult)) return "{\"createdCount\":3}";
            if (type == typeof(DeploymentEnvironment)) return "{\"id\":\"env_response\",\"name\":\"typed environment\"}";
            if (type == typeof(Deployment)) return "{\"id\":\"dpl_response\",\"title\":\"typed deployment\"}";
            if (type == typeof(Release)) return "{\"id\":\"rel_response\",\"title\":\"typed release\"}";
            if (type == typeof(CheckRun)) return "{\"id\":\"chk_response\",\"label\":\"typed check\"}";
            return "{\"id\":\"job_response\",\"name\":\"typed job\"}";
        }

        private static void AssertTypedResponse<T>(T? result)
        {
            AssertNotNull(result);
            switch (result)
            {
                case VesselReadinessResult readiness:
                    AssertTrue(readiness.IsReady, "IsReady must deserialize.");
                    break;
                case LandingPreviewResult preview:
                    AssertTrue(preview.IsReadyToLand, "IsReadyToLand must deserialize.");
                    break;
                case GitHubPullRequestDetail pullRequest:
                    AssertEqual(42, pullRequest.Number);
                    AssertEqual("abc123", pullRequest.HeadSha);
                    break;
                case TokenUsageSummaryResult usage:
                    AssertEqual(123L, usage.TotalTokens);
                    break;
                case GitHubActionsSyncResult sync:
                    AssertEqual(3, sync.CreatedCount);
                    break;
                case DeploymentEnvironment environment:
                    AssertEqual("env_response", environment.Id);
                    break;
                case Deployment deployment:
                    AssertEqual("dpl_response", deployment.Id);
                    break;
                case Release release:
                    AssertEqual("rel_response", release.Id);
                    break;
                case CheckRun check:
                    AssertEqual("chk_response", check.Id);
                    break;
                case Job job:
                    AssertEqual("job_response", job.Id);
                    break;
            }
        }

        private static TestCaseDescriptor ContractCaseVoid(string id, string method, string path, Func<ArmadaApiClient, CancellationToken, Task> call)
        {
            return CaseAsync(id, "ArmadaApiClient " + id + " contract", TestTags.Positive, async () =>
            {
                RecordingHandler handler = new RecordingHandler(HttpStatusCode.NoContent, String.Empty);
                using (ArmadaApiClient client = CreateClient(handler))
                {
                await call(client, CancellationToken.None);
                AssertEqual(method, handler.Method);
                AssertEqual(path, handler.PathAndQuery);
                }
            });
        }

        private static ArmadaApiClient CreateClient(RecordingHandler handler)
        {
            return new ArmadaApiClient(new HttpClient(handler), "http://localhost:7890");
        }

        private static TestCaseDescriptor CaseAsync(string id, string name, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor("Services.ArmadaApiClient", id, name, _ => body(), new List<string> { tag });
        }

        private sealed class RecordingHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _Status;
            private readonly string? _Content;
            private readonly Exception? _Error;

            public string Method { get; private set; } = String.Empty;
            public string PathAndQuery { get; private set; } = String.Empty;
            public string Body { get; private set; } = String.Empty;
            public bool CancellationObserved { get; private set; }

            public RecordingHandler(HttpStatusCode status, string content)
            {
                _Status = status;
                _Content = content;
            }

            public RecordingHandler(Exception error)
            {
                _Error = error;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Method = request.Method.Method;
                PathAndQuery = request.RequestUri!.PathAndQuery;
                Body = request.Content == null ? String.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
                if (_Error is OperationCanceledException)
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        CancellationObserved = cancellationToken.IsCancellationRequested;
                        throw;
                    }
                }
                if (_Error != null) throw _Error;
                return new HttpResponseMessage(_Status) { Content = new StringContent(_Content, Encoding.UTF8, "application/json") };
            }
        }
    }
}
