namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Unit coverage for the captain "false complete" detection. The platform catches
    /// GLM 5.2 / Zyloo captains that emit [ARMADA:RESULT] COMPLETE after running
    /// briefly with no diff and a tiny AgentOutput, so the rescue path can retry
    /// with a different captain rather than let the mission reach WorkProduced.
    /// </summary>
    public class MissionServiceNoOpCompletionTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "MissionService No-Op Completion Detection";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("DetectNoOpCompletion_ImplementationShortRuntimeEmptyDiff_ReturnsTrue", () =>
            {
                Mission mission = new Mission
                {
                    Id = "msn_test_x",
                    Mode = MissionModeEnum.Implementation,
                };
                TimeSpan runtime = TimeSpan.FromSeconds(8);
                bool detected = MissionService.DetectNoOpCompletion(mission, runtime, 0, 113, true);
                AssertTrue(detected,
                    "An Implementation mission with 8s runtime, 0 diff lines, and 113 chars of AgentOutput is the false-complete pattern.");
            }).ConfigureAwait(false);

            await RunTest("DetectNoOpCompletion_AuditMode_Exempt", () =>
            {
                Mission mission = new Mission
                {
                    Id = "msn_test_y",
                    Mode = MissionModeEnum.Audit,
                };
                TimeSpan runtime = TimeSpan.FromSeconds(8);
                bool detected = MissionService.DetectNoOpCompletion(mission, runtime, 0, 113, true);
                AssertFalse(detected,
                    "Audit-mode missions can legitimately have no diff; they are exempt from the false-complete check.");
            }).ConfigureAwait(false);

            await RunTest("DetectNoOpCompletion_ResearchMode_Exempt", () =>
            {
                Mission mission = new Mission
                {
                    Id = "msn_test_z",
                    Mode = MissionModeEnum.Research,
                };
                TimeSpan runtime = TimeSpan.FromSeconds(8);
                bool detected = MissionService.DetectNoOpCompletion(mission, runtime, 0, 113, true);
                AssertFalse(detected,
                    "Research-mode missions can legitimately have no diff; they are exempt from the false-complete check.");
            }).ConfigureAwait(false);

            await RunTest("DetectNoOpCompletion_LongRuntime_ReturnsFalse", () =>
            {
                Mission mission = new Mission
                {
                    Id = "msn_test_long",
                    Mode = MissionModeEnum.Implementation,
                };
                TimeSpan runtime = TimeSpan.FromSeconds(120);
                bool detected = MissionService.DetectNoOpCompletion(mission, runtime, 0, 113, true);
                AssertFalse(detected,
                    "A 120-second runtime with no diff is suspicious but no longer the false-complete pattern; let it pass to DoD gate for judgment.");
            }).ConfigureAwait(false);

            await RunTest("DetectNoOpCompletion_NonEmptyDiff_ReturnsFalse", () =>
            {
                Mission mission = new Mission
                {
                    Id = "msn_test_diff",
                    Mode = MissionModeEnum.Implementation,
                };
                TimeSpan runtime = TimeSpan.FromSeconds(8);
                bool detected = MissionService.DetectNoOpCompletion(mission, runtime, 235, 113, true);
                AssertFalse(detected,
                    "Any non-zero diff count means the captain actually wrote something; let it pass.");
            }).ConfigureAwait(false);

            await RunTest("DetectNoOpCompletion_LongAgentOutput_ReturnsFalse", () =>
            {
                Mission mission = new Mission
                {
                    Id = "msn_test_summary",
                    Mode = MissionModeEnum.Implementation,
                };
                TimeSpan runtime = TimeSpan.FromSeconds(8);
                bool detected = MissionService.DetectNoOpCompletion(mission, runtime, 0, 1500, true);
                AssertFalse(detected,
                    "An AgentOutput >= 200 chars holds a real summary; the captain wrote something. Let it pass.");
            }).ConfigureAwait(false);

            await RunTest("DetectNoOpCompletion_NullMission_ReturnsFalse", () =>
            {
                bool detected = MissionService.DetectNoOpCompletion(null, TimeSpan.FromSeconds(8), 0, 113, true);
                AssertFalse(detected,
                    "Null mission must not throw; it is exempt.");
            }).ConfigureAwait(false);

            
            await RunTest("DetectNoOpCompletion_NoAgentOutput_ReturnsFalse", () =>
            {
                Mission mission = new Mission
                {
                    Id = "msn_test_stub",
                    Mode = MissionModeEnum.Implementation,
                    AgentOutput = null,
                };
                TimeSpan runtime = TimeSpan.FromSeconds(8);
                bool detected = MissionService.DetectNoOpCompletion(mission, runtime, 0, 113, false);
                AssertFalse(detected,
                    "When AgentOutput is null the captain never wrote a result line; this is the unit-test stub signature, not a false-complete.");
            }).ConfigureAwait(false);

            await RunTest("DetectNoOpCompletion_WithAgentOutputSet_Detects", () =>
            {
                Mission mission = new Mission
                {
                    Id = "msn_test_real",
                    Mode = MissionModeEnum.Implementation,
                    AgentOutput = "[ARMADA:RESULT] COMPLETE\nFull AGENTS.md read. Mission objectives and operating rules loaded for strict execution.",
                };
                TimeSpan runtime = TimeSpan.FromSeconds(8);
                bool detected = MissionService.DetectNoOpCompletion(mission, runtime, 0, 113, true);
                AssertTrue(detected,
                    "A real captain with AgentOutput set + 8s runtime + 0 diff lines + 113-char output is the false-complete pattern.");
            }).ConfigureAwait(false);


            await RunTest("BuildNoOpCompletionFailureReason_ContainsRuntimeAndOutputLength", () =>
            {
                string reason = MissionService.BuildNoOpCompletionFailureReason(TimeSpan.FromSeconds(7.5), 113);
                AssertTrue(reason.Contains("7.5"),
                    "Failure reason must name the runtime so a future operator can see how short the captain ran.");
                AssertTrue(reason.Contains("113"),
                    "Failure reason must name the AgentOutput length so a future operator can see the no-op signature.");
                AssertTrue(reason.Contains("no_op_completion_detected"),
                    "Failure reason must carry a stable token so the rescue path and operator grep can find it.");
            }).ConfigureAwait(false);
        }
    }
}
