namespace Test.Shared.Suites.Services
{
    using System;
    using System.IO;
    using System.Text;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Server;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>Behavior checks for OpenCode provider failures in captain chat.</summary>
    public sealed class OpenCodeChatFailureSuite : IArmadaTestSuite
    {
        /// <summary>Build the registered OpenCode chat failure cases.</summary>
        public TestSuiteDescriptor Build()
        {
            return new TestSuiteDescriptor("Services.OpenCodeChatFailure", "OpenCode chat failure", new System.Collections.Generic.List<TestCaseDescriptor>
            {
                Case("provider_error_fails_chat_even_after_partial_text", async () =>
                {
                    string errorLine = await ReadFixtureAsync().ConfigureAwait(false);
                    CaptainChatResponse response = await RunChatAsync("{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"partial answer\"}}\n" + errorLine).ConfigureAwait(false);
                    AssertFalse(response.Success, "A terminal provider error must fail chat after partial text.");
                    AssertContains("opencode error", response.Error ?? String.Empty);
                }),
                Case("provider_error_fails_empty_chat", async () =>
                {
                    string errorLine = await ReadFixtureAsync().ConfigureAwait(false);
                    CaptainChatResponse response = await RunChatAsync(errorLine).ConfigureAwait(false);
                    AssertFalse(response.Success, "An error-only stream must fail chat.");
                    AssertContains("Local fixture provider rejected the request", response.Error ?? String.Empty);
                }),
                Case("successful_text_remains_chat_success", async () =>
                {
                    CaptainChatResponse response = await RunChatAsync("{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"complete answer\"}}\n").ConfigureAwait(false);
                    AssertTrue(response.Success, "A normal text event must remain successful.");
                    AssertEqual("complete answer", response.Reply);
                })
            });
        }

        private static TestCaseDescriptor Case(string id, Func<Task> body)
        {
            return new TestCaseDescriptor("Services.OpenCodeChatFailure", id, id, _ => body(), new System.Collections.Generic.List<string> { TestTags.Positive });
        }

        private static async Task<CaptainChatResponse> RunChatAsync(string output)
        {
            await OpenCodeTestEnvironmentGate.Instance.WaitAsync().ConfigureAwait(false);
            string root = Path.Combine(Path.GetTempPath(), "armada-opencode-chat-" + Guid.NewGuid().ToString("N"));
            string? prior = null;
            try
            {
                string script = Path.Combine(root, OperatingSystem.IsWindows() ? "opencode.cmd" : "opencode");
                Directory.CreateDirectory(root);
                string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(output));
                string contents = OperatingSystem.IsWindows()
                    ? "@echo off\npowershell -NoProfile -Command \"[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + encoded + "'))\"\n"
                    : "#!/bin/sh\nprintf '%s' '" + encoded + "' | base64 -d\n";
                await File.WriteAllTextAsync(script, contents).ConfigureAwait(false);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                prior = Environment.GetEnvironmentVariable("ARMADA_TEST_OPENCODE");
                Environment.SetEnvironmentVariable("ARMADA_TEST_OPENCODE", script);
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("OpenCode chat fixture", AgentRuntimeEnum.OpenCode)).ConfigureAwait(false);
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    ArmadaSettings settings = new ArmadaSettings
                    {
                        DataDirectory = root,
                        DatabasePath = Path.Combine(root, "armada.db"),
                        LogDirectory = Path.Combine(root, "logs"),
                        DocksDirectory = Path.Combine(root, "docks"),
                        ReposDirectory = Path.Combine(root, "repos")
                    };
                    settings.InitializeDirectories();
                    CaptainChatService service = new CaptainChatService(testDb.Driver, new AgentRuntimeFactory(logging), null, null, logging, settings);
                    return await service.ChatAsync(captain.Id, new CaptainChatRequest { Message = "fixture request" }).ConfigureAwait(false);
                }
            }
            finally
            {
                try
                {
                    if (prior != null) Environment.SetEnvironmentVariable("ARMADA_TEST_OPENCODE", prior);
                    else Environment.SetEnvironmentVariable("ARMADA_TEST_OPENCODE", null);
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
                finally
                {
                    OpenCodeTestEnvironmentGate.Instance.Release();
                }
            }
        }

        private static async Task<string> ReadFixtureAsync()
        {
            return (await File.ReadAllTextAsync(Path.Combine(FindRepositoryRoot(), "docs", "upstream-review", "fixtures", "opencode-api-error.jsonl")).ConfigureAwait(false)).Trim();
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "README.md")) && Directory.Exists(Path.Combine(current.FullName, "src"))) return current.FullName;
                current = current.Parent;
            }
            throw new DirectoryNotFoundException("Could not find the Armada repository root.");
        }
    }
}
