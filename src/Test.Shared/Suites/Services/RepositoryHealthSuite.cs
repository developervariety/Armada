namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Advisory repository health: real temporary Git repositories, owner-host refusal,
    /// and a dispatch preview that does not consume a failing health result.
    /// </summary>
    public sealed class RepositoryHealthSuite : IArmadaTestSuite
    {
        private const string SuiteId = "Services.RepositoryHealth";

        /// <summary>Build the repository health suite.</summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>
            {
                CaseAsync("healthy_repository_has_no_findings", "A clean even repository with a profile and a passed check has no findings", TestTags.Positive, HealthyHasNoFindingsAsync),
                CaseAsync("dirty_checkout_is_reported", "A dirty working checkout produces a dirty finding and leaves the tree unchanged", TestTags.Negative, DirtyLeavesTreeUnchangedAsync),
                CaseAsync("behind_checkout_is_reported", "A working checkout behind origin produces a behind finding", TestTags.Negative, BehindIsReportedAsync),
                CaseAsync("diverged_bare_is_reported", "A bare repository that has diverged from origin produces a divergence finding", TestTags.Negative, DivergedIsReportedAsync),
                CaseAsync("missing_workflow_profile_is_reported", "A repository with no workflow profile produces that finding", TestTags.Negative, MissingProfileIsReportedAsync),
                CaseAsync("recent_failed_check_is_reported", "A recent failed check produces a finding", TestTags.Negative, RecentFailureIsReportedAsync),
                CaseAsync("offline_owner_is_unavailable", "An offline owner host yields unavailable and does not read the local checkout", TestTags.Negative, OfflineOwnerIsUnavailableAsync),
                CaseAsync("other_tenant_is_refused", "A caller outside the vessel tenant receives no report", TestTags.Negative, OtherTenantIsRefusedAsync),
                CaseAsync("dispatch_ignores_failing_health", "Dispatch preview does not gain a finding from a failing health result", TestTags.Positive, DispatchIgnoresFailingHealthAsync)
            };
            return new TestSuiteDescriptor(SuiteId, "Repository health", cases);
        }

        private static async Task HealthyHasNoFindingsAsync()
        {
            using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
            using RepoSet repos = RepoSet.Create();
            AuthContext auth = await SeedCallerAsync(testDb).ConfigureAwait(false);
            Vessel vessel = await SaveVesselAsync(testDb, auth, repos).ConfigureAwait(false);
            await SaveProfileAsync(testDb, auth, vessel).ConfigureAwait(false);
            await SaveCheckAsync(testDb, vessel, CheckRunStatusEnum.Passed).ConfigureAwait(false);

            RepositoryHealthReport report = await EvaluateAsync(testDb, auth, vessel.Id).ConfigureAwait(false);
            AssertEqual(RepositoryHealthAvailabilityEnum.Available, report.Availability);
            AssertTrue(report.Advisory);
            AssertEqual(0, report.Findings.Count, Describe(report));
        }

        private static async Task DirtyLeavesTreeUnchangedAsync()
        {
            using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
            using RepoSet repos = RepoSet.Create();
            File.AppendAllText(Path.Combine(repos.Work, "readme.txt"), "dirty\n");
            AuthContext auth = await SeedCallerAsync(testDb).ConfigureAwait(false);
            Vessel vessel = await SaveVesselAsync(testDb, auth, repos).ConfigureAwait(false);
            await SaveProfileAsync(testDb, auth, vessel).ConfigureAwait(false);
            string beforeWork = repos.Snapshot(repos.Work, true);
            string beforeBare = repos.Snapshot(repos.Bare, false);

            RepositoryHealthReport report = await EvaluateAsync(testDb, auth, vessel.Id).ConfigureAwait(false);

            AssertEqual(beforeWork, repos.Snapshot(repos.Work, true), "the working checkout changed during evaluation");
            AssertEqual(beforeBare, repos.Snapshot(repos.Bare, false), "the bare repository changed during evaluation");
            AssertTrue(Has(report, RepositoryHealthService.WorkingCheckoutDirty), "expected working_checkout_dirty, saw " + Describe(report));
        }

        private static async Task BehindIsReportedAsync()
        {
            using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
            using RepoSet repos = RepoSet.Create();
            File.WriteAllText(Path.Combine(repos.Work, "readme.txt"), "two\n");
            repos.Git(repos.Work, "add", "readme.txt");
            repos.Git(repos.Work, "commit", "-m", "two");
            repos.Git(repos.Work, "push", "origin", "main");
            repos.Git(repos.Work, "reset", "--hard", "HEAD~1");
            repos.Git(repos.Bare, "fetch", "origin");
            AuthContext auth = await SeedCallerAsync(testDb).ConfigureAwait(false);
            Vessel vessel = await SaveVesselAsync(testDb, auth, repos).ConfigureAwait(false);
            await SaveProfileAsync(testDb, auth, vessel).ConfigureAwait(false);

            RepositoryHealthReport report = await EvaluateAsync(testDb, auth, vessel.Id).ConfigureAwait(false);
            AssertTrue(Has(report, RepositoryHealthService.WorkingCheckoutBehind), Describe(report));
            AssertFalse(Has(report, RepositoryHealthService.BareOriginDiverged), Describe(report));
        }

        private static async Task DivergedIsReportedAsync()
        {
            using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
            using RepoSet repos = RepoSet.Create();
            string bareWork = Path.Combine(repos.Root, "bare-work");
            repos.Git(repos.Root, "clone", repos.Bare, bareWork);
            File.WriteAllText(Path.Combine(bareWork, "readme.txt"), "bare side\n");
            repos.Git(bareWork, "add", "readme.txt");
            repos.Git(bareWork, "commit", "-m", "bare side");
            repos.Git(bareWork, "push", "origin", "main");
            File.WriteAllText(Path.Combine(repos.Work, "readme.txt"), "origin side\n");
            repos.Git(repos.Work, "add", "readme.txt");
            repos.Git(repos.Work, "commit", "-m", "origin side");
            repos.Git(repos.Work, "push", "origin", "main");
            repos.Git(repos.Bare, "fetch", "origin");
            AuthContext auth = await SeedCallerAsync(testDb).ConfigureAwait(false);
            Vessel vessel = await SaveVesselAsync(testDb, auth, repos).ConfigureAwait(false);
            await SaveProfileAsync(testDb, auth, vessel).ConfigureAwait(false);

            RepositoryHealthReport report = await EvaluateAsync(testDb, auth, vessel.Id).ConfigureAwait(false);
            AssertTrue(Has(report, RepositoryHealthService.BareOriginDiverged), Describe(report));
            AssertFalse(Has(report, RepositoryHealthService.WorkingCheckoutBehind), Describe(report));
        }

        private static async Task MissingProfileIsReportedAsync()
        {
            using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
            using RepoSet repos = RepoSet.Create();
            AuthContext auth = await SeedCallerAsync(testDb).ConfigureAwait(false);
            Vessel vessel = await SaveVesselAsync(testDb, auth, repos).ConfigureAwait(false);

            RepositoryHealthReport report = await EvaluateAsync(testDb, auth, vessel.Id).ConfigureAwait(false);
            AssertTrue(Has(report, RepositoryHealthService.WorkflowProfileMissing), Describe(report));
        }

        private static async Task RecentFailureIsReportedAsync()
        {
            using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
            using RepoSet repos = RepoSet.Create();
            AuthContext auth = await SeedCallerAsync(testDb).ConfigureAwait(false);
            Vessel vessel = await SaveVesselAsync(testDb, auth, repos).ConfigureAwait(false);
            await SaveProfileAsync(testDb, auth, vessel).ConfigureAwait(false);
            await SaveCheckAsync(testDb, vessel, CheckRunStatusEnum.Failed).ConfigureAwait(false);

            RepositoryHealthReport report = await EvaluateAsync(testDb, auth, vessel.Id).ConfigureAwait(false);
            AssertTrue(Has(report, RepositoryHealthService.RecentCheckFailed), Describe(report));
        }

        private static async Task OfflineOwnerIsUnavailableAsync()
        {
            using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
            using RepoSet repos = RepoSet.Create();
            File.AppendAllText(Path.Combine(repos.Work, "readme.txt"), "dirty\n");
            AuthContext auth = await SeedCallerAsync(testDb).ConfigureAwait(false);
            Vessel vessel = await SaveVesselAsync(testDb, auth, repos).ConfigureAwait(false);
            HarborSettings harbor = new HarborSettings
            {
                Enabled = true,
                MissionRoutes = new List<HarborMissionRoute>
                {
                    new HarborMissionRoute
                    {
                        VesselId = vessel.Id,
                        RunnerId = "runner-offline",
                        RepositoryGrantId = "grant-offline",
                        CheckoutMode = HarborCheckoutModeEnum.HarborOwned
                    }
                }
            };
            string before = repos.Snapshot(repos.Work, true);
            RepositoryHealthService service = new RepositoryHealthService(
                testDb.Driver,
                new WorkflowProfileService(testDb.Driver, CreateLogging()),
                harbor,
                new OfflineOwner());
            service.CacheDuration = TimeSpan.Zero;

            RepositoryHealthReport? report = await service.EvaluateAsync(auth, vessel.Id).ConfigureAwait(false);

            AssertTrue(report != null);
            AssertEqual(RepositoryHealthAvailabilityEnum.Unavailable, report!.Availability);
            AssertEqual(RepositoryHealthService.OwnerHostUnavailable, report.UnavailableReason);
            AssertEqual(0, report.Findings.Count, Describe(report));
            AssertEqual(before, repos.Snapshot(repos.Work, true), "an offline owner was evaluated on this host");
        }

        private static async Task OtherTenantIsRefusedAsync()
        {
            using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
            using RepoSet repos = RepoSet.Create();
            AuthContext owner = await SeedCallerAsync(testDb).ConfigureAwait(false);
            Vessel vessel = await SaveVesselAsync(testDb, owner, repos).ConfigureAwait(false);
            await EnsureTenantAndUserAsync(testDb, "ten_other", "usr_other").ConfigureAwait(false);
            AuthContext other = AuthContext.Authenticated("ten_other", "usr_other", false, true, "UnitTest");

            RepositoryHealthService service = new RepositoryHealthService(
                testDb.Driver,
                new WorkflowProfileService(testDb.Driver, CreateLogging()));
            service.CacheDuration = TimeSpan.Zero;
            RepositoryHealthReport? refused = await service.EvaluateAsync(other, vessel.Id).ConfigureAwait(false);
            RepositoryHealthReport? allowed = await service.EvaluateAsync(owner, vessel.Id).ConfigureAwait(false);
            AssertTrue(refused == null, "a caller outside the vessel tenant received a report");
            AssertTrue(allowed != null);
            AssertEqual(vessel.Id, allowed!.VesselId);
        }

        private static async Task DispatchIgnoresFailingHealthAsync()
        {
            using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
            using RepoSet repos = RepoSet.Create();
            string bareWork = Path.Combine(repos.Root, "bare-work");
            repos.Git(repos.Root, "clone", repos.Bare, bareWork);
            File.WriteAllText(Path.Combine(bareWork, "readme.txt"), "bare side\n");
            repos.Git(bareWork, "add", "readme.txt");
            repos.Git(bareWork, "commit", "-m", "bare side");
            repos.Git(bareWork, "push", "origin", "main");
            File.WriteAllText(Path.Combine(repos.Work, "readme.txt"), "origin side\n");
            repos.Git(repos.Work, "add", "readme.txt");
            repos.Git(repos.Work, "commit", "-m", "origin side");
            repos.Git(repos.Work, "push", "origin", "main");
            repos.Git(repos.Bare, "fetch", "origin");
            AuthContext auth = await SeedCallerAsync(testDb).ConfigureAwait(false);
            Vessel vessel = await SaveVesselAsync(testDb, auth, repos).ConfigureAwait(false);
            await SaveProfileAsync(testDb, auth, vessel).ConfigureAwait(false);
            RepositoryHealthReport health = await EvaluateAsync(testDb, auth, vessel.Id).ConfigureAwait(false);
            AssertTrue(Has(health, RepositoryHealthService.BareOriginDiverged), Describe(health));

            Objective objective = new Objective
            {
                Id = "obj_healthdispatch",
                TenantId = auth.TenantId,
                UserId = auth.UserId,
                Title = "Health dispatch",
                Kind = ObjectiveKindEnum.Feature,
                VesselIds = new List<string> { vessel.Id }
            };
            for (int number = 1; number <= ObjectivePreflight.QuestionCount; number++)
            {
                objective.Preparation.Preflight.Questions.Add(new ObjectivePreflightAnswer
                {
                    Number = number,
                    Answer = number == ObjectivePreflight.OwnerQuestionNumber
                        ? ObjectivePreflightAnswerEnum.No
                        : ObjectivePreflightAnswerEnum.Yes
                });
            }
            AssertTrue(ObjectivePreflightEvaluator.IsComplete(objective.Preparation.Preflight));

            LoggingModule logging = CreateLogging();
            ObjectiveDispatchPreviewService preview = new ObjectiveDispatchPreviewService(
                testDb.Driver,
                new WorkflowProfileService(testDb.Driver, logging),
                new VesselReadinessService(testDb.Driver, new WorkflowProfileService(testDb.Driver, logging), logging),
                new GitService(logging),
                new ArmadaSettings());
            ObjectiveDispatchPreview result = await preview.PreviewAsync(auth, objective).ConfigureAwait(false);
            AssertFalse(result.Issues.Exists(issue => String.Equals(issue.Code, RepositoryHealthService.BareOriginDiverged, StringComparison.Ordinal)),
                "dispatch preview included repository health: " + String.Join(", ", result.Issues.ConvertAll(issue => issue.Code)));
        }

        private static async Task<RepositoryHealthReport> EvaluateAsync(TestDatabase testDb, AuthContext auth, string vesselId)
        {
            RepositoryHealthService service = new RepositoryHealthService(
                testDb.Driver,
                new WorkflowProfileService(testDb.Driver, CreateLogging()));
            service.CacheDuration = TimeSpan.Zero;
            RepositoryHealthReport? report = await service.EvaluateAsync(auth, vesselId).ConfigureAwait(false);
            AssertTrue(report != null, "the vessel report was missing");
            return report!;
        }

        private static async Task<AuthContext> SeedCallerAsync(TestDatabase testDb)
        {
            await EnsureTenantAndUserAsync(testDb, "ten_health", "usr_health").ConfigureAwait(false);
            return AuthContext.Authenticated("ten_health", "usr_health", false, true, "UnitTest");
        }

        private static async Task<Vessel> SaveVesselAsync(TestDatabase testDb, AuthContext auth, RepoSet repos)
        {
            Vessel vessel = new Vessel
            {
                TenantId = auth.TenantId,
                UserId = auth.UserId,
                Name = "Health Vessel " + Guid.NewGuid().ToString("N").Substring(0, 8),
                RepoUrl = "https://example.test/health.git",
                LocalPath = repos.Bare,
                WorkingDirectory = repos.Work,
                DefaultBranch = "main"
            };
            return await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task SaveProfileAsync(TestDatabase testDb, AuthContext auth, Vessel vessel)
        {
            WorkflowProfileService profiles = new WorkflowProfileService(testDb.Driver, CreateLogging());
            RecordWriteResult<WorkflowProfile> saved = await profiles.CreateAsync(auth, new WorkflowProfile
            {
                TenantId = auth.TenantId,
                UserId = auth.UserId,
                Name = "Health profile",
                Scope = WorkflowProfileScopeEnum.Vessel,
                VesselId = vessel.Id,
                BuildCommand = "dotnet build",
                Active = true
            }).ConfigureAwait(false);
            AssertEqual(RecordWriteOutcomeEnum.Succeeded, saved.Outcome, saved.Message ?? "profile create failed");
        }

        private static async Task SaveCheckAsync(TestDatabase testDb, Vessel vessel, CheckRunStatusEnum status)
        {
            DateTime now = DateTime.UtcNow;
            await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
            {
                TenantId = vessel.TenantId,
                UserId = vessel.UserId,
                VesselId = vessel.Id,
                Type = CheckRunTypeEnum.Build,
                Source = CheckRunSourceEnum.Armada,
                Status = status,
                Command = "dotnet build",
                ExitCode = status == CheckRunStatusEnum.Passed ? 0 : 1,
                CreatedUtc = now,
                CompletedUtc = now,
                LastUpdateUtc = now
            }).ConfigureAwait(false);
        }

        private static async Task EnsureTenantAndUserAsync(TestDatabase testDb, string tenantId, string userId)
        {
            if (await testDb.Driver.Tenants.ReadAsync(tenantId).ConfigureAwait(false) == null)
            {
                await testDb.Driver.Tenants.CreateAsync(new TenantMetadata { Id = tenantId, Name = tenantId }).ConfigureAwait(false);
            }
            if (await testDb.Driver.Users.ReadByIdAsync(userId).ConfigureAwait(false) == null)
            {
                await testDb.Driver.Users.CreateAsync(new UserMaster
                {
                    Id = userId,
                    TenantId = tenantId,
                    Email = userId + "@armada.test",
                    PasswordSha256 = UserMaster.ComputePasswordHash("password"),
                    IsTenantAdmin = true
                }).ConfigureAwait(false);
            }
        }

        private static bool Has(RepositoryHealthReport report, string code)
        {
            return report.Findings.Exists(finding => String.Equals(finding.Code, code, StringComparison.Ordinal));
        }

        private static string Describe(RepositoryHealthReport report)
        {
            return String.Join(", ", report.Findings.ConvertAll(finding => finding.Code));
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(SuiteId, caseId, displayName, (CancellationToken _) => body(), new List<string> { tag });
        }

        private sealed class OfflineOwner : IRepositoryHealthOwnerChannel
        {
            public Task<RepositoryHealthOwnerReply> ReadAsync(Vessel vessel, HarborMissionRoute route, CancellationToken token)
            {
                return Task.FromResult(new RepositoryHealthOwnerReply
                {
                    Unavailable = true,
                    Reason = RepositoryHealthService.OwnerHostUnavailable
                });
            }
        }

        private sealed class RepoSet : IDisposable
        {
            public string Root { get; }
            public string Origin { get; }
            public string Bare { get; }
            public string Work { get; }

            private RepoSet(string root)
            {
                Root = root;
                Origin = Path.Combine(root, "origin.git");
                Bare = Path.Combine(root, "bare.git");
                Work = Path.Combine(root, "work");
            }

            public static RepoSet Create()
            {
                string root = Path.Combine(Path.GetTempPath(), "armada-health-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                RepoSet repos = new RepoSet(root);
                Directory.CreateDirectory(repos.Work);
                repos.Git(repos.Work, "init", "-b", "main");
                File.WriteAllText(Path.Combine(repos.Work, "readme.txt"), "one\n");
                repos.Git(repos.Work, "add", "readme.txt");
                repos.Git(repos.Work, "commit", "-m", "one");
                repos.Git(root, "init", "--bare", repos.Origin);
                repos.Git(repos.Work, "remote", "add", "origin", repos.Origin);
                repos.Git(repos.Work, "push", "-u", "origin", "main");
                repos.Git(root, "clone", "--bare", repos.Origin, repos.Bare);
                return repos;
            }

            public void Git(string cwd, params string[] args)
            {
                string error = Run(cwd, args, out int code);
                if (code != 0) throw new InvalidOperationException(String.Join(" ", args) + " failed: " + error);
            }

            public string Snapshot(string path, bool worktree)
            {
                string refs = Run(path, new[] { "for-each-ref", "--format=%(refname) %(objectname)" }, out int refsCode);
                string head = Run(path, new[] { "rev-parse", "HEAD" }, out int headCode);
                if (refsCode != 0 || headCode != 0) throw new InvalidOperationException("snapshot failed");
                if (!worktree) return refs + "\n--\n" + head;
                string status = Run(path, new[] { "status", "--porcelain", "--untracked-files=all" }, out int statusCode);
                if (statusCode != 0) throw new InvalidOperationException("status snapshot failed");
                string file = File.ReadAllText(Path.Combine(path, "readme.txt"));
                return refs + "\n--\n" + status + "\n--\n" + head + "\n--\n" + file;
            }

            private static string Run(string cwd, string[] args, out int code)
            {
                ProcessStartInfo start = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = cwd,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false)
                };
                foreach (string arg in args) start.ArgumentList.Add(arg);
                start.Environment["GIT_AUTHOR_NAME"] = "Health";
                start.Environment["GIT_AUTHOR_EMAIL"] = "health@example.test";
                start.Environment["GIT_COMMITTER_NAME"] = "Health";
                start.Environment["GIT_COMMITTER_EMAIL"] = "health@example.test";
                using Process process = Process.Start(start) ?? throw new InvalidOperationException("git did not start");
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                code = process.ExitCode;
                return output + error;
            }

            public void Dispose()
            {
                try
                {
                    if (Directory.Exists(Root)) Directory.Delete(Root, true);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
