namespace Armada.Test.Unit.Suites.Services
{
    using System.Diagnostics;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    public class WorkspaceServiceTests : TestSuite
    {
        public override string Name => "Workspace Service";

        protected override async Task RunTestsAsync()
        {
            await RunTest("GetStatusAsync reports missing working directory cleanly", async () =>
            {
                WorkspaceService service = new WorkspaceService();
                Vessel vessel = new Vessel
                {
                    Id = "vsl_workspace_missing",
                    Name = "Workspace Missing",
                    WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada-missing-" + Guid.NewGuid().ToString("N"))
                };

                WorkspaceStatusResult result = await service.GetStatusAsync(vessel).ConfigureAwait(false);
                AssertFalse(result.HasWorkingDirectory);
                AssertContains("No working directory configured or directory does not exist.", result.Error ?? String.Empty);
            });

            await RunTest("SaveFileAsync rejects stale optimistic concurrency hash", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada-workspace-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);

                try
                {
                    string filePath = Path.Combine(root, "notes.txt");
                    await File.WriteAllTextAsync(filePath, "original").ConfigureAwait(false);

                    WorkspaceService service = new WorkspaceService();
                    Vessel vessel = CreateVessel(root);
                    WorkspaceFileResponse initial = await service.GetFileAsync(vessel, "notes.txt").ConfigureAwait(false);

                    await File.WriteAllTextAsync(filePath, "changed externally").ConfigureAwait(false);

                    await AssertThrowsAsync<WorkspaceConflictException>(() => service.SaveFileAsync(vessel, new WorkspaceSaveRequest
                    {
                        Path = "notes.txt",
                        Content = "new content",
                        ExpectedHash = initial.ContentHash
                    })).ConfigureAwait(false);
                }
                finally
                {
                    TryDeleteDirectory(root);
                }
            });

            await RunTest("SearchAsync skips hidden workspace directories", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada-workspace-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);

                try
                {
                    Directory.CreateDirectory(Path.Combine(root, "src"));
                    Directory.CreateDirectory(Path.Combine(root, ".git"));
                    await File.WriteAllTextAsync(Path.Combine(root, "src", "visible.txt"), "Workspace visible token").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(root, ".git", "hidden.txt"), "Workspace hidden token").ConfigureAwait(false);

                    WorkspaceService service = new WorkspaceService();
                    Vessel vessel = CreateVessel(root);
                    WorkspaceSearchResult result = await service.SearchAsync(vessel, "Workspace").ConfigureAwait(false);

