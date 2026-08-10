namespace Test.Shared.Suites.Runtimes
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Runtimes;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="MuxRuntime"/> metadata and argument construction. Cases verify the
    /// runtime name and default executable path, that endpoint configuration and the final-message
    /// artifact flag are emitted from captain runtime options, and that approval defaults to yolo when no
    /// approval policy is configured. Argument construction is exercised through an inspectable subclass.
    /// </summary>
    public sealed class MuxRuntimeSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Runtimes.MuxRuntime";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Mux Runtime suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("name_returns_mux", "Name Returns Mux", TestTags.Positive, () =>
            {
                InspectableMuxRuntime runtime = CreateRuntime();
                AssertEqual("Mux", runtime.Name);
            }));

            cases.Add(Case("executable_path_default_is_mux", "ExecutablePath Default Is Mux", TestTags.Positive, () =>
            {
                InspectableMuxRuntime runtime = CreateRuntime();
                AssertEqual("mux", runtime.ExecutablePath);
            }));

            cases.Add(Case("build_arguments_includes_endpoint_config_and_final_message_artifact", "BuildArguments Includes Endpoint Config And Final Message Artifact", TestTags.Positive, () =>
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
                AssertTrue(args.Contains("--config-dir"));
                AssertTrue(args.Contains("C:/mux/config"));
                AssertTrue(args.Contains("--output-format"));
                AssertTrue(args.Contains("jsonl"));
                AssertTrue(args.Contains("--output-last-message"));
                AssertTrue(args.Contains("C:/logs/final.txt"));
                AssertTrue(args.Contains("--endpoint"));
                AssertTrue(args.Contains("captain-prod"));
                AssertTrue(args.Contains("--model"));
                AssertTrue(args.Contains("gpt-5.4-mini"));
                AssertTrue(args.Contains("--base-url"));
                AssertTrue(args.Contains("https://mux.example.com"));
                AssertTrue(args.Contains("--adapter-type"));
                AssertTrue(args.Contains("openai"));
                AssertTrue(args.Contains("--temperature"));
                AssertTrue(args.Contains("0.2"));
                AssertTrue(args.Contains("--max-tokens"));
                AssertTrue(args.Contains("4096"));
                AssertTrue(args.Contains("--system-prompt"));
                AssertTrue(args.Contains("C:/mux/prompts/system.txt"));
                AssertTrue(args.Contains("--approval-policy"));
                AssertTrue(args.Contains("deny"));
                AssertEqual("test prompt", args[args.Count - 1]);
            }));

            cases.Add(Case("build_arguments_defaults_to_yolo_approval", "BuildArguments Defaults To Yolo Approval", TestTags.Positive, () =>
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
            }));

            cases.Add(Case("is_protocol_event_line_detects_run_started", "IsProtocolEventLine Detects Protocol Events", TestTags.Positive, () =>
            {
                string runStarted = "{\"contractVersion\":1,\"eventType\":\"run_started\",\"runId\":\"625b9b8b\",\"endpointName\":\"gpt-oss:20b\",\"adapterType\":\"Ollama\"}";
                AssertTrue(MuxRuntime.IsProtocolEventLine(runStarted), "A run_started event line should be detected");
                AssertTrue(MuxRuntime.IsProtocolEventLine("  {\"eventType\":\"run_completed\",\"contractVersion\":1}  "), "Whitespace-padded events should be detected");
            }));

            cases.Add(Case("is_protocol_event_line_ignores_assistant_text", "IsProtocolEventLine Ignores Assistant Text", TestTags.Negative, () =>
            {
                AssertFalse(MuxRuntime.IsProtocolEventLine("Here is your answer."), "Plain text is not an event");
                AssertFalse(MuxRuntime.IsProtocolEventLine("## Heading\n\n- a markdown list"), "Markdown is not an event");
                AssertFalse(MuxRuntime.IsProtocolEventLine("{\"foo\":\"bar\"}"), "JSON without an eventType field is not a protocol event");
                AssertFalse(MuxRuntime.IsProtocolEventLine("{ not valid json"), "Malformed JSON is not an event");
                AssertFalse(MuxRuntime.IsProtocolEventLine(""), "Empty is not an event");
                AssertFalse(MuxRuntime.IsProtocolEventLine(null), "Null is not an event");
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Mux Runtime",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static InspectableMuxRuntime CreateRuntime()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new InspectableMuxRuntime(logging);
        }

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
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

        #region Private-Types

        /// <summary>
        /// Inspectable subclass exposing protected argument construction for assertions.
        /// </summary>
        private sealed class InspectableMuxRuntime : MuxRuntime
        {
            public InspectableMuxRuntime(LoggingModule logging) : base(logging)
            {
            }

            public List<string> Args(string workingDirectory, string prompt, string? model = null, string? finalMessageFilePath = null, Captain? captain = null) =>
                BuildArguments(workingDirectory, prompt, model, finalMessageFilePath, captain);
        }

        #endregion
    }
}
