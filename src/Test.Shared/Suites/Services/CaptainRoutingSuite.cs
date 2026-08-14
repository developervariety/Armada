namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for per-step captain selection: the voyage captain-override serialization contract and the
    /// persistence round-trip of the persona default captain, mission requested captain, and voyage captain
    /// overrides across create/read/update. Covers both positive round-trips and negative (null / malformed)
    /// inputs.
    /// </summary>
    public sealed class CaptainRoutingSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.CaptainRouting";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Captain Routing suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(CaseAsync("overrides_serialize_roundtrip", "Captain overrides serialize and deserialize round-trip", TestTags.Positive, () =>
            {
                List<CaptainAssignmentOverride> overrides = new List<CaptainAssignmentOverride>
                {
                    new CaptainAssignmentOverride("Worker", "cpt_worker", CaptainTierEnum.Economy),
                    new CaptainAssignmentOverride("Judge", "cpt_judge", CaptainTierEnum.Premium),
                    new CaptainAssignmentOverride("Architect", null, CaptainTierEnum.Standard)
                };

                string? json = MissionService.SerializeCaptainOverrides(overrides);
                AssertNotNull(json, "Serialized overrides should not be null");

                List<CaptainAssignmentOverride> parsed = MissionService.DeserializeCaptainOverrides(json);
                AssertEqual(3, parsed.Count, "Round-trip should preserve every override");
                AssertEqual("Worker", parsed[0].Persona, "First persona should round-trip");
                AssertEqual("cpt_worker", parsed[0].CaptainId, "First captain id should round-trip");
                AssertEqual(CaptainTierEnum.Economy, parsed[0].FallbackTier, "First fallback tier should round-trip");
                AssertNull(parsed[2].CaptainId, "A tier-only override should round-trip with a null captain id");
                return Task.CompletedTask;
            }));

            cases.Add(CaseAsync("empty_overrides_serialize_to_null", "Empty or null overrides serialize to null (column stays null)", TestTags.Positive, () =>
            {
                AssertNull(MissionService.SerializeCaptainOverrides(null), "Null overrides should serialize to null");
                AssertNull(MissionService.SerializeCaptainOverrides(new List<CaptainAssignmentOverride>()), "Empty overrides should serialize to null");
                return Task.CompletedTask;
            }));

            cases.Add(CaseAsync("malformed_overrides_deserialize_to_empty", "Malformed override JSON deserializes to an empty list, never throws", TestTags.Negative, () =>
            {
                AssertEqual(0, MissionService.DeserializeCaptainOverrides("this is not json").Count, "Malformed JSON should yield an empty list");
                AssertEqual(0, MissionService.DeserializeCaptainOverrides(null).Count, "Null JSON should yield an empty list");
                AssertEqual(0, MissionService.DeserializeCaptainOverrides("   ").Count, "Whitespace JSON should yield an empty list");
                return Task.CompletedTask;
            }));

            cases.Add(CaseAsync("persona_default_captain_persists", "Persona default captain persists and round-trips through create and read", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Captain captain = new Captain("routing-default");
                    captain.Tier = CaptainTierEnum.Premium;
                    captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                    Persona persona = new Persona("Worker", "persona.worker");
                    persona.DefaultCaptainId = captain.Id;
                    persona = await testDb.Driver.Personas.CreateAsync(persona).ConfigureAwait(false);

                    Persona? read = await testDb.Driver.Personas.ReadAsync(persona.Id).ConfigureAwait(false);
                    AssertNotNull(read, "Persona should be readable");
                    AssertEqual(captain.Id, read!.DefaultCaptainId, "Default captain id should persist");

                    read.DefaultCaptainId = null;
                    await testDb.Driver.Personas.UpdateAsync(read).ConfigureAwait(false);
                    Persona? cleared = await testDb.Driver.Personas.ReadAsync(persona.Id).ConfigureAwait(false);
                    AssertNull(cleared!.DefaultCaptainId, "Clearing the default captain should persist as null");
                }
            }));

            cases.Add(CaseAsync("persona_null_default_persists_as_null", "Persona with no default captain persists as null", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Persona persona = new Persona("Judge", "persona.judge");
                    persona = await testDb.Driver.Personas.CreateAsync(persona).ConfigureAwait(false);

                    Persona? read = await testDb.Driver.Personas.ReadAsync(persona.Id).ConfigureAwait(false);
                    AssertNotNull(read, "Persona should be readable");
                    AssertNull(read!.DefaultCaptainId, "Default captain should be null when unset");
                }
            }));

            cases.Add(CaseAsync("mission_requested_captain_persists", "Mission requested captain persists and round-trips", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = new Mission("routing mission", "route me");
                    mission.RequestedCaptainId = "cpt_preferred";
                    mission.Tier = CaptainTierEnum.Premium;
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Mission? read = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(read, "Mission should be readable");
                    AssertEqual("cpt_preferred", read!.RequestedCaptainId, "Requested captain id should persist");
                    AssertEqual(CaptainTierEnum.Premium, read.Tier, "Fallback tier should persist");
                }
            }));

            cases.Add(CaseAsync("mission_null_requested_captain_persists_as_null", "Mission with no requested captain persists as null", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Mission mission = new Mission("unrouted mission", "no preference");
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);

                    Mission? read = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNotNull(read, "Mission should be readable");
                    AssertNull(read!.RequestedCaptainId, "Requested captain should be null when unset");
                }
            }));

            cases.Add(CaseAsync("voyage_overrides_persist", "Voyage captain overrides persist and deserialize back to the same entries", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    List<CaptainAssignmentOverride> overrides = new List<CaptainAssignmentOverride>
                    {
                        new CaptainAssignmentOverride("Worker", "cpt_w", CaptainTierEnum.Economy)
                    };

                    Voyage voyage = new Voyage("routing voyage", "carries overrides");
                    voyage.CaptainOverridesJson = MissionService.SerializeCaptainOverrides(overrides);
                    voyage = await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);

                    Voyage? read = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                    AssertNotNull(read, "Voyage should be readable");
                    AssertNotNull(read!.CaptainOverridesJson, "Overrides JSON should persist");

                    List<CaptainAssignmentOverride> parsed = MissionService.DeserializeCaptainOverrides(read.CaptainOverridesJson);
                    AssertEqual(1, parsed.Count, "Persisted overrides should deserialize");
                    AssertEqual("Worker", parsed[0].Persona, "Persisted persona should round-trip");
                    AssertEqual("cpt_w", parsed[0].CaptainId, "Persisted captain id should round-trip");
                    AssertEqual(CaptainTierEnum.Economy, parsed[0].FallbackTier, "Persisted fallback tier should round-trip");
                }
            }));

            cases.Add(CaseAsync("preferred_idle_captain_is_assigned", "Preferred captain, when idle, is assigned even against the persona fence", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    RoutingHarness harness = await BuildHarnessAsync(testDb.Driver).ConfigureAwait(false);
                    // The preferred captain only allows "Judge", yet the mission is a "RoutingWorker" -- the
                    // explicit choice must override the AllowedPersonas fence.
                    Captain preferred = await AddCaptainAsync(testDb.Driver, "pref-idle", CaptainStateEnum.Idle, "[\"Judge\"]", null).ConfigureAwait(false);
                    Mission mission = await AddMissionAsync(testDb.Driver, harness.Vessel, "RoutingWorker", preferred.Id, null).ConfigureAwait(false);

                    bool assigned = await harness.Missions.TryAssignAsync(mission, harness.Vessel).ConfigureAwait(false);
                    AssertTrue(assigned, "Idle preferred captain should be assignable");

                    Mission? read = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(preferred.Id, read!.CaptainId, "Idle preferred captain should be assigned despite the persona fence");
                }
            }));

            cases.Add(CaseAsync("persona_default_applies_at_assignment", "Persona default captain is resolved as the preferred captain and assigned", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    RoutingHarness harness = await BuildHarnessAsync(testDb.Driver).ConfigureAwait(false);
                    Captain defaultCaptain = await AddCaptainAsync(testDb.Driver, "persona-default", CaptainStateEnum.Idle, null, null).ConfigureAwait(false);
                    Persona persona = new Persona("RoutingDefaultRole", "persona.worker");
                    persona.DefaultCaptainId = defaultCaptain.Id;
                    await testDb.Driver.Personas.CreateAsync(persona).ConfigureAwait(false);

                    Mission mission = await AddMissionAsync(testDb.Driver, harness.Vessel, "RoutingDefaultRole", null, null).ConfigureAwait(false);
                    bool assigned = await harness.Missions.TryAssignAsync(mission, harness.Vessel).ConfigureAwait(false);
                    AssertTrue(assigned, "Persona default captain should be assignable");

                    Mission? read = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(defaultCaptain.Id, read!.RequestedCaptainId, "Persona default should resolve into the mission's preferred captain");
                    AssertEqual(defaultCaptain.Id, read.CaptainId, "Mission should be assigned to the persona default captain");
                }
            }));

            cases.Add(CaseAsync("preferred_busy_falls_back_by_tier", "A busy preferred captain falls back to an idle captain at or above the fallback tier", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    RoutingHarness harness = await BuildHarnessAsync(testDb.Driver).ConfigureAwait(false);
                    Captain busyPreferred = await AddCaptainAsync(testDb.Driver, "busy-pref", CaptainStateEnum.Working, null, CaptainTierEnum.Premium).ConfigureAwait(false);
                    Captain fallback = await AddCaptainAsync(testDb.Driver, "tier-fallback", CaptainStateEnum.Idle, null, CaptainTierEnum.Standard).ConfigureAwait(false);

                    Mission mission = await AddMissionAsync(testDb.Driver, harness.Vessel, "RoutingWorker", busyPreferred.Id, CaptainTierEnum.Standard).ConfigureAwait(false);
                    bool assigned = await harness.Missions.TryAssignAsync(mission, harness.Vessel).ConfigureAwait(false);
                    AssertTrue(assigned, "A tier-eligible idle captain should take the fallback");

                    Mission? read = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(fallback.Id, read!.CaptainId, "Busy preferred captain should fall back to the tier-eligible idle captain");
                }
            }));

            cases.Add(CaseAsync("deleted_preferred_captain_falls_back_to_normal_routing", "A preferred captain that no longer exists falls back to normal persona routing", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    RoutingHarness harness = await BuildHarnessAsync(testDb.Driver).ConfigureAwait(false);
                    Captain normal = await AddCaptainAsync(testDb.Driver, "normal-worker", CaptainStateEnum.Idle, "[\"RoutingWorker\"]", null).ConfigureAwait(false);

                    Mission mission = await AddMissionAsync(testDb.Driver, harness.Vessel, "RoutingWorker", "cpt_does_not_exist", null).ConfigureAwait(false);
                    bool assigned = await harness.Missions.TryAssignAsync(mission, harness.Vessel).ConfigureAwait(false);
                    AssertTrue(assigned, "A deleted preferred captain should not block normal routing");

                    Mission? read = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(normal.Id, read!.CaptainId, "Deleted preferred captain should fall back to a persona-eligible idle captain");
                }
            }));

            cases.Add(CaseAsync("no_tier_eligible_captain_stays_pending", "A busy preferred captain with no tier-eligible fallback leaves the mission Pending", TestTags.Negative, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    RoutingHarness harness = await BuildHarnessAsync(testDb.Driver).ConfigureAwait(false);
                    Captain busyPremium = await AddCaptainAsync(testDb.Driver, "busy-premium", CaptainStateEnum.Working, null, CaptainTierEnum.Premium).ConfigureAwait(false);
                    // The only idle captain is Economy, below the Premium fallback tier.
                    await AddCaptainAsync(testDb.Driver, "idle-economy", CaptainStateEnum.Idle, null, CaptainTierEnum.Economy).ConfigureAwait(false);

                    Mission mission = await AddMissionAsync(testDb.Driver, harness.Vessel, "RoutingWorker", busyPremium.Id, CaptainTierEnum.Premium).ConfigureAwait(false);
                    bool assigned = await harness.Missions.TryAssignAsync(mission, harness.Vessel).ConfigureAwait(false);
                    AssertFalse(assigned, "No Premium-or-above idle captain should mean no assignment");

                    Mission? read = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNull(read!.CaptainId, "Mission should remain unassigned");
                    AssertEqual(MissionStatusEnum.Pending, read.Status, "Mission should remain Pending for the next tick");
                }
            }));

            cases.Add(CaseAsync("no_preference_routes_normally", "A mission with no preferred captain and no persona default routes normally (regression guard)", TestTags.Positive, async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    RoutingHarness harness = await BuildHarnessAsync(testDb.Driver).ConfigureAwait(false);
                    Captain worker = await AddCaptainAsync(testDb.Driver, "plain-worker", CaptainStateEnum.Idle, "[\"RoutingPlainRole\"]", null).ConfigureAwait(false);

                    Mission mission = await AddMissionAsync(testDb.Driver, harness.Vessel, "RoutingPlainRole", null, null).ConfigureAwait(false);
                    bool assigned = await harness.Missions.TryAssignAsync(mission, harness.Vessel).ConfigureAwait(false);
                    AssertTrue(assigned, "Normal persona routing should still assign");

                    Mission? read = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(worker.Id, read!.CaptainId, "Mission should route to the persona-eligible idle captain");
                    AssertNull(read.RequestedCaptainId, "No preferred captain should be recorded when there is no default");
                }
            }));

            return new TestSuiteDescriptor(SuiteId, "Captain routing and per-step selection", cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        private static async Task<RoutingHarness> BuildHarnessAsync(SqliteDatabaseDriver db)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;

            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_routing_docks_" + Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_routing_repos_" + Guid.NewGuid().ToString("N"));

            DirCreatingGitStub git = new DirCreatingGitStub();
            IDockService dockService = new DockService(logging, db, settings, git);
            CaptainService captainService = new CaptainService(logging, db, settings, git, dockService);
            MissionService missionService = new MissionService(logging, db, settings, dockService, captainService, git: git);

            int nextPid = 5000;
            captainService.OnLaunchAgent = (_, _, _) =>
            {
                nextPid++;
                return Task.FromResult(nextPid);
            };
            missionService.OnGetMissionOutput = _ => "routing output";

            Vessel vessel = new Vessel("routing-vessel", "https://github.com/test/repo.git");
            vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_routing_bare_" + Guid.NewGuid().ToString("N"));
            vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_routing_work_" + Guid.NewGuid().ToString("N"));
            vessel.DefaultBranch = "main";
            vessel = await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);

            return new RoutingHarness { Missions = missionService, Vessel = vessel };
        }

        private static async Task<Captain> AddCaptainAsync(SqliteDatabaseDriver db, string name, CaptainStateEnum state, string? allowedPersonasJson, CaptainTierEnum? tier)
        {
            Captain captain = new Captain(name);
            captain.State = state;
            captain.AllowedPersonas = allowedPersonasJson;
            captain.Tier = tier;
            return await db.Captains.CreateAsync(captain).ConfigureAwait(false);
        }

        private static async Task<Mission> AddMissionAsync(SqliteDatabaseDriver db, Vessel vessel, string persona, string? requestedCaptainId, CaptainTierEnum? tier)
        {
            Mission mission = new Mission("route " + persona, "do the work");
            mission.VesselId = vessel.Id;
            mission.Persona = persona;
            mission.RequestedCaptainId = requestedCaptainId;
            mission.Tier = tier;
            mission.Status = MissionStatusEnum.Pending;
            return await db.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        #endregion

        #region Private-Types

        private sealed class RoutingHarness
        {
            public MissionService Missions { get; set; } = null!;
            public Vessel Vessel { get; set; } = null!;
        }

        /// <summary>
        /// Git stub that creates worktree directories so mission instructions can be written during dock
        /// provisioning, and treats every operation as a no-op success otherwise.
        /// </summary>
        private sealed class DirCreatingGitStub : IGitService
        {
            private readonly HashSet<string> _Branches = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "main" };

            public Task CloneBareAsync(string repoUrl, string localPath, CancellationToken token = default) => Task.CompletedTask;

            public Task CreateWorktreeAsync(string repoPath, string worktreePath, string branchName, string baseBranch = "main", CancellationToken token = default)
            {
                Directory.CreateDirectory(worktreePath);
                _Branches.Add(branchName);
                return Task.CompletedTask;
            }

            public Task RemoveWorktreeAsync(string worktreePath, CancellationToken token = default) => Task.CompletedTask;
            public Task FetchAsync(string repoPath, CancellationToken token = default) => Task.CompletedTask;
            public Task PushBranchAsync(string worktreePath, string remoteName = "origin", CancellationToken token = default) => Task.CompletedTask;
            public Task<string> CreatePullRequestAsync(string worktreePath, string title, string body, CancellationToken token = default)
                => Task.FromResult("https://github.com/test/repo/pull/1");
            public Task RepairWorktreeAsync(string worktreePath, CancellationToken token = default) => Task.CompletedTask;
            public Task<bool> IsRepositoryAsync(string path, CancellationToken token = default) => Task.FromResult(true);
            public Task DeleteLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;
            public Task DeleteRemoteBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;
            public Task PruneWorktreesAsync(string repoPath, CancellationToken token = default) => Task.CompletedTask;
            public Task EnableAutoMergeAsync(string worktreePath, string prUrl, CancellationToken token = default) => Task.CompletedTask;
            public Task MergeBranchLocalAsync(string targetWorkDir, string sourceRepoPath, string branchName, string? targetBranch = null, string? commitMessage = null, CancellationToken token = default) => Task.CompletedTask;
            public Task PullAsync(string workingDirectory, CancellationToken token = default) => Task.CompletedTask;
            public Task<string> DiffAsync(string worktreePath, string baseBranch = "main", CancellationToken token = default) => Task.FromResult(String.Empty);
            public Task<IReadOnlyList<string>> GetChangedFilesSinceAsync(string worktreePath, string startCommit, CancellationToken token = default)
                => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            public Task<IReadOnlyList<string>> GetConflictedFilesAsync(string worktreePath, CancellationToken token = default)
                => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            public Task<bool> IsPrMergedAsync(string workingDirectory, string prUrl, CancellationToken token = default) => Task.FromResult(true);
            public Task<string?> GetHeadCommitHashAsync(string worktreePath, CancellationToken token = default) => Task.FromResult<string?>("abc123def456");
            public Task<bool> BranchExistsAsync(string repoPath, string branchName, CancellationToken token = default)
                => Task.FromResult(_Branches.Contains(branchName));
            public Task<bool> EnsureLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default)
                => BranchExistsAsync(repoPath, branchName, token);
            public Task<bool> IsWorktreeRegisteredAsync(string repoPath, string worktreePath, CancellationToken token = default) => Task.FromResult(false);
        }

        #endregion
    }
}
