namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;

    /// <summary>Behavioral contracts for saved Fleet Action definitions and run admission.</summary>
    public sealed partial class FleetActionServiceSuite : IArmadaTestSuite
    {
        private const string SuiteId = "Services.FleetActionService";

        /// <inheritdoc />
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>
            {
                CaseAsync("run_snapshots_owned_inputs", "Run admission stores an exact owner snapshot", RunSnapshotsOwnedInputsAsync),
                CaseAsync("request_replay_precedes_mutable_lookup", "Request replay is stable and changed input conflicts", RequestReplayPrecedesMutableLookupAsync),
                CaseAsync("operator_revocation_and_owner_scope", "Revoked operators and other owners cannot use saved definitions", OperatorRevocationAndOwnerScopeAsync),
                CaseAsync("pending_run_cancel_is_durable", "Cancellation marks every pending target before the worker runs", PendingRunCancelIsDurableAsync),
                CaseAsync("voyage_outcomes_use_current_landing_facts", "Voyage outcomes separate source work from a current-attempt landing", VoyageOutcomesUseCurrentLandingFactsAsync),
                CaseAsync("recovery_fences_dispatch_and_blocks_pending", "Recovery fences an interrupted target and does not start another target", RecoveryFencesDispatchAndBlocksPendingAsync),
                CaseAsync("runner_claim_is_bounded_and_unknown_is_fenced", "Concurrent runners respect one saved slot and never repeat an unknown dispatch", RunnerClaimIsBoundedAndUnknownIsFencedAsync),
                CaseAsync("pipeline_hash_ignores_metadata_and_rejects_stage_drift", "Pipeline timestamps do not change the snapshot hash and changed stages block dispatch", PipelineHashIgnoresMetadataAndRejectsStageDriftAsync)
            };
            return new TestSuiteDescriptor(SuiteId, "Fleet Action service", cases);
        }

        private static TestCaseDescriptor CaseAsync(string id, string name, Func<Task> body)
        {
            return new TestCaseDescriptor(SuiteId, id, name, _ => body(), new List<string> { TestTags.Positive });
        }
    }
}
