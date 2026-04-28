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

    public class RemoteTriggerServiceLocalDaemonModeTests : TestSuite
    {
        public override string Name => "RemoteTrigger Service LocalDaemon Mode";

        protected override async Task RunTestsAsync()
        {
            await RunTest("FireDrainer_LocalDaemonMode_CallsSpawnerNotHttp", async () =>
            {
                RecordingProcessHost processHost = new RecordingProcessHost();
                RecordingHttpClient http = new RecordingHttpClient();
                RemoteTriggerService service = new RemoteTriggerService(MakeLocalDaemonSettings(), http, processHost, new LoggingModule(), TimeSpan.Zero);

                await service.FireDrainerAsync("vessel-ld-1", "WorkProduced: mission m1");

                AssertEqual(0, http.CallCount, "LocalDaemon mode should not call HTTP client");
                AssertEqual(1, processHost.SpawnCallCount, "LocalDaemon mode should call spawner once");
            });

            await RunTest("FireDrainer_LocalDaemonMode_StdinIsTemplateNewlinePlusText", async () =>
            {
                RecordingProcessHost processHost = new RecordingProcessHost();
                RecordingHttpClient http = new RecordingHttpClient();
                RemoteTriggerSettings settings = MakeLocalDaemonSettings("You are the orchestrator.\nDrain the queue.");
                RemoteTriggerService service = new RemoteTriggerService(settings, http, processHost, new LoggingModule(), TimeSpan.Zero);

                await service.FireDrainerAsync("vessel-ld-2", "WorkProduced: mission m99");

                AssertEqual(1, processHost.SpawnCallCount, "should have spawned once");
                string stdin = processHost.LastRequest!.StdinPayload;
                AssertContains("You are the orchestrator.", stdin, "stdin should contain prompt template");
                AssertContains("WorkProduced: mission m99", stdin, "stdin should contain event text");
                // blank line between template and text
                AssertContains("\n\n", stdin, "stdin should have blank line separator between template and text");
            });

            await RunTest("FireDrainer_RemoteFireMode_CallsHttpNotSpawner", async () =>
            {
                RecordingProcessHost processHost = new RecordingProcessHost();
                RecordingHttpClient http = new RecordingHttpClient();
                RemoteTriggerService service = new RemoteTriggerService(MakeRemoteFireSettings(), http, processHost, new LoggingModule(), TimeSpan.Zero);

                await service.FireDrainerAsync("vessel-rf-1", "WorkProduced: mission m2");

                AssertEqual(1, http.CallCount, "RemoteFire mode should call HTTP client");
                AssertEqual(0, processHost.SpawnCallCount, "RemoteFire mode should not call spawner");
            });

            await RunTest("FireDrainer_DisabledMode_CallsNeither", async () =>
            {
                RecordingProcessHost processHost = new RecordingProcessHost();
                RecordingHttpClient http = new RecordingHttpClient();
                RemoteTriggerSettings settings = new RemoteTriggerSettings
                {
                    Enabled = true,
                    Mode = RemoteTriggerMode.Disabled,
                };
                RemoteTriggerService service = new RemoteTriggerService(settings, http, processHost, new LoggingModule(), TimeSpan.Zero);

                await service.FireDrainerAsync("vessel-dis-1", "some event");

                AssertEqual(0, http.CallCount, "Disabled mode should not call HTTP client");
                AssertEqual(0, processHost.SpawnCallCount, "Disabled mode should not call spawner");
            });

            await RunTest("FireCritical_LocalDaemonMode_CallsSpawnerNotHttp", async () =>
            {
                RecordingProcessHost processHost = new RecordingProcessHost();
                RecordingHttpClient http = new RecordingHttpClient();
                RemoteTriggerService service = new RemoteTriggerService(MakeLocalDaemonSettings(), http, processHost, new LoggingModule(), TimeSpan.Zero);

                await service.FireCriticalAsync("audit critical finding");

                AssertEqual(0, http.CallCount, "LocalDaemon critical should not call HTTP client");
                AssertEqual(1, processHost.SpawnCallCount, "LocalDaemon critical should call spawner once");
            });

            await RunTest("FireDrainer_LocalDaemonMode_CoalescingStillApplies", async () =>
            {
                RecordingProcessHost processHost = new RecordingProcessHost();
                RecordingHttpClient http = new RecordingHttpClient();
                RemoteTriggerService service = new RemoteTriggerService(MakeLocalDaemonSettings(), http, processHost, new LoggingModule(), TimeSpan.Zero);

                await service.FireDrainerAsync("vessel-coal-1", "first event");
                await service.FireDrainerAsync("vessel-coal-1", "second event within 60s");

                AssertEqual(1, processHost.SpawnCallCount, "second call for same vessel within 60s should be coalesced in LocalDaemon mode");
            });
        }

        private static RemoteTriggerSettings MakeLocalDaemonSettings(string promptTemplate = "drain the queue")
        {
            return new RemoteTriggerSettings
            {
                Enabled = true,
                Mode = RemoteTriggerMode.LocalDaemon,
                LocalDaemon = new LocalDaemonSettings
                {
                    Command = "claude",
                    Args = "--dangerously-skip-permissions --print",
                    PromptTemplate = promptTemplate,
                },
            };
        }

        private static RemoteTriggerSettings MakeRemoteFireSettings()
        {
            return new RemoteTriggerSettings
            {
                Enabled = true,
                Mode = RemoteTriggerMode.RemoteFire,
                DrainerFireUrl = "https://api.anthropic.com/v1/claude_code/routines/trig_test/fire",
                DrainerBearerToken = "sk-ant-test-token",
            };
        }

        private sealed class RecordingProcessHost : IProcessHost
        {
            private readonly List<ProcessSpawnRequest> _Calls = new List<ProcessSpawnRequest>();

            public int SpawnCallCount => _Calls.Count;
            public ProcessSpawnRequest? LastRequest => _Calls.Count > 0 ? _Calls[_Calls.Count - 1] : null;

            public Task<ProcessSpawnResult> SpawnDetachedAsync(ProcessSpawnRequest request, CancellationToken token)
            {
                _Calls.Add(request);
                return Task.FromResult(new ProcessSpawnResult { ProcessId = 99999, Exited = false });
            }
        }

        private sealed class RecordingHttpClient : IRemoteTriggerHttpClient
        {
            private int _CallCount = 0;

            public int CallCount => _CallCount;

            public Task<FireResult> FireAsync(FireRequest request, CancellationToken token = default)
            {
                _CallCount++;
                return Task.FromResult(new FireResult { Outcome = FireOutcome.Success, StatusCode = 200 });
            }
        }
    }
}
