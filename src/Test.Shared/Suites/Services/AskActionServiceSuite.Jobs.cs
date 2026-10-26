namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Server;
    using Test.Shared.Infrastructure;
    using static Test.Shared.Infrastructure.Asserts;

    public sealed partial class AskActionServiceSuite
    {
        private static async Task AcceptedJobLinksTerminalWorkAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                Fixture fixture = await CreateFixtureAsync(testDb.Driver).ConfigureAwait(false);
                LongRunningJobService jobs = new LongRunningJobService();
                TaskCompletionSource<object?> finishWork = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<string> finished = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                jobs.ExecutionFinished = id => finished.TrySetResult(id);
                AskActionService service = CreateService(testDb.Driver, jobs);
                LongRunningJob? accepted = null;
                int handlerCalls = 0;
                Func<JsonElement?, Task<object>> handler = _ =>
                {
                    handlerCalls++;
                    accepted = jobs.Start("ask_action_test", _ => finishWork.Task);
                    return Task.FromResult<object>(accepted!);
                };
                Func<JsonElement?, Task<object>> tool = Register(service, "armada_create_mission", handler);
                try
                {
                    await CallAsync(tool, ScopedCaller(fixture), Args("{\"title\":\"job\"}")).ConfigureAwait(false);
                    AskActionProposal pending = (await service.ListAsync(fixture.Owner, fixture.Thread.Id).ConfigureAwait(false))![0];
                    AssertEqual(AskProposalStateEnum.Pending, pending.State, "a saved mutation waits for human approval before it starts a job");
                    AssertEqual(0, handlerCalls, "the proposal call does not invoke the job-producing handler");
                    AssertEqual(0, jobs.ListUnfinished().Count, "the proposal call does not start background work");
                    AssertEqual(0, pending.WorkReferences.Count, "a pending proposal has no job reference");

                    AskProposalDecision approved = await service.ApproveAsync(fixture.Owner, fixture.Thread.Id, pending.Id, pending.Fingerprint).ConfigureAwait(false);
                    AssertEqual(AskProposalStateEnum.Executing, approved.Proposal!.State, "owner approval starts the handler and records an active job");
                    AssertEqual(1, handlerCalls);
                    AssertNotNull(accepted);
                    AssertEqual(1, approved.Proposal.WorkReferences.Count);
                    AssertEqual(AskWorkKindEnum.Job, approved.Proposal.WorkReferences[0].Kind);
                    AssertEqual(accepted!.JobId, approved.Proposal.WorkReferences[0].Id);

                    AskActionService recreated = CreateService(testDb.Driver, jobs);
                    Register(recreated, "armada_create_mission", handler);
                    AskProposalDecision replay = await recreated.ApproveAsync(fixture.Owner, fixture.Thread.Id, pending.Id, pending.Fingerprint).ConfigureAwait(false);
                    AssertEqual(AskProposalStateEnum.Executing, replay.Proposal!.State, "a recreated service observes the running job");
                    AssertEqual(1, handlerCalls, "a recreated approval does not start another job");

                    string missionId = new Mission().Id;
                    string voyageId = new Voyage().Id;
                    finishWork.TrySetResult(new { MissionId = missionId, VoyageId = voyageId });
                    AssertEqual(accepted!.JobId, await finished.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false));
                    List<AskActionProposal> observed = await recreated.ListAsync(fixture.Owner, fixture.Thread.Id).ConfigureAwait(false) ?? new List<AskActionProposal>();
                    AssertEqual(AskProposalStateEnum.Executed, observed[0].State);
                    AssertEqual(3, observed[0].WorkReferences.Count);
                    AssertTrue(observed[0].WorkReferences.Exists(work => work.Kind == AskWorkKindEnum.Mission && work.Id == missionId), "the terminal job links the exact mission");
                    AssertTrue(observed[0].WorkReferences.Exists(work => work.Kind == AskWorkKindEnum.Voyage && work.Id == voyageId), "the terminal job links the exact voyage");
                    AssertTrue(observed[0].WorkReferences.Exists(work => work.Kind == AskWorkKindEnum.Job && work.Id == accepted!.JobId), "the accepted job reference remains available");
                }
                finally
                {
                    if (accepted != null && !finished.Task.IsCompleted)
                    {
                        finishWork.TrySetResult(new { Success = true });
                        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                    }
                }
            }
        }

        private static async Task MissingAndLostJobsStayUncertainAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                Fixture missingFixture = await CreateFixtureAsync(testDb.Driver).ConfigureAwait(false);
                AskActionService missingService = CreateService(testDb.Driver);
                int missingHandlerCalls = 0;
                Func<JsonElement?, Task<object>> missingTool = Register(missingService, "armada_create_mission", _ =>
                {
                    missingHandlerCalls++;
                    return Task.FromResult<object>(new LongRunningJob { JobId = "job_not_in_registry" });
                });
                await CallAsync(missingTool, ScopedCaller(missingFixture), Args("{\"title\":\"missing job\"}")).ConfigureAwait(false);
                AskActionProposal missingPending = (await missingService.ListAsync(missingFixture.Owner, missingFixture.Thread.Id).ConfigureAwait(false))![0];
                AssertEqual(AskProposalStateEnum.Pending, missingPending.State);
                AssertEqual(0, missingHandlerCalls, "a missing-job proposal does not run before approval");
                AskProposalDecision missingAccepted = await missingService.ApproveAsync(missingFixture.Owner, missingFixture.Thread.Id, missingPending.Id, missingPending.Fingerprint).ConfigureAwait(false);
                AssertEqual(1, missingHandlerCalls);
                AssertEqual(AskProposalStateEnum.Executing, missingAccepted.Proposal!.State);
                List<AskActionProposal> missingView = await missingService.ListAsync(missingFixture.Owner, missingFixture.Thread.Id).ConfigureAwait(false) ?? new List<AskActionProposal>();
                AssertEqual(AskProposalStateEnum.Uncertain, missingView[0].State, "a missing accepted job is not reported as success or replayed");

                string journal = Path.Combine(Path.GetTempPath(), "ask-action-jobs-" + Guid.NewGuid().ToString("N"));
                LongRunningJob? accepted = null;
                TaskCompletionSource<object?> release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<string> finished = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    Fixture lostFixture = await CreateFixtureAsync(testDb.Driver).ConfigureAwait(false);
                    LongRunningJobService acceptingJobs = new LongRunningJobService(journalDirectory: journal);
                    acceptingJobs.ExecutionFinished = id => finished.TrySetResult(id);
                    AskActionService accepting = CreateService(testDb.Driver, acceptingJobs);
                    int lostHandlerCalls = 0;
                    Func<JsonElement?, Task<object>> lostTool = Register(accepting, "armada_create_mission", _ =>
                    {
                        lostHandlerCalls++;
                        accepted = acceptingJobs.Start("ask_action_lost_test", _ => release.Task);
                        return Task.FromResult<object>(accepted!);
                    });
                    await CallAsync(lostTool, ScopedCaller(lostFixture), Args("{\"title\":\"lost job\"}")).ConfigureAwait(false);
                    AskActionProposal lostPending = (await accepting.ListAsync(lostFixture.Owner, lostFixture.Thread.Id).ConfigureAwait(false))![0];
                    AssertEqual(AskProposalStateEnum.Pending, lostPending.State);
                    AssertEqual(0, lostHandlerCalls, "a lost-job proposal does not start work before approval");
                    AskProposalDecision executing = await accepting.ApproveAsync(lostFixture.Owner, lostFixture.Thread.Id, lostPending.Id, lostPending.Fingerprint).ConfigureAwait(false);
                    AssertEqual(1, lostHandlerCalls);
                    AssertEqual(AskProposalStateEnum.Executing, executing.Proposal!.State);
                    AssertNotNull(accepted);

                    LongRunningJobService recoveredJobs = new LongRunningJobService(journalDirectory: journal);
                    AssertEqual(1, await recoveredJobs.RecoverInterruptedJobsAsync().ConfigureAwait(false));
                    AssertTrue(recoveredJobs.TryGetStatus(accepted!.JobId, out LongRunningJob? lost));
                    AssertEqual(LongRunningJobStatusEnum.Lost, lost!.Status);
                    List<AskActionProposal> lostView = await CreateService(testDb.Driver, recoveredJobs).ListAsync(lostFixture.Owner, lostFixture.Thread.Id).ConfigureAwait(false) ?? new List<AskActionProposal>();
                    AssertEqual(AskProposalStateEnum.Uncertain, lostView[0].State, "a journaled job recorded Lost remains uncertain");
                }
                finally
                {
                    if (accepted != null && !finished.Task.IsCompleted)
                    {
                        release.TrySetResult(new { Success = true });
                        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                    }
                    if (Directory.Exists(journal)) Directory.Delete(journal, true);
                }
            }
        }
    }
}
