namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using SyslogLogging;


    public class RemoteTriggerServiceTests : TestSuite
    {
        public override string Name => "RemoteTrigger Service";

        protected override async Task RunTestsAsync()
        {
            await RunTest("FireDrainer_NotConfigured_NoOp", async () =>
            {
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                RemoteTriggerSettings settings = new RemoteTriggerSettings { Enabled = false };
                RemoteTriggerService service = new RemoteTriggerService(settings, http, new LoggingModule(), TimeSpan.Zero);

                await service.FireDrainerAsync("vessel-a", "some event");

                AssertEqual(0, http.CallCount, "disabled settings should produce no HTTP calls");
            });

            await RunTest("FireDrainer_ModeDisabledWithRemoteFireFields_NoOp", async () =>
            {
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                RemoteTriggerService service = new RemoteTriggerService(MakeDisabledModeSettings(), http, new LoggingModule(), TimeSpan.Zero);

                await service.FireDrainerAsync("vessel-disabled-mode", "event with configured RemoteFire fields");

                AssertEqual(0, http.CallCount, "Disabled mode should produce no HTTP calls even when RemoteFire fields are configured");
            });

            await RunTest("FireDrainer_FirstCall_FiresOnce", async () =>
            {
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                RemoteTriggerService service = new RemoteTriggerService(MakeSettings(), http, new LoggingModule(), TimeSpan.Zero);

                await service.FireDrainerAsync("vessel-a", "WorkProduced: mission m1 on vessel vessel-a");

                AssertEqual(1, http.CallCount, "first call should fire exactly once");
                AssertContains("vessel-a", http.LastRequest!.Text);
                AssertContains("WorkProduced", http.LastRequest!.Text);
            });

            await RunTest("FireDrainer_SameVesselWithin60s_Coalesced", async () =>
            {
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                RemoteTriggerService service = new RemoteTriggerService(MakeSettings(), http, new LoggingModule(), TimeSpan.Zero);

                await service.FireDrainerAsync("vessel-a", "first event");
                await service.FireDrainerAsync("vessel-a", "second event within window");

                AssertEqual(1, http.CallCount, "second call for same vessel within 60s should be coalesced");
            });

            await RunTest("FireDrainer_DifferentVessels_BothFire", async () =>
            {
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                RemoteTriggerService service = new RemoteTriggerService(MakeSettings(), http, new LoggingModule(), TimeSpan.Zero);

                await service.FireDrainerAsync("vessel-a", "event for a");
                await service.FireDrainerAsync("vessel-b", "event for b");

                AssertEqual(2, http.CallCount, "different vessels should each fire independently");
            });

            await RunTest("FireDrainer_HitsThrottle_Suppressed", async () =>
            {
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                RemoteTriggerService service = new RemoteTriggerService(MakeSettings(), http, new LoggingModule(), TimeSpan.Zero);

                for (int i = 0; i < 20; i++)
                    await service.FireDrainerAsync("vessel-" + i, "event-" + i);

                AssertEqual(20, http.CallCount, "20 distinct vessels should each fire once");

                await service.FireDrainerAsync("vessel-throttled", "should be suppressed");

                AssertEqual(20, http.CallCount, "21st fire should be suppressed by throttle");
            });

            await RunTest("FireDrainer_OmittedThrottleCap_UsesDefault20", async () =>
            {
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                RemoteTriggerSettings settings = MakeSettings();
                AssertEqual(20, settings.ThrottleCapPerHour, "property default should be 20 when omitted in initializer");
                RemoteTriggerService service = new RemoteTriggerService(settings, http, new LoggingModule(), TimeSpan.Zero);

                for (int i = 0; i < 20; i++)
                    await service.FireDrainerAsync("vessel-" + i, "event-" + i);

                AssertEqual(20, http.CallCount, "20 distinct vessels should each fire once at default cap");

                await service.FireDrainerAsync("vessel-throttled", "should be suppressed");

                AssertEqual(20, http.CallCount, "21st fire should be suppressed at default cap");
            });

            await RunTest("FireDrainer_CustomThrottleCap_SixthWakeSuppressed", async () =>
            {
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                RemoteTriggerSettings settings = MakeSettings();
                settings.ThrottleCapPerHour = 5;
                RemoteTriggerService service = new RemoteTriggerService(settings, http, new LoggingModule(), TimeSpan.Zero);

                for (int i = 0; i < 5; i++)
                    await service.FireDrainerAsync("vessel-" + i, "event-" + i);

                AssertEqual(5, http.CallCount, "5 distinct vessels should fire at cap 5");

                await service.FireDrainerAsync("vessel-throttled", "sixth should be suppressed");

                AssertEqual(5, http.CallCount, "6th wake should be suppressed when cap is 5");
            });

            await RunTest("FireDrainer_NonPositiveThrottleCap_ClampsToDefault20", async () =>
            {
                int[] badCaps = new int[] { 0, -3 };
                foreach (int badCap in badCaps)
                {
                    RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                    RemoteTriggerSettings settings = MakeSettings();
                    settings.ThrottleCapPerHour = badCap;
                    RemoteTriggerService service = new RemoteTriggerService(settings, http, new LoggingModule(), TimeSpan.Zero);

                    for (int i = 0; i < 20; i++)
                        await service.FireDrainerAsync("vessel-" + badCap + "-" + i, "event-" + i);

                    AssertEqual(20, http.CallCount, "non-positive cap should clamp to 20; expected 20 fires for badCap=" + badCap);

                    await service.FireDrainerAsync("vessel-throttled-" + badCap, "should be suppressed");

                    AssertEqual(20, http.CallCount, "21st fire should be suppressed after clamp for badCap=" + badCap);
                }
            });

            await RunTest("FireDrainer_5xxThenSuccess_RetryWorks", async () =>
            {
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                http.EnqueueResult(new FireResult { Outcome = FireOutcome.RetriableFailure, StatusCode = 500, ErrorMessage = "5xx from /fire: 500" });
                http.EnqueueResult(new FireResult { Outcome = FireOutcome.Success, StatusCode = 200, SessionUrl = "https://claude.ai/code/sessions/sess_ok" });

                RemoteTriggerService service = new RemoteTriggerService(MakeSettings(), http, new LoggingModule(), TimeSpan.Zero);

                await service.FireDrainerAsync("vessel-a", "some event");

                AssertEqual(2, http.CallCount, "retry on 5xx should produce a second HTTP call");
                AssertEqual(0, service.ConsecutiveFailures, "consecutive failures should reset to 0 on success");
            });

            await RunTest("FireDrainer_3ConsecutiveRetriableFailures_LogsButNoCrash", async () =>
            {
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                for (int i = 0; i < 6; i++)
                    http.EnqueueResult(new FireResult { Outcome = FireOutcome.RetriableFailure, StatusCode = 500, ErrorMessage = "5xx from /fire: 500" });

                RemoteTriggerService service = new RemoteTriggerService(MakeSettings(), http, new LoggingModule(), TimeSpan.Zero);

                await service.FireDrainerAsync("vessel-a1", "event 1");
                await service.FireDrainerAsync("vessel-a2", "event 2");
                await service.FireDrainerAsync("vessel-a3", "event 3");

                AssertEqual(6, http.CallCount, "each fire should attempt twice (initial + retry)");
                AssertEqual(3, service.ConsecutiveFailures, "consecutive failure counter should be 3 after 3 retriable failures");
            });

            await RunTest("FireCritical_BypassesCoalescing", async () =>
            {
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                RemoteTriggerService service = new RemoteTriggerService(MakeSettings(), http, new LoggingModule(), TimeSpan.Zero);

                await service.FireDrainerAsync("vessel-a", "drainer event");
                await service.FireCriticalAsync("audit critical finding on entry e1");

                AssertEqual(2, http.CallCount, "critical should fire independently of coalescing; drainer + critical = 2 calls");
                AssertContains("[CRITICAL]", http.LastRequest!.Text, "critical fallback via drainer should prefix [CRITICAL]");
            });

            await RunTest("FireCritical_Disabled_NoOp", async () =>
            {
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                RemoteTriggerSettings settings = new RemoteTriggerSettings { Enabled = false };
                RemoteTriggerService service = new RemoteTriggerService(settings, http, new LoggingModule(), TimeSpan.Zero);

                await service.FireCriticalAsync("some critical event");

                AssertEqual(0, http.CallCount, "disabled settings should produce no HTTP calls for FireCritical");
            });

            await RunTest("FireCritical_ModeDisabledWithRemoteFireFields_NoOp", async () =>
            {
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                RemoteTriggerService service = new RemoteTriggerService(MakeDisabledModeSettings(), http, new LoggingModule(), TimeSpan.Zero);

                await service.FireCriticalAsync("critical event with configured RemoteFire fields");

                AssertEqual(0, http.CallCount, "Disabled mode should produce no critical HTTP calls even when RemoteFire fields are configured");
            });

            await RunTest("FireDrainer_AgentWake_Disabled_NoOp", async () =>
            {
                RecordingAgentWakeProcessHost host = new RecordingAgentWakeProcessHost();
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                RemoteTriggerSettings settings = new RemoteTriggerSettings { Enabled = false, Mode = RemoteTriggerMode.AgentWake };
                RemoteTriggerService service = new RemoteTriggerService(settings, http, host, new LoggingModule(), TimeSpan.Zero);

                await service.FireDrainerAsync("vessel-a", "some event");

                AssertEqual(0, host.StartCallCount, "disabled AgentWake should produce no process spawns");
            });

            await RunTest("FireDrainer_AgentWake_StartsClaude_Continue", async () =>
            {
                RecordingAgentWakeProcessHost host = new RecordingAgentWakeProcessHost();
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                RemoteTriggerService service = new RemoteTriggerService(MakeAgentWakeSettings(), http, host, new LoggingModule(), TimeSpan.Zero);

                await service.FireDrainerAsync("vessel-a", "WorkProduced event text");

                AssertEqual(1, host.StartCallCount, "AgentWake should spawn exactly one process");
                AgentWakeProcessRequest req = host.LastRequest!;
                AssertEqual("claude", req.Command, "default runtime should invoke 'claude'");
                AssertTrue(req.ArgumentList.Contains("--print"), "Claude args must include --print");
                AssertTrue(req.ArgumentList.Contains("--continue"), "Claude without SessionId must use --continue");
                AssertTrue(req.ArgumentList.Contains("--strict-mcp-config"), "Claude args must include --strict-mcp-config");
                AssertContains("WorkProduced", req.StdinPayload!, "stdin should contain the event text");
                AssertContains("[AgentWake]", req.StdinPayload!, "stdin should contain one-shot instruction");
            });

            await RunTest("FireDrainer_AgentWake_WithClaudeSessionId_UsesResume", async () =>
            {
                RecordingAgentWakeProcessHost host = new RecordingAgentWakeProcessHost();
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                RemoteTriggerSettings settings = MakeAgentWakeSettings();
                settings.AgentWake = new AgentWakeSettings { Runtime = AgentWakeRuntime.Claude, SessionId = "sess_abc123" };
                RemoteTriggerService service = new RemoteTriggerService(settings, http, host, new LoggingModule(), TimeSpan.Zero);

                await service.FireDrainerAsync("vessel-a", "event");

                AgentWakeProcessRequest req = host.LastRequest!;
                AssertTrue(req.ArgumentList.Contains("--resume"), "Claude with SessionId must use --resume");
                AssertTrue(req.ArgumentList.Contains("sess_abc123"), "Claude --resume must include the session id");
                AssertFalse(req.ArgumentList.Contains("--continue"), "Claude with SessionId must NOT use --continue");
            });

            await RunTest("FireDrainer_AgentWake_WithCodexSessionId_UsesExecResume", async () =>
            {
                RecordingAgentWakeProcessHost host = new RecordingAgentWakeProcessHost();
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                RemoteTriggerSettings settings = MakeAgentWakeSettings();
                settings.AgentWake = new AgentWakeSettings { Runtime = AgentWakeRuntime.Codex, SessionId = "codex-sess-xyz" };
                RemoteTriggerService service = new RemoteTriggerService(settings, http, host, new LoggingModule(), TimeSpan.Zero);

                await service.FireDrainerAsync("vessel-a", "event");

                AgentWakeProcessRequest req = host.LastRequest!;
                AssertEqual("codex", req.Command, "Codex runtime should invoke 'codex'");
                AssertTrue(req.ArgumentList.Contains("exec"), "Codex args must include exec");
                AssertTrue(req.ArgumentList.Contains("resume"), "Codex args must include resume");
                AssertTrue(req.ArgumentList.Contains("codex-sess-xyz"), "Codex resume must include session id");
                AssertTrue(req.ArgumentList.Contains("-"), "Codex must include - to read stdin");
                AssertFalse(req.ArgumentList.Contains("--last"), "Codex with SessionId must NOT use --last");
            });

            await RunTest("FireDrainer_AgentWake_WithoutCodexSessionId_UsesLast", async () =>
            {
                RecordingAgentWakeProcessHost host = new RecordingAgentWakeProcessHost();
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                RemoteTriggerSettings settings = MakeAgentWakeSettings();
                settings.AgentWake = new AgentWakeSettings { Runtime = AgentWakeRuntime.Codex };
                RemoteTriggerService service = new RemoteTriggerService(settings, http, host, new LoggingModule(), TimeSpan.Zero);

                await service.FireDrainerAsync("vessel-a", "event");

                AgentWakeProcessRequest req = host.LastRequest!;
                AssertTrue(req.ArgumentList.Contains("--last"), "Codex without SessionId must use --last");
                AssertFalse(req.ArgumentList.Any(a => a.StartsWith("sess_")), "Codex without SessionId must not include a session id");
            });

            await RunTest("FireDrainer_AgentWake_Auto_DefaultPreferenceUsesCodexLast", async () =>
            {
                RecordingAgentWakeProcessHost host = new RecordingAgentWakeProcessHost();
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                RemoteTriggerSettings settings = MakeAgentWakeSettings();
                settings.AgentWake = new AgentWakeSettings { Runtime = AgentWakeRuntime.Auto };
                RemoteTriggerService service = new RemoteTriggerService(settings, http, host, new LoggingModule(), TimeSpan.Zero);

                await service.FireDrainerAsync("vessel-a", "event");

                AgentWakeProcessRequest req = host.LastRequest!;
                AssertEqual("codex", req.Command, "Auto without a registered session should use Codex first by default");
                AssertTrue(req.ArgumentList.Contains("--last"), "Auto Codex fallback should use --last");
            });

            await RunTest("FireDrainer_AgentWake_Auto_RegisteredClaudeSessionWins", async () =>
            {
                RecordingAgentWakeProcessHost host = new RecordingAgentWakeProcessHost();
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                RemoteTriggerSettings settings = MakeAgentWakeSettings();
                settings.AgentWake = new AgentWakeSettings { Runtime = AgentWakeRuntime.Auto };
                RemoteTriggerService service = new RemoteTriggerService(settings, http, host, new LoggingModule(), TimeSpan.Zero);
                service.RegisterAgentWakeSession(new AgentWakeSessionRegistration
                {
                    Runtime = AgentWakeRuntime.Claude,
                    SessionId = "claude-session-123",
                    Command = "custom-claude",
                    WorkingDirectory = "registered-workdir",
                    ClientName = "unit-test"
                });

                await service.FireDrainerAsync("vessel-a", "event");

                AgentWakeProcessRequest req = host.LastRequest!;
                AssertEqual("custom-claude", req.Command, "registered command should be used for Auto session wake");
                AssertEqual("registered-workdir", req.WorkingDirectory, "registered working directory should be used for Auto session wake");
                AssertTrue(req.ArgumentList.Contains("--resume"), "registered Claude session should use --resume");
                AssertTrue(req.ArgumentList.Contains("claude-session-123"), "registered Claude session id should be resumed");
            });

            await RunTest("FireDrainer_AgentWake_Auto_FallsBackWhenFirstCandidateUnavailable", async () =>
            {
                RecordingAgentWakeProcessHost host = new RecordingAgentWakeProcessHost();
                host.FailCommands.Add("codex");
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                RemoteTriggerSettings settings = MakeAgentWakeSettings();
                settings.AgentWake = new AgentWakeSettings
                {
                    Runtime = AgentWakeRuntime.Auto,
                    RuntimePreference = new List<AgentWakeRuntime> { AgentWakeRuntime.Codex, AgentWakeRuntime.Claude }
                };
                RemoteTriggerService service = new RemoteTriggerService(settings, http, host, new LoggingModule(), TimeSpan.Zero);

                await service.FireDrainerAsync("vessel-a", "event");

                AssertEqual(2, host.StartCallCount, "Auto should try Codex then fall back to Claude when Codex cannot start");
                AssertEqual("claude", host.LastRequest!.Command, "Auto fallback should start Claude after Codex fails");
                AssertTrue(host.LastRequest.ArgumentList.Contains("--continue"), "Claude fallback without session should use --continue");
            });

            await RunTest("McpAgentWakeTools_RegisterSession_UpdatesAutoRuntime", async () =>
            {
                RecordingAgentWakeProcessHost host = new RecordingAgentWakeProcessHost();
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                RemoteTriggerSettings settings = MakeAgentWakeSettings();
                settings.AgentWake = new AgentWakeSettings { Runtime = AgentWakeRuntime.Auto };
                RemoteTriggerService service = new RemoteTriggerService(settings, http, host, new LoggingModule(), TimeSpan.Zero);
                Func<JsonElement?, Task<object>>? handler = null;
                McpAgentWakeTools.Register((name, description, schema, registeredHandler) =>
                {
                    if (name == "armada_register_agentwake_session") handler = registeredHandler;
                }, service);

                AssertNotNull(handler, "armada_register_agentwake_session handler should be registered");
                using JsonDocument doc = JsonDocument.Parse("{\"runtime\":\"Codex\",\"sessionId\":\"codex-session-456\",\"workingDirectory\":\"mcp-workdir\"}");
                await handler!(doc.RootElement).ConfigureAwait(false);
                await service.FireDrainerAsync("vessel-a", "event").ConfigureAwait(false);

                AgentWakeProcessRequest req = host.LastRequest!;
                AssertEqual("codex", req.Command, "MCP-registered runtime should drive Auto wake");
                AssertTrue(req.ArgumentList.Contains("codex-session-456"), "MCP-registered session should be resumed");
                AssertEqual("mcp-workdir", req.WorkingDirectory, "MCP-registered working directory should be used");
            });

            await RunTest("FireDrainer_AgentWake_CustomSettings_PropagatesRequestOptions", async () =>
            {
                RecordingAgentWakeProcessHost host = new RecordingAgentWakeProcessHost();
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                Dictionary<string, string> env = new Dictionary<string, string>();
                env["ARMADA_AGENTWAKE_TEST"] = "1";
                RemoteTriggerSettings settings = MakeAgentWakeSettings();
                settings.AgentWake = new AgentWakeSettings
                {
                    Runtime = AgentWakeRuntime.Claude,
                    Command = "custom-claude",
                    WorkingDirectory = "agent-wake-workdir",
                    TimeoutSeconds = 0,
                    EnvironmentVariables = env,
                };
                RemoteTriggerService service = new RemoteTriggerService(settings, http, host, new LoggingModule(), TimeSpan.Zero);

                await service.FireDrainerAsync("vessel-a", "event");

                AgentWakeProcessRequest req = host.LastRequest!;
                AssertEqual("custom-claude", req.Command, "custom command should be passed to the process request");
                AssertEqual("agent-wake-workdir", req.WorkingDirectory, "working directory should be passed through");
                AssertEqual(1, req.TimeoutSeconds, "timeout should be clamped to at least 1 second");
                Dictionary<string, string>? actualEnv = req.EnvironmentVariables;
                AssertNotNull(actualEnv, "environment variables should be passed through");
                AssertEqual("1", actualEnv!["ARMADA_AGENTWAKE_TEST"], "environment variable should be preserved");
            });

            await RunTest("FireDrainer_AgentWake_SameVesselWithin60s_Coalesced", async () =>
            {
                RecordingAgentWakeProcessHost host = new RecordingAgentWakeProcessHost();
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                RemoteTriggerService service = new RemoteTriggerService(MakeAgentWakeSettings(), http, host, new LoggingModule(), TimeSpan.Zero);

                await service.FireDrainerAsync("vessel-a", "first event");
                await service.FireDrainerAsync("vessel-a", "second event within window");

                AssertEqual(1, host.StartCallCount, "second call for same vessel within 60s should be coalesced");
            });

            await RunTest("FireDrainer_AgentWake_HitsThrottle_Suppressed", async () =>
            {
                RecordingAgentWakeProcessHost host = new RecordingAgentWakeProcessHost();
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                RemoteTriggerService service = new RemoteTriggerService(MakeAgentWakeSettings(), http, host, new LoggingModule(), TimeSpan.Zero);

                for (int i = 0; i < 20; i++)
                    await service.FireDrainerAsync("vessel-" + i, "event-" + i);

                AssertEqual(20, host.StartCallCount, "20 distinct vessels should each spawn once");

                await service.FireDrainerAsync("vessel-throttled", "should be suppressed");

                AssertEqual(20, host.StartCallCount, "21st fire should be suppressed by throttle");
            });

            await RunTest("FireDrainer_AgentWake_CustomThrottleCap_SixthWakeSuppressed", async () =>
            {
                RecordingAgentWakeProcessHost host = new RecordingAgentWakeProcessHost();
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                RemoteTriggerSettings settings = MakeAgentWakeSettings();
                settings.ThrottleCapPerHour = 5;
                RemoteTriggerService service = new RemoteTriggerService(settings, http, host, new LoggingModule(), TimeSpan.Zero);

                for (int i = 0; i < 5; i++)
                    await service.FireDrainerAsync("vessel-agentwake-" + i, "event-" + i);

                AssertEqual(5, host.StartCallCount, "5 distinct vessels should each spawn once at custom AgentWake cap");

                await service.FireDrainerAsync("vessel-agentwake-throttled", "sixth should be suppressed");

                AssertEqual(5, host.StartCallCount, "6th AgentWake should be suppressed when cap is 5");
            });

            await RunTest("FireCritical_AgentWake_AfterThrottleCap_BypassesThrottle", async () =>
            {
                RecordingAgentWakeProcessHost host = new RecordingAgentWakeProcessHost();
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                RemoteTriggerService service = new RemoteTriggerService(MakeAgentWakeSettings(), http, host, new LoggingModule(), TimeSpan.Zero);

                for (int i = 0; i < 20; i++)
                    await service.FireDrainerAsync("vessel-" + i, "event-" + i);

                AssertEqual(20, host.StartCallCount, "drainer wakes should fill the throttle window");

                await service.FireCriticalAsync("critical event after throttle cap");

                AssertEqual(21, host.StartCallCount, "critical AgentWake should bypass drainer throttle");
                AssertContains("[CRITICAL]", host.LastRequest!.StdinPayload!, "critical payload should retain the critical marker");
            });

            await RunTest("FireDrainer_AgentWake_SingleFlight_SuppressesConcurrentWake", async () =>
            {
                RecordingAgentWakeProcessHost host = new RecordingAgentWakeProcessHost();
                host.BlockRelease = true; // don't call onExited immediately so lease stays held
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                RemoteTriggerService service = new RemoteTriggerService(MakeAgentWakeSettings(), http, host, new LoggingModule(), TimeSpan.Zero);

                await service.FireDrainerAsync("vessel-a", "first event -- acquires lease");
                await service.FireDrainerAsync("vessel-b", "second event -- should be suppressed by single-flight");

                AssertEqual(1, host.StartCallCount, "second call while AgentWake is running should be suppressed by single-flight");
            });

            await RunTest("FireCritical_AgentWake_BypassesCoalescing_HonorsSingleFlight", async () =>
            {
                RecordingAgentWakeProcessHost host = new RecordingAgentWakeProcessHost();
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                RemoteTriggerService service = new RemoteTriggerService(MakeAgentWakeSettings(), http, host, new LoggingModule(), TimeSpan.Zero);

                // Critical should start even without a prior drainer call
                await service.FireCriticalAsync("audit critical finding");

                AssertEqual(1, host.StartCallCount, "critical AgentWake should spawn a process");
                AssertContains("[CRITICAL]", host.LastRequest!.StdinPayload!, "critical AgentWake stdin should include [CRITICAL] prefix");
            });

            await RunTest("FireCritical_AgentWake_SingleFlight_SuppressesWhileRunning", async () =>
            {
                RecordingAgentWakeProcessHost host = new RecordingAgentWakeProcessHost();
                host.BlockRelease = true;
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                RemoteTriggerService service = new RemoteTriggerService(MakeAgentWakeSettings(), http, host, new LoggingModule(), TimeSpan.Zero);

                await service.FireCriticalAsync("first critical event -- acquires lease");
                await service.FireCriticalAsync("second critical event -- should be suppressed");

                AssertEqual(1, host.StartCallCount, "second critical while AgentWake is running should be suppressed by single-flight");
            });

            await RunTest("FireDrainer_AgentWake_SpawnFailure_RetriesAndIncrements", async () =>
            {
                RecordingAgentWakeProcessHost host = new RecordingAgentWakeProcessHost();
                host.AlwaysFail = true;
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                RemoteTriggerService service = new RemoteTriggerService(MakeAgentWakeSettings(), http, host, new LoggingModule(), TimeSpan.Zero);

                await service.FireDrainerAsync("vessel-a", "event that will fail");

                AssertEqual(2, host.StartCallCount, "spawn failure should be retried once (2 total attempts)");
                AssertEqual(1, service.ConsecutiveFailures, "consecutive failure counter should increment after spawn failure");
            });

            await RunTest("FireDrainer_AgentWake_CanceledRetry_ReleasesSingleFlightLease", async () =>
            {
                RecordingAgentWakeProcessHost host = new RecordingAgentWakeProcessHost();
                host.AlwaysFail = true;
                RecordingRemoteTriggerHttpClient http = new RecordingRemoteTriggerHttpClient();
                RemoteTriggerService service = new RemoteTriggerService(MakeAgentWakeSettings(), http, host, new LoggingModule(), TimeSpan.FromMinutes(5));
                using CancellationTokenSource cts = new CancellationTokenSource();
                cts.Cancel();
                bool canceled = false;

                try
                {
                    await service.FireDrainerAsync("vessel-a", "event canceled during retry", cts.Token);
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                }

                AssertTrue(canceled, "canceled retry delay should propagate cancellation");
                AssertEqual(1, host.StartCallCount, "canceled retry should not make the second start attempt");

                host.AlwaysFail = false;
                await service.FireDrainerAsync("vessel-b", "event after cancellation");

                AssertEqual(2, host.StartCallCount, "single-flight lease should be released after canceled retry");
            });
        }

        private static RemoteTriggerSettings MakeSettings()
        {
            return new RemoteTriggerSettings
            {
                Enabled = true,
                DrainerFireUrl = "https://api.anthropic.com/v1/claude_code/routines/trig_test/fire",
                DrainerBearerToken = "sk-ant-test-token",
            };
        }

        private static RemoteTriggerSettings MakeAgentWakeSettings()
        {
            return new RemoteTriggerSettings
            {
                Enabled = true,
                Mode = RemoteTriggerMode.AgentWake,
            };
        }

        private static RemoteTriggerSettings MakeDisabledModeSettings()
        {
            return new RemoteTriggerSettings
            {
                Enabled = true,
                Mode = RemoteTriggerMode.Disabled,
                DrainerFireUrl = "https://api.anthropic.com/v1/claude_code/routines/trig_test/fire",
                DrainerBearerToken = "sk-ant-test-token",
                CriticalFireUrl = "https://api.anthropic.com/v1/claude_code/routines/trig_critical/fire",
                CriticalBearerToken = "sk-ant-critical-token",
            };
        }

        private sealed class RecordingAgentWakeProcessHost : IAgentWakeProcessHost
        {
            private readonly List<AgentWakeProcessRequest> _Calls = new List<AgentWakeProcessRequest>();
            private Action? _PendingCallback;

            public int StartCallCount => _Calls.Count;
            public AgentWakeProcessRequest? LastRequest => _Calls.Count > 0 ? _Calls[_Calls.Count - 1] : null;
            public bool AlwaysFail { get; set; } = false;
            public HashSet<string> FailCommands { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // When true, onExited is stored but not called immediately (simulates long-running process)
            public bool BlockRelease { get; set; } = false;

            public bool TryStart(AgentWakeProcessRequest request, Action onExited)
            {
                _Calls.Add(request);
                if (FailCommands.Contains(request.Command)) return false;
                if (AlwaysFail) return false;
                if (BlockRelease)
                    _PendingCallback = onExited;
                else
                    onExited();
                return true;
            }

            public void ReleasePending() { _PendingCallback?.Invoke(); _PendingCallback = null; }
        }

        private sealed class RecordingRemoteTriggerHttpClient : IRemoteTriggerHttpClient
        {
            private readonly Queue<FireResult> _QueuedResults = new Queue<FireResult>();
            private readonly List<FireRequest> _Calls = new List<FireRequest>();

            public int CallCount => _Calls.Count;
            public FireRequest? LastRequest => _Calls.Count > 0 ? _Calls[_Calls.Count - 1] : null;

            public void EnqueueResult(FireResult result) => _QueuedResults.Enqueue(result);

            public Task<FireResult> FireAsync(FireRequest request, CancellationToken token = default)
            {
                _Calls.Add(request);
                FireResult result = _QueuedResults.Count > 0
                    ? _QueuedResults.Dequeue()
                    : new FireResult { Outcome = FireOutcome.Success, StatusCode = 200 };
                return Task.FromResult(result);
            }
        }
    }
}
