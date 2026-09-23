namespace Armada.Test.Runtimes.Suites
{
    using System.Diagnostics;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Runtimes;
    using Armada.Test.Common;
    using SyslogLogging;

    public class MuxRuntimeTests : TestSuite
    {
        public override string Name => "Mux Runtime Tests";

        private sealed class InspectableMuxRuntime : MuxRuntime
        {
            public InspectableMuxRuntime(LoggingModule logging) : base(logging)
            {
            }

            public List<string> Args(string workingDirectory, string prompt, string? model = null, string? finalMessageFilePath = null, Captain? captain = null) =>
                BuildArguments(workingDirectory, prompt, model, finalMessageFilePath, captain);

            public bool UsesPromptStdin() => UsePromptStdin;

            public void FeedUsage(int processId, string line) => HandleRawOutputLine(processId, line);

            public string TransformLine(string line) => TransformOutputLine(line);

            public string? AppliedEnvironmentValue(Captain captain, string key)
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                ApplyEnvironment(startInfo, captain);

                if (startInfo.Environment.ContainsKey(key))
                {
                    return startInfo.Environment[key];
                }

                return null;
            }
        }

        private static InspectableMuxRuntime CreateRuntime()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new InspectableMuxRuntime(logging);
        }

        private void AssertFlagValue(List<string> args, string flag, string expectedValue)
        {
            int index = args.IndexOf(flag);
            AssertTrue(index >= 0 && index + 1 < args.Count, flag + " must be present and carry a value");
            AssertEqual(expectedValue, args[index + 1], flag + " value");
        }

        private const string MissionCredentialValue = "mux-mission-session-token";
        private const string ProbeOutputVariable = "ARMADA_MUX_PROBE_OUT";

        private sealed class MuxLaunch
        {
            public string ScopedDirectory { get; set; } = String.Empty;
            public List<string> Arguments { get; set; } = new List<string>();
            public Dictionary<string, string> Environment { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        /// <summary>
        /// Launch a real process through StartAsync with the mission launch plan, and read back the exact argv and
        /// environment it received. The executable is a probe script standing in for the Mux CLI.
        /// </summary>
        private static async Task<MuxLaunch> LaunchWithMissionPlanAsync(string scratch, string? configDirectory)
        {
            string output = Path.Combine(scratch, "probe");
            string probe = Path.Combine(scratch, "mux");
            File.WriteAllText(probe,
                "#!/bin/sh\n" +
                "for a in \"$@\"; do printf '%s\\n' \"$a\"; done > \"$" + ProbeOutputVariable + ".args\"\n" +
                "env > \"$" + ProbeOutputVariable + ".env\"\n" +
                "touch \"$" + ProbeOutputVariable + ".done\"\n");
            File.SetUnixFileMode(probe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            string scoped = Path.Combine(scratch, "runtime-config");
            Directory.CreateDirectory(scoped);
            CaptainLaunchIsolationPlan plan = CaptainLaunchIsolationPlanner.PlanForLaunch(
                AgentRuntimeEnum.Mux, true, 7891, scoped, McpCredentialReference.ForMission(MissionCredentialValue));
            foreach (IsolationConfigFile file in plan.FilesToWrite)
                File.WriteAllText(Path.Combine(scoped, file.RelativePath), file.Contents);

            Captain captain = new Captain("mux-launch-captain", AgentRuntimeEnum.Mux)
            {
                RuntimeOptionsJson = CaptainRuntimeOptions.Serialize(new MuxCaptainOptions { ConfigDirectory = configDirectory })
            };

            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            MuxRuntime runtime = new MuxRuntime(logging) { ExecutablePath = probe };
            string dock = Path.Combine(scratch, "dock");
            Directory.CreateDirectory(dock);
            int pid = await runtime.StartAsync(dock, "mux launch probe",
                environment: new Dictionary<string, string> { [ProbeOutputVariable] = output },
                captain: captain, isolationPlan: plan).ConfigureAwait(false);
            if (pid <= 0) throw new Exception("Runtime did not start");

            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            while (!File.Exists(output + ".done") && DateTime.UtcNow < deadline) await Task.Delay(50).ConfigureAwait(false);
            if (!File.Exists(output + ".done")) throw new Exception("Launched probe wrote no output");

            MuxLaunch launch = new MuxLaunch { ScopedDirectory = scoped };
            launch.Arguments = File.ReadAllLines(output + ".args").ToList();
            foreach (string line in File.ReadAllLines(output + ".env"))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) launch.Environment[line.Substring(0, eq)] = line.Substring(eq + 1);
            }
            return launch;
        }

        private static string? FlagValue(List<string> args, string flag)
        {
            int index = args.IndexOf(flag);
            return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
        }

        /// <summary>
        /// The config directory Mux uses for a launch: the --config-dir flag outranks MUX_CONFIG_DIR, and with
        /// neither Mux uses ~/.mux (returned as null). Mux reads no other variable for this.
        /// </summary>
        private static string? EffectiveConfigDirectory(List<string> args, Dictionary<string, string> environment)
        {
            string? flag = FlagValue(args, MuxCommandBuilder.ConfigDirectoryFlag);
            if (flag != null) return flag;
            return environment.TryGetValue(MuxCommandBuilder.ConfigDirectoryEnvironmentVariable, out string? variable) ? variable : null;
        }

        /// <summary>
        /// The MCP servers file a `mux print` run loads: only the --mcp-config file. Without that flag the run
        /// loads no MCP server at all (returned as null).
        /// </summary>
        private static string? EffectiveMcpConfig(List<string> args)
        {
            return FlagValue(args, MuxCommandBuilder.McpConfigFlag);
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("BuildArguments Uses Current Mux Run Contract", () =>
            {
                InspectableMuxRuntime runtime = CreateRuntime();
                Captain captain = new Captain("mux-captain", AgentRuntimeEnum.Mux)
                {
                    RuntimeOptionsJson = CaptainRuntimeOptions.Serialize(new MuxCaptainOptions
                    {
                        ConfigDirectory = "C:/mux/config",
                        Endpoint = "captain-prod",
                        BaseUrl = "https://mux.example.com",
                        AdapterType = "openai",
                        Temperature = 0.2,
                        MaxTokens = 4096,
                        SystemPromptPath = "C:/mux/prompts/system.txt",
                        ApprovalPolicy = "deny"
                    })
                };

                List<string> args = runtime.Args("C:/worktree", "test prompt", "gpt-5.4-mini", "C:/logs/final.txt", captain);

                AssertEqual("print", args[0]);
                AssertTrue(args.Contains("--model"));
                AssertTrue(args.Contains("gpt-5.4-mini"));
                AssertTrue(args.Contains("-w"));
                AssertTrue(args.Contains("C:/worktree"));
                AssertTrue(args.Contains("--yolo"));
                AssertFlagValue(args, "--config-dir", "C:/mux/config");
                AssertTrue(args.Contains("--output-format"));
                AssertTrue(args.Contains("jsonl"));
                AssertFlagValue(args, "--output-last-message", "C:/logs/final.txt");
                AssertFlagValue(args, "--endpoint", "captain-prod");
                AssertFalse(args.Contains("--base-url"));
                AssertFalse(args.Contains("--adapter-type"));
                AssertFalse(args.Contains("--temperature"));
                AssertFalse(args.Contains("--max-tokens"));
                AssertFalse(args.Contains("--system-prompt"));
                AssertFalse(args.Contains("--approval-policy"));
                AssertEqual("test prompt", args[args.Count - 1], "Mux takes the prompt as the trailing positional argument");
                AssertTrue(runtime.UsesPromptStdin());
            });

            await RunTest("BuildArguments Defaults To Exec Mode", () =>
            {
                InspectableMuxRuntime runtime = CreateRuntime();
                Captain captain = new Captain("mux-captain", AgentRuntimeEnum.Mux)
                {
                    RuntimeOptionsJson = CaptainRuntimeOptions.Serialize(new MuxCaptainOptions
                    {
                        Endpoint = "captain-prod"
                    })
                };

                List<string> args = runtime.Args("C:/worktree", "test prompt", captain: captain);

                AssertTrue(args.Contains("--yolo"));
                AssertFalse(args.Contains("--approval-policy"));
                AssertFalse(args.Contains("--mode"));
            });

            await RunTest("BuildArguments IgnoresLegacyPlanApproval", () =>
            {
                InspectableMuxRuntime runtime = CreateRuntime();
                Captain captain = new Captain("mux-captain", AgentRuntimeEnum.Mux)
                {
                    RuntimeOptionsJson = CaptainRuntimeOptions.Serialize(new MuxCaptainOptions
                    {
                        ApprovalPolicy = "plan"
                    })
                };

                List<string> args = runtime.Args("C:/worktree", "test prompt", captain: captain);

                AssertFalse(args.Contains("--mode"));
                AssertFalse(args.Contains("plan"));
            });

            await RunTest("ExactProviderUsageIsPublishedButEstimatesAreIgnored", () =>
            {
                InspectableMuxRuntime runtime = CreateRuntime();
                RuntimeTokenUsage? captured = null;
                runtime.OnTokenUsageReceived += (_, usage) => captured = usage;
                runtime.FeedUsage(21, "{\"eventType\":\"llm_response\",\"usage\":{\"inputTokens\":100,\"outputTokens\":20}}");
                AssertNotNull(captured);
                AssertEqual(100L, captured!.InputTokens);
                AssertEqual(20L, captured.OutputTokens);

                captured = null;
                runtime.FeedUsage(21, "{\"eventType\":\"run_completed\",\"finalEstimatedTokens\":999}");
                AssertNull(captured, "Mux estimates must never be recorded as authoritative usage");
            });

            await RunTest("A JSON Error Event Reaches The Mission Log", () =>
            {
                InspectableMuxRuntime runtime = CreateRuntime();
                string rendered = runtime.TransformLine("{\"type\":\"error\",\"message\":\"quota exceeded\"}");
                AssertContains("quota exceeded", rendered, "the provider's error text is kept");
                string nested = runtime.TransformLine("{\"type\":\"error\",\"error\":{\"message\":\"rate limited\"}}");
                AssertContains("rate limited", nested, "an error object's message is kept");
            });

            await RunTest("ApplyEnvironment Maps BaseUrl And Sets No Config Variable", () =>
            {
                InspectableMuxRuntime runtime = CreateRuntime();
                Captain captain = new Captain("mux-captain", AgentRuntimeEnum.Mux)
                {
                    RuntimeOptionsJson = CaptainRuntimeOptions.Serialize(new MuxCaptainOptions
                    {
                        ConfigDirectory = "C:/mux/config",
                        BaseUrl = "https://example-provider/v1"
                    })
                };

                // The config directory reaches Mux only as --config-dir; Mux reads no MUX_CONFIG_ROOT variable.
                AssertNull(runtime.AppliedEnvironmentValue(captain, "MUX_CONFIG_ROOT"), "Mux reads no MUX_CONFIG_ROOT variable");
                AssertNull(runtime.AppliedEnvironmentValue(captain, MuxCommandBuilder.ConfigDirectoryEnvironmentVariable), "the runtime does not set the config directory variable");
                AssertEqual("https://example-provider/v1", runtime.AppliedEnvironmentValue(captain, "OPENAI_BASE_URL"));
            });

            await RunTest("A Mission Launch Loads Only Its Own MCP Config And Keeps The Captain Config Directory", async () =>
            {
                if (OperatingSystem.IsWindows()) return;

                foreach (string? configDirectory in new[] { "/captain/mux-config", null })
                {
                    string label = configDirectory == null ? "without a captain config directory" : "with a captain config directory";
                    string scratch = Path.Combine(Path.GetTempPath(), "armada_mux_launch_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(scratch);
                    try
                    {
                        MuxLaunch launch = await LaunchWithMissionPlanAsync(scratch, configDirectory).ConfigureAwait(false);
                        string scopedMcpConfig = Path.Combine(launch.ScopedDirectory, MuxCommandBuilder.ScopedMcpConfigFileName);

                        AssertEqual("print", launch.Arguments[0], label + ": single-shot print run");
                        AssertEqual(scopedMcpConfig, EffectiveMcpConfig(launch.Arguments), label + ": Mux loads the per-launch MCP config");
                        AssertTrue(launch.Arguments.Contains(MuxCommandBuilder.StrictMcpConfigFlag), label + ": the config directory's mcp-servers.json is ignored");
                        string? expectedConfigDirectory = configDirectory ?? System.Environment.GetEnvironmentVariable(MuxCommandBuilder.ConfigDirectoryEnvironmentVariable);
                        AssertEqual(expectedConfigDirectory, EffectiveConfigDirectory(launch.Arguments, launch.Environment), label + ": the captain's own config directory (or ~/.mux) still selects its endpoints");
                        AssertFalse(launch.Environment.ContainsKey("MUX_CONFIG_ROOT"), label + ": no MUX_CONFIG_ROOT");

                        string mcpConfig = File.ReadAllText(EffectiveMcpConfig(launch.Arguments)!);
                        AssertContains("\"name\": \"armada\"", mcpConfig, label + ": the per-launch config names the Armada server");
                        AssertContains("${" + McpLaunchCredential.EnvironmentVariable + "}", mcpConfig, label + ": the config references the launch credential by name");
                        AssertFalse(mcpConfig.Contains(MissionCredentialValue, StringComparison.Ordinal), label + ": the credential value is never written into the config");
                        AssertEqual(MissionCredentialValue, launch.Environment[McpLaunchCredential.EnvironmentVariable], label + ": the launch carries this mission's own credential");
                    }
                    finally
                    {
                        try { Directory.Delete(scratch, true); } catch { }
                    }
                }
            });
        }
    }
}
