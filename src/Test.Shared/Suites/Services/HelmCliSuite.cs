namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Threading;
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
                        // A real client sends the credential its entry declares, expanding the variable
                        // reference from its environment; the fixture API key stands in for that value.
                        KeyValuePair<string, string>? credential = AdvertisedMcpCredential(target, fx.ApiKey, out string credentialProblem);
                        if (credential == null)
                        {
                            failures.Add(target.ClientName + " (" + url + "): " + credentialProblem);
                            continue;
                        }

                        string failure = await InitializeMcpAsync(url, credential.Value);
                        if (failure.Length > 0) failures.Add(target.ClientName + " (" + url + "): " + failure);
                    }

                    AssertTrue(checkedClients.Contains("Claude Code") && checkedClients.Contains("Cursor") && checkedClients.Contains("Gemini CLI"),
                        "Claude Code, Cursor and Gemini CLI payloads must be checked; checked: " + String.Join(", ", checkedClients));
                    AssertTrue(failures.Count == 0, "MCP client payloads must reach the served endpoint:\n" + String.Join("\n", failures));
                }),
                CaseAsync("status_refusal_names_the_required_role", "Helm status and watch name a refused fleet status", TestTags.Negative, () =>
                {
                    string? forbidden = StatusCommand.DescribeStatusRefusal(System.Net.HttpStatusCode.Forbidden);
                    AssertNotNull(forbidden, "A 403 on the status route must produce a named message.");
                    AssertContains("global administrator", forbidden!, "The message must name the role the route requires.");
                    AssertNull(StatusCommand.DescribeStatusRefusal(System.Net.HttpStatusCode.InternalServerError), "Other failures keep their own handling.");
                    AssertNull(StatusCommand.DescribeStatusRefusal(null), "A failure without a status code keeps its own handling.");
                    return Task.CompletedTask;
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
                }),
                CaseAsync("reset_keeps_data_when_admiral_refuses_stop", "Helm reset deletes nothing when the Admiral refuses the stop request", TestTags.Negative, async () =>
                {
                    ScriptedAdmiral admiral = new ScriptedAdmiral { StopStatus = HttpStatusCode.Unauthorized };
                    await AssertResetKeepsDataAsync(admiral, "a refused stop");
                    AssertEqual(1, admiral.StopRequests, "Reset must send exactly one stop request.");
                    AssertEqual("Bearer reset-bearer", admiral.LastStopAuthorization, "The stop request must carry the Helm bearer credential.");
                }),
                CaseAsync("reset_keeps_data_while_admiral_still_answers", "Helm reset deletes nothing while the Admiral keeps answering after a stop", TestTags.Negative, async () =>
                {
                    ScriptedAdmiral admiral = new ScriptedAdmiral { StopStatus = HttpStatusCode.OK, ExitOnStop = false };
                    await AssertResetKeepsDataAsync(admiral, "a server that keeps answering");
                    AssertEqual(1, admiral.StopRequests, "Reset must send exactly one stop request.");
                }),
                CaseAsync("reset_deletes_data_after_admiral_exits", "Helm reset stops the Admiral through the stop route before deleting data", TestTags.Positive, async () =>
                {
                    string root = TestTemp.NewDirectory("helm-reset");
                    try
                    {
                        ArmadaSettings settings = ResetSettingsIn(root);
                        ScriptedAdmiral admiral = new ScriptedAdmiral { StopStatus = HttpStatusCode.OK, ExitOnStop = true };
                        using (HttpClient client = new HttpClient(admiral))
                        {
                            int exit = await ResetCommand.ResetDataAsync(settings, FastShutdown(client), CancellationToken.None);
                            AssertEqual(0, exit, "Reset must succeed once the Admiral has exited.");
                        }

                        AssertEqual(1, admiral.StopRequests, "Reset must stop the Admiral through its stop route.");
                        AssertEqual("Bearer reset-bearer", admiral.LastStopAuthorization, "The stop request must carry the Helm bearer credential.");
                        AssertTrue(admiral.UnknownRequests.Count == 0, "Reset must call only served routes: " + String.Join(", ", admiral.UnknownRequests));
                        AssertFalse(File.Exists(settings.DatabasePath), "Reset must delete the database after the Admiral exits.");
                        AssertFalse(File.Exists(Path.Combine(settings.LogDirectory, "admiral.log")), "Reset must delete the logs after the Admiral exits.");
                    }
                    finally
                    {
                        if (Directory.Exists(root)) Directory.Delete(root, true);
                    }
                }),
                CaseAsync("server_stop_fails_unless_admiral_exits", "Helm server stop exits non-zero unless the Admiral stops answering", TestTags.Negative, async () =>
                {
                    ScriptedAdmiral refused = new ScriptedAdmiral { StopStatus = HttpStatusCode.Unauthorized };
                    ScriptedAdmiral surviving = new ScriptedAdmiral { StopStatus = HttpStatusCode.OK, ExitOnStop = false };
                    ScriptedAdmiral exiting = new ScriptedAdmiral { StopStatus = HttpStatusCode.OK, ExitOnStop = true };
                    ScriptedAdmiral absent = new ScriptedAdmiral { Alive = false };

                    AssertEqual(1, await StopExitCodeAsync(refused), "A refused stop must exit non-zero.");
                    AssertEqual(1, await StopExitCodeAsync(surviving), "A server that keeps answering must exit non-zero.");
                    AssertEqual(0, await StopExitCodeAsync(exiting), "A server that exits must exit zero.");
                    AssertEqual(1, await StopExitCodeAsync(absent), "A server that was not running must exit non-zero.");
                    AssertEqual("Bearer reset-bearer", refused.LastStopAuthorization, "The stop request must carry the Helm bearer credential.");
                    AssertEqual(0, absent.StopRequests, "No stop request is sent when nothing answers.");
                }),
                CaseAsync("captain_stop_all_calls_shared_route_and_reports_counts", "Helm captain stop-all calls the server's stop-all once and reports its counts and failures", TestTags.Positive, async () =>
                {
                    ScriptedCaptainAdmiral partial = new ScriptedCaptainAdmiral
                    {
                        StopAllBody = "{\"CaptainsStopped\":2,\"CaptainsFailed\":0,\"PlanningSessionsStopped\":1,\"PlanningSessionsFailed\":0,\"RefinementSessionsStopped\":0,\"RefinementSessionsFailed\":1,\"Failures\":[{\"Kind\":\"RefinementSession\",\"Id\":\"ors_stuck\",\"Message\":\"refinement runtime did not exit\"}]}"
                    };
                    StringWriter partialOutput = new StringWriter();
                    int partialExit;
                    using (HttpClient client = new HttpClient(partial))
                    using (Armada.Core.Client.ArmadaApiClient api = new Armada.Core.Client.ArmadaApiClient(client, "http://127.0.0.1:1"))
                    {
                        partialExit = await CaptainStopAllCommand.StopAllAsync(api, CreateConsole(partialOutput), CancellationToken.None);
                    }

                    AssertTrue(partial.Requests.SequenceEqual(new[] { "POST /api/v1/captains/stop-all" }),
                        "Stop-all must make exactly one request to the shared stop-all route: " + String.Join(", ", partial.Requests));
                    string text = partialOutput.ToString();
                    AssertTrue(text.Contains("Captains stopped: 2, failed: 0", StringComparison.Ordinal), "The captain counts are reported: " + text);
                    AssertTrue(text.Contains("Planning sessions stopped: 1, failed: 0", StringComparison.Ordinal), "The planning session counts are reported: " + text);
                    AssertTrue(text.Contains("Refinement sessions stopped: 0, failed: 1", StringComparison.Ordinal), "The refinement session counts are reported: " + text);
                    AssertTrue(text.Contains("ors_stuck", StringComparison.Ordinal) && text.Contains("refinement runtime did not exit", StringComparison.Ordinal), "Each failure is named: " + text);
                    AssertEqual(1, partialExit, "A stop-all that left anything running exits non-zero.");

                    ScriptedCaptainAdmiral complete = new ScriptedCaptainAdmiral
                    {
                        StopAllBody = "{\"CaptainsStopped\":1,\"CaptainsFailed\":0,\"PlanningSessionsStopped\":0,\"PlanningSessionsFailed\":0,\"RefinementSessionsStopped\":0,\"RefinementSessionsFailed\":0,\"Failures\":[]}"
                    };
                    using (HttpClient client = new HttpClient(complete))
                    using (Armada.Core.Client.ArmadaApiClient api = new Armada.Core.Client.ArmadaApiClient(client, "http://127.0.0.1:1"))
                    {
                        AssertEqual(0, await CaptainStopAllCommand.StopAllAsync(api, CreateConsole(new StringWriter()), CancellationToken.None), "A stop-all that stopped everything exits zero.");
                    }
                }),
                CaseAsync("server_restart_does_not_start_beside_a_running_admiral", "Helm server restart refuses to start while the old Admiral keeps running", TestTags.Negative, async () =>
                {
                    ScriptedAdmiral refused = new ScriptedAdmiral { StopStatus = HttpStatusCode.Forbidden };
                    ScriptedAdmiral exiting = new ScriptedAdmiral { StopStatus = HttpStatusCode.OK, ExitOnStop = true };
                    ScriptedAdmiral absent = new ScriptedAdmiral { Alive = false };

                    AssertFalse(await RestartMayStartAsync(refused), "A refused stop must cancel the restart.");
                    AssertTrue(await RestartMayStartAsync(exiting), "A server that exits lets the restart proceed.");
                    AssertTrue(await RestartMayStartAsync(absent), "A server that was not running lets the restart proceed.");
                }),
                CaseAsync("server_start_build_step_survives_a_full_stderr_pipe", "Helm server start build steps do not block on a child that fills its stderr pipe", TestTags.Negative, async () =>
                {
                    if (OperatingSystem.IsWindows()) return;

                    // 1 MiB on stderr before anything on stdout: a reader that finishes stdout before starting stderr
                    // waits for an end of stdout that never comes, because the child is blocked writing stderr.
                    System.Diagnostics.ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo("/bin/sh");
                    startInfo.ArgumentList.Add("-c");
                    startInfo.ArgumentList.Add("head -c 1048576 /dev/zero | tr '\\0' 'e' >&2; echo done; exit 3");
                    startInfo.RedirectStandardOutput = true;
                    startInfo.RedirectStandardError = true;
                    startInfo.UseShellExecute = false;

                    Task<Armada.Core.Services.BoundedProcessResult> step = Task.Run(() => ServerStartCommand.RunBuildStepAsync(startInfo, CancellationToken.None));
                    Task winner = await Task.WhenAny(step, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
                    AssertTrue(winner == step, "The build step must return while its child writes more than a pipe buffer to stderr.");

                    Armada.Core.Services.BoundedProcessResult result = await step.ConfigureAwait(false);
                    AssertEqual(3, result.ExitCode ?? -1, "The step reports the child's exit code.");
                    AssertFalse(result.TimedOut, "The step finished on its own, not by timeout.");
                    AssertTrue(result.StandardErrorTruncated, "Stderr past the step's budget is dropped and counted, not held whole.");
                    AssertTrue(result.StandardError.EndsWith("eeee", StringComparison.Ordinal), "The end of stderr, where a build explains its failure, is kept.");
                })
            };

            return new TestSuiteDescriptor("Services.HelmCli", "Helm CLI contracts", cases);
        }

        #endregion

        #region Private-Methods

        private static async Task AssertResetKeepsDataAsync(ScriptedAdmiral admiral, string situation)
        {
            string root = TestTemp.NewDirectory("helm-reset");
            try
            {
                ArmadaSettings settings = ResetSettingsIn(root);
                using (HttpClient client = new HttpClient(admiral))
                {
                    int exit = await ResetCommand.ResetDataAsync(settings, FastShutdown(client), CancellationToken.None);
                    AssertEqual(1, exit, "Reset must fail after " + situation + ".");
                }

                AssertTrue(File.Exists(settings.DatabasePath), "Reset must keep the database after " + situation + ".");
                AssertTrue(File.Exists(Path.Combine(settings.LogDirectory, "admiral.log")), "Reset must keep the logs after " + situation + ".");
                AssertTrue(Directory.Exists(settings.DocksDirectory), "Reset must keep the docks after " + situation + ".");
                AssertTrue(Directory.Exists(settings.ReposDirectory), "Reset must keep the bare repositories after " + situation + ".");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private static ArmadaSettings ResetSettingsIn(string root)
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DataDirectory = root;
            settings.DatabasePath = Path.Combine(root, "armada.db");
            settings.LogDirectory = Path.Combine(root, "logs");
            settings.DocksDirectory = Path.Combine(root, "docks");
            settings.ReposDirectory = Path.Combine(root, "repos");
            settings.ApiKey = "reset-bearer";
            Directory.CreateDirectory(settings.LogDirectory);
            Directory.CreateDirectory(settings.DocksDirectory);
            Directory.CreateDirectory(settings.ReposDirectory);
            File.WriteAllText(settings.DatabasePath, "database");
            File.WriteAllText(Path.Combine(settings.LogDirectory, "admiral.log"), "log");
            return settings;
        }

        private static AdmiralShutdown FastShutdown(HttpClient client)
        {
            return new AdmiralShutdown(client, "http://127.0.0.1:1", "reset-bearer", 3, TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(5));
        }

        private static async Task<int> StopExitCodeAsync(ScriptedAdmiral admiral)
        {
            using (HttpClient client = new HttpClient(admiral))
            {
                return await ServerStopCommand.StopAsync(FastShutdown(client), CancellationToken.None);
            }
        }

        private static async Task<bool> RestartMayStartAsync(ScriptedAdmiral admiral)
        {
            using (HttpClient client = new HttpClient(admiral))
            {
                return await ServerRestartCommand.StopRunningServerAsync(FastShutdown(client), CancellationToken.None);
            }
        }

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

        private static KeyValuePair<string, string>? AdvertisedMcpCredential(McpConfigHelper.ConfigTarget target, string apiKey, out string problem)
        {
            List<KeyValuePair<string, string>> headers = new List<KeyValuePair<string, string>>();
            if (target.ArmadaConfig != null)
            {
                if (target.ArmadaConfig["headers"] is JsonObject declared)
                {
                    foreach (KeyValuePair<string, JsonNode?> header in declared)
                        headers.Add(new KeyValuePair<string, string>(header.Key, header.Value?.GetValue<string>() ?? String.Empty));
                }

                // Mux declares an HTTP server's credential in an auth object instead of a headers map: an
                // api_key scheme sends the key in the named header, a bearer_token scheme in Authorization.
                if (target.ArmadaConfig["auth"] is JsonObject auth)
                {
                    string scheme = auth["scheme"]?.GetValue<string>() ?? String.Empty;
                    if (scheme == "api_key")
                        headers.Add(new KeyValuePair<string, string>(auth["headerName"]?.GetValue<string>() ?? "X-API-Key", auth["key"]?.GetValue<string>() ?? String.Empty));
                    else if (scheme == "bearer_token")
                        headers.Add(new KeyValuePair<string, string>("Authorization", auth["token"]?.GetValue<string>() ?? String.Empty));
                }
            }
            else if (target.InstallArgs != null)
            {
                for (int index = 0; index + 1 < target.InstallArgs.Length; index++)
                {
                    if (target.InstallArgs[index] != "--header" && target.InstallArgs[index] != "-H") continue;
                    string[] parts = target.InstallArgs[index + 1].Split(':', 2);
                    if (parts.Length == 2) headers.Add(new KeyValuePair<string, string>(parts[0].Trim(), parts[1].Trim()));
                }
            }

            string variable = McpConfigHelper.ApiKeyEnvironmentVariable;
            string[] references = { "${" + variable + "}", "${env:" + variable + "}", "{env:" + variable + "}" };
            foreach (KeyValuePair<string, string> header in headers)
            {
                if (!references.Contains(header.Value)) continue;
                problem = String.Empty;
                return new KeyValuePair<string, string>(header.Key, apiKey);
            }

            problem = headers.Count == 0
                ? "the entry declares no credential header, so the client cannot authenticate"
                : "the entry's credential headers do not reference " + variable + ": " + String.Join(", ", headers.Select(header => header.Key + "=" + header.Value));
            return null;
        }

        private static async Task<string> InitializeMcpAsync(string url, KeyValuePair<string, string> credential)
        {
            string request = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"helm-contract\",\"version\":\"1.0\"}}}";
            using (HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
            using (HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Post, url))
            {
                message.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
                message.Headers.TryAddWithoutValidation(credential.Key, credential.Value);
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

        #region Private-Classes

        /// <summary>
        /// In-process stand-in for an Admiral's captain routes: it records every request, answers the stop-all route
        /// with a scripted result, lists two captains, and accepts a per-captain stop.
        /// </summary>
        private sealed class ScriptedCaptainAdmiral : HttpMessageHandler
        {
            public string StopAllBody { get; set; } = "{}";

            public List<string> Requests { get; } = new List<string>();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string path = request.RequestUri!.AbsolutePath;
                Requests.Add(request.Method + " " + path);
                if (request.Method == HttpMethod.Post && path == "/api/v1/captains/stop-all")
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(StopAllBody, Encoding.UTF8, "application/json") });
                if (request.Method == HttpMethod.Get && path == "/api/v1/captains")
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"Objects\":[{\"Id\":\"cpt_one\",\"Name\":\"one\"},{\"Id\":\"cpt_two\",\"Name\":\"two\"}]}", Encoding.UTF8, "application/json") });
                if (request.Method == HttpMethod.Post && path.StartsWith("/api/v1/captains/", StringComparison.Ordinal) && path.EndsWith("/stop", StringComparison.Ordinal))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"Status\":\"stopped\"}", Encoding.UTF8, "application/json") });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        }

        /// <summary>
        /// In-process stand-in for an Admiral: its health route answers while it is alive, its stop route answers
        /// with a scripted status, and once it has exited every connection fails the way a closed port does.
        /// </summary>
        private sealed class ScriptedAdmiral : HttpMessageHandler
        {
            public bool Alive { get; set; } = true;

            public HttpStatusCode StopStatus { get; set; } = HttpStatusCode.OK;

            public bool ExitOnStop { get; set; } = true;

            public int StopRequests { get; private set; } = 0;

            public string? LastStopAuthorization { get; private set; } = null;

            public List<string> UnknownRequests { get; } = new List<string>();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (!Alive) throw new HttpRequestException("Connection refused");

                string path = request.RequestUri!.AbsolutePath;
                if (request.Method == HttpMethod.Get && path == AdmiralShutdown.HealthPath)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

                if (request.Method == HttpMethod.Post && path == AdmiralShutdown.StopPath)
                {
                    StopRequests++;
                    LastStopAuthorization = request.Headers.Authorization?.ToString();
                    bool accepted = (int)StopStatus >= 200 && (int)StopStatus < 300;
                    if (accepted && ExitOnStop) Alive = false;
                    return Task.FromResult(new HttpResponseMessage(StopStatus) { Content = new StringContent(accepted ? "{\"Status\":\"shutting_down\"}" : "{\"Message\":\"Authentication required\"}") });
                }

                UnknownRequests.Add(request.Method + " " + path);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        }

        #endregion
    }
}
