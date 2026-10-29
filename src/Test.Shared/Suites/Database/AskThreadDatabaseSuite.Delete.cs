namespace Test.Shared.Suites.Database
{
    using System;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using static Test.Shared.Infrastructure.Asserts;

    public sealed partial class AskThreadDatabaseSuite
    {
        private static async Task DeleteRulesAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                DatabaseDriver database = testDb.Driver;
                AskThread thread = NewThread();
                await database.AskThreads.CreateAsync(thread).ConfigureAwait(false);
                AssertEqual(AskThreadDeleteOutcomeEnum.NotFound, await database.AskThreads.TryDeleteAsync(thread.TenantId, "usr_other", thread.Id).ConfigureAwait(false));
                AssertEqual(AskThreadDeleteOutcomeEnum.NotArchived, await database.AskThreads.TryDeleteAsync(thread.TenantId, thread.UserId, thread.Id).ConfigureAwait(false));

                AskTurn turn = NewTurn(thread.Id, "turn-delete");
                await database.AskThreads.TryReserveTurnAsync(thread.TenantId, thread.UserId, turn).ConfigureAwait(false);
                await database.AskThreads.MarkRunningAsync(thread.TenantId, thread.UserId, thread.Id, turn.RequestId).ConfigureAwait(false);
                AskActionProposal proposal = new AskActionProposal
                {
                    TenantId = thread.TenantId, UserId = thread.UserId, ThreadId = thread.Id, RequestId = turn.RequestId,
                    ToolName = "armada_dispatch", ArgumentsJson = "{\"prompt\":\"x\"}", Fingerprint = new string('a', 64), ExpiresUtc = DateTime.UtcNow.AddHours(1)
                };
                AskActionProposal saved = (await database.AskActionProposals.TryCreateAsync(proposal).ConfigureAwait(false))!;
                thread.Archived = true;
                await database.AskThreads.UpdateAsync(thread).ConfigureAwait(false);
                AssertEqual(AskThreadDeleteOutcomeEnum.OpenTurn, await database.AskThreads.TryDeleteAsync(thread.TenantId, thread.UserId, thread.Id).ConfigureAwait(false));

                await database.AskThreads.CompleteTurnAsync(thread.TenantId, thread.UserId, thread.Id, turn.RequestId, new CaptainChatResponse { Success = true, Reply = "Answer" }).ConfigureAwait(false);
                AssertEqual(AskThreadDeleteOutcomeEnum.OpenProposal, await database.AskThreads.TryDeleteAsync(thread.TenantId, thread.UserId, thread.Id).ConfigureAwait(false));

                AssertTrue(await database.AskActionProposals.TryRejectAsync(thread.TenantId, thread.UserId, thread.Id, saved.Id, proposal.Fingerprint, DateTime.UtcNow).ConfigureAwait(false));
                AssertEqual(AskThreadDeleteOutcomeEnum.Deleted, await database.AskThreads.TryDeleteAsync(thread.TenantId, thread.UserId, thread.Id).ConfigureAwait(false));
                AssertNull(await database.AskThreads.ReadAsync(thread.TenantId, thread.UserId, thread.Id).ConfigureAwait(false));
                AssertNull(await database.AskThreads.ReadTurnAsync(thread.TenantId, thread.UserId, thread.Id, turn.RequestId).ConfigureAwait(false));
                AssertEqual(0, (await database.AskActionProposals.EnumerateAsync(thread.TenantId, thread.UserId, thread.Id).ConfigureAwait(false)).Count);
                AssertEqual(AskThreadDeleteOutcomeEnum.NotFound, await database.AskThreads.TryDeleteAsync(thread.TenantId, thread.UserId, thread.Id).ConfigureAwait(false));
            }
        }

        private static async Task DeleteIgnoresExpiredPendingProposalAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                DatabaseDriver database = testDb.Driver;
                AskThread thread = NewThread();
                await database.AskThreads.CreateAsync(thread).ConfigureAwait(false);
                AskTurn turn = NewTurn(thread.Id, "turn-expired");
                await database.AskThreads.TryReserveTurnAsync(thread.TenantId, thread.UserId, turn).ConfigureAwait(false);
                await database.AskThreads.MarkRunningAsync(thread.TenantId, thread.UserId, thread.Id, turn.RequestId).ConfigureAwait(false);
                AskActionProposal proposal = new AskActionProposal
                {
                    TenantId = thread.TenantId, UserId = thread.UserId, ThreadId = thread.Id, RequestId = turn.RequestId,
                    ToolName = "armada_dispatch", ArgumentsJson = "{\"prompt\":\"x\"}", Fingerprint = new string('b', 64), ExpiresUtc = DateTime.UtcNow.AddMinutes(-1)
                };
                await database.AskActionProposals.TryCreateAsync(proposal).ConfigureAwait(false);
                await database.AskThreads.CompleteTurnAsync(thread.TenantId, thread.UserId, thread.Id, turn.RequestId, new CaptainChatResponse { Success = true, Reply = "Answer" }).ConfigureAwait(false);
                thread.Archived = true;
                await database.AskThreads.UpdateAsync(thread).ConfigureAwait(false);
                AssertEqual(AskThreadDeleteOutcomeEnum.Deleted, await database.AskThreads.TryDeleteAsync(thread.TenantId, thread.UserId, thread.Id).ConfigureAwait(false));
            }
        }
    }
}
