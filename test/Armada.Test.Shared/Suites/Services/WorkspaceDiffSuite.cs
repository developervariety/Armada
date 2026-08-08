namespace Armada.Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Armada.Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="WorkspaceService.GetDiffAsync"/>, the in-app review/diff backend.
    /// The positive case initializes a real git repository, commits a file, modifies it, and asserts the
    /// unified diff reflects the change; negative cases assert graceful handling of a non-git directory
    /// and a missing working directory.
    /// </summary>
    public sealed class WorkspaceDiffSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.WorkspaceDiff";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Workspace Diff suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("diff_reflects_modification", "Diff ReflectsModification", TestTags.Positive, async () =>
            {
                string dir = CreateTempDir();
                try
                {
                    Git(dir, "init");
                    Git(dir, "config", "user.email", "test@armada.local");
                    Git(dir, "config", "user.name", "Armada Test");
                    string file = Path.Combine(dir, "hello.txt");
                    await File.WriteAllTextAsync(file, "original line\n");
                    Git(dir, "add", "hello.txt");
                    Git(dir, "commit", "-m", "initial");
                    await File.WriteAllTextAsync(file, "changed line\n");

                    WorkspaceService service = new WorkspaceService();
                    Vessel vessel = new Vessel("Diff Vessel", "https://github.com/test/diff");
                    vessel.WorkingDirectory = dir;

                    WorkspaceDiffResult result = await service.GetDiffAsync(vessel);
                    AssertNull(result.Error);
                    AssertContains("changed line", result.Diff);
                    AssertContains("original line", result.Diff);
                }
                finally
                {
                    TryDelete(dir);
                }
            }));

            cases.Add(CaseAsync("diff_non_git_directory_returns_error", "Diff NonGitDirectory ReturnsError", TestTags.Negative, async () =>
            {
                string dir = CreateTempDir();
                try
                {
                    WorkspaceService service = new WorkspaceService();
                    Vessel vessel = new Vessel("No Git Vessel", "https://github.com/test/nogit");
                    vessel.WorkingDirectory = dir;

                    WorkspaceDiffResult result = await service.GetDiffAsync(vessel);
                    AssertNotNull(result.Error);
                }
                finally
                {
                    TryDelete(dir);
                }
            }));

            cases.Add(CaseAsync("diff_missing_working_directory_throws", "Diff MissingWorkingDirectory Throws", TestTags.Negative, async () =>
            {
                WorkspaceService service = new WorkspaceService();
                Vessel vessel = new Vessel("No Dir Vessel", "https://github.com/test/nodir");
                vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_nonexistent_" + Guid.NewGuid().ToString("N"));

                await AssertThrowsAsync<DirectoryNotFoundException>(async () =>
                    await service.GetDiffAsync(vessel));
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Workspace Diff",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static void Git(string dir, params string[] args)
        {
            ProcessStartInfo psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            foreach (string arg in args) psi.ArgumentList.Add(arg);
            using Process process = Process.Start(psi)!;
            // Drain both pipes to avoid a deadlock when git writes more than the buffer holds.
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
            Task.WaitAll(new Task[] { stdout, stderr }, 5000);
        }

        private static string CreateTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "armada_diff_" + Guid.NewGuid().ToString("N"));
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
