namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.IO;
    using System.Net.Http;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Guards production wiring of semantic-search HTTP clients into <see cref="CodeIndexService"/>.
    /// </summary>
    public class CodeIndexProductionWiringTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Code Index Production Wiring";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("ArmadaServer wires inference-client switch into CodeIndexService", () =>
            {
                string path = Path.Combine(FindRepositoryRoot(), "src", "Armada.Server", "ArmadaServer.cs");
                string contents = File.ReadAllText(path);
                AssertEqual(
                    1,
                    CountOccurrences(contents, "new CodeIndexService("),
                    "ArmadaServer should have exactly one CodeIndexService construction site");
                AssertFalse(
                    contents.Contains("new CodeIndexService(_Logging, _Database, _Settings, _Git)"),
                    "ArmadaServer should not use the legacy CodeIndexService constructor without semantic clients");
                AssertContains(
                    "new DeepSeekEmbeddingClient(_Settings.CodeIndex, _Logging, codeIndexHttpClient)",
                    contents,
                    "ArmadaServer should construct DeepSeekEmbeddingClient with CodeIndex settings");
                AssertContains(
                    "string.Equals(_Settings.CodeIndex.InferenceClient, \"OpenCodeServer\", StringComparison.OrdinalIgnoreCase)",
                    contents,
                    "ArmadaServer should switch inference client based on CodeIndex.InferenceClient");
                AssertContains(
                    "new OpenCodeServerInferenceClient(_Settings, _Logging, codeIndexHttpClient)",
                    contents,
                    "ArmadaServer should construct OpenCodeServerInferenceClient for OpenCodeServer mode");
                AssertContains(
                    "new DeepSeekInferenceClient(_Settings.CodeIndex, _Logging, codeIndexHttpClient)",
                    contents,
                    "ArmadaServer should keep DeepSeekInferenceClient for Http mode");
                AssertContains(
                    "new CodeIndexService(_Logging, _Database, _Settings, _Git, embeddingClient, inferenceClient)",
                    contents,
                    "ArmadaServer should pass non-null embedding and inference clients into CodeIndexService");
                AssertContains(
                    "new MergeQueueService(_Logging, _Database, _Settings, _Git, mergeFailureClassifier, prServiceFactory, _CodeIndex)",
                    contents,
                    "ArmadaServer should pass CodeIndexService into MergeQueueService for post-land refreshes");
                AssertContains(
                    "ScheduleStartupBaselineCacheWarmup(_CodeIndex)",
                    contents,
                    "ArmadaServer should schedule best-effort baseline cache warm-up after CodeIndexService construction");
                AssertContains(
                    "WarmBaselineCacheAsync(vessel.Id",
                    contents,
                    "Startup warm-up should call WarmBaselineCacheAsync for indexed vessels");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("ArmadaServer source runs can auto-detect the React dashboard build", () =>
            {
                string path = Path.Combine(FindRepositoryRoot(), "src", "Armada.Server", "ArmadaServer.cs");
                string contents = File.ReadAllText(path);
                AssertContains(
                    "TryFindSourceDashboardDist()",
                    contents,
                    "ArmadaServer should fall back to the source React dashboard build before using the embedded legacy dashboard");
                AssertContains(
                    "src\", \"Armada.Dashboard\", \"dist",
                    contents,
                    "Source dashboard auto-detection should look for src/Armada.Dashboard/dist so direct /dashboard/code-index loads in repo runs");
                AssertTrue(
                    contents.IndexOf("TryFindSourceDashboardDist()", StringComparison.Ordinal) <
                    contents.IndexOf("using embedded legacy dashboard", StringComparison.Ordinal),
                    "Source React dashboard detection must happen before the legacy embedded dashboard fallback");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("ArmadaServer warns on silent code-index feature disables", () =>
            {
                CodeIndexSettings settings = new CodeIndexSettings
                {
                    EmbeddingApiKey = "configured",
                    SummarizerApiKey = "configured",
                    UseSemanticSearch = false,
                    UseSummarizer = false
                };

                IReadOnlyList<string> warnings = ArmadaServer.BuildCodeIndexConfigurationWarnings(settings);

                AssertEqual(2, warnings.Count);
                AssertTrue(warnings.Any(w => w.Contains("EmbeddingApiKey is configured but UseSemanticSearch=false", StringComparison.Ordinal)));
                AssertTrue(warnings.Any(w => w.Contains("SummarizerApiKey is configured but UseSummarizer=false", StringComparison.Ordinal)));
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("ArmadaServer emits no code-index warnings for consistent settings", () =>
            {
                CodeIndexSettings allOff = new CodeIndexSettings
                {
                    EmbeddingApiKey = string.Empty,
                    SummarizerApiKey = string.Empty,
                    UseSemanticSearch = false,
                    UseSummarizer = false
                };
                AssertEqual(0, ArmadaServer.BuildCodeIndexConfigurationWarnings(allOff).Count);

                CodeIndexSettings allOn = new CodeIndexSettings
                {
                    EmbeddingApiKey = "configured",
                    SummarizerApiKey = "configured",
                    UseSemanticSearch = true,
                    UseSummarizer = true
                };
                AssertEqual(0, ArmadaServer.BuildCodeIndexConfigurationWarnings(allOn).Count);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("McpStdioCommand wires inference-client switch into CodeIndexService", () =>
            {
                string path = Path.Combine(FindRepositoryRoot(), "src", "Armada.Helm", "Commands", "McpStdioCommand.cs");
                string contents = File.ReadAllText(path);
                AssertEqual(
                    1,
                    CountOccurrences(contents, "new CodeIndexService("),
                    "McpStdioCommand should have exactly one CodeIndexService construction site");
                AssertFalse(
                    contents.Contains("new CodeIndexService(logging, database, armadaSettings, git)"),
                    "McpStdioCommand should not use the legacy CodeIndexService constructor without semantic clients");
                AssertContains(
                    "new DeepSeekEmbeddingClient(armadaSettings.CodeIndex, logging, codeIndexHttpClient)",
                    contents,
                    "McpStdioCommand should construct DeepSeekEmbeddingClient with CodeIndex settings");
                AssertContains(
                    "string.Equals(armadaSettings.CodeIndex.InferenceClient, \"OpenCodeServer\", StringComparison.OrdinalIgnoreCase)",
                    contents,
                    "McpStdioCommand should switch inference client based on CodeIndex.InferenceClient");
                AssertContains(
                    "new OpenCodeServerInferenceClient(armadaSettings, logging, codeIndexHttpClient)",
                    contents,
                    "McpStdioCommand should construct OpenCodeServerInferenceClient for OpenCodeServer mode");
                AssertContains(
                    "new DeepSeekInferenceClient(armadaSettings.CodeIndex, logging, codeIndexHttpClient)",
                    contents,
                    "McpStdioCommand should keep DeepSeekInferenceClient for Http mode");
                AssertContains(
                    "new CodeIndexService(logging, database, armadaSettings, git, embeddingClient, inferenceClient)",
                    contents,
                    "McpStdioCommand should pass non-null embedding and inference clients into CodeIndexService");
                AssertContains(
                    "new MergeQueueService(logging, database, armadaSettings, git, mergeFailureClassifier, codeIndexService: codeIndexService)",
                    contents,
                    "McpStdioCommand should pass CodeIndexService into MergeQueueService for post-land refreshes");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Mirrored production CodeIndexService ctor defaults to DeepSeek inference for Http mode", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-index-prod-wire-");
                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    using (HttpClient http = new HttpClient())
                    {
                        LoggingModule logging = SilentLogging();
                        ArmadaSettings settings = BuildMinimalSettings(dataRoot);
                        IEmbeddingClient embeddingClient = new DeepSeekEmbeddingClient(settings.CodeIndex, logging, http);
                        IInferenceClient inferenceClient = new DeepSeekInferenceClient(settings.CodeIndex, logging, http);
                        CodeIndexService service = new CodeIndexService(
                            logging,
                            testDb.Driver,
                            settings,
                            new GitService(logging),
                            embeddingClient,
                            inferenceClient);

                        FieldInfo? embField = typeof(CodeIndexService).GetField(
                            "_EmbeddingClient",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        FieldInfo? infField = typeof(CodeIndexService).GetField(
                            "_InferenceClient",
                            BindingFlags.Instance | BindingFlags.NonPublic);

                        AssertTrue(embField != null, "_EmbeddingClient field should exist");
                        AssertTrue(infField != null, "_InferenceClient field should exist");

                        object? embValue = embField!.GetValue(service);
                        object? infValue = infField!.GetValue(service);

                        AssertTrue(embValue is DeepSeekEmbeddingClient, "Embedding field should hold DeepSeekEmbeddingClient");
                        AssertTrue(infValue is DeepSeekInferenceClient, "Inference field should hold DeepSeekInferenceClient");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            }).ConfigureAwait(false);

            await RunTest("Mirrored production CodeIndexService ctor holds OpenCode inference for OpenCodeServer mode", async () =>
            {
                string dataRoot = NewTempDirectory("armada-code-index-prod-wire-opencode-");
                try
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    using (HttpClient http = new HttpClient())
                    {
                        LoggingModule logging = SilentLogging();
                        ArmadaSettings settings = BuildMinimalSettings(dataRoot);
                        settings.CodeIndex.InferenceClient = "OpenCodeServer";
                        IEmbeddingClient embeddingClient = new DeepSeekEmbeddingClient(settings.CodeIndex, logging, http);
                        IInferenceClient inferenceClient = new OpenCodeServerInferenceClient(settings, logging, http);
                        CodeIndexService service = new CodeIndexService(
                            logging,
                            testDb.Driver,
                            settings,
                            new GitService(logging),
                            embeddingClient,
                            inferenceClient);

                        FieldInfo? infField = typeof(CodeIndexService).GetField(
                            "_InferenceClient",
                            BindingFlags.Instance | BindingFlags.NonPublic);

                        AssertTrue(infField != null, "_InferenceClient field should exist");
                        object? infValue = infField!.GetValue(service);
                        AssertTrue(infValue is OpenCodeServerInferenceClient, "Inference field should hold OpenCodeServerInferenceClient when selected.");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }
            }).ConfigureAwait(false);

            await RunTest("CodeIndexRefreshScheduler invokes WarmBaselineCacheAsync after UpdateAsync", async () =>
            {
                WarmTrackingCodeIndexDouble codeIndex = new WarmTrackingCodeIndexDouble();
                LoggingModule logging = SilentLogging();
                CodeIndexSettings settings = new CodeIndexSettings
                {
                    PostLandRefreshDebounceSeconds = 0
                };

                CodeIndexRefreshScheduler.Schedule(
                    codeIndex,
                    settings,
                    logging,
                    "[test] ",
                    "vsl_test",
                    "test reason");

                // Allow the background worker time to complete.
                int waited = 0;
                while (!codeIndex.WarmWasCalled && waited < 5000)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                    waited += 50;
                }

                AssertTrue(codeIndex.UpdateWasCalled, "UpdateAsync should be called by the refresh scheduler");
                AssertTrue(codeIndex.WarmWasCalled, "WarmBaselineCacheAsync should be called after UpdateAsync");
            }).ConfigureAwait(false);
        }

        private sealed class WarmTrackingCodeIndexDouble : ICodeIndexService
        {
            private readonly object _Gate = new object();
            private bool _UpdateCalled;
            private bool _WarmCalled;

            public bool UpdateWasCalled { get { lock (_Gate) { return _UpdateCalled; } } }
            public bool WarmWasCalled { get { lock (_Gate) { return _WarmCalled; } } }

            public Task<CodeIndexStatus> GetStatusAsync(string vesselId, CancellationToken token = default)
                => Task.FromResult(new CodeIndexStatus { VesselId = vesselId ?? "" });

            public Task<CodeIndexStatus> UpdateAsync(string vesselId, CancellationToken token = default)
            {
                lock (_Gate) { _UpdateCalled = true; }
                return Task.FromResult(new CodeIndexStatus { VesselId = vesselId ?? "" });
            }

            public Task WarmBaselineCacheAsync(string vesselId, CancellationToken token = default)
            {
                lock (_Gate) { _WarmCalled = true; }
                return Task.CompletedTask;
            }

            public Task<ContextPackResponse?> TryGetCachedContextPackAsync(ContextPackRequest request, CancellationToken token = default)
                => Task.FromResult<ContextPackResponse?>(null);

            public Task<CodeSearchResponse> SearchAsync(CodeSearchRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeSearchResponse());

            public Task<FleetCodeSearchResponse> SearchFleetAsync(FleetCodeSearchRequest request, CancellationToken token = default)
                => Task.FromResult(new FleetCodeSearchResponse());

            public Task<ContextPackResponse> BuildContextPackAsync(ContextPackRequest request, CancellationToken token = default)
                => Task.FromResult(new ContextPackResponse());

            public Task<FleetContextPackResponse> BuildFleetContextPackAsync(FleetContextPackRequest request, CancellationToken token = default)
                => Task.FromResult(new FleetContextPackResponse());

            public Task<CodeGraphSymbolSearchResponse> SearchSymbolsAsync(CodeGraphSymbolSearchRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeGraphSymbolSearchResponse());

            public Task<CodeGraphNeighborsResponse> GetCallersAsync(CodeGraphNeighborsRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeGraphNeighborsResponse());

            public Task<CodeGraphNeighborsResponse> GetCalleesAsync(CodeGraphNeighborsRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeGraphNeighborsResponse());

            public Task<CodeGraphImpactResponse> GetImpactAsync(CodeGraphImpactRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeGraphImpactResponse());

            public Task<CodeGraphAffectedTestsResponse> SuggestAffectedTestsAsync(CodeGraphAffectedTestsRequest request, CancellationToken token = default)
                => Task.FromResult(new CodeGraphAffectedTestsResponse());
        }

        private static ArmadaSettings BuildMinimalSettings(string dataRoot)
        {
            CodeIndexSettings codeIndex = new CodeIndexSettings
            {
                IndexDirectory = Path.Combine(dataRoot, "code-index"),
                MaxChunkLines = 20,
                MaxSearchResults = 10,
                MaxContextPackResults = 8,
                UseSemanticSearch = false
            };

            ArmadaSettings settings = new ArmadaSettings
            {
                DataDirectory = Path.Combine(dataRoot, "data"),
                ReposDirectory = Path.Combine(dataRoot, "repos"),
                CodeIndex = codeIndex
            };
            settings.InitializeDirectories();
            return settings;
        }

        private static LoggingModule SilentLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static string NewTempDirectory(string prefix)
        {
            string path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
            }
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "src"))
                    && Directory.Exists(Path.Combine(current.FullName, "test")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
        }

        private static int CountOccurrences(string contents, string value)
        {
            int count = 0;
            int index = 0;
            while (index < contents.Length)
            {
                int found = contents.IndexOf(value, index, StringComparison.Ordinal);
                if (found < 0)
                {
                    return count;
                }

                count++;
                index = found + value.Length;
            }

            return count;
        }
    }
}
