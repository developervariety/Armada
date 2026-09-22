namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.IO;
    using System.Net.Http;
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
    /// Guards production wiring of the code-index service and the provider clients it is built with.
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
                    "EmbeddingClientFactory.CreateAsync(_Settings, _Database, _Logging, codeIndexHttpClient)",
                    contents,
                    "ArmadaServer should build the embedding client through EmbeddingClientFactory (endpoint-or-settings)");
                AssertContains(
                    "CodeIndexInferenceClientFactory.Create(_Settings, _Logging, codeIndexHttpClient)",
                    contents,
                    "ArmadaServer should select the inference client through the shared production factory");
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
                    "ArmadaServer should look for the source React dashboard build");
                AssertContains(
                    "src\", \"Armada.Dashboard\", \"dist",
                    contents,
                    "Source dashboard auto-detection should look for src/Armada.Dashboard/dist so direct /dashboard/code-index loads in repo runs");
                AssertTrue(
                    contents.IndexOf("TryFindSourceDashboardDist()", StringComparison.Ordinal) <
                    contents.IndexOf("no dashboard directory found", StringComparison.Ordinal),
                    "Source React dashboard detection must happen before the no-dashboard warning");
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
                    "EmbeddingClientFactory.CreateAsync(armadaSettings, database, logging, codeIndexHttpClient, cancellationToken)",
                    contents,
                    "McpStdioCommand should build the embedding client through EmbeddingClientFactory (endpoint-or-settings)");
                AssertContains(
                    "CodeIndexInferenceClientFactory.Create(armadaSettings, logging, codeIndexHttpClient)",
                    contents,
                    "McpStdioCommand should select the inference client through the shared production factory");
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

            await RunTest("Production inference-client factory selects the client for each configured mode", () =>
            {
                string dataRoot = NewTempDirectory("armada-code-index-prod-wire-");
                try
                {
                    using (HttpClient http = new HttpClient())
                    {
                        LoggingModule logging = SilentLogging();
                        ArmadaSettings settings = BuildMinimalSettings(dataRoot);

                        settings.CodeIndex.InferenceClient = "Http";
                        AssertTrue(CodeIndexInferenceClientFactory.Create(settings, logging, http) is DeepSeekInferenceClient,
                            "Http mode must select the HTTP inference client");

                        settings.CodeIndex.InferenceClient = "OpenCodeServer";
                        AssertTrue(CodeIndexInferenceClientFactory.Create(settings, logging, http) is OpenCodeServerInferenceClient,
                            "OpenCodeServer mode must select the OpenCode server client");

                        settings.CodeIndex.InferenceClient = "opencodeserver";
                        AssertTrue(CodeIndexInferenceClientFactory.Create(settings, logging, http) is OpenCodeServerInferenceClient,
                            "the mode is matched without regard to letter case");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }

                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Production inference-client factory falls back to the HTTP client for an unknown or empty mode", () =>
            {
                string dataRoot = NewTempDirectory("armada-code-index-prod-wire-invalid-");
                try
                {
                    using (HttpClient http = new HttpClient())
                    {
                        LoggingModule logging = SilentLogging();
                        ArmadaSettings settings = BuildMinimalSettings(dataRoot);

                        settings.CodeIndex.InferenceClient = "NoSuchClient";
                        AssertTrue(CodeIndexInferenceClientFactory.Create(settings, logging, http) is DeepSeekInferenceClient,
                            "an unknown mode must select the HTTP inference client");

                        settings.CodeIndex.InferenceClient = "";
                        AssertTrue(CodeIndexInferenceClientFactory.Create(settings, logging, http) is DeepSeekInferenceClient,
                            "an empty mode must select the HTTP inference client");
                        AssertFalse(CodeIndexInferenceClientFactory.IsOpenCodeServerMode(null), "a null mode is not OpenCodeServer");
                    }
                }
                finally
                {
                    TryDeleteDirectory(dataRoot);
                }

                return Task.CompletedTask;
            }).ConfigureAwait(false);
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
