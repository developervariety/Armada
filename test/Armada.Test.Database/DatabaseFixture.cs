namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    internal class DatabaseFixture
    {
        private readonly DatabaseDriver _Driver;
        private readonly bool _NoCleanup;
        private readonly Stack<Func<CancellationToken, Task>> _Cleanup = new Stack<Func<CancellationToken, Task>>();

        public DatabaseFixture(DatabaseDriver driver, bool noCleanup)
        {
            _Driver = driver;
            _NoCleanup = noCleanup;
        }

        public async Task<TenantMetadata> CreateTenantAsync(string namePrefix, bool isProtected = false, CancellationToken token = default)
        {
            TenantMetadata tenant = new TenantMetadata(namePrefix + "-" + Token())
            {
                Active = true,
                IsProtected = isProtected
            };

            await _Driver.Tenants.CreateAsync(tenant, token).ConfigureAwait(false);
            RegisterCleanup(async ct => await _Driver.Tenants.DeleteAsync(tenant.Id, ct).ConfigureAwait(false));
            return tenant;
        }

        public async Task<UserMaster> CreateUserAsync(string tenantId, string emailPrefix, bool isAdmin = false, bool isTenantAdmin = false, bool isProtected = false, CancellationToken token = default)
        {
            UserMaster user = new UserMaster(tenantId, emailPrefix + "-" + Token() + "@example.com", "password")
            {
                FirstName = "Test",
                LastName = "User",
                IsAdmin = isAdmin,
                IsTenantAdmin = isTenantAdmin,
                IsProtected = isProtected,
                Active = true
            };

            await _Driver.Users.CreateAsync(user, token).ConfigureAwait(false);
            RegisterCleanup(async ct => await _Driver.Users.DeleteAsync(tenantId, user.Id, ct).ConfigureAwait(false));
            return user;
        }

        public async Task<Credential> CreateCredentialAsync(string tenantId, string userId, string namePrefix, bool active = true, bool isProtected = false, CancellationToken token = default)
        {
            Credential credential = new Credential(tenantId, userId)
            {
                Name = namePrefix + "-" + Token(),
                Active = active,
                IsProtected = isProtected
            };

            await _Driver.Credentials.CreateAsync(credential, token).ConfigureAwait(false);
            RegisterCleanup(async ct => await _Driver.Credentials.DeleteAsync(tenantId, credential.Id, ct).ConfigureAwait(false));
            return credential;
        }

        public async Task<Fleet> CreateFleetAsync(string tenantId, string userId, string namePrefix, CancellationToken token = default)
        {
            Fleet fleet = new Fleet(namePrefix + "-" + Token())
            {
                TenantId = tenantId,
                UserId = userId,
                Description = "Fleet description for " + namePrefix,
                Active = true
            };

            await _Driver.Fleets.CreateAsync(fleet, token).ConfigureAwait(false);
            RegisterCleanup(async ct => await _Driver.Fleets.DeleteAsync(fleet.Id, ct).ConfigureAwait(false));
            return fleet;
        }

        public async Task<Vessel> CreateVesselAsync(string tenantId, string userId, string fleetId, string namePrefix, CancellationToken token = default)
        {
            Vessel vessel = new Vessel(namePrefix + "-" + Token(), "https://github.com/example/" + namePrefix + ".git")
            {
                TenantId = tenantId,
                UserId = userId,
                FleetId = fleetId,
                DefaultBranch = "main",
                Active = true
            };

            await _Driver.Vessels.CreateAsync(vessel, token).ConfigureAwait(false);
            RegisterCleanup(async ct => await _Driver.Vessels.DeleteAsync(vessel.Id, ct).ConfigureAwait(false));
            return vessel;
        }

        public async Task<Captain> CreateCaptainAsync(string tenantId, string userId, string namePrefix, CancellationToken token = default, string model = null)
        {
            Captain captain = new Captain(namePrefix + "-" + Token(), AgentRuntimeEnum.Codex)
            {
                TenantId = tenantId,
                UserId = userId,
                State = CaptainStateEnum.Idle,
                Model = model
            };

            await _Driver.Captains.CreateAsync(captain, token).ConfigureAwait(false);
            RegisterCleanup(async ct => await _Driver.Captains.DeleteAsync(captain.Id, ct).ConfigureAwait(false));
            return captain;
        }

        public async Task<Voyage> CreateVoyageAsync(string tenantId, string userId, string titlePrefix, CancellationToken token = default)
        {
            Voyage voyage = new Voyage(titlePrefix + "-" + Token(), "Voyage description")
            {
                TenantId = tenantId,
                UserId = userId,
                Status = VoyageStatusEnum.Open
            };

            await _Driver.Voyages.CreateAsync(voyage, token).ConfigureAwait(false);
            RegisterCleanup(async ct => await _Driver.Voyages.DeleteAsync(voyage.Id, ct).ConfigureAwait(false));
            return voyage;
        }

        public async Task<Mission> CreateMissionAsync(string tenantId, string userId, string voyageId, string vesselId, string captainId, string titlePrefix, CancellationToken token = default, DateTime? startedUtc = null, DateTime? completedUtc = null)
        {
            Mission mission = new Mission(titlePrefix + "-" + Token(), "Mission description")
            {
                TenantId = tenantId,
                UserId = userId,
                VoyageId = voyageId,
                VesselId = vesselId,
                CaptainId = captainId,
                Status = MissionStatusEnum.Pending,
                Priority = 10,
                BranchName = "feature/" + Token(),
                StartedUtc = startedUtc,
                CompletedUtc = completedUtc
            };

            await _Driver.Missions.CreateAsync(mission, token).ConfigureAwait(false);
            RegisterCleanup(async ct => await _Driver.Missions.DeleteAsync(mission.Id, ct).ConfigureAwait(false));
            return mission;
        }

        public async Task<Dock> CreateDockAsync(string tenantId, string userId, string vesselId, string captainId, CancellationToken token = default)
        {
            Dock dock = new Dock(vesselId)
            {
                TenantId = tenantId,
                UserId = userId,
                CaptainId = captainId,
                BranchName = "main",
                WorktreePath = "/tmp/" + Token(),
                Active = true
            };

            await _Driver.Docks.CreateAsync(dock, token).ConfigureAwait(false);
            RegisterCleanup(async ct => await _Driver.Docks.DeleteAsync(dock.Id, ct).ConfigureAwait(false));
            return dock;
        }

        public async Task<Signal> CreateSignalAsync(string tenantId, string userId, string toCaptainId, CancellationToken token = default)
        {
            Signal signal = new Signal(SignalTypeEnum.Nudge, "{\"message\":\"hello\"}")
            {
                TenantId = tenantId,
                UserId = userId,
                ToCaptainId = toCaptainId,
                Read = false
            };

            await _Driver.Signals.CreateAsync(signal, token).ConfigureAwait(false);
            RegisterCleanup(async ct => await _Driver.Signals.DeleteAsync(signal.Id, ct).ConfigureAwait(false));
            return signal;
        }

        public async Task<ArmadaEvent> CreateEventAsync(string tenantId, string userId, string missionId, string voyageId, string vesselId, string captainId, CancellationToken token = default)
        {
            ArmadaEvent evt = new ArmadaEvent("mission.created", "Mission created for integration test")
            {
                TenantId = tenantId,
                UserId = userId,
                MissionId = missionId,
                VoyageId = voyageId,
                VesselId = vesselId,
                CaptainId = captainId,
                EntityType = "mission",
                EntityId = missionId,
                Payload = "{\"kind\":\"test\"}"
            };

            await _Driver.Events.CreateAsync(evt, token).ConfigureAwait(false);
            RegisterCleanup(async ct => await _Driver.Events.DeleteAsync(evt.Id, ct).ConfigureAwait(false));
            return evt;
        }

        public async Task<MergeEntry> CreateMergeEntryAsync(string tenantId, string userId, string missionId, string vesselId, CancellationToken token = default)
        {
            MergeEntry entry = new MergeEntry("feature/" + Token())
            {
                TenantId = tenantId,
                UserId = userId,
                MissionId = missionId,
                VesselId = vesselId,
                Status = MergeStatusEnum.Queued,
                Priority = 5,
                TestCommand = "dotnet test"
            };

            await _Driver.MergeEntries.CreateAsync(entry, token).ConfigureAwait(false);
            RegisterCleanup(async ct => await _Driver.MergeEntries.DeleteAsync(entry.Id, ct).ConfigureAwait(false));
            return entry;
        }

        public async Task<WorkflowProfile> CreateWorkflowProfileAsync(
            string tenantId,
            string userId,
            string namePrefix,
            string? fleetId = null,
            string? vesselId = null,
            CancellationToken token = default)
        {
            WorkflowProfile profile = new WorkflowProfile
            {
                TenantId = tenantId,
                UserId = userId,
                Name = namePrefix + "-" + Token(),
                Description = "Workflow profile for " + namePrefix,
                Scope = vesselId != null ? WorkflowProfileScopeEnum.Vessel : fleetId != null ? WorkflowProfileScopeEnum.Fleet : WorkflowProfileScopeEnum.Global,
                FleetId = fleetId,
                VesselId = vesselId,
                IsDefault = true,
                Active = true,
                BuildCommand = "dotnet build",
                UnitTestCommand = "dotnet test",
                ExpectedArtifacts = new List<string> { "artifacts/package.zip" },
                RequiredInputs = new List<WorkflowInputReference>
                {
                    new WorkflowInputReference
                    {
                        Provider = WorkflowInputReferenceProviderEnum.EnvironmentVariable,
                        Key = "API_TOKEN",
                        Description = "Token required for publishing"
                    }
                },
                Environments = new List<WorkflowEnvironmentProfile>
                {
                    new WorkflowEnvironmentProfile
                    {
                        EnvironmentName = "staging",
                        DeployCommand = "deploy staging",
                        SmokeTestCommand = "smoke staging"
                    }
                }
            };

            await _Driver.WorkflowProfiles.CreateAsync(profile, token).ConfigureAwait(false);
            RegisterCleanup(async ct => await _Driver.WorkflowProfiles.DeleteAsync(profile.Id, null, ct).ConfigureAwait(false));
            return profile;
        }

        public async Task<CheckRun> CreateCheckRunAsync(
            string tenantId,
            string userId,
            string vesselId,
            string? workflowProfileId = null,
            string? missionId = null,
            string? voyageId = null,
            CancellationToken token = default)
        {
            CheckRun run = new CheckRun
            {
                TenantId = tenantId,
                UserId = userId,
                WorkflowProfileId = workflowProfileId,
                VesselId = vesselId,
                MissionId = missionId,
                VoyageId = voyageId,
                Label = "Fixture Check " + Token(),
                Type = CheckRunTypeEnum.Build,
                Source = CheckRunSourceEnum.Armada,
                Status = CheckRunStatusEnum.Passed,
                Command = "echo fixture",
                WorkingDirectory = "C:/temp",
                ExitCode = 0,
                Output = "Build succeeded.",
                Summary = "Fixture check passed.",
                DurationMs = 1250,
                StartedUtc = DateTime.UtcNow.AddSeconds(-2),
                CompletedUtc = DateTime.UtcNow.AddSeconds(-1),
                Artifacts = new List<CheckRunArtifact>
                {
                    new CheckRunArtifact
                    {
                        Path = "artifacts/check.txt",
                        SizeBytes = 12,
                        LastWriteUtc = DateTime.UtcNow
                    }
                }
            };

            await _Driver.CheckRuns.CreateAsync(run, token).ConfigureAwait(false);
            RegisterCleanup(async ct => await _Driver.CheckRuns.DeleteAsync(run.Id, null, ct).ConfigureAwait(false));
            return run;
        }

        public async Task<Release> CreateReleaseAsync(
            string tenantId,
            string userId,
            string vesselId,
            string? workflowProfileId = null,
            IEnumerable<string>? voyageIds = null,
            IEnumerable<string>? missionIds = null,
            IEnumerable<string>? checkRunIds = null,
            CancellationToken token = default)
        {
            Release release = new Release
            {
                TenantId = tenantId,
                UserId = userId,
                VesselId = vesselId,
                WorkflowProfileId = workflowProfileId,
                Title = "Fixture Release " + Token(),
                Version = "1.2.3",
                TagName = "v1.2.3",
                Summary = "Fixture release summary",
                Notes = "Fixture release notes",
                Status = ReleaseStatusEnum.Candidate,
                VoyageIds = voyageIds?.ToList() ?? new List<string>(),
                MissionIds = missionIds?.ToList() ?? new List<string>(),
                CheckRunIds = checkRunIds?.ToList() ?? new List<string>(),
                Artifacts = new List<ReleaseArtifact>
                {
                    new ReleaseArtifact
                    {
                        SourceType = "CheckRun",
                        SourceId = checkRunIds?.FirstOrDefault(),
                        Path = "artifacts/package.zip",
                        SizeBytes = 2048
                    }
                }
            };

            await _Driver.Releases.CreateAsync(release, token).ConfigureAwait(false);
            RegisterCleanup(async ct => await _Driver.Releases.DeleteAsync(release.Id, null, ct).ConfigureAwait(false));
            return release;
        }

        public async Task<DeploymentEnvironment> CreateDeploymentEnvironmentAsync(
            string tenantId,
            string userId,
            string vesselId,
            string namePrefix,
            EnvironmentKindEnum kind = EnvironmentKindEnum.Staging,
            bool isDefault = false,
            CancellationToken token = default)
        {
            DeploymentEnvironment environment = new DeploymentEnvironment
            {
                TenantId = tenantId,
                UserId = userId,
                VesselId = vesselId,
                Name = namePrefix + "-" + Token(),
                Description = "Fixture environment for " + namePrefix,
                Kind = kind,
                ConfigurationSource = "config/" + namePrefix + ".json",
                BaseUrl = "https://" + namePrefix + ".example.test",
                HealthEndpoint = "/health",
                AccessNotes = "Use standard fixture credentials",
                DeploymentRules = "Deploy after validation",
                RequiresApproval = kind == EnvironmentKindEnum.Production,
                IsDefault = isDefault,
                Active = true
            };

            await _Driver.Environments.CreateAsync(environment, token).ConfigureAwait(false);
            RegisterCleanup(async ct => await _Driver.Environments.DeleteAsync(environment.Id, null, ct).ConfigureAwait(false));
            return environment;
        }

        public async Task<Deployment> CreateDeploymentAsync(
            string tenantId,
            string userId,
            string vesselId,
            string environmentId,
            string environmentName,
            string? workflowProfileId = null,
            string? releaseId = null,
            string? missionId = null,
            string? voyageId = null,
            CancellationToken token = default)
        {
            Deployment deployment = new Deployment
            {
                TenantId = tenantId,
                UserId = userId,
                VesselId = vesselId,
                WorkflowProfileId = workflowProfileId,
                EnvironmentId = environmentId,
                EnvironmentName = environmentName,
                ReleaseId = releaseId,
                MissionId = missionId,
                VoyageId = voyageId,
                Title = "Fixture Deployment " + Token(),
                SourceRef = "main",
                Summary = "Fixture deployment summary",
                Notes = "Fixture deployment notes",
                Status = DeploymentStatusEnum.Succeeded,
                VerificationStatus = DeploymentVerificationStatusEnum.Passed,
                ApprovalRequired = false,
                ApprovedByUserId = userId,
                ApprovedUtc = DateTime.UtcNow.AddMinutes(-4),
                DeployCheckRunId = null,
                SmokeTestCheckRunId = null,
                HealthCheckRunId = null,
                DeploymentVerificationCheckRunId = null,
                RollbackCheckRunId = null,
                RollbackVerificationCheckRunId = null,
                CreatedUtc = DateTime.UtcNow.AddMinutes(-5),
                StartedUtc = DateTime.UtcNow.AddMinutes(-4),
                CompletedUtc = DateTime.UtcNow.AddMinutes(-3),
                VerifiedUtc = DateTime.UtcNow.AddMinutes(-3),
                LastUpdateUtc = DateTime.UtcNow.AddMinutes(-3)
            };

            await _Driver.Deployments.CreateAsync(deployment, token).ConfigureAwait(false);
            RegisterCleanup(async ct => await _Driver.Deployments.DeleteAsync(deployment.Id, null, ct).ConfigureAwait(false));
            return deployment;
        }

        public async Task<Objective> CreateObjectiveAsync(
            string tenantId,
            string userId,
            string titlePrefix,
            string? parentObjectiveId = null,
            IEnumerable<string>? vesselIds = null,
            CancellationToken token = default)
        {
            Objective objective = new Objective
            {
                TenantId = tenantId,
                UserId = userId,
                Title = titlePrefix + "-" + Token(),
                Description = "Objective fixture for " + titlePrefix,
                Status = ObjectiveStatusEnum.Scoped,
                Kind = ObjectiveKindEnum.Feature,
                Category = "Fixture",
                Priority = ObjectivePriorityEnum.P1,
                Rank = 25,
                BacklogState = ObjectiveBacklogStateEnum.Triaged,
                Effort = ObjectiveEffortEnum.M,
                Owner = "fixture-owner",
                TargetVersion = "0.8.0",
                ParentObjectiveId = parentObjectiveId,
                BlockedByObjectiveIds = new List<string>(),
                RefinementSummary = "Fixture summary",
                SuggestedPipelineId = null,
                Tags = new List<string> { "fixture", "objective" },
                AcceptanceCriteria = new List<string> { "Create", "Read", "Update" },
                NonGoals = new List<string> { "No legacy API changes" },
                RolloutConstraints = new List<string> { "Validate in staging" },
                EvidenceLinks = new List<string> { "https://example.test/objective-fixture" },
                VesselIds = vesselIds?.ToList() ?? new List<string>(),
                CreatedUtc = DateTime.UtcNow.AddMinutes(-2),
                LastUpdateUtc = DateTime.UtcNow.AddMinutes(-1)
            };

            await _Driver.Objectives.CreateAsync(objective, token).ConfigureAwait(false);
            RegisterCleanup(async ct => await _Driver.Objectives.DeleteAsync(objective.Id, ct).ConfigureAwait(false));
            return objective;
        }

        public async Task<ObjectiveRefinementSession> CreateObjectiveRefinementSessionAsync(
            string tenantId,
            string userId,
            string objectiveId,
            string captainId,
            string? vesselId = null,
            ObjectiveRefinementSessionStatusEnum status = ObjectiveRefinementSessionStatusEnum.Active,
            CancellationToken token = default)
        {
            ObjectiveRefinementSession session = new ObjectiveRefinementSession
            {
                ObjectiveId = objectiveId,
                TenantId = tenantId,
                UserId = userId,
                CaptainId = captainId,
                VesselId = vesselId,
                Title = "Refine " + Token(),
                Status = status,
                StartedUtc = DateTime.UtcNow.AddMinutes(-1),
                CreatedUtc = DateTime.UtcNow.AddMinutes(-2),
                LastUpdateUtc = DateTime.UtcNow
            };

            await _Driver.ObjectiveRefinementSessions.CreateAsync(session, token).ConfigureAwait(false);
            RegisterCleanup(async ct => await _Driver.ObjectiveRefinementSessions.DeleteAsync(session.Id, ct).ConfigureAwait(false));
            return session;
        }

        public async Task<ObjectiveRefinementMessage> CreateObjectiveRefinementMessageAsync(
            string sessionId,
            string objectiveId,
            string tenantId,
            string userId,
            string role,
            int sequence,
            string content,
            bool isSelected = false,
            CancellationToken token = default)
        {
            ObjectiveRefinementMessage message = new ObjectiveRefinementMessage
            {
                ObjectiveRefinementSessionId = sessionId,
                ObjectiveId = objectiveId,
                TenantId = tenantId,
                UserId = userId,
                Role = role,
                Sequence = sequence,
                Content = content,
                IsSelected = isSelected,
                CreatedUtc = DateTime.UtcNow.AddMinutes(-1),
                LastUpdateUtc = DateTime.UtcNow
            };

            await _Driver.ObjectiveRefinementMessages.CreateAsync(message, token).ConfigureAwait(false);
            RegisterCleanup(async ct => await _Driver.ObjectiveRefinementMessages.DeleteAsync(message.Id, ct).ConfigureAwait(false));
            return message;
        }

        public async Task CleanupAsync(CancellationToken token = default)
        {
            if (_NoCleanup) return;

            while (_Cleanup.Count > 0)
            {
                Func<CancellationToken, Task> cleanup = _Cleanup.Pop();
                try
                {
                    await cleanup(token).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }

        private void RegisterCleanup(Func<CancellationToken, Task> cleanup)
        {
            if (!_NoCleanup) _Cleanup.Push(cleanup);
        }

        private static string Token()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 8);
        }
    }
}
