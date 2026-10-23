namespace Test.Shared.Suites.Runtimes
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Runtimes;
    using Armada.Runtimes.Interfaces;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="RemoteAgentRuntime"/> and the connection manager's launch delegation,
    /// exercised against a simulated Harbor whose send channel replies with the started/output/exited
    /// lifecycle. Proves that a delegated launch surfaces as the ordinary agent-runtime events (started pid,
    /// output lines, exit code) and that stop routes by process id.
    /// </summary>
    public sealed class RemoteAgentRuntimeSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the RemoteAgentRuntime suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("harbor_failure_metadata_is_not_assistant_output", "Harbor transports precise runtime failure before exit without polluting replies", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                HarborConnectionManager manager = new HarborConnectionManager(new HarborService(testDb.Driver, CreateLogging()), CreateLogging(), null);
                RemoteAgentRuntime runtime = new RemoteAgentRuntime(manager, "hbr_failure", AgentRuntimeEnum.ApiEndpoint);
                RuntimeProviderError? observed = null;
                List<string> output = new List<string>();
                runtime.OnProviderError += (pid, error) => observed = error;
                runtime.OnOutputReceived += (pid, line) => output.Add(line);
                runtime.OnOutput(HarborOutputStreamEnum.Stdout, "{\"ErrorType\":\"authentication_error\"}");
                AssertNull(observed, "Ordinary assistant/tool output cannot impersonate metadata.");
                runtime.OnOutput(HarborOutputStreamEnum.ProviderError, System.Text.Json.JsonSerializer.Serialize(new RuntimeProviderError
                {
                    ErrorType = "output_token_limit_reached", Reason = "Hit the 4096 output-token limit.", HttpStatusCode = 200
                }));
                AssertNotNull(observed);
                AssertEqual("output_token_limit_reached", observed!.ErrorType);
                AssertEqual(RuntimeFailureKindEnum.TaskFailure, RuntimeFailureClassifier.Classify(1, observed));
                AssertContains("4096", RuntimeFailureClassifier.Decide(1, observed).Describe());
                AssertContains("Remediation:", RuntimeFailureClassifier.Decide(1, observed).Describe());
                AssertEqual(1, output.Count, "Metadata must not become assistant text.");
                runtime.OnOutput(HarborOutputStreamEnum.ProviderError, "{invalid}");
                AssertEqual("output_token_limit_reached", observed.ErrorType);
            }));

            cases.Add(CaseAsync("permission_policy_survives_harbor_launch", "Resolved permissions survive API and CLI Harbor launches, including Bypass", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                HarborService harbors = new HarborService(testDb.Driver, CreateLogging());
                HarborConnectionManager manager = new HarborConnectionManager(harbors, CreateLogging(), null);
                HarborLaunchRequest? captured = null;
                HarborSendDelegate simulated = async (message, cancellation) =>
                {
                    if (message is HarborLaunchRequest launch)
                    {
                        captured = System.Text.Json.JsonSerializer.Deserialize<HarborLaunchRequest>(System.Text.Json.JsonSerializer.Serialize(launch));
                        await manager.OnMessageAsync("hbr_policy", new HarborStarted { JobId = launch.JobId, ProcessId = 4243 }, cancellation).ConfigureAwait(false);
                        await manager.OnMessageAsync("hbr_policy", new HarborExited { JobId = launch.JobId, ExitCode = 0 }, cancellation).ConfigureAwait(false);
                    }
                };
                await manager.OnHandshakeAsync(new HarborHandshake { HarborId = "hbr_policy", Name = "Policy Harbor", ProtocolVersion = "1.0" }, "t", "u", simulated).ConfigureAwait(false);
                foreach (AgentRuntimeEnum runtimeType in new[] { AgentRuntimeEnum.ApiEndpoint, AgentRuntimeEnum.ClaudeCode })
                foreach (CliPermissionPolicyEnum policy in new[] { CliPermissionPolicyEnum.Bypass, CliPermissionPolicyEnum.Refuse, CliPermissionPolicyEnum.ApproveInArmada })
                {
                    ModelEndpoint endpoint = new ModelEndpoint { Kind = ModelEndpointKindEnum.Inference, Provider = ModelProviderEnum.OpenAICompatible, BaseUrl = "http://localhost:1", Model = "test-model" };
                    RemoteAgentRuntime runtime = new RemoteAgentRuntime(manager, "hbr_policy", runtimeType, _ => endpoint);
                    Armada.Server.CliPermissionLaunch.Apply(runtime, policy, 321);
                    Captain captain = CaptainRuntimeOptions.WithEffectiveAutoApprove(new Captain("policy") { Runtime = runtimeType, ModelEndpointId = endpoint.Id }, policy == CliPermissionPolicyEnum.Bypass);
                    await runtime.StartAsync("/repo", "test policy", captain: captain).ConfigureAwait(false);
                    AssertNotNull(captured);
                    AssertEqual(policy, captured!.CliPermissionPolicy!.Value, "Harbor must receive the same effective policy as the Admiral.");
                    AssertEqual(policy == CliPermissionPolicyEnum.Bypass, captured.AutoApprove!.Value);
                    AssertEqual(policy == CliPermissionPolicyEnum.ApproveInArmada ? (int?)321 : null, captured.PermissionPromptTimeoutSeconds);
                }
            }));

            cases.Add(CaseAsync("delegated_launch_raises_lifecycle_events", "Delegated launch raises started/output/exited", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                HarborService harbors = new HarborService(testDb.Driver, CreateLogging());
                HarborConnectionManager manager = new HarborConnectionManager(harbors, CreateLogging(), null);

                // Simulated Harbor: on a launch request, report started (pid 4242), one output line, then exit 0.
                HarborSendDelegate simulated = async (message, cancellation) =>
                {
                    if (message is HarborLaunchRequest launch)
                    {
                        await manager.OnMessageAsync("hbr_rt", new HarborStarted { JobId = launch.JobId, ProcessId = 4242 }, cancellation).ConfigureAwait(false);
                        await manager.OnMessageAsync("hbr_rt", new HarborOutput { JobId = launch.JobId, Stream = HarborOutputStreamEnum.Stdout, Data = "working" }, cancellation).ConfigureAwait(false);
                        await manager.OnMessageAsync("hbr_rt", new HarborExited { JobId = launch.JobId, ExitCode = 0 }, cancellation).ConfigureAwait(false);
                    }
                };

                await manager.OnHandshakeAsync(new HarborHandshake { HarborId = "hbr_rt", Name = "Rig", ProtocolVersion = "1.0" }, "ten_rt", "usr_rt", simulated).ConfigureAwait(false);

                RemoteHostProcessExecutor executor = new RemoteHostProcessExecutor(manager, "hbr_rt");
                IAgentRuntime runtime = executor.CreateRuntime(AgentRuntimeEnum.ClaudeCode);

                int startedPid = 0;
                List<string> output = new List<string>();
                int? exitCode = null;
                runtime.OnProcessStarted += pid => startedPid = pid;
                runtime.OnOutputReceived += (pid, line) => output.Add(line);
                runtime.OnProcessExited += (pid, code) => exitCode = code;

                int returnedPid = await runtime.StartAsync("/repo", "do the work").ConfigureAwait(false);

                // The Admiral's handle, never the Harbor's process id (4242), which could equal a local process's (L6).
                AssertTrue(returnedPid >= 1000000000 && returnedPid < 2000000000, "handle " + returnedPid + " is in the Harbor range");
                AssertEqual(returnedPid, startedPid);
                AssertTrue(output.Contains("working"), "Expected the delegated output line to surface.");
                AssertEqual(0, exitCode ?? -1);
            }));

            cases.Add(CaseAsync("foreign_harbor_cannot_finish_owned_job", "A different authenticated Harbor cannot complete another Harbor's tracked job", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                HarborConnectionManager manager = new HarborConnectionManager(new HarborService(testDb.Driver, CreateLogging()), CreateLogging(), null);
                string? jobId = null;
                HarborSendDelegate owner = async (message, cancellation) =>
                {
                    if (message is HarborLaunchRequest launch)
                    {
                        jobId = launch.JobId;
                        await manager.OnMessageAsync("hbr_event_owner", new HarborStarted { JobId = launch.JobId, ProcessId = 42427 }, cancellation).ConfigureAwait(false);
                    }
                };
                TenantMetadata ownerTenant = await testDb.Driver.Tenants.CreateAsync(new TenantMetadata { Name = "event-owner" }).ConfigureAwait(false);
                UserMaster ownerUser = await testDb.Driver.Users.CreateAsync(new UserMaster { TenantId = ownerTenant.Id, Email = "owner@events.invalid" }).ConfigureAwait(false);
                TenantMetadata otherTenant = await testDb.Driver.Tenants.CreateAsync(new TenantMetadata { Name = "event-other" }).ConfigureAwait(false);
                UserMaster otherUser = await testDb.Driver.Users.CreateAsync(new UserMaster { TenantId = otherTenant.Id, Email = "other@events.invalid" }).ConfigureAwait(false);
                await manager.OnHandshakeAsync(new HarborHandshake { HarborId = "hbr_event_owner", Name = "Owner", ProtocolVersion = "1.0" }, ownerTenant.Id, ownerUser.Id, owner).ConfigureAwait(false);
                await manager.OnHandshakeAsync(new HarborHandshake { HarborId = "hbr_event_other", Name = "Other", ProtocolVersion = "1.0" }, otherTenant.Id, otherUser.Id,
                    (message, cancellation) => Task.CompletedTask).ConfigureAwait(false);
                RemoteAgentRuntime runtime = new RemoteAgentRuntime(manager, "hbr_event_owner", AgentRuntimeEnum.ClaudeCode);
                int? exit = null;
                List<int> starts = new List<int>();
                List<string> output = new List<string>();
                List<RuntimeActivity> activity = new List<RuntimeActivity>();
                runtime.OnProcessStarted += pid => starts.Add(pid);
                runtime.OnOutputReceived += (_, text) => output.Add(text);
                runtime.OnActivity += (_, value) => activity.Add(value);
                runtime.OnProcessExited += (_, code) => exit = code;
                int pid = await runtime.StartAsync("/repo", "Own this job").ConfigureAwait(false);
                try
                {
                    await manager.OnMessageAsync("hbr_event_other", new HarborStarted { JobId = jobId!, ProcessId = 91919 }).ConfigureAwait(false);
                    await manager.OnMessageAsync("hbr_event_other", new HarborOutput { JobId = jobId!, Stream = HarborOutputStreamEnum.Stdout, Data = "foreign text" }).ConfigureAwait(false);
                    await manager.OnMessageAsync("hbr_event_other", new HarborActivity { JobId = jobId!, Activity = new RuntimeActivity { Summary = "foreign activity" } }).ConfigureAwait(false);
                    await manager.OnMessageAsync("hbr_event_other", new HarborError { JobId = jobId!, Message = "foreign failure" }).ConfigureAwait(false);
                    AssertEqual(1, starts.Count, "A foreign start must not rebind process routing.");
                    AssertTrue(starts[0] >= 1000000000 && starts[0] < 2000000000, "the owner's start keeps its Admiral handle");
                    AssertEqual(0, output.Count, "Foreign output must not enter an owner's log.");
                    AssertEqual(0, activity.Count, "Foreign activity must not enter the owner's session.");
                    await manager.OnMessageAsync("hbr_event_other", new HarborExited { JobId = jobId!, ExitCode = 0 }).ConfigureAwait(false);
                    AssertNull(exit, "Knowing a job ID is not authority to finish another Harbor's job.");
                    AssertTrue(manager.IsProcessTracked(pid), "Foreign lifecycle messages must not remove owner tracking.");
                    await manager.OnMessageAsync("hbr_event_owner", new HarborOutput { JobId = jobId!, Stream = HarborOutputStreamEnum.Stdout, Data = "owner text" }).ConfigureAwait(false);
                    AssertTrue(output.Contains("owner text"), "The owner's events still flow.");
                    await manager.OnMessageAsync("hbr_event_owner", new HarborExited { JobId = jobId!, ExitCode = 0 }).ConfigureAwait(false);
                    AssertEqual(0, exit!.Value);
                }
                finally
                {
                    await manager.OnMessageAsync("hbr_event_owner", new HarborExited { JobId = jobId!, ExitCode = -1 }).ConfigureAwait(false);
                }
            }));

            cases.Add(CaseAsync("start_times_out_without_started", "StartAsync surfaces a timeout when the Harbor never starts the job", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                HarborService harbors = new HarborService(testDb.Driver, CreateLogging());
                HarborConnectionManager manager = new HarborConnectionManager(harbors, CreateLogging(), null);

                HarborSendDelegate silent = (message, cancellation) => Task.CompletedTask;
                await manager.OnHandshakeAsync(new HarborHandshake { HarborId = "hbr_silent_rt", Name = "Rig", ProtocolVersion = "1.0" }, "t", "u", silent).ConfigureAwait(false);

                RemoteHostProcessExecutor executor = new RemoteHostProcessExecutor(manager, "hbr_silent_rt");
                IAgentRuntime runtime = executor.CreateRuntime(AgentRuntimeEnum.ClaudeCode);

                // Cancel quickly so the 60s start-timeout wait does not stall the suite; StartAsync should throw.
                using CancellationTokenSource cts = new CancellationTokenSource(300);
                await AssertThrowsAsync<Exception>(() => runtime.StartAsync("/repo", "x", token: cts.Token));
            }));

            cases.Add(CaseAsync("launch_unconnected_harbor_throws", "LaunchAsync rejects an unconnected Harbor", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                HarborService harbors = new HarborService(testDb.Driver, CreateLogging());
                HarborConnectionManager manager = new HarborConnectionManager(harbors, CreateLogging(), null);

                RemoteHostProcessExecutor executor = new RemoteHostProcessExecutor(manager, "hbr_absent_rt");
                IAgentRuntime runtime = executor.CreateRuntime(AgentRuntimeEnum.ClaudeCode);
                await AssertThrowsAsync<InvalidOperationException>(() => runtime.StartAsync("/repo", "x"));
            }));

            cases.Add(CaseAsync("delegated_output_is_mirrored_to_log_file", "Delegated output is mirrored to the Admiral-side mission log file", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                HarborService harbors = new HarborService(testDb.Driver, CreateLogging());
                HarborConnectionManager manager = new HarborConnectionManager(harbors, CreateLogging(), null);

                HarborSendDelegate simulated = async (message, cancellation) =>
                {
                    if (message is HarborLaunchRequest launch)
                    {
                        await manager.OnMessageAsync("hbr_log", new HarborStarted { JobId = launch.JobId, ProcessId = 7777 }, cancellation).ConfigureAwait(false);
                        await manager.OnMessageAsync("hbr_log", new HarborOutput { JobId = launch.JobId, Stream = HarborOutputStreamEnum.Stdout, Data = "line-one" }, cancellation).ConfigureAwait(false);
                        await manager.OnMessageAsync("hbr_log", new HarborOutput { JobId = launch.JobId, Stream = HarborOutputStreamEnum.Stdout, Data = "line-two" }, cancellation).ConfigureAwait(false);
                        await manager.OnMessageAsync("hbr_log", new HarborExited { JobId = launch.JobId, ExitCode = 0 }, cancellation).ConfigureAwait(false);
                    }
                };
                await manager.OnHandshakeAsync(new HarborHandshake { HarborId = "hbr_log", Name = "Rig", ProtocolVersion = "1.0" }, "t", "u", simulated).ConfigureAwait(false);

                string logPath = Path.Combine(Path.GetTempPath(), "armada-test-" + Guid.NewGuid().ToString("N") + ".log");
                try
                {
                    RemoteHostProcessExecutor executor = new RemoteHostProcessExecutor(manager, "hbr_log");
                    IAgentRuntime runtime = executor.CreateRuntime(AgentRuntimeEnum.ClaudeCode);
                    await runtime.StartAsync("/repo", "the prompt", logFilePath: logPath).ConfigureAwait(false);

                    AssertTrue(File.Exists(logPath), "Expected the mission log file to be written on the Admiral side.");
                    string contents = await File.ReadAllTextAsync(logPath).ConfigureAwait(false);
                    AssertTrue(contents.Contains("line-one") && contents.Contains("line-two"), "Expected both streamed output lines in the mirrored log.");
                }
                finally
                {
                    try { File.Delete(logPath); } catch { }
                }
            }));

            cases.Add(CaseAsync("select_prefers_connected_capable_harbor", "SelectHarborAsync chooses a connected Harbor that advertises the runtime", TestTags.Positive, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                HarborService harbors = new HarborService(testDb.Driver, CreateLogging());
                HarborConnectionManager manager = new HarborConnectionManager(harbors, CreateLogging(), null);

                HarborSendDelegate send = (message, cancellation) => Task.CompletedTask;
                await manager.OnHandshakeAsync(new HarborHandshake
                {
                    HarborId = "hbr_sel",
                    Name = "Rig",
                    ProtocolVersion = "1.0",
                    MaxConcurrentJobs = 4,
                    Capabilities = new List<HarborCapability> { new HarborCapability { Name = "ClaudeCode", Available = true } }
                }, "ten_sel", "usr_sel", send).ConfigureAwait(false);

                HarborRoutingDecision decision = await manager.SelectHarborAsync("ten_sel", new HarborRoutingRequest { RequestedRuntime = "ClaudeCode" }).ConfigureAwait(false);
                AssertTrue(decision.Success, "Expected a connected, capable Harbor to be chosen.");
                AssertEqual("hbr_sel", decision.HarborId ?? string.Empty);
            }));

            cases.Add(CaseAsync("select_without_capability_falls_back", "SelectHarborAsync declines when no Harbor advertises the runtime", TestTags.Negative, async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                HarborService harbors = new HarborService(testDb.Driver, CreateLogging());
                HarborConnectionManager manager = new HarborConnectionManager(harbors, CreateLogging(), null);

                HarborSendDelegate send = (message, cancellation) => Task.CompletedTask;
                await manager.OnHandshakeAsync(new HarborHandshake
                {
                    HarborId = "hbr_git_only",
                    Name = "Rig",
                    ProtocolVersion = "1.0",
                    MaxConcurrentJobs = 4,
                    Capabilities = new List<HarborCapability> { new HarborCapability { Name = "git", Available = true } }
                }, "ten_git", "usr_git", send).ConfigureAwait(false);

                HarborRoutingDecision decision = await manager.SelectHarborAsync("ten_git", new HarborRoutingRequest { RequestedRuntime = "ClaudeCode" }).ConfigureAwait(false);
                AssertTrue(!decision.Success, "Expected no Harbor to be chosen when the runtime is not advertised (caller runs locally).");
            }));

            cases.Add(CaseAsync("same_native_pid_isolated_across_harbors", "Two Harbors reporting the same native PID retain independent output, stop and exit identities", TestTags.Reliability, async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                HarborConnectionManager manager = new HarborConnectionManager(new HarborService(db.Driver, CreateLogging()), CreateLogging(), null);
                List<string> kills = new List<string>();
                Dictionary<string, string> jobs = new Dictionary<string, string>();
                foreach (string harbor in new[] { "hbr_pid_a", "hbr_pid_b" })
                {
                    string target = harbor;
                    await manager.OnHandshakeAsync(new HarborHandshake { HarborId = target, Name = target, ProtocolVersion = "1.0" }, null, null,
                        async (message, token) =>
                        {
                            if (message is HarborLaunchRequest launch)
                            {
                                jobs[target] = launch.JobId;
                                await manager.OnMessageAsync(target, new HarborStarted { JobId = launch.JobId, ProcessId = 4242 }, token).ConfigureAwait(false);
                            }
                            if (message is HarborKillRequest kill)
                            {
                                kills.Add(target);
                                await manager.OnMessageAsync(target, new HarborExited { JobId = kill.JobId, ExitCode = 137 }, token).ConfigureAwait(false);
                            }
                        }).ConfigureAwait(false);
                }
                RemoteAgentRuntime first = new RemoteAgentRuntime(manager, "hbr_pid_a", AgentRuntimeEnum.ClaudeCode);
                RemoteAgentRuntime second = new RemoteAgentRuntime(manager, "hbr_pid_b", AgentRuntimeEnum.ClaudeCode);
                int firstPid = await first.StartAsync("/repo", "fixture").ConfigureAwait(false);
                int secondPid = await second.StartAsync("/repo", "fixture").ConfigureAwait(false);
                AssertTrue(firstPid != secondPid, "Admiral identities must not collide across hosts.");
                int outputPid = 0;
                second.OnOutputReceived += (pid, _) => outputPid = pid;
                await manager.OnMessageAsync("hbr_pid_b", new HarborOutput { JobId = jobs["hbr_pid_b"], Data = "second" }).ConfigureAwait(false);
                AssertEqual(secondPid, outputPid);
                manager.TryGetHarborForProcess(firstPid, out string? owner);
                AssertEqual("hbr_pid_a", owner);
                await first.StopAndConfirmAsync().ConfigureAwait(false);
                AssertEqual("hbr_pid_a", kills[0]);
                AssertTrue(manager.IsProcessTracked(secondPid), "Stopping A cannot forget B.");
                AssertFalse(manager.IsProcessTracked(firstPid));
                AssertTrue(await second.IsRunningAsync(secondPid).ConfigureAwait(false));
                await second.StopAndConfirmAsync().ConfigureAwait(false);
                AssertEqual("hbr_pid_b", kills[1]);
                AssertFalse(manager.IsProcessTracked(secondPid));
            }));

            cases.Add(CaseAsync("launched_job_handles_never_overlap_local_ids", "A newly started Harbor job gets an Admiral handle outside the range of local process ids and API in-process ids, and keeps it on a replayed start", TestTags.Reliability, async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                HarborConnectionManager manager = new HarborConnectionManager(new HarborService(db.Driver, CreateLogging()), CreateLogging(), null);
                int a = manager.RegisterLaunchedJob("hbr_l6_a", "job-1");
                int b = manager.RegisterLaunchedJob("hbr_l6_b", "job-1");
                AssertTrue(a >= 1000000000 && a < 2000000000, "handle " + a + " is in the Harbor range");
                AssertTrue(b >= 1000000000 && b < 2000000000);
                AssertTrue(a != b, "two Harbors' jobs never share a handle");
                AssertEqual(a, manager.RegisterLaunchedJob("hbr_l6_a", "job-1"), "a replayed start keeps its handle");
                manager.TryGetHarborForProcess(b, out string? owner);
                AssertEqual("hbr_l6_b", owner);
            }));

            cases.Add(CaseAsync("restored_native_pid_collision_gets_unique_handles", "Restart restores identical persisted native IDs without losing either job", TestTags.Reliability, async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                HarborConnectionManager manager = new HarborConnectionManager(new HarborService(db.Driver, CreateLogging()), CreateLogging(), null);
                RecordingHarborJobListener first = new RecordingHarborJobListener();
                RecordingHarborJobListener second = new RecordingHarborJobListener();
                int a = manager.RestoreJob("hbr_restore_a", "job-a", 2000000001, first);
                int b = manager.RestoreJob("hbr_restore_b", "job-b", 2000000001, second);
                AssertTrue(a != b);
                AssertTrue(manager.IsProcessTracked(a));
                AssertTrue(manager.IsProcessTracked(b));
                AssertEqual(b, manager.RegisterProcessId(2000000001, "hbr_restore_b", "job-b"), "Replayed starts retain the persisted Admiral handle.");
                await manager.OnMessageAsync("hbr_restore_a", new HarborExited { JobId = "job-a", ExitCode = 0 }).ConfigureAwait(false);
                AssertEqual(0, first.ExitCode);
                AssertNull(second.ExitCode);
                manager.TryGetHarborForProcess(b, out string? owner);
                AssertEqual("hbr_restore_b", owner);
            }));

            cases.Add(CaseAsync("mission_stop_requires_owned_exit_confirmation", "Lost or unconfirmed stop retains ownership, survives reconnect, and requires the owner's exit", TestTags.Reliability, async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                HarborConnectionManager manager = new HarborConnectionManager(new HarborService(db.Driver, CreateLogging()), CreateLogging(), null);
                string job = "";
                HarborSendDelegate send = async (message, token) =>
                {
                    if (message is HarborLaunchRequest launch)
                    {
                        job = launch.JobId;
                        await manager.OnMessageAsync("hbr_stop", new HarborStarted { JobId = job, ProcessId = 4343 }, token).ConfigureAwait(false);
                    }
                };
                HarborHandshake handshake = new HarborHandshake { HarborId = "hbr_stop", Name = "stop", ProtocolVersion = "1.0" };
                await manager.OnHandshakeAsync(handshake, null, null, send).ConfigureAwait(false);
                RemoteAgentRuntime runtime = new RemoteAgentRuntime(manager, "hbr_stop", AgentRuntimeEnum.ClaudeCode);
                int pid = await runtime.StartAsync("/repo", "fixture").ConfigureAwait(false);
                int exits = 0;
                runtime.OnProcessExited += (_, _) => exits++;
                bool timedOut = false;
                try { await manager.StopJobAndConfirmAsync("hbr_stop", job, TimeSpan.FromMilliseconds(40)).ConfigureAwait(false); }
                catch (TimeoutException) { timedOut = true; }
                AssertTrue(timedOut, "A sent request is not an exit acknowledgement.");
                AssertEqual(0, exits, "Never synthesize a successful stop.");
                AssertTrue(manager.IsProcessTracked(pid));
                await manager.OnDisconnectedAsync("hbr_stop").ConfigureAwait(false);
                bool disconnected = false;
                try { await manager.StopJobAndConfirmAsync("hbr_stop", job, TimeSpan.FromMilliseconds(40)).ConfigureAwait(false); }
                catch (InvalidOperationException) { disconnected = true; }
                AssertTrue(disconnected);
                HarborHandshakeAck ack = await manager.OnHandshakeAsync(handshake, null, null, send).ConfigureAwait(false);
                AssertTrue(ack.OwnedJobIds!.Contains(job), "Reconnect preserves the original until confirmed exit.");
                Task stop = manager.StopJobAndConfirmAsync("hbr_stop", job, TimeSpan.FromSeconds(2));
                await manager.OnMessageAsync("hbr_other", new HarborExited { JobId = job, ExitCode = 0 }).ConfigureAwait(false);
                AssertFalse(stop.IsCompleted, "Another Harbor cannot acknowledge this stop.");
                await manager.OnMessageAsync("hbr_stop", new HarborExited { JobId = job, ExitCode = 137 }).ConfigureAwait(false);
                await stop.ConfigureAwait(false);
                AssertEqual(1, exits);
                AssertFalse(manager.IsProcessTracked(pid));
            }));

            cases.Add(CaseAsync("overlapping_stop_waiters_keep_exit_confirmation", "A timed-out stop caller cannot orphan another caller waiting for the same owned exit", TestTags.Reliability, async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                HarborConnectionManager manager = new HarborConnectionManager(new HarborService(db.Driver, CreateLogging()), CreateLogging(), null);
                await manager.OnHandshakeAsync(new HarborHandshake { HarborId = "hbr_waiters", Name = "waiters" }, null, null, (message, token) => Task.CompletedTask).ConfigureAwait(false);
                manager.RestoreJob("hbr_waiters", "job_waiters", 6161, new RecordingHarborJobListener());
                Task first = manager.StopJobAndConfirmAsync("hbr_waiters", "job_waiters", TimeSpan.FromMilliseconds(30));
                Task second = manager.StopJobAndConfirmAsync("hbr_waiters", "job_waiters", TimeSpan.FromSeconds(1));
                bool timedOut = false;
                try { await first.ConfigureAwait(false); } catch (TimeoutException) { timedOut = true; }
                AssertTrue(timedOut);
                await manager.OnMessageAsync("hbr_waiters", new HarborExited { JobId = "job_waiters", ExitCode = 137 }).ConfigureAwait(false);
                await second.ConfigureAwait(false);
            }));

            return new TestSuiteDescriptor(
                suiteId: "Runtimes.RemoteAgentRuntime",
                displayName: "Remote Agent Runtime",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "Runtimes.RemoteAgentRuntime",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
