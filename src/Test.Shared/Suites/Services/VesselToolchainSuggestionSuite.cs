namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Toolchain suggestions come from marker files. A suggestion does not create or change a
    /// workflow profile, and the same suggestion is returned by the REST route and the MCP tool.
    /// </summary>
    public sealed class VesselToolchainSuggestionSuite : IArmadaTestSuite
    {
        private const string SuiteId = "Services.VesselToolchainSuggestion";
        private static readonly object SuiteKey = new object();

        /// <summary>
        /// Build the toolchain suggestion suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            return new TestSuiteDescriptor(SuiteId, "Vessel toolchain suggestion", new List<TestCaseDescriptor>
            {
                Case("marker_files_select_ecosystems_and_leave_the_checkout_unchanged", "Marker files select ecosystems and leave the checkout unchanged", TestTags.Positive, MarkerFilesSelectEcosystems),
                Case("a_dependency_directory_is_not_an_ecosystem", "A dependency directory is not an ecosystem", TestTags.Negative, DependencyDirectoryIsNotAnEcosystem),
                Case("a_missing_directory_is_not_readable_and_names_no_path", "A missing directory is not readable and names no path", TestTags.Negative, MissingDirectoryIsNotReadable),
                CaseAsync("rest_and_mcp_return_the_suggestion_and_write_no_profile", "REST and MCP return the suggestion and write no profile", TestTags.Positive, RestAndMcpReturnTheSuggestion)
            });
        }

        private static void MarkerFilesSelectEcosystems()
        {
            string root = TestTemp.NewDirectory("toolchain");
            try
            {
                File.WriteAllText(Path.Combine(root, "go.mod"), "module example\n");
                File.WriteAllText(Path.Combine(root, "package.json"), "{}\n");
                Directory.CreateDirectory(Path.Combine(root, "src", "App"));
                File.WriteAllText(Path.Combine(root, "src", "App", "App.csproj"), "<Project />\n");
                string before = Snapshot(root);

                ToolchainSuggestion suggestion = VesselToolchainDetector.Detect(root);

                AssertTrue(suggestion.CheckoutReadable, "the checkout can be read");
                AssertFalse(suggestion.Truncated, "a small checkout is not truncated");
                AssertNull(suggestion.Reason, "a found ecosystem has no empty reason");
                AssertEqual("DotNet,Node,Go", String.Join(",", suggestion.Ecosystems.Select(item => item.Ecosystem)), "ecosystems stay in catalog order");
                ToolchainEcosystemSuggestion dotnet = suggestion.Ecosystems[0];
                AssertEqual("src/App/App.csproj", dotnet.Markers[0], "a project two levels down is a marker");
                AssertEqual("dotnet build", dotnet.BuildCommand, "DotNet build suggestion");
                AssertEqual("dotnet test", dotnet.TestCommand, "DotNet test suggestion");
                AssertEqual("npm run lint", suggestion.Ecosystems[1].LintCommand, "Node lint suggestion");
                AssertEqual("go test ./...", suggestion.Ecosystems[2].TestCommand, "Go test suggestion");
                AssertFalse(JsonSerializer.Serialize(suggestion).Contains(root, StringComparison.Ordinal), "the suggestion does not contain the checkout path");
                AssertEqual(before, Snapshot(root), "detection writes nothing in the checkout");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static void DependencyDirectoryIsNotAnEcosystem()
        {
            string root = TestTemp.NewDirectory("toolchain-skip");
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "node_modules", "leftpad"));
                File.WriteAllText(Path.Combine(root, "node_modules", "leftpad", "package.json"), "{}\n");
                ToolchainSuggestion suggestion = VesselToolchainDetector.Detect(root);
                AssertTrue(suggestion.CheckoutReadable, "the checkout can be read");
                AssertEqual(0, suggestion.Ecosystems.Count, "a package inside node_modules is not a Node checkout");
                AssertEqual("No supported ecosystem marker was found.", suggestion.Reason, "the empty reason names the miss");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static void MissingDirectoryIsNotReadable()
        {
            string missing = Path.Combine(TestTemp.NewDirectory("toolchain-missing"), "absent");
            ToolchainSuggestion suggestion = VesselToolchainDetector.Detect(missing);
            AssertFalse(suggestion.CheckoutReadable, "a missing directory is not readable");
            AssertEqual(0, suggestion.Ecosystems.Count, "a missing directory has no ecosystem");
            AssertEqual("The vessel has no readable working directory.", suggestion.Reason, "the reason names the miss");
            AssertFalse((suggestion.Reason ?? "").Contains(missing, StringComparison.Ordinal), "the reason does not contain the path");
        }

        private static async Task RestAndMcpReturnTheSuggestion()
        {
            E2EServerFixture fx = await E2EServerFixture.AcquireAsync(SuiteKey).ConfigureAwait(false);
            string root = TestTemp.NewDirectory("toolchain-api");
            File.WriteAllText(Path.Combine(root, "Cargo.toml"), "[package]\nname = \"example\"\n");
            try
            {
                long profilesBefore = await ProfileCountAsync(fx.AuthClient).ConfigureAwait(false);
                string vesselId;
                using (HttpResponseMessage created = await fx.AuthClient.PostAsync("/api/v1/vessels", JsonHelper.ToJsonContent(new
                {
                    Name = "toolchain-" + Guid.NewGuid().ToString("N"),
                    RepoUrl = "https://example.invalid/toolchain.git",
                    WorkingDirectory = root
                })).ConfigureAwait(false))
                {
                    AssertEqual(HttpStatusCode.Created, created.StatusCode, "vessel create");
                    Vessel vessel = await JsonHelper.DeserializeAsync<Vessel>(created).ConfigureAwait(false);
                    vesselId = vessel.Id;
                }

                ToolchainSuggestion rest;
                using (HttpResponseMessage response = await fx.AuthClient.GetAsync("/api/v1/vessels/" + vesselId + "/toolchain-suggestion").ConfigureAwait(false))
                {
                    AssertEqual(HttpStatusCode.OK, response.StatusCode, "suggestion read");
                    rest = await JsonHelper.DeserializeAsync<ToolchainSuggestion>(response).ConfigureAwait(false);
                }

                AssertEqual(vesselId, rest.VesselId, "the suggestion names the vessel");
                AssertEqual(1, rest.Ecosystems.Count, "one ecosystem");
                AssertEqual("Rust", rest.Ecosystems[0].Ecosystem, "Cargo.toml is Rust");
                AssertEqual("cargo test", rest.Ecosystems[0].TestCommand, "Rust test suggestion");
                AssertEqual(profilesBefore, await ProfileCountAsync(fx.AuthClient).ConfigureAwait(false), "the read creates no workflow profile");

                string session = Guid.NewGuid().ToString("N");
                await SendMcpAsync(fx.McpClient, session, "initialize", new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { },
                    clientInfo = new { name = "toolchain-test", version = "1" }
                }).ConfigureAwait(false);
                JsonElement call = await SendMcpAsync(fx.McpClient, session, "tools/call", new
                {
                    name = "armada_suggest_vessel_toolchain",
                    arguments = new { vesselId }
                }).ConfigureAwait(false);
                string text = call.GetProperty("content")[0].GetProperty("text").GetString()!;
                ToolchainSuggestion mcp = JsonSerializer.Deserialize<ToolchainSuggestion>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                AssertEqual("Rust", mcp.Ecosystems[0].Ecosystem, "MCP returns the same ecosystem");
                AssertEqual(profilesBefore, await ProfileCountAsync(fx.AuthClient).ConfigureAwait(false), "the MCP read creates no workflow profile");
                AssertFalse(text.Contains(root, StringComparison.Ordinal), "the MCP text does not contain the checkout path");

                using (HttpResponseMessage deleted = await fx.AuthClient.DeleteAsync("/api/v1/vessels/" + vesselId).ConfigureAwait(false))
                    AssertTrue(deleted.IsSuccessStatusCode, "vessel delete");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static async Task<long> ProfileCountAsync(HttpClient client)
        {
            using (HttpResponseMessage response = await client.GetAsync("/api/v1/workflow-profiles?pageNumber=1&pageSize=1").ConfigureAwait(false))
            {
                AssertEqual(HttpStatusCode.OK, response.StatusCode, "profile list");
                EnumerationResult<WorkflowProfile> page = await JsonHelper.DeserializeAsync<EnumerationResult<WorkflowProfile>>(response).ConfigureAwait(false);
                return page.TotalRecords;
            }
        }

        private static async Task<JsonElement> SendMcpAsync(HttpClient client, string sessionId, string method, object parameters)
        {
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/mcp"))
            {
                request.Content = JsonHelper.ToJsonContent(new { jsonrpc = "2.0", id = 1, method, @params = parameters });
                request.Headers.Add("X-Session-Id", sessionId);
                request.Headers.Add("Accept", "application/json, text/event-stream");
                using (HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false))
                {
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    AssertTrue(response.IsSuccessStatusCode, "MCP " + method + " failed: " + body);
                    string json = body;
                    if (String.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                    {
                        json = "";
                        foreach (string line in body.Split('\n'))
                        {
                            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                            json = line.Substring(5).Trim();
                            break;
                        }
                    }

                    JsonElement payload = JsonSerializer.Deserialize<JsonElement>(json);
                    if (payload.TryGetProperty("error", out JsonElement error))
                        throw new InvalidOperationException("MCP error: " + error);
                    return payload.GetProperty("result");
                }
            }
        }

        private static string Snapshot(string root)
        {
            return String.Join("\n", Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal));
        }

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(SuiteId, caseId, displayName, (CancellationToken ct) =>
            {
                body();
                return Task.CompletedTask;
            }, new List<string> { tag });
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(SuiteId, caseId, displayName, (CancellationToken ct) => body(), new List<string> { tag });
        }
    }
}
