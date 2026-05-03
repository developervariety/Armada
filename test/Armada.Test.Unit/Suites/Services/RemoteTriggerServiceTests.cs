namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
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
