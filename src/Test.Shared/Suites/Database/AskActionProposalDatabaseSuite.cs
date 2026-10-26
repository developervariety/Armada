namespace Test.Shared.Suites.Database
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>Provider-neutral contracts for owner-scoped, idempotent Ask action proposals.</summary>
    public sealed class AskActionProposalDatabaseSuite : IArmadaTestSuite
    {
        /// <inheritdoc />
        public TestSuiteDescriptor Build() => new TestSuiteDescriptor(suiteId: "Database.AskActionProposal", displayName: "Ask Action Proposal Database", cases: new List<TestCaseDescriptor>
        {
            Case("owner_scope_and_payload_fingerprint_dedup", OwnerAndDedupAsync),
            Case("concurrent_duplicate_creation_returns_one_row", ConcurrentCreateAsync),
            Case("claim_is_compare_and_set_and_terminal", ClaimAndTerminalAsync),
            Case("expired_and_wrong_hash_claims_are_denied", ExpiredAndHashAsync),
            Case("only_active_running_turn_can_create_and_work_refs_round_trip", EligibilityAndWorkAsync)
        });

        private static TestCaseDescriptor Case(string id, Func<Task> body) => new TestCaseDescriptor("Database.AskActionProposal", id, id.Replace('_', ' '), _ => body(), new List<string> { TestTags.Positive });

        private sealed class Fixture : IDisposable
        {
            public TestDatabase TestDatabase { get; }
            public DatabaseDriver Driver => TestDatabase.Driver;
            public AskThread Thread { get; }
            public AskActionProposal Proposal { get; }
            public Fixture(TestDatabase testDatabase, AskThread thread, AskActionProposal proposal) { TestDatabase = testDatabase; Thread = thread; Proposal = proposal; }
            public void Dispose() => TestDatabase.Dispose();
        }

        private static async Task<Fixture> SetupAsync()
        {
            TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
            AskThread thread = new AskThread { TenantId = "ten_action", UserId = "usr_owner", Title = "Action" };
            await db.Driver.AskThreads.CreateAsync(thread).ConfigureAwait(false);
            AskTurn turn = new AskTurn { ThreadId = thread.Id, RequestId = "req-action", CaptainId = "cpt_test", Content = "hello" };
            await db.Driver.AskThreads.TryReserveTurnAsync(thread.TenantId, thread.UserId, turn).ConfigureAwait(false);
            await db.Driver.AskThreads.MarkRunningAsync(thread.TenantId, thread.UserId, thread.Id, turn.RequestId).ConfigureAwait(false);
            AskActionProposal proposal = new AskActionProposal { TenantId = thread.TenantId, UserId = thread.UserId, ThreadId = thread.Id, RequestId = turn.RequestId, ToolName = "create_mission", ArgumentsJson = "{\"title\":\"x\"}", Fingerprint = new string('a', 64), ExpiresUtc = DateTime.UtcNow.AddHours(1) };
            return new Fixture(db, thread, proposal);
        }

        private static async Task OwnerAndDedupAsync()
        {
            Fixture fixture = await SetupAsync().ConfigureAwait(false);
            using (fixture)
            {
            DatabaseDriver db = fixture.Driver; AskThread thread = fixture.Thread; AskActionProposal p = fixture.Proposal;
            AskActionProposal first = (await db.AskActionProposals.TryCreateAsync(p).ConfigureAwait(false))!;
            AskActionProposal duplicate = (await db.AskActionProposals.TryCreateAsync(new AskActionProposal { Id = "aap_other", TenantId = p.TenantId, UserId = p.UserId, ThreadId = p.ThreadId, RequestId = p.RequestId, ToolName = p.ToolName, ArgumentsJson = p.ArgumentsJson, Fingerprint = p.Fingerprint, ExpiresUtc = p.ExpiresUtc }).ConfigureAwait(false))!;
            AssertEqual(first.Id, duplicate.Id);
            AskActionProposal alteredPayload = new AskActionProposal { TenantId = p.TenantId, UserId = p.UserId, ThreadId = p.ThreadId, RequestId = p.RequestId, ToolName = p.ToolName, ArgumentsJson = "{\"title\":\"changed\"}", Fingerprint = new string('d', 64), ExpiresUtc = p.ExpiresUtc };
            AskActionProposal distinct = (await db.AskActionProposals.TryCreateAsync(alteredPayload).ConfigureAwait(false))!;
            AssertTrue(distinct.Id != first.Id);
            AssertNull(await db.AskActionProposals.ReadAsync("ten_action", "usr_wrong", thread.Id, first.Id).ConfigureAwait(false));
            AssertNull(await db.AskActionProposals.ReadAsync("ten_wrong", "usr_owner", thread.Id, first.Id).ConfigureAwait(false));
            AssertNull(await db.AskActionProposals.ReadAsync("ten_action", "usr_owner", "thr_wrong", first.Id).ConfigureAwait(false));
            AssertEqual(2, (await db.AskActionProposals.EnumerateAsync(p.TenantId, p.UserId, p.ThreadId).ConfigureAwait(false)).Count);
            }
        }

        private static async Task ConcurrentCreateAsync()
        {
            Fixture fixture = await SetupAsync().ConfigureAwait(false);
            using (fixture)
            {
            DatabaseDriver db = fixture.Driver; AskThread thread = fixture.Thread; AskActionProposal p = fixture.Proposal;
            Task<AskActionProposal?> one = db.AskActionProposals.TryCreateAsync(p);
            Task<AskActionProposal?> two = db.AskActionProposals.TryCreateAsync(new AskActionProposal { TenantId = p.TenantId, UserId = p.UserId, ThreadId = p.ThreadId, RequestId = p.RequestId, ToolName = p.ToolName, ArgumentsJson = p.ArgumentsJson, Fingerprint = p.Fingerprint, ExpiresUtc = p.ExpiresUtc });
            AskActionProposal?[] result = await Task.WhenAll(one, two).ConfigureAwait(false);
            AssertNotNull(result[0]); AssertNotNull(result[1]); AssertEqual(result[0]!.Id, result[1]!.Id);
            AssertEqual(1, (await db.AskActionProposals.EnumerateAsync(p.TenantId, p.UserId, p.ThreadId).ConfigureAwait(false)).Count);
            }
        }

        private static async Task ClaimAndTerminalAsync()
        {
            Fixture fixture = await SetupAsync().ConfigureAwait(false);
            using (fixture)
            {
            DatabaseDriver db = fixture.Driver; AskThread thread = fixture.Thread; AskActionProposal p = fixture.Proposal;
            AskActionProposal row = (await db.AskActionProposals.TryCreateAsync(p).ConfigureAwait(false))!;
            Task<bool> a = db.AskActionProposals.TryClaimAsync(p.TenantId, p.UserId, p.ThreadId, row.Id, p.Fingerprint, DateTime.UtcNow);
            Task<bool> b = db.AskActionProposals.TryClaimAsync(p.TenantId, p.UserId, p.ThreadId, row.Id, p.Fingerprint, DateTime.UtcNow);
            bool[] won = await Task.WhenAll(a, b).ConfigureAwait(false);
            AssertEqual(1, won.Count(x => x));
            List<AskWorkReference> refs = new List<AskWorkReference> { new AskWorkReference { Kind = AskWorkKindEnum.Mission, Id = "msn_created" }, new AskWorkReference { Kind = AskWorkKindEnum.Voyage, Id = "vyg_created" } };
            AssertTrue(await db.AskActionProposals.RecordExecutingWorkAsync(p.TenantId, p.UserId, p.ThreadId, row.Id, refs).ConfigureAwait(false));
            AskActionProposal executing = (await db.AskActionProposals.ReadAsync(p.TenantId, p.UserId, p.ThreadId, row.Id).ConfigureAwait(false))!;
            AssertEqual(AskProposalStateEnum.Executing, executing.State); AssertEqual(2, executing.WorkReferences.Count);
            AssertTrue(await db.AskActionProposals.CompleteAsync(p.TenantId, p.UserId, p.ThreadId, row.Id, AskProposalStateEnum.Executed, "Created", refs, DateTime.UtcNow).ConfigureAwait(false));
            AssertFalse(await db.AskActionProposals.TryClaimAsync(p.TenantId, p.UserId, p.ThreadId, row.Id, p.Fingerprint, DateTime.UtcNow).ConfigureAwait(false));
            AskActionProposal saved = (await db.AskActionProposals.ReadAsync(p.TenantId, p.UserId, p.ThreadId, row.Id).ConfigureAwait(false))!;
            AssertEqual(AskProposalStateEnum.Executed, saved.State); AssertEqual(2, saved.WorkReferences.Count); AssertEqual(AskWorkKindEnum.Voyage, saved.WorkReferences[1].Kind);
            }
        }

        private static async Task ExpiredAndHashAsync()
        {
            Fixture fixture = await SetupAsync().ConfigureAwait(false);
            using (fixture)
            {
            DatabaseDriver db = fixture.Driver; AskThread thread = fixture.Thread; AskActionProposal p = fixture.Proposal;
            p.ExpiresUtc = DateTime.UtcNow.AddMinutes(-1);
            AskActionProposal row = (await db.AskActionProposals.TryCreateAsync(p).ConfigureAwait(false))!;
            AssertFalse(await db.AskActionProposals.TryClaimAsync(p.TenantId, p.UserId, p.ThreadId, row.Id, p.Fingerprint, DateTime.UtcNow).ConfigureAwait(false));
            p.Id = new AskActionProposal().Id;
            p.ExpiresUtc = DateTime.UtcNow.AddHours(1); p.Fingerprint = new string('b', 64);
            AskActionProposal row2 = (await db.AskActionProposals.TryCreateAsync(p).ConfigureAwait(false))!;
            AssertFalse(await db.AskActionProposals.TryClaimAsync(p.TenantId, p.UserId, p.ThreadId, row2.Id, new string('c', 64), DateTime.UtcNow).ConfigureAwait(false));
            AssertTrue(await db.AskActionProposals.TryRejectAsync(p.TenantId, p.UserId, p.ThreadId, row2.Id, p.Fingerprint, DateTime.UtcNow).ConfigureAwait(false));
            AssertFalse(await db.AskActionProposals.TryClaimAsync(p.TenantId, p.UserId, p.ThreadId, row2.Id, p.Fingerprint, DateTime.UtcNow).ConfigureAwait(false));
            }
        }

        private static async Task EligibilityAndWorkAsync()
        {
            Fixture fixture = await SetupAsync().ConfigureAwait(false);
            using (fixture)
            {
            DatabaseDriver db = fixture.Driver; AskThread thread = fixture.Thread; AskActionProposal p = fixture.Proposal;
            AskActionProposal? wrongThread = await db.AskActionProposals.TryCreateAsync(new AskActionProposal { TenantId = p.TenantId, UserId = p.UserId, ThreadId = "thr_missing", RequestId = p.RequestId, ToolName = p.ToolName, ArgumentsJson = p.ArgumentsJson, Fingerprint = p.Fingerprint, ExpiresUtc = p.ExpiresUtc }).ConfigureAwait(false);
            AssertNull(wrongThread);
            AskThread second = new AskThread { TenantId = thread.TenantId, UserId = thread.UserId, Title = "Other" };
            await db.AskThreads.CreateAsync(second).ConfigureAwait(false);
            AskActionProposal? wrongTurn = await db.AskActionProposals.TryCreateAsync(new AskActionProposal { TenantId = p.TenantId, UserId = p.UserId, ThreadId = second.Id, RequestId = p.RequestId, ToolName = p.ToolName, ArgumentsJson = p.ArgumentsJson, Fingerprint = p.Fingerprint, ExpiresUtc = p.ExpiresUtc }).ConfigureAwait(false);
            AssertNull(wrongTurn);
            AssertNotNull(await db.AskActionProposals.TryCreateAsync(p).ConfigureAwait(false));
        }
    }
}
}
