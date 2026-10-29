namespace Test.Shared.Suites.Database
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using static Test.Shared.Infrastructure.Asserts;

    public sealed partial class AskThreadDatabaseSuite
    {
        private static async Task HistoryPagingAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                DatabaseDriver database = testDb.Driver;
                AskThread thread = NewThread();
                await database.AskThreads.CreateAsync(thread).ConfigureAwait(false);
                for (int index = 1; index <= 4; index++)
                {
                    string requestId = "history-" + index;
                    AskTurnReservation reservation = await database.AskThreads.TryReserveTurnAsync(thread.TenantId, thread.UserId, NewTurn(thread.Id, requestId, "Question " + index)).ConfigureAwait(false);
                    AssertEqual(AskTurnReservationResultEnum.Created, reservation.Result);
                    AssertTrue(await database.AskThreads.MarkRunningAsync(thread.TenantId, thread.UserId, thread.Id, requestId).ConfigureAwait(false));
                    CaptainChatResponse response = index == 4
                        ? new CaptainChatResponse { Success = false, Error = "failed" }
                        : new CaptainChatResponse { Success = true, Reply = "Answer " + index, Model = "test-model", Thinking = "trace", ToolCalls = new List<ChatToolCall> { new ChatToolCall { Id = "call-" + index, Name = "read", Status = "success", Arguments = "{\"path\":\"a\"}", Result = "ok", ElapsedMs = 12 } }, Metrics = new CaptainChatMetrics { PromptTokens = 20 + index, TotalMs = 5.5 } };
                    AssertTrue(await database.AskThreads.CompleteTurnAsync(thread.TenantId, thread.UserId, thread.Id, requestId, response).ConfigureAwait(false));
                }

                List<AskTurn> page = await database.AskThreads.EnumerateTurnsAsync(thread.TenantId, thread.UserId, thread.Id, null, 2).ConfigureAwait(false);
                AssertEqual(2, page.Count);
                AssertEqual(3L, page[0].Sequence);
                AssertEqual(4L, page[1].Sequence);
                AssertEqual(AskTurnStateEnum.Failed, page[1].State);
                AssertEqual("failed", page[1].Response!.Error);
                List<AskTurn> older = await database.AskThreads.EnumerateTurnsAsync(thread.TenantId, thread.UserId, thread.Id, page[0].Sequence, 2).ConfigureAwait(false);
                AssertEqual(2, older.Count);
                AssertEqual(1L, older[0].Sequence);
                AssertEqual(2L, older[1].Sequence);

                List<AskTurn> history = await database.AskThreads.ReadHistoryAsync(thread.TenantId, thread.UserId, thread.Id, 2).ConfigureAwait(false);
                AssertEqual(2, history.Count);
                AssertEqual(2L, history[0].Sequence);
                AssertEqual(3L, history[1].Sequence);
                AssertEqual("Answer 3", history[1].Response!.Reply);
                AssertEqual("test-model", history[1].Response.Model);
                AssertEqual("trace", history[1].Response.Thinking);
                AssertEqual(1, history[1].Response.ToolCalls.Count);
                AssertEqual("call-3", history[1].Response.ToolCalls[0].Id);
                AssertEqual("success", history[1].Response.ToolCalls[0].Status);
                AssertEqual("{\"path\":\"a\"}", history[1].Response.ToolCalls[0].Arguments);
                AssertEqual(12.0, history[1].Response.ToolCalls[0].ElapsedMs!.Value);
                AssertEqual(23, history[1].Response.Metrics.PromptTokens);
                AssertEqual(5.5, history[1].Response.Metrics.TotalMs);
            }
        }

        private static async Task StaleCompletionAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                DatabaseDriver database = testDb.Driver;
                AskThread thread = NewThread();
                await database.AskThreads.CreateAsync(thread).ConfigureAwait(false);
                AskTurnReservation first = await database.AskThreads.TryReserveTurnAsync(thread.TenantId, thread.UserId, NewTurn(thread.Id, "turn-first")).ConfigureAwait(false);
                AssertTrue(await database.AskThreads.CompleteTurnAsync(thread.TenantId, thread.UserId, thread.Id, "turn-first", new CaptainChatResponse { Success = true }).ConfigureAwait(false));
                AskTurnReservation second = await database.AskThreads.TryReserveTurnAsync(thread.TenantId, thread.UserId, NewTurn(thread.Id, "turn-second")).ConfigureAwait(false);
                AssertEqual(AskTurnReservationResultEnum.Created, second.Result);
                AssertFalse(await database.AskThreads.CompleteTurnAsync(thread.TenantId, thread.UserId, thread.Id, "turn-first", new CaptainChatResponse { Success = true, Reply = "stale" }).ConfigureAwait(false));
                AskThread current = (await database.AskThreads.ReadAsync(thread.TenantId, thread.UserId, thread.Id).ConfigureAwait(false))!;
                AssertEqual("turn-second", current.ActiveTurnId);
                AskTurnReservation third = await database.AskThreads.TryReserveTurnAsync(thread.TenantId, thread.UserId, NewTurn(thread.Id, "turn-third")).ConfigureAwait(false);
                AssertEqual(AskTurnReservationResultEnum.ThreadBusy, third.Result);
                AssertEqual("turn-second", third.Turn!.RequestId);
            }
        }

        private static async Task UnreadLifecycleAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                DatabaseDriver database = testDb.Driver;
                AskThread thread = NewThread();
                await database.AskThreads.CreateAsync(thread).ConfigureAwait(false);
                AssertEqual(0, (await database.AskThreads.ReadAsync(thread.TenantId, thread.UserId, thread.Id).ConfigureAwait(false))!.UnreadCount);

                await database.AskThreads.TryReserveTurnAsync(thread.TenantId, thread.UserId, NewTurn(thread.Id, "unread-first")).ConfigureAwait(false);
                AssertTrue(await database.AskThreads.CompleteTurnAsync(thread.TenantId, thread.UserId, thread.Id, "unread-first", new CaptainChatResponse { Success = true, Reply = "one" }).ConfigureAwait(false));
                AssertEqual(1, (await database.AskThreads.ReadAsync(thread.TenantId, thread.UserId, thread.Id).ConfigureAwait(false))!.UnreadCount);

                // A completion the request fence rejects is not a new reply.
                AssertFalse(await database.AskThreads.CompleteTurnAsync(thread.TenantId, thread.UserId, thread.Id, "unread-first", new CaptainChatResponse { Success = true, Reply = "replay" }).ConfigureAwait(false));
                AssertEqual(1, (await database.AskThreads.ReadAsync(thread.TenantId, thread.UserId, thread.Id).ConfigureAwait(false))!.UnreadCount);

                await database.AskThreads.TryReserveTurnAsync(thread.TenantId, thread.UserId, NewTurn(thread.Id, "unread-second")).ConfigureAwait(false);
                AssertTrue(await database.AskThreads.CompleteTurnAsync(thread.TenantId, thread.UserId, thread.Id, "unread-second", new CaptainChatResponse { Success = false, Error = "failed" }).ConfigureAwait(false));
                AssertEqual(2, (await database.AskThreads.ReadAsync(thread.TenantId, thread.UserId, thread.Id).ConfigureAwait(false))!.UnreadCount);

                AssertFalse(await database.AskThreads.MarkReadAsync(thread.TenantId, "usr_other", thread.Id).ConfigureAwait(false));
                AssertEqual(2, (await database.AskThreads.ReadAsync(thread.TenantId, thread.UserId, thread.Id).ConfigureAwait(false))!.UnreadCount);
                AssertTrue(await database.AskThreads.MarkReadAsync(thread.TenantId, thread.UserId, thread.Id).ConfigureAwait(false));
                AssertEqual(0, (await database.AskThreads.ReadAsync(thread.TenantId, thread.UserId, thread.Id).ConfigureAwait(false))!.UnreadCount);
                AssertTrue(await database.AskThreads.MarkUnreadAsync(thread.TenantId, thread.UserId, thread.Id).ConfigureAwait(false));
                AssertEqual(1, (await database.AskThreads.ReadAsync(thread.TenantId, thread.UserId, thread.Id).ConfigureAwait(false))!.UnreadCount);
                AssertFalse(await database.AskThreads.MarkUnreadAsync(thread.TenantId, thread.UserId, "ask_missing").ConfigureAwait(false));

                // Metadata edits leave the unread count alone.
                thread.Title = "Renamed";
                await database.AskThreads.UpdateAsync(thread).ConfigureAwait(false);
                AssertEqual(1, (await database.AskThreads.ReadAsync(thread.TenantId, thread.UserId, thread.Id).ConfigureAwait(false))!.UnreadCount);
            }
        }
    }
}
