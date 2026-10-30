namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;

    public partial class MergeQueueServiceClassificationTests
    {
        private const string GeneratedManifest = "manifest.json";

        private sealed class RoutedStages
        {
            public Mission Worker { get; set; } = null!;
            public Mission Judge { get; set; } = null!;
            public Mission Recorder { get; set; } = null!;
            public MergeEntry Entry { get; set; } = null!;
        }

        private sealed class GeneratedManifestRepos
        {
            public string BareDir { get; set; } = String.Empty;
            public string[] Branches { get; set; } = Array.Empty<string>();
        }

        private sealed class PreparingQueue
        {
            public MergeQueueService Queue { get; set; } = null!;
            public Vessel Vessel { get; set; } = null!;
            public MissionService Missions { get; set; } = null!;
            public ArmadaSettings Settings { get; set; } = null!;
        }

        private async Task RunGeneratedCandidatePreparationTestsAsync()
        {
            await RunTest("Regenerated landing reruns the same Judge record at the regenerated commit and waits for its approval", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_prepare_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GeneratedManifestRepos repos = await CreateGeneratedManifestReposAsync(rootDir, true, "b.txt").ConfigureAwait(false);
                    string bareDir = repos.BareDir;
                    string[] branches = repos.Branches;
                    string branch = branches[0];
                    string source = await RunGitCaptureAsync(bareDir, "rev-parse", branch).ConfigureAwait(false);
                    string mainBefore = await RunGitCaptureAsync(bareDir, "rev-parse", "main").ConfigureAwait(false);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        PreparingQueue prepared = await CreatePreparingQueueAsync(testDb, bareDir).ConfigureAwait(false);
                        MergeQueueService queue = prepared.Queue;
                        Vessel vessel = prepared.Vessel;
                        RoutedStages stages = await CreateRoutedStagesAsync(testDb, vessel, branch, source, "first").ConfigureAwait(false);
                        int voyagesBefore = (await testDb.Driver.Voyages.EnumerateAsync().ConfigureAwait(false)).Count;

                        await queue.ProcessEntryByIdAsync(stages.Entry.Id).ConfigureAwait(false);

                        Mission judge = (await testDb.Driver.Missions.ReadAsync(stages.Judge.Id).ConfigureAwait(false))!;
                        GeneratedArtifactReviewBinding? binding = judge.GeneratedArtifactReview;
                        AssertNotNull(binding, "the same Judge record carries the review binding");
                        string tip = await RunGitCaptureAsync(bareDir, "rev-parse", branch).ConfigureAwait(false);
                        AssertEqual(tip, binding!.CandidateCommit, "the binding names the regenerated commit on the retained branch");
                        AssertEqual(source, binding.SourceCommit, "the binding keeps the commit the Judge reviewed");
                        AssertEqual(mainBefore, binding.TargetBaseCommit, "the binding names the target the preparation read");
                        AssertNotEqual(source, binding.CandidateCommit, "regeneration produced a new commit");
                        AssertTrue(await IsAncestorAsync(bareDir, source, binding.CandidateCommit), "the reviewed commit is an ancestor of the candidate");
                        AssertTrue(await IsAncestorAsync(bareDir, mainBefore, binding.CandidateCommit), "the target is an ancestor of the candidate");
                        AssertEqual("a.txt\nb.txt\nc.txt", await RunGitCaptureAsync(bareDir, "show", binding.CandidateCommit + ":" + GeneratedManifest).ConfigureAwait(false),
                            "the candidate carries the regenerated artifact for both sides");
                        AssertEqual(1, binding.PreparationAttempt, "first preparation");
                        AssertNull(binding.ApprovedUtc, "no approval before the Judge reruns");

                        AssertEqual(MissionStatusEnum.Pending, judge.Status, "the Judge waits to rerun at the candidate");
                        AssertEqual(binding.CandidateCommit, judge.CommitHash, "the Judge reruns at the candidate");
                        AssertEqual(stages.Worker.Id, judge.DependsOnMissionId, "the Judge keeps its dependency");
                        AssertEqual(stages.Judge.RecoveryAttempts, judge.RecoveryAttempts, "preparation spends no recovery attempt");
                        AssertEqual(stages.Judge.LandingRetryCount, judge.LandingRetryCount, "preparation spends no landing retry");
                        AssertContains("first review", judge.PreservedRunOutput ?? String.Empty, "the earlier Judge output is preserved");

                        List<Mission> voyageMissions = await testDb.Driver.Missions.EnumerateByVoyageAsync(judge.VoyageId!).ConfigureAwait(false);
                        AssertEqual(3, voyageMissions.Count, "no new Judge mission is created");
                        AssertEqual(1, voyageMissions.Count(m => m.GeneratedArtifactReview != null), "only the Judge carries a binding");
                        AssertEqual(voyagesBefore, (await testDb.Driver.Voyages.EnumerateAsync().ConfigureAwait(false)).Count, "no new voyage is created");
                        ProductionFactPage<MissionAttemptFact> facts = await testDb.Driver.MissionAttemptFacts.EnumerateByMissionAsync(
                            Constants.DefaultTenantId, judge.Id).ConfigureAwait(false);
                        AssertTrue(facts.Items.Any(f => f.FactType == MissionAttemptFactTypeEnum.Retried && f.ReasonCode == "generated_candidate_review"),
                            "an attempt fact records the rerun with its bounded reason");

                        MergeEntry parked = (await testDb.Driver.MergeEntries.ReadAsync(stages.Entry.Id).ConfigureAwait(false))!;
                        AssertNotEqual(MergeStatusEnum.Landed, parked.Status, "the entry does not land before approval");
                        AssertContains("generated_candidate_review_pending", parked.TestOutput ?? String.Empty, "the entry names the wait");
                        AssertEqual(mainBefore, await RunGitCaptureAsync(bareDir, "rev-parse", "main").ConfigureAwait(false), "the target does not move");
                        Mission recorder = (await testDb.Driver.Missions.ReadAsync(stages.Recorder.Id).ConfigureAwait(false))!;
                        AssertEqual(MissionStatusEnum.WorkProduced, recorder.Status, "the later stage stays blocked");

                        await queue.ProcessEntryByIdAsync(stages.Entry.Id).ConfigureAwait(false);
                        Mission again = (await testDb.Driver.Missions.ReadAsync(stages.Judge.Id).ConfigureAwait(false))!;
                        AssertEqual(1, again.GeneratedArtifactReview!.PreparationAttempt, "a second pass over the waiting entry does not prepare again");
                        AssertEqual(binding.CandidateCommit, again.GeneratedArtifactReview.CandidateCommit, "the candidate is unchanged");
                        AssertEqual(mainBefore, await RunGitCaptureAsync(bareDir, "rev-parse", "main").ConfigureAwait(false), "the target still does not move");
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); } catch (Exception ex) { Console.WriteLine("Test cleanup failed: " + ex.Message); }
                }
            });

            foreach (bool diverged in new[] { false, true })
            await RunTest("A declared-artifact voyage without a Judge "
                + (diverged ? "refuses required regeneration by name" : "keeps ordinary fast-forward landing"), async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_nojudge_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GeneratedManifestRepos repos = await CreateGeneratedManifestReposAsync(rootDir, diverged, "b.txt").ConfigureAwait(false);
                    string before = await RunGitCaptureAsync(repos.BareDir, "rev-parse", "main").ConfigureAwait(false);
                    string source = await RunGitCaptureAsync(repos.BareDir, "rev-parse", repos.Branches[0]).ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        PreparingQueue prepared = await CreatePreparingQueueAsync(testDb, repos.BareDir).ConfigureAwait(false);
                        RoutedStages stages = await CreateRoutedStagesAsync(testDb, prepared.Vessel, repos.Branches[0], source, "no-judge").ConfigureAwait(false);
                        await testDb.Driver.Missions.DeleteAsync(stages.Recorder.Id).ConfigureAwait(false);
                        await testDb.Driver.Missions.DeleteAsync(stages.Judge.Id).ConfigureAwait(false);
                        stages.Worker.Status = MissionStatusEnum.WorkProduced;
                        await testDb.Driver.Missions.UpdateAsync(stages.Worker).ConfigureAwait(false);
                        stages.Entry.MissionId = stages.Worker.Id;
                        await testDb.Driver.MergeEntries.UpdateAsync(stages.Entry).ConfigureAwait(false);
                        await prepared.Queue.ProcessEntryByIdAsync(stages.Entry.Id).ConfigureAwait(false);
                        MergeEntry after = (await testDb.Driver.MergeEntries.ReadAsync(stages.Entry.Id).ConfigureAwait(false))!;
                        if (diverged)
                        {
                            AssertNotEqual(MergeStatusEnum.Landed, after.Status);
                            AssertContains("generated_candidate_judge_missing", (after.TestOutput ?? String.Empty) + after.MergeFailureSummary);
                            AssertEqual(before, await RunGitCaptureAsync(repos.BareDir, "rev-parse", "main").ConfigureAwait(false));
                        }
                        else
                        {
                            AssertEqual(MergeStatusEnum.Landed, after.Status, after.TestOutput + after.MergeFailureSummary);
                            AssertEqual(source, await RunGitCaptureAsync(repos.BareDir, "rev-parse", "main").ConfigureAwait(false));
                        }
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); }
                    catch (Exception ex) { Console.WriteLine("Test cleanup failed: " + ex.Message); }
                }
            }).ConfigureAwait(false);

            foreach (int maxRetries in new[] { 0, 2 })
            await RunTest("A generated candidate target drift " + (maxRetries == 0
                ? "ends with a named preparation limit" : "replaces approval with the next bounded review"), async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_drift_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GeneratedManifestRepos repos = await CreateGeneratedManifestReposAsync(rootDir, true, "b.txt").ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        PreparingQueue prepared = await CreatePreparingQueueAsync(testDb, repos.BareDir, maxRetries).ConfigureAwait(false);
                        string source = await RunGitCaptureAsync(repos.BareDir, "rev-parse", repos.Branches[0]).ConfigureAwait(false);
                        RoutedStages stages = await CreateRoutedStagesAsync(testDb, prepared.Vessel, repos.Branches[0], source, "drift").ConfigureAwait(false);
                        await prepared.Queue.ProcessEntryByIdAsync(stages.Entry.Id).ConfigureAwait(false);
                        Mission judge = (await testDb.Driver.Missions.ReadAsync(stages.Judge.Id).ConfigureAwait(false))!;
                        GeneratedArtifactReviewBinding prior = judge.GeneratedArtifactReview!;
                        AssertNotNull(prior);
                        judge.Status = MissionStatusEnum.WorkProduced;
                        await testDb.Driver.Missions.UpdateAsync(judge).ConfigureAwait(false);
                        judge = (await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false))!;
                        GeneratedArtifactReviewBinding fixtureApproval = new GeneratedArtifactReviewBinding
                        {
                            SourceCommit = prior.SourceCommit, TargetBaseCommit = prior.TargetBaseCommit,
                            CandidateCommit = prior.CandidateCommit, PreparationAttempt = prior.PreparationAttempt,
                            RequestedUtc = prior.RequestedUtc, ApprovedUtc = DateTime.UtcNow
                        };
                        AssertTrue(await testDb.Driver.Missions.TryWriteGeneratedArtifactReviewAsync(judge, fixtureApproval, false).ConfigureAwait(false),
                            "Fixture approval measures publication control, not a real Judge verdict.");
                        string targetWorktree = Path.Combine(rootDir, "target-drift");
                        await RunGitAsync(repos.BareDir, "worktree", "add", "--detach", targetWorktree, "main").ConfigureAwait(false);
                        string movedTarget;
                        try
                        {
                            await AddAuthoredAsync(targetWorktree, "d.txt", "Advance the landing base").ConfigureAwait(false);
                            movedTarget = await RunGitCaptureAsync(targetWorktree, "rev-parse", "HEAD").ConfigureAwait(false);
                            await RunGitAsync(targetWorktree, "push", "origin", "HEAD:refs/heads/main").ConfigureAwait(false);
                            await RunGitAsync(repos.BareDir, "update-ref", "refs/heads/main", movedTarget, prior.TargetBaseCommit).ConfigureAwait(false);
                        }
                        finally { await RunGitAsync(repos.BareDir, "worktree", "remove", targetWorktree).ConfigureAwait(false); }
                        await prepared.Queue.ProcessEntryByIdAsync(stages.Entry.Id).ConfigureAwait(false);
                        MergeEntry entry = (await testDb.Driver.MergeEntries.ReadAsync(stages.Entry.Id).ConfigureAwait(false))!;
                        Mission after = (await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false))!;
                        AssertEqual(movedTarget, await RunGitCaptureAsync(repos.BareDir, "rev-parse", "main").ConfigureAwait(false),
                            "A stale approval never moves the target.");
                        AssertEqual(stages.Judge.RecoveryAttempts, after.RecoveryAttempts);
                        AssertEqual(stages.Judge.LandingRetryCount, after.LandingRetryCount);
                        if (maxRetries == 0)
                        {
                            AssertEqual(MergeStatusEnum.Failed, entry.Status, "A spent preparation bound is terminal, not a parked wait.");
                            AssertContains("generated_candidate_drift_limit", (entry.TestOutput ?? String.Empty) + entry.MergeFailureSummary);
                            AssertEqual(1, after.GeneratedArtifactReview!.PreparationAttempt);
                        }
                        else
                        {
                            AssertEqual(MergeStatusEnum.Queued, entry.Status);
                            AssertEqual(MissionStatusEnum.Pending, after.Status);
                            AssertEqual(2, after.GeneratedArtifactReview!.PreparationAttempt);
                            AssertEqual(prior.CandidateCommit, after.GeneratedArtifactReview.SourceCommit);
                            AssertEqual(movedTarget, after.GeneratedArtifactReview.TargetBaseCommit);
                            AssertNull(after.GeneratedArtifactReview.ApprovedUtc, "Earlier approval cannot carry over after drift.");
                            AssertNotEqual(prior.CandidateCommit, after.GeneratedArtifactReview.CandidateCommit);
                            AssertEqual("a.txt\nb.txt\nc.txt\nd.txt", await RunGitCaptureAsync(repos.BareDir, "show",
                                after.GeneratedArtifactReview.CandidateCommit + ":" + GeneratedManifest).ConfigureAwait(false));
                        }
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); }
                    catch (Exception ex) { Console.WriteLine("Test cleanup failed: " + ex.Message); }
                }
            }).ConfigureAwait(false);

            await RunTest("Native publication drift re-prepares the same Judge without spending a landing retry", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_native_drift_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GeneratedManifestRepos repos = await CreateGeneratedManifestReposAsync(rootDir, true, "b.txt").ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        PreparingQueue prepared = await CreatePreparingQueueAsync(testDb, repos.BareDir, 2).ConfigureAwait(false);
                        prepared.Vessel.LandingMode = LandingModeEnum.LocalMerge;
                        prepared.Vessel.WorkingDirectory = Path.Combine(rootDir, "source");
                        await testDb.Driver.Vessels.UpdateAsync(prepared.Vessel).ConfigureAwait(false);
                        string source = await RunGitCaptureAsync(repos.BareDir, "rev-parse", repos.Branches[0]).ConfigureAwait(false);
                        RoutedStages stages = await CreateRoutedStagesAsync(testDb, prepared.Vessel, repos.Branches[0], source, "native-drift").ConfigureAwait(false);
                        await prepared.Queue.ProcessEntryByIdAsync(stages.Entry.Id).ConfigureAwait(false);
                        Mission judge = (await testDb.Driver.Missions.ReadAsync(stages.Judge.Id).ConfigureAwait(false))!;
                        GeneratedArtifactReviewBinding prior = judge.GeneratedArtifactReview!;
                        judge.Status = MissionStatusEnum.WorkProduced;
                        await testDb.Driver.Missions.UpdateAsync(judge).ConfigureAwait(false);
                        judge = (await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false))!;
                        AssertTrue(await testDb.Driver.Missions.TryWriteGeneratedArtifactReviewAsync(judge,
                            new GeneratedArtifactReviewBinding
                            {
                                SourceCommit = prior.SourceCommit, TargetBaseCommit = prior.TargetBaseCommit,
                                CandidateCommit = prior.CandidateCommit, PreparationAttempt = prior.PreparationAttempt,
                                RequestedUtc = prior.RequestedUtc, ApprovedUtc = DateTime.UtcNow
                            }, false).ConfigureAwait(false), "Fixture approval isolates the native publication race.");
                        LoggingModule logging = CreateLogging();
                        GitService git = new GitService(logging);
                        WorkflowProfileService profiles = new WorkflowProfileService(testDb.Driver, logging);
                        await testDb.Driver.WorkflowProfiles.CreateAsync(new WorkflowProfile
                        {
                            TenantId = prepared.Vessel.TenantId, UserId = prepared.Vessel.UserId,
                            Name = "Native candidate commands", Scope = WorkflowProfileScopeEnum.Vessel,
                            VesselId = prepared.Vessel.Id, IsDefault = true,
                            BuildCommand = "git rev-parse HEAD", UnitTestCommand = "git status --porcelain && git rev-parse HEAD"
                        }).ConfigureAwait(false);
                        CheckRunService checks = new CheckRunService(testDb.Driver, profiles,
                            new VesselReadinessService(testDb.Driver, profiles, logging), logging);
                        LandingService landing = new LandingService(logging, testDb.Driver, prepared.Settings, git);
                        landing.SetIntegrationValidator(new IntegrationCandidateValidator(logging, testDb.Driver, prepared.Settings, checks, profiles));
                        string? movedTarget = null;
                        landing.GeneratedArtifactPreparer = async (mission, target, token) =>
                        {
                            GeneratedArtifactLandingResult result = await prepared.Missions.PrepareGeneratedArtifactsForTargetAsync(mission, target, token).ConfigureAwait(false);
                            if (movedTarget == null && result.Outcome == "Regenerated")
                            {
                                string worktree = Path.Combine(rootDir, "late-target");
                                await RunGitAsync(repos.BareDir, "worktree", "add", "--detach", worktree, "main").ConfigureAwait(false);
                                try
                                {
                                    await AddAuthoredAsync(worktree, "d.txt", "Move target during candidate validation").ConfigureAwait(false);
                                    movedTarget = await RunGitCaptureAsync(worktree, "rev-parse", "HEAD").ConfigureAwait(false);
                                    await RunGitAsync(worktree, "push", "origin", "HEAD:refs/heads/main").ConfigureAwait(false);
                                    await RunGitAsync(repos.BareDir, "update-ref", "refs/heads/main", movedTarget, prior.TargetBaseCommit).ConfigureAwait(false);
                                }
                                finally { await RunGitAsync(repos.BareDir, "worktree", "remove", worktree).ConfigureAwait(false); }
                            }
                            return result;
                        };
                        Mission leaf = (await testDb.Driver.Missions.ReadAsync(stages.Recorder.Id).ConfigureAwait(false))!;
                        AssertFalse(await landing.MergeInDedicatedWorktreeAsync(prepared.Vessel, leaf, "main", leaf.BranchName).ConfigureAwait(false));
                        Mission afterJudge = (await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false))!;
                        Mission afterLeaf = (await testDb.Driver.Missions.ReadAsync(leaf.Id).ConfigureAwait(false))!;
                        AssertNotNull(movedTarget, "The fixture moves the target after candidate preparation.");
                        AssertEqual(MissionStatusEnum.Pending, afterJudge.Status,
                            "Native landing reason: " + leaf.FailureReason + "; stored reason: " + afterLeaf.FailureReason);
                        AssertEqual(2, afterJudge.GeneratedArtifactReview!.PreparationAttempt);
                        AssertNull(afterJudge.GeneratedArtifactReview.ApprovedUtc);
                        AssertEqual(movedTarget, afterJudge.GeneratedArtifactReview.TargetBaseCommit);
                        AssertEqual(prior.CandidateCommit, afterJudge.GeneratedArtifactReview.SourceCommit);
                        AssertEqual(stages.Recorder.LandingRetryCount, afterLeaf.LandingRetryCount,
                            "Generated preparation uses its own bound, not the ordinary landing-retry budget.");
                        AssertEqual(stages.Judge.RecoveryAttempts, afterJudge.RecoveryAttempts);
                        AssertEqual(movedTarget, await RunGitCaptureAsync(repos.BareDir, "rev-parse", "main").ConfigureAwait(false));
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); }
                    catch (Exception ex) { Console.WriteLine("Test cleanup failed: " + ex.Message); }
                }
            }).ConfigureAwait(false);

            await RunTest("A busy generated branch defers handoff and retries normally without an operator hold", async () =>
            {
                string rootDir = Path.Combine(Path.GetTempPath(), "armada_generated_busy_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(rootDir);
                    GeneratedManifestRepos repos = await CreateGeneratedManifestReposAsync(rootDir, true, "b.txt").ConfigureAwait(false);
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        PreparingQueue prepared = await CreatePreparingQueueAsync(testDb, repos.BareDir).ConfigureAwait(false);
                        string source = await RunGitCaptureAsync(repos.BareDir, "rev-parse", repos.Branches[0]).ConfigureAwait(false);
                        RoutedStages stages = await CreateRoutedStagesAsync(testDb, prepared.Vessel, repos.Branches[0], source, "busy").ConfigureAwait(false);
                        stages.Judge.Status = MissionStatusEnum.WorkProduced;
                        await testDb.Driver.Missions.UpdateAsync(stages.Judge).ConfigureAwait(false);
                        stages.Recorder.Status = MissionStatusEnum.Pending;
                        stages.Recorder.BranchName = "unprepared-recorder";
                        await testDb.Driver.Missions.UpdateAsync(stages.Recorder).ConfigureAwait(false);
                        Mission busy = await testDb.Driver.Missions.CreateAsync(new Mission("Existing branch writer")
                        {
                            TenantId = Constants.DefaultTenantId, VesselId = prepared.Vessel.Id, VoyageId = stages.Judge.VoyageId,
                            Persona = "Worker", Status = MissionStatusEnum.InProgress, BranchName = repos.Branches[0]
                        }).ConfigureAwait(false);
                        await prepared.Missions.RecoverDanglingHandoffsAsync().ConfigureAwait(false);
                        Mission waiting = (await testDb.Driver.Missions.ReadAsync(stages.Judge.Id).ConfigureAwait(false))!;
                        AssertFalse(waiting.HeldForOperatorReview, "A live writer is a temporary wait, not a permanent operator decision.");
                        AssertNull(waiting.GeneratedArtifactReview, "No candidate is prepared while another stage can commit.");
                        AssertEqual(MissionStatusEnum.WorkProduced, waiting.Status);
                        busy.Status = MissionStatusEnum.Complete;
                        await testDb.Driver.Missions.UpdateAsync(busy).ConfigureAwait(false);
                        await prepared.Missions.RecoverDanglingHandoffsAsync().ConfigureAwait(false);
                        Mission preparedJudge = (await testDb.Driver.Missions.ReadAsync(stages.Judge.Id).ConfigureAwait(false))!;
                        AssertFalse(preparedJudge.HeldForOperatorReview);
                        AssertEqual(MissionStatusEnum.Pending, preparedJudge.Status);
                        AssertEqual(1, preparedJudge.GeneratedArtifactReview!.PreparationAttempt);
                        AssertEqual(stages.Judge.RecoveryAttempts, preparedJudge.RecoveryAttempts);
                        AssertEqual(stages.Judge.LandingRetryCount, preparedJudge.LandingRetryCount);
                    }
                }
                finally
                {
                    try { Directory.Delete(rootDir, true); }
                    catch (Exception ex) { Console.WriteLine("Test cleanup failed: " + ex.Message); }
                }
            }).ConfigureAwait(false);

            foreach (bool firstLandsB in new[] { false, true })
            {
                string order = firstLandsB ? "second voyage lands first" : "first voyage lands first";
                await RunTest("Parallel voyages that both change a declared artifact: the later landing regenerates, waits, and after a fixture approval lands at the candidate (" + order + ")", async () =>
                {
                    string rootDir = Path.Combine(Path.GetTempPath(), "armada_mq_parallel_" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        Directory.CreateDirectory(rootDir);
                        GeneratedManifestRepos repos = await CreateGeneratedManifestReposAsync(rootDir, false, "b.txt", "c.txt").ConfigureAwait(false);
                        string bareDir = repos.BareDir;
                        string[] branches = repos.Branches;
                        string[] ordered = firstLandsB ? new[] { branches[1], branches[0] } : branches;

                        using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                        {
                            PreparingQueue prepared = await CreatePreparingQueueAsync(testDb, bareDir).ConfigureAwait(false);
                            MergeQueueService queue = prepared.Queue;
                            Vessel vessel = prepared.Vessel;
                            string firstSource = await RunGitCaptureAsync(bareDir, "rev-parse", ordered[0]).ConfigureAwait(false);
                            string secondSource = await RunGitCaptureAsync(bareDir, "rev-parse", ordered[1]).ConfigureAwait(false);
                            RoutedStages first = await CreateRoutedStagesAsync(testDb, vessel, ordered[0], firstSource, "one").ConfigureAwait(false);
                            RoutedStages second = await CreateRoutedStagesAsync(testDb, vessel, ordered[1], secondSource, "two").ConfigureAwait(false);

                            await queue.ProcessEntryByIdAsync(first.Entry.Id).ConfigureAwait(false);
                            MergeEntry firstEntry = (await testDb.Driver.MergeEntries.ReadAsync(first.Entry.Id).ConfigureAwait(false))!;
                            AssertEqual(MergeStatusEnum.Landed, firstEntry.Status, "the first branch extends the target and lands; output: " + firstEntry.TestOutput);
                            string mainAfterFirst = await RunGitCaptureAsync(bareDir, "rev-parse", "main").ConfigureAwait(false);
                            AssertEqual(firstSource, mainAfterFirst, "the first landing is an ordinary fast-forward");
                            AssertNull((await testDb.Driver.Missions.ReadAsync(first.Judge.Id).ConfigureAwait(false))!.GeneratedArtifactReview,
                                "the first Judge is not rerun");

                            await queue.ProcessEntryByIdAsync(second.Entry.Id).ConfigureAwait(false);
                            MergeEntry parked = (await testDb.Driver.MergeEntries.ReadAsync(second.Entry.Id).ConfigureAwait(false))!;
                            AssertNotEqual(MergeStatusEnum.Landed, parked.Status, "the second branch waits for its rerun Judge");
                            AssertEqual(mainAfterFirst, await RunGitCaptureAsync(bareDir, "rev-parse", "main").ConfigureAwait(false), "the target does not move");
                            Mission judge = (await testDb.Driver.Missions.ReadAsync(second.Judge.Id).ConfigureAwait(false))!;
                            GeneratedArtifactReviewBinding? binding = judge.GeneratedArtifactReview;
                            AssertNotNull(binding, "the second Judge is bound to a regenerated candidate");
                            AssertEqual(mainAfterFirst, binding!.TargetBaseCommit, "the candidate is prepared on the moved target");
                            AssertEqual("a.txt\nb.txt\nc.txt", await RunGitCaptureAsync(bareDir, "show", binding.CandidateCommit + ":" + GeneratedManifest).ConfigureAwait(false),
                                "the regenerated artifact includes both voyages");

                            judge.Status = MissionStatusEnum.WorkProduced;
                            await testDb.Driver.Missions.UpdateAsync(judge).ConfigureAwait(false);
                            Mission produced = (await testDb.Driver.Missions.ReadAsync(judge.Id).ConfigureAwait(false))!;
                            GeneratedArtifactReviewBinding approved = new GeneratedArtifactReviewBinding
                            {
                                SourceCommit = binding.SourceCommit,
                                CandidateCommit = binding.CandidateCommit,
                                TargetBaseCommit = binding.TargetBaseCommit,
                                PreparationAttempt = binding.PreparationAttempt,
                                RequestedUtc = binding.RequestedUtc,
                                ApprovedUtc = binding.RequestedUtc.AddMinutes(1)
                            };
                            AssertTrue(await testDb.Driver.Missions.TryWriteGeneratedArtifactReviewAsync(produced, approved, false).ConfigureAwait(false),
                                "the fixture writes the approval directly; this is a publication control, not a Judge PASS or current Checks");

                            await queue.ProcessEntryByIdAsync(second.Entry.Id).ConfigureAwait(false);
                            MergeEntry landed = (await testDb.Driver.MergeEntries.ReadAsync(second.Entry.Id).ConfigureAwait(false))!;
                            AssertEqual(MergeStatusEnum.Landed, landed.Status, "the fixture-approved candidate lands; output: " + landed.TestOutput);
                            AssertEqual(binding.CandidateCommit, await RunGitCaptureAsync(bareDir, "rev-parse", "main").ConfigureAwait(false),
                                "the target moves onto exactly the approved candidate");
                        }
                    }
                    finally
                    {
                        try { Directory.Delete(rootDir, true); } catch (Exception ex) { Console.WriteLine("Test cleanup failed: " + ex.Message); }
                    }
                });
            }
        }

        /// <summary>
        /// Build source -> remote bare -> local bare where each named file is added on its own branch from a shared base,
        /// with the declared manifest regenerated on every commit. With <paramref name="advanceMain"/> the target also adds
        /// c.txt, so the manifest differs on both sides.
        /// </summary>
        private static async Task<GeneratedManifestRepos> CreateGeneratedManifestReposAsync(string rootDir, bool advanceMain, params string[] files)
        {
            string remoteDir = Path.Combine(rootDir, "remote.git");
            string sourceDir = Path.Combine(rootDir, "source");
            string bareDir = Path.Combine(rootDir, "bare.git");
            Directory.CreateDirectory(Path.Combine(sourceDir, "authored"));
            Directory.CreateDirectory(Path.Combine(sourceDir, "tools"));
            await RunGitAsync(sourceDir, "init", "-b", "main").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "tools", "regen.sh"), "#!/bin/sh\nset -e\nLC_ALL=C ls authored | sort > manifest.json\n").ConfigureAwait(false);
            await AddAuthoredAsync(sourceDir, "a.txt", "Initial commit").ConfigureAwait(false);

            List<string> branches = new List<string>();
            foreach (string file in files)
            {
                string branch = "armada/captain-generated/msn_" + Path.GetFileNameWithoutExtension(file);
                await RunGitAsync(sourceDir, "checkout", "-b", branch, "main").ConfigureAwait(false);
                await AddAuthoredAsync(sourceDir, file, "Add " + file).ConfigureAwait(false);
                branches.Add(branch);
            }
            await RunGitAsync(sourceDir, "checkout", "main").ConfigureAwait(false);
            if (advanceMain) await AddAuthoredAsync(sourceDir, "c.txt", "Target adds c.txt").ConfigureAwait(false);

            await RunGitAsync(rootDir, "clone", "--bare", sourceDir, remoteDir).ConfigureAwait(false);
            await RunGitAsync(rootDir, "clone", "--bare", remoteDir, bareDir).ConfigureAwait(false);
            await RunGitAsync(bareDir, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(bareDir, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
            return new GeneratedManifestRepos { BareDir = bareDir, Branches = branches.ToArray() };
        }

        private static async Task AddAuthoredAsync(string sourceDir, string file, string message)
        {
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "authored", file), file + "\n").ConfigureAwait(false);
            IEnumerable<string> names = Directory.GetFiles(Path.Combine(sourceDir, "authored")).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal)!;
            await File.WriteAllTextAsync(Path.Combine(sourceDir, GeneratedManifest), String.Join("\n", names) + "\n").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "add", "-A").ConfigureAwait(false);
            await RunGitAsync(sourceDir, "commit", "-m", message).ConfigureAwait(false);
        }

        private async Task<PreparingQueue> CreatePreparingQueueAsync(TestDatabase testDb, string bareDir, int? maxLandingRetries = null)
        {
            LoggingModule logging = CreateLogging();
            ArmadaSettings settings = CreateSettings();
            if (maxLandingRetries.HasValue) settings.MaxLandingRetries = maxLandingRetries.Value;
            Vessel vessel = new Vessel("generated-vessel", Path.Combine(Path.GetDirectoryName(bareDir)!, "remote.git"));
            vessel.LocalPath = bareDir;
            vessel.TenantId = Constants.DefaultTenantId;
            vessel.DefaultBranch = "main";
            vessel.BranchCleanupPolicy = BranchCleanupPolicyEnum.None;
            vessel.GeneratedArtifacts = new GeneratedArtifactPolicy
            {
                Paths = new List<string> { GeneratedManifest },
                RegenerateCommand = "sh tools/regen.sh"
            };
            vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

            GitService git = new GitService(logging);
            IDockService docks = new DockService(logging, testDb.Driver, settings, git);
            ICaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
            MissionService missions = new MissionService(logging, testDb.Driver, settings, docks, captains, git: git,
                resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
            RecordingMergeFailureClassifier classifier = new RecordingMergeFailureClassifier(
                new MergeFailureClassification(MergeFailureClassEnum.TextConflict, "Recording classifier", new List<string>()));
            MergeQueueService queue = new MergeQueueService(logging, testDb.Driver, settings, git, classifier);
            queue.GeneratedArtifactPreparer = missions.PrepareGeneratedArtifactsForTargetAsync;
            return new PreparingQueue { Queue = queue, Vessel = vessel, Missions = missions, Settings = settings };
        }

        /// <summary>A Worker, a passed Judge and a Recorder in one voyage, with the Recorder queued for landing.</summary>
        private static async Task<RoutedStages> CreateRoutedStagesAsync(TestDatabase testDb, Vessel vessel, string branch, string commit, string label)
        {
            Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("generated-voyage-" + label) { TenantId = Constants.DefaultTenantId }).ConfigureAwait(false);
            Mission worker = await testDb.Driver.Missions.CreateAsync(new Mission("Worker " + label, "Do the work")
            {
                TenantId = Constants.DefaultTenantId, VesselId = vessel.Id, VoyageId = voyage.Id, Persona = "Worker",
                Status = MissionStatusEnum.Complete, BranchName = branch, CommitHash = commit
            }).ConfigureAwait(false);
            Mission judge = await testDb.Driver.Missions.CreateAsync(new Mission("Judge " + label, "Review the work")
            {
                TenantId = Constants.DefaultTenantId, VesselId = vessel.Id, VoyageId = voyage.Id, Persona = "Judge", DependsOnMissionId = worker.Id,
                Status = MissionStatusEnum.Complete, BranchName = branch, CommitHash = commit,
                AgentOutput = "[ARMADA:VERDICT] PASS " + label + " review"
            }).ConfigureAwait(false);
            Mission recorder = await testDb.Driver.Missions.CreateAsync(new Mission("Recorder " + label, "Record the work")
            {
                TenantId = Constants.DefaultTenantId, VesselId = vessel.Id, VoyageId = voyage.Id, Persona = "Recorder", DependsOnMissionId = judge.Id,
                Status = MissionStatusEnum.WorkProduced, BranchName = branch, CommitHash = commit
            }).ConfigureAwait(false);

            MergeEntry entry = new MergeEntry();
            entry.TenantId = Constants.DefaultTenantId;
            entry.VesselId = vessel.Id;
            entry.MissionId = recorder.Id;
            entry.BranchName = branch;
            entry.TargetBranch = "main";
            entry.Status = MergeStatusEnum.Queued;
            entry.CreatedUtc = DateTime.UtcNow;
            entry.LastUpdateUtc = DateTime.UtcNow;
            await testDb.Driver.MergeEntries.CreateAsync(entry).ConfigureAwait(false);
            return new RoutedStages { Worker = worker, Judge = judge, Recorder = recorder, Entry = entry };
        }

        private static async Task<bool> IsAncestorAsync(string repository, string ancestor, string descendant)
        {
            try
            {
                await RunGitAsync(repository, "merge-base", "--is-ancestor", ancestor, descendant).ConfigureAwait(false);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }
}
