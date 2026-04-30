namespace Armada.Test.Unit.Suites.Recovery
{
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Recovery;
    using Armada.Core.Settings;
    using Armada.Test.Common;

    /// <summary>
    /// Unit tests for the pure <see cref="RecoveryRouter"/>: covers every cell of the
    /// routing table plus cap-boundary conditions. The router has no side effects so
    /// each test instantiates a fresh router with a configured cap.
    /// </summary>
    public class RecoveryRouterTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Recovery Router";

        private static RecoveryRouter CreateRouter(int cap)
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.MaxRecoveryAttempts = cap;
            return new RecoveryRouter(settings);
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Route_StaleBaseUnderCap_ReturnsRedispatch", () =>
            {
                RecoveryRouter router = CreateRouter(2);
                RecoveryAction action = router.Route(MergeFailureClassEnum.StaleBase, conflictTrivial: false, recoveryAttempts: 0);
                AssertTrue(action is RecoveryAction.Redispatch, "stale-base under cap should redispatch");
                return Task.CompletedTask;
            });

            await RunTest("Route_StaleBaseAtCap_SurfacesRecoveryExhausted", () =>
            {
                RecoveryRouter router = CreateRouter(2);
                RecoveryAction action = router.Route(MergeFailureClassEnum.StaleBase, conflictTrivial: false, recoveryAttempts: 2);
                AssertTrue(action is RecoveryAction.Surface, "stale-base at cap should surface");
                RecoveryAction.Surface surface = (RecoveryAction.Surface)action;
                AssertEqual("recovery_exhausted", surface.Reason, "surface reason should be recovery_exhausted");
                return Task.CompletedTask;
            });

            await RunTest("Route_TextConflictTrivialUnderCap_ReturnsRedispatch", () =>
            {
                RecoveryRouter router = CreateRouter(2);
                RecoveryAction action = router.Route(MergeFailureClassEnum.TextConflict, conflictTrivial: true, recoveryAttempts: 0);
                AssertTrue(action is RecoveryAction.Redispatch, "trivial text conflict under cap should redispatch");
                return Task.CompletedTask;
            });

            await RunTest("Route_TextConflictNonTrivialUnderCap_ReturnsRebaseCaptain", () =>
            {
                RecoveryRouter router = CreateRouter(2);
                RecoveryAction action = router.Route(MergeFailureClassEnum.TextConflict, conflictTrivial: false, recoveryAttempts: 0);
                AssertTrue(action is RecoveryAction.RebaseCaptain, "non-trivial text conflict under cap should rebase-captain");
                return Task.CompletedTask;
            });

            await RunTest("Route_TextConflictTrivialAtCap_SurfacesRecoveryExhausted", () =>
            {
                RecoveryRouter router = CreateRouter(2);
                RecoveryAction action = router.Route(MergeFailureClassEnum.TextConflict, conflictTrivial: true, recoveryAttempts: 2);
                AssertTrue(action is RecoveryAction.Surface, "text conflict at cap should surface even when trivial");
                AssertEqual("recovery_exhausted", ((RecoveryAction.Surface)action).Reason, "surface reason");
                return Task.CompletedTask;
            });

            await RunTest("Route_TextConflictNonTrivialAtCap_SurfacesRecoveryExhausted", () =>
            {
                RecoveryRouter router = CreateRouter(2);
                RecoveryAction action = router.Route(MergeFailureClassEnum.TextConflict, conflictTrivial: false, recoveryAttempts: 2);
                AssertTrue(action is RecoveryAction.Surface, "text conflict at cap should surface");
                AssertEqual("recovery_exhausted", ((RecoveryAction.Surface)action).Reason, "surface reason");
                return Task.CompletedTask;
            });

            await RunTest("Route_TestFailureAfterMergeUnderCap_ReturnsRebaseCaptain", () =>
            {
                RecoveryRouter router = CreateRouter(2);
                RecoveryAction action = router.Route(MergeFailureClassEnum.TestFailureAfterMerge, conflictTrivial: false, recoveryAttempts: 1);
                AssertTrue(action is RecoveryAction.RebaseCaptain, "test failure after merge under cap should rebase-captain");
                return Task.CompletedTask;
            });

            await RunTest("Route_TestFailureAfterMergeAtCap_SurfacesRecoveryExhausted", () =>
            {
                RecoveryRouter router = CreateRouter(2);
                RecoveryAction action = router.Route(MergeFailureClassEnum.TestFailureAfterMerge, conflictTrivial: false, recoveryAttempts: 2);
                AssertTrue(action is RecoveryAction.Surface, "test failure after merge at cap should surface");
                AssertEqual("recovery_exhausted", ((RecoveryAction.Surface)action).Reason, "surface reason");
                return Task.CompletedTask;
            });

            await RunTest("Route_TestFailureBeforeMerge_AlwaysSurfaces", () =>
            {
                RecoveryRouter router = CreateRouter(2);
                RecoveryAction first = router.Route(MergeFailureClassEnum.TestFailureBeforeMerge, conflictTrivial: false, recoveryAttempts: 0);
                RecoveryAction second = router.Route(MergeFailureClassEnum.TestFailureBeforeMerge, conflictTrivial: true, recoveryAttempts: 5);
                AssertTrue(first is RecoveryAction.Surface, "first attempt should surface");
                AssertTrue(second is RecoveryAction.Surface, "above-cap attempt should still surface");
                AssertEqual("test_failure_before_merge", ((RecoveryAction.Surface)first).Reason, "surface reason on first");
                AssertEqual("test_failure_before_merge", ((RecoveryAction.Surface)second).Reason, "surface reason on second");
                return Task.CompletedTask;
            });

            await RunTest("Route_Unknown_AlwaysSurfacesClassifierUnknown", () =>
            {
                RecoveryRouter router = CreateRouter(2);
                RecoveryAction action = router.Route(MergeFailureClassEnum.Unknown, conflictTrivial: true, recoveryAttempts: 0);
                AssertTrue(action is RecoveryAction.Surface, "unknown should surface");
                AssertEqual("classifier_unknown", ((RecoveryAction.Surface)action).Reason, "surface reason");
                return Task.CompletedTask;
            });

            await RunTest("Route_StaleBaseAttemptsOverCap_StillSurfaces", () =>
            {
                RecoveryRouter router = CreateRouter(2);
                RecoveryAction action = router.Route(MergeFailureClassEnum.StaleBase, conflictTrivial: false, recoveryAttempts: 7);
                AssertTrue(action is RecoveryAction.Surface, "attempts past cap should surface");
                return Task.CompletedTask;
            });

            await RunTest("Route_CapZero_AlwaysSurfacesForRetryClasses", () =>
            {
                RecoveryRouter router = CreateRouter(0);
                RecoveryAction stale = router.Route(MergeFailureClassEnum.StaleBase, conflictTrivial: true, recoveryAttempts: 0);
                RecoveryAction text = router.Route(MergeFailureClassEnum.TextConflict, conflictTrivial: true, recoveryAttempts: 0);
                RecoveryAction testAfter = router.Route(MergeFailureClassEnum.TestFailureAfterMerge, conflictTrivial: false, recoveryAttempts: 0);
                AssertTrue(stale is RecoveryAction.Surface, "stale-base with cap=0 should surface immediately");
                AssertTrue(text is RecoveryAction.Surface, "text conflict with cap=0 should surface immediately");
                AssertTrue(testAfter is RecoveryAction.Surface, "test-after-merge with cap=0 should surface immediately");
                return Task.CompletedTask;
            });
        }
    }
}