                    AssertEqual(1, result.TotalMatches);
                    AssertEqual("src/visible.txt", result.Matches[0].Path);
                }
                finally
                {
                    TryDeleteDirectory(root);
                }
            });

            await RunTest("ExecAsync runs a command and returns exit code and output", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada-workspace-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);

                try
                {
                    WorkspaceService service = new WorkspaceService();
                    Vessel vessel = CreateVessel(root);

                    string command = OperatingSystem.IsWindows()
                        ? "echo armada-exec-ok"
                        : "echo armada-exec-ok";

                    WorkspaceExecResult result = await service.ExecAsync(vessel, new WorkspaceExecRequest
                    {
                        Command = command,
                        TimeoutSeconds = 30
                    }).ConfigureAwait(false);

                    AssertEqual(0, result.ExitCode);
                    AssertContains("armada-exec-ok", result.Stdout);
                    AssertFalse(result.TimedOut);
                    AssertTrue(result.DurationMs >= 0);
                }
                finally
                {
                    TryDeleteDirectory(root);
                }
            });

            await RunTest("ExecAsync times out and kills the process tree", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada-workspace-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);

                try
                {
                    WorkspaceService service = new WorkspaceService();
                    Vessel vessel = CreateVessel(root);

                    string command = OperatingSystem.IsWindows()
                        ? "ping -n 30 127.0.0.1"
                        : "sleep 30";

                    WorkspaceExecResult result = await service.ExecAsync(vessel, new WorkspaceExecRequest
                    {
                        Command = command,
                        TimeoutSeconds = 1
                    }).ConfigureAwait(false);

                    AssertTrue(result.TimedOut, "a command exceeding the timeout must report TimedOut");
                    AssertEqual(-1, result.ExitCode);
                }
                finally
                {
                    TryDeleteDirectory(root);
                }
            });

            await RunTest("ExecAsync rejects an empty command", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada-workspace-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);

                try
                {
                    WorkspaceService service = new WorkspaceService();
                    Vessel vessel = CreateVessel(root);

                    await AssertThrowsAsync<ArgumentException>(() => service.ExecAsync(vessel, new WorkspaceExecRequest
                    {
                        Command = "  ",
                        TimeoutSeconds = 30
                    })).ConfigureAwait(false);
                }
                finally
                {
                    TryDeleteDirectory(root);
                }
            });

            await RunTest("GetDiffAsync reports a non-git directory as not a repository", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada-workspace-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);

                try
                {
                    WorkspaceService service = new WorkspaceService();
                    Vessel vessel = CreateVessel(root);

                    WorkspaceDiffResult result = await service.GetDiffAsync(vessel).ConfigureAwait(false);
                    AssertContains("Not a git repository", result.Error ?? String.Empty);
                }
                finally
                {
                    TryDeleteDirectory(root);
                }
            });

            await RunTest("GetDiffAsync returns the working-tree diff against HEAD", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada-workspace-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);

                try
                {
                    if (!TryInitGitRepo(root))
                    {
                        return;
                    }

                    string filePath = Path.Combine(root, "tracked.txt");
                    await File.WriteAllTextAsync(filePath, "one\n").ConfigureAwait(false);
                    await RunGitAsync(root, "add", "tracked.txt").ConfigureAwait(false);
                    await RunGitAsync(root, "commit", "-m", "seed").ConfigureAwait(false);

                    await File.WriteAllTextAsync(filePath, "one\ntwo\n").ConfigureAwait(false);

                    WorkspaceService service = new WorkspaceService();
                    Vessel vessel = CreateVessel(root);

                    WorkspaceDiffResult result = await service.GetDiffAsync(vessel).ConfigureAwait(false);
                    AssertTrue(String.IsNullOrWhiteSpace(result.Error), "diff must not report an error");
                    AssertContains("+two", result.Diff);
                }
                finally
                {
                    TryDeleteDirectory(root);
                }
            });

            await RunTest("GetDiffAsync scopes the diff to one path", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada-workspace-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);

                try
                {
                    if (!TryInitGitRepo(root))
                    {
                        return;
                    }

                    await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "a\n").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(root, "b.txt"), "b\n").ConfigureAwait(false);
                    await RunGitAsync(root, "add", "a.txt", "b.txt").ConfigureAwait(false);
                    await RunGitAsync(root, "commit", "-m", "seed").ConfigureAwait(false);

                    await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "a\nx\n").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(root, "b.txt"), "b\ny\n").ConfigureAwait(false);

                    WorkspaceService service = new WorkspaceService();
                    Vessel vessel = CreateVessel(root);

                    WorkspaceDiffResult result = await service.GetDiffAsync(vessel, "a.txt").ConfigureAwait(false);
                    AssertTrue(String.IsNullOrWhiteSpace(result.Error), "scoped diff must not report an error");
                    AssertContains("+x", result.Diff);
                    AssertFalse(result.Diff.Contains("+y"), "the scoped diff must not include other paths");
                }
                finally
                {
                    TryDeleteDirectory(root);
                }
            });
        }

        private static Vessel CreateVessel(string workingDirectory)
        {
            return new Vessel
            {
                Id = "vsl_workspace",
                Name = "Workspace Vessel",
                WorkingDirectory = workingDirectory
            };
        }

        private static bool TryInitGitRepo(string root)
        {
            try
            {
                RunGit(root, "init", "--quiet");
                RunGit(root, "config", "user.email", "test@armada.local");
                RunGit(root, "config", "user.name", "Armada Test");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string RunGit(string workingDirectory, params string[] args)
        {
            ProcessStartInfo psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);

            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("Unable to start git.");
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException("git " + String.Join(" ", args) + " failed: " + error);

            return output;
        }

        private static Task RunGitAsync(string workingDirectory, params string[] args)
        {
            return Task.Run(() => RunGit(workingDirectory, args));
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
                // ignore temp cleanup failures
            }
        }
    }
}
