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
