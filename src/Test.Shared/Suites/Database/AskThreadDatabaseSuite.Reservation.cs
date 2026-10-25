namespace Test.Shared.Suites.Database
{
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using static Test.Shared.Infrastructure.Asserts;

    public sealed partial class AskThreadDatabaseSuite
    {
        private static async Task ConcurrentDuplicateReservationAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                DatabaseDriver database = testDb.Driver;
                AskThread thread = NewThread();
                await database.AskThreads.CreateAsync(thread).ConfigureAwait(false);
                Task<AskTurnReservation> firstTask = database.AskThreads.TryReserveTurnAsync(thread.TenantId, thread.UserId, NewTurn(thread.Id, "same-request", "Same question"));
                Task<AskTurnReservation> secondTask = database.AskThreads.TryReserveTurnAsync(thread.TenantId, thread.UserId, NewTurn(thread.Id, "same-request", "Same question"));
                AskTurnReservation[] results = await Task.WhenAll(firstTask, secondTask).ConfigureAwait(false);
                AssertEqual(1, results.Count(result => result.Result == AskTurnReservationResultEnum.Created));
                AssertEqual(1, results.Count(result => result.Result == AskTurnReservationResultEnum.Existing));
                AssertEqual(results[0].Turn!.Sequence, results[1].Turn!.Sequence);
                AssertEqual(results[0].Turn.RequestId, results[1].Turn.RequestId);
            }
        }

        private static async Task RequestReplayAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                DatabaseDriver database = testDb.Driver;
                AskThread thread = NewThread();
                await database.AskThreads.CreateAsync(thread).ConfigureAwait(false);
                AskTurn original = NewTurn(thread.Id, "Turn-Case", "Question");
                AskTurnReservation created = await database.AskThreads.TryReserveTurnAsync(thread.TenantId, thread.UserId, original).ConfigureAwait(false);
                AssertEqual(AskTurnReservationResultEnum.Created, created.Result);
                AssertEqual(1L, created.Turn!.Sequence);
                AssertTrue(await database.AskThreads.MarkRunningAsync(thread.TenantId, thread.UserId, thread.Id, original.RequestId).ConfigureAwait(false));

                AskTurnReservation replay = await database.AskThreads.TryReserveTurnAsync(thread.TenantId, thread.UserId, NewTurn(thread.Id, "Turn-Case", "Question")).ConfigureAwait(false);
                AssertEqual(AskTurnReservationResultEnum.Existing, replay.Result);
                AssertEqual(AskTurnStateEnum.Running, replay.Turn!.State);
                AskTurnReservation mismatch = await database.AskThreads.TryReserveTurnAsync(thread.TenantId, thread.UserId, NewTurn(thread.Id, "Turn-Case", "Changed")).ConfigureAwait(false);
                AssertEqual(AskTurnReservationResultEnum.RequestConflict, mismatch.Result);

                CaptainChatResponse response = new CaptainChatResponse { Success = true, Reply = "Answer" };
                AssertTrue(await database.AskThreads.CompleteTurnAsync(thread.TenantId, thread.UserId, thread.Id, original.RequestId, response).ConfigureAwait(false));
                AskTurnReservation caseDistinct = await database.AskThreads.TryReserveTurnAsync(thread.TenantId, thread.UserId, NewTurn(thread.Id, "turn-case", "Different request")).ConfigureAwait(false);
                AssertEqual(AskTurnReservationResultEnum.Created, caseDistinct.Result);
                AssertEqual(2L, caseDistinct.Turn!.Sequence);

                AskTurnReservation captainMismatch = await database.AskThreads.TryReserveTurnAsync(thread.TenantId, thread.UserId, NewTurn(thread.Id, "turn-case", "Different request", "cpt_other")).ConfigureAwait(false);
                AssertEqual(AskTurnReservationResultEnum.RequestConflict, captainMismatch.Result);
            }
        }

        private static async Task ConcurrentReservationAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                DatabaseDriver database = testDb.Driver;
                AskThread thread = NewThread();
                await database.AskThreads.CreateAsync(thread).ConfigureAwait(false);
                Task<AskTurnReservation> firstTask = database.AskThreads.TryReserveTurnAsync(thread.TenantId, thread.UserId, NewTurn(thread.Id, "turn-a"));
                Task<AskTurnReservation> secondTask = database.AskThreads.TryReserveTurnAsync(thread.TenantId, thread.UserId, NewTurn(thread.Id, "turn-b"));
                AskTurnReservation[] results = await Task.WhenAll(firstTask, secondTask).ConfigureAwait(false);
                AssertEqual(1, results.Count(result => result.Result == AskTurnReservationResultEnum.Created));
                AssertEqual(1, results.Count(result => result.Result == AskTurnReservationResultEnum.ThreadBusy));
                AskThread stored = (await database.AskThreads.ReadAsync(thread.TenantId, thread.UserId, thread.Id).ConfigureAwait(false))!;
                AssertNotNull(stored.ActiveTurnId);
            }
        }
    }
}
