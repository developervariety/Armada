namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Server;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>Behavioral contracts for saved Ask action proposals and approval.</summary>
    public sealed partial class AskActionServiceSuite : IArmadaTestSuite
    {
        private const string SuiteId = "Services.AskActionService";

        /// <inheritdoc />
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>
            {
                CaseAsync("proposal_gate_and_ordinary_calls", "Saved writes become proposals while ordinary and read calls still run", ProposalGateAndOrdinaryCallsAsync),
                CaseAsync("fingerprint_approval_is_once", "Fingerprint approval is owner-bound and invokes a handler once across concurrent and recreated services", FingerprintApprovalIsOnceAsync),
                CaseAsync("secret_arguments_are_not_saved_or_echoed", "Secret-shaped arguments leave no proposal and do not reach the handler", SecretArgumentsAreNotSavedOrEchoedAsync),
                CaseAsync("throwing_handler_stays_uncertain", "A handler that throws after a side effect stays executing and never runs again", ThrowingHandlerStaysUncertainAsync),
                CaseAsync("accepted_job_links_terminal_work", "An accepted job is retained and its terminal mission and voyage links are exact", AcceptedJobLinksTerminalWorkAsync),
                CaseAsync("missing_and_lost_jobs_stay_uncertain", "A missing or lost accepted job remains uncertain", MissingAndLostJobsStayUncertainAsync),
                CaseAsync("registrar_normalizes_before_proposal_gate", "The registered MCP pipeline saves the resolved vessel id before it gates the mutation", RegistrarNormalizesBeforeProposalGateAsync),
                CaseAsync("inbox_lists_only_the_callers_own_pending_proposals", "The inbox lists the caller's own pending proposals with a conversation link and no other user's, tenant's, mission's or saved-chat caller's", InboxListsOnlyTheCallersOwnPendingProposalsAsync),
                CaseAsync("inbox_omits_resolved_and_expired_proposals", "Rejected, approved and expired proposals leave the inbox", InboxOmitsResolvedAndExpiredProposalsAsync),
                CaseAsync("inbox_item_cannot_decide_the_proposal", "An inbox item carries no approval key and listing it never decides or runs the proposal", InboxItemCannotDecideTheProposalAsync),
                CaseAsync("proposal_summary_names_the_owners_vessel", "A proposal summary names the owner's vessel, falls back to the id for an unknown or foreign vessel, and keeps the saved arguments", ProposalSummaryNamesTheOwnersVesselAsync)
            };
            return new TestSuiteDescriptor(suiteId: SuiteId, displayName: "Ask action proposals", cases: cases);
        }

        private static TestCaseDescriptor CaseAsync(string id, string name, Func<Task> body)
        {
            return new TestCaseDescriptor(SuiteId, id, name, _ => body(), new List<string> { TestTags.Positive });
        }
    }
}
