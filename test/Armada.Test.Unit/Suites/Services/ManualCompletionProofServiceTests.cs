namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests the proof service used by the manual Complete status route.
    /// </summary>
    public class ManualCompletionProofServiceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Manual Completion Proof Service";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("RealGit_UnlandedCommitIsRejected", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string repository = await CreateRepositoryAsync().ConfigureAwait(false);
                    try
                    {
                        Mission mission = await CreateMissionAsync(testDb, repository).ConfigureAwait(false);
                        ManualCompletionProofResult result = await new ManualCompletionProofService(
                            testDb.Driver, CreateGit()).EvaluateAsync(mission, false).ConfigureAwait(false);
                        AssertFalse(result.Allowed, "A feature commit absent from the target must not complete");
                        AssertEqual("manual_completion_unlanded", result.Reason, "Unlanded reason");
                    }
                    finally
                    {
                        DeleteDirectory(repository);
                    }
                }
            });

            await RunTest("RealGit_LandedCommitIsAccepted", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string repository = await CreateRepositoryAsync().ConfigureAwait(false);
                    try
                    {
                        Mission mission = await CreateMissionAsync(testDb, repository).ConfigureAwait(false);
                        await RunGitAsync(repository, "checkout", "main").ConfigureAwait(false);
                        await RunGitAsync(repository, "merge", "feature").ConfigureAwait(false);
                        ManualCompletionProofResult result = await new ManualCompletionProofService(
                            testDb.Driver, CreateGit()).EvaluateAsync(mission, false).ConfigureAwait(false);
                        AssertTrue(result.Allowed, "A commit proven in the target must complete");
                        AssertEqual("target_ancestry", result.Reason, "Landed reason");
                    }
                    finally
                    {
                        DeleteDirectory(repository);
                    }
                }
            });

            await RunTest("RealGit_MissingCommitIsUnknownAndRejected", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string repository = await CreateRepositoryAsync().ConfigureAwait(false);
                    try
                    {
                        Mission mission = await CreateMissionAsync(testDb, repository).ConfigureAwait(false);
                        mission.CommitHash = new String('0', 40);
                        ManualCompletionProofResult result = await new ManualCompletionProofService(
                            testDb.Driver, CreateGit()).EvaluateAsync(mission, false).ConfigureAwait(false);
                        AssertFalse(result.Allowed, "A missing commit must fail closed");
                        AssertEqual("manual_completion_unlanded", result.Reason, "Missing commit is safely treated as unlanded");
                    }
                    finally
                    {
                        DeleteDirectory(repository);
                    }
                }
            });

            await RunTest("UnknownAncestryAndJudgeCompletionAreRejected", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = new Vessel("manual-unknown-vessel", "https://example.test/manual-proof.git")
                    {
                        LocalPath = Path.Combine(Path.GetTempPath(), "manual-unknown-repo")
                    };
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    Mission mission = new Mission("manual unknown")
                    {
                        VesselId = vessel.Id,
                        CommitHash = new String('a', 40)
                    };
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                    StubGitService unknownGit = new StubGitService { IsAncestorResult = null };
                    ManualCompletionProofResult unknown = await new ManualCompletionProofService(
                        testDb.Driver, unknownGit).EvaluateAsync(mission, false).ConfigureAwait(false);
                    AssertFalse(unknown.Allowed, "Unknown ancestry must fail closed");
                    AssertEqual("manual_completion_ancestry_unknown", unknown.Reason, "Unknown ancestry reason");

                    mission.Persona = PersonaCatalog.Judge;
                    ManualCompletionProofResult judge = await new ManualCompletionProofService(
                        testDb.Driver, unknownGit).EvaluateAsync(mission, true).ConfigureAwait(false);
                    AssertFalse(judge.Allowed, "Manual status must not replace Judge authority");
                    AssertEqual("manual_completion_judge_required", judge.Reason, "Judge authority reason");
                }
            });

            await RunTest("FailedPendingAndStaleChecksRemainBlocking", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = new Vessel("manual-proof-vessel", "https://example.test/manual-proof.git");
                    vessel.LocalPath = Path.Combine(Path.GetTempPath(), "manual-proof-repo");
                    await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    Voyage voyage = new Voyage { Title = "manual proof voyage" };
                    await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);
                    Mission mission = new Mission("manual proof checks");
                    mission.VesselId = vessel.Id;
                    mission.VoyageId = voyage.Id;
                    mission.CommitHash = new String('a', 40);
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    CheckRun failed = new CheckRun
                    {
                        MissionId = mission.Id,
                        Status = CheckRunStatusEnum.Failed,
                        Command = "dotnet test",
                        StartedUtc = DateTime.UtcNow,
                        CommitHash = mission.CommitHash
                    };
                    await testDb.Driver.CheckRuns.CreateAsync(failed).ConfigureAwait(false);
                    ManualCompletionProofService service = new ManualCompletionProofService(testDb.Driver, CreateGit());
                    ManualCompletionProofResult result = await service.EvaluateAsync(mission, true).ConfigureAwait(false);
                    AssertFalse(result.Allowed, "Failed check must block");
                    AssertEqual("manual_completion_failed_check", result.Reason, "Failed check reason");

                    failed.Status = CheckRunStatusEnum.Pending;
                    await testDb.Driver.CheckRuns.UpdateAsync(failed).ConfigureAwait(false);
                    result = await service.EvaluateAsync(mission, true).ConfigureAwait(false);
                    AssertFalse(result.Allowed, "Pending check must block");
                    AssertEqual("manual_completion_pending_check", result.Reason, "Pending check reason");

                    failed.Status = CheckRunStatusEnum.Passed;
                    failed.CommitHash = new String('b', 40);
                    await testDb.Driver.CheckRuns.UpdateAsync(failed).ConfigureAwait(false);
                    result = await service.EvaluateAsync(mission, true).ConfigureAwait(false);
                    AssertFalse(result.Allowed, "Stale check must block");
                    AssertEqual("manual_completion_stale_check", result.Reason, "Stale check reason");
                }
            });

            await RunTest("ReportOnlyCompletionDoesNotRequireGitProof", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = new Mission("manual report completion")
                    {
                        Mode = MissionModeEnum.Research
                    };
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                    ManualCompletionProofResult result = await new ManualCompletionProofService(
                        testDb.Driver, CreateGit()).EvaluateAsync(mission, false).ConfigureAwait(false);
                    AssertTrue(result.Allowed, "Report-only completion must keep its no-commit contract");
                    AssertEqual("report_only", result.Reason, "Report-only reason");
                }
            });

            await RunTest("IntermediateStageMayPrecedePendingJudge", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Voyage voyage = new Voyage { Title = "manual judge voyage" };
                    await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);
                    Mission worker = new Mission("manual worker")
                    {
                        VoyageId = voyage.Id,
                        Mode = MissionModeEnum.Implementation
                    };
                    await testDb.Driver.Missions.CreateAsync(worker).ConfigureAwait(false);
                    Mission judge = new Mission("manual judge")
                    {
                        VoyageId = voyage.Id,
                        Persona = PersonaCatalog.Judge,
                        Status = MissionStatusEnum.InProgress,
                        DependsOnMissionId = worker.Id
                    };
                    await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);
                    ManualCompletionProofResult result = await new ManualCompletionProofService(
                        testDb.Driver, new StubGitService { IsAncestorResult = true })
                        .EvaluateAsync(worker, true).ConfigureAwait(false);
                    AssertTrue(result.Allowed, "An intermediate stage may complete before its downstream Judge");
                    AssertEqual("landing_pipeline", result.Reason, "Intermediate stage reason");
                }
            });

            await RunTest("TerminalStageCannotBypassPendingJudge", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Voyage voyage = new Voyage { Title = "manual terminal judge voyage" };
                    await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);
                    Mission worker = new Mission("manual terminal worker")
                    {
                        VoyageId = voyage.Id,
                        Mode = MissionModeEnum.Implementation
                    };
                    await testDb.Driver.Missions.CreateAsync(worker).ConfigureAwait(false);
                    Mission judge = new Mission("manual terminal judge")
                    {
                        VoyageId = voyage.Id,
                        Persona = PersonaCatalog.Judge,
                        Status = MissionStatusEnum.InProgress
                    };
                    await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);
                    ManualCompletionProofResult result = await new ManualCompletionProofService(
                        testDb.Driver, new StubGitService { IsAncestorResult = true })
                        .EvaluateAsync(worker, true).ConfigureAwait(false);
                    AssertFalse(result.Allowed, "A terminal stage must not bypass a pending Judge");
                    AssertEqual("manual_completion_judge_required", result.Reason, "Pending Judge reason");
                }
            });

            await RunTest("ChecksAreReadAcrossAllPages", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = new Mission("manual paged checks")
                    {
                        Mode = MissionModeEnum.Implementation,
                        CommitHash = new String('a', 40)
                    };
                    await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    CheckRun failed = new CheckRun
                    {
                        MissionId = mission.Id,
                        Status = CheckRunStatusEnum.Failed,
                        Command = "dotnet test",
                        StartedUtc = DateTime.UtcNow.AddHours(-2),
                        CompletedUtc = DateTime.UtcNow.AddHours(-2),
                        CreatedUtc = DateTime.UtcNow.AddHours(-2),
                        CommitHash = mission.CommitHash
                    };
                    await testDb.Driver.CheckRuns.CreateAsync(failed).ConfigureAwait(false);
                    for (int index = 0; index < 100; index++)
                    {
                        CheckRun passed = new CheckRun
                        {
                            MissionId = mission.Id,
                            Status = CheckRunStatusEnum.Passed,
                            Command = "dotnet test",
                            StartedUtc = DateTime.UtcNow,
                            CompletedUtc = DateTime.UtcNow,
                            CreatedUtc = DateTime.UtcNow,
                            CommitHash = mission.CommitHash
                        };
                        await testDb.Driver.CheckRuns.CreateAsync(passed).ConfigureAwait(false);
                    }

                    ManualCompletionProofResult result = await new ManualCompletionProofService(
                        testDb.Driver, new StubGitService { IsAncestorResult = true })
                        .EvaluateAsync(mission, true).ConfigureAwait(false);
                    AssertFalse(result.Allowed, "A failed check on a later page must block completion");
                    AssertEqual("manual_completion_failed_check", result.Reason, "Paged failed check reason");
                }
            });
        }

        private static GitService CreateGit()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new GitService(logging);
        }

        private static async Task<Mission> CreateMissionAsync(TestDatabase testDb, string repository)
        {
            Vessel vessel = new Vessel("manual-proof-vessel-" + Guid.NewGuid().ToString("N"), "https://example.test/manual-proof.git");
            vessel.LocalPath = repository;
            vessel.DefaultBranch = "main";
            await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
            Mission mission = new Mission("manual proof mission")
            {
                VesselId = vessel.Id,
                CommitHash = (await RunGitAsync(repository, "rev-parse", "feature").ConfigureAwait(false)).Trim()
            };
            await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
            return mission;
        }

        private static async Task<string> CreateRepositoryAsync()
        {
            string directory = Path.Combine(Path.GetTempPath(), "armada-manual-proof-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            await RunGitAsync(directory, "init", "-b", "main").ConfigureAwait(false);
            await RunGitAsync(directory, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(directory, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(directory, "base.txt"), "base\n").ConfigureAwait(false);
            await RunGitAsync(directory, "add", "base.txt").ConfigureAwait(false);
            await RunGitAsync(directory, "commit", "-m", "base").ConfigureAwait(false);
            await RunGitAsync(directory, "checkout", "-b", "feature").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(directory, "feature.txt"), "feature\n").ConfigureAwait(false);
            await RunGitAsync(directory, "add", "feature.txt").ConfigureAwait(false);
            await RunGitAsync(directory, "commit", "-m", "feature").ConfigureAwait(false);
            return directory;
        }

        private static async Task<string> RunGitAsync(string directory, params string[] arguments)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
            using Process process = new Process { StartInfo = startInfo };
            process.Start();
            string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException("git failed: " + stderr.Trim());
            return stdout;
        }

        private static void DeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch { }
        }
    }
}
