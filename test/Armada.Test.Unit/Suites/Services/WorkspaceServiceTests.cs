namespace Armada.Test.Unit.Suites.Services
{
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
