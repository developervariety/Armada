namespace Test.Shared.Suites.Services
{
    using System;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Server;
    using Test.Shared.Infrastructure;
    using static Test.Shared.Infrastructure.Asserts;

    public sealed partial class AskActionServiceSuite
    {
        private static async Task ProposalGateAndOrdinaryCallsAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                Fixture fixture = await CreateFixtureAsync(testDb.Driver).ConfigureAwait(false);
                AskActionService service = CreateService(testDb.Driver);
                int invoked = 0;
                Func<JsonElement?, Task<object>> handler = _ =>
                {
                    Interlocked.Increment(ref invoked);
                    return Task.FromResult<object>(new { Success = true });
                };
                Func<JsonElement?, Task<object>> write = Register(service, "armada_create_mission", handler);
                Func<JsonElement?, Task<object>> read = Register(service, "armada_status", handler);

                object proposalResult = await CallAsync(write, ScopedCaller(fixture), Args("{\"title\":\"write\"}")).ConfigureAwait(false);
                AssertEqual(0, invoked, "a saved-turn mutation does not reach its original handler");
                AssertContains("ask_action_proposed", JsonSerializer.Serialize(proposalResult));
                List<AskActionProposal>? saved = await service.ListAsync(fixture.Owner, fixture.Thread.Id).ConfigureAwait(false);
                AssertNotNull(saved);
                AssertEqual(1, saved!.Count);
                AssertEqual(AskProposalStateEnum.Pending, saved[0].State);

                await CallAsync(write, ScopedCaller(fixture), Args("{\"title\":\"reject\"}")).ConfigureAwait(false);
                AskActionProposal toReject = (await service.ListAsync(fixture.Owner, fixture.Thread.Id).ConfigureAwait(false))!
                    .Find(proposal => proposal.ArgumentsJson.Contains("reject", StringComparison.Ordinal))!;
                AssertEqual(409, (await service.RejectAsync(fixture.Owner, fixture.Thread.Id, toReject.Id, "wrong").ConfigureAwait(false)).StatusCode,
                    "a reject request with a different review fingerprint cannot decide the proposal");
                AskProposalDecision rejected = await service.RejectAsync(fixture.Owner, fixture.Thread.Id, toReject.Id, toReject.Fingerprint).ConfigureAwait(false);
                AssertEqual(200, rejected.StatusCode);
                AssertEqual(AskProposalStateEnum.Rejected, rejected.Proposal!.State);
                AssertEqual(0, invoked, "rejecting a proposed action never invokes its handler");

                await CallAsync(read, ScopedCaller(fixture)).ConfigureAwait(false);
                AssertEqual(1, invoked, "a scoped read tool keeps its normal behavior");
                await CallAsync(write, fixture.Owner, Args("{\"title\":\"ordinary\"}")).ConfigureAwait(false);
                AssertEqual(2, invoked, "an ordinary human caller keeps legacy direct-call behavior");
            }
        }

        private static async Task FingerprintApprovalIsOnceAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                Fixture fixture = await CreateFixtureAsync(testDb.Driver).ConfigureAwait(false);
                AskActionService first = CreateService(testDb.Driver);
                AskActionService second = CreateService(testDb.Driver);
                TaskCompletionSource<bool> entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<object> release = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                int invoked = 0;
                Func<JsonElement?, Task<object>> handler = _ =>
                {
                    Interlocked.Increment(ref invoked);
                    entered.TrySetResult(true);
                    return release.Task;
                };
                Func<JsonElement?, Task<object>> firstTool = Register(first, "armada_create_mission", handler);
                Register(second, "armada_create_mission", handler);
                await CallAsync(firstTool, ScopedCaller(fixture), Args("{\"title\":\"approve\"}")).ConfigureAwait(false);
                AskActionProposal proposal = (await first.ListAsync(fixture.Owner, fixture.Thread.Id).ConfigureAwait(false))![0];

                AskProposalDecision wrongFingerprint = await first.ApproveAsync(fixture.Owner, fixture.Thread.Id, proposal.Id, "wrong").ConfigureAwait(false);
                AssertEqual(409, wrongFingerprint.StatusCode);
                AssertEqual(0, invoked, "a stale review fingerprint cannot invoke the handler");

                Fixture other = await CreateFixtureAsync(testDb.Driver).ConfigureAwait(false);
                AskProposalDecision wrongOwner = await first.ApproveAsync(other.Owner, fixture.Thread.Id, proposal.Id, proposal.Fingerprint).ConfigureAwait(false);
                AssertEqual(404, wrongOwner.StatusCode);
                AuthContext scoped = ScopedCaller(fixture);
                await AssertThrowsAsync<UnauthorizedAccessException>(async () => await first.ApproveAsync(scoped, fixture.Thread.Id, proposal.Id, proposal.Fingerprint).ConfigureAwait(false));
                AssertEqual(0, invoked, "wrong owner and scoped callers cannot approve");

                Task<AskProposalDecision> accepted = first.ApproveAsync(fixture.Owner, fixture.Thread.Id, proposal.Id, proposal.Fingerprint);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                AskProposalDecision concurrent = await second.ApproveAsync(fixture.Owner, fixture.Thread.Id, proposal.Id, proposal.Fingerprint).ConfigureAwait(false);
                AssertEqual(AskProposalStateEnum.Uncertain, concurrent.Proposal!.State, "a concurrent service cannot treat the active handler as a completed action");
                AssertEqual(1, invoked, "concurrent approvals invoke one handler");
                release.TrySetResult(new { Success = true });
                AskProposalDecision completed = await accepted.ConfigureAwait(false);
                AssertEqual(AskProposalStateEnum.Executed, completed.Proposal!.State);

                AskActionService recreated = CreateService(testDb.Driver);
                Register(recreated, "armada_create_mission", handler);
                AskProposalDecision replay = await recreated.ApproveAsync(fixture.Owner, fixture.Thread.Id, proposal.Id, proposal.Fingerprint).ConfigureAwait(false);
                AssertEqual(AskProposalStateEnum.Executed, replay.Proposal!.State);
                AssertEqual(1, invoked, "a recreated service reads the durable decision and does not invoke again");
            }
        }

        private static async Task SecretArgumentsAreNotSavedOrEchoedAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                Fixture fixture = await CreateFixtureAsync(testDb.Driver).ConfigureAwait(false);
                AskActionService service = CreateService(testDb.Driver);
                int invoked = 0;
                Func<JsonElement?, Task<object>> tool = Register(service, "armada_create_mission", _ =>
                {
                    invoked++;
                    return Task.FromResult<object>(new { Success = true });
                });
                string secret = "ghp_" + new string('a', 36);
                object response = await CallAsync(tool, ScopedCaller(fixture), Args("{\"title\":\"safe\",\"description\":\"\" ,\"comment\":\"" + secret + "\"}")).ConfigureAwait(false);
                string responseJson = JsonSerializer.Serialize(response);
                AssertFalse(responseJson.Contains(secret, StringComparison.Ordinal), "the refusal does not echo secret-shaped text");
                AssertContains("ask_action_not_supported", responseJson);
                AssertEqual(0, invoked, "secret-bearing arguments do not reach the handler");
                AssertEqual(0, (await service.ListAsync(fixture.Owner, fixture.Thread.Id).ConfigureAwait(false))!.Count, "secret-bearing arguments leave no proposal row");
            }
        }

        private static async Task ThrowingHandlerStaysUncertainAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                Fixture fixture = await CreateFixtureAsync(testDb.Driver).ConfigureAwait(false);
                AskActionService service = CreateService(testDb.Driver);
                int sideEffects = 0;
                Func<JsonElement?, Task<object>> handler = _ =>
                {
                    sideEffects++;
                    throw new InvalidOperationException("simulated failure after the effect");
                };
                Func<JsonElement?, Task<object>> tool = Register(service, "armada_create_mission", handler);
                await CallAsync(tool, ScopedCaller(fixture), Args("{\"title\":\"uncertain\"}")).ConfigureAwait(false);
                AskActionProposal proposal = (await service.ListAsync(fixture.Owner, fixture.Thread.Id).ConfigureAwait(false))![0];
                AskProposalDecision decision = await service.ApproveAsync(fixture.Owner, fixture.Thread.Id, proposal.Id, proposal.Fingerprint).ConfigureAwait(false);
                AssertEqual(409, decision.StatusCode);
                AssertEqual(AskProposalStateEnum.Uncertain, decision.Proposal!.State);
                AskActionProposal? durable = await testDb.Driver.AskActionProposals.ReadAsync(fixture.Owner.TenantId!, fixture.Owner.UserId!, fixture.Thread.Id, proposal.Id).ConfigureAwait(false);
                AssertEqual(AskProposalStateEnum.Executing, durable!.State, "the durable claim remains executing for later observation");

                AskActionService recreated = CreateService(testDb.Driver);
                Register(recreated, "armada_create_mission", handler);
                AskActionProposal view = (await recreated.ListAsync(fixture.Owner, fixture.Thread.Id).ConfigureAwait(false))![0];
                AssertEqual(AskProposalStateEnum.Uncertain, view.State, "a recreated service presents the unresolved effect as uncertain");
                AskProposalDecision retry = await recreated.ApproveAsync(fixture.Owner, fixture.Thread.Id, proposal.Id, proposal.Fingerprint).ConfigureAwait(false);
                AssertEqual(AskProposalStateEnum.Uncertain, retry.Proposal!.State, "a later approval observes uncertainty without re-running the handler");
                AssertEqual(1, sideEffects, "the uncertain action is never repeated");
            }
        }
    }
}
