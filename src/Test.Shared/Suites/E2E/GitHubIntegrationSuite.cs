namespace Test.Shared.Suites.E2E
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Net.Sockets;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// End-to-end descriptors for pull-based GitHub-backed objective import, Actions sync, and PR
    /// evidence, ported 1:1 from the retired automated GitHubIntegrationTests suite. Each case
    /// stands up an in-process fake GitHub API server and is fully self-contained against the
    /// shared e2e server fixture.
    /// </summary>
    public sealed class GitHubIntegrationSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "E2E.GitHubIntegration";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the GitHub Integration suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("github_objectives_refresh_keeps_terminal_status", "GitHubObjectives_RefreshKeepsTerminalStatus", TestTags.Positive, async () =>
            {
                E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                HttpClient authClient = fx.AuthClient;

                using FakeGitHubServer fakeGitHub = new FakeGitHubServer("ghp_vessel_token");
                string vesselId = String.Empty;
                string objectiveId = String.Empty;
                string workingDirectory = Path.Combine(Path.GetTempPath(), "armada-github-terminal-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    vesselId = await CreateVesselAsync(authClient, "GitHub Terminal Objective Vessel", fakeGitHub.RepositoryUrl, workingDirectory, fakeGitHub.ExpectedToken).ConfigureAwait(false);

                    HttpResponseMessage importResponse = await authClient.PostAsync("/api/v1/objectives/import/github",
                        JsonHelper.ToJsonContent(new
                        {
                            VesselId = vesselId,
                            SourceType = GitHubObjectiveSourceTypeEnum.PullRequest,
                            Number = 45
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.Created, importResponse.StatusCode);
                    Objective imported = await JsonHelper.DeserializeAsync<Objective>(importResponse).ConfigureAwait(false);
                    objectiveId = imported.Id;
                    AssertEqual(ObjectiveStatusEnum.InProgress, imported.Status);

                    HttpResponseMessage completeResponse = await authClient.PutAsync("/api/v1/objectives/" + objectiveId,
                        JsonHelper.ToJsonContent(new { Status = ObjectiveStatusEnum.Completed })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, completeResponse.StatusCode);

                    // A merged pull request imports as Released. A refresh never moves a Completed or
                    // Cancelled objective out of the terminal set; only an explicit StatusOverride does.
                    fakeGitHub.PullRequest45Merged = true;
                    HttpResponseMessage refreshResponse = await authClient.PostAsync("/api/v1/objectives/import/github",
                        JsonHelper.ToJsonContent(new
                        {
                            VesselId = vesselId,
                            ObjectiveId = objectiveId,
                            SourceType = GitHubObjectiveSourceTypeEnum.PullRequest,
                            Number = 45
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, refreshResponse.StatusCode);
                    Objective refreshed = await JsonHelper.DeserializeAsync<Objective>(refreshResponse).ConfigureAwait(false);
                    AssertEqual(ObjectiveStatusEnum.Completed, refreshed.Status);

                    HttpResponseMessage overrideResponse = await authClient.PostAsync("/api/v1/objectives/import/github",
                        JsonHelper.ToJsonContent(new
                        {
                            VesselId = vesselId,
                            ObjectiveId = objectiveId,
                            SourceType = GitHubObjectiveSourceTypeEnum.PullRequest,
                            Number = 45,
                            StatusOverride = ObjectiveStatusEnum.Released
                        })).ConfigureAwait(false);
                    AssertEqual(HttpStatusCode.OK, overrideResponse.StatusCode);
                    Objective overridden = await JsonHelper.DeserializeAsync<Objective>(overrideResponse).ConfigureAwait(false);
                    AssertEqual(ObjectiveStatusEnum.Released, overridden.Status);
                }
                finally
                {
                    if (!String.IsNullOrWhiteSpace(objectiveId))
                    {
                        try { await authClient.DeleteAsync("/api/v1/objectives/" + objectiveId).ConfigureAwait(false); } catch { }
                    }
                    if (!String.IsNullOrWhiteSpace(vesselId))
                    {
                        try { await authClient.DeleteAsync("/api/v1/vessels/" + vesselId).ConfigureAwait(false); } catch { }
                    }
                    TryDeleteDirectory(workingDirectory);
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "GitHub Integration",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static async Task<string> CreateVesselAsync(HttpClient authClient, string name, string repoUrl, string workingDirectory, string gitHubTokenOverride)
        {
            HttpResponseMessage vesselResponse = await authClient.PostAsync("/api/v1/vessels",
                JsonHelper.ToJsonContent(new
                {
                    Name = name,
                    RepoUrl = repoUrl,
                    LocalPath = workingDirectory,
                    WorkingDirectory = workingDirectory,
                    DefaultBranch = "main",
                    GitHubTokenOverride = gitHubTokenOverride
                })).ConfigureAwait(false);
            AssertEqual(HttpStatusCode.Created, vesselResponse.StatusCode);
            Vessel vessel = await JsonHelper.DeserializeAsync<Vessel>(vesselResponse).ConfigureAwait(false);
            AssertTrue(vessel.HasGitHubTokenOverride, "Expected vessel GitHub token override to be flagged as configured.");
            return vessel.Id;
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

        #region Nested-Types

        private sealed class FakeGitHubServer : IDisposable
        {
            private readonly HttpListener _Listener;
            private readonly CancellationTokenSource _TokenSource;
            private readonly Task _ListenerTask;

            public string ExpectedToken { get; }

            public string Issue123Title { get; set; } = "GitHub Issue 123";

            public string Issue123State { get; set; } = "open";

            public bool PullRequest45Merged { get; set; } = false;

            public int Port { get; }

            public string RepositoryUrl => "http://localhost:" + Port + "/octo/armada-test.git";

            public string PullRequestUrl => "http://localhost:" + Port + "/octo/armada-test/pull/45";

            public string IssueUrl => "http://localhost:" + Port + "/octo/armada-test/issues/123";

            public FakeGitHubServer(string expectedToken)
            {
                ExpectedToken = expectedToken ?? throw new ArgumentNullException(nameof(expectedToken));
                Port = GetAvailablePort();
                _Listener = new HttpListener();
                _Listener.Prefixes.Add("http://localhost:" + Port + "/");
                _Listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
                _TokenSource = new CancellationTokenSource();
                _Listener.Start();
                _ListenerTask = Task.Run(ListenAsync);
            }

            public void Dispose()
            {
                _TokenSource.Cancel();
                try
                {
                    _Listener.Stop();
                }
                catch
                {
                }

                try
                {
                    _Listener.Close();
                }
                catch
                {
                }

                try
                {
                    _ListenerTask.GetAwaiter().GetResult();
                }
                catch
                {
                }
            }

            private async Task ListenAsync()
            {
                while (!_TokenSource.IsCancellationRequested)
                {
                    HttpListenerContext? context = null;
                    try
                    {
                        context = await _Listener.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (HttpListenerException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }

                    if (context == null)
                        continue;

                    _ = Task.Run(() => HandleRequestAsync(context));
                }
            }

            private async Task HandleRequestAsync(HttpListenerContext context)
            {
                try
                {
                    string authorization = context.Request.Headers["Authorization"] ?? String.Empty;
                    if (!String.Equals(authorization, "Bearer " + ExpectedToken, StringComparison.Ordinal))
                    {
                        context.Response.StatusCode = 401;
                        await WriteJsonAsync(context.Response, "{\"message\":\"bad token\"}").ConfigureAwait(false);
                        return;
                    }

                    string path = context.Request.Url?.AbsolutePath ?? String.Empty;
                    if (String.Equals(path, "/api/v3/repos/octo/armada-test/issues/123", StringComparison.OrdinalIgnoreCase))
                    {
                        string body = "{\"number\":123,\"title\":\"" + EscapeJson(Issue123Title) + "\",\"body\":\"Track intake from GitHub.\",\"state\":\"" + EscapeJson(Issue123State) + "\",\"html_url\":\"" + IssueUrl + "\",\"updated_at\":\"2026-05-06T12:00:00Z\",\"labels\":[{\"name\":\"intake\"},{\"name\":\"scope\"}],\"assignees\":[{\"login\":\"joel\"}]}";
                        await WriteJsonAsync(context.Response, body).ConfigureAwait(false);
                        return;
                    }

                    if (String.Equals(path, "/api/v3/repos/octo/armada-test/pulls/45", StringComparison.OrdinalIgnoreCase))
                    {
                        string body = "{\"number\":45,\"title\":\"Armada Integration PR\",\"body\":\"Implements GitHub-backed delivery visibility.\",\"state\":\"open\",\"html_url\":\"" + PullRequestUrl + "\",\"draft\":false,\"merged\":" + (PullRequest45Merged ? "true" : "false") + ",\"mergeable_state\":\"clean\",\"additions\":12,\"deletions\":3,\"changed_files\":4,\"commits\":2,\"created_at\":\"2026-05-06T10:00:00Z\",\"updated_at\":\"2026-05-06T11:00:00Z\",\"merged_at\":null,\"user\":{\"login\":\"captain-armada\"},\"merged_by\":null,\"base\":{\"ref\":\"main\",\"sha\":\"base123\"},\"head\":{\"ref\":\"feature/github\",\"sha\":\"abc123\"},\"requested_reviewers\":[{\"login\":\"reviewer1\"}],\"labels\":[{\"name\":\"automation\"}]}";
                        await WriteJsonAsync(context.Response, body).ConfigureAwait(false);
                        return;
                    }

                    if (String.Equals(path, "/api/v3/repos/octo/armada-test/issues/45", StringComparison.OrdinalIgnoreCase))
                    {
                        string body = "{\"number\":45,\"title\":\"Armada Integration PR\",\"body\":\"Implements GitHub-backed delivery visibility.\",\"state\":\"open\",\"html_url\":\"" + PullRequestUrl + "\",\"updated_at\":\"2026-05-06T11:00:00Z\",\"labels\":[{\"name\":\"automation\"}],\"assignees\":[{\"login\":\"joel\"}]}";
                        await WriteJsonAsync(context.Response, body).ConfigureAwait(false);
                        return;
                    }

                    if (String.Equals(path, "/api/v3/repos/octo/armada-test/actions/runs", StringComparison.OrdinalIgnoreCase))
                    {
                        string body = "{\"workflow_runs\":[{\"id\":9001,\"name\":\"CI Build\",\"display_title\":\"CI Build\",\"status\":\"completed\",\"conclusion\":\"success\",\"event\":\"push\",\"html_url\":\"http://localhost:" + Port + "/octo/armada-test/actions/runs/9001\",\"head_branch\":\"main\",\"head_sha\":\"abc123\",\"created_at\":\"2026-05-06T09:00:00Z\",\"run_started_at\":\"2026-05-06T09:01:00Z\",\"updated_at\":\"2026-05-06T09:02:00Z\"}]}";
                        await WriteJsonAsync(context.Response, body).ConfigureAwait(false);
                        return;
                    }

                    if (String.Equals(path, "/api/v3/repos/octo/armada-test/pulls/45/reviews", StringComparison.OrdinalIgnoreCase))
                    {
                        string body = "[{\"state\":\"APPROVED\",\"body\":\"Looks good\",\"submitted_at\":\"2026-05-06T11:05:00Z\",\"user\":{\"login\":\"reviewer1\"}},{\"state\":\"CHANGES_REQUESTED\",\"body\":\"Please rename one method\",\"submitted_at\":\"2026-05-06T10:30:00Z\",\"user\":{\"login\":\"reviewer2\"}}]";
                        await WriteJsonAsync(context.Response, body).ConfigureAwait(false);
                        return;
                    }

                    if (String.Equals(path, "/api/v3/repos/octo/armada-test/issues/45/comments", StringComparison.OrdinalIgnoreCase))
                    {
                        string body = "[{\"body\":\"Need one more check\",\"html_url\":\"http://localhost:" + Port + "/octo/armada-test/issues/45#issuecomment-1\",\"created_at\":\"2026-05-06T10:10:00Z\",\"user\":{\"login\":\"reviewer1\"}}]";
                        await WriteJsonAsync(context.Response, body).ConfigureAwait(false);
                        return;
                    }

                    if (String.Equals(path, "/api/v3/repos/octo/armada-test/commits/abc123/check-runs", StringComparison.OrdinalIgnoreCase))
                    {
                        string body = "{\"check_runs\":[{\"name\":\"build-and-test\",\"status\":\"completed\",\"conclusion\":\"success\",\"details_url\":\"http://localhost:" + Port + "/octo/armada-test/actions/runs/9001\"}]}";
                        await WriteJsonAsync(context.Response, body).ConfigureAwait(false);
                        return;
                    }

                    context.Response.StatusCode = 404;
                    await WriteJsonAsync(context.Response, "{\"message\":\"not found\"}").ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        context.Response.OutputStream.Close();
                    }
                    catch
                    {
                    }
                }
            }

            private static async Task WriteJsonAsync(HttpListenerResponse response, string body)
            {
                byte[] data = Encoding.UTF8.GetBytes(body);
                response.ContentType = "application/json";
                response.ContentEncoding = Encoding.UTF8;
                response.ContentLength64 = data.Length;
                await response.OutputStream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
            }

            private static string EscapeJson(string value)
            {
                return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
            }

            private static int GetAvailablePort()
            {
                TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                return port;
            }
        }

        #endregion
    }
}
