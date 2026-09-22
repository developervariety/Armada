namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for <see cref="CodeIndexRefreshScheduler"/>: debounced, coalesced automatic refresh, the
    /// enablement gate, the warm-up after an update, and the enrollment rule that an automatic
    /// trigger never indexes a vessel for the first time.
    /// </summary>
    public class CodeIndexRefreshSchedulerTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Code Index Refresh Scheduler";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Rapid requests for one vessel coalesce into one update", async () =>
            {
                RecordingCodeIndexService recordingIndex = new RecordingCodeIndexService();
                CodeIndexSettings settings = new CodeIndexSettings { PostLandRefreshDebounceSeconds = 1 };
                string vesselId = "vsl_debounce_" + Guid.NewGuid().ToString("N");

                CodeIndexRefreshScheduler.Schedule(recordingIndex, settings, SilentLogging(), "[test] ", vesselId, "first landing");
                CodeIndexRefreshScheduler.Schedule(recordingIndex, settings, SilentLogging(), "[test] ", vesselId, "second landing");
                CodeIndexRefreshScheduler.Schedule(recordingIndex, settings, SilentLogging(), "[test] ", vesselId, "third landing");

                await WaitForIdleAsync(vesselId).ConfigureAwait(false);
                AssertEqual(1, recordingIndex.UpdateAsyncVesselIds.Count(id => id == vesselId),
                    "rapid refresh requests for one vessel should be coalesced into one update");
            }).ConfigureAwait(false);

            await RunTest("Disabled code indexing schedules no refresh", async () =>
            {
                RecordingCodeIndexService recordingIndex = new RecordingCodeIndexService();
                CodeIndexSettings settings = new CodeIndexSettings { Enabled = false };
                string vesselId = "vsl_disabled_" + Guid.NewGuid().ToString("N");

                CodeIndexRefreshScheduler.Schedule(recordingIndex, settings, SilentLogging(), "[test] ", vesselId, "disabled landing");

                AssertFalse(CodeIndexRefreshScheduler.IsPending(vesselId), "disabled code indexing must not queue a refresh");
                await Task.Delay(100).ConfigureAwait(false);
                AssertEqual(0, recordingIndex.UpdateAsyncVesselIds.Count,
                    "disabled code indexing must not schedule post-land refresh work");
            }).ConfigureAwait(false);

            await RunTest("An automatic refresh warms the baseline cache after the update", async () =>
            {
                RecordingCodeIndexService recordingIndex = new RecordingCodeIndexService();
                CodeIndexSettings settings = new CodeIndexSettings { PostLandRefreshDebounceSeconds = 0 };
                string vesselId = "vsl_warm_" + Guid.NewGuid().ToString("N");

                CodeIndexRefreshScheduler.Schedule(recordingIndex, settings, SilentLogging(), "[test] ", vesselId, "test reason");

                await WaitForIdleAsync(vesselId).ConfigureAwait(false);
                AssertTrue(recordingIndex.HasUpdateForVessel(vesselId), "UpdateAsync should be called by the refresh scheduler");
                AssertTrue(recordingIndex.HasWarmForVessel(vesselId), "WarmBaselineCacheAsync should be called after UpdateAsync");
            }).ConfigureAwait(false);

            await RunTest("An automatic refresh skips a vessel the index service reports as never indexed", async () =>
            {
                RecordingCodeIndexService recordingIndex = new RecordingCodeIndexService { Indexed = false };
                CodeIndexSettings settings = new CodeIndexSettings { PostLandRefreshDebounceSeconds = 0 };
                string vesselId = "vsl_unenrolled_" + Guid.NewGuid().ToString("N");

                CodeIndexRefreshScheduler.Schedule(recordingIndex, settings, SilentLogging(), "[test] ", vesselId, "landing");

                await WaitForIdleAsync(vesselId).ConfigureAwait(false);
                AssertTrue(recordingIndex.IsIndexedVesselIds.Contains(vesselId), "the scheduler must check enrollment");
                AssertFalse(recordingIndex.HasUpdateForVessel(vesselId), "a never-indexed vessel must not be updated automatically");
                AssertFalse(recordingIndex.HasWarmForVessel(vesselId), "a never-indexed vessel must not be warmed automatically");
            }).ConfigureAwait(false);

            await RunTest("Post-landing refresh never first-indexes a vessel and refreshes an indexed one", async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "armada-index-scheduler-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try
                {
                    string newRepo = await CreateRepositoryAsync(Path.Combine(root, "new-repo")).ConfigureAwait(false);
                    string indexedRepo = await CreateRepositoryAsync(Path.Combine(root, "indexed-repo")).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = SilentLogging();
                        ArmadaSettings settings = new ArmadaSettings
                        {
                            DataDirectory = Path.Combine(root, "data"),
                            ReposDirectory = Path.Combine(root, "repos"),
                            CodeIndex = new CodeIndexSettings
                            {
                                IndexDirectory = Path.Combine(root, "code-index"),
                                UseSemanticSearch = false,
                                PostLandRefreshDebounceSeconds = 0
                            }
                        };
                        settings.InitializeDirectories();
                        CodeIndexService service = new CodeIndexService(logging, testDb.Driver, settings, new GitService(logging));

                        Vessel newVessel = await CreateVesselAsync(testDb, newRepo).ConfigureAwait(false);
                        Vessel indexedVessel = await CreateVesselAsync(testDb, indexedRepo).ConfigureAwait(false);
                        CodeIndexStatus explicitStatus = await service.UpdateAsync(indexedVessel.Id).ConfigureAwait(false);

                        await File.WriteAllTextAsync(Path.Combine(indexedRepo, "src", "Landed.cs"), "namespace Sample { public class Landed { } }\n").ConfigureAwait(false);
                        await RunGitAsync(indexedRepo, "add", ".").ConfigureAwait(false);
                        await RunGitAsync(indexedRepo, "commit", "-m", "Landed change").ConfigureAwait(false);
                        string landedCommit = (await RunGitAsync(indexedRepo, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
                        AssertNotEqual(explicitStatus.IndexedCommitSha, landedCommit, "the landing must move the default branch");

                        AssertFalse(await service.IsIndexedAsync(newVessel.Id).ConfigureAwait(false), "a vessel never updated is not enrolled");
                        AssertTrue(await service.IsIndexedAsync(indexedVessel.Id).ConfigureAwait(false), "an explicitly updated vessel is enrolled");

                        CodeIndexRefreshScheduler.Schedule(service, settings.CodeIndex, logging, "[test] ", newVessel.Id, "mission landed");
                        CodeIndexRefreshScheduler.Schedule(service, settings.CodeIndex, logging, "[test] ", indexedVessel.Id, "mission landed");
                        await WaitForIdleAsync(newVessel.Id).ConfigureAwait(false);
                        await WaitForIdleAsync(indexedVessel.Id).ConfigureAwait(false);

                        CodeIndexStatus newStatus = await service.GetStatusAsync(newVessel.Id).ConfigureAwait(false);
                        AssertEqual("Missing", newStatus.Freshness, "post-landing refresh must not create a first index");
                        AssertTrue(String.IsNullOrEmpty(newStatus.IndexedCommitSha), "a never-indexed vessel must stay unindexed");
                        AssertFalse(File.Exists(Path.Combine(settings.CodeIndex.IndexDirectory, newVessel.Id, "chunks.jsonl")),
                            "no chunks may be written for a never-indexed vessel");

                        CodeIndexStatus refreshed = await service.GetStatusAsync(indexedVessel.Id).ConfigureAwait(false);
                        AssertEqual(landedCommit, refreshed.IndexedCommitSha, "post-landing refresh must bring an indexed vessel to the landed commit");
                    }
                }
                finally
                {
                    TryDeleteDirectory(root);
                }
            }).ConfigureAwait(false);
        }

        private static async Task WaitForIdleAsync(string vesselId)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (CodeIndexRefreshScheduler.IsPending(vesselId) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25).ConfigureAwait(false);
            }

            if (CodeIndexRefreshScheduler.IsPending(vesselId))
                throw new TimeoutException("the refresh for vessel " + vesselId + " did not finish");
        }

        private static async Task<Vessel> CreateVesselAsync(TestDatabase testDb, string repositoryPath)
        {
            Vessel vessel = new Vessel
            {
                Name = "index-scheduler-vessel-" + Guid.NewGuid().ToString("N"),
                RepoUrl = repositoryPath,
                WorkingDirectory = repositoryPath,
                DefaultBranch = "main"
            };

            return await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task<string> CreateRepositoryAsync(string repo)
        {
            Directory.CreateDirectory(Path.Combine(repo, "src"));
            await RunGitAsync(repo, "init", "-b", "main").ConfigureAwait(false);
            await RunGitAsync(repo, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(repo, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(repo, "src", "Target.cs"), "namespace Sample { public class Target { } }\n").ConfigureAwait(false);
            await RunGitAsync(repo, "add", ".").ConfigureAwait(false);
            await RunGitAsync(repo, "commit", "-m", "Initial commit").ConfigureAwait(false);
            return repo;
        }

        private static async Task<string> RunGitAsync(string workingDirectory, params string[] args)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (string arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using (Process process = new Process { StartInfo = startInfo })
            {
                process.Start();
                string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException("git failed (exit " + process.ExitCode + "): " + stderr.Trim());
                }

                return stdout;
            }
        }

        private static LoggingModule SilentLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
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
    }
}
