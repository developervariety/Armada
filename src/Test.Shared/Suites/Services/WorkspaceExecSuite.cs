namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="WorkspaceService.ExecAsync"/>, the in-browser dock terminal backend.
    /// Positive cases assert that a portable command runs and its output is captured; negative cases
    /// assert rejection of empty commands and missing working directories.
    /// </summary>
    public sealed class WorkspaceExecSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.WorkspaceExec";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Workspace Exec suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("echo_captures_stdout", "Exec Echo CapturesStdout", TestTags.Positive, async () =>
            {
                string dir = CreateTempDir();
                try
                {
                    WorkspaceService service = new WorkspaceService();
                    Vessel vessel = new Vessel("Exec Vessel", "https://github.com/test/exec");
                    vessel.WorkingDirectory = dir;

                    WorkspaceExecResult result = await service.ExecAsync(vessel, new WorkspaceExecRequest { Command = "echo armada-exec-ok" });
                    AssertEqual(0, result.ExitCode);
                    AssertFalse(result.TimedOut, "Command should not time out");
                    AssertContains("armada-exec-ok", result.Stdout);
                    AssertEqual(dir, result.WorkingDirectory);
                }
                finally
                {
                    TryDelete(dir);
                }
            }));

            cases.Add(CaseAsync("empty_command_throws", "Exec EmptyCommand Throws", TestTags.Negative, async () =>
            {
                string dir = CreateTempDir();
                try
                {
                    WorkspaceService service = new WorkspaceService();
                    Vessel vessel = new Vessel("Exec Vessel", "https://github.com/test/exec");
                    vessel.WorkingDirectory = dir;

                    await AssertThrowsAsync<ArgumentException>(async () =>
                        await service.ExecAsync(vessel, new WorkspaceExecRequest { Command = "   " }));
                }
                finally
                {
                    TryDelete(dir);
                }
            }));

            cases.Add(CaseAsync("missing_working_directory_throws", "Exec MissingWorkingDirectory Throws", TestTags.Negative, async () =>
            {
                WorkspaceService service = new WorkspaceService();
                Vessel vessel = new Vessel("No Dir Vessel", "https://github.com/test/nodir");
                vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_nonexistent_" + Guid.NewGuid().ToString("N"));

                await AssertThrowsAsync<DirectoryNotFoundException>(async () =>
                    await service.ExecAsync(vessel, new WorkspaceExecRequest { Command = "echo hi" }));
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Workspace Exec",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static string CreateTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "armada_exec_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void TryDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
