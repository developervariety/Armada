namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Server;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>Saved chat behavior at the real captain runtime boundary.</summary>
    public sealed partial class AskThreadServiceSuite : IArmadaTestSuite
    {
        /// <summary>Register the conversation persistence and execution cases.</summary>
        public TestSuiteDescriptor Build()
        {
            return new TestSuiteDescriptor("Services.AskThreadService", "Saved Ask conversations", new List<TestCaseDescriptor>
            {
                Case("replay_and_service_recreation_use_saved_results_and_history", ReplayAndHistoryAsync),
                Case("overlap_and_another_service_cannot_launch_again", OverlapAsync),
                Case("new_turn_after_completed_fence_is_not_stranded_by_runtime_cleanup", CompletionFenceWindowAsync),
                Case("human_owner_scope_is_required_before_runtime", OwnerAsync),
                Case("failed_response_is_saved_and_replayed", FailureAsync),
                Case("orphan_pending_turn_remains_fenced_after_recreation", RecoveryAsync),
                Case("tool_calls_are_saved_with_the_turn_and_survive_a_failed_turn", ToolCallsSavedAsync),
                Case("running_turn_reports_the_tool_calls_made_so_far", ToolCallsLiveAsync),
                Case("finished_reply_is_unread_until_the_owner_reads_it", UnreadAsync),
                Case("mcp_tools_only_read_the_callers_own_conversations", McpReadToolsAsync),
                Case("delete_removes_archived_idle_threads_and_reports_each_refusal", DeleteAsync)
            });
        }

        private static TestCaseDescriptor Case(string id, Func<Task> body)
        {
            return new TestCaseDescriptor("Services.AskThreadService", id, id, _ => body(), new List<string> { TestTags.Positive });
        }

        private static AuthContext Owner() => AuthContext.Authenticated("default", "default", true, true, "Session");

        private static AskThreadService Service(TestDatabase db, AskThreadRuntimeFactory factory, LoggingModule logging)
        {
            return new AskThreadService(db.Driver, new CaptainChatService(db.Driver, factory, null, null, logging));
        }

        private static async Task<AskThread> ThreadAsync(TestDatabase db, AskThreadService service)
        {
            Captain captain = await db.Driver.Captains.CreateAsync(new Captain("Saved chat fixture", AgentRuntimeEnum.Codex)).ConfigureAwait(false);
            return await service.CreateThreadAsync(Owner(), new AskThreadCreateRequest { Title = "Saved", CaptainId = captain.Id }).ConfigureAwait(false);
        }

        private static async Task ReplayAndHistoryAsync()
        {
            using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                AskThreadRuntimeFactory factory = new AskThreadRuntimeFactory(logging);
                AskThreadService service = Service(db, factory, logging);
                AskThread thread = await ThreadAsync(db, service).ConfigureAwait(false);
                AskTurnSendRequest request = new AskTurnSendRequest { RequestId = "first", Content = "First question" };
                AskTurnStart sent = await service.SendTurnAsync(Owner(), thread.Id, request).ConfigureAwait(false);
                AssertEqual(200, sent.StatusCode);
                AssertEqual("Saved assistant reply", sent.Turn!.Response!.Reply);
                Captain replacement = await db.Driver.Captains.CreateAsync(new Captain("Replacement chat fixture", AgentRuntimeEnum.Codex)).ConfigureAwait(false);
                await service.UpdateThreadAsync(Owner(), thread.Id, new AskThreadUpdateRequest { SetTitle = true, Title = "Renamed", SetCaptainId = true, CaptainId = replacement.Id }).ConfigureAwait(false);
                AskThreadService restarted = Service(db, factory, logging);
                AskTurnStart replay = await restarted.SendTurnAsync(Owner(), thread.Id, request).ConfigureAwait(false);
                AssertEqual(200, replay.StatusCode);
                AssertEqual(1, factory.Starts.Count);
                AssertEqual(thread.CaptainId, replay.Turn!.CaptainId);
                AssertEqual(sent.Turn.Response.Metrics.TotalMs, replay.Turn!.Response!.Metrics.TotalMs);
                AskTurnStart conflict = await restarted.SendTurnAsync(Owner(), thread.Id, new AskTurnSendRequest { RequestId = "first", Content = "Different" }).ConfigureAwait(false);
                AssertEqual(409, conflict.StatusCode);
                await restarted.SendTurnAsync(Owner(), thread.Id, new AskTurnSendRequest { RequestId = "second", Content = "Second question" }).ConfigureAwait(false);
                AssertEqual(2, factory.Starts.Count);
                AssertContains("First question", factory.Starts[1].Prompt);
                AssertContains("Saved assistant reply", factory.Starts[1].Prompt);
            }
        }

        private static async Task FailureAsync()
        {
            using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                AskThreadRuntimeFactory factory = new AskThreadRuntimeFactory(logging) { Reply = "[ARMADA:ACTIVITY] codex error fixture unavailable" };
                AskThreadService service = Service(db, factory, logging);
                AskThread thread = await ThreadAsync(db, service).ConfigureAwait(false);
                AskTurnSendRequest request = new AskTurnSendRequest { RequestId = "failure", Content = "Question" };
                AskTurnStart first = await service.SendTurnAsync(Owner(), thread.Id, request).ConfigureAwait(false);
                AskTurnStart replay = await Service(db, factory, logging).SendTurnAsync(Owner(), thread.Id, request).ConfigureAwait(false);
                AssertEqual(AskTurnStateEnum.Failed, replay.Turn!.State);
                AssertFalse(replay.Turn.Response!.Success);
                AssertEqual(first.Turn!.Response!.Error, replay.Turn.Response.Error);
                AssertEqual(1, factory.Starts.Count);
            }
        }
    }
}
