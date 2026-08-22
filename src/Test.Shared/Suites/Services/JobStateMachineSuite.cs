namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="JobStateMachine"/> and <see cref="Job"/> progress clamping. Positive
    /// cases confirm the allowed lifecycle transitions and the K-sortable id; negative cases confirm
    /// terminal statuses do not transition, invalid jumps are rejected, and progress clamps to [0,100].
    /// </summary>
    public sealed class JobStateMachineSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the job-state-machine suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("valid_transitions", "Queued->Running->Succeeded/Failed and any->Cancelled are allowed", TestTags.Positive, () =>
            {
                AssertTrue(JobStateMachine.CanTransition(JobStatusEnum.Queued, JobStatusEnum.Running), "queued->running");
                AssertTrue(JobStateMachine.CanTransition(JobStatusEnum.Queued, JobStatusEnum.Cancelled), "queued->cancelled");
                AssertTrue(JobStateMachine.CanTransition(JobStatusEnum.Running, JobStatusEnum.Succeeded), "running->succeeded");
                AssertTrue(JobStateMachine.CanTransition(JobStatusEnum.Running, JobStatusEnum.Failed), "running->failed");
                AssertTrue(JobStateMachine.CanTransition(JobStatusEnum.Running, JobStatusEnum.Cancelled), "running->cancelled");
            }));

            cases.Add(Case("terminal_is_terminal", "Succeeded/Failed/Cancelled are terminal", TestTags.Positive, () =>
            {
                AssertTrue(JobStateMachine.IsTerminal(JobStatusEnum.Succeeded), "succeeded terminal");
                AssertTrue(JobStateMachine.IsTerminal(JobStatusEnum.Failed), "failed terminal");
                AssertTrue(JobStateMachine.IsTerminal(JobStatusEnum.Cancelled), "cancelled terminal");
                AssertFalse(JobStateMachine.IsTerminal(JobStatusEnum.Queued), "queued not terminal");
                AssertFalse(JobStateMachine.IsTerminal(JobStatusEnum.Running), "running not terminal");
            }));

            cases.Add(Case("ksortable_id", "A new job gets a job_ prefixed id", TestTags.Positive, () =>
            {
                Job job = new Job("nightly cleanup", JobKindEnum.Cleanup);
                AssertTrue(job.Id.StartsWith("job_", StringComparison.Ordinal), "expected job_ prefix");
                AssertEqual(JobStatusEnum.Queued, job.Status);
            }));

            cases.Add(Case("terminal_does_not_transition", "A terminal status cannot transition", TestTags.Negative, () =>
            {
                AssertFalse(JobStateMachine.CanTransition(JobStatusEnum.Succeeded, JobStatusEnum.Running), "succeeded->running");
                AssertFalse(JobStateMachine.CanTransition(JobStatusEnum.Cancelled, JobStatusEnum.Running), "cancelled->running");
                AssertFalse(JobStateMachine.CanTransition(JobStatusEnum.Failed, JobStatusEnum.Queued), "failed->queued");
            }));

            cases.Add(Case("invalid_jumps_rejected", "Invalid jumps and no-op transitions are rejected", TestTags.Negative, () =>
            {
                AssertFalse(JobStateMachine.CanTransition(JobStatusEnum.Queued, JobStatusEnum.Succeeded), "queued->succeeded skips running");
                AssertFalse(JobStateMachine.CanTransition(JobStatusEnum.Running, JobStatusEnum.Queued), "running->queued backwards");
                AssertFalse(JobStateMachine.CanTransition(JobStatusEnum.Running, JobStatusEnum.Running), "no-op");
            }));

            cases.Add(Case("progress_clamps", "Progress clamps to [0,100]", TestTags.Negative, () =>
            {
                Job job = new Job();
                job.Progress = 150;
                AssertEqual(100, job.Progress);
                job.Progress = -20;
                AssertEqual(0, job.Progress);
                job.Progress = 42;
                AssertEqual(42, job.Progress);
            }));

            return new TestSuiteDescriptor(
                suiteId: "Services.JobStateMachine",
                displayName: "Job State Machine",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.JobStateMachine",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) =>
                {
                    body();
                    return Task.CompletedTask;
                },
                tags: new List<string> { tag });
        }

        #endregion
    }
}
