namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Recovery;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Verifies that <see cref="MergeQueueService"/> wires the
    /// <see cref="IMergeFailureClassifier"/> at fail-time: the classification fields
    /// (MergeFailureClass / ConflictedFiles / MergeFailureSummary / DiffLineCount)
    /// are persisted on the merge entry alongside the Failed status, and
    /// <see cref="MergeEntry.ConflictedFiles"/> is stored as a JSON array.
    /// </summary>
    public class MergeQueueServiceClassificationTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Merge Queue Service Classification";

        private LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_mq_class_docks_" + Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_mq_class_repos_" + Guid.NewGuid().ToString("N"));
            settings.BranchCleanupPolicy = BranchCleanupPolicyEnum.None;
            return settings;
        }

        /// <summary>
        /// Build a local git repo (source -> remote bare -> local bare) where the
        /// captain branch and main both modified the same line of the same file,
        /// guaranteeing that the merge-queue service will hit a TextConflict.
        /// </summary>
        private async Task<ConflictRepoSetup> CreateConflictingReposAsync(string rootDir)
        {
            string remoteDir = Path.Combine(rootDir, "remote.git");
            string sourceDir = Path.Combine(rootDir, "source");
            string bareDir = Path.Combine(rootDir, "bare.git");

            Directory.CreateDirectory(sourceDir);
            await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "config", "receive.denyCurrentBranch", "ignore").ConfigureAwait(false);

            string sharedFile = Path.Combine(sourceDir, "shared.txt");
            await File.WriteAllTextAsync(sharedFile, "original line\n").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "add", "shared.txt").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "commit", "-m", "Initial commit").ConfigureAwait(false);

            string captainBranch = "armada/captain-conflict/msn_test001";
            await RunGitAsync(sourceDir, "checkout", "-b", captainBranch).ConfigureAwait(false);
            await File.WriteAllTextAsync(sharedFile, "captain line\n").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "add", "shared.txt").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "commit", "-m", "Captain edits shared").ConfigureAwait(false);

            // Diverge main with conflicting edit on the same line.
            await RunGitAsync(sourceDir, "checkout", "main").ConfigureAwait(false);
            await File.WriteAllTextAsync(sharedFile, "main line\n").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "add", "shared.txt").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "commit", "-m", "Main edits shared").ConfigureAwait(false);

            await RunGitAsync(rootDir, "clone", "--bare", sourceDir, remoteDir).ConfigureAwait(false);
            await RunGitAsync(sourceDir, "remote", "add", "origin", remoteDir).ConfigureAwait(false);
            await RunGitAsync(sourceDir, "push", "origin", "main").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "push", "origin", captainBranch).ConfigureAwait(false);

            await RunGitAsync(rootDir, "clone", "--bare", remoteDir, bareDir).ConfigureAwait(false);
            await RunGitAsync(bareDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(bareDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

            return new ConflictRepoSetup(remoteDir, bareDir, captainBranch);
        }

        /// <summary>Run git as a subprocess (synchronous wait, stderr captured via exception).</summary>
        private static async Task RunGitAsync(string workingDir, params string[] args)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (string arg in args) startInfo.ArgumentList.Add(arg);

            using (Process proc = new Process { StartInfo = startInfo })
            {
                proc.Start();
                string stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await proc.WaitForExitAsync().ConfigureAwait(false);
                if (proc.ExitCode != 0)
                {
                    throw new InvalidOperationException("git " + String.Join(" ", args) + " failed: " + stderr);
                }
            }
        }

        /// <summary>Run all classification persistence tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("ProcessEntry_MergeConflict_PersistsClassificationFieldsBeforeFailedStatus", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_class_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    ConflictRepoSetup repos = await CreateConflictingReposAsync(rootDir).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);
                        RecordingMergeFailureClassifier classifier = new RecordingMergeFailureClassifier(
                            new MergeFailureClassification(
                                MergeFailureClassEnum.TextConflict,
                                "Recording classifier: text conflict",
                                new List<string> { "shared.txt" }));

                        Vessel vessel = new Vessel("classification-vessel", repos.RemoteDir);
                        vessel.LocalPath = repos.BareDir;
                        vessel.DefaultBranch = "main";
                        vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.None;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        MergeEntry entry = new MergeEntry();
                        entry.VesselId = vessel.Id;
                        entry.BranchName = repos.CaptainBranch;
                        entry.TargetBranch = "main";
                        entry.Status = MergeStatusEnum.Queued;
                        entry.CreatedUtc = DateTime.UtcNow;
                        entry.LastUpdateUtc = DateTime.UtcNow;
                        await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, classifier);
                        await service.ProcessEntryByIdAsync(entry.Id).ConfigureAwait(false);

                        MergeEntry? updated = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                        AssertNotNull(updated, "entry should still exist after failed merge");
                        AssertEqual(MergeStatusEnum.Failed, updated!.Status, "entry must be Failed after merge conflict");
                        AssertEqual(1, classifier.CallCount, "classifier should have been invoked exactly once");
                        AssertNotNull(classifier.LastContext, "classifier should have received a context");
                        AssertTrue(classifier.LastContext!.GitExitCode.HasValue,
                            "classifier context must include a git exit code from the failed merge");
                        AssertTrue(classifier.LastContext.GitExitCode!.Value != 0,
                            "git exit code in context must be non-zero for a conflict");

                        AssertNotNull(updated.MergeFailureClass, "MergeFailureClass must be persisted");
                        AssertEqual(MergeFailureClassEnum.TextConflict, updated.MergeFailureClass!.Value,
                            "MergeFailureClass must match classifier output");
                        AssertEqual("Recording classifier: text conflict", updated.MergeFailureSummary,
                            "MergeFailureSummary must match classifier output");
                        AssertNotNull(updated.ConflictedFiles, "ConflictedFiles must be persisted");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });

            await RunTest("ProcessEntry_MergeConflict_ConflictedFilesStoredAsJsonArray", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_class_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    ConflictRepoSetup repos = await CreateConflictingReposAsync(rootDir).ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = CreateLogging();
                        ArmadaSettings settings = CreateSettings();
                        GitService git = new GitService(logging);
                        RecordingMergeFailureClassifier classifier = new RecordingMergeFailureClassifier(
                            new MergeFailureClassification(
                                MergeFailureClassEnum.TextConflict,
                                "two conflicts",
                                new List<string> { "shared.txt", "another.txt" }));

                        Vessel vessel = new Vessel("classification-vessel-2", repos.RemoteDir);
                        vessel.LocalPath = repos.BareDir;
                        vessel.DefaultBranch = "main";
                        vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.None;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        MergeEntry entry = new MergeEntry();
                        entry.VesselId = vessel.Id;
                        entry.BranchName = repos.CaptainBranch;
                        entry.TargetBranch = "main";
                        entry.Status = MergeStatusEnum.Queued;
                        entry.CreatedUtc = DateTime.UtcNow;
                        entry.LastUpdateUtc = DateTime.UtcNow;
                        await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);

                        MergeQueueService service = new MergeQueueService(logging, testDb.Driver, settings, git, classifier);
                        await service.ProcessEntryByIdAsync(entry.Id).ConfigureAwait(false);

                        MergeEntry? updated = await testDb.Driver.MergeEntries.ReadAsync(entry.Id).ConfigureAwait(false);
                        AssertNotNull(updated, "entry should exist");
                        AssertNotNull(updated!.ConflictedFiles, "ConflictedFiles JSON must be persisted");

                        List<string>? files = JsonSerializer.Deserialize<List<string>>(updated.ConflictedFiles!);
                        AssertNotNull(files, "ConflictedFiles must deserialize as JSON array");
                        AssertEqual(2, files!.Count, "two conflicted files expected");
                        AssertEqual("shared.txt", files[0], "first conflicted file path");
                        AssertEqual("another.txt", files[1], "second conflicted file path");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });

            await RunTest("Constructor_NullClassifier_ThrowsArgumentNullException", async () =>
            {
                LoggingModule logging = CreateLogging();
                ArmadaSettings settings = CreateSettings();
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    GitService git = new GitService(logging);
                    bool threw = false;
                    try
                    {
                        new MergeQueueService(logging, testDb.Driver, settings, git, null!);
                    }
                    catch (ArgumentNullException)
                    {
                        threw = true;
                    }
                    AssertTrue(threw, "null classifier must throw ArgumentNullException -- the classifier is a required dependency");
                }
            });
        }

        private sealed class ConflictRepoSetup
        {
            public string RemoteDir { get; }
            public string BareDir { get; }
            public string CaptainBranch { get; }
            public ConflictRepoSetup(string remoteDir, string bareDir, string captainBranch)
            {
                RemoteDir = remoteDir;
                BareDir = bareDir;
                CaptainBranch = captainBranch;
            }
        }

        /// <summary>
        /// Hand-rolled <see cref="IMergeFailureClassifier"/> double that records every
        /// invocation and returns a fixed <see cref="MergeFailureClassification"/>.
        /// </summary>
        private sealed class RecordingMergeFailureClassifier : IMergeFailureClassifier
        {
            private readonly MergeFailureClassification _Result;

            public int CallCount { get; private set; }
            public MergeFailureContext? LastContext { get; private set; }

            public RecordingMergeFailureClassifier(MergeFailureClassification result)
            {
                _Result = result;
            }

            public MergeFailureClassification Classify(MergeFailureContext context)
            {
                CallCount++;
                LastContext = context;
                return _Result;
            }
        }
    }
}
