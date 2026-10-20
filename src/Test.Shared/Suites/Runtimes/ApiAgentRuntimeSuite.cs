namespace Test.Shared.Suites.Runtimes
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Runtimes;
    using Armada.Runtimes.Interfaces;
    using Armada.Runtimes.Mcp;
    using Armada.Runtimes.Tools;
    using Armada.Runtimes.Tools.Arguments;
    using PolyPrompt.Clients;
    using PolyPrompt.Models;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for the ported coding tools and the <see cref="ApiAgentRuntime"/> tool-calling loop. The
    /// loop is exercised with a scripted fake inference client so no live endpoint is required.
    /// </summary>
    public sealed class ApiAgentRuntimeSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the API agent runtime suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("endpoint_policy_enforced_and_generation_forwarded", "Read-only and exact allowlist block unadvertised writes, carry generation settings and skip MCP discovery", TestTags.Positive, async () =>
            {
                foreach (InferenceOptions policy in new[] {
                    new InferenceOptions { ReadOnlyTools = true, MaxTokens = 123, Temperature = 0.4, TopP = 0.9 },
                    new InferenceOptions { AllowedTools = new List<string> { "read_file" }, McpToolsEnabled = false, MaxTokens = 123, Temperature = 0.4, TopP = 0.9 } })
                {
                    string dir = NewTempDir();
                    try
                    {
                        Queue<ToolChatResponse> script = new Queue<ToolChatResponse>();
                        script.Enqueue(new ToolChatResponse { Success = true, ToolCalls = new List<ToolCall> { new ToolCall { Id = "forged", Name = "write_file", ArgumentsJson = "{\"file_path\":\"blocked.txt\",\"content\":\"bad\"}" } } });
                        script.Enqueue(new ToolChatResponse { Success = true, Text = "done", ToolCalls = new List<ToolCall>() });
                        ScriptedClient? client = null;
                        bool mcpDiscovered = false;
                        bool denied = false;
                        ModelEndpoint endpoint = new ModelEndpoint { Name = "policy", Kind = ModelEndpointKindEnum.Inference, Provider = ModelProviderEnum.OpenAICompatible, Model = "endpoint-model", BaseUrl = "http://localhost:1", InferenceOptions = policy };
                        ApiAgentRuntime runtime = new ApiAgentRuntime(endpoint, CreateLogging(), 20, (ep, log) => { endpoint.InferenceOptions = new InferenceOptions(); return client = new ScriptedClient(script, log); }, (url, key, log) => { mcpDiscovered = true; throw new InvalidOperationException("MCP discovery must be disabled"); });
                        using ManualResetEventSlim exited = new ManualResetEventSlim(false);
                        int code = -1;
                        runtime.OnToolEvent += (pid, evt) => { if (evt.PermissionDenied == true) denied = true; };
                        runtime.OnProcessExited += (pid, value) => { code = value ?? -1; exited.Set(); };
                        await runtime.StartAsync(dir, "try a write", new Dictionary<string, string> { ["ARMADA_MCP_URL"] = "http://localhost:1/mcp" }, model: "captain-model", captain: new Captain("effort") { ReasoningEffort = ReasoningEffortEnum.Low }).ConfigureAwait(false);
                        AssertTrue(exited.Wait(TimeSpan.FromSeconds(10)), "Run must finish.");
                        AssertEqual(0, code);
                        AssertTrue(denied, "Injected tool receives a typed permission denial.");
                        AssertFalse(mcpDiscovered, "Disabled or read-only MCP must never be contacted.");
                        AssertTrue(!File.Exists(Path.Combine(dir, "blocked.txt")), "An injected unadvertised tool must never execute.");
                        AssertNotNull(client);
                        AssertTrue(!client!.ToolNames.Contains("write_file"), "Mutating tool must not be advertised.");
                        AssertTrue(client.ToolNames.Contains("read_file"), "Read tool remains available.");
                        AssertEqual(123, client.Settings!.MaxTokens);
                        AssertEqual(0.4, client.Settings.Temperature);
                        AssertEqual(0.9, client.Settings.TopP);
                        AssertEqual("captain-model", client.Settings.Model);
                        AssertEqual(ReasoningEffortLevel.Low, client.Settings.ReasoningEffort!.Level);
                    }
                    finally { Cleanup(dir); }
                }
            }));

            cases.Add(CaseAsync("endpoint_tools_disabled_uses_plain_chat", "Tools disabled or empty allowlist uses plain chat and skips MCP", TestTags.Positive, async () =>
            {
                foreach (InferenceOptions policy in new[] { new InferenceOptions { ToolsEnabled = false }, new InferenceOptions { AllowedTools = new List<string>() }, new InferenceOptions { AllowedTools = new List<string> { "not_available" }, McpToolsEnabled = false } })
                {
                    string dir = NewTempDir();
                    try
                    {
                        ScriptedClient? client = null;
                        ModelEndpoint endpoint = new ModelEndpoint { Name = "plain", Kind = ModelEndpointKindEnum.Inference, Provider = ModelProviderEnum.OpenAICompatible, BaseUrl = "http://localhost:1", InferenceOptions = policy };
                        ApiAgentRuntime runtime = new ApiAgentRuntime(endpoint, CreateLogging(), 20, (ep, log) => client = new ScriptedClient(new Queue<ToolChatResponse>(), log), (url, key, log) => throw new InvalidOperationException("No MCP"));
                        using ManualResetEventSlim exited = new ManualResetEventSlim(false);
                        int code = -1; runtime.OnProcessExited += (pid, value) => { code = value ?? -1; exited.Set(); };
                        string finalPath = Path.Combine(dir, "answer.txt");
                        await runtime.StartAsync(dir, "hello", new Dictionary<string, string> { ["ARMADA_MCP_URL"] = "http://localhost:1/mcp" }, finalMessageFilePath: finalPath).ConfigureAwait(false);
                        AssertTrue(exited.Wait(TimeSpan.FromSeconds(10)), "Plain run must finish.");
                        AssertEqual(0, code);
                        AssertTrue(client!.PlainChatCalled, "Must use chat without tool definitions.");
                        AssertEqual(0, client.ToolNames.Count);
                        AssertEqual("plain answer", File.ReadAllText(finalPath));
                    }
                    finally { Cleanup(dir); }
                }
            }));

            cases.Add(CaseAsync("incomplete_terminal_responses_are_not_success", "Empty or introductory terminal replies cannot reuse earlier narration as a mission result", TestTags.Negative, async () =>
            {
                foreach (bool recover in new[] { false, true })
                foreach (string? terminal in new string?[] { null, "I will start by reading instructions.", "[ARMADA:RESULT] COMPLETE", "[ARMADA:RESULT] COMPLETE\nTruncated summary" })
                {
                    string dir = NewTempDir();
                    try
                    {
                        Queue<ToolChatResponse> script = new Queue<ToolChatResponse>();
                        script.Enqueue(new ToolChatResponse { Success = true, Text = "I will start by reading instructions.", ToolCalls = new List<ToolCall> { new ToolCall { Id = "read", Name = "list_directory", ArgumentsJson = "{}" } } });
                        script.Enqueue(new ToolChatResponse { Success = true, Text = terminal, FinishReason = terminal?.Contains("Truncated") == true ? "length" : "stop" });
                        if (recover) script.Enqueue(new ToolChatResponse { Success = true, Text = "[ARMADA:RESULT] COMPLETE\nImplemented and tested.", FinishReason = "stop", Usage = new TokenUsage { PromptTokens = 42, CompletionTokens = 7 } });
                        ModelEndpoint endpoint = new ModelEndpoint { Kind = ModelEndpointKindEnum.Inference, Provider = ModelProviderEnum.OpenAICompatible, BaseUrl = "http://localhost:1", InferenceOptions = new InferenceOptions { McpToolsEnabled = false } };
                        ApiAgentRuntime runtime = new ApiAgentRuntime(endpoint, CreateLogging(), 10, (ep, log) => new ScriptedClient(script, log));
                        using ManualResetEventSlim exited = new ManualResetEventSlim(false);
                        int code = -1; RuntimeProviderError? failure = null;
                        List<ApiRuntimeDiagnostic> diagnostics = new List<ApiRuntimeDiagnostic>();
                        runtime.OnProviderError += (pid, error) => failure = error;
                        runtime.OnDiagnostic += (pid, diagnostic) => diagnostics.Add(diagnostic);
                        runtime.OnProcessExited += (pid, value) => { code = value ?? -1; exited.Set(); };
                        string finalPath = Path.Combine(dir, "result.txt");
                        await runtime.StartAsync(dir, "Implement the mission. End with [ARMADA:RESULT] COMPLETE", finalMessageFilePath: finalPath).ConfigureAwait(false);
                        AssertTrue(exited.Wait(TimeSpan.FromSeconds(10)), "Incomplete responses must terminate or recover within the bounded budget.");
                        AssertEqual(recover ? 0 : 1, code);
                        AssertFalse(File.ReadAllText(finalPath).Contains("I will start"), "The final failed empty response clears earlier narration.");
                        AssertTrue(diagnostics.Exists(d => d.Kind == ApiRuntimeDiagnosticKindEnum.Inference), "Provider turn metadata must be recorded.");
                        if (recover)
                        {
                            AssertContains("Implemented and tested", File.ReadAllText(finalPath));
                            AssertTrue(diagnostics.Exists(d => d.Kind == ApiRuntimeDiagnosticKindEnum.Inference && d.Message.Contains("\"PromptTokens\":42") && d.Message.Contains("\"FinishReason\":\"stop\"")), "Finish reason and actual usage must survive into turn diagnostics.");
                        }
                        else
                        {
                            AssertEqual(RuntimeFailureKindEnum.TaskFailure, Armada.Core.Services.RuntimeFailureClassifier.Classify(code, failure));
                            AssertEqual(terminal?.Contains("Truncated") == true ? "output_token_limit_reached" : "incomplete_completion", failure!.ErrorType);
                            AssertContains("Remediation:", Armada.Core.Services.RuntimeFailureClassifier.Decide(code, failure).Describe());
                        }
                    }
                    finally { Cleanup(dir); }
                }
            }));

            cases.Add(CaseAsync("endpoint_turn_budget_exits_failure", "Tool loop budget exhaustion fails the mission rather than reporting success", TestTags.Negative, async () =>
            {
                string dir = NewTempDir();
                try
                {
                    Queue<ToolChatResponse> script = new Queue<ToolChatResponse>();
                    script.Enqueue(new ToolChatResponse { Success = true, ToolCalls = new List<ToolCall> { new ToolCall { Id = "read", Name = "list_directory", ArgumentsJson = "{}" } } });
                    ModelEndpoint endpoint = new ModelEndpoint { Kind = ModelEndpointKindEnum.Inference, Provider = ModelProviderEnum.OpenAICompatible, BaseUrl = "http://localhost:1", InferenceOptions = new InferenceOptions { MaxIterations = 1 } };
                    ApiAgentRuntime runtime = new ApiAgentRuntime(endpoint, CreateLogging(), 20, (ep, log) => new ScriptedClient(script, log));
                    using ManualResetEventSlim exited = new ManualResetEventSlim(false); int code = -1;
                    RuntimeProviderError? failure = null;
                    runtime.OnProviderError += (pid, error) => failure = error;
                    runtime.OnProcessExited += (pid, value) => { code = value ?? -1; exited.Set(); };
                    await runtime.StartAsync(dir, "loop").ConfigureAwait(false);
                    AssertTrue(exited.Wait(TimeSpan.FromSeconds(10)), "Budget must terminate run."); AssertEqual(1, code);
                    AssertNotNull(failure, "Turn budget must produce a structured failure.");
                    AssertEqual(RuntimeFailureKindEnum.TaskFailure, Armada.Core.Services.RuntimeFailureClassifier.Classify(code, failure), "Turn budgets must not count as captain crashes.");
                }
                finally { Cleanup(dir); }
            }));

            cases.Add(CaseAsync("tools_write_then_read_round_trip", "Ported write_file and read_file round-trip in a working directory", TestTags.Positive, async () =>
            {
                string dir = NewTempDir();
                try
                {
                    BuiltInToolRegistry registry = new BuiltInToolRegistry(null);
                    AssertTrue(registry.HasTool("write_file"), "Expected write_file to be registered.");
                    AssertTrue(registry.HasTool("read_file"), "Expected read_file to be registered.");
                    AssertTrue(registry.HasTool("run_process"), "Expected run_process to be registered.");

                    ToolResult write = await registry.ExecuteAsync("w1", "write_file", ParseArgs("{\"file_path\":\"notes/hello.txt\",\"content\":\"hi there\"}"), dir, CancellationToken.None).ConfigureAwait(false);
                    AssertTrue(write.Success, "Expected write_file to succeed: " + write.Content);
                    AssertTrue(File.Exists(Path.Combine(dir, "notes", "hello.txt")), "Expected the file to be created on disk.");

                    ToolResult read = await registry.ExecuteAsync("r1", "read_file", ParseArgs("{\"file_path\":\"notes/hello.txt\"}"), dir, CancellationToken.None).ConfigureAwait(false);
                    AssertTrue(read.Success, "Expected read_file to succeed: " + read.Content);
                    AssertTrue(read.Content.Contains("hi there"), "Expected the read content to contain the written text.");
                }
                finally
                {
                    Cleanup(dir);
                }
            }));

            cases.Add(CaseAsync("registry_unknown_tool_fails", "Registry returns a clear failure for an unknown tool", TestTags.Negative, async () =>
            {
                string dir = NewTempDir();
                try
                {
                    BuiltInToolRegistry registry = new BuiltInToolRegistry(null);
                    ToolResult result = await registry.ExecuteAsync("x1", "does_not_exist", ParseArgs("{}"), dir, CancellationToken.None).ConfigureAwait(false);
                    AssertTrue(!result.Success, "Expected an unknown tool to fail.");
                    ToolErrorContent? error = System.Text.Json.JsonSerializer.Deserialize<ToolErrorContent>(result.Content);
                    AssertNotNull(error, "Expected a JSON error payload: " + result.Content);
                    AssertEqual("unknown_tool", error!.Error, "Expected an unknown_tool error code.");
                }
                finally
                {
                    Cleanup(dir);
                }
            }));

            cases.Add(CaseAsync("loop_executes_tool_and_completes", "Agent loop runs a scripted tool call then completes, writing the final message", TestTags.Positive, async () =>
            {
                string dir = NewTempDir();
                string finalPath = Path.Combine(dir, "final.txt");
                try
                {
                    Queue<ToolChatResponse> script = new Queue<ToolChatResponse>();
                    script.Enqueue(new ToolChatResponse
                    {
                        Success = true,
                        Text = "Creating the file.",
                        ToolCalls = new List<ToolCall> { new ToolCall { Id = "c1", Name = "write_file", ArgumentsJson = "{\"file_path\":\"out.txt\",\"content\":\"generated\"}" } }
                    });
                    script.Enqueue(new ToolChatResponse
                    {
                        Success = true,
                        Text = "Done. Created out.txt.",
                        ToolCalls = new List<ToolCall>()
                    });

                    ModelEndpoint endpoint = new ModelEndpoint { Name = "test-endpoint", Provider = ModelProviderEnum.OpenAICompatible, Kind = ModelEndpointKindEnum.Inference, Model = "test-model", BaseUrl = "http://localhost:1" };
                    ApiAgentRuntime runtime = new ApiAgentRuntime(endpoint, CreateLogging(), 20, (ep, log) => new ScriptedClient(script, log));

                    int startedPid = 0;
                    int? exitCode = null;
                    List<string> output = new List<string>();
                    ManualResetEventSlim exited = new ManualResetEventSlim(false);
                    runtime.OnProcessStarted += pid => startedPid = pid;
                    runtime.OnOutputReceived += (pid, line) => output.Add(line);
                    runtime.OnProcessExited += (pid, code) => { exitCode = code; exited.Set(); };

                    int pid = await runtime.StartAsync(dir, "Create out.txt with the text generated.", finalMessageFilePath: finalPath).ConfigureAwait(false);
                    AssertEqual(pid, startedPid);

                    AssertTrue(exited.Wait(TimeSpan.FromSeconds(10)), "Expected the loop to complete within the timeout.");
                    AssertEqual(0, exitCode ?? -1);
                    AssertTrue(File.Exists(Path.Combine(dir, "out.txt")), "Expected the tool to have created out.txt.");
                    AssertTrue(File.Exists(finalPath), "Expected the final message file to be written.");
                    AssertTrue(File.ReadAllText(finalPath).Contains("Created out.txt"), "Expected the final message text.");
                }
                finally
                {
                    Cleanup(dir);
                }
            }));

            cases.Add(CaseAsync("shell_tool_follows_cli_permission_policy", "run_process is refused under Refuse, asks the in-process prompt under ApproveInArmada, and runs under Bypass or with no policy", TestTags.Positive, async () =>
            {
                ApiRuntimeToolEvent refused = await RunShellAsync(Armada.Core.Enums.CliPermissionPolicyEnum.Refuse, null).ConfigureAwait(false);
                AssertEqual(false, refused.Ok, "Refuse refuses");
                AssertEqual(true, refused.PermissionDenied, "typed refusal");

                List<string> asked = new List<string>();
                ApiRuntimeToolEvent denied = await RunShellAsync(Armada.Core.Enums.CliPermissionPolicyEnum.ApproveInArmada, (tool, input, ct) =>
                {
                    lock (asked) asked.Add(tool + " " + input);
                    return Task.FromResult(new Armada.Core.Models.CliPermissionPromptOutcome { Allowed = false, Message = "no shell today" });
                }).ConfigureAwait(false);
                AssertEqual(true, denied.PermissionDenied, "denied in Armada");
                AssertEqual("no shell today", denied.Result);
                AssertEqual(1, asked.Count, "asked once");
                AssertTrue(asked[0].StartsWith("run_process ", StringComparison.Ordinal), "prompted for run_process");

                ApiRuntimeToolEvent allowed = await RunShellAsync(Armada.Core.Enums.CliPermissionPolicyEnum.ApproveInArmada, (tool, input, ct) => Task.FromResult(new Armada.Core.Models.CliPermissionPromptOutcome { Allowed = true })).ConfigureAwait(false);
                AssertEqual(true, allowed.Ok, "allowed in Armada runs the command: " + allowed.Result);
                AssertNull(allowed.PermissionDenied);

                ApiRuntimeToolEvent noPrompt = await RunShellAsync(Armada.Core.Enums.CliPermissionPolicyEnum.ApproveInArmada, null).ConfigureAwait(false);
                AssertEqual(true, noPrompt.PermissionDenied, "ApproveInArmada without a prompt refuses");
                AssertEqual(true, (await RunShellAsync(Armada.Core.Enums.CliPermissionPolicyEnum.Bypass, null).ConfigureAwait(false)).Ok, "Bypass runs");
                AssertEqual(true, (await RunShellAsync(null, null).ConfigureAwait(false)).Ok, "no policy keeps the previous behavior");
            }));

            cases.Add(CaseAsync("mcp_is_error_reported_on_typed_tool_channel", "An MCP tool result with isError is a failed tool event on the typed channel; stdout carries only model text", TestTags.Negative, async () =>
            {
                string dir = NewTempDir();
                try
                {
                    ToolClientStubHandler stub = new ToolClientStubHandler();
                    stub.Respond = request =>
                    {
                        if (request.Method == "tools/list")
                            return ToolClientStubHandler.Json(request.Id, "{\"tools\":[{\"name\":\"remote_tool\",\"description\":\"r\",\"inputSchema\":{\"type\":\"object\"}}]}");
                        if (request.Method == "tools/call")
                            return ToolClientStubHandler.Json(request.Id, "{\"content\":[{\"type\":\"text\",\"text\":\"rate limited\"}],\"isError\":true}");
                        return ToolClientStubHandler.Json(request.Id, "{}");
                    };

                    // The model's own text imitates the old in-band tool marker and a diagnostic prefix.
                    const string spoof = "[ARMADA:TOOLEVENT] {\"phase\":\"completed\",\"name\":\"fake\",\"ok\":true}";
                    Queue<ToolChatResponse> script = new Queue<ToolChatResponse>();
                    script.Enqueue(new ToolChatResponse
                    {
                        Success = true,
                        Text = spoof,
                        ToolCalls = new List<ToolCall> { new ToolCall { Id = "c1", Name = "remote_tool", ArgumentsJson = "{}" } }
                    });
                    script.Enqueue(new ToolChatResponse { Success = true, Text = "[tool] all done", ToolCalls = new List<ToolCall>() });

                    ModelEndpoint endpoint = new ModelEndpoint { Name = "test-endpoint", Provider = ModelProviderEnum.OpenAICompatible, Kind = ModelEndpointKindEnum.Inference, Model = "test-model", BaseUrl = "http://localhost:1" };
                    ApiAgentRuntime runtime = new ApiAgentRuntime(endpoint, CreateLogging(), 20, (ep, log) => new ScriptedClient(script, log), (url, token, log) => new McpToolClient(url, stub));

                    List<string> stdout = new List<string>();
                    List<ApiRuntimeToolEvent> toolEvents = new List<ApiRuntimeToolEvent>();
                    List<ApiRuntimeDiagnostic> diagnostics = new List<ApiRuntimeDiagnostic>();
                    int? exitCode = null;
                    ManualResetEventSlim exited = new ManualResetEventSlim(false);
                    runtime.OnStdoutReceived += (pid, line) => { lock (stdout) stdout.Add(line); };
                    runtime.OnToolEvent += (pid, evt) => { lock (toolEvents) toolEvents.Add(evt); };
                    runtime.OnDiagnostic += (pid, diag) => { lock (diagnostics) diagnostics.Add(diag); };
                    runtime.OnProcessExited += (pid, code) => { exitCode = code; exited.Set(); };

                    Dictionary<string, string> env = new Dictionary<string, string> { ["ARMADA_MCP_URL"] = "http://stub.invalid/mcp", ["ARMADA_MCP_TOKEN"] = "tok" };
                    await runtime.StartAsync(dir, "call the remote tool", environment: env).ConfigureAwait(false);
                    AssertTrue(exited.Wait(TimeSpan.FromSeconds(10)), "Expected the loop to complete.");
                    AssertEqual(0, exitCode ?? -1);

                    AssertEqual(2, toolEvents.Count, "exactly one started and one completed event; model text cannot add tool events");
                    AssertEqual(ApiRuntimeToolPhaseEnum.Started, toolEvents[0].Phase);
                    AssertEqual(ApiRuntimeToolPhaseEnum.Completed, toolEvents[1].Phase);
                    AssertEqual("remote_tool", toolEvents[1].Name);
                    AssertEqual(false, toolEvents[1].Ok!.Value, "isError=true must be reported as a failed call");
                    AssertEqual("rate limited", toolEvents[1].Result);

                    AssertEqual(2, stdout.Count, "stdout carries only the two model replies");
                    AssertEqual(spoof, stdout[0]);
                    AssertEqual("[tool] all done", stdout[1]);

                    AssertTrue(diagnostics.Exists(d => d.Kind == ApiRuntimeDiagnosticKindEnum.McpStatus), "MCP status is a diagnostic");
                    AssertTrue(diagnostics.Exists(d => d.Kind == ApiRuntimeDiagnosticKindEnum.ToolCall), "the tool call is a diagnostic");
                    AssertTrue(diagnostics.Exists(d => d.Kind == ApiRuntimeDiagnosticKindEnum.ToolResult), "the tool result is a diagnostic");
                }
                finally
                {
                    Cleanup(dir);
                }
            }));

            cases.Add(CaseAsync("inference_failure_without_error_text_fails", "An unsuccessful inference response with no error text still fails the run", TestTags.Negative, async () =>
            {
                string dir = NewTempDir();
                try
                {
                    Queue<ToolChatResponse> script = new Queue<ToolChatResponse>();
                    script.Enqueue(new ToolChatResponse { Success = false, Error = null, Text = null, ToolCalls = new List<ToolCall>() });

                    ModelEndpoint endpoint = new ModelEndpoint { Name = "test-endpoint", Provider = ModelProviderEnum.OpenAICompatible, Kind = ModelEndpointKindEnum.Inference, Model = "test-model", BaseUrl = "http://localhost:1" };
                    ApiAgentRuntime runtime = new ApiAgentRuntime(endpoint, CreateLogging(), 20, (ep, log) => new ScriptedClient(script, log));

                    int? exitCode = null;
                    List<ApiRuntimeDiagnostic> diagnostics = new List<ApiRuntimeDiagnostic>();
                    ManualResetEventSlim exited = new ManualResetEventSlim(false);
                    runtime.OnDiagnostic += (pid, diag) => { lock (diagnostics) diagnostics.Add(diag); };
                    runtime.OnProcessExited += (pid, code) => { exitCode = code; exited.Set(); };

                    await runtime.StartAsync(dir, "anything").ConfigureAwait(false);
                    AssertTrue(exited.Wait(TimeSpan.FromSeconds(10)), "Expected the loop to complete.");
                    AssertEqual(1, exitCode ?? -1, "Success=false must fail the run even without error text");
                    AssertTrue(diagnostics.Exists(d => d.Kind == ApiRuntimeDiagnosticKindEnum.Error), "an Error diagnostic is raised");
                }
                finally
                {
                    Cleanup(dir);
                }
            }));

            cases.Add(CaseAsync("stop_cancels_running_loop", "StopAsync cancels a running loop and it is no longer tracked", TestTags.Positive, async () =>
            {
                string dir = NewTempDir();
                try
                {
                    // A client that blocks until cancelled, so the loop stays running until StopAsync fires.
                    ModelEndpoint endpoint = new ModelEndpoint { Name = "blocking", Provider = ModelProviderEnum.OpenAICompatible, Kind = ModelEndpointKindEnum.Inference, Model = "m", BaseUrl = "http://localhost:1" };
                    ApiAgentRuntime runtime = new ApiAgentRuntime(endpoint, CreateLogging(), 20, (ep, log) => new BlockingClient(log));

                    int? exitCode = null;
                    ManualResetEventSlim exited = new ManualResetEventSlim(false);
                    runtime.OnProcessExited += (pid2, code) => { exitCode = code; exited.Set(); };

                    int pid = await runtime.StartAsync(dir, "do work").ConfigureAwait(false);
                    // Give the loop a moment to enter the blocking call.
                    await Task.Delay(200).ConfigureAwait(false);
                    AssertTrue(await runtime.IsRunningAsync(pid).ConfigureAwait(false), "Expected the loop to be running.");

                    await runtime.StopAsync(pid).ConfigureAwait(false);
                    AssertTrue(exited.Wait(TimeSpan.FromSeconds(10)), "Expected the loop to exit after cancellation.");
                    AssertTrue(!await runtime.IsRunningAsync(pid).ConfigureAwait(false), "Expected the loop to no longer be tracked.");
                }
                finally
                {
                    Cleanup(dir);
                }
            }));

            cases.Add(CaseAsync("timeout_cancellation_fails_the_run", "An OperationCanceledException that is not a stop (an inference HTTP timeout) fails the run with exit 1; only a stop reports -1", TestTags.Negative, async () =>
            {
                string dir = NewTempDir();
                try
                {
                    ModelEndpoint endpoint = new ModelEndpoint { Name = "timeout", Provider = ModelProviderEnum.OpenAICompatible, Kind = ModelEndpointKindEnum.Inference, Model = "m", BaseUrl = "http://localhost:1" };
                    ApiAgentRuntime runtime = new ApiAgentRuntime(endpoint, CreateLogging(), 20, (ep, log) => new TimeoutClient(log));

                    int? exitCode = null; RuntimeProviderError? failure = null;
                    runtime.OnProviderError += (pid, error) => failure = error;
                    List<ApiRuntimeDiagnostic> diagnostics = new List<ApiRuntimeDiagnostic>();
                    ManualResetEventSlim exited = new ManualResetEventSlim(false);
                    runtime.OnDiagnostic += (pid, diag) => { lock (diagnostics) diagnostics.Add(diag); };
                    runtime.OnProcessExited += (pid, code) => { exitCode = code; exited.Set(); };

                    await runtime.StartAsync(dir, "anything").ConfigureAwait(false);
                    AssertTrue(exited.Wait(TimeSpan.FromSeconds(10)), "Expected the loop to complete.");
                    AssertEqual(1, exitCode ?? 0, "A timeout is a failed run, not the -1 interruption sentinel");
                    AssertEqual("llm_timeout", failure!.ErrorType);
                    AssertContains("120000 ms", failure.Reason!);
                    AssertContains("Remediation:", Armada.Core.Services.RuntimeFailureClassifier.Decide(exitCode, failure).Describe());
                    lock (diagnostics)
                    {
                        AssertFalse(diagnostics.Exists(d => d.Kind == ApiRuntimeDiagnosticKindEnum.Cancelled), "No cancelled diagnostic without a stop");
                        AssertTrue(diagnostics.Exists(d => d.Kind == ApiRuntimeDiagnosticKindEnum.Error), "an Error diagnostic is raised");
                    }
                }
                finally
                {
                    Cleanup(dir);
                }
            }));

            string? simulation = Environment.GetEnvironmentVariable("ARMADA_MODEL_PROVIDER_SIMULATION");
            if (!String.IsNullOrWhiteSpace(simulation))
            {
                cases.Add(CaseAsync("ollama_thinking_replayed_through_harbor_http", "Harbor executes sequential and parallel Ollama tools while replaying native thinking unchanged", TestTags.Reliability, async () =>
                {
                    string dir = NewTempDir();
                    try
                    {
                        ModelEndpoint endpoint = new ModelEndpoint { Name = "Ollama continuation HTTP", Provider = ModelProviderEnum.Ollama,
                            Kind = ModelEndpointKindEnum.Inference, Model = "ollama-thinking-replay", BaseUrl = simulation, ApiKey = "test-token",
                            InferenceOptions = new InferenceOptions { McpToolsEnabled = false, MaxIterations = 3, MaxTokens = 32768 } };
                        LocalHarborJobRunner runner = new LocalHarborJobRunner(CreateLogging(), new AgentRuntimeFactory(CreateLogging()), Path.Combine(dir, "scratch"));
                        Armada.Core.Harbor.HarborLaunchRequest request = new Armada.Core.Harbor.HarborLaunchRequest { JobId = "thinking-replay", Runtime = "ApiEndpoint", WorkingDirectory = dir,
                            Prompt = "Write and verify provider-thinking.txt, then finish the mission.", InferenceEndpoint = Armada.Core.Harbor.HarborInferenceEndpoint.FromEndpoint(endpoint), ReturnProviderErrors = true };
                        List<string> lines = new List<string>();
                        RuntimeProviderError? failure = null;
                        int code = -1;
                        using ManualResetEventSlim exited = new ManualResetEventSlim(false);
                        await runner.StartAsync(request, null, _ => { }, (stream, text) =>
                        {
                            if (stream == Armada.Core.Harbor.HarborOutputStreamEnum.ProviderError) failure = System.Text.Json.JsonSerializer.Deserialize<RuntimeProviderError>(text);
                            else lines.Add(text);
                        }, value => { code = value; exited.Set(); }, CancellationToken.None).ConfigureAwait(false);
                        AssertTrue(exited.Wait(TimeSpan.FromSeconds(15)), "Real SDK/tool loop must finish.");
                        AssertEqual(0, code, "Fixture rejects dropped thinking, changed whitespace, unnamed tool results or unexecuted file tools.");
                        AssertNull(failure);
                        AssertEqual("native thinking replay verified", File.ReadAllText(Path.Combine(dir, "provider-thinking.txt")));
                        AssertEqual(3, lines.Count(line => line.StartsWith("[inference] ", StringComparison.Ordinal)), "Three requests finish without retry/continuation.");
                        AssertFalse(lines.Any(line => line.Contains("asking the captain to continue", StringComparison.Ordinal)));
                        AssertTrue(lines.Any(line => line.Contains("Verified native Ollama thinking", StringComparison.Ordinal)));
                    }
                    finally { Cleanup(dir); }
                }));

                cases.Add(CaseAsync("harbor_reasoning_budget_native_http", "Ollama reasoning truncation reaches Admiral with remediation; sufficient budget finishes without continuation", TestTags.Positive, async () =>
                {
                    foreach (bool reportErrors in new[] { false, true })
                    foreach (int maxTokens in new[] { 4096, 32768 })
                    {
                        string dir = NewTempDir();
                        try
                        {
                            ModelEndpoint endpoint = new ModelEndpoint { Provider = ModelProviderEnum.Ollama, Kind = ModelEndpointKindEnum.Inference, Model = "reasoning-budget",
                                BaseUrl = simulation, InferenceOptions = new InferenceOptions { McpToolsEnabled = false, MaxTokens = maxTokens } };
                            endpoint.ApiKey = "test-token";
                            LocalHarborJobRunner runner = new LocalHarborJobRunner(CreateLogging(), new AgentRuntimeFactory(CreateLogging()), Path.Combine(dir, "scratch"));
                            Armada.Core.Harbor.HarborLaunchRequest request = new Armada.Core.Harbor.HarborLaunchRequest { JobId = "reasoning-budget", Runtime = "ApiEndpoint", WorkingDirectory = dir,
                                Prompt = "Finish the mission with [ARMADA:RESULT] COMPLETE", InferenceEndpoint = Armada.Core.Harbor.HarborInferenceEndpoint.FromEndpoint(endpoint), ReturnProviderErrors = reportErrors };
                            RuntimeProviderError? failure = null;
                            List<string> lines = new List<string>();
                            int code = -1; using ManualResetEventSlim exited = new ManualResetEventSlim(false);
                            await runner.StartAsync(request, null, pid => { }, (stream, text) =>
                            {
                                if (stream == Armada.Core.Harbor.HarborOutputStreamEnum.ProviderError) failure = System.Text.Json.JsonSerializer.Deserialize<RuntimeProviderError>(text);
                                else lines.Add(text);
                            }, value => { code = value; exited.Set(); }, CancellationToken.None).ConfigureAwait(false);
                            AssertTrue(exited.Wait(TimeSpan.FromSeconds(15)), "Harbor HTTP run must finish.");
                            AssertEqual(maxTokens == 32768 ? 0 : 1, code);
                            if (maxTokens == 32768)
                            {
                                AssertEqual(1, lines.Count(line => line.StartsWith("[inference] ", StringComparison.Ordinal)));
                                AssertFalse(lines.Any(line => line.Contains("asking the captain to continue")), "Enough output budget succeeds in one request.");
                            }
                            else if (reportErrors)
                            {
                                AssertNotNull(failure);
                                AssertEqual("output_token_limit_reached", failure!.ErrorType);
                                string reason = Armada.Core.Services.RuntimeFailureClassifier.Decide(code, failure).Describe();
                                AssertContains("4096", reason);
                                AssertContains("Remediation:", reason);
                            }
                            else AssertNull(failure, "An older Admiral must not receive an unknown metadata stream.");
                        }
                        finally { Cleanup(dir); }
                    }
                }));

                cases.Add(CaseAsync("incomplete_completion_native_http", "Real PolyPrompt HTTP parsing preserves empty-terminal recovery and bounded task failure", TestTags.Negative, async () =>
                {
                    foreach (bool recover in new[] { true, false })
                    {
                        string dir = NewTempDir();
                        try
                        {
                            ModelEndpoint endpoint = new ModelEndpoint { Provider = ModelProviderEnum.XAI, Kind = ModelEndpointKindEnum.Inference, Model = recover ? "recover-incomplete" : "always-incomplete", BaseUrl = simulation,
                                InferenceOptions = new InferenceOptions { McpToolsEnabled = false } };
                            endpoint.ApiKey = "test-token";
                            ApiAgentRuntime runtime = new ApiAgentRuntime(endpoint, CreateLogging());
                            int code = -1; RuntimeProviderError? failure = null;
                            using ManualResetEventSlim exited = new ManualResetEventSlim(false);
                            runtime.OnProviderError += (pid, error) => failure = error;
                            runtime.OnProcessExited += (pid, value) => { code = value ?? -1; exited.Set(); };
                            string finalPath = Path.Combine(dir, "final.txt");
                            File.WriteAllText(finalPath, "stale result from an earlier run");
                            await runtime.StartAsync(dir, "Finish the task with [ARMADA:RESULT] COMPLETE", finalMessageFilePath: finalPath).ConfigureAwait(false);
                            AssertTrue(exited.Wait(TimeSpan.FromSeconds(20)), "Native HTTP continuation must be bounded.");
                            AssertEqual(recover ? 0 : 1, code);
                            AssertFalse(File.ReadAllText(finalPath).Contains("stale result"), "The terminal result replaces earlier output even when empty.");
                            if (recover) AssertContains("Recovered", File.ReadAllText(finalPath));
                            else
                        {
                            AssertEqual(RuntimeFailureKindEnum.TaskFailure, Armada.Core.Services.RuntimeFailureClassifier.Classify(code, failure));
                            AssertEqual("incomplete_completion", failure!.ErrorType);
                            AssertContains("Remediation:", Armada.Core.Services.RuntimeFailureClassifier.Decide(code, failure).Describe());
                        }
                        }
                        finally { Cleanup(dir); }
                    }
                }));

                cases.Add(CaseAsync("harbor_endpoint_policy_native_http", "Harbor API captain executes file tools in its worktree and enforces transported endpoint restrictions", TestTags.Positive, async () =>
                {
                    foreach (bool readOnly in new[] { false, true })
                    {
                        string dir = NewTempDir();
                        try
                        {
                            ModelEndpoint endpoint = new ModelEndpoint { Name = "xAI Harbor", Provider = ModelProviderEnum.XAI, Kind = ModelEndpointKindEnum.Inference, Model = "test-model", BaseUrl = simulation,
                                InferenceOptions = new InferenceOptions { ReadOnlyTools = readOnly, McpToolsEnabled = false, MaxIterations = 3, MaxTokens = 256, Temperature = 0, TopP = 1 } };
                            endpoint.ApiKey = "test-token";
                            LocalHarborJobRunner runner = new LocalHarborJobRunner(CreateLogging(), new AgentRuntimeFactory(CreateLogging()), Path.Combine(dir, "scratch"));
                            Armada.Core.Harbor.HarborLaunchRequest request = new Armada.Core.Harbor.HarborLaunchRequest { JobId = "policy-job", Runtime = "ApiEndpoint", WorkingDirectory = dir, Prompt = "Write provider-test.txt",
                                InferenceEndpoint = Armada.Core.Harbor.HarborInferenceEndpoint.FromEndpoint(endpoint), CliPermissionPolicy = CliPermissionPolicyEnum.Refuse };
                            int code = -1; using ManualResetEventSlim exited = new ManualResetEventSlim(false);
                            await runner.StartAsync(request, null, pid => { }, (stream, text) => { }, value => { code = value; exited.Set(); }, CancellationToken.None).ConfigureAwait(false);
                            AssertTrue(exited.Wait(TimeSpan.FromSeconds(15)), "Harbor run must finish.");
                            AssertEqual(0, code);
                            AssertEqual(!readOnly, File.Exists(Path.Combine(dir, "provider-test.txt")), "Transported read-only policy controls Harbor file writes.");
                            if (!readOnly) AssertEqual("native xAI tool loop", File.ReadAllText(Path.Combine(dir, "provider-test.txt")));
                        }
                        finally { Cleanup(dir); }
                    }
                }));

                cases.Add(CaseAsync("xai_native_http_tool_loop", "API captain executes a native xAI tool call and replays provider state over HTTP", TestTags.Positive, async () =>
                {
                    string dir = NewTempDir();
                    try
                    {
                        ModelEndpoint endpoint = new ModelEndpoint { Name = "xAI HTTP", Provider = ModelProviderEnum.XAI, Kind = ModelEndpointKindEnum.Inference, Model = "test-model", BaseUrl = simulation };
                        endpoint.ApiKey = "test-token";
                        ApiAgentRuntime runtime = new ApiAgentRuntime(endpoint, CreateLogging(), 5);
                        int? exitCode = null;
                        using ManualResetEventSlim exited = new ManualResetEventSlim(false);
                        runtime.OnProcessExited += (pid, code) => { exitCode = code; exited.Set(); };
                        string finalPath = Path.Combine(dir, "final.txt");
                        await runtime.StartAsync(dir, "Write provider-test.txt", finalMessageFilePath: finalPath).ConfigureAwait(false);
                        AssertTrue(exited.Wait(TimeSpan.FromSeconds(15)), "Expected HTTP tool loop to finish.");
                        AssertEqual(0, exitCode ?? -1);
                        AssertEqual("native xAI tool loop", File.ReadAllText(Path.Combine(dir, "provider-test.txt")));
                        AssertEqual("pong", File.ReadAllText(finalPath).Trim());
                    }
                    finally { Cleanup(dir); }
                }));
            }

            return new TestSuiteDescriptor(
                suiteId: "Runtimes.ApiAgentRuntime",
                displayName: "API Agent Runtime",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static async Task<ApiRuntimeToolEvent> RunShellAsync(Armada.Core.Enums.CliPermissionPolicyEnum? policy, Func<string, string, CancellationToken, Task<Armada.Core.Models.CliPermissionPromptOutcome>>? prompt)
        {
            string dir = NewTempDir();
            try
            {
                Queue<ToolChatResponse> script = new Queue<ToolChatResponse>();
                script.Enqueue(new ToolChatResponse
                {
                    Success = true,
                    Text = "Running.",
                    ToolCalls = new List<ToolCall> { new ToolCall { Id = "sh1", Name = "run_process", ArgumentsJson = "{\"command\":\"echo hi\"}" } }
                });
                script.Enqueue(new ToolChatResponse { Success = true, Text = "Done.", ToolCalls = new List<ToolCall>() });
                ModelEndpoint endpoint = new ModelEndpoint { Name = "test-endpoint", Provider = ModelProviderEnum.OpenAICompatible, Kind = ModelEndpointKindEnum.Inference, Model = "test-model", BaseUrl = "http://localhost:1" };
                ApiAgentRuntime runtime = new ApiAgentRuntime(endpoint, CreateLogging(), 20, (ep, log) => new ScriptedClient(script, log));
                runtime.ShellPolicy = policy;
                runtime.PermissionPrompt = prompt;
                List<ApiRuntimeToolEvent> events = new List<ApiRuntimeToolEvent>();
                ManualResetEventSlim exited = new ManualResetEventSlim(false);
                runtime.OnToolEvent += (pid, e) => { lock (events) events.Add(e); };
                runtime.OnProcessExited += (pid, code) => exited.Set();
                await runtime.StartAsync(dir, "Run echo.").ConfigureAwait(false);
                AssertTrue(exited.Wait(TimeSpan.FromSeconds(30)), "loop completed");
                lock (events) return events.Single(e => e.Phase == ApiRuntimeToolPhaseEnum.Completed && e.Id == "sh1");
            }
            finally
            {
                Cleanup(dir);
            }
        }

        private static System.Text.Json.JsonElement ParseArgs(string json)
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }

        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "armada-api-runtime-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Cleanup(string dir)
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "Runtimes.ApiAgentRuntime",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion

        #region Fake-Clients

        private sealed class ScriptedClient : CompletionClientBase
        {
            public List<string> ToolNames { get; private set; } = new List<string>();
            public CompletionOptions? Settings { get; private set; }
            public bool PlainChatCalled { get; private set; }
            private readonly Queue<ToolChatResponse> _Script;
            private readonly CompletionOptions _Defaults = new CompletionOptions { Model = "scripted-model" };

            public ScriptedClient(Queue<ToolChatResponse> script, LoggingModule logging)
                : base("http://localhost:1", null, logging)
            {
                _Script = script;
            }

            public override CompletionOptions Defaults => _Defaults;

            protected override Task<ToolChatResponse> ToolChatCoreAsync(ToolChatRequest request, List<ChatMessage> messages, ResolvedCompletion settings, CancellationToken token)
            {
                ToolNames = request.Tools.Select(t => t.Name).ToList();
                Settings = new CompletionOptions { Model = settings.Model, MaxTokens = settings.MaxTokens, Temperature = settings.Temperature, TopP = settings.TopP, ReasoningEffort = settings.ReasoningEffort };
                if (_Script.Count == 0) return Task.FromResult(new ToolChatResponse { Success = true, Text = "", ToolCalls = new List<ToolCall>() });
                return Task.FromResult(_Script.Dequeue());
            }

            public override Task<bool> ValidateConnectivityAsync(CancellationToken token = default) => Task.FromResult(true);
            protected override Task<ChatResponse> ChatCoreAsync(string prompt, ResolvedCompletion settings, CancellationToken token) { PlainChatCalled = true; Settings = new CompletionOptions { Model = settings.Model, MaxTokens = settings.MaxTokens, Temperature = settings.Temperature, TopP = settings.TopP, ReasoningEffort = settings.ReasoningEffort }; return Task.FromResult(new ChatResponse { Success = true, Text = "plain answer" }); }
            protected override Task<ChatStreamingResponse> ChatStreamingCoreAsync(string prompt, ResolvedCompletion settings, CancellationToken token) => throw new NotImplementedException();
            protected override Task<ToolChatStreamingResponse> ToolChatStreamingCoreAsync(ToolChatRequest request, List<ChatMessage> messages, ResolvedCompletion settings, CancellationToken token) => throw new NotImplementedException();
            protected override Task<GenerationResponse> GenerateCoreAsync(string prompt, ResolvedCompletion settings, CancellationToken token) => throw new NotImplementedException();
            protected override Task<GenerationStreamingResponse> GenerateStreamingCoreAsync(string prompt, ResolvedCompletion settings, CancellationToken token) => throw new NotImplementedException();
        }

        private sealed class BlockingClient : CompletionClientBase
        {
            private readonly CompletionOptions _Defaults = new CompletionOptions { Model = "blocking-model" };

            public BlockingClient(LoggingModule logging) : base("http://localhost:1", null, logging) { }

            public override CompletionOptions Defaults => _Defaults;

            protected override async Task<ToolChatResponse> ToolChatCoreAsync(ToolChatRequest request, List<ChatMessage> messages, ResolvedCompletion settings, CancellationToken token)
            {
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return new ToolChatResponse { Success = true, ToolCalls = new List<ToolCall>() };
            }

            public override Task<bool> ValidateConnectivityAsync(CancellationToken token = default) => Task.FromResult(true);
            protected override Task<ChatResponse> ChatCoreAsync(string prompt, ResolvedCompletion settings, CancellationToken token) => throw new NotImplementedException();
            protected override Task<ChatStreamingResponse> ChatStreamingCoreAsync(string prompt, ResolvedCompletion settings, CancellationToken token) => throw new NotImplementedException();
            protected override Task<ToolChatStreamingResponse> ToolChatStreamingCoreAsync(ToolChatRequest request, List<ChatMessage> messages, ResolvedCompletion settings, CancellationToken token) => throw new NotImplementedException();
            protected override Task<GenerationResponse> GenerateCoreAsync(string prompt, ResolvedCompletion settings, CancellationToken token) => throw new NotImplementedException();
            protected override Task<GenerationStreamingResponse> GenerateStreamingCoreAsync(string prompt, ResolvedCompletion settings, CancellationToken token) => throw new NotImplementedException();
        }

        private sealed class TimeoutClient : CompletionClientBase
        {
            private readonly CompletionOptions _Defaults = new CompletionOptions { Model = "timeout-model" };

            public TimeoutClient(LoggingModule logging) : base("http://localhost:1", null, logging) { }

            public override CompletionOptions Defaults => _Defaults;

            protected override Task<ToolChatResponse> ToolChatCoreAsync(ToolChatRequest request, List<ChatMessage> messages, ResolvedCompletion settings, CancellationToken token)
            {
                // What HttpClient throws when its own Timeout elapses: a cancellation the caller never requested.
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.");
            }

            public override Task<bool> ValidateConnectivityAsync(CancellationToken token = default) => Task.FromResult(true);
            protected override Task<ChatResponse> ChatCoreAsync(string prompt, ResolvedCompletion settings, CancellationToken token) => throw new NotImplementedException();
            protected override Task<ChatStreamingResponse> ChatStreamingCoreAsync(string prompt, ResolvedCompletion settings, CancellationToken token) => throw new NotImplementedException();
            protected override Task<ToolChatStreamingResponse> ToolChatStreamingCoreAsync(ToolChatRequest request, List<ChatMessage> messages, ResolvedCompletion settings, CancellationToken token) => throw new NotImplementedException();
            protected override Task<GenerationResponse> GenerateCoreAsync(string prompt, ResolvedCompletion settings, CancellationToken token) => throw new NotImplementedException();
            protected override Task<GenerationStreamingResponse> GenerateStreamingCoreAsync(string prompt, ResolvedCompletion settings, CancellationToken token) => throw new NotImplementedException();
        }

        #endregion
    }
}
