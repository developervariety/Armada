namespace Armada.Test.Unit.Suites.Services
{
    using System.Diagnostics;
    using System.Reflection;
    using System.Text.Json;
    using Armada.Core;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Server;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using StubGitService = global::Test.Shared.Infrastructure.StubGitService;
    using SyslogLogging;

    /// <summary>
    /// Tests the landing sweep that proposes LandingFailed missions whose work is already on the default branch, the
    /// Needs You item it raises, and the confirmation that completes the mission.
    /// </summary>
    public class LandingReconcileTests : TestSuite
    {
        private const string TargetRef = "refs/remotes/origin/main";

        /// <inheritdoc />
        public override string Name => "Landing Reconcile";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Sweep proposes a mission whose commit is on the default branch once and leaves it LandingFailed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Voyage voyage = await CreateVoyageAsync(testDb).ConfigureAwait(false);
                    Mission mission = await CreateLandingFailedAsync(testDb, vessel, voyage, "Already landed", "merge conflicts in: src/Foo.cs").ConfigureAwait(false);
                    StubGitService git = new StubGitService();
                    git.AncestorResults[mission.CommitHash + "|" + TargetRef] = true;
                    LandingReconcileSweep sweep = CreateSweep(testDb, git);

                    AssertEqual(1, await sweep.SweepAsync().ConfigureAwait(false), "the first sweep proposes the mission");
                    AssertEqual(0, await sweep.SweepAsync().ConfigureAwait(false), "the second sweep proposes nothing new");

                    List<ArmadaEvent> proposals = await ProposalsAsync(testDb, mission).ConfigureAwait(false);
                    AssertEqual(1, proposals.Count, "one proposal per mission");
                    LandingReconcileEvidence evidence = JsonSerializer.Deserialize<LandingReconcileEvidence>(proposals[0].Payload!)!;
                    AssertEqual(LandingEvidenceKindEnum.CommitAncestry, evidence.EvidenceKind, "ancestry is the proof");
                    AssertEqual(mission.CommitHash!, evidence.EvidenceCommit, "the proof names the mission commit");
                    AssertEqual("origin/main", evidence.TargetRef, "the proof names the ref it read");

                    Mission? after = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.LandingFailed, after!.Status, "the sweep never completes the mission");

                    List<InboxItem> items = await new InboxService(testDb.Driver, CreateLogging()).GetInboxAsync().ConfigureAwait(false);
                    List<InboxItem> forMission = items.Where(item => item.EntityId == mission.Id).ToList();
                    AssertEqual(1, forMission.Count, "one Needs You item per mission");
                    AssertEqual(InboxService.LandingReconcileKind, forMission[0].Kind, "the item asks for confirmation");
                    AssertContains(mission.CommitHash!.Substring(0, 12), forMission[0].Detail, "the item carries the evidence");
                    AssertContains("merge conflicts in: src/Foo.cs", forMission[0].Detail, "the item keeps the failure reason");
                }
            });

            await RunTest("Sweep proposes on the exact mission trailer when the commit is not an ancestor", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Mission mission = await CreateLandingFailedAsync(testDb, vessel, null, "Squash landed", "landing failed").ConfigureAwait(false);
                    StubGitService git = new StubGitService();
                    git.AncestorResults[mission.CommitHash + "|" + TargetRef] = false;
                    string trailerCommit = new String('c', 40);
                    git.TrailerCommits[mission.Id] = trailerCommit;

                    AssertEqual(1, await CreateSweep(testDb, git).SweepAsync().ConfigureAwait(false), "the trailer is proof");

                    List<ArmadaEvent> proposals = await ProposalsAsync(testDb, mission).ConfigureAwait(false);
                    LandingReconcileEvidence evidence = JsonSerializer.Deserialize<LandingReconcileEvidence>(proposals[0].Payload!)!;
                    AssertEqual(LandingEvidenceKindEnum.MissionTrailer, evidence.EvidenceKind, "the trailer is the proof kind");
                    AssertEqual(trailerCommit, evidence.EvidenceCommit, "the proof names the trailer commit");
                    AssertTrue(git.TrailerSearchCalls.Any(call => call.Contains(LandingReconcileProof.MissionIdTrailerKey + ":" + mission.Id)),
                        "the search used the Armada-Mission-Id trailer key and the exact mission id");
                }
            });

            await RunTest("Sweep skips landing refusals, a stage start commit and work with no proof", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string logDirectory = Path.Combine(Path.GetTempPath(), "armada_reconcile_logs_" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                        StubGitService git = new StubGitService { IsAncestorResult = true };

                        Mission evidenceRefusal = await CreateLandingFailedAsync(testDb, vessel, null, "Evidence refusal",
                            LandingEvidence.RefusalPrefix + ": the change could not be read").ConfigureAwait(false);
                        Mission boundaryRefusal = await CreateLandingFailedAsync(testDb, vessel, null, "Boundary refusal",
                            LandingRefusalReasons.DockBoundaryPrefix + " for vessel 'v': a finding").ConfigureAwait(false);
                        Mission protectedPath = await CreateLandingFailedAsync(testDb, vessel, null, "Protected path",
                            ProtectedPathsValidator.FormatFailureReason("CLAUDE.md", "v")).ConfigureAwait(false);
                        Mission startCommit = await CreateLandingFailedAsync(testDb, vessel, null, "Unchanged checkout", "landing failed").ConfigureAwait(false);
                        await MissionStageStartCommitResolver.WriteStageStartRecordAsync(logDirectory, startCommit.Id, startCommit.CommitHash!).ConfigureAwait(false);
                        Mission unproven = await CreateLandingFailedAsync(testDb, vessel, null, "No proof", "landing failed").ConfigureAwait(false);
                        git.AncestorResults[unproven.CommitHash + "|" + TargetRef] = false;
                        Mission proven = await CreateLandingFailedAsync(testDb, vessel, null, "Proven", "landing failed").ConfigureAwait(false);

                        LandingReconcileSweep sweep = new LandingReconcileSweep(testDb.Driver, git, CreateLogging(), logDirectory);
                        AssertEqual(1, await sweep.SweepAsync().ConfigureAwait(false), "only the proven mission is proposed");

                        foreach (Mission skipped in new[] { evidenceRefusal, boundaryRefusal, protectedPath, startCommit, unproven })
                            AssertEqual(0, (await ProposalsAsync(testDb, skipped).ConfigureAwait(false)).Count, skipped.Title + " is not proposed");
                        AssertEqual(1, (await ProposalsAsync(testDb, proven).ConfigureAwait(false)).Count, "the proven mission is proposed");
                    }
                    finally
                    {
                        if (Directory.Exists(logDirectory)) Directory.Delete(logDirectory, true);
                    }
                }
            });

            await RunTest("Sweep fetches each vessel once per sweep and rotates through bounded pages", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    StubGitService git = new StubGitService { IsAncestorResult = true };
                    List<Mission> missions = new List<Mission>();
                    for (int index = 0; index < 3; index++)
                    {
                        missions.Add(await CreateLandingFailedAsync(testDb, vessel, null, "Mission " + index, "landing failed").ConfigureAwait(false));
                        await Task.Delay(5).ConfigureAwait(false);
                    }

                    LandingReconcileSweep sweep = CreateSweep(testDb, git);
                    sweep.MaxPerCycle = 2;

                    AssertEqual(2, await sweep.SweepAsync().ConfigureAwait(false), "the first sweep reads one page of two");
                    AssertEqual(1, git.FetchCalls.Count, "the vessel is fetched once for two missions");
                    AssertEqual(1, await sweep.SweepAsync().ConfigureAwait(false), "the second sweep reads the next page");
                    AssertEqual(2, git.FetchCalls.Count, "each sweep fetches the vessel once");
                    AssertEqual(0, await sweep.SweepAsync().ConfigureAwait(false), "the third sweep wraps to the first page, already proposed");
                    foreach (Mission mission in missions)
                        AssertEqual(1, (await ProposalsAsync(testDb, mission).ConfigureAwait(false)).Count, mission.Title + " is proposed once");
                }
            });

            await RunTest("A LandingFailed mission cannot complete without a proposal", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Mission mission = await CreateLandingFailedAsync(testDb, vessel, null, "Not proposed", "landing failed").ConfigureAwait(false);
                    StubGitService git = new StubGitService { IsAncestorResult = true };

                    MissionStatusTransitionResult result = await CreateTransitions(testDb, git).TransitionAsync(mission, MissionStatusEnum.Complete).ConfigureAwait(false);

                    AssertEqual(MissionStatusTransitionOutcomeEnum.InvalidTransition, result.Outcome, "LandingFailed to Complete stays invalid without a proposal");
                    Mission? after = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.LandingFailed, after!.Status, "the mission is unchanged");
                }
            });

            await RunTest("Confirming a proposal completes the mission, its voyage and closes its incident", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Voyage voyage = await CreateVoyageAsync(testDb).ConfigureAwait(false);
                    Mission mission = await CreateLandingFailedAsync(testDb, vessel, voyage, "Landed elsewhere", "landing failed").ConfigureAwait(false);
                    StubGitService git = new StubGitService();
                    git.AncestorResults[mission.CommitHash + "|" + TargetRef] = true;
                    AuthContext auth = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, true, "UnitTest");
                    IncidentService incidents = new IncidentService(testDb.Driver);
                    Incident incident = await incidents.CreateAsync(auth, new IncidentUpsertRequest
                    {
                        Title = "Landing failed",
                        MissionId = mission.Id,
                        VesselId = vessel.Id
                    }).ConfigureAwait(false);
                    await CreateSweep(testDb, git).SweepAsync().ConfigureAwait(false);

                    MissionStatusTransitionResult result = await CreateTransitions(testDb, git).TransitionAsync(mission, MissionStatusEnum.Complete).ConfigureAwait(false);

                    AssertEqual(MissionStatusTransitionOutcomeEnum.Applied, result.Outcome, "the confirmation applies: " + result.Message);
                    Mission? after = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Complete, after!.Status, "the mission is Complete");
                    AssertNull(after.FailureReason, "the failure reason is cleared");
                    AssertNotNull(after.CompletedUtc, "the completion time is recorded");
                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByMissionAsync(mission.Id, 50).ConfigureAwait(false);
                    ArmadaEvent? reconciled = events.FirstOrDefault(item => item.EventType == LandingReconcileProposals.ReconciledEventType);
                    AssertNotNull(reconciled, "the confirmation is recorded");
                    LandingReconcileEvidence recorded = JsonSerializer.Deserialize<LandingReconcileEvidence>(reconciled!.Payload!)!;
                    AssertEqual("landing failed", recorded.FailureReason, "the event keeps the reason the landing failed");
                    Voyage? voyageAfter = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(VoyageStatusEnum.Complete, voyageAfter!.Status, "voyage completion runs after the confirmation");
                    Incident? incidentAfter = await incidents.ReadAsync(auth, incident.Id).ConfigureAwait(false);
                    AssertEqual(IncidentStatusEnum.Closed, incidentAfter!.Status, "the mission's incident is closed");
                    List<InboxItem> items = await new InboxService(testDb.Driver, CreateLogging()).GetInboxAsync().ConfigureAwait(false);
                    AssertFalse(items.Any(item => item.EntityId == mission.Id), "the Needs You item is gone");
                }
            });

            await RunTest("Confirming refuses when the proof is gone or a Check failed and leaves the mission LandingFailed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                    Mission mission = await CreateLandingFailedAsync(testDb, vessel, null, "Proof changes", "landing failed").ConfigureAwait(false);
                    StubGitService git = new StubGitService();
                    git.AncestorResults[mission.CommitHash + "|" + TargetRef] = true;
                    await CreateSweep(testDb, git).SweepAsync().ConfigureAwait(false);
                    MissionStatusTransitionService transitions = CreateTransitions(testDb, git);

                    git.AncestorResults[mission.CommitHash + "|" + TargetRef] = false;
                    MissionStatusTransitionResult gone = await transitions.TransitionAsync(mission, MissionStatusEnum.Complete).ConfigureAwait(false);
                    AssertEqual(MissionStatusTransitionOutcomeEnum.Refused, gone.Outcome, "a proposal whose proof no longer holds is refused");
                    AssertContains("landing_reconcile_proof_unavailable", gone.Message, "the refusal names the missing proof");

                    git.AncestorResults[mission.CommitHash + "|" + TargetRef] = true;
                    await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                    {
                        MissionId = mission.Id,
                        Status = CheckRunStatusEnum.Failed,
                        Command = "dotnet test",
                        StartedUtc = DateTime.UtcNow,
                        CommitHash = mission.CommitHash
                    }).ConfigureAwait(false);
                    MissionStatusTransitionResult failedCheck = await transitions.TransitionAsync(mission, MissionStatusEnum.Complete).ConfigureAwait(false);
                    AssertEqual(MissionStatusTransitionOutcomeEnum.Refused, failedCheck.Outcome, "the manual completion gates still apply");
                    AssertContains("manual_completion_failed_check", failedCheck.Message, "the refusal names the failed Check");

                    Mission? after = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.LandingFailed, after!.Status, "a refusal never changes the mission");
                }
            });

            await RunTest("Trailer search matches the exact Armada-Mission-Id trailer and not a prose mention", async () =>
            {
                string repository = Path.Combine(Path.GetTempPath(), "armada_trailer_repo_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(repository);
                try
                {
                    await RunGitAsync(repository, "init", "-b", "main").ConfigureAwait(false);
                    await RunGitAsync(repository, "config", "user.email", "test@example.test").ConfigureAwait(false);
                    await RunGitAsync(repository, "config", "user.name", "Test").ConfigureAwait(false);
                    await CommitAsync(repository, "a.txt", "mentions msn_prose in prose only").ConfigureAwait(false);
                    await CommitAsync(repository, "b.txt", "carries the trailer\n\nArmada-Mission-Id: msn_exact").ConfigureAwait(false);
                    string exactCommit = (await RunGitAsync(repository, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
                    await CommitAsync(repository, "c.txt", "longer id\n\nArmada-Mission-Id: msn_exact_longer").ConfigureAwait(false);

                    LoggingModule logging = CreateLogging();
                    GitService git = new GitService(logging);
                    IReadOnlyList<string> exact = await git.FindCommitsByTrailerAsync(
                        repository, "refs/heads/main", LandingReconcileProof.MissionIdTrailerKey, "msn_exact", null, 1).ConfigureAwait(false);
                    AssertEqual(1, exact.Count, "the exact trailer matches one commit");
                    AssertEqual(exactCommit, exact[0], "the match is the trailer commit, not the longer id");
                    AssertEqual(0, (await git.FindCommitsByTrailerAsync(
                        repository, "refs/heads/main", LandingReconcileProof.MissionIdTrailerKey, "msn_prose", null, 1).ConfigureAwait(false)).Count,
                        "a prose mention is not a trailer");
                    AssertEqual(0, (await git.FindCommitsByTrailerAsync(
                        repository, "refs/heads/main", LandingReconcileProof.MissionIdTrailerKey, "msn_exact", DateTime.UtcNow.AddDays(1), 1).ConfigureAwait(false)).Count,
                        "the creation bound excludes commits older than the mission");
                    AssertEqual(0, (await git.FindCommitsByTrailerAsync(
                        repository, "refs/remotes/origin/missing", LandingReconcileProof.MissionIdTrailerKey, "msn_exact", null, 1).ConfigureAwait(false)).Count,
                        "a ref that does not resolve finds nothing");
                }
                finally
                {
                    if (Directory.Exists(repository))
                    {
                        foreach (string file in Directory.EnumerateFiles(repository, "*", SearchOption.AllDirectories))
                            File.SetAttributes(file, FileAttributes.Normal);
                        Directory.Delete(repository, true);
                    }
                }
            });
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static LandingReconcileSweep CreateSweep(TestDatabase testDb, IGitService git)
        {
            return new LandingReconcileSweep(testDb.Driver, git, CreateLogging());
        }

        private static MissionStatusTransitionService CreateTransitions(TestDatabase testDb, IGitService git)
        {
            return new MissionStatusTransitionService(
                testDb.Driver,
                DispatchProxy.Create<IAdmiralService, EmptyServiceProxy>(),
                DispatchProxy.Create<IMissionService, EmptyServiceProxy>(),
                git,
                (Mission mission, CancellationToken token) => Task.FromResult(false),
                (Mission mission, Dock dock) => Task.CompletedTask,
                (string eventType, string message, string? entityType, string? entityId, string? captainId, string? missionId, string? vesselId, string? voyageId) => Task.CompletedTask,
                CreateLogging());
        }

        private static async Task<Vessel> CreateVesselAsync(TestDatabase testDb)
        {
            Vessel vessel = new Vessel("reconcile-vessel-" + Guid.NewGuid().ToString("N").Substring(0, 6), "https://example.test/reconcile.git");
            vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_reconcile_bare_" + Guid.NewGuid().ToString("N"));
            vessel.DefaultBranch = "main";
            return await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task<Voyage> CreateVoyageAsync(TestDatabase testDb)
        {
            return await testDb.Driver.Voyages.CreateAsync(new Voyage("Reconcile voyage")
            {
                Status = VoyageStatusEnum.InProgress
            }).ConfigureAwait(false);
        }

        private static async Task<Mission> CreateLandingFailedAsync(TestDatabase testDb, Vessel vessel, Voyage? voyage, string title, string failureReason)
        {
            Mission mission = new Mission(title)
            {
                Status = MissionStatusEnum.LandingFailed,
                VesselId = vessel.Id,
                VoyageId = voyage?.Id,
                FailureReason = failureReason,
                CommitHash = Guid.NewGuid().ToString("N") + "00000000"
            };
            return await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private static async Task<List<ArmadaEvent>> ProposalsAsync(TestDatabase testDb, Mission mission)
        {
            List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByMissionAsync(mission.Id, 100).ConfigureAwait(false);
            return events.Where(item => item.EventType == LandingReconcileProposals.ProposedEventType).ToList();
        }

        private static async Task CommitAsync(string repository, string file, string message)
        {
            await File.WriteAllTextAsync(Path.Combine(repository, file), message).ConfigureAwait(false);
            await RunGitAsync(repository, "add", file).ConfigureAwait(false);
            await RunGitAsync(repository, "commit", "-m", message).ConfigureAwait(false);
        }

        private static async Task<string> RunGitAsync(string repository, params string[] arguments)
        {
            ProcessStartInfo start = new ProcessStartInfo("git")
            {
                WorkingDirectory = repository,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using (Process process = Process.Start(start)!)
            {
                string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);
                if (process.ExitCode != 0) throw new InvalidOperationException("git " + String.Join(" ", arguments) + " failed: " + error);
                return output;
            }
        }

        /// <summary>
        /// Stand-in for a collaborator whose members the confirmation reads but does not need: every member returns the
        /// default value, so an unset hook reads as null.
        /// </summary>
        public class EmptyServiceProxy : DispatchProxy
        {
            /// <inheritdoc />
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                Type? returnType = targetMethod?.ReturnType;
                if (returnType == null || returnType == typeof(void)) return null;
                if (returnType == typeof(Task)) return Task.CompletedTask;
                return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
            }
        }
    }
}
