namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// The launch-attempt rule the completion de-duplication guard uses to tell a new launch from a
    /// late duplicate of the launch it already handled.
    /// </summary>
    public sealed class MissionCompletionAttemptTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Mission Completion Attempt";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            DateTime first = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            await RunTest("Of_MissionNotLaunched_HasNoAttempt", () =>
            {
                Mission requeued = new Mission("requeued");
                requeued.ProcessId = 4100;
                AssertNull(MissionCompletionAttempt.Of(requeued), "a mission with no start time has not been launched");
                AssertNull(MissionCompletionAttempt.Of(null), "no mission has no attempt");
            }).ConfigureAwait(false);

            await RunTest("IsNewAttempt_UnlaunchedMission_IsADuplicateOfTheHandledLaunch", () =>
            {
                MissionCompletionAttempt handled = new MissionCompletionAttempt(first, 4100);
                AssertFalse(MissionCompletionAttempt.IsNewAttempt(handled, null), "a requeued mission waiting for assignment is not a new launch");
            }).ConfigureAwait(false);

            await RunTest("IsNewAttempt_SameLaunchWithProcessCleared_IsADuplicate", () =>
            {
                MissionCompletionAttempt handled = new MissionCompletionAttempt(first, 4100);
                AssertFalse(MissionCompletionAttempt.IsNewAttempt(handled, new MissionCompletionAttempt(first, null)),
                    "the handler clears the process id; that is not a new launch");
                AssertFalse(MissionCompletionAttempt.IsNewAttempt(handled, new MissionCompletionAttempt(first, 4100)),
                    "the same start and process is the same launch");
            }).ConfigureAwait(false);

            await RunTest("IsNewAttempt_LaterStartTime_IsNew", () =>
            {
                MissionCompletionAttempt handled = new MissionCompletionAttempt(first, 4100);
                AssertTrue(MissionCompletionAttempt.IsNewAttempt(handled, new MissionCompletionAttempt(first.AddSeconds(5), null)),
                    "a relaunch records a new start time");
            }).ConfigureAwait(false);

            await RunTest("IsNewAttempt_SameStartNewProcess_IsNew", () =>
            {
                MissionCompletionAttempt handled = new MissionCompletionAttempt(first, 4100);
                AssertTrue(MissionCompletionAttempt.IsNewAttempt(handled, new MissionCompletionAttempt(first, 4200)),
                    "an in-place relaunch keeps the start time but starts a new process");
            }).ConfigureAwait(false);

            await RunTest("IsNewAttempt_HandledCompletionForAnUnlaunchedMission_AnyLaunchIsNew", () =>
            {
                AssertTrue(MissionCompletionAttempt.IsNewAttempt(null, new MissionCompletionAttempt(first, 4100)),
                    "a launch after a completion handled with no launch recorded is new");
            }).ConfigureAwait(false);
        }
    }
}
