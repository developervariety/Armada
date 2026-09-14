namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Threading.Tasks;
    using System.Xml.Linq;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Armada.Helm;
    using Armada.Helm.Commands;
    using Armada.Helm.Infrastructure;
    using Spectre.Console;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Behavior checks for Helm help, request and response serialization, and settings loading.
    /// </summary>
    public sealed class HelmCliSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the registered Helm CLI behavior cases.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>
            {
                CaseAsync("every_command_help_renders_without_server", "Helm per-command help renders for every command", TestTags.Positive, async () =>
                {
                    List<string[]> paths = ReadCommandPaths();
                    AssertTrue(paths.Count > 50, "The command model must list every registered command; found " + paths.Count + ".");
                    bool settingsExisted = File.Exists(ArmadaSettings.DefaultSettingsPath);
                    List<string> failures = new List<string>();

                    foreach (string[] path in paths)
                    {
                        List<string[]> invocations = new List<string[]>
                        {
                            path.Concat(new[] { "--help" }).ToArray(),
                            new[] { "help" }.Concat(path).ToArray()
                        };

                        foreach (string[] invocation in invocations)
                        {
                            StringWriter writer = new StringWriter();
                            int exit = Program.Run(invocation, CreateConsole(writer));
                            string output = writer.ToString();
                            if (exit != 0 || !output.Contains("USAGE", StringComparison.Ordinal))
                            {
                                failures.Add(String.Join(" ", invocation) + " -> exit " + exit + ": " + FirstLine(output));
                            }
                        }
                    }

                    AssertTrue(failures.Count == 0, "Help must render for every command:\n" + String.Join("\n", failures));
                    AssertFalse(EmbeddedServer.IsRunning, "Help must not start an embedded Admiral.");
                    AssertEqual(settingsExisted, File.Exists(ArmadaSettings.DefaultSettingsPath), "Help must not create or remove settings.");
                    await Task.CompletedTask;
                }),
                CaseAsync("response_enums_deserialize_from_live_admiral", "Helm reads named enum values the Admiral writes", TestTags.Positive, async () =>
                {
                    E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                    using (StringContent body = new StringContent("{\"message\":\"help\"}", Encoding.UTF8, "application/json"))
                    {
                        HttpResponseMessage response = await fx.AuthClient.PostAsync("/api/v1/ask", body);
                        string json = await response.Content.ReadAsStringAsync();
                        AssertTrue(response.IsSuccessStatusCode, "Ask must succeed: " + json);
                        AssertContains("\"Kind\":\"Help\"", json.Replace(" ", String.Empty), "The Admiral writes the enum name.");

                        AskResponse? parsed = JsonSerializer.Deserialize<AskResponse>(json, HelmJson.Options);
                        AssertNotNull(parsed);
                        AssertEqual(AskResponseKindEnum.Help, parsed!.Kind);
                    }
                }),
                CaseAsync("request_enums_serialize_as_names_accepted_by_admiral", "Helm writes named enum values the Admiral stores", TestTags.Positive, async () =>
                {
                    ObjectiveUpsertRequest request = new ObjectiveUpsertRequest
                    {
                        Title = "helm enum contract " + Guid.NewGuid().ToString("N"),
                        Status = ObjectiveStatusEnum.Blocked,
                        Priority = ObjectivePriorityEnum.P0
                    };

                    string json = JsonSerializer.Serialize(request, HelmJson.Options);
                    AssertContains("\"Blocked\"", json, "Status must use its API name.");
                    AssertContains("\"P0\"", json, "Priority must use its API name.");

                    E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                    using (StringContent body = new StringContent(json, Encoding.UTF8, "application/json"))
                    {
                        HttpResponseMessage response = await fx.AuthClient.PostAsync("/api/v1/objectives", body);
                        string created = await response.Content.ReadAsStringAsync();
                        AssertTrue(response.IsSuccessStatusCode, "Objective create must succeed: " + created);
                        Objective? objective = JsonSerializer.Deserialize<Objective>(created, HelmJson.Options);
                        AssertNotNull(objective);
                        AssertEqual(ObjectiveStatusEnum.Blocked, objective!.Status);
                        AssertEqual(ObjectivePriorityEnum.P0, objective.Priority);
                    }
                }),
                CaseAsync("embedded_admiral_reads_saved_settings", "Embedded Admiral reads the settings file Helm saves", TestTags.Positive, async () =>
                {
                    string root = TestTemp.NewDirectory("helm-settings");
                    string path = Path.Combine(root, "settings.json");
                    try
                    {
                        ArmadaSettings saved = new ArmadaSettings();
                        saved.DataDirectory = root;
                        saved.AdmiralPort = 17_345;
                        saved.McpPort = 17_346;
                        saved.ApiKey = "saved-bearer-" + Guid.NewGuid().ToString("N");
                        saved.BranchCleanupPolicy = BranchCleanupPolicyEnum.None;
                        await saved.SaveAsync(path);

                        ArmadaSettings loaded = await EmbeddedServer.LoadSettingsAsync(path);

                        AssertEqual(17_345, loaded.AdmiralPort, "Embedded start must bind the saved Admiral port.");
                        AssertEqual(17_346, loaded.McpPort, "Embedded start must bind the saved MCP port.");
                        AssertEqual(saved.ApiKey, loaded.ApiKey, "Embedded start must accept the bearer key Helm sends.");
                        AssertEqual(BranchCleanupPolicyEnum.None, loaded.BranchCleanupPolicy);
                        AssertEqual(Path.GetFullPath(root), Path.GetFullPath(loaded.DataDirectory), "Embedded start must use the saved data directory.");
                    }
                    finally
                    {
                        if (Directory.Exists(root)) Directory.Delete(root, true);
                    }
                }),
                CaseAsync("mcp_client_payloads_reach_the_served_mcp_endpoint", "Every Helm MCP client payload reaches the served MCP endpoint", TestTags.Positive, async () =>
                {
                    E2EServerFixture fx = await E2EServerFixture.AcquireAsync(this);
                    List<McpConfigHelper.ConfigTarget> targets = McpConfigHelper.BuildTargets(fx.McpPort);
                    List<string> checkedClients = new List<string>();
                    List<string> failures = new List<string>();

                    foreach (McpConfigHelper.ConfigTarget target in targets)
                    {
                        string? url = AdvertisedMcpUrl(target);
                        if (url == null)
                        {
                            AssertTrue(target.CliCommand != null && target.InstallArgs != null && target.InstallArgs.Contains("stdio"),
                                target.ClientName + " advertises no HTTP endpoint and must use the stdio bridge.");
                            continue;
                        }

                        checkedClients.Add(target.ClientName);
                        string failure = await InitializeMcpAsync(url);
                        if (failure.Length > 0) failures.Add(target.ClientName + " (" + url + "): " + failure);
                    }

                    AssertTrue(checkedClients.Contains("Claude Code") && checkedClients.Contains("Cursor") && checkedClients.Contains("Gemini CLI"),
                        "Claude Code, Cursor and Gemini CLI payloads must be checked; checked: " + String.Join(", ", checkedClients));
                    AssertTrue(failures.Count == 0, "MCP client payloads must reach the served endpoint:\n" + String.Join("\n", failures));
                }),
                CaseAsync("file_based_mcp_clients_install_and_remove_idempotently", "Claude Code and Cursor MCP entries install and remove idempotently", TestTags.Positive, async () =>
                {
                    string root = TestTemp.NewDirectory("helm-mcp-clients");
                    try
                    {
                        foreach (McpConfigHelper.ConfigTarget built in McpConfigHelper.BuildTargets(7891).Where(target => target.ClientName == "Claude Code" || target.ClientName == "Cursor"))
                        {
                            string path = Path.Combine(root, built.ClientName.Replace(" ", "-") + ".json");
                            string original = "{\n  // user settings stay\n  \"theme\": \"dark\",\n  \"mcpServers\": {\n    \"other\": { \"url\": \"http://example.test/mcp\" }\n  }\n}\n";
                            await File.WriteAllTextAsync(path, original);
                            McpConfigHelper.ConfigTarget target = built with { FilePath = path };

                            McpConfigHelper.ApplyResult first = await McpConfigHelper.InstallTargetAsync(target);
                            McpConfigHelper.ApplyResult second = await McpConfigHelper.InstallTargetAsync(target);
                            AssertTrue(first.Changed, built.ClientName + " first install must write the entry.");
                            AssertFalse(second.Changed, built.ClientName + " second install must be idempotent.");
                            string installed = await File.ReadAllTextAsync(path);
                            AssertContains("// user settings stay", installed, built.ClientName + " install must keep comments.");
                            AssertContains("\"other\"", installed, built.ClientName + " install must keep other servers.");
                            AssertContains(McpConfigHelper.GetMcpUrl(7891), installed, built.ClientName + " install must write the MCP URL.");

                            McpConfigHelper.ApplyResult removed = await McpConfigHelper.RemoveTargetAsync(target);
                            McpConfigHelper.ApplyResult removedAgain = await McpConfigHelper.RemoveTargetAsync(target);
                            AssertTrue(removed.Changed, built.ClientName + " remove must delete the entry.");
                            AssertFalse(removedAgain.Changed, built.ClientName + " second remove must be idempotent.");
                            string final = await File.ReadAllTextAsync(path);
                            AssertFalse(final.Contains("armada", StringComparison.OrdinalIgnoreCase), built.ClientName + " remove must delete only the Armada entry.");
                            AssertContains("// user settings stay", final, built.ClientName + " remove must keep comments.");
                            AssertContains("\"other\"", final, built.ClientName + " remove must keep other servers.");
                        }
                    }
                    finally
                    {
                        if (Directory.Exists(root)) Directory.Delete(root, true);
                    }
                }),
                CaseAsync("settings_initialize_once_and_preserve_existing_file", "Helm initializes settings once and keeps saved values", TestTags.Positive, async () =>
                {
                    string root = TestTemp.NewDirectory("helm-settings-init");
                    string path = Path.Combine(root, "nested", "settings.json");
                    try
                    {
                        bool initialized;
                        ArmadaSettings first = HelmSettings.LoadOrInitialize(path, out initialized);
                        AssertTrue(initialized, "A missing settings file must be initialized.");
                        AssertTrue(File.Exists(path), "Initialization must write the settings file.");
                        AssertNotNull(first);

                        ArmadaSettings edited = await ArmadaSettings.LoadAsync(path);
                        edited.AdmiralPort = 17_400;
                        edited.ApiKey = "edited-bearer";
                        await edited.SaveAsync(path);
                        string savedText = await File.ReadAllTextAsync(path);

                        ArmadaSettings second = HelmSettings.LoadOrInitialize(path, out initialized);
                        AssertFalse(initialized, "An existing settings file must not be initialized again.");
                        AssertEqual(17_400, second.AdmiralPort);
                        AssertEqual("edited-bearer", second.ApiKey);
                        AssertEqual(savedText, await File.ReadAllTextAsync(path), "Loading must not rewrite existing settings.");
                    }
                    finally
                    {
                        if (Directory.Exists(root)) Directory.Delete(root, true);
                    }
                })
            };

            return new TestSuiteDescriptor("Services.HelmCli", "Helm CLI contracts", cases);
        }

        #endregion

        #region Private-Methods

        private static List<string[]> ReadCommandPaths()
        {
            StringWriter writer = new StringWriter();
            int exit = Program.Run(new[] { "cli", "xmldoc" }, CreateConsole(writer));
            if (exit != 0) throw new InvalidOperationException("The CLI command model could not be read: " + writer);

            XDocument model = XDocument.Parse(writer.ToString().Trim());
            List<string[]> paths = new List<string[]>();
            foreach (XElement command in model.Root!.Elements("Command"))
            {
                AddPaths(command, new List<string>(), paths);
            }

            return paths;
        }

        private static void AddPaths(XElement command, List<string> parent, List<string[]> paths)
        {
            List<string> path = new List<string>(parent) { (string)command.Attribute("Name")! };
            paths.Add(path.ToArray());
            foreach (XElement child in command.Elements("Command"))
            {
                AddPaths(child, path, paths);
            }
        }

        private static IAnsiConsole CreateConsole(StringWriter writer)
        {
            IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Interactive = InteractionSupport.No,
                Out = new AnsiConsoleOutput(writer)
            });
            console.Profile.Width = 4096;
            return console;
        }

        private static string? AdvertisedMcpUrl(McpConfigHelper.ConfigTarget target)
        {
            if (target.ArmadaConfig != null)
            {
                string? url = target.ArmadaConfig["url"]?.GetValue<string>();
                string? mcpPath = target.ArmadaConfig["mcpPath"]?.GetValue<string>();
                if (url == null) return null;
                return mcpPath == null ? url : url.TrimEnd('/') + mcpPath;
            }

            if (target.InstallArgs == null) return null;
            return target.InstallArgs.FirstOrDefault(argument => argument.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
        }

        private static async Task<string> InitializeMcpAsync(string url)
        {
            string request = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"helm-contract\",\"version\":\"1.0\"}}}";
            using (HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
            using (HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Post, url))
            {
                message.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
                message.Content = new StringContent(request, Encoding.UTF8, "application/json");
                HttpResponseMessage response = await client.SendAsync(message);
                string body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) return "HTTP " + (int)response.StatusCode + ": " + body;
                if (!body.Contains("protocolVersion", StringComparison.Ordinal)) return "no initialize result: " + body;
                return String.Empty;
            }
        }

        private static string FirstLine(string output)
        {
            string trimmed = output.Trim();
            int newline = trimmed.IndexOf('\n');
            return newline < 0 ? trimmed : trimmed.Substring(0, newline);
        }

        private static TestCaseDescriptor CaseAsync(string id, string name, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor("Services.HelmCli", id, name, _ => body(), new List<string> { tag });
        }

        #endregion
    }
}
