namespace Test.Shared.Suites.Runtimes
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Runtimes;
    using Armada.Runtimes.Interfaces;
    using Armada.Runtimes.Tools;
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

            cases.Add(CaseAsync("tools_write_then_read_round_trip", "Ported write_file and read_file round-trip in a working directory", TestTags.Positive, async () =>
            {
                string dir = NewTempDir();
                try
                {
                    BuiltInToolRegistry registry = new BuiltInToolRegistry(null);
                    AssertTrue(registry.HasTool("write_file"), "Expected write_file to be registered.");
                    AssertTrue(registry.HasTool("read_file"), "Expected read_file to be registered.");
                    AssertTrue(!registry.HasTool("run_process"), "The API endpoint runtime must not expose an unrestricted shell tool.");

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

            cases.Add(CaseAsync("workspace_symlink_escape_is_rejected", "Workspace file tools reject symlink escapes", TestTags.Negative, async () =>
            {
                string dir = NewTempDir();
                string outside = NewTempDir();
                try
                {
                    Directory.CreateSymbolicLink(Path.Combine(dir, "outside"), outside);
                    BuiltInToolRegistry registry = new BuiltInToolRegistry(null);
                    ToolResult result = await registry.ExecuteAsync("escape", "write_file",
                        ParseArgs("{\"file_path\":\"outside/escaped.txt\",\"content\":\"must not write\"}"), dir, CancellationToken.None).ConfigureAwait(false);
                    AssertTrue(!result.Success, "A symlink escape must be rejected by the workspace policy.");
                    AssertTrue(!File.Exists(Path.Combine(outside, "escaped.txt")), "A rejected path must not write outside the workspace.");
                }
                finally
                {
                    Cleanup(dir);
                    Cleanup(outside);
                }
            }));

            cases.Add(CaseAsync("workspace_case_distinct_sibling_is_rejected", "Workspace paths use host case sensitivity", TestTags.Negative, async () =>
            {
                if (OperatingSystem.IsWindows()) return;
                string parent = NewTempDir();
                string root = Path.Combine(parent, "CaseRoot");
                string sibling = Path.Combine(parent, "caseroot");
                try
                {
                    Directory.CreateDirectory(root);
                    Directory.CreateDirectory(sibling);
                    AssertThrows<WorkspaceBoundaryException>(() => WorkspacePathPolicy.ResolvePath(Path.Combine(parent, "caseroot", "outside.txt"), root));
                }
                finally
                {
                    Cleanup(parent);
                }
            }));

            cases.Add(CaseAsync("recursive_file_tools_reject_symlink_descendants", "Recursive file tools fail closed on symlink descendants", TestTags.Negative, async () =>
            {
                string dir = NewTempDir();
                string outside = NewTempDir();
                try
                {
                    File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside-secret");
                    Directory.CreateSymbolicLink(Path.Combine(dir, "linked"), outside);
                    BuiltInToolRegistry registry = new BuiltInToolRegistry(null);
                    ToolResult glob = await registry.ExecuteAsync("glob-link", "glob", ParseArgs("{\"pattern\":\"**/*\"}"), dir, CancellationToken.None).ConfigureAwait(false);
                    ToolResult grep = await registry.ExecuteAsync("grep-link", "grep", ParseArgs("{\"pattern\":\"secret\"}"), dir, CancellationToken.None).ConfigureAwait(false);
                    AssertTrue(!glob.Success, "Glob must reject a symlink descendant.");
                    AssertTrue(!grep.Success, "Grep must reject a symlink descendant.");
                    AssertTrue(!glob.Content.Contains("outside-secret", StringComparison.Ordinal), "Glob must not expose outside content.");
                    AssertTrue(!grep.Content.Contains("outside-secret", StringComparison.Ordinal), "Grep must not expose outside content.");
                }
                finally
                {
                    Cleanup(dir);
                    Cleanup(outside);
                }
            }));

            cases.Add(CaseAsync("workspace_tool_limits_are_bounded", "Workspace tools enforce input, output, and cancellation limits", TestTags.Negative, async () =>
            {
                string dir = NewTempDir();
                int oldRead = ToolSafetyLimits.MaxReadFileBytes;
                int oldInput = ToolSafetyLimits.MaxToolInputBytes;
                int oldOutput = ToolSafetyLimits.MaxToolOutputBytes;
                try
                {
                    ToolSafetyLimits.MaxReadFileBytes = 64;
                    ToolSafetyLimits.MaxToolInputBytes = 64;
                    ToolSafetyLimits.MaxToolOutputBytes = 64;
                    File.WriteAllText(Path.Combine(dir, "large.txt"), new string('x', 128));
                    BuiltInToolRegistry registry = new BuiltInToolRegistry(null);
                    ToolResult largeRead = await registry.ExecuteAsync("large", "read_file", ParseArgs("{\"file_path\":\"large.txt\"}"), dir, CancellationToken.None).ConfigureAwait(false);
                    AssertTrue(!largeRead.Success, "Read must reject oversized files.");
                    ToolResult largeWrite = await registry.ExecuteAsync("write-large", "write_file", ParseArgs("{\"file_path\":\"write.txt\",\"content\":\"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\"}"), dir, CancellationToken.None).ConfigureAwait(false);
                    AssertTrue(!largeWrite.Success, "Write must reject oversized input.");
                    for (int i = 0; i < 20; i++) File.WriteAllText(Path.Combine(dir, "file-" + i + ".txt"), "x");
                    ToolResult listing = await registry.ExecuteAsync("list", "list_directory", ParseArgs("{\"path\":\".\"}"), dir, CancellationToken.None).ConfigureAwait(false);
                    AssertTrue(listing.Success, "A bounded listing should complete.");
                    AssertTrue(listing.Content.Contains("output truncated", StringComparison.Ordinal), "Large tool output must identify truncation.");
                    using CancellationTokenSource cancelled = new CancellationTokenSource();
                    cancelled.Cancel();
                    ToolResult cancellation = await registry.ExecuteAsync("cancel", "glob", ParseArgs("{\"pattern\":\"**/*\"}"), dir, cancelled.Token).ConfigureAwait(false);
                    AssertTrue(!cancellation.Success && cancellation.Content.Contains("cancelled", StringComparison.Ordinal), "Cancelled recursive work must report cancellation.");
                }
                finally
                {
                    ToolSafetyLimits.MaxReadFileBytes = oldRead;
                    ToolSafetyLimits.MaxToolInputBytes = oldInput;
                    ToolSafetyLimits.MaxToolOutputBytes = oldOutput;
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
                    AssertTrue(result.Content.Contains("unknown_tool"), "Expected an unknown_tool error payload.");
                }
                finally
                {
                    Cleanup(dir);
                }
            }));

            cases.Add(CaseAsync("pre_cancelled_start_does_not_register", "A pre-cancelled API run does not emit started or register a process", TestTags.Negative, async () =>
            {
                string dir = NewTempDir();
                try
                {
                    ModelEndpoint endpoint = NewEndpoint("pre-cancelled");
                    ApiAgentRuntime runtime = new ApiAgentRuntime(endpoint, CreateLogging(), 1, (ep, log) => new ScriptedClient(new Queue<ToolChatResponse>(), log));
                    bool started = false;
                    runtime.OnProcessStarted += pid => started = true;
                    using CancellationTokenSource cancelled = new CancellationTokenSource();
                    cancelled.Cancel();
                    await AssertThrowsAsync<OperationCanceledException>(() => runtime.StartAsync(dir, "cancel before start", token: cancelled.Token)).ConfigureAwait(false);
                    AssertFalse(started, "Pre-cancelled start must not emit a started event.");
                }
                finally
                {
                    Cleanup(dir);
                }
            }));

            cases.Add(CaseAsync("started_event_failure_cleans_registration", "A started-event failure cleans the synthetic process registration", TestTags.Negative, async () =>
            {
                string dir = NewTempDir();
                try
                {
                    ModelEndpoint endpoint = NewEndpoint("event-failure");
                    ApiAgentRuntime runtime = new ApiAgentRuntime(endpoint, CreateLogging(), 1, (ep, log) => new ScriptedClient(new Queue<ToolChatResponse>(), log));
                    int processId = 0;
                    runtime.OnProcessStarted += pid =>
                    {
                        processId = pid;
                        throw new InvalidOperationException("started subscriber failed");
                    };
                    await AssertThrowsAsync<InvalidOperationException>(() => runtime.StartAsync(dir, "event failure")).ConfigureAwait(false);
                    AssertTrue(processId > 0, "The started callback must receive the synthetic process id.");
                    AssertFalse(ApiAgentRuntime.IsTracked(processId), "A failed started callback must not leave a tracked run.");
                    AssertFalse(Armada.Core.ProcessSupervisor.IsTrackedProcessAlive(processId), "A failed started callback must not leave synthetic liveness.");
                }
                finally
                {
                    Cleanup(dir);
                }
            }));

            cases.Add(CaseAsync("unsuccessful_empty_error_is_failure", "An unsuccessful provider response without an error is still a failed run", TestTags.Negative, async () =>
            {
                string dir = NewTempDir();
                try
                {
                    Queue<ToolChatResponse> script = new Queue<ToolChatResponse>();
                    script.Enqueue(new ToolChatResponse { Success = false, Error = String.Empty, Text = "must not succeed" });
                    ApiAgentRuntime runtime = new ApiAgentRuntime(NewEndpoint("unsuccessful"), CreateLogging(), 1, (ep, log) => new ScriptedClient(script, log));
                    List<string> output = new List<string>();
                    int? exitCode = null;
                    using ManualResetEventSlim exited = new ManualResetEventSlim(false);
                    runtime.OnOutputReceived += (pid, line) => output.Add(line);
                    runtime.OnProcessExited += (pid, code) => { exitCode = code; exited.Set(); };
                    await runtime.StartAsync(dir, "provider failure").ConfigureAwait(false);
                    AssertTrue(exited.Wait(TimeSpan.FromSeconds(5)), "The failed provider run must exit.");
                    AssertEqual(1, exitCode ?? 0);
                    AssertTrue(output.Exists(line => line.Contains("unsuccessful response", StringComparison.Ordinal)), "The failure must have a safe fallback message.");
                    AssertFalse(output.Exists(line => line == "must not succeed"), "Failed provider text must not be emitted as a final answer.");
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

                    ModelEndpoint endpoint = new ModelEndpoint { Name = "test-endpoint", Provider = ModelProviderEnum.OpenAICompatible, Kind = ModelEndpointKindEnum.Inference, Model = "test-model", BaseUrl = "http://localhost:1", Enabled = true };
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
                    AssertEqual(0, exitCode ?? -1, "Local HTTP runtime output: " + String.Join(" | ", output));
                    AssertTrue(File.Exists(Path.Combine(dir, "out.txt")), "Expected the tool to have created out.txt.");
                    AssertTrue(File.Exists(finalPath), "Expected the final message file to be written.");
                    AssertTrue(File.ReadAllText(finalPath).Contains("Created out.txt"), "Expected the final message text.");
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
                    ModelEndpoint endpoint = new ModelEndpoint { Name = "blocking", Provider = ModelProviderEnum.OpenAICompatible, Kind = ModelEndpointKindEnum.Inference, Model = "m", BaseUrl = "http://localhost:1", Enabled = true };
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

            cases.Add(CaseAsync("local_http_model_tool_round_trip_is_bounded", "A local model can advertise a workspace tool and receive its bounded result", TestTags.Positive, async () =>
            {
                string dir = NewTempDir();
                string finalPath = Path.Combine(dir, "final.txt");
                TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                List<string> requests = new List<string>();
                Task server = ServeOpenAiRoundTripAsync(listener, requests);
                try
                {
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    ModelEndpoint endpoint = new ModelEndpoint
                    {
                        Name = "local-http",
                        Provider = ModelProviderEnum.OpenAICompatible,
                        Kind = ModelEndpointKindEnum.Inference,
                        Model = "fixture-model",
                        BaseUrl = "http://127.0.0.1:" + port,
                        ApiKey = "endpoint-secret",
                        Enabled = true
                    };
                    ApiAgentRuntime runtime = new ApiAgentRuntime(endpoint, CreateLogging(), 2);
                    List<RuntimeTokenUsage> progress = new List<RuntimeTokenUsage>();
                    List<RuntimeTokenUsage> usage = new List<RuntimeTokenUsage>();
                    List<string> output = new List<string>();
                    int? exitCode = null;
                    using ManualResetEventSlim exited = new ManualResetEventSlim(false);
                    runtime.OnProviderProgressReceived += (pid, usage) => progress.Add(usage);
                    runtime.OnTokenUsageReceived += (pid, sample) => usage.Add(sample);
                    runtime.OnOutputReceived += (pid, line) => output.Add(line);
                    runtime.OnProcessExited += (pid, code) => { exitCode = code; exited.Set(); };

                    int pid = await runtime.StartAsync(dir, "Create the requested fixture file.", finalMessageFilePath: finalPath).ConfigureAwait(false);
                    AssertTrue(exited.Wait(TimeSpan.FromSeconds(10)), "The local model round trip must complete within the bounded test timeout.");
                    AssertEqual(0, exitCode ?? -1, "Local HTTP runtime output: " + String.Join(" | ", output));
                    await server.ConfigureAwait(false);
                    AssertEqual(2, requests.Count);
                    AssertContains("\"tools\"", requests[0], "Model request tool catalog");
                    AssertContains("\"model\":\"fixture-model\"", requests[0], "Model request endpoint model");
                    AssertContains("write_file", requests[0], "Model request workspace tool");
                    AssertFalse(requests[0].Contains("admin", StringComparison.OrdinalIgnoreCase), "The runtime must not advertise administrative tools or credentials.");
                    AssertContains("endpoint-secret", requests[0], "Endpoint credential header");
                    AssertContains("local model wrote this", requests[1], "Bounded tool result in the follow-up request");
                    AssertTrue(File.Exists(Path.Combine(dir, "roundtrip.txt")), "The advertised workspace tool must run in the workspace.");
                    AssertContains("final answer", File.ReadAllText(finalPath), "Final model answer");
                    AssertEqual(2, progress.Count);
                    AssertTrue(progress.TrueForAll(item => item.Source == "api_endpoint" && item.Model == "fixture-model"), "Each provider progress event must identify the configured endpoint model.");
                    AssertEqual(2, usage.Count);
                    AssertEqual(12L, usage[0].InputTokens);
                    AssertEqual(7L, usage[0].OutputTokens);
                    AssertEqual(19L, usage[0].ProviderTotalTokens ?? 0L);
                }
                finally
                {
                    listener.Stop();
                    try { await server.ConfigureAwait(false); } catch { }
                    Cleanup(dir);
                }
            }));

            cases.Add(CaseAsync("local_http_cancellation_stops_blocked_request", "Cancellation stops a local HTTP request that has not produced a model response", TestTags.Negative, async () =>
            {
                string dir = NewTempDir();
                TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                TaskCompletionSource<bool> received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task server = ServeBlockedHttpAsync(listener, received);
                try
                {
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    ModelEndpoint endpoint = new ModelEndpoint
                    {
                        Name = "local-blocked",
                        Provider = ModelProviderEnum.OpenAICompatible,
                        Kind = ModelEndpointKindEnum.Inference,
                        Model = "fixture-model",
                        BaseUrl = "http://127.0.0.1:" + port,
                        Enabled = true
                    };
                    ApiAgentRuntime runtime = new ApiAgentRuntime(endpoint, CreateLogging(), 2);
                    int? exitCode = null;
                    using ManualResetEventSlim exited = new ManualResetEventSlim(false);
                    runtime.OnProcessExited += (pid, code) => { exitCode = code; exited.Set(); };
                    int pid = await runtime.StartAsync(dir, "Wait for the blocked fixture.").ConfigureAwait(false);
                    await received.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    await runtime.StopAsync(pid).ConfigureAwait(false);
                    AssertTrue(exited.Wait(TimeSpan.FromSeconds(5)), "Cancellation must stop a blocked HTTP request promptly.");
                    AssertEqual(-1, exitCode ?? 0);
                }
                finally
                {
                    listener.Stop();
                    try { await server.ConfigureAwait(false); } catch { }
                    Cleanup(dir);
                }
            }));

            cases.Add(CaseAsync("local_http_oversized_response_is_rejected", "An oversized model response is rejected before the runtime keeps it in history", TestTags.Negative, async () =>
            {
                string dir = NewTempDir();
                TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                Task server = ServeOversizedHttpAsync(listener);
                try
                {
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    ModelEndpoint endpoint = new ModelEndpoint
                    {
                        Name = "local-oversized",
                        Provider = ModelProviderEnum.OpenAICompatible,
                        Kind = ModelEndpointKindEnum.Inference,
                        Model = "fixture-model",
                        BaseUrl = "http://127.0.0.1:" + port,
                        Enabled = true
                    };
                    ApiAgentRuntime runtime = new ApiAgentRuntime(endpoint, CreateLogging(), 1);
                    List<string> output = new List<string>();
                    int? exitCode = null;
                    using ManualResetEventSlim exited = new ManualResetEventSlim(false);
                    runtime.OnOutputReceived += (pid, line) => output.Add(line);
                    runtime.OnProcessExited += (pid, code) => { exitCode = code; exited.Set(); };
                    await runtime.StartAsync(dir, "Reject an oversized response.").ConfigureAwait(false);
                    AssertTrue(exited.Wait(TimeSpan.FromSeconds(10)), "The oversized response must fail within the bounded timeout.");
                    AssertEqual(1, exitCode ?? 0);
                    AssertTrue(output.Exists(line => line.Contains("allowed response limit", StringComparison.Ordinal)), "The runtime must report the bounded response failure.");
                }
                finally
                {
                    listener.Stop();
                    try { await server.ConfigureAwait(false); } catch { }
                    Cleanup(dir);
                }
            }));

            cases.Add(CaseAsync("runtime_bounds_turns_and_output_and_reports_progress", "The API loop bounds tool turns and reports provider progress", TestTags.Negative, async () =>
            {
                string dir = NewTempDir();
                int oldOutput = ToolSafetyLimits.MaxProcessOutputBytes;
                try
                {
                    ToolSafetyLimits.MaxProcessOutputBytes = 128;
                    Queue<ToolChatResponse> script = new Queue<ToolChatResponse>();
                    script.Enqueue(new ToolChatResponse
                    {
                        Success = true,
                        Text = new String('z', 4096),
                        ToolCalls = new List<ToolCall> { new ToolCall { Id = "bounded-1", Name = "list_directory", ArgumentsJson = "{\"path\":\".\"}" } }
                    });
                    ModelEndpoint endpoint = new ModelEndpoint
                    {
                        Name = "bounded",
                        Provider = ModelProviderEnum.OpenAICompatible,
                        Kind = ModelEndpointKindEnum.Inference,
                        Model = "fixture-model",
                        BaseUrl = "http://127.0.0.1:1",
                        Enabled = true
                    };
                    ApiAgentRuntime runtime = new ApiAgentRuntime(endpoint, CreateLogging(), 1, (ep, log) => new ScriptedClient(script, log));
                    List<string> output = new List<string>();
                    List<RuntimeTokenUsage> progress = new List<RuntimeTokenUsage>();
                    int? exitCode = null;
                    using ManualResetEventSlim exited = new ManualResetEventSlim(false);
                    runtime.OnOutputReceived += (pid, line) => output.Add(line);
                    runtime.OnProviderProgressReceived += (pid, usage) => progress.Add(usage);
                    runtime.OnProcessExited += (pid, code) => { exitCode = code; exited.Set(); };
                    int pid = await runtime.StartAsync(dir, "Bound this run.").ConfigureAwait(false);
                    AssertTrue(exited.Wait(TimeSpan.FromSeconds(5)), "Bounded loop must finish.");
                    AssertEqual(2, exitCode ?? -1);
                    AssertTrue(output.Exists(line => line.Contains("maximum tool iterations reached (1)", StringComparison.Ordinal)), "The runtime must stop at the configured turn bound.");
                    AssertTrue(output.Exists(line => line.Contains("output truncated at 128 bytes", StringComparison.Ordinal)), "The runtime must bound model output.");
                    AssertEqual(1, progress.Count);
                    AssertEqual("fixture-model", progress[0].Model);
                }
                finally
                {
                    ToolSafetyLimits.MaxProcessOutputBytes = oldOutput;
                    Cleanup(dir);
                }
            }));

            cases.Add(CaseAsync("cancelled_file_edits_preserve_original_bytes", "Cancelled or invalid file edits preserve the original bytes", TestTags.Negative, async () =>
            {
                string dir = NewTempDir();
                string path = Path.Combine(dir, "original.txt");
                const string original = "first\r\nsecond\r\n";
                try
                {
                    File.WriteAllText(path, original, new UTF8Encoding(false));
                    BuiltInToolRegistry registry = new BuiltInToolRegistry(null);
                    using CancellationTokenSource cancelled = new CancellationTokenSource();
                    cancelled.Cancel();
                    ToolResult write = await registry.ExecuteAsync("cancel-write", "write_file", ParseArgs("{\"file_path\":\"original.txt\",\"content\":\"replacement\"}"), dir, cancelled.Token).ConfigureAwait(false);
                    AssertFalse(write.Success, "Cancelled write must fail.");
                    AssertEqual(original, File.ReadAllText(path), "Cancelled write must preserve original bytes.");

                    byte[] invalidUtf8 = new byte[] { 0x66, 0x69, 0x72, 0x73, 0x74, 0xFF };
                    await File.WriteAllBytesAsync(path, invalidUtf8).ConfigureAwait(false);
                    ToolResult edit = await registry.ExecuteAsync("invalid-edit", "edit_file", ParseArgs("{\"file_path\":\"original.txt\",\"old_string\":\"first\",\"new_string\":\"changed\"}"), dir, CancellationToken.None).ConfigureAwait(false);
                    AssertFalse(edit.Success, "Invalid UTF-8 edit must fail closed.");
                    byte[] afterInvalidEdit = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
                    AssertTrue(ByteArraysEqual(invalidUtf8, afterInvalidEdit), "Invalid UTF-8 edit must preserve original bytes.");

                    await File.WriteAllTextAsync(path, original, new UTF8Encoding(false)).ConfigureAwait(false);
                    UnixFileMode? originalMode = null;
                    if (!OperatingSystem.IsWindows())
                    {
                        originalMode = File.GetUnixFileMode(path);
                    }

                    using CancellationTokenSource stagedCancellation = new CancellationTokenSource();
                    ToolExecution.BeforeAtomicReplaceAsync = token =>
                    {
                        stagedCancellation.Cancel();
                        return Task.CompletedTask;
                    };
                    ToolResult stagedWrite = await registry.ExecuteAsync("staged-cancel-write", "write_file", ParseArgs("{\"file_path\":\"original.txt\",\"content\":\"replacement after staging\"}"), dir, stagedCancellation.Token).ConfigureAwait(false);
                    AssertFalse(stagedWrite.Success, "Cancellation after staging must fail before replacement.");
                    AssertEqual(original, File.ReadAllText(path), "Cancellation after staging must preserve original bytes.");
                    AssertTrue(Directory.GetFiles(dir, ".armada-write-*.tmp").Length == 0, "Cancellation after staging must clean the temporary file.");
                    ToolExecution.BeforeAtomicReplaceAsync = null;

                    ToolResult modeWrite = await registry.ExecuteAsync("mode-write", "write_file", ParseArgs("{\"file_path\":\"original.txt\",\"content\":\"mode preserved\"}"), dir, CancellationToken.None).ConfigureAwait(false);
                    AssertTrue(modeWrite.Success, "A normal atomic write must succeed after a cancelled staged write.");
                    if (originalMode.HasValue)
                    {
                        AssertEqual(originalMode.Value, File.GetUnixFileMode(path), "Atomic replacement must preserve the existing Unix file mode.");
                    }
                }
                finally
                {
                    ToolExecution.BeforeAtomicReplaceAsync = null;
                    Cleanup(dir);
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: "Runtimes.ApiAgentRuntime",
                displayName: "API Agent Runtime",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static string ParseArgs(string json)
        {
            return json;
        }

        private static ModelEndpoint NewEndpoint(string name)
        {
            return new ModelEndpoint
            {
                Name = name,
                Provider = ModelProviderEnum.OpenAICompatible,
                Kind = ModelEndpointKindEnum.Inference,
                Model = "fixture-model",
                BaseUrl = "http://127.0.0.1:1",
                Enabled = true
            };
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

        private static bool ByteArraysEqual(byte[] first, byte[] second)
        {
            if (first.Length != second.Length) return false;
            for (int i = 0; i < first.Length; i++)
                if (first[i] != second[i]) return false;
            return true;
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static async Task ServeOpenAiRoundTripAsync(TcpListener listener, List<string> requests)
        {
            for (int i = 0; i < 2; i++)
            {
                using TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                using NetworkStream stream = client.GetStream();
                string request = await ReadHttpRequestAsync(stream).ConfigureAwait(false);
                requests.Add(request);
                string[] response = i == 0
                    ? new[]
                    {
                        "{\"id\":\"fixture-1\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"tool_calls\":[{\"index\":0,\"id\":\"call-1\",\"type\":\"function\",\"function\":{\"name\":\"write_file\",\"arguments\":\"{\\\"file_path\\\":\\\"roundtrip.txt\\\",\\\"content\\\":\\\"local model wrote this\\\"}\"}}]},\"finish_reason\":null}],\"usage\":null}",
                        "{\"id\":\"fixture-1\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":7,\"total_tokens\":19}}"
                    }
                    : new[]
                    {
                        "{\"id\":\"fixture-2\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"final answer\"},\"finish_reason\":null}],\"usage\":null}",
                        "{\"id\":\"fixture-2\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":20,\"completion_tokens\":3,\"total_tokens\":23}}"
                    };
                await WriteHttpStreamingResponseAsync(stream, response).ConfigureAwait(false);
            }
        }

        private static async Task ServeBlockedHttpAsync(TcpListener listener, TaskCompletionSource<bool> received)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            using NetworkStream stream = client.GetStream();
            await ReadHttpRequestAsync(stream).ConfigureAwait(false);
            received.TrySetResult(true);
            try { await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); } catch { }
        }

        private static async Task ServeOversizedHttpAsync(TcpListener listener)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            using NetworkStream stream = client.GetStream();
            await ReadHttpRequestAsync(stream).ConfigureAwait(false);
            string body = "data: {\"id\":\"oversized\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"" + new String('x', 4 * 1024 * 1024 + 1) + "\"},\"finish_reason\":null}]}\n\n";
            await WriteHttpResponseAsync(stream, body).ConfigureAwait(false);
        }

        private static async Task<string> ReadHttpRequestAsync(NetworkStream stream)
        {
            using MemoryStream bytes = new MemoryStream();
            byte[] buffer = new byte[4096];
            int headerEnd = -1;
            int contentLength = 0;
            while (headerEnd < 0 || bytes.Length < headerEnd + 4 + contentLength)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                if (count == 0) break;
                bytes.Write(buffer, 0, count);
                byte[] snapshot = bytes.ToArray();
                if (headerEnd < 0)
                {
                    for (int i = 3; i < snapshot.Length; i++)
                    {
                        if (snapshot[i - 3] == '\r' && snapshot[i - 2] == '\n' && snapshot[i - 1] == '\r' && snapshot[i] == '\n')
                        {
                            headerEnd = i - 3;
                            string headers = Encoding.ASCII.GetString(snapshot, 0, headerEnd);
                            foreach (string line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                    Int32.TryParse(line.Substring("Content-Length:".Length).Trim(), out contentLength);
                            break;
                        }
                    }
                }
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        private static async Task WriteHttpResponseAsync(NetworkStream stream, string body)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            string headers = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers)).ConfigureAwait(false);
            await stream.WriteAsync(bytes).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        private static async Task WriteHttpStreamingResponseAsync(NetworkStream stream, string[] chunks)
        {
            StringBuilder body = new StringBuilder();
            foreach (string chunk in chunks)
                body.Append("data: ").Append(chunk).Append("\n\n");
            body.Append("data: [DONE]\n\n");
            byte[] bytes = Encoding.UTF8.GetBytes(body.ToString());
            string headers = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers)).ConfigureAwait(false);
            await stream.WriteAsync(bytes).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
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
            private readonly Queue<ToolChatResponse> _Script;

            public ScriptedClient(Queue<ToolChatResponse> script, LoggingModule logging)
                : base("http://localhost:1", null, logging)
            {
                _Script = script;
            }

            public override Task<ToolChatResponse> ToolChatAsync(ToolChatRequest request, CancellationToken token = default)
            {
                if (_Script.Count == 0) return Task.FromResult(new ToolChatResponse { Success = true, Text = "", ToolCalls = new List<ToolCall>() });
                return Task.FromResult(_Script.Dequeue());
            }

            public override Task<ChatResponse> ChatAsync(string prompt, ChatCompletionOptions? options = null, CancellationToken token = default) => throw new NotImplementedException();
            public override Task<ChatStreamingResponse> ChatStreamingAsync(string prompt, ChatCompletionOptions? options = null, CancellationToken token = default) => throw new NotImplementedException();
            public override Task<ToolChatStreamingResponse> ToolChatStreamingAsync(ToolChatRequest request, CancellationToken token = default)
            {
                ToolChatResponse response = _Script.Count == 0
                    ? new ToolChatResponse { Success = true, Text = "", ToolCalls = new List<ToolCall>() }
                    : _Script.Dequeue();
                ToolChatStreamingResponse streamed = new ToolChatStreamingResponse
                {
                    Success = response.Success,
                    Text = response.Text,
                    Reasoning = response.Reasoning,
                    ToolCalls = response.ToolCalls,
                    Model = response.Model,
                    ResponseId = response.ResponseId,
                    FinishReason = response.FinishReason,
                    StatusCode = response.StatusCode,
                    Error = response.Error
                };
                return Task.FromResult(streamed);
            }
            public override Task<EmbeddingResponse> EmbedAsync(string input, EmbeddingOptions? options = null, CancellationToken token = default) => throw new NotImplementedException();
            public override Task<EmbeddingResponse> EmbedAsync(List<string> inputs, EmbeddingOptions? options = null, CancellationToken token = default) => throw new NotImplementedException();
            public override Task<GenerationResponse> GenerateAsync(string prompt, GenerationOptions? options = null, CancellationToken token = default) => throw new NotImplementedException();
            public override Task<GenerationStreamingResponse> GenerateStreamingAsync(string prompt, GenerationOptions? options = null, CancellationToken token = default) => throw new NotImplementedException();
            public override IAsyncEnumerable<ModelInformation> ListModelsAsync(CancellationToken token = default) => throw new NotImplementedException();
            public override Task<ModelInformation?> GetModelInformationAsync(string model, CancellationToken token = default) => throw new NotImplementedException();
        }

        private sealed class BlockingClient : CompletionClientBase
        {
            public BlockingClient(LoggingModule logging) : base("http://localhost:1", null, logging) { }

            public override async Task<ToolChatResponse> ToolChatAsync(ToolChatRequest request, CancellationToken token = default)
            {
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return new ToolChatResponse { Success = true, ToolCalls = new List<ToolCall>() };
            }

            public override Task<ChatResponse> ChatAsync(string prompt, ChatCompletionOptions? options = null, CancellationToken token = default) => throw new NotImplementedException();
            public override Task<ChatStreamingResponse> ChatStreamingAsync(string prompt, ChatCompletionOptions? options = null, CancellationToken token = default) => throw new NotImplementedException();
            public override async Task<ToolChatStreamingResponse> ToolChatStreamingAsync(ToolChatRequest request, CancellationToken token = default)
            {
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return new ToolChatStreamingResponse { Success = true, ToolCalls = new List<ToolCall>() };
            }
            public override Task<EmbeddingResponse> EmbedAsync(string input, EmbeddingOptions? options = null, CancellationToken token = default) => throw new NotImplementedException();
            public override Task<EmbeddingResponse> EmbedAsync(List<string> inputs, EmbeddingOptions? options = null, CancellationToken token = default) => throw new NotImplementedException();
            public override Task<GenerationResponse> GenerateAsync(string prompt, GenerationOptions? options = null, CancellationToken token = default) => throw new NotImplementedException();
            public override Task<GenerationStreamingResponse> GenerateStreamingAsync(string prompt, GenerationOptions? options = null, CancellationToken token = default) => throw new NotImplementedException();
            public override IAsyncEnumerable<ModelInformation> ListModelsAsync(CancellationToken token = default) => throw new NotImplementedException();
            public override Task<ModelInformation?> GetModelInformationAsync(string model, CancellationToken token = default) => throw new NotImplementedException();
        }

        #endregion
    }
}
