namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Services;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="ArmadaMcpConfigBuilder"/> and <see cref="CaptainLaunchIsolationPlanner"/>.
    /// Positive cases confirm each runtime yields the right scoped-config files, arguments, and environment
    /// overrides; negative cases confirm an invalid port or missing scoped directory produces an empty plan
    /// (so isolation never half-applies and disabled launches stay unchanged).
    /// </summary>
    public sealed class CaptainLaunchIsolationSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the launch-isolation suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();
            string scoped = Path.Combine(Path.GetTempPath(), "armada-isolation-test");

            // Every launch presents a caller-scoped session token, never the admiral launch credential. A
            // mission launch carries the mission owner's own session token; this stand-in stands for one, and
            // uses the launch variable so the dock and scoped MCP configurations keep referencing it by name.
            const string missionSessionToken = "armada-mission-scoped-test-token";
            McpCredentialReference missionCredential = McpCredentialReference.ForMission(missionSessionToken);

            // ---- MCP config builder ----
            cases.Add(Case("mcp_url_matches_installer", "MCP URL matches the installer endpoint", TestTags.Positive, () =>
            {
                AssertEqual("http://localhost:7891/mcp", ArmadaMcpConfigBuilder.GetMcpUrl(7891));
            }));

            cases.Add(Case("keyed_config_registers_armada_http", "Keyed config registers the armada HTTP server", TestTags.Positive, () =>
            {
                string json = ArmadaMcpConfigBuilder.BuildKeyedMcpServersJson(7891);
                AssertTrue(json.Contains("mcpServers"), "expected mcpServers key");
                AssertTrue(json.Contains("\"armada\""), "expected armada server key");
                AssertTrue(json.Contains("http://localhost:7891/mcp"), "expected mcp url");
                AssertTrue(json.Contains("\"http\""), "expected http transport");
            }));

            cases.Add(Case("mux_config_is_the_exact_document_mux_reads", "Mux config is the exact servers document mux reads, with a bearer auth object", TestTags.Positive, () =>
            {
                // Mux ignores auth fields it does not know and then connects with no credential, so the
                // document is pinned field for field: auth.type "bearer" and auth.bearerToken.
                string json = ArmadaMcpConfigBuilder.BuildMuxServersJson(7891, "ARMADA_MCP_TOKEN");
                string expected = "{\"servers\":[{\"name\":\"armada\",\"transport\":\"http\",\"url\":\"http://localhost:7891\",\"mcpPath\":\"/mcp\","
                    + "\"auth\":{\"type\":\"bearer\",\"bearerToken\":\"${ARMADA_MCP_TOKEN}\"}}]}";
                AssertEqual(expected, System.Text.Json.Nodes.JsonNode.Parse(json)!.ToJsonString(), "the Mux servers document");
            }));

            // ---- Planner: Claude Code ----
            cases.Add(Case("plan_claude_injects_strict_mcp", "Claude plan injects strict MCP config + scoped file", TestTags.Positive, () =>
            {
                CaptainLaunchIsolationPlan plan = CaptainLaunchIsolationPlanner.Plan(AgentRuntimeEnum.ClaudeCode, 7891, scoped, missionCredential);
                AssertFalse(plan.IsEmpty, "expected a non-empty plan");
                AssertTrue(plan.ExtraArguments.Contains("--strict-mcp-config"), "expected --strict-mcp-config");
                AssertTrue(plan.ExtraArguments.Contains("--mcp-config"), "expected --mcp-config");
                AssertTrue(plan.ExtraArguments.Contains("--setting-sources"), "expected --setting-sources");
                AssertTrue(plan.ExtraArguments.Contains("project,local"), "expected project,local source list");
                AssertEqual(1, plan.FilesToWrite.Count);
                AssertEqual("armada-mcp.json", plan.FilesToWrite[0].RelativePath);
                // The --mcp-config value must be the absolute path to the scoped file.
                int idx = plan.ExtraArguments.IndexOf("--mcp-config");
                AssertEqual(Path.Combine(scoped, "armada-mcp.json"), plan.ExtraArguments[idx + 1]);
            }));

            // ---- Planner: Codex ----
            cases.Add(Case("plan_codex_injects_http_override", "Codex plan injects the local MCP URL and sets CODEX_HOME only to the account home", TestTags.Positive, () =>
            {
                CaptainLaunchIsolationPlan plan = CaptainLaunchIsolationPlanner.Plan(AgentRuntimeEnum.Codex, 7891, scoped, missionCredential);
                AssertFalse(plan.IsEmpty, "expected a non-empty plan");
                AssertEqual(4, plan.ExtraArguments.Count);
                AssertEqual("-c", plan.ExtraArguments[0]);
                AssertTrue(plan.ExtraArguments[1].Contains("http://localhost:7891/mcp"), "expected local MCP URL override");
                AssertEqual("-c", plan.ExtraArguments[2]);
                AssertEqual("mcp_servers.armada.bearer_token_env_var=\"" + McpLaunchCredential.EnvironmentVariable + "\"", plan.ExtraArguments[3], "Codex reads the scoped credential from the launch variable");
                AssertFalse(plan.EnvironmentOverrides.ContainsKey("CODEX_HOME"), "a captain with no account keeps the shared login");
                AssertEqual(0, plan.FilesToWrite.Count);

                string home = Path.Combine(Path.GetTempPath(), "armada-isolation-codex-home-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(home);
                try
                {
                    File.WriteAllText(Path.Combine(home, "auth.json"), "{}");
                    Armada.Core.Models.Captain captain = new Armada.Core.Models.Captain("codex-account", AgentRuntimeEnum.Codex);
                    Armada.Core.Settings.UsageAccountSettings account = new Armada.Core.Settings.UsageAccountSettings { Id = "codex-second", Runtime = AgentRuntimeEnum.Codex, HomeDirectory = home };
                    CaptainLaunchIsolationPlanner.ApplyAccount(plan, captain, account);
                    AssertEqual(home, plan.EnvironmentOverrides["CODEX_HOME"], "an account captain launches in the account home");
                    AssertFalse(String.Equals(scoped, plan.EnvironmentOverrides["CODEX_HOME"], StringComparison.Ordinal), "CODEX_HOME must never be the empty scoped directory");
                    // The MCP credential reaches Codex as a command-line override naming the variable, so it
                    // holds whichever home the account selects, and the account switch leaves it in place.
                    AssertEqual(4, plan.ExtraArguments.Count);
                    AssertEqual("mcp_servers.armada.bearer_token_env_var=\"" + McpLaunchCredential.EnvironmentVariable + "\"", plan.ExtraArguments[3], "an account launch still references the MCP credential by name");
                    AssertEqual(missionSessionToken, plan.EnvironmentOverrides[McpLaunchCredential.EnvironmentVariable], "an account launch still carries the mission scoped credential");
                }
                finally
                {
                    Directory.Delete(home, true);
                }
            }));

            // ---- Planner: Gemini / Cursor (HOME override) ----
            cases.Add(Case("plan_gemini_overrides_home", "Gemini plan overrides HOME + writes settings.json", TestTags.Positive, () =>
            {
                CaptainLaunchIsolationPlan plan = CaptainLaunchIsolationPlanner.Plan(AgentRuntimeEnum.Gemini, 7891, scoped, missionCredential);
                AssertTrue(plan.EnvironmentOverrides.ContainsKey("HOME"), "expected HOME override");
                AssertTrue(plan.EnvironmentOverrides.ContainsKey("USERPROFILE"), "expected USERPROFILE override");
                AssertEqual(Path.Combine(".gemini", "settings.json"), plan.FilesToWrite[0].RelativePath);
            }));

            cases.Add(Case("plan_cursor_overrides_home", "Cursor plan overrides HOME + writes mcp.json", TestTags.Positive, () =>
            {
                CaptainLaunchIsolationPlan plan = CaptainLaunchIsolationPlanner.Plan(AgentRuntimeEnum.Cursor, 7891, scoped, missionCredential);
                AssertTrue(plan.EnvironmentOverrides.ContainsKey("HOME"), "expected HOME override");
                AssertEqual(Path.Combine(".cursor", "mcp.json"), plan.FilesToWrite[0].RelativePath);
            }));

            // ---- Planner: OpenCode ----
            cases.Add(Case("plan_opencode_writes_mcp_config", "OpenCode plan writes an Armada MCP config", TestTags.Positive, () =>
            {
                CaptainLaunchIsolationPlan plan = CaptainLaunchIsolationPlanner.Plan(AgentRuntimeEnum.OpenCode, 7891, scoped, missionCredential);
                AssertFalse(plan.IsEmpty, "expected a non-empty plan");
                AssertEqual("opencode.json", plan.FilesToWrite[0].RelativePath);
                AssertTrue(plan.FilesToWrite[0].Contents.Contains("\"armada\"", StringComparison.Ordinal), "expected Armada MCP entry");
            }));

            // ---- Planner: Mux ----
            cases.Add(Case("plan_mux_delivers_strict_mcp_config", "Mux plan passes its own servers file through --mcp-config in strict mode and leaves the config directory to the captain", TestTags.Positive, () =>
            {
                // `mux print` loads MCP servers only from --mcp-config, and --config-dir outranks MUX_CONFIG_DIR, so a
                // scoped config directory would neither deliver the server nor survive a captain's own directory.
                CaptainLaunchIsolationPlan plan = CaptainLaunchIsolationPlanner.Plan(AgentRuntimeEnum.Mux, 7891, scoped, missionCredential);
                AssertEqual(MuxCommandBuilder.ScopedMcpConfigFileName, plan.FilesToWrite[0].RelativePath);
                AssertTrue(plan.FilesToWrite[0].Contents.Contains("\"armada\"", StringComparison.Ordinal), "expected Armada MCP entry");
                List<string> expected = new List<string> { MuxCommandBuilder.McpConfigFlag, Path.Combine(scoped, MuxCommandBuilder.ScopedMcpConfigFileName), MuxCommandBuilder.StrictMcpConfigFlag };
                AssertEqual(String.Join(" ", expected), String.Join(" ", plan.ExtraArguments), "Mux MCP arguments");
                AssertFalse(plan.EnvironmentOverrides.ContainsKey(MuxCommandBuilder.ConfigDirectoryEnvironmentVariable), "the plan does not replace the captain's config directory");
                AssertFalse(plan.ExtraArguments.Contains(MuxCommandBuilder.ConfigDirectoryFlag), "the plan does not pass a config directory");
            }));

            // ---- MCP credential per launch kind ----
            cases.Add(Case("plan_carries_scoped_credential_by_reference", "A mission plan carries the mission owner's scoped session token, never the launch credential, and a chat plan carries the caller session token, each by name", TestTags.Positive, () =>
            {
                // A MISSION launch carries the mission owner's own scoped session token in the launch variable,
                // which the endpoint scopes to that owner. The admiral launch credential (global admin) is
                // never carried in the environment, files or arguments, so a mission never gains global admin.
                string launchToken = McpLaunchCredential.Token;
                foreach (AgentRuntimeEnum runtime in new[] { AgentRuntimeEnum.ClaudeCode, AgentRuntimeEnum.Codex, AgentRuntimeEnum.Gemini, AgentRuntimeEnum.Cursor, AgentRuntimeEnum.OpenCode, AgentRuntimeEnum.Mux })
                {
                    CaptainLaunchIsolationPlan plan = CaptainLaunchIsolationPlanner.Plan(runtime, 7891, scoped, missionCredential);
                    AssertTrue(plan.EnvironmentOverrides.TryGetValue(McpLaunchCredential.EnvironmentVariable, out string? carried), runtime + " carries the mission credential in the launch variable");
                    AssertEqual(missionSessionToken, carried, runtime + " carries the mission owner's scoped session token");
                    AssertFalse(plan.EnvironmentOverrides.ContainsValue(launchToken), runtime + " never carries the admiral launch credential value");
                    foreach (IsolationConfigFile file in plan.FilesToWrite)
                    {
                        AssertFalse(file.Contents.Contains(missionSessionToken, StringComparison.Ordinal), runtime + " never writes the credential value into " + file.RelativePath);
                        AssertFalse(file.Contents.Contains(launchToken, StringComparison.Ordinal), runtime + " never writes the launch credential value into " + file.RelativePath);
                    }
                    foreach (string argument in plan.ExtraArguments)
                    {
                        AssertFalse(argument.Contains(missionSessionToken, StringComparison.Ordinal), runtime + " never passes the credential value as an argument");
                        AssertFalse(argument.Contains(launchToken, StringComparison.Ordinal), runtime + " never passes the launch credential value as an argument");
                    }
                }

                // A CHAT launch carries the authenticated caller's own session token, referenced by the chat
                // variable, and the admiral launch credential never enters its environment, files or arguments.
                string callerSessionToken = "caller-session-token-abc123";
                foreach (AgentRuntimeEnum runtime in new[] { AgentRuntimeEnum.ClaudeCode, AgentRuntimeEnum.Codex, AgentRuntimeEnum.Gemini, AgentRuntimeEnum.Cursor, AgentRuntimeEnum.OpenCode, AgentRuntimeEnum.Mux })
                {
                    CaptainLaunchIsolationPlan chatPlan = CaptainLaunchIsolationPlanner.Plan(runtime, 7891, scoped, McpCredentialReference.ForChat(callerSessionToken));
                    AssertTrue(chatPlan.EnvironmentOverrides.TryGetValue(McpCredentialReference.ChatEnvironmentVariable, out string? chatCarried), runtime + " carries the caller session token in the chat variable");
                    AssertEqual(callerSessionToken, chatCarried, runtime + " carries the caller's own session token");
                    AssertFalse(chatPlan.EnvironmentOverrides.ContainsKey(McpLaunchCredential.EnvironmentVariable), runtime + " never sets the launch credential variable for chat");
                    AssertFalse(chatPlan.EnvironmentOverrides.ContainsValue(launchToken), runtime + " never carries the launch credential value for chat");
                    foreach (IsolationConfigFile file in chatPlan.FilesToWrite)
                    {
                        AssertFalse(file.Contents.Contains(McpLaunchCredential.EnvironmentVariable, StringComparison.Ordinal), runtime + " never references the launch variable in " + file.RelativePath);
                        AssertFalse(file.Contents.Contains(callerSessionToken, StringComparison.Ordinal), runtime + " never writes the caller token value into " + file.RelativePath);
                        AssertTrue(file.Contents.Contains(McpCredentialReference.ChatEnvironmentVariable, StringComparison.Ordinal), runtime + " references the chat variable in " + file.RelativePath);
                    }
                    foreach (string argument in chatPlan.ExtraArguments)
                    {
                        AssertFalse(argument.Contains(McpLaunchCredential.EnvironmentVariable, StringComparison.Ordinal), runtime + " never references the launch variable in an argument");
                        AssertFalse(argument.Contains(callerSessionToken, StringComparison.Ordinal), runtime + " never passes the caller token value as an argument");
                    }
                }

                // An unauthenticated chat launch names the chat variable but leaves it unset, so the runtime
                // presents no credential and the endpoint refuses it.
                CaptainLaunchIsolationPlan unauth = CaptainLaunchIsolationPlanner.Plan(AgentRuntimeEnum.ClaudeCode, 7891, scoped, McpCredentialReference.ChatUnauthenticated);
                AssertFalse(unauth.EnvironmentOverrides.ContainsKey(McpCredentialReference.ChatEnvironmentVariable), "An unauthenticated chat launch sets no token value");
                AssertFalse(unauth.EnvironmentOverrides.ContainsKey(McpLaunchCredential.EnvironmentVariable), "An unauthenticated chat launch never sets the launch credential");
                AssertTrue(unauth.FilesToWrite[0].Contents.Contains(McpCredentialReference.ChatEnvironmentVariable, StringComparison.Ordinal), "An unauthenticated chat launch still references the chat variable");

                AssertTrue(CaptainLaunchIsolationPlanner.Plan(AgentRuntimeEnum.ClaudeCode, 7891, scoped, missionCredential).FilesToWrite[0].Contents.Contains("${" + McpLaunchCredential.EnvironmentVariable + "}", StringComparison.Ordinal), "Claude Code references the variable");
                AssertTrue(CaptainLaunchIsolationPlanner.Plan(AgentRuntimeEnum.Gemini, 7891, scoped, missionCredential).FilesToWrite[0].Contents.Contains("${" + McpLaunchCredential.EnvironmentVariable + "}", StringComparison.Ordinal), "Gemini references the variable");
                AssertTrue(CaptainLaunchIsolationPlanner.Plan(AgentRuntimeEnum.Cursor, 7891, scoped, missionCredential).FilesToWrite[0].Contents.Contains("${env:" + McpLaunchCredential.EnvironmentVariable + "}", StringComparison.Ordinal), "Cursor references the variable");
                AssertTrue(CaptainLaunchIsolationPlanner.Plan(AgentRuntimeEnum.OpenCode, 7891, scoped, missionCredential).FilesToWrite[0].Contents.Contains("{env:" + McpLaunchCredential.EnvironmentVariable + "}", StringComparison.Ordinal), "OpenCode references the variable");

                // Mux reads an HTTP server's credential from its auth object and expands ${VAR} in the token.
                System.Text.Json.Nodes.JsonObject muxServer = System.Text.Json.Nodes.JsonNode.Parse(
                    CaptainLaunchIsolationPlanner.Plan(AgentRuntimeEnum.Mux, 7891, scoped, missionCredential).FilesToWrite[0].Contents)!["servers"]!.AsArray()[0]!.AsObject();
                AssertEqual("bearer", muxServer["auth"]?["type"]?.GetValue<string>(), "Mux presents the scoped credential as a bearer token");
                AssertEqual("${" + McpLaunchCredential.EnvironmentVariable + "}", muxServer["auth"]?["bearerToken"]?.GetValue<string>(), "Mux references the variable");
            }));

            cases.Add(Case("launch_plan_carries_credential_for_dock_config_runtimes", "With dock MCP delivery enabled every runtime launches with the credential its dock configuration references", TestTags.Positive, () =>
            {
                // Dock seeding writes Cursor, Gemini and OpenCode MCP configs that reference the credential
                // variable, so those captains need it in their environment even without a scoped config.
                foreach (AgentRuntimeEnum runtime in new[] { AgentRuntimeEnum.Cursor, AgentRuntimeEnum.Gemini, AgentRuntimeEnum.OpenCode })
                {
                    CaptainLaunchIsolationPlan seeded = CaptainLaunchIsolationPlanner.PlanForLaunch(runtime, true, 7891, scoped, missionCredential);
                    AssertTrue(seeded.EnvironmentOverrides.TryGetValue(McpLaunchCredential.EnvironmentVariable, out string? value) && value == missionSessionToken,
                        runtime + " launches with the mission credential its dock configuration references");
                    AssertFalse(seeded.EnvironmentOverrides.ContainsValue(McpLaunchCredential.Token), runtime + " never carries the admiral launch credential value");
                    AssertEqual(0, seeded.FilesToWrite.Count, runtime + " keeps its dock configuration instead of a scoped one");
                    AssertEqual(0, seeded.ExtraArguments.Count, runtime + " gets no scoped arguments");
                    AssertFalse(seeded.EnvironmentOverrides.ContainsKey("HOME"), runtime + " keeps its own home");

                    CaptainLaunchIsolationPlan unseeded = CaptainLaunchIsolationPlanner.PlanForLaunch(runtime, false, 7891, scoped, missionCredential);
                    AssertTrue(unseeded.IsEmpty, runtime + " gets no credential when MCP delivery is disabled");
                }

                foreach (AgentRuntimeEnum runtime in new[] { AgentRuntimeEnum.ClaudeCode, AgentRuntimeEnum.Codex })
                {
                    CaptainLaunchIsolationPlan seeded = CaptainLaunchIsolationPlanner.PlanForLaunch(runtime, true, 7891, scoped, missionCredential);
                    AssertTrue(seeded.ExtraArguments.Count > 0, runtime + " keeps its scoped MCP arguments");
                    AssertEqual(missionSessionToken, seeded.EnvironmentOverrides[McpLaunchCredential.EnvironmentVariable], runtime + " carries the mission scoped credential");
                    AssertFalse(seeded.EnvironmentOverrides.ContainsValue(McpLaunchCredential.Token), runtime + " never carries the admiral launch credential value");
                    AssertTrue(CaptainLaunchIsolationPlanner.PlanForLaunch(runtime, false, 7891, scoped, missionCredential).IsEmpty, runtime + " gets an empty plan when MCP delivery is disabled");
                }

                // A mission launch whose owner did not resolve names the launch variable but leaves it unset,
                // so the runtime presents no credential and the endpoint refuses it, never global admin.
                foreach (AgentRuntimeEnum runtime in new[] { AgentRuntimeEnum.Cursor, AgentRuntimeEnum.ClaudeCode })
                {
                    CaptainLaunchIsolationPlan unresolved = CaptainLaunchIsolationPlanner.PlanForLaunch(runtime, true, 7891, scoped, McpCredentialReference.MissionUnresolvedOwner);
                    AssertFalse(unresolved.EnvironmentOverrides.ContainsKey(McpLaunchCredential.EnvironmentVariable), runtime + " sets no credential value for an unresolved mission owner");
                    AssertFalse(unresolved.EnvironmentOverrides.ContainsValue(McpLaunchCredential.Token), runtime + " never falls back to the admiral launch credential");
                }
            }));

            cases.Add(Case("launch_credential_matches_only_itself", "The launch credential matches only its own value", TestTags.Negative, () =>
            {
                AssertTrue(McpLaunchCredential.Matches(McpLaunchCredential.Token), "own token");
                AssertFalse(McpLaunchCredential.Matches(null), "missing");
                AssertFalse(McpLaunchCredential.Matches(""), "empty");
                AssertFalse(McpLaunchCredential.Matches(McpLaunchCredential.Token + "x"), "longer");
                AssertFalse(McpLaunchCredential.Matches("armada-launch-" + new string('0', 64)), "same shape, different value");
            }));

            // ---- Negative cases ----
            cases.Add(Case("plan_empty_for_invalid_port", "Invalid/zero port yields an empty plan", TestTags.Negative, () =>
            {
                AssertTrue(CaptainLaunchIsolationPlanner.Plan(AgentRuntimeEnum.ClaudeCode, 0, scoped, missionCredential).IsEmpty, "port 0 should be empty");
                AssertTrue(CaptainLaunchIsolationPlanner.Plan(AgentRuntimeEnum.ClaudeCode, -5, scoped, missionCredential).IsEmpty, "negative port should be empty");
                AssertTrue(CaptainLaunchIsolationPlanner.Plan(AgentRuntimeEnum.ClaudeCode, 70000, scoped, missionCredential).IsEmpty, "out-of-range port should be empty");
            }));

            cases.Add(Case("plan_empty_for_missing_scoped_dir", "Missing scoped directory yields an empty plan", TestTags.Negative, () =>
            {
                AssertTrue(CaptainLaunchIsolationPlanner.Plan(AgentRuntimeEnum.ClaudeCode, 7891, "", missionCredential).IsEmpty, "empty scoped dir should be empty");
                AssertTrue(CaptainLaunchIsolationPlanner.Plan(AgentRuntimeEnum.ClaudeCode, 7891, "   ", missionCredential).IsEmpty, "whitespace scoped dir should be empty");
            }));

            cases.Add(Case("mux_tool_probe_sends_the_declared_credential", "The Mux tool probe sends the credential the Mux server file declares", TestTags.Positive, () =>
            {
                const string variable = "ARMADA_ISOLATION_PROBE_KEY";
                string? prior = Environment.GetEnvironmentVariable(variable);
                Environment.SetEnvironmentVariable(variable, "probe-secret");
                try
                {
                    string file = "{\"servers\":["
                        + "{\"name\":\"bearer\",\"transport\":\"http\",\"url\":\"http://localhost:7891\",\"mcpPath\":\"/mcp\",\"auth\":{\"type\":\"bearer\",\"bearerToken\":\"${" + variable + "}\"}},"
                        + "{\"name\":\"keyed\",\"transport\":\"http\",\"url\":\"http://localhost:7891\",\"auth\":{\"type\":\"api_key\",\"apiKeyValue\":\"${" + variable + "}\",\"apiKeyHeader\":\"X-Api-Key\"}},"
                        + "{\"name\":\"open\",\"transport\":\"http\",\"url\":\"http://localhost:7891\",\"auth\":{\"type\":\"none\"}},"
                        + "{\"name\":\"unknown-fields\",\"transport\":\"http\",\"url\":\"http://localhost:7891\",\"auth\":{\"scheme\":\"bearer_token\",\"token\":\"${" + variable + "}\"}}]}";

                    IReadOnlyDictionary<string, string> bearer = Armada.Server.CaptainRuntimeToolCatalogService.BuildMuxProbeHeaders(file, "bearer");
                    AssertTrue(bearer.TryGetValue("Authorization", out string? authorization), "a bearer server probes with an Authorization header");
                    AssertEqual("Bearer probe-secret", authorization, "the token reference expands from the environment");

                    IReadOnlyDictionary<string, string> keyed = Armada.Server.CaptainRuntimeToolCatalogService.BuildMuxProbeHeaders(file, "keyed");
                    AssertTrue(keyed.TryGetValue("X-Api-Key", out string? key), "an apikey server probes with its named header, reading the type as Mux does");
                    AssertEqual("probe-secret", key, "the key reference expands from the environment");

                    AssertEqual(0, Armada.Server.CaptainRuntimeToolCatalogService.BuildMuxProbeHeaders(file, "open").Count, "a server without auth sends no credential");
                    AssertEqual(0, Armada.Server.CaptainRuntimeToolCatalogService.BuildMuxProbeHeaders(file, "unknown-fields").Count,
                        "auth fields Mux does not read send no credential, so the probe reports what Mux would actually send");
                }
                finally
                {
                    Environment.SetEnvironmentVariable(variable, prior);
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: "Services.CaptainLaunchIsolation",
                displayName: "Captain Launch Isolation",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.CaptainLaunchIsolation",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) =>
                {
                    body();
                    return Task.CompletedTask;
                },
                tags: new List<string> { tag });
        }

        #endregion
    }
}
