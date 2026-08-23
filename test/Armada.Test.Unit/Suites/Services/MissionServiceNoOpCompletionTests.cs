namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Unit coverage for the captain "false complete" detection. The platform catches
    /// GLM 5.2 captains that emit [ARMADA:RESULT] COMPLETE (or DeepSeek V4 Pro
    /// captains that exit 0 with a bare acknowledgment) after running briefly with no
    /// diff and a tiny AgentOutput, so the rescue path can retry with a different
    /// captain rather than let the mission reach WorkProduced.
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

            await RunTest("DetectNoOpCompletion_AuditShortRuntimeTinyOutput_Detects", () =>
            {
                Mission mission = new Mission
                {
                    Id = "msn_test_y",
                    Mode = MissionModeEnum.Audit,
                };
                TimeSpan runtime = TimeSpan.FromSeconds(8);
                bool detected = MissionService.DetectNoOpCompletion(mission, runtime, 0, 113, true);
                AssertTrue(detected,
                    "An Audit mission that completes in 8s with 113 chars of AgentOutput is a false-complete: it read the brief and exited without composing the report that IS its deliverable.");
            }).ConfigureAwait(false);

            await RunTest("DetectNoOpCompletion_ResearchShortRuntimeTinyOutput_Detects", () =>
            {
                Mission mission = new Mission
                {
                    Id = "msn_test_z",
                    Mode = MissionModeEnum.Research,
                };
                TimeSpan runtime = TimeSpan.FromSeconds(8);
                bool detected = MissionService.DetectNoOpCompletion(mission, runtime, 0, 113, true);
                AssertTrue(detected,
                    "A Research mission that completes in 8s with 113 chars of AgentOutput is a false-complete: no report was produced.");
            }).ConfigureAwait(false);

            await RunTest("DetectNoOpCompletion_AuditReportSizedOutput_NotDetected", () =>
            {
                Mission mission = new Mission
                {
                    Id = "msn_test_report",
                    Mode = MissionModeEnum.Audit,
                };
                TimeSpan runtime = TimeSpan.FromSeconds(45);
                bool detected = MissionService.DetectNoOpCompletion(mission, runtime, 0, 1200, true);
                AssertFalse(detected,
                    "An Audit mission with report-sized AgentOutput (1200 chars) is real work and must pass even when the runtime is under 60s.");
            }).ConfigureAwait(false);

            await RunTest("DetectNoOpCompletion_ResearchBriefRestatement_Detects", () =>
            {
                Mission mission = new Mission
                {
                    Id = "msn_test_brief_restatement",
                    Mode = MissionModeEnum.Research,
                };
                TimeSpan runtime = TimeSpan.FromSeconds(10);
                bool detected = MissionService.DetectNoOpCompletion(mission, runtime, 0, 522, true);
                AssertTrue(detected,
                    "A short Research mission with a 522-character brief restatement and no diff is a false-complete.");
            }).ConfigureAwait(false);

            await RunTest("DetectNoOpCompletion_AuditLongRuntimeTinyOutput_Detects", () =>
            {
                Mission mission = new Mission
                {
                    Id = "msn_test_report2",
                    Mode = MissionModeEnum.Audit,
                };
                TimeSpan runtime = TimeSpan.FromSeconds(240);
                bool detected = MissionService.DetectNoOpCompletion(mission, runtime, 0, 100, true);
                AssertTrue(detected,
                    "The report IS the deliverable of a read-only mission, so a 100-character report is a no-op however long the captain ran. " +
                    "A run that spends minutes reading and then delivers nothing is the shape of a stream that died before the report was written, " +
                    "and treating the elapsed time as proof of work is what let one through.");
            }).ConfigureAwait(false);

            await RunTest("DetectNoOpCompletion_LongRuntimeNoMarker_Detects", () =>
            {
                Mission mission = new Mission
                {
                    Id = "msn_test_long",
                    Mode = MissionModeEnum.Implementation,
                };
                TimeSpan runtime = TimeSpan.FromSeconds(120);
                bool detected = MissionService.DetectNoOpCompletion(mission, runtime, 0, 113, true);
                AssertTrue(detected,
                    "A 120-second run that committed nothing and never claimed completion is a no-op. The elapsed time cannot exonerate it, " +
                    "and the DoD gate cannot judge it either: the gate's build and test commands measure the base commit when the diff is empty.");
            }).ConfigureAwait(false);

            await RunTest("DetectNoOpCompletion_LongRuntimeWithMarker_ReturnsFalse", () =>
            {
                Mission mission = new Mission
                {
                    Id = "msn_test_long_claimed",
                    Mode = MissionModeEnum.Implementation,
                    AgentOutput = "[ARMADA:RESULT] COMPLETE\nThe premise did not hold: the construct named in the brief is already absent from the target tip.",
                };
                TimeSpan runtime = TimeSpan.FromSeconds(120);
                bool detected = MissionService.DetectNoOpCompletion(mission, runtime, 0, 113, true);
                AssertFalse(detected,
                    "A captain that ran two minutes and explicitly claimed completion is making a claim a reviewer can judge -- a stale premise " +
                    "leaves nothing to commit. Only the unclaimed run is decided here.");
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

            await RunTest("DetectNoOpCompletion_StaleBranchDiffWithNoNewChanges_ReturnsTrue", () =>
            {
                Mission mission = new Mission
                {
                    Id = "msn_test_stale_branch",
                    Mode = MissionModeEnum.Implementation,
                };
                TimeSpan runtime = TimeSpan.FromSeconds(8);
                bool detected = MissionService.DetectNoOpCompletion(
                    mission,
                    runtime,
                    235,
                    113,
                    true,
                    false);
                AssertTrue(detected,
                    "An old branch diff must not hide a short captain run that made no changes since dock start.");
            }).ConfigureAwait(false);

            await RunTest("DetectNoOpCompletion_LongAgentOutput_ReturnsFalse", () =>
            {
                Mission mission = new Mission
                {
                    Id = "msn_test_summary",
                    Mode = MissionModeEnum.Implementation,
                    AgentOutput = "[ARMADA:RESULT] COMPLETE\n" + new String('s', 1500),
                };
                TimeSpan runtime = TimeSpan.FromSeconds(8);
                bool detected = MissionService.DetectNoOpCompletion(mission, runtime, 0, 1500, true);
                AssertFalse(detected,
                    "A claimed completion carrying a real summary of >= 200 chars passes. The summary alone is not the signal -- " +
                    "output length measures narration, not committed work -- so the claim has to be present with it.");
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

            await RunTest("DetectNoOpCompletion_NoMarkerShortOutput_Detects", () =>
            {
                Mission mission = new Mission
                {
                    Id = "msn_test_deepseek",
                    Mode = MissionModeEnum.Implementation,
                    AgentOutput = "AGENTS.md read and instructions received. Proceeding with extraction, parity report, and glossary fold exactly as described.",
                };
                TimeSpan runtime = TimeSpan.FromSeconds(7);
                bool detected = MissionService.DetectNoOpCompletion(mission, runtime, 0, 137, true);
                AssertTrue(detected,
                    "A captain that exits 0 with a brief acknowledgment and NO [ARMADA:RESULT] COMPLETE marker is the DeepSeek V4 Pro false-complete flavor; it must be detected too.");
            }).ConfigureAwait(false);

            await RunTest("DetectNoOpCompletion_StreamDiedMidRunAfterRealReading_Detects", () =>
            {
                Mission mission = new Mission
                {
                    Id = "msn_test_stream_death",
                    Mode = MissionModeEnum.Implementation,
                    AgentOutput = "All five source files exist in ground truth. Reading them plus the ported reference and landed seams:\n"
                        + "The reference shows the projection pattern. Now let me examine the other landed seams:\n"
                        + "Landed seams identified. Now let me find the port-coverage ledger:",
                };
                TimeSpan runtime = TimeSpan.FromSeconds(185);
                bool detected = MissionService.DetectNoOpCompletion(mission, runtime, 0, 474, true, false);
                AssertTrue(detected,
                    "This is the shape a dead provider stream leaves: minutes of genuine repository reading, a long narration cut off " +
                    "mid-sentence, no commit, and no completion claim. Runtime and output length both read as real work here, so the " +
                    "absent claim is the only thing that separates it from a finished mission.");
            }).ConfigureAwait(false);

            await RunTest("DetectNoOpCompletion_ReadOnlyStreamDiedMidRun_Detects", () =>
            {
                Mission mission = new Mission
                {
                    Id = "msn_test_stream_death_readonly",
                    Mode = MissionModeEnum.Research,
                    AgentOutput = "Memory loaded. Now let me locate the source siblings and the relevant files.",
                };
                TimeSpan runtime = TimeSpan.FromSeconds(98);
                bool detected = MissionService.DetectNoOpCompletion(mission, runtime, 0, 97, true, false);
                AssertTrue(detected,
                    "A read-only mission that ran 98 seconds and delivered 97 characters produced no report. The runtime check must not " +
                    "short-circuit the report-size check, or every read-only no-op that outlives it is accepted as Complete.");
            }).ConfigureAwait(false);

            await RunTest("HasCompletionMarker_DistinguishesClaimedFromUnclaimed", () =>
            {
                AssertTrue(MissionService.HasCompletionMarker("work done\n[ARMADA:RESULT] COMPLETE\nsummary"),
                    "Output carrying the worker marker claims completion.");
                AssertTrue(MissionService.HasCompletionMarker("review body\n[ARMADA:VERDICT] PASS"),
                    "A Judge delivers a verdict instead of a commit, so its verdict IS its completion claim. " +
                    "Omitting it here fails every Judge as a no-op, because a Judge never writes a diff.");
                AssertFalse(MissionService.HasCompletionMarker("Now let me find the port-coverage ledger:"),
                    "Output cut off mid-sentence claims nothing.");
                AssertFalse(MissionService.HasCompletionMarker(null),
                    "Absent output claims nothing and must not throw.");
            }).ConfigureAwait(false);

            await RunTest("DetectNoOpCompletion_JudgeVerdictWithoutDiff_ReturnsFalse", () =>
            {
                // A full review body, so the outcome turns on the verdict marker alone and not on
                // the short-output threshold that applies to a claimed completion.
                string review = "Completeness: every item in the brief is addressed. " + new String('r', 400)
                    + "\nCorrectness: the decode matches the source.\nTests: the added cases fail without the fix."
                    + "\nFailure Modes: none reachable from the catalogue.\n[ARMADA:VERDICT] PASS";
                Mission mission = new Mission
                {
                    Id = "msn_test_judge",
                    Persona = "Judge",
                    Mode = MissionModeEnum.Implementation,
                    AgentOutput = review,
                };
                TimeSpan runtime = TimeSpan.FromSeconds(20);
                bool detected = MissionService.DetectNoOpCompletion(mission, runtime, 0, review.Length, true, false);
                AssertFalse(detected,
                    "A Judge produces a verdict and no commit by design, so an empty diff is its normal outcome. " +
                    "Reading only the worker marker here fails every Judge in every pipeline.");
            }).ConfigureAwait(false);

            await RunTest("DetectNoOpCompletion_JudgeWithoutVerdict_Detects", () =>
            {
                // The same review body with the verdict line cut off, which is what a stream that
                // dies before the concluding turn leaves behind.
                string review = "Completeness: every item in the brief is addressed. " + new String('r', 400)
                    + "\nCorrectness: the decode matches the source.\nTests: the added cases fail without the fix.";
                Mission mission = new Mission
                {
                    Id = "msn_test_judge_cut",
                    Persona = "Judge",
                    Mode = MissionModeEnum.Implementation,
                    AgentOutput = review,
                };
                TimeSpan runtime = TimeSpan.FromSeconds(20);
                bool detected = MissionService.DetectNoOpCompletion(mission, runtime, 0, review.Length, true, false);
                AssertTrue(detected,
                    "The verdict line is the only difference from the passing case above, which is what makes it the signal. " +
                    "A Judge whose output ends before its verdict decided nothing, however much review text preceded it.");
            }).ConfigureAwait(false);

            await RunTest("BuildNoOpCompletionFailureReason_ContainsRuntimeAndOutputLength", () =>
            {
                Mission mission = new Mission
                {
                    Id = "msn_test_reason",
                    Mode = MissionModeEnum.Implementation,
                    AgentOutput = "[ARMADA:RESULT] COMPLETE\nbrief",
                };
                string reason = MissionService.BuildNoOpCompletionFailureReason(mission, TimeSpan.FromSeconds(7.5), 113);
                AssertTrue(reason.Contains("7.5"),
                    "Failure reason must name the runtime so a future operator can see how short the captain ran.");
                AssertTrue(reason.Contains("113"),
                    "Failure reason must name the AgentOutput length so a future operator can see the no-op signature.");
                AssertTrue(reason.Contains("no_op_completion_detected"),
                    "Failure reason must carry a stable token so the rescue path and operator grep can find it.");
            }).ConfigureAwait(false);

            await RunTest("DetectNoOpCompletion_AgentOutputSetOnMission_Detects", () =>
            {
                Mission mission = new Mission
                {
                    Id = "msn_test_captured",
                    Mode = MissionModeEnum.Implementation,
                };
                TimeSpan runtime = TimeSpan.FromSeconds(12);
                int diffLineCount = 0;
                int agentOutputLength = mission.AgentOutput?.Length ?? 0;
                bool hasAgentOutput = !String.IsNullOrEmpty(mission.AgentOutput);
                bool detected = MissionService.DetectNoOpCompletion(mission, runtime, diffLineCount, agentOutputLength, hasAgentOutput);
                AssertFalse(detected,
                    "When AgentOutput is not yet captured (null on the mission object), detection must NOT fire. " +
                    "This validates the ordering requirement: AgentOutput capture must happen before the no-op check. " +
                    "If this test fails, the HandleCompletionCoreAsync reordering is correct; if it passes, the reordering was needed.");
            }).ConfigureAwait(false);

            await RunTest("DetectNoOpCompletion_AgentOutputCaptured_Detects", () =>
            {
                Mission mission = new Mission
                {
                    Id = "msn_test_captured2",
                    Mode = MissionModeEnum.Implementation,
                    AgentOutput = "[ARMADA:RESULT] COMPLETE\nRead AGENTS.md. Mission loaded. Executing.",
                };
                TimeSpan runtime = TimeSpan.FromSeconds(12);
                int diffLineCount = 0;
                int agentOutputLength = mission.AgentOutput?.Length ?? 85;
                bool hasAgentOutput = !String.IsNullOrEmpty(mission.AgentOutput);
                bool detected = MissionService.DetectNoOpCompletion(mission, runtime, diffLineCount, agentOutputLength, hasAgentOutput);
                AssertTrue(detected,
                    "When AgentOutput IS populated on the mission object (as it is after the reordered capture block), " +
                    "a short runtime + empty diff + short AgentOutput must be detected as false-complete.");
            }).ConfigureAwait(false);
        }
    }
}
