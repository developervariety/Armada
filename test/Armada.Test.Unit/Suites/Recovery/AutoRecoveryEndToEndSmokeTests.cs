namespace Armada.Test.Unit.Suites.Recovery
{
    using System;
    using System.Diagnostics;
    using System.IO;
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
    /// End-to-end smoke test for the auto-recovery loop. Exercises the real
    /// <see cref="MergeQueueService"/> against actual local git repositories so
    /// the classifier, recovery-handler invocation, redispatch bookkeeping, and
    /// follow-on land flow all run as they would in production.
    ///
    /// Load-bearing: removing the merge-queue's
    /// <c>FireRecoveryHandlerForEntry</c> invocation breaks the
    /// <c>RecoveryAttempts == 1</c> + <c>recovery_redispatched</c> assertions
    /// because nothing else updates those rows when an entry transitions to
    /// Failed.
    /// </summary>
    public class AutoRecoveryEndToEndSmokeTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Auto Recovery End To End Smoke";

        /// <summary>Run all cases.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("EndToEnd_DuplicateEnumEntryConflict_AutoRedispatchLandsCleanOnSecondAttempt", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_recovery_smoke_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);

                    // Build a synthetic duplicate-enum-entry text conflict: main has one
                    // value added, captain branch has a different value added on the same
                    // line, so a 3-way merge produces a real text conflict on Foo.cs.
                    string sourceDir = Path.Combine(rootDir, "source");
                    string remoteDir = Path.Combine(rootDir, "remote.git");
                    string bareDir = Path.Combine(rootDir, "bare.git");

                    Directory.CreateDirectory(sourceDir);
                    await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "config", "receive.denyCurrentBranch", "ignore").ConfigureAwait(false);

                    string enumFile = Path.Combine(sourceDir, "Foo.cs");
                    await File.WriteAllTextAsync(enumFile, "public enum Foo { A, B }\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "add", "Foo.cs").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-m", "Initial enum").ConfigureAwait(false);

                    string captainBranch = "captain/dup-enum";
                    await RunGitAsync(sourceDir, "checkout", "-b", captainBranch).ConfigureAwait(false);
                    await File.WriteAllTextAsync(enumFile, "public enum Foo { A, B, C }\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "add", "Foo.cs").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-m", "Captain adds C").ConfigureAwait(false);

                    await RunGitAsync(sourceDir, "checkout", "main").ConfigureAwait(false);
                    await File.WriteAllTextAsync(enumFile, "public enum Foo { A, B, X }\n").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "add", "Foo.cs").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "commit", "-m", "Main adds X (conflicting addition)").ConfigureAwait(false);

                    await RunGitAsync(rootDir, "clone", "--bare", sourceDir, remoteDir).ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "remote", "add", "origin", remoteDir).ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "push", "origin", "main").ConfigureAwait(false);
                    await RunGitAsync(sourceDir, "push", "origin", captainBranch).ConfigureAwait(false);

                    await RunGitAsync(rootDir, "clone", "--bare", remoteDir, bareDir).ConfigureAwait(false);
                    await RunGitAsync(bareDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                    await RunGitAsync(bareDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = NewQuietLogging();
                        ArmadaSettings settings = new ArmadaSettings
                        {
                            DocksDirectory = Path.Combine(rootDir, "docks"),
                            ReposDirectory = Path.Combine(rootDir, "repos"),
                            MaxRecoveryAttempts = 2,
                            BranchCleanupPolicy = BranchCleanupPolicyEnum.None
                        };
                        Directory.CreateDirectory(settings.DocksDirectory);
                        Directory.CreateDirectory(settings.ReposDirectory);

                        Vessel vessel = new Vessel("smoke-vessel", remoteDir);
                        vessel.LocalPath = bareDir;
                        vessel.WorkingDirectory = sourceDir;
                        vessel.DefaultBranch = "main";
                        vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.None;
                        await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        Mission mission = new Mission("dup-enum-mission", "Add enum value")
                        {
                            BranchName = captainBranch,
                            VesselId = vessel.Id,
                            Status = MissionStatusEnum.Complete
                        };
                        await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                        MergeEntry firstEntry = new MergeEntry(captainBranch, "main")
                        {
                            VesselId = vessel.Id,
                            MissionId = mission.Id,
                            Status = MergeStatusEnum.Queued,
                            CreatedUtc = DateTime.UtcNow,
                            LastUpdateUtc = DateTime.UtcNow
                        };
                        await testDb.Driver.MergeEntries.CreateAsync(firstEntry).ConfigureAwait(false);

                        GitService git = new GitService(logging);
                        MergeQueueService mergeQueue = new MergeQueueService(logging, testDb.Driver, settings, git, new MergeFailureClassifier());

                        // Wire a real recovery handler with a real router/playbook service so the
                        // background-fired OnMergeFailedAsync exercises the redispatch path end-to-end.
                        IRecoveryRouter router = new RecoveryRouter(settings.MaxRecoveryAttempts);
                        IRebaseCaptainDockSetup dockSetup = new RebaseCaptainDockSetup(git, testDb.Driver, logging);
                        IMergeRecoveryHandler recoveryHandler = new MergeRecoveryHandler(
                            logging, testDb.Driver, settings, router, dockSetup, mergeQueue,
                            new PlaybookService(testDb.Driver, logging));
                        mergeQueue.SetRecoveryHandler(recoveryHandler);

                        // Stage 1: process the entry. Real merge -> real text conflict -> Failed.
                        await mergeQueue.ProcessEntryByIdAsync(firstEntry.Id).ConfigureAwait(false);

                        MergeEntry? failed = await testDb.Driver.MergeEntries.ReadAsync(firstEntry.Id).ConfigureAwait(false);
                        AssertEqual(MergeStatusEnum.Failed, ResolveCurrentStatus(failed), "first attempt should fail or be cancelled by recovery handler");
                        // Classifier ran at fail-time
                        AssertEqual(MergeFailureClassEnum.TextConflict, failed!.MergeFailureClass!.Value, "classifier should label this as TextConflict");
                        AssertTrue(failed.DiffLineCount <= 60, "diff should be trivial-eligible (1 file, small line count)");

                        // The recovery handler runs fire-and-forget. Wait for the background
                        // task to drive the entry to Cancelled and bump the mission's
                        // recovery counter.
                        bool recoveryObserved = await WaitForRecoveryAsync(testDb, firstEntry.Id, mission.Id).ConfigureAwait(false);
                        AssertTrue(recoveryObserved,
                            "recovery handler must observe the Failed transition and drive the redispatch flow within the wait window -- removing the FireRecoveryHandlerForEntry invocation breaks this assertion");

                        MergeEntry? afterRecovery = await testDb.Driver.MergeEntries.ReadAsync(firstEntry.Id).ConfigureAwait(false);
                        AssertEqual(MergeStatusEnum.Cancelled, afterRecovery!.Status, "first entry should be cancelled by the recovery redispatch path");
                        AssertEqual("recovery_redispatched", afterRecovery.TestOutput ?? "", "entry note should mark recovery_redispatched");

                        Mission? afterRecoveryMission = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertEqual(1, afterRecoveryMission!.RecoveryAttempts, "first failure should burn exactly 1 recovery attempt");
                        AssertEqual(MissionStatusEnum.Pending, afterRecoveryMission.Status, "redispatched mission should be back in Pending");

                        // Simulate the redispatched captain: produce a clean captain branch
                        // that no longer collides with main and force-push it. Update the
                        // local bare so the merge-queue fetch picks up the new tip.
                        string fixedSourceDir = Path.Combine(rootDir, "fixed-source");
                        await RunGitAsync(rootDir, "clone", remoteDir, fixedSourceDir).ConfigureAwait(false);
                        await RunGitAsync(fixedSourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                        await RunGitAsync(fixedSourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                        await RunGitAsync(fixedSourceDir, "checkout", "-B", captainBranch, "origin/main").ConfigureAwait(false);
                        await File.WriteAllTextAsync(Path.Combine(fixedSourceDir, "Notes.md"), "// captain redispatched\n").ConfigureAwait(false);
                        await RunGitAsync(fixedSourceDir, "add", "Notes.md").ConfigureAwait(false);
                        await RunGitAsync(fixedSourceDir, "commit", "-m", "Captain redispatch (clean diff)").ConfigureAwait(false);
                        await RunGitAsync(fixedSourceDir, "push", "--force", "origin", captainBranch).ConfigureAwait(false);

                        // Refresh the local bare with the new captain-branch tip.
                        await RunGitAsync(bareDir, "fetch", "--prune", "origin").ConfigureAwait(false);
                        await RunGitAsync(bareDir, "branch", "-f", captainBranch, "origin/" + captainBranch).ConfigureAwait(false);

                        // Stage 2: a fresh merge entry models the redispatched mission's
                        // re-enqueue. With the conflict resolved, the merge succeeds and
                        // the entry lands.
                        MergeEntry secondEntry = new MergeEntry(captainBranch, "main")
                        {
                            VesselId = vessel.Id,
                            MissionId = mission.Id,
                            Status = MergeStatusEnum.Queued,
                            CreatedUtc = DateTime.UtcNow,
                            LastUpdateUtc = DateTime.UtcNow
                        };
                        await testDb.Driver.MergeEntries.CreateAsync(secondEntry).ConfigureAwait(false);

                        await mergeQueue.ProcessEntryByIdAsync(secondEntry.Id).ConfigureAwait(false);

                        MergeEntry? landed = await testDb.Driver.MergeEntries.ReadAsync(secondEntry.Id).ConfigureAwait(false);
                        AssertEqual(MergeStatusEnum.Landed, landed!.Status, "second attempt should land cleanly after the captain-fix simulation");

                        Mission? finalMission = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertEqual(1, finalMission!.RecoveryAttempts,
                            "second attempt must NOT burn an additional recovery slot -- it landed");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch { }
                }
            });
        }

        /// <summary>
        /// Wait up to a few seconds for the fire-and-forget recovery handler to run.
        /// Polls the entry until it transitions out of Failed (recovery handler set
        /// it to Cancelled) or the budget expires.
        /// </summary>
        private static async Task<bool> WaitForRecoveryAsync(TestDatabase testDb, string entryId, string missionId)
        {
            for (int i = 0; i < 100; i++)
            {
                MergeEntry? entry = await testDb.Driver.MergeEntries.ReadAsync(entryId).ConfigureAwait(false);
                Mission? mission = await testDb.Driver.Missions.ReadAsync(missionId).ConfigureAwait(false);
                if (entry != null && entry.Status == MergeStatusEnum.Cancelled
                    && mission != null && mission.RecoveryAttempts >= 1)
                {
                    return true;
                }
                await Task.Delay(50).ConfigureAwait(false);
            }
            return false;
        }

        private static MergeStatusEnum ResolveCurrentStatus(MergeEntry? entry)
        {
            if (entry == null) return MergeStatusEnum.Failed;
            // The recovery handler may have already raced ahead and flipped Failed -> Cancelled
            // by the time the synchronous ProcessEntryByIdAsync caller observes the row, but
            // the failure-class fields stay populated either way.
            if (entry.Status == MergeStatusEnum.Cancelled) return MergeStatusEnum.Failed;
            return entry.Status;
        }

        private static LoggingModule NewQuietLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
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
            foreach (string arg in args) startInfo.ArgumentList.Add(arg);

            using (Process process = new Process { StartInfo = startInfo })
            {
                process.Start();
                string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException("git " + String.Join(' ', args) + " failed (exit " + process.ExitCode + "): " + stderr.Trim());
                }
                return stdout;
            }
        }
    }
}
