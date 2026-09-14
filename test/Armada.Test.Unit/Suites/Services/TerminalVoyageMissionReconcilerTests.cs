namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// A WorkProduced mission under an ended voyage must reach a terminal status from landing evidence:
    /// Complete when its commit is on the default branch, Failed or Cancelled otherwise. The voyage
    /// completion, halt and cancellation paths all count WorkProduced as done and leave it in place, so
    /// these tests drive those real paths and then the reconciliation pass. Real git repositories are used
    /// so the ancestry probe is genuinely exercised.
    /// </summary>
    public sealed class TerminalVoyageMissionReconcilerTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Terminal Voyage Mission Reconciliation";

        private static readonly DateTime _Now = new DateTime(2031, 3, 4, 12, 0, 0, DateTimeKind.Utc);

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Voyage completion leaves upstream stages WorkProduced until the pass completes landed work and cancels superseded work", async () =>
            {
                await WithFixtureAsync(async (fx, db, logging) =>
                {
                    Voyage voyage = await CreateVoyageAsync(db, VoyageStatusEnum.InProgress, null).ConfigureAwait(false);
                    Mission worker = await CreateMissionAsync(db, fx.Vessel, voyage, "Worker", fx.LandedSha).ConfigureAwait(false);
                    Mission tester = await CreateMissionAsync(db, fx.Vessel, voyage, "TestEngineer", fx.UnlandedSha, worker.Id).ConfigureAwait(false);
                    Mission judge = await CreateMissionAsync(db, fx.Vessel, voyage, "Judge", fx.LandedSha, tester.Id, MissionStatusEnum.Complete).ConfigureAwait(false);

                    List<Voyage> ended = await new VoyageService(logging, db).CheckCompletionsAsync().ConfigureAwait(false);
                    AssertEqual(1, ended.Count, "the completion check must end the voyage");
                    AssertEqual(MissionStatusEnum.WorkProduced, (await db.Missions.ReadAsync(worker.Id).ConfigureAwait(false))!.Status,
                        "the completion check leaves an upstream stage WorkProduced");

                    await BackdateVoyageAsync(db, voyage.Id, TimeSpan.FromHours(1)).ConfigureAwait(false);
                    TerminalVoyageMissionReconciliationResult result = await Reconciler(fx, db, logging)
                        .ReconcileAsync(Apply(), CancellationToken.None).ConfigureAwait(false);

                    AssertEqual(2, result.Examined, "both WorkProduced stages are examined");
                    AssertEqual(MissionStatusEnum.Complete, (await db.Missions.ReadAsync(worker.Id).ConfigureAwait(false))!.Status,
                        "a stage whose commit is on the default branch is Complete");
                    Mission testerAfter = (await db.Missions.ReadAsync(tester.Id).ConfigureAwait(false))!;
                    AssertEqual(MissionStatusEnum.Cancelled, testerAfter.Status,
                        "unlanded work under a Complete voyage is Cancelled, never Complete");
                    AssertContains(TerminalVoyageMissionRule.ReasonWorkUnlanded, testerAfter.FailureReason ?? "", "the reason is recorded on the mission");
                    AssertEqual(MissionStatusEnum.Complete, (await db.Missions.ReadAsync(judge.Id).ConfigureAwait(false))!.Status,
                        "an already terminal mission is untouched");

                    EnumerationResult<ArmadaEvent> events = await db.Events.EnumerateAsync(
                        new EnumerationQuery { PageNumber = 1, PageSize = 50 }).ConfigureAwait(false);
                    AssertEqual(2, events.Objects.Count(e => e.EventType == "mission.terminal_voyage_reconciled"),
                        "each change records a reconciliation event");
                }).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("A failed voyage fails unlanded, absent and commitless work, completes landed work, and keeps every ref", async () =>
            {
                await WithFixtureAsync(async (fx, db, logging) =>
                {
                    Voyage voyage = await CreateVoyageAsync(db, VoyageStatusEnum.Failed, _Now.AddHours(-2)).ConfigureAwait(false);
                    Mission landed = await CreateMissionAsync(db, fx.Vessel, voyage, "Worker", fx.LandedSha).ConfigureAwait(false);
                    Mission unlanded = await CreateMissionAsync(db, fx.Vessel, voyage, "TestEngineer", fx.UnlandedSha).ConfigureAwait(false);
                    Mission absent = await CreateMissionAsync(db, fx.Vessel, voyage, "Worker", "0123456789abcdef0123456789abcdef01234567").ConfigureAwait(false);
                    Mission noCommit = await CreateMissionAsync(db, fx.Vessel, voyage, "Architect", null).ConfigureAwait(false);

                    string refsBefore = await GitAsync(fx.Bare, "for-each-ref", "--format=%(refname) %(objectname)").ConfigureAwait(false);
                    TerminalVoyageMissionReconciliationResult result = await Reconciler(fx, db, logging)
                        .ReconcileAsync(Apply(), CancellationToken.None).ConfigureAwait(false);
                    string refsAfter = await GitAsync(fx.Bare, "for-each-ref", "--format=%(refname) %(objectname)").ConfigureAwait(false);

                    AssertEqual(1, result.Completed, "one landed mission");
                    AssertEqual(3, result.Failed, "three unlanded missions");
                    AssertEqual(MissionStatusEnum.Complete, (await db.Missions.ReadAsync(landed.Id).ConfigureAwait(false))!.Status, "landed work under a Failed voyage is Complete");
                    AssertReason(await db.Missions.ReadAsync(unlanded.Id).ConfigureAwait(false), MissionStatusEnum.Failed, TerminalVoyageMissionRule.ReasonWorkUnlanded);
                    AssertReason(await db.Missions.ReadAsync(absent.Id).ConfigureAwait(false), MissionStatusEnum.Failed, TerminalVoyageMissionRule.ReasonCommitAbsent);
                    AssertReason(await db.Missions.ReadAsync(noCommit.Id).ConfigureAwait(false), MissionStatusEnum.Failed, TerminalVoyageMissionRule.ReasonNoCommit);
                    AssertEqual(refsBefore, refsAfter, "the pass never creates, moves or deletes a ref");
                    AssertEqual(fx.UnlandedSha, (await db.Missions.ReadAsync(unlanded.Id).ConfigureAwait(false))!.CommitHash, "the commit evidence stays on the mission");
                }).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("Voyage cancellation leaves WorkProduced work until the pass cancels it", async () =>
            {
                await WithFixtureAsync(async (fx, db, logging) =>
                {
                    Voyage voyage = await CreateVoyageAsync(db, VoyageStatusEnum.InProgress, null).ConfigureAwait(false);
                    Mission produced = await CreateMissionAsync(db, fx.Vessel, voyage, "Worker", fx.UnlandedSha).ConfigureAwait(false);

                    await VoyageCancellation.CancelVoyageAsync(db, voyage, "operator cancel").ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.WorkProduced, (await db.Missions.ReadAsync(produced.Id).ConfigureAwait(false))!.Status,
                        "cancellation cancels only Pending, Assigned and InProgress missions");

                    await BackdateVoyageAsync(db, voyage.Id, TimeSpan.FromHours(1)).ConfigureAwait(false);
                    await Reconciler(fx, db, logging).ReconcileAsync(Apply(), CancellationToken.None).ConfigureAwait(false);
                    AssertReason(await db.Missions.ReadAsync(produced.Id).ConfigureAwait(false), MissionStatusEnum.Cancelled, TerminalVoyageMissionRule.ReasonWorkUnlanded);
                }).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("A dry run changes nothing and a repeated pass is idempotent", async () =>
            {
                await WithFixtureAsync(async (fx, db, logging) =>
                {
                    Voyage voyage = await CreateVoyageAsync(db, VoyageStatusEnum.Failed, _Now.AddHours(-2)).ConfigureAwait(false);
                    Mission landed = await CreateMissionAsync(db, fx.Vessel, voyage, "Worker", fx.LandedSha).ConfigureAwait(false);
                    Mission unlanded = await CreateMissionAsync(db, fx.Vessel, voyage, "TestEngineer", fx.UnlandedSha).ConfigureAwait(false);
                    TerminalVoyageMissionReconciler reconciler = Reconciler(fx, db, logging);

                    TerminalVoyageMissionReconciliationRequest dry = Apply();
                    dry.DryRun = true;
                    TerminalVoyageMissionReconciliationResult preview = await reconciler.ReconcileAsync(dry, CancellationToken.None).ConfigureAwait(false);
                    AssertTrue(preview.DryRun, "the result says it was a dry run");
                    AssertEqual(1, preview.Completed, "the dry run reports the landed mission");
                    AssertEqual(1, preview.Failed, "the dry run reports the unlanded mission");
                    AssertEqual(2, preview.Items.Count, "the dry run lists each mission");
                    AssertEqual(MissionStatusEnum.WorkProduced, (await db.Missions.ReadAsync(landed.Id).ConfigureAwait(false))!.Status, "a dry run writes nothing");
                    AssertEqual(MissionStatusEnum.WorkProduced, (await db.Missions.ReadAsync(unlanded.Id).ConfigureAwait(false))!.Status, "a dry run writes nothing");

                    TerminalVoyageMissionReconciliationResult first = await reconciler.ReconcileAsync(Apply(), CancellationToken.None).ConfigureAwait(false);
                    AssertEqual(preview.Completed, first.Completed, "the apply pass matches the dry run");
                    AssertEqual(preview.Failed, first.Failed, "the apply pass matches the dry run");

                    TerminalVoyageMissionReconciliationResult second = await reconciler.ReconcileAsync(Apply(), CancellationToken.None).ConfigureAwait(false);
                    AssertEqual(0, second.Examined, "a second pass finds nothing left to reconcile");
                }).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("Keeps a live voyage, a voyage inside grace, a landing in flight, unknown ancestry and a no-landing voyage, each with its reason", async () =>
            {
                await WithFixtureAsync(async (fx, db, logging) =>
                {
                    Voyage live = await CreateVoyageAsync(db, VoyageStatusEnum.InProgress, null).ConfigureAwait(false);
                    Mission liveMission = await CreateMissionAsync(db, fx.Vessel, live, "Worker", fx.UnlandedSha).ConfigureAwait(false);

                    Voyage recent = await CreateVoyageAsync(db, VoyageStatusEnum.Failed, _Now.AddMinutes(-1)).ConfigureAwait(false);
                    Mission recentMission = await CreateMissionAsync(db, fx.Vessel, recent, "Worker", fx.UnlandedSha).ConfigureAwait(false);

                    Voyage landing = await CreateVoyageAsync(db, VoyageStatusEnum.Failed, _Now.AddHours(-2)).ConfigureAwait(false);
                    Mission landingMission = await CreateMissionAsync(db, fx.Vessel, landing, "Worker", fx.UnlandedSha).ConfigureAwait(false);
                    MergeEntry queued = new MergeEntry();
                    queued.MissionId = landingMission.Id;
                    queued.VesselId = fx.Vessel.Id;
                    queued.BranchName = "armada/test/landing";
                    queued.TargetBranch = "main";
                    queued.Status = MergeStatusEnum.Queued;
                    await db.MergeEntries.CreateAsync(queued).ConfigureAwait(false);

                    Vessel missingRepo = new Vessel("missing-repo-" + Guid.NewGuid().ToString("N"), "https://github.com/test/missing.git");
                    missingRepo.LocalPath = Path.Combine(fx.Root, "does-not-exist.git");
                    missingRepo.DefaultBranch = "main";
                    missingRepo = await db.Vessels.CreateAsync(missingRepo).ConfigureAwait(false);
                    Voyage unknown = await CreateVoyageAsync(db, VoyageStatusEnum.Failed, _Now.AddHours(-2)).ConfigureAwait(false);
                    Mission unknownMission = await CreateMissionAsync(db, missingRepo, unknown, "Worker", fx.UnlandedSha).ConfigureAwait(false);

                    Voyage manual = await CreateVoyageAsync(db, VoyageStatusEnum.Complete, _Now.AddHours(-2), LandingModeEnum.None).ConfigureAwait(false);
                    Mission manualMission = await CreateMissionAsync(db, fx.Vessel, manual, "Worker", fx.UnlandedSha).ConfigureAwait(false);

                    TerminalVoyageMissionReconciliationResult result = await Reconciler(fx, db, logging)
                        .ReconcileAsync(Apply(), CancellationToken.None).ConfigureAwait(false);

                    AssertEqual(0, result.Completed + result.Failed + result.Cancelled, "nothing may change");
                    AssertEqual(4, result.Kept, "the live voyage is not examined; the other four are kept");
                    AssertEqual(1, Reason(result, TerminalVoyageMissionRule.ReasonGrace), "grace is named");
                    AssertEqual(1, Reason(result, TerminalVoyageMissionRule.ReasonLandingInFlight), "a landing in flight is named");
                    AssertEqual(1, Reason(result, TerminalVoyageMissionRule.ReasonAncestryUnknown), "unknown ancestry is named");
                    AssertEqual(1, Reason(result, TerminalVoyageMissionRule.ReasonAwaitingManualLanding), "a no-landing voyage is named");
                    foreach (Mission kept in new[] { liveMission, recentMission, landingMission, unknownMission, manualMission })
                    {
                        AssertEqual(MissionStatusEnum.WorkProduced, (await db.Missions.ReadAsync(kept.Id).ConfigureAwait(false))!.Status, "kept missions stay WorkProduced");
                    }
                }).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("The automatic window skips voyages that ended before the lookback; the historical repair includes them", async () =>
            {
                await WithFixtureAsync(async (fx, db, logging) =>
                {
                    Voyage old = await CreateVoyageAsync(db, VoyageStatusEnum.Cancelled, _Now.AddDays(-30)).ConfigureAwait(false);
                    Mission oldMission = await CreateMissionAsync(db, fx.Vessel, old, "Worker", fx.LandedSha).ConfigureAwait(false);
                    TerminalVoyageMissionReconciler reconciler = Reconciler(fx, db, logging);

                    TerminalVoyageMissionReconciliationRequest automatic = Apply();
                    automatic.IncludeHistorical = false;
                    TerminalVoyageMissionReconciliationResult windowed = await reconciler.ReconcileAsync(automatic, CancellationToken.None).ConfigureAwait(false);
                    AssertEqual(0, windowed.Examined, "a voyage outside the lookback is not examined automatically");
                    AssertEqual(MissionStatusEnum.WorkProduced, (await db.Missions.ReadAsync(oldMission.Id).ConfigureAwait(false))!.Status, "the automatic pass leaves it");

                    TerminalVoyageMissionReconciliationResult historical = await reconciler.ReconcileAsync(Apply(), CancellationToken.None).ConfigureAwait(false);
                    AssertEqual(1, historical.Completed, "the historical repair completes landed work under a Cancelled voyage");
                }).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("A recorded landing completes a mission whose commit is no longer in the repository", async () =>
            {
                await WithFixtureAsync(async (fx, db, logging) =>
                {
                    Voyage voyage = await CreateVoyageAsync(db, VoyageStatusEnum.Failed, _Now.AddHours(-2)).ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(db, fx.Vessel, voyage, "Worker", "fedcba9876543210fedcba9876543210fedcba98").ConfigureAwait(false);
                    MergeEntry landedEntry = new MergeEntry();
                    landedEntry.MissionId = mission.Id;
                    landedEntry.VesselId = fx.Vessel.Id;
                    landedEntry.BranchName = "armada/test/landed";
                    landedEntry.TargetBranch = "main";
                    landedEntry.Status = MergeStatusEnum.Landed;
                    await db.MergeEntries.CreateAsync(landedEntry).ConfigureAwait(false);

                    await Reconciler(fx, db, logging).ReconcileAsync(Apply(), CancellationToken.None).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Complete, (await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!.Status,
                        "a Landed merge entry is landing evidence");
                }).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunTest("The rule never completes unlanded work for any terminal voyage", () =>
            {
                TerminalVoyageLandingProbeEnum[] unlanded =
                {
                    TerminalVoyageLandingProbeEnum.NotLanded,
                    TerminalVoyageLandingProbeEnum.CommitAbsent,
                    TerminalVoyageLandingProbeEnum.NoCommit,
                    TerminalVoyageLandingProbeEnum.Unknown
                };
                foreach (VoyageStatusEnum voyageStatus in Enum.GetValues<VoyageStatusEnum>())
                {
                    foreach (TerminalVoyageLandingProbeEnum probe in unlanded)
                    {
                        foreach (bool noLanding in new[] { false, true })
                        {
                            TerminalVoyageMissionDecision decision = TerminalVoyageMissionRule.Decide(
                                voyageStatus, _Now.AddDays(-1), _Now, TimeSpan.FromMinutes(10), false, noLanding, probe);
                            AssertFalse(decision.TargetStatus == MissionStatusEnum.Complete,
                                voyageStatus + "/" + probe + " must never become Complete");
                            if (decision.TargetStatus.HasValue)
                            {
                                AssertTrue(MissionStateMachine.IsValidTransition(MissionStatusEnum.WorkProduced, decision.TargetStatus.Value),
                                    "WorkProduced to " + decision.TargetStatus.Value + " must be a legal transition");
                            }
                        }
                    }
                }
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }

        #region Helpers

        private sealed class Fixture
        {
            public string Root { get; set; } = String.Empty;

            public string Bare { get; set; } = String.Empty;

            public string LandedSha { get; set; } = String.Empty;

            public string UnlandedSha { get; set; } = String.Empty;

            public Vessel Vessel { get; set; } = null!;
        }

        private static TerminalVoyageMissionReconciliationRequest Apply()
        {
            TerminalVoyageMissionReconciliationRequest request = new TerminalVoyageMissionReconciliationRequest();
            request.DryRun = false;
            request.IncludeHistorical = true;
            request.NowUtc = _Now;
            return request;
        }

        private static TerminalVoyageMissionReconciler Reconciler(Fixture fx, SqliteDatabaseDriver db, LoggingModule logging)
        {
            return new TerminalVoyageMissionReconciler(logging, db, new GitService(logging));
        }

        private static int Reason(TerminalVoyageMissionReconciliationResult result, string reason)
        {
            return result.Reasons.TryGetValue(reason, out int count) ? count : 0;
        }

        private void AssertReason(Mission? mission, MissionStatusEnum status, string reason)
        {
            AssertNotNull(mission, "mission exists");
            AssertEqual(status, mission!.Status, "mission status");
            AssertContains(reason, mission.FailureReason ?? "", "mission failure reason names " + reason);
        }

        private async Task WithFixtureAsync(Func<Fixture, SqliteDatabaseDriver, LoggingModule, Task> body)
        {
            string root = Path.Combine(Path.GetTempPath(), "armada_tvm_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string source = Path.Combine(root, "source");
                string bare = Path.Combine(root, "vessel.git");
                Directory.CreateDirectory(source);
                await GitAsync(source, "init", "-b", "main").ConfigureAwait(false);
                await GitAsync(source, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                await GitAsync(source, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                await CommitAsync(source, "base.txt").ConfigureAwait(false);

                await GitAsync(source, "checkout", "-b", "armada/test/landed").ConfigureAwait(false);
                await CommitAsync(source, "landed.txt").ConfigureAwait(false);
                string landedSha = await GitAsync(source, "rev-parse", "HEAD").ConfigureAwait(false);
                await GitAsync(source, "checkout", "main").ConfigureAwait(false);
                await GitAsync(source, "merge", "--no-ff", "-m", "land", "armada/test/landed").ConfigureAwait(false);

                await GitAsync(source, "checkout", "-b", "armada/test/unlanded", "main").ConfigureAwait(false);
                await CommitAsync(source, "unlanded.txt").ConfigureAwait(false);
                string unlandedSha = await GitAsync(source, "rev-parse", "HEAD").ConfigureAwait(false);
                await GitAsync(source, "checkout", "main").ConfigureAwait(false);
                await GitAsync(root, "clone", "--bare", source, bare).ConfigureAwait(false);

                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = new LoggingModule();
                    logging.Settings.EnableConsole = false;
                    Vessel vessel = new Vessel("tvm-vessel-" + Guid.NewGuid().ToString("N"), "https://github.com/test/tvm.git");
                    vessel.LocalPath = bare;
                    vessel.DefaultBranch = "main";
                    vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Fixture fx = new Fixture
                    {
                        Root = root,
                        Bare = bare,
                        LandedSha = landedSha,
                        UnlandedSha = unlandedSha,
                        Vessel = vessel
                    };
                    await body(fx, testDb.Driver, logging).ConfigureAwait(false);
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(root, true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("could not remove test directory " + root + ": " + ex.Message);
                }
            }
        }

        private static async Task<Voyage> CreateVoyageAsync(SqliteDatabaseDriver db, VoyageStatusEnum status, DateTime? completedUtc, LandingModeEnum? landingMode = null)
        {
            Voyage voyage = new Voyage("tvm voyage " + Guid.NewGuid().ToString("N"));
            voyage.Status = status;
            voyage.CompletedUtc = completedUtc;
            voyage.LandingMode = landingMode;
            if (completedUtc.HasValue) voyage.LastUpdateUtc = completedUtc.Value;
            return await db.Voyages.CreateAsync(voyage).ConfigureAwait(false);
        }

        private static async Task BackdateVoyageAsync(SqliteDatabaseDriver db, string voyageId, TimeSpan age)
        {
            Voyage voyage = (await db.Voyages.ReadAsync(voyageId).ConfigureAwait(false))!;
            voyage.CompletedUtc = _Now - age;
            voyage.LastUpdateUtc = _Now - age;
            await db.Voyages.UpdateAsync(voyage).ConfigureAwait(false);
        }

        private static async Task<Mission> CreateMissionAsync(
            SqliteDatabaseDriver db,
            Vessel vessel,
            Voyage voyage,
            string persona,
            string? commit,
            string? dependsOn = null,
            MissionStatusEnum status = MissionStatusEnum.WorkProduced)
        {
            Mission mission = new Mission(persona + " stage", "stage work");
            mission.VesselId = vessel.Id;
            mission.VoyageId = voyage.Id;
            mission.Persona = persona;
            mission.Status = status;
            mission.CommitHash = commit;
            mission.DependsOnMissionId = dependsOn;
            mission.BranchName = "armada/test/" + Guid.NewGuid().ToString("N");
            return await db.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private static async Task CommitAsync(string dir, string file)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, file), file + "\n").ConfigureAwait(false);
            await GitAsync(dir, "add", file).ConfigureAwait(false);
            await GitAsync(dir, "commit", "-m", file).ConfigureAwait(false);
        }

        private static async Task<string> GitAsync(string dir, params string[] args)
        {
            ProcessStartInfo psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);
            using (Process process = Process.Start(psi)!)
            {
                string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("git " + String.Join(" ", args) + " failed: " + stderr);
                return stdout.Trim();
            }
        }

        #endregion
    }
}
