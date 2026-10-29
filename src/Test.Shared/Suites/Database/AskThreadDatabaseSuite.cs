namespace Test.Shared.Suites.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;

    /// <summary>Provider-neutral ownership, idempotency, active-turn, and saved-history contracts for Ask storage.</summary>
    public sealed partial class AskThreadDatabaseSuite : IArmadaTestSuite
    {
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>
            {
                CaseAsync("thread_reads_and_updates_are_owner_scoped", "Thread reads and updates use the exact owner", TestTags.Positive, OwnerScopeAsync),
                CaseAsync("request_id_replays_and_payload_conflicts", "A request id replays once and rejects changed payload", TestTags.Positive, RequestReplayAsync),
                CaseAsync("concurrent_same_request_id_reserves_once", "Concurrent retries share one durable turn", TestTags.Positive, ConcurrentDuplicateReservationAsync),
                CaseAsync("one_thread_allows_only_one_concurrent_active_turn", "Concurrent reservations hold one active turn", TestTags.Positive, ConcurrentReservationAsync),
                CaseAsync("completed_turns_round_trip_and_page_as_history", "Completed turns preserve responses and page as history", TestTags.Positive, HistoryPagingAsync),
                CaseAsync("stale_completion_cannot_release_a_later_turn", "A stale completion cannot release another active turn", TestTags.Negative, StaleCompletionAsync),
                CaseAsync("finished_replies_raise_the_unread_count_until_the_owner_reads", "Finished replies raise the unread count until the owner reads", TestTags.Positive, UnreadLifecycleAsync),
                CaseAsync("delete_removes_only_archived_idle_threads_without_open_proposals", "Delete removes only archived idle threads without an open proposal", TestTags.Negative, DeleteRulesAsync),
                CaseAsync("delete_is_not_blocked_by_an_expired_pending_proposal", "An expired pending proposal does not block a delete", TestTags.Positive, DeleteIgnoresExpiredPendingProposalAsync)
            };
            return new TestSuiteDescriptor(suiteId: "Database.AskThread", displayName: "Ask Thread Database", cases: cases);
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(suiteId: "Database.AskThread", caseId: caseId, displayName: displayName,
                executeAsync: (CancellationToken token) => body(), tags: new List<string> { tag });
        }

        private static AskThread NewThread(string tenantId = "ten_ask", string userId = "usr_owner")
        {
            return new AskThread { TenantId = tenantId, UserId = userId, Title = "Saved thread" };
        }

        private static AskTurn NewTurn(string threadId, string requestId, string content = "Question", string captainId = "cpt_ask")
        {
            return new AskTurn { ThreadId = threadId, RequestId = requestId, CaptainId = captainId, Content = content };
        }
    }
}
