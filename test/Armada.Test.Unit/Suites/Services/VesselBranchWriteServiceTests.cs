namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Recovery;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Guarded vessel branch push and merge against real repositories: an origin, a bare landing
    /// repository cloned from it, and a separate working checkout.
    /// </summary>
    public class VesselBranchWriteServiceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Vessel Branch Write Service";

        private sealed class Fixture
        {
            public string Root { get; set; } = String.Empty;
            public string Source { get; set; } = String.Empty;
            public string Origin { get; set; } = String.Empty;
            public string Landing { get; set; } = String.Empty;
            public string Working { get; set; } = String.Empty;
            public string BaseCommit { get; set; } = String.Empty;
            public string FeatureCommit { get; set; } = String.Empty;
        }

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Push fast-forwards origin, verifies the tip and leaves landing refs unchanged", async () =>
            {
                await WithFixtureAsync(async (fx, db, service, vessel) =>
                {
                    string landingRefs = await GitAsync(fx.Landing, "show-ref");
                    BranchWriteResult result = await service.PushAsync(vessel, new BranchPushRequest { SourceRef = "feature/work", TargetRef = "main", Remote = "origin" });
                    AssertTrue(result.Succeeded, "push succeeds: " + result.Reason + " " + result.Message);
                    AssertEqual(fx.BaseCommit, result.PreviousTargetCommit, "previous remote tip");
                    AssertEqual(fx.FeatureCommit, result.TargetCommit, "verified remote tip");
                    AssertEqual(fx.FeatureCommit, await RevAsync(fx.Origin, "refs/heads/main"), "origin main advanced");
                    AssertEqual(landingRefs, await GitAsync(fx.Landing, "show-ref"), "push does not change landing refs");
                });
            });

            await RunTest("Push that would rewrite origin history is refused and forces nothing", async () =>
            {
                await WithFixtureAsync(async (fx, db, service, vessel) =>
                {
                    await CommitAsync(fx.Source, "main", "other.txt", "remote moved\n", "Remote moved");
                    await GitAsync(fx.Source, "push", "origin", "main");
                    string movedRemote = await RevAsync(fx.Origin, "refs/heads/main");
                    BranchWriteResult result = await service.PushAsync(vessel, new BranchPushRequest { SourceRef = "feature/work", TargetRef = "main", Remote = "origin" });
                    AssertFalse(result.Succeeded, "non-fast-forward push is refused");
                    AssertEqual(BranchWriteReasons.NonFastForward, result.Reason);
                    AssertEqual(movedRemote, await RevAsync(fx.Origin, "refs/heads/main"), "origin main unchanged");
                });
            });

            await RunTest("Push to any remote other than the vessel origin is refused", async () =>
            {
                await WithFixtureAsync(async (fx, db, service, vessel) =>
                {
                    await GitAsync(fx.Landing, "remote", "add", "upstream", fx.Origin);
                    BranchWriteResult upstream = await service.PushAsync(vessel, new BranchPushRequest { SourceRef = "feature/work", TargetRef = "main", Remote = "upstream" });
                    AssertEqual(BranchWriteReasons.RemoteNotAllowed, upstream.Reason, "upstream remote");

                    vessel.RepoUrl = "https://example.invalid/other/repository.git";
                    BranchWriteResult mismatch = await service.PushAsync(vessel, new BranchPushRequest { SourceRef = "feature/work", TargetRef = "main", Remote = "origin" });
                    AssertEqual(BranchWriteReasons.RemoteMismatch, mismatch.Reason, "origin that is not the vessel repository");
                    AssertEqual(fx.BaseCommit, await RevAsync(fx.Origin, "refs/heads/main"), "origin unchanged");
                });
            });

            await RunTest("Invalid, missing and identical refs are refused with named reasons", async () =>
            {
                await WithFixtureAsync(async (fx, db, service, vessel) =>
                {
                    string[] invalid = new[] { "bad..name", "-delete", "refs/heads/main", "HEAD", "with space", "trailing.lock" };
                    foreach (string name in invalid)
                    {
                        BranchWriteResult merge = await service.MergeAsync(vessel, new BranchMergeRequest { SourceRef = name, TargetRef = "main", Strategy = "FastForward" });
                        AssertEqual(BranchWriteReasons.InvalidRef, merge.Reason, "merge source '" + name + "'");
                        BranchWriteResult push = await service.PushAsync(vessel, new BranchPushRequest { SourceRef = "feature/work", TargetRef = name, Remote = "origin" });
                        AssertEqual(BranchWriteReasons.InvalidRef, push.Reason, "push target '" + name + "'");
                    }

                    AssertEqual(BranchWriteReasons.SourceMissing, (await service.MergeAsync(vessel, new BranchMergeRequest { SourceRef = "no-such-branch", TargetRef = "main", Strategy = "FastForward" })).Reason, "missing source");
                    AssertEqual(BranchWriteReasons.TargetMissing, (await service.MergeAsync(vessel, new BranchMergeRequest { SourceRef = "feature/work", TargetRef = "no-such-target", Strategy = "FastForward" })).Reason, "missing merge target");
                    AssertEqual(BranchWriteReasons.SameRef, (await service.MergeAsync(vessel, new BranchMergeRequest { SourceRef = "main", TargetRef = "main", Strategy = "FastForward" })).Reason, "same ref");
                    AssertEqual(BranchWriteReasons.InvalidStrategy, (await service.MergeAsync(vessel, new BranchMergeRequest { SourceRef = "feature/work", TargetRef = "main" })).Reason, "strategy is explicit");
                    AssertEqual(BranchWriteReasons.InvalidRequest, (await service.PushAsync(vessel, new BranchPushRequest { SourceRef = "feature/work", TargetRef = "main" })).Reason, "remote is explicit");
                    AssertEqual(fx.BaseCommit, await RevAsync(fx.Landing, "refs/heads/main"), "landing main unchanged");
                });
            });

            await RunTest("Dirty, detached and missing working checkouts refuse both writes", async () =>
            {
                await WithFixtureAsync(async (fx, db, service, vessel) =>
                {
                    await File.WriteAllTextAsync(Path.Combine(fx.Working, "uncommitted.txt"), "dirty\n");
                    AssertEqual(BranchWriteReasons.WorkingCheckoutDirty, (await service.MergeAsync(vessel, new BranchMergeRequest { SourceRef = "feature/work", TargetRef = "main", Strategy = "FastForward" })).Reason, "dirty merge");
                    AssertEqual(BranchWriteReasons.WorkingCheckoutDirty, (await service.PushAsync(vessel, new BranchPushRequest { SourceRef = "feature/work", TargetRef = "main", Remote = "origin" })).Reason, "dirty push");
                    File.Delete(Path.Combine(fx.Working, "uncommitted.txt"));

                    await GitAsync(fx.Working, "checkout", "--detach", "HEAD");
                    AssertEqual(BranchWriteReasons.WorkingCheckoutDetached, (await service.MergeAsync(vessel, new BranchMergeRequest { SourceRef = "feature/work", TargetRef = "main", Strategy = "FastForward" })).Reason, "detached merge");
                    await GitAsync(fx.Working, "checkout", "main");

                    vessel.WorkingDirectory = Path.Combine(fx.Root, "missing-working");
                    AssertEqual(BranchWriteReasons.WorkingCheckoutMissing, (await service.PushAsync(vessel, new BranchPushRequest { SourceRef = "feature/work", TargetRef = "main", Remote = "origin" })).Reason, "missing working checkout");

                    vessel.LocalPath = Path.Combine(fx.Root, "missing-landing.git");
                    AssertEqual(BranchWriteReasons.RepositoryMissing, (await service.MergeAsync(vessel, new BranchMergeRequest { SourceRef = "feature/work", TargetRef = "main", Strategy = "FastForward" })).Reason, "missing landing repository");

                    AssertEqual(fx.BaseCommit, await RevAsync(fx.Landing, "refs/heads/main"), "landing main unchanged");
                    AssertEqual(fx.BaseCommit, await RevAsync(fx.Origin, "refs/heads/main"), "origin main unchanged");
                });
            });

            await RunTest("Fast-forward merge advances the landing target, syncs the working checkout and pushes nothing", async () =>
            {
                await WithFixtureAsync(async (fx, db, service, vessel) =>
                {
                    BranchWriteResult result = await service.MergeAsync(vessel, new BranchMergeRequest { SourceRef = "feature/work", TargetRef = "main", Strategy = "FastForward" });
                    AssertTrue(result.Succeeded, "merge succeeds: " + result.Reason + " " + result.Message);
                    AssertEqual(fx.BaseCommit, result.PreviousTargetCommit);
                    AssertEqual(fx.FeatureCommit, result.TargetCommit);
                    AssertEqual(fx.FeatureCommit, await RevAsync(fx.Landing, "refs/heads/main"), "landing main fast-forwarded");
                    AssertEqual("fast_forwarded", result.WorkingCheckoutSync);
                    AssertEqual(fx.FeatureCommit, await RevAsync(fx.Working, "HEAD"), "working checkout fast-forwarded");
                    AssertEqual(fx.BaseCommit, await RevAsync(fx.Origin, "refs/heads/main"), "merge never pushes");
                    AssertEqual(BranchWriteReasons.NothingToWrite, (await service.MergeAsync(vessel, new BranchMergeRequest { SourceRef = "feature/work", TargetRef = "main", Strategy = "MergeCommit" })).Reason, "second merge has nothing to write");
                });
            });

            await RunTest("Diverged target refuses fast-forward and an explicit merge commit preserves both histories", async () =>
            {
                await WithFixtureAsync(async (fx, db, service, vessel) =>
                {
                    string diverged = await CommitAsync(fx.Source, "main", "main-only.txt", "main only\n", "Main moved");
                    await GitAsync(fx.Landing, "fetch", fx.Source, "main:refs/heads/main");
                    await GitAsync(fx.Working, "fetch", fx.Landing, "main");
                    await GitAsync(fx.Working, "merge", "--ff-only", "FETCH_HEAD");

                    BranchWriteResult ff = await service.MergeAsync(vessel, new BranchMergeRequest { SourceRef = "feature/work", TargetRef = "main", Strategy = "FastForward" });
                    AssertEqual(BranchWriteReasons.NonFastForward, ff.Reason, "fast-forward refused");
                    AssertEqual(diverged, await RevAsync(fx.Landing, "refs/heads/main"), "landing main unchanged after refusal");

                    BranchWriteResult merge = await service.MergeAsync(vessel, new BranchMergeRequest { SourceRef = "feature/work", TargetRef = "main", Strategy = "MergeCommit" });
                    AssertTrue(merge.Succeeded, "merge commit succeeds: " + merge.Reason + " " + merge.Message);
                    string tip = await RevAsync(fx.Landing, "refs/heads/main");
                    AssertEqual(tip, merge.TargetCommit);
                    string parents = (await GitAsync(fx.Landing, "rev-list", "--parents", "-n", "1", tip)).Trim();
                    AssertEqual(tip + " " + diverged + " " + fx.FeatureCommit, parents, "merge commit parents are previous target then source");
                    AssertEqual("fast_forwarded", merge.WorkingCheckoutSync);
                });
            });

            await RunTest("Conflicting merge is refused without creating a commit", async () =>
            {
                await WithFixtureAsync(async (fx, db, service, vessel) =>
                {
                    string diverged = await CommitAsync(fx.Source, "main", "feature.txt", "conflicting main content\n", "Main conflicts");
                    await GitAsync(fx.Landing, "fetch", fx.Source, "main:refs/heads/main");
                    await GitAsync(fx.Working, "fetch", fx.Landing, "main");
                    await GitAsync(fx.Working, "merge", "--ff-only", "FETCH_HEAD");
                    BranchWriteResult result = await service.MergeAsync(vessel, new BranchMergeRequest { SourceRef = "feature/work", TargetRef = "main", Strategy = "MergeCommit" });
                    AssertEqual(BranchWriteReasons.MergeConflict, result.Reason);
                    AssertEqual(diverged, await RevAsync(fx.Landing, "refs/heads/main"), "landing main unchanged");
                });
            });

            await RunTest("A target checked out in a landing worktree is refused", async () =>
            {
                await WithFixtureAsync(async (fx, db, service, vessel) =>
                {
                    string worktree = Path.Combine(fx.Root, "landing-worktree");
                    await GitAsync(fx.Landing, "worktree", "add", worktree, "main");
                    BranchWriteResult result = await service.MergeAsync(vessel, new BranchMergeRequest { SourceRef = "feature/work", TargetRef = "main", Strategy = "FastForward" });
                    AssertEqual(BranchWriteReasons.TargetCheckedOut, result.Reason);
                    AssertEqual(fx.BaseCommit, await RevAsync(fx.Landing, "refs/heads/main"));
                });
            });

            await RunTest("Unlanded mission branches, active queue entries and protected or release targets keep their gates", async () =>
            {
                await WithFixtureAsync(async (fx, db, service, vessel) =>
                {
                    Mission mission = new Mission("gated mission");
                    mission.VesselId = vessel.Id;
                    mission.BranchName = "feature/work";
                    mission.Status = MissionStatusEnum.WorkProduced;
                    await db.Driver.Missions.CreateAsync(mission);
                    BranchWriteResult missionGate = await service.MergeAsync(vessel, new BranchMergeRequest { SourceRef = "feature/work", TargetRef = "main", Strategy = "FastForward" });
                    AssertEqual(BranchWriteReasons.MissionBranchNotLanded, missionGate.Reason, "unlanded mission branch");
                    AssertEqual(BranchWriteReasons.MissionBranchNotLanded, (await service.PushAsync(vessel, new BranchPushRequest { SourceRef = "feature/work", TargetRef = "main", Remote = "origin" })).Reason, "push of an unlanded mission branch into another branch");

                    mission.Status = MissionStatusEnum.Complete;
                    await db.Driver.Missions.UpdateAsync(mission);
                    MergeEntry entry = new MergeEntry();
                    entry.VesselId = vessel.Id;
                    entry.BranchName = "feature/work";
                    entry.TargetBranch = "main";
                    entry.Status = MergeStatusEnum.Queued;
                    entry.CreatedUtc = DateTime.UtcNow;
                    entry.LastUpdateUtc = DateTime.UtcNow;
                    await db.Driver.MergeEntries.CreateAsync(entry);
                    AssertEqual(BranchWriteReasons.MergeQueueEntryActive, (await service.MergeAsync(vessel, new BranchMergeRequest { SourceRef = "feature/work", TargetRef = "main", Strategy = "FastForward" })).Reason, "active merge-queue entry");
                    entry.Status = MergeStatusEnum.Cancelled;
                    await db.Driver.MergeEntries.UpdateAsync(entry);

                    vessel.ProtectedBranchPatterns = new List<string> { "ma*" };
                    AssertEqual(BranchWriteReasons.ProtectedTarget, (await service.MergeAsync(vessel, new BranchMergeRequest { SourceRef = "feature/work", TargetRef = "main", Strategy = "FastForward" })).Reason, "protected target");
                    AssertEqual(BranchWriteReasons.ProtectedTarget, (await service.PushAsync(vessel, new BranchPushRequest { SourceRef = "feature/work", TargetRef = "main", Remote = "origin" })).Reason, "protected push target");
                    vessel.ProtectedBranchPatterns = new List<string>();

                    await GitAsync(fx.Landing, "branch", "release/1.0", "feature/work");
                    vessel.RequireMergeQueueForReleaseBranches = true;
                    AssertEqual(BranchWriteReasons.ReleaseRequiresMergeQueue, (await service.MergeAsync(vessel, new BranchMergeRequest { SourceRef = "release/1.0", TargetRef = "main", Strategy = "FastForward" })).Reason, "release branch");
                    vessel.RequireMergeQueueForReleaseBranches = false;

                    AssertEqual(fx.BaseCommit, await RevAsync(fx.Landing, "refs/heads/main"), "landing main unchanged by every refusal");
                    BranchWriteResult allowed = await service.MergeAsync(vessel, new BranchMergeRequest { SourceRef = "feature/work", TargetRef = "main", Strategy = "FastForward" });
                    AssertTrue(allowed.Succeeded, "landed mission branch with a terminal queue entry merges: " + allowed.Reason);
                });
            });

            await RunTest("Branch writes refuse while a landing holds the vessel and proceed after it releases", async () =>
            {
                await WithFixtureAsync(async (fx, db, service, vessel) =>
                {
                    using (await VesselRepositoryLock.AcquireAsync(vessel.Id))
                    {
                        AssertEqual(BranchWriteReasons.VesselBusy, (await service.MergeAsync(vessel, new BranchMergeRequest { SourceRef = "feature/work", TargetRef = "main", Strategy = "FastForward" })).Reason, "merge while held");
                        AssertEqual(BranchWriteReasons.VesselBusy, (await service.PushAsync(vessel, new BranchPushRequest { SourceRef = "feature/work", TargetRef = "main", Remote = "origin" })).Reason, "push while held");
                        AssertEqual(fx.BaseCommit, await RevAsync(fx.Landing, "refs/heads/main"), "landing unchanged while held");
                    }
                    AssertTrue((await service.MergeAsync(vessel, new BranchMergeRequest { SourceRef = "feature/work", TargetRef = "main", Strategy = "FastForward" })).Succeeded, "merge after release");
                });
            });

            await RunTest("Merge-queue entry processing waits for the vessel slot a branch write holds", async () =>
            {
                await WithFixtureAsync(async (fx, db, service, vessel) =>
                {
                    MergeEntry entry = new MergeEntry();
                    entry.VesselId = vessel.Id;
                    entry.BranchName = "feature/work";
                    entry.TargetBranch = "main";
                    entry.Status = MergeStatusEnum.Queued;
                    entry.CreatedUtc = DateTime.UtcNow;
                    entry.LastUpdateUtc = DateTime.UtcNow;
                    await db.Driver.MergeEntries.CreateAsync(entry);

                    LoggingModule logging = Logging();
                    ArmadaSettings settings = new ArmadaSettings();
                    settings.DocksDirectory = Path.Combine(fx.Root, "docks");
                    settings.ReposDirectory = Path.Combine(fx.Root, "repos");
                    MergeQueueService queue = new MergeQueueService(logging, db.Driver, settings, new GitService(logging), new MergeFailureClassifier());

                    Task<MergeEntry?> processing;
                    using (await VesselRepositoryLock.AcquireAsync(vessel.Id))
                    {
                        processing = Task.Run(() => queue.ProcessSingleAsync(entry.Id));
                        await Task.Delay(750);
                        AssertFalse(processing.IsCompleted, "queue processing must wait while the vessel slot is held");
                        MergeEntry? held = await db.Driver.MergeEntries.ReadAsync(entry.Id);
                        AssertEqual(MergeStatusEnum.Queued, held!.Status, "entry does not advance while the slot is held");
                    }
                    Task finished = await Task.WhenAny(processing, Task.Delay(TimeSpan.FromMinutes(2)));
                    AssertTrue(ReferenceEquals(finished, processing), "queue processing completes after the slot is released");
                    MergeEntry? after = await db.Driver.MergeEntries.ReadAsync(entry.Id);
                    AssertTrue(after!.Status != MergeStatusEnum.Queued, "entry advanced after release (status " + after.Status + ")");
                });
            });

            await RunTest("Write controls report availability and named reasons", async () =>
            {
                await WithFixtureAsync(async (fx, db, service, vessel) =>
                {
                    BranchWriteControls member = await service.DescribeControlsAsync(vessel, isAdministrator: false);
                    AssertFalse(member.MergeAvailable || member.PushAvailable, "non-administrator sees no controls");
                    AssertEqual(BranchWriteReasons.AdministratorRequired, member.PushUnavailableReason);

                    BranchWriteControls admin = await service.DescribeControlsAsync(vessel, isAdministrator: true);
                    AssertTrue(admin.MergeAvailable && admin.PushAvailable, "administrator with a verified origin sees both controls");
                    AssertEqual("origin", admin.Remote);

                    vessel.RepoUrl = "https://example.invalid/elsewhere.git";
                    BranchWriteControls mismatch = await service.DescribeControlsAsync(vessel, isAdministrator: true);
                    AssertTrue(mismatch.MergeAvailable, "merge does not depend on the remote");
                    AssertFalse(mismatch.PushAvailable, "push hidden when origin is not the vessel repository");
                    AssertEqual(BranchWriteReasons.RemoteMismatch, mismatch.PushUnavailableReason);

                    vessel.LocalPath = Path.Combine(fx.Root, "absent.git");
                    AssertEqual(BranchWriteReasons.RepositoryMissing, (await service.DescribeControlsAsync(vessel, isAdministrator: true)).MergeUnavailableReason);
                });
            });
        }

        private async Task WithFixtureAsync(Func<Fixture, TestDatabase, VesselBranchWriteService, Vessel, Task> body)
        {
            Fixture fx = await CreateFixtureAsync();
            try
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Vessel vessel = new Vessel("branch-write-" + Guid.NewGuid().ToString("N").Substring(0, 8), fx.Origin);
                    vessel.LocalPath = fx.Landing;
                    vessel.WorkingDirectory = fx.Working;
                    vessel.DefaultBranch = "main";
                    await db.Driver.Vessels.CreateAsync(vessel);
                    await body(fx, db, new VesselBranchWriteService(db.Driver, Logging()), vessel);
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(fx.Root, true);
                }
                catch (IOException)
                {
                    // Temporary fixture cleanup is best effort; a leftover directory does not affect the result.
                }
            }
        }

        private static async Task<Fixture> CreateFixtureAsync()
        {
            Fixture fx = new Fixture { Root = Path.Combine(Path.GetTempPath(), "armada-branch-write-" + Guid.NewGuid().ToString("N")) };
            fx.Source = Path.Combine(fx.Root, "source");
            fx.Origin = Path.Combine(fx.Root, "origin.git");
            fx.Landing = Path.Combine(fx.Root, "landing.git");
            fx.Working = Path.Combine(fx.Root, "working");
            Directory.CreateDirectory(fx.Source);
            await GitAsync(fx.Source, "init", "-b", "main");
            await ConfigureIdentityAsync(fx.Source);
            await File.WriteAllTextAsync(Path.Combine(fx.Source, "README.md"), "base\n");
            await GitAsync(fx.Source, "add", "README.md");
            await GitAsync(fx.Source, "commit", "-m", "Base");
            fx.BaseCommit = await RevAsync(fx.Source, "HEAD");
            fx.FeatureCommit = await CommitAsync(fx.Source, "feature/work", "feature.txt", "feature\n", "Feature", createFrom: "main");
            await GitAsync(fx.Source, "checkout", "main");

            await GitAsync(fx.Root, "init", "--bare", "-b", "main", fx.Origin);
            await GitAsync(fx.Source, "remote", "add", "origin", fx.Origin);
            await GitAsync(fx.Source, "push", "origin", "main");

            await GitAsync(fx.Root, "clone", "--bare", fx.Origin, fx.Landing);
            await ConfigureIdentityAsync(fx.Landing);
            await GitAsync(fx.Landing, "fetch", fx.Source, "feature/work:refs/heads/feature/work");

            await GitAsync(fx.Root, "clone", fx.Origin, fx.Working);
            await ConfigureIdentityAsync(fx.Working);
            return fx;
        }

        private static async Task ConfigureIdentityAsync(string repository)
        {
            await GitAsync(repository, "config", "user.name", "Armada Tests");
            await GitAsync(repository, "config", "user.email", "armada-tests@example.com");
        }

        private static async Task<string> CommitAsync(string repository, string branch, string file, string content, string message, string? createFrom = null)
        {
            if (createFrom != null) await GitAsync(repository, "checkout", "-b", branch, createFrom);
            else await GitAsync(repository, "checkout", branch);
            await File.WriteAllTextAsync(Path.Combine(repository, file), content);
            await GitAsync(repository, "add", file);
            await GitAsync(repository, "commit", "-m", message);
            return await RevAsync(repository, "HEAD");
        }

        private static async Task<string> RevAsync(string repository, string rev)
        {
            return (await GitAsync(repository, "rev-parse", rev)).Trim();
        }

        private static LoggingModule Logging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static async Task<string> GitAsync(string workingDirectory, params string[] args)
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
            using Process process = new Process { StartInfo = startInfo };
            process.Start();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdout, stderr);
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new InvalidOperationException("git " + String.Join(" ", args) + " failed: " + stderr.Result);
            return stdout.Result;
        }
    }
}
