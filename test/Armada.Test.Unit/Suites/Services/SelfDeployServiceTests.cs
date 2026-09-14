namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests for self-deploy build gating, fail-closed preconditions, incident creation, and the supervised
    /// cutover handshake on the running admiral side.
    /// </summary>
    public class SelfDeployServiceTests : TestSuite
    {
        public override string Name => "Self Deploy Service";

        protected override async Task RunTestsAsync()
        {
            await RunTest("ExecuteAsync_Disabled_SkipsDeploy", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                using (SelfDeployTestContext context = await CreateContextAsync(testDb, enabled: false))
                {
                    bool restarted = await context.Service.ExecuteAsync(context.Vessel.Id, "mrg_test", "test land");
                    AssertFalse(restarted, "restarted");
                    AssertEqual(0, context.Host.Starts.Count, "process launches");
                    AssertEqual(0, context.BuildRunner.Calls.Count, "build calls");
                }
            });

            await RunTest("ExecuteAsync_NonSelfVessel_SkipsDeploy", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                using (SelfDeployTestContext context = await CreateContextAsync(testDb, enabled: true))
                {
                    bool restarted = await context.Service.ExecuteAsync("vsl_other", "mrg_test", "test land");
                    AssertFalse(restarted, "restarted");
                    AssertEqual(0, context.BuildRunner.Calls.Count, "build calls");
                }
            });

            await RunTest("ExecuteAsync_BuildFails_AbortsRestart_OpensIncident", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                using (SelfDeployTestContext context = await CreateContextAsync(testDb, enabled: true))
                {
                    context.BuildRunner.NextResult = new SelfDeployBuildResult
                    {
                        Succeeded = false,
                        ExitCode = 1,
                        OutputTail = "error CS0000"
                    };

                    bool restarted = await context.Service.ExecuteAsync(context.Vessel.Id, "mrg_test", "test land");
                    AssertFalse(restarted, "restarted");
                    AssertEqual(0, context.Host.Starts.Count, "process launches");
                    AssertEqual(1, context.BuildRunner.Calls.Count, "build calls");
                    Incident incident = await LatestIncidentAsync(testDb, context);
                    AssertContains("Self-deploy blocked", incident.Title);
                }
            });

            await RunTest("ExecuteAsync_PreflightPasses_ArmedSupervisorReceivesDurableExitRequest", async () =>
            {
                if (SkipWindows("ExecuteAsync_PreflightPasses_ArmedSupervisorReceivesDurableExitRequest")) return;
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                using (SelfDeployTestContext context = await CreateContextAsync(testDb, enabled: true))
                {
                    context.Host.SupervisorBehavior = SupervisorBehaviorEnum.Arm;

                    bool restarted = await context.Service.ExecuteAsync(context.Vessel.Id, "mrg_test", "test land");

                    AssertTrue(restarted, "restarted");
                    AssertEqual(1, context.Preflight.Calls.Count, "preflight calls");
                    AssertEqual(1, context.Host.Starts.Count, "only the supervisor launched");
                    AssertEqual(context.Artifacts.Rollback.Digest, context.Planner.SupervisorArtifacts[0], "supervisor runs from the immutable rollback artifact");
                    SelfDeployRestartRecord record = await ReadRecordAsync(context);
                    AssertEqual(SelfDeployRestartStateEnum.ExitRequested, record.State, "admiral committed to exit only after arm");
                    AssertEqual(context.Host.AdmiralIdentity.ProcessId, record.OldProcess!.ProcessId, "old admiral recorded by pid");
                    AssertEqual(context.Host.AdmiralIdentity.StartedUtc, record.OldProcess.StartedUtc, "old admiral recorded by start time");
                    AssertEqual(context.Artifacts.Candidate.Digest, record.Candidate.Digest, "candidate artifact recorded");
                    AssertEqual(context.Artifacts.Rollback.Digest, record.Rollback.Digest, "rollback artifact recorded");
                    AssertEqual(2, context.Artifacts.CaptureOrder.Count, "two captures");
                    AssertEqual("rollback", context.Artifacts.CaptureOrder[0], "rollback captured first");
                }
            });

            await RunTest("ExecuteAsync_ContainerHost_FailsClosedBeforeBuild", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                using (SelfDeployTestContext context = await CreateContextAsync(testDb, enabled: true))
                {
                    context.Environment.IsContainer = true;
                    bool restarted = await context.Service.ExecuteAsync(context.Vessel.Id, "mrg_test", "test land");
                    AssertFalse(restarted, "restarted");
                    AssertEqual(0, context.BuildRunner.Calls.Count, "build calls");
                    AssertEqual(0, context.Host.Starts.Count, "process launches");
                    Incident incident = await LatestIncidentAsync(testDb, context);
                    AssertEqual("container_host_requires_external_deploy", incident.RootCause, "incident reason");
                }
            });

            await RunTest("ExecuteAsync_RollbackCaptureFails_FailsClosedBeforeBuild", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                using (SelfDeployTestContext context = await CreateContextAsync(testDb, enabled: true))
                {
                    context.Artifacts.FailRole = "rollback";
                    bool restarted = await context.Service.ExecuteAsync(context.Vessel.Id, "mrg_test", "test land");
                    AssertFalse(restarted, "restarted");
                    AssertEqual(0, context.BuildRunner.Calls.Count, "no build without a rollback target");
                    AssertEqual(0, context.Host.Starts.Count, "process launches");
                    Incident incident = await LatestIncidentAsync(testDb, context);
                    AssertEqual("rollback_artifact_copy_failed", incident.RootCause, "incident reason");
                }
            });

            await RunTest("ExecuteAsync_CandidateCaptureFails_NoSupervisorStarted", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                using (SelfDeployTestContext context = await CreateContextAsync(testDb, enabled: true))
                {
                    context.Artifacts.FailRole = "candidate";
                    bool restarted = await context.Service.ExecuteAsync(context.Vessel.Id, "mrg_test", "test land");
                    AssertFalse(restarted, "restarted");
                    AssertEqual(0, context.Host.Starts.Count, "process launches");
                    AssertFalse(File.Exists(context.Records.RecordPath), "no restart record written");
                }
            });

            await RunTest("ExecuteAsync_SupervisorNeverArms_AbortsAndStopsSupervisor", async () =>
            {
                if (SkipWindows("ExecuteAsync_SupervisorNeverArms_AbortsAndStopsSupervisor")) return;
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                using (SelfDeployTestContext context = await CreateContextAsync(testDb, enabled: true))
                {
                    context.Host.SupervisorBehavior = SupervisorBehaviorEnum.Silent;
                    bool restarted = await context.Service.ExecuteAsync(context.Vessel.Id, "mrg_test", "test land");
                    AssertFalse(restarted, "restarted");
                    SelfDeployRestartRecord record = await ReadRecordAsync(context);
                    AssertEqual(SelfDeployRestartStateEnum.Aborted, record.State, "record aborted");
                    AssertEqual("supervisor_arm_timeout", record.Reason, "abort reason");
                    AssertEqual(1, context.Host.Terminations.Count, "silent supervisor stopped");
                    AssertEqual(context.Host.SupervisorIdentity.ProcessId, context.Host.Terminations[0].ProcessId, "stopped identity is the supervisor");
                    AssertEqual(0, context.ProcessExit.Calls, "admiral stays running");
                }
            });

            await RunTest("ExecuteAsync_SupervisorRefusesArtifacts_AdmiralKeepsRunning", async () =>
            {
                if (SkipWindows("ExecuteAsync_SupervisorRefusesArtifacts_AdmiralKeepsRunning")) return;
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                using (SelfDeployTestContext context = await CreateContextAsync(testDb, enabled: true))
                {
                    context.Host.SupervisorBehavior = SupervisorBehaviorEnum.Abort;
                    bool restarted = await context.Service.ExecuteAsync(context.Vessel.Id, "mrg_test", "test land");
                    AssertFalse(restarted, "restarted");
                    SelfDeployRestartRecord record = await ReadRecordAsync(context);
                    AssertEqual(SelfDeployRestartStateEnum.Aborted, record.State, "supervisor aborted before any stop");
                    Incident incident = await LatestIncidentAsync(testDb, context);
                    AssertEqual("supervisor_candidate_artifact_digest_mismatch", incident.RootCause, "incident carries the supervisor reason");
                    AssertEqual(0, context.ProcessExit.Calls, "admiral stays running");
                }
            });

            await RunTest("ExecuteAsync_RestartAlreadyInProgress_FailsClosed", async () =>
            {
                if (SkipWindows("ExecuteAsync_RestartAlreadyInProgress_FailsClosed")) return;
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                using (SelfDeployTestContext context = await CreateContextAsync(testDb, enabled: true))
                {
                    SelfDeployRestartRecord existing = new SelfDeployRestartRecord { OperationId = "sdo_existing", CreatedUtc = DateTime.UtcNow };
                    existing.MoveTo(SelfDeployRestartStateEnum.RollingBack, "earlier_operation");
                    await context.Records.CreateAsync(existing);

                    bool restarted = await context.Service.ExecuteAsync(context.Vessel.Id, "mrg_test", "test land");
                    AssertFalse(restarted, "restarted");
                    AssertEqual(0, context.Host.Starts.Count, "no supervisor while another restart is unresolved");
                    AssertEqual("sdo_existing", (await ReadRecordAsync(context)).OperationId, "existing record kept");
                    Incident incident = await LatestIncidentAsync(testDb, context);
                    AssertEqual("restart_in_progress", incident.RootCause, "incident reason");
                }
            });

            await RunTest("ExecuteAsync_AdmiralIdentityUnverified_FailsClosed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                using (SelfDeployTestContext context = await CreateContextAsync(testDb, enabled: true))
                {
                    context.Host.CaptureReturnsNull = true;
                    bool restarted = await context.Service.ExecuteAsync(context.Vessel.Id, "mrg_test", "test land");
                    AssertFalse(restarted, "restarted");
                    AssertEqual(0, context.Host.Starts.Count, "process launches");
                    Incident incident = await LatestIncidentAsync(testDb, context);
                    AssertEqual("admiral_identity_unverified", incident.RootCause, "incident reason");
                }
            });

            await RunTest("ExecuteAsync_BackupValidationFails_AbortsCutover", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                using (SelfDeployTestContext context = await CreateContextAsync(testDb, enabled: true))
                {
                    context.Preflight.NextResult = new SelfDeployPreflightResult
                    {
                        RestoreVerified = true,
                        CandidateValidated = true,
                        FailureReason = "backup_failed"
                    };

                    await AssertPreflightAbortsAsync(context, 1);
                }
            });

            await RunTest("ExecuteAsync_RestoreVerificationFails_AbortsCutover", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                using (SelfDeployTestContext context = await CreateContextAsync(testDb, enabled: true))
                {
                    context.Preflight.NextResult = new SelfDeployPreflightResult
                    {
                        BackupValidated = true,
                        CandidateValidated = true,
                        FailureReason = "restore_verification_failed"
                    };

                    await AssertPreflightAbortsAsync(context, 1);
                }
            });

            await RunTest("ExecuteAsync_CandidateValidationFails_AbortsCutover", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                using (SelfDeployTestContext context = await CreateContextAsync(testDb, enabled: true))
                {
                    context.Preflight.NextResult = new SelfDeployPreflightResult
                    {
                        BackupValidated = true,
                        RestoreVerified = true,
                        FailureReason = "candidate_validation_failed"
                    };

                    await AssertPreflightAbortsAsync(context, 1);
                }
            });

            await RunTest("ExecuteAsync_PreflightConflict_FailsClosed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                using (SelfDeployTestContext context = await CreateContextAsync(testDb, enabled: true))
                {
                    context.Preflight.NextResult = new SelfDeployPreflightResult
                    {
                        BackupValidated = true,
                        RestoreVerified = true,
                        CandidateValidated = true,
                        FailureReason = "provider_reported_conflict"
                    };

                    await AssertPreflightAbortsAsync(context, 1);
                }
            });

            await RunTest("ExecuteAsync_NullPreflightResult_FailsClosed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                using (SelfDeployTestContext context = await CreateContextAsync(testDb, enabled: true))
                {
                    context.Preflight.ReturnNull = true;
                    await AssertPreflightAbortsAsync(context, 1);
                }
            });

            await RunTest("ExecuteAsync_PreflightThrows_AbortsCutover", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                using (SelfDeployTestContext context = await CreateContextAsync(testDb, enabled: true))
                {
                    context.Preflight.ThrowOnValidate = true;

                    await AssertPreflightAbortsAsync(context, 1);

                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync("self_deploy.preflight_failed");
                    AssertEqual(1, events.Count, "preflight failure events");
                    AssertFalse((events[0].Payload ?? String.Empty).Contains("sentinel-secret", StringComparison.Ordinal), "event must not contain provider exception text");

                    Incident incident = await LatestIncidentAsync(testDb, context);
                    AssertFalse((incident.Summary ?? String.Empty).Contains("sentinel-secret", StringComparison.Ordinal), "incident must not contain provider exception text");
                }
            });

            await RunTest("ExecuteAsync_DefaultPreflight_FailsClosed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                using (SelfDeployTestContext context = await CreateContextAsync(testDb, enabled: true, includePreflight: false))
                {
                    bool restarted = await context.Service.ExecuteAsync(context.Vessel.Id, "mrg_test", "test land");
                    AssertFalse(restarted, "restarted");
                    AssertEqual(0, context.Host.Starts.Count, "process launches");
                    AssertEqual(1, context.Artifacts.CaptureOrder.Count, "only the rollback target was captured");
                }
            });

            await RunTest("ExecuteAsync_UnpushedLocalCommits_SkipsRestart", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                using (SelfDeployTestContext context = await CreateContextAsync(testDb, enabled: true))
                {
                    context.Git.AheadCount = 2;
                    context.Git.BehindCount = 0;

                    bool restarted = await context.Service.ExecuteAsync(context.Vessel.Id, "mrg_test", "test land");
                    AssertFalse(restarted, "restarted");
                    AssertEqual(0, context.BuildRunner.Calls.Count, "build calls");
                    AssertEqual(0, context.Host.Starts.Count, "process launches");
                }
            });
        }

        private async Task AssertPreflightAbortsAsync(SelfDeployTestContext context, int expectedPreflightCalls)
        {
            bool restarted = await context.Service.ExecuteAsync(context.Vessel.Id, "mrg_test", "test land");
            AssertFalse(restarted, "restarted");
            AssertEqual(expectedPreflightCalls, context.Preflight.Calls.Count, "preflight calls");
            AssertEqual(0, context.Host.Starts.Count, "no supervisor or server launched");
            AssertEqual(0, context.ProcessExit.Calls, "process exit calls");
            AssertFalse(File.Exists(context.Records.RecordPath), "no restart record written");
        }

        private bool SkipWindows(string testName)
        {
            if (!OperatingSystem.IsWindows()) return false;
            SkipTest(testName, "Self-deploy private storage fails closed on Windows.");
            return true;
        }

        private static async Task<Incident> LatestIncidentAsync(TestDatabase testDb, SelfDeployTestContext context)
        {
            IncidentService incidents = new IncidentService(testDb.Driver);
            AuthContext auth = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, true, true, "Test");
            EnumerationResult<Incident> page = await incidents.EnumerateAsync(auth, new IncidentQuery
            {
                VesselId = context.Vessel.Id,
                PageNumber = 1,
                PageSize = 10
            });
            if (page.Objects.Count < 1) throw new InvalidOperationException("expected a self-deploy incident");
            return page.Objects[0];
        }

        private static async Task<SelfDeployRestartRecord> ReadRecordAsync(SelfDeployTestContext context)
        {
            SelfDeployRestartRecordReadResult read = await context.Records.ReadAsync();
            if (!read.IsReadable) throw new InvalidOperationException("restart record unreadable: " + read.FailureReason);
            return read.Record!;
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static async Task<SelfDeployTestContext> CreateContextAsync(
            TestDatabase testDb,
            bool enabled,
            bool includePreflight = true)
        {
            SelfDeployTestDirectory directory = new SelfDeployTestDirectory();

            Vessel vessel = new Vessel("armada-self", "https://github.com/test/armada");
            vessel.WorkingDirectory = directory.Root;
            vessel.DefaultBranch = "main";
            await testDb.Driver.Vessels.CreateAsync(vessel);

            ArmadaSettings settings = new ArmadaSettings();
            settings.SelfDeploy.Enabled = enabled;
            settings.SelfDeploy.SelfVesselId = vessel.Id;
            settings.SelfDeploy.DebounceSeconds = 0;
            settings.SelfDeploy.MergeQueueDrainTimeoutSeconds = 5;
            settings.SelfDeploy.ServerDllRelativePath = "bin/Armada.Server.dll";

            SelfDeployRestartRecordStore records = new SelfDeployRestartRecordStore(Path.Combine(directory.Root, "state"));
            FakeProcessHost host = new FakeProcessHost(records);
            FakeArtifactStore artifacts = new FakeArtifactStore(directory.Root);
            RecordingLaunchPlanner planner = new RecordingLaunchPlanner();
            FakeHostEnvironment environment = new FakeHostEnvironment { CurrentServerDirectory = Path.Combine(directory.Root, "running") };
            SelfDeployCutoverComponents components = new SelfDeployCutoverComponents(host, artifacts, records, planner, environment,
                new SelfDeployCutoverOptions
                {
                    HandshakeTimeout = TimeSpan.FromMilliseconds(800),
                    TerminationTimeout = TimeSpan.FromSeconds(1),
                    PollInterval = TimeSpan.FromMilliseconds(20)
                });

            SelfDeployStubGitService git = new SelfDeployStubGitService();
            RecordingSelfDeployBuildRunner buildRunner = new RecordingSelfDeployBuildRunner();
            RecordingSelfDeployPreflight preflight = new RecordingSelfDeployPreflight();
            RecordingProcessExit processExit = new RecordingProcessExit();
            SelfDeployService service = includePreflight
                ? new SelfDeployService(CreateLogging(), testDb.Driver, settings, git, buildRunner, components, preflight, processExit.Request)
                : new SelfDeployService(CreateLogging(), testDb.Driver, settings, git, buildRunner, components, processExit.Request);

            return new SelfDeployTestContext(directory)
            {
                Vessel = vessel,
                Service = service,
                Git = git,
                BuildRunner = buildRunner,
                Preflight = preflight,
                ProcessExit = processExit,
                Host = host,
                Artifacts = artifacts,
                Planner = planner,
                Environment = environment,
                Records = records
            };
        }

        private enum SupervisorBehaviorEnum
        {
            Arm,
            Silent,
            Abort
        }

        private sealed class SelfDeployTestContext : IDisposable
        {
            private readonly SelfDeployTestDirectory _Directory;

            public SelfDeployTestContext(SelfDeployTestDirectory directory)
            {
                _Directory = directory;
            }

            public Vessel Vessel { get; set; } = null!;
            public SelfDeployService Service { get; set; } = null!;
            public SelfDeployStubGitService Git { get; set; } = null!;
            public RecordingSelfDeployBuildRunner BuildRunner { get; set; } = null!;
            public RecordingSelfDeployPreflight Preflight { get; set; } = null!;
            public RecordingProcessExit ProcessExit { get; set; } = null!;
            public FakeProcessHost Host { get; set; } = null!;
            public FakeArtifactStore Artifacts { get; set; } = null!;
            public RecordingLaunchPlanner Planner { get; set; } = null!;
            public FakeHostEnvironment Environment { get; set; } = null!;
            public SelfDeployRestartRecordStore Records { get; set; } = null!;

            public void Dispose()
            {
                _Directory.Dispose();
            }
        }

        private sealed class FakeProcessHost : ISelfDeployProcessHost
        {
            private readonly SelfDeployRestartRecordStore _Records;

            public FakeProcessHost(SelfDeployRestartRecordStore records)
            {
                _Records = records;
            }

            public SupervisorBehaviorEnum SupervisorBehavior { get; set; } = SupervisorBehaviorEnum.Arm;
            public bool CaptureReturnsNull { get; set; }
            public SelfDeployProcessIdentity AdmiralIdentity { get; } = new SelfDeployProcessIdentity { ProcessId = 4100, StartedUtc = DateTime.UtcNow.AddHours(-2) };
            public SelfDeployProcessIdentity SupervisorIdentity { get; } = new SelfDeployProcessIdentity { ProcessId = 4200, StartedUtc = DateTime.UtcNow };
            public List<SelfDeployLaunchSpec> Starts { get; } = new List<SelfDeployLaunchSpec>();
            public List<SelfDeployProcessIdentity> Terminations { get; } = new List<SelfDeployProcessIdentity>();

            public SelfDeployProcessIdentity? Capture(int processId) => CaptureReturnsNull ? null : AdmiralIdentity;

            public SelfDeployProcessStateEnum GetState(SelfDeployProcessIdentity identity) => SelfDeployProcessStateEnum.Running;

            public SelfDeployProcessIdentity Start(SelfDeployLaunchSpec spec)
            {
                Starts.Add(spec);
                string operationId = spec.EnvironmentVariables[SelfDeployRestartRecordStore.OperationIdVariable];
                if (SupervisorBehavior == SupervisorBehaviorEnum.Arm)
                {
                    _ = Task.Run(() => _Records.TryTransitionAsync(operationId, new[] { SelfDeployRestartStateEnum.Prepared },
                        r => r.MoveTo(SelfDeployRestartStateEnum.Armed, "supervisor_armed")));
                }
                else if (SupervisorBehavior == SupervisorBehaviorEnum.Abort)
                {
                    _ = Task.Run(() => _Records.TryTransitionAsync(operationId, new[] { SelfDeployRestartStateEnum.Prepared },
                        r => r.MoveTo(SelfDeployRestartStateEnum.Aborted, "candidate_artifact_digest_mismatch")));
                }
                return SupervisorIdentity;
            }

            public Task<bool> TerminateAsync(SelfDeployProcessIdentity identity, bool entireProcessTree, TimeSpan timeout, CancellationToken token = default)
            {
                Terminations.Add(identity);
                return Task.FromResult(true);
            }

            public Task<SelfDeployProcessStateEnum> WaitForExitAsync(SelfDeployProcessIdentity identity, TimeSpan timeout, TimeSpan pollInterval, CancellationToken token = default)
            {
                return Task.FromResult(SelfDeployProcessStateEnum.Exited);
            }
        }

        private sealed class FakeArtifactStore : ISelfDeployArtifactStore
        {
            private readonly string _Root;

            public FakeArtifactStore(string root)
            {
                _Root = root;
                Rollback = Artifact("rollback");
                Candidate = Artifact("candidate");
            }

            public string? FailRole { get; set; }
            public List<string> CaptureOrder { get; } = new List<string>();
            public SelfDeployReleaseArtifact Rollback { get; }
            public SelfDeployReleaseArtifact Candidate { get; }

            public Task<SelfDeployReleaseArtifact> CaptureAsync(string sourceDirectory, string entryAssembly, CancellationToken token = default)
            {
                string role = CaptureOrder.Count == 0 ? "rollback" : "candidate";
                CaptureOrder.Add(role);
                if (String.Equals(FailRole, role, StringComparison.Ordinal)) throw new SelfDeployCutoverException("artifact_copy_failed");
                return Task.FromResult(role == "rollback" ? Rollback : Candidate);
            }

            public Task<string?> VerifyAsync(SelfDeployReleaseArtifact artifact, CancellationToken token = default)
            {
                return Task.FromResult<string?>(null);
            }

            private SelfDeployReleaseArtifact Artifact(string role)
            {
                string digest = role + "0000000000000000000000000000000000000000000000000000";
                return new SelfDeployReleaseArtifact
                {
                    Digest = digest,
                    Directory = Path.Combine(_Root, "releases", digest),
                    EntryAssembly = "Armada.Server.dll",
                    FileCount = 1
                };
            }
        }

        private sealed class RecordingLaunchPlanner : ISelfDeployLaunchPlanner
        {
            private readonly SelfDeployDotnetLaunchPlanner _Inner = new SelfDeployDotnetLaunchPlanner();

            public List<string> SupervisorArtifacts { get; } = new List<string>();

            public SelfDeployLaunchSpec ForServer(SelfDeployReleaseArtifact artifact, string operationId)
            {
                throw new NotSupportedException("The running admiral never launches a server directly.");
            }

            public SelfDeployLaunchSpec ForSupervisor(SelfDeployReleaseArtifact rollback, string operationId)
            {
                SupervisorArtifacts.Add(rollback.Digest);
                return _Inner.ForSupervisor(rollback, operationId);
            }
        }

        private sealed class FakeHostEnvironment : ISelfDeployHostEnvironment
        {
            public bool IsContainer { get; set; }
            public string CurrentServerDirectory { get; set; } = String.Empty;
            public int CurrentProcessId { get; set; } = 4100;
        }

        private sealed class RecordingSelfDeployBuildRunner : ISelfDeployBuildRunner
        {
            public List<string> Calls { get; } = new List<string>();
            public SelfDeployBuildResult NextResult { get; set; } = new SelfDeployBuildResult
            {
                Succeeded = true,
                ExitCode = 0
            };

            public Task<SelfDeployBuildResult> BuildAsync(
                string workingDirectory,
                SelfDeploySettings settings,
                CancellationToken token = default)
            {
                Calls.Add(workingDirectory);
                return Task.FromResult(NextResult);
            }
        }

        private sealed class RecordingSelfDeployPreflight : ISelfDeployPreflight
        {
            public List<SelfDeployPreflightRequest> Calls { get; } = new List<SelfDeployPreflightRequest>();
            public bool ThrowOnValidate { get; set; }
            public bool ReturnNull { get; set; }
            public SelfDeployPreflightResult NextResult { get; set; } = new SelfDeployPreflightResult
            {
                BackupValidated = true,
                RestoreVerified = true,
                CandidateValidated = true
            };

            public Task<SelfDeployPreflightResult> ValidateAsync(
                SelfDeployPreflightRequest request,
                CancellationToken token = default)
            {
                Calls.Add(request);
                if (ThrowOnValidate) throw new InvalidOperationException("preflight failure sentinel-secret");
                if (ReturnNull) return Task.FromResult<SelfDeployPreflightResult>(null!);
                return Task.FromResult(NextResult);
            }
        }

        private sealed class RecordingProcessExit
        {
            public int Calls { get; private set; }

            public void Request()
            {
                Calls++;
            }
        }

        private sealed class SelfDeployStubGitService : IGitService
        {
            public int AheadCount { get; set; }
            public int BehindCount { get; set; }

            public Task<bool> IsRepositoryAsync(string path, CancellationToken token = default) => Task.FromResult(true);
            public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken token = default) => Task.FromResult<string?>("main");
            public Task<bool> IsWorkingDirectoryCleanAsync(string workingDirectory, CancellationToken token = default) => Task.FromResult(true);
            public Task<bool> HasUncommittedTrackedChangesAsync(string workingDirectory, CancellationToken token = default) => Task.FromResult(false);
            public Task FetchAsync(string repoPath, CancellationToken token = default) => Task.CompletedTask;
            public Task PullFastForwardOnlyAsync(string workingDirectory, CancellationToken token = default) => Task.CompletedTask;

            public Task<IReadOnlyList<string>> GetConflictedFilesAsync(string worktreePath, CancellationToken token = default)
                => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            public Task<int> GetCommitCountBetweenAsync(string repoPath, string fromRef, string toRef, CancellationToken token = default)
            {
                if (fromRef.StartsWith("origin/", StringComparison.Ordinal))
                {
                    return Task.FromResult(AheadCount);
                }

                return Task.FromResult(BehindCount);
            }

            public Task CloneBareAsync(string repoUrl, string localPath, CancellationToken token = default) => Task.CompletedTask;
            public Task CreateWorktreeAsync(string repoPath, string worktreePath, string branchName, string baseBranch = "main", bool detached = false, CancellationToken token = default) => Task.CompletedTask;
            public Task RemoveWorktreeAsync(string worktreePath, CancellationToken token = default) => Task.CompletedTask;
            public Task PushBranchAsync(string worktreePath, string remoteName = "origin", CancellationToken token = default) => Task.CompletedTask;
            public Task<string> CreatePullRequestAsync(string worktreePath, string title, string body, CancellationToken token = default) => Task.FromResult(String.Empty);
            public Task RepairWorktreeAsync(string worktreePath, CancellationToken token = default) => Task.CompletedTask;
            public Task DeleteLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;
            public Task DeleteRemoteBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;
            public Task PushRefSpecAsync(string repoPath, string srcRef, string destRef, CancellationToken token = default) => Task.CompletedTask;
            public Task<string> GetRepositoryHeadRefAsync(string repoPath, CancellationToken token = default) => Task.FromResult("refs/heads/main");
            public Task SetRepositoryHeadAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;
            public Task PruneWorktreesAsync(string repoPath, CancellationToken token = default) => Task.CompletedTask;
            public Task EnableAutoMergeAsync(string worktreePath, string prUrl, CancellationToken token = default) => Task.CompletedTask;
            public Task MergeBranchLocalAsync(string targetWorkDir, string sourceRepoPath, string branchName, string? targetBranch = null, string? commitMessage = null, CancellationToken token = default) => Task.CompletedTask;
            public Task PullAsync(string workingDirectory, CancellationToken token = default) => Task.CompletedTask;
            public Task<string> DiffAsync(string worktreePath, string baseBranch = "main", CancellationToken token = default) => Task.FromResult(String.Empty);
            public Task<string?> GetHeadCommitHashAsync(string worktreePath, CancellationToken token = default) => Task.FromResult<string?>("abc123");
            public Task<IReadOnlyList<string>> GetChangedFilesSinceAsync(string worktreePath, string startCommit, CancellationToken token = default) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            public Task<bool> IsPrMergedAsync(string workingDirectory, string prUrl, CancellationToken token = default) => Task.FromResult(false);
            public Task<bool> BranchExistsAsync(string repoPath, string branchName, CancellationToken token = default) => Task.FromResult(true);
            public Task<bool> EnsureLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.FromResult(true);
            public Task<bool> IsWorktreeRegisteredAsync(string repoPath, string worktreePath, CancellationToken token = default) => Task.FromResult(false);
            public Task SetHeadSymbolicRefAsync(string repoPath, string targetRef, CancellationToken token = default) => Task.CompletedTask;
        }
    }
}
