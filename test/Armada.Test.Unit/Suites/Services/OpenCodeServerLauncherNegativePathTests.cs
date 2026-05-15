namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Net;
    using System.Net.Http;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using SyslogLogging;

    /// <summary>
    /// Edge-case and negative-path coverage for OpenCodeServerLauncher. Sibling
    /// OpenCodeServerLauncherTests covers the happy paths (already-healthy attach, AutoLaunch
    /// false no-op, Http inference no-op, spawn-then-healthy). This suite covers:
    /// - constructor null-arg validation,
    /// - process-runner returning null,
    /// - cancellation during health polling,
    /// - explicit ExecutablePath override (no cmd.exe shim),
    /// - Basic auth header on health probes when password is configured,
    /// - OPENCODE_SERVER_PASSWORD env var on spawned process when password is configured,
    /// - base-URL trailing-slash normalization on the health endpoint,
    /// - Dispose is a no-op when attached to an existing daemon,
    /// - Dispose is idempotent (safe to call multiple times),
    /// - never-becomes-healthy startup timeout still disposes the spawned process before throwing.
    /// </summary>
    public class OpenCodeServerLauncherNegativePathTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "OpenCode Server Launcher (negative paths)";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Constructor_NullSettings_ThrowsArgumentNullException", () =>
            {
                HttpClient http = new HttpClient(new EmptyHandler());
                AssertThrows<ArgumentNullException>(() =>
                {
                    OpenCodeServerLauncher _ = new OpenCodeServerLauncher(null!, SilentLogging(), http);
                });
            });

            await RunTest("Constructor_NullLogging_ThrowsArgumentNullException", () =>
            {
                HttpClient http = new HttpClient(new EmptyHandler());
                AssertThrows<ArgumentNullException>(() =>
                {
                    OpenCodeServerLauncher _ = new OpenCodeServerLauncher(BuildSettings("OpenCodeServer", true), null!, http);
                });
            });

            await RunTest("Constructor_NullHttpClient_ThrowsArgumentNullException", () =>
            {
                AssertThrows<ArgumentNullException>(() =>
                {
                    OpenCodeServerLauncher _ = new OpenCodeServerLauncher(BuildSettings("OpenCodeServer", true), SilentLogging(), null!);
                });
            });

            await RunTest("Constructor_TestingCtor_NullProcessRunner_ThrowsArgumentNullException", () =>
            {
                HttpClient http = new HttpClient(new EmptyHandler());
                AssertThrows<ArgumentNullException>(() =>
                {
                    OpenCodeServerLauncher _ = new OpenCodeServerLauncher(
                        BuildSettings("OpenCodeServer", true),
                        SilentLogging(),
                        http,
                        null!,
                        NoDelayAsync);
                });
            });

            await RunTest("Constructor_TestingCtor_NullDelayFunc_ThrowsArgumentNullException", () =>
            {
                HttpClient http = new HttpClient(new EmptyHandler());
                AssertThrows<ArgumentNullException>(() =>
                {
                    OpenCodeServerLauncher _ = new OpenCodeServerLauncher(
                        BuildSettings("OpenCodeServer", true),
                        SilentLogging(),
                        http,
                        new RecordingProcessRunner(),
                        null!);
                });
            });

            await RunTest("StartAsync_ProcessRunnerReturnsNull_ThrowsInvalidOperationException", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(HttpStatusCode.OK, "{\"healthy\":false}");
                HttpClient http = new HttpClient(handler);
                NullReturningProcessRunner runner = new NullReturningProcessRunner();
                OpenCodeServerLauncher launcher = new OpenCodeServerLauncher(BuildSettings("OpenCodeServer", true), SilentLogging(), http, runner, NoDelayAsync);

                await AssertThrowsAsync<InvalidOperationException>(async () =>
                {
                    await launcher.StartAsync().ConfigureAwait(false);
                }).ConfigureAwait(false);

                AssertEqual(1, runner.StartCallCount, "Runner must be invoked exactly once before failing.");
            });

            await RunTest("StartAsync_PreCancelledToken_ThrowsOperationCanceledException", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                HttpClient http = new HttpClient(handler);
                RecordingProcessRunner runner = new RecordingProcessRunner();
                OpenCodeServerLauncher launcher = new OpenCodeServerLauncher(BuildSettings("OpenCodeServer", true), SilentLogging(), http, runner, NoDelayAsync);

                using CancellationTokenSource cts = new CancellationTokenSource();
                cts.Cancel();

                await AssertThrowsAsync<OperationCanceledException>(async () =>
                {
                    await launcher.StartAsync(cts.Token).ConfigureAwait(false);
                }).ConfigureAwait(false);
            });

            await RunTest("StartAsync_HealthProbePassword_AddsBasicAuthHeader", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(HttpStatusCode.OK, "{\"healthy\":true}");
                HttpClient http = new HttpClient(handler);
                RecordingProcessRunner runner = new RecordingProcessRunner();
                ArmadaSettings settings = BuildSettings("OpenCodeServer", true);
                settings.CodeIndex.OpenCodeServer.Username = "alice";
                settings.CodeIndex.OpenCodeServer.Password = "s3cret";
                OpenCodeServerLauncher launcher = new OpenCodeServerLauncher(settings, SilentLogging(), http, runner, NoDelayAsync);

                await launcher.StartAsync().ConfigureAwait(false);

                AssertEqual(1, handler.RequestUris.Count);
                AssertEqual("http://127.0.0.1:4096/global/health", handler.RequestUris[0]);
                string expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:s3cret"));
                AssertEqual(expected, handler.AuthorizationHeaders[0], "Health probe must carry Basic auth when password is set.");
            });

            await RunTest("StartAsync_BaseUrlWithTrailingSlash_NormalizedHealthEndpoint", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(HttpStatusCode.OK, "{\"healthy\":true}");
                HttpClient http = new HttpClient(handler);
                RecordingProcessRunner runner = new RecordingProcessRunner();
                ArmadaSettings settings = BuildSettings("OpenCodeServer", true);
                settings.CodeIndex.OpenCodeServer.BaseUrl = "  http://127.0.0.1:9999/  ";
                OpenCodeServerLauncher launcher = new OpenCodeServerLauncher(settings, SilentLogging(), http, runner, NoDelayAsync);

                await launcher.StartAsync().ConfigureAwait(false);

                AssertEqual("http://127.0.0.1:9999/global/health", handler.RequestUris[0]);
            });

            await RunTest("StartAsync_ExecutablePathSet_OverridesCmdShim", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(HttpStatusCode.OK, "{\"healthy\":false}");
                handler.Enqueue(HttpStatusCode.OK, "{\"healthy\":true}");
                HttpClient http = new HttpClient(handler);
                RecordingProcessRunner runner = new RecordingProcessRunner();
                ArmadaSettings settings = BuildSettings("OpenCodeServer", true);
                settings.CodeIndex.OpenCodeServer.ExecutablePath = "/opt/opencode/bin/opencode";
                OpenCodeServerLauncher launcher = new OpenCodeServerLauncher(settings, SilentLogging(), http, runner, NoDelayAsync);

                await launcher.StartAsync().ConfigureAwait(false);

                AssertEqual(1, runner.StartCallCount);
                AssertNotNull(runner.LastStartInfo);
                AssertEqual("/opt/opencode/bin/opencode", runner.LastStartInfo!.FileName, "ExecutablePath must override file name resolution.");
                AssertEqual("serve --port 4096 --hostname 127.0.0.1", runner.LastStartInfo.Arguments);
            });

            await RunTest("StartAsync_PasswordSet_PutsEnvVarOnSpawnedProcess", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(HttpStatusCode.OK, "{\"healthy\":false}");
                handler.Enqueue(HttpStatusCode.OK, "{\"healthy\":true}");
                HttpClient http = new HttpClient(handler);
                RecordingProcessRunner runner = new RecordingProcessRunner();
                ArmadaSettings settings = BuildSettings("OpenCodeServer", true);
                settings.CodeIndex.OpenCodeServer.ExecutablePath = "/opt/opencode/bin/opencode";
                settings.CodeIndex.OpenCodeServer.Password = "secret-pw";
                OpenCodeServerLauncher launcher = new OpenCodeServerLauncher(settings, SilentLogging(), http, runner, NoDelayAsync);

                await launcher.StartAsync().ConfigureAwait(false);

                AssertNotNull(runner.LastStartInfo);
                AssertTrue(
                    runner.LastStartInfo!.Environment.ContainsKey("OPENCODE_SERVER_PASSWORD"),
                    "Spawned process should carry OPENCODE_SERVER_PASSWORD when password is configured.");
                AssertEqual("secret-pw", runner.LastStartInfo.Environment["OPENCODE_SERVER_PASSWORD"]);
            });

            await RunTest("StartAsync_PasswordEmpty_DoesNotSetEnvVarOnSpawnedProcess", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(HttpStatusCode.OK, "{\"healthy\":false}");
                handler.Enqueue(HttpStatusCode.OK, "{\"healthy\":true}");
                HttpClient http = new HttpClient(handler);
                RecordingProcessRunner runner = new RecordingProcessRunner();
                ArmadaSettings settings = BuildSettings("OpenCodeServer", true);
                settings.CodeIndex.OpenCodeServer.ExecutablePath = "/opt/opencode/bin/opencode";
                settings.CodeIndex.OpenCodeServer.Password = string.Empty;
                OpenCodeServerLauncher launcher = new OpenCodeServerLauncher(settings, SilentLogging(), http, runner, NoDelayAsync);

                await launcher.StartAsync().ConfigureAwait(false);

                AssertNotNull(runner.LastStartInfo);
                AssertFalse(
                    runner.LastStartInfo!.Environment.ContainsKey("OPENCODE_SERVER_PASSWORD"),
                    "Spawned process must not carry OPENCODE_SERVER_PASSWORD when password is blank.");
            });

            await RunTest("StartAsync_NonDefaultPortAndHostname_FlowIntoServeArguments", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(HttpStatusCode.OK, "{\"healthy\":false}");
                handler.Enqueue(HttpStatusCode.OK, "{\"healthy\":true}");
                HttpClient http = new HttpClient(handler);
                RecordingProcessRunner runner = new RecordingProcessRunner();
                ArmadaSettings settings = BuildSettings("OpenCodeServer", true);
                settings.CodeIndex.OpenCodeServer.Port = 5000;
                settings.CodeIndex.OpenCodeServer.Hostname = "0.0.0.0";
                settings.CodeIndex.OpenCodeServer.ExecutablePath = "/opt/opencode/bin/opencode";
                OpenCodeServerLauncher launcher = new OpenCodeServerLauncher(settings, SilentLogging(), http, runner, NoDelayAsync);

                await launcher.StartAsync().ConfigureAwait(false);

                AssertNotNull(runner.LastStartInfo);
                AssertEqual("serve --port 5000 --hostname 0.0.0.0", runner.LastStartInfo!.Arguments);
            });

            await RunTest("Dispose_AttachedToExistingDaemon_DoesNotKillAnything", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(HttpStatusCode.OK, "{\"healthy\":true}");
                HttpClient http = new HttpClient(handler);
                RecordingProcessRunner runner = new RecordingProcessRunner();
                OpenCodeServerLauncher launcher = new OpenCodeServerLauncher(BuildSettings("OpenCodeServer", true), SilentLogging(), http, runner, NoDelayAsync);

                await launcher.StartAsync().ConfigureAwait(false);
                launcher.Dispose();

                AssertEqual(0, runner.StartCallCount, "No spawn should occur when attaching to existing daemon.");
            });

            await RunTest("Dispose_StartAsyncNeverCalled_DoesNotThrow", () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                HttpClient http = new HttpClient(handler);
                RecordingProcessRunner runner = new RecordingProcessRunner();
                OpenCodeServerLauncher launcher = new OpenCodeServerLauncher(BuildSettings("OpenCodeServer", true), SilentLogging(), http, runner, NoDelayAsync);

                launcher.Dispose();
            });

            await RunTest("Dispose_CalledTwice_IdempotentAndDoesNotThrow", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(HttpStatusCode.OK, "{\"healthy\":false}");
                handler.Enqueue(HttpStatusCode.OK, "{\"healthy\":true}");
                HttpClient http = new HttpClient(handler);
                RecordingProcessRunner runner = new RecordingProcessRunner();
                OpenCodeServerLauncher launcher = new OpenCodeServerLauncher(BuildSettings("OpenCodeServer", true), SilentLogging(), http, runner, NoDelayAsync);

                await launcher.StartAsync().ConfigureAwait(false);
                AssertNotNull(runner.LastLaunched);

                launcher.Dispose();
                launcher.Dispose();

                AssertEqual(1, runner.LastLaunched!.KillCallCount, "Process kill must only occur on the first Dispose.");
                AssertEqual(1, runner.LastLaunched.WaitForExitCallCount, "WaitForExit must only occur on the first Dispose.");
                AssertEqual(1, runner.LastLaunched.DisposeCallCount, "Process Dispose must only occur on the first Dispose.");
            });

            await RunTest("Dispose_SpawnedProcessKillThrows_StillCompletes", async () =>
            {
                ScriptedHttpMessageHandler handler = new ScriptedHttpMessageHandler();
                handler.Enqueue(HttpStatusCode.OK, "{\"healthy\":false}");
                handler.Enqueue(HttpStatusCode.OK, "{\"healthy\":true}");
                HttpClient http = new HttpClient(handler);
                ThrowingProcessRunner runner = new ThrowingProcessRunner();
                OpenCodeServerLauncher launcher = new OpenCodeServerLauncher(BuildSettings("OpenCodeServer", true), SilentLogging(), http, runner, NoDelayAsync);

                await launcher.StartAsync().ConfigureAwait(false);
                launcher.Dispose();

                AssertNotNull(runner.LastLaunched);
                AssertTrue(runner.LastLaunched!.KillCallCount >= 1, "Kill must have been attempted.");
            });

            await RunTest("StartAsync_NeverHealthy_DisposesSpawnedProcessAndThrowsInvalidOperationException", async () =>
            {
                NeverHealthyHandler handler = new NeverHealthyHandler();
                HttpClient http = new HttpClient(handler);
                RecordingProcessRunner runner = new RecordingProcessRunner();
                ArmadaSettings settings = BuildSettings("OpenCodeServer", true);
                settings.CodeIndex.OpenCodeServer.StartupTimeoutSeconds = 5;
                OpenCodeServerLauncher launcher = new OpenCodeServerLauncher(settings, SilentLogging(), http, runner, RealOneSecondDelayAsync);

                await AssertThrowsAsync<InvalidOperationException>(async () =>
                {
                    await launcher.StartAsync().ConfigureAwait(false);
                }).ConfigureAwait(false);

                AssertEqual(1, runner.StartCallCount, "Daemon must be spawned exactly once before giving up.");
                AssertNotNull(runner.LastLaunched);
                AssertTrue(runner.LastLaunched!.KillCallCount >= 1, "Process must be killed when the startup timeout elapses without a healthy probe.");
            });
        }

        private static ArmadaSettings BuildSettings(string inferenceClient, bool autoLaunch)
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.CodeIndex.InferenceClient = inferenceClient;
            settings.CodeIndex.OpenCodeServer.AutoLaunch = autoLaunch;
            settings.CodeIndex.OpenCodeServer.BaseUrl = "http://127.0.0.1:4096";
            settings.CodeIndex.OpenCodeServer.Port = 4096;
            settings.CodeIndex.OpenCodeServer.Hostname = "127.0.0.1";
            settings.CodeIndex.OpenCodeServer.RequestTimeoutSeconds = 5;
            settings.CodeIndex.OpenCodeServer.StartupTimeoutSeconds = 5;
            return settings;
        }

        private static LoggingModule SilentLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static Task NoDelayAsync(TimeSpan delay, CancellationToken token)
        {
            return Task.CompletedTask;
        }

        private static Task RealOneSecondDelayAsync(TimeSpan delay, CancellationToken token)
        {
            return Task.Delay(TimeSpan.FromSeconds(1), token);
        }

        private sealed class EmptyHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        }

        private sealed class ScriptedHttpMessageHandler : HttpMessageHandler
        {
            private readonly Queue<ResponseScript> _Scripts = new Queue<ResponseScript>();

            public List<string> RequestUris { get; } = new List<string>();
            public List<string> AuthorizationHeaders { get; } = new List<string>();

            public void Enqueue(HttpStatusCode statusCode, string body)
            {
                ResponseScript script = new ResponseScript();
                script.StatusCode = statusCode;
                script.Body = body;
                _Scripts.Enqueue(script);
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequestUris.Add(request.RequestUri?.ToString() ?? string.Empty);
                if (request.Headers.Authorization != null)
                    AuthorizationHeaders.Add(request.Headers.Authorization.Scheme + " " + request.Headers.Authorization.Parameter);
                else
                    AuthorizationHeaders.Add(string.Empty);

                if (_Scripts.Count < 1) throw new InvalidOperationException("No scripted response available.");
                ResponseScript script = _Scripts.Dequeue();
                HttpResponseMessage response = new HttpResponseMessage(script.StatusCode)
                {
                    Content = new StringContent(script.Body ?? string.Empty, Encoding.UTF8, "application/json")
                };
                return Task.FromResult(response);
            }
        }

        private sealed class NeverHealthyHandler : HttpMessageHandler
        {
            public int RequestCount { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestCount++;
                HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"healthy\":false}", Encoding.UTF8, "application/json")
                };
                return Task.FromResult(response);
            }
        }

        private sealed class ResponseScript
        {
            public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
            public string? Body { get; set; }
        }

        private sealed class RecordingProcessRunner : OpenCodeServerLauncher.IProcessRunner
        {
            public int StartCallCount { get; private set; }
            public ProcessStartInfo? LastStartInfo { get; private set; }
            public RecordingLaunchedProcess? LastLaunched { get; private set; }

            public OpenCodeServerLauncher.ILaunchedProcess? Start(ProcessStartInfo startInfo)
            {
                StartCallCount++;
                LastStartInfo = startInfo;
                LastLaunched = new RecordingLaunchedProcess();
                return LastLaunched;
            }
        }

        private sealed class NullReturningProcessRunner : OpenCodeServerLauncher.IProcessRunner
        {
            public int StartCallCount { get; private set; }

            public OpenCodeServerLauncher.ILaunchedProcess? Start(ProcessStartInfo startInfo)
            {
                StartCallCount++;
                return null;
            }
        }

        private sealed class ThrowingProcessRunner : OpenCodeServerLauncher.IProcessRunner
        {
            public ThrowingLaunchedProcess? LastLaunched { get; private set; }

            public OpenCodeServerLauncher.ILaunchedProcess? Start(ProcessStartInfo startInfo)
            {
                LastLaunched = new ThrowingLaunchedProcess();
                return LastLaunched;
            }
        }

        private sealed class RecordingLaunchedProcess : OpenCodeServerLauncher.ILaunchedProcess
        {
            public int ProcessId => 12345;
            public bool HasExited { get; private set; }
            public int KillCallCount { get; private set; }
            public int WaitForExitCallCount { get; private set; }
            public int DisposeCallCount { get; private set; }

            public Task<string> DrainStandardOutputAsync()
            {
                return Task.FromResult(string.Empty);
            }

            public Task<string> DrainStandardErrorAsync()
            {
                return Task.FromResult(string.Empty);
            }

            public void Kill(bool entireProcessTree)
            {
                KillCallCount++;
                HasExited = true;
            }

            public bool WaitForExit(int milliseconds)
            {
                WaitForExitCallCount++;
                HasExited = true;
                return true;
            }

            public void Dispose()
            {
                DisposeCallCount++;
            }
        }

        private sealed class ThrowingLaunchedProcess : OpenCodeServerLauncher.ILaunchedProcess
        {
            public int ProcessId => 67890;
            public bool HasExited => false;
            public int KillCallCount { get; private set; }

            public Task<string> DrainStandardOutputAsync()
            {
                return Task.FromResult(string.Empty);
            }

            public Task<string> DrainStandardErrorAsync()
            {
                return Task.FromResult(string.Empty);
            }

            public void Kill(bool entireProcessTree)
            {
                KillCallCount++;
                throw new InvalidOperationException("kill failure");
            }

            public bool WaitForExit(int milliseconds)
            {
                throw new InvalidOperationException("waitforexit failure");
            }

            public void Dispose()
            {
                throw new InvalidOperationException("dispose failure");
            }
        }
    }
}
