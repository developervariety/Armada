namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Server;
    using Test.Shared.Infrastructure;
    using static Test.Shared.Infrastructure.Asserts;

    public sealed partial class AskActionServiceSuite
    {
        private const string InboxKind = "ask_proposal";

        private static async Task<List<InboxItem>> AskItemsAsync(DatabaseDriver database, AuthContext? caller)
        {
            List<InboxItem> items = await new InboxService(database, CreateLogging()).GetInboxAsync(caller).ConfigureAwait(false);
            return items.Where(item => item.Kind == InboxKind).ToList();
        }

        private static async Task<AskActionProposal> ProposeAsync(AskActionService service, Fixture fixture, Func<JsonElement?, Task<object>> write, string argumentsJson)
        {
            HashSet<string> known = (await service.ListAsync(fixture.Owner, fixture.Thread.Id).ConfigureAwait(false))!.Select(proposal => proposal.Id).ToHashSet(StringComparer.Ordinal);
            await CallAsync(write, ScopedCaller(fixture), Args(argumentsJson)).ConfigureAwait(false);
            return (await service.ListAsync(fixture.Owner, fixture.Thread.Id).ConfigureAwait(false))!.Single(proposal => !known.Contains(proposal.Id));
        }

        private static async Task InboxListsOnlyTheCallersOwnPendingProposalsAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                AskActionService service = CreateService(testDb.Driver);
                Func<JsonElement?, Task<object>> write = Register(service, "armada_create_mission", _ => Task.FromResult<object>(new { Success = true }));
                Fixture own = await CreateFixtureAsync(testDb.Driver).ConfigureAwait(false);
                Fixture otherTenant = await CreateFixtureAsync(testDb.Driver).ConfigureAwait(false);
                AskActionProposal ownProposal = await ProposeAsync(service, own, write, "{\"title\":\"own\"}").ConfigureAwait(false);
                AskActionProposal foreignProposal = await ProposeAsync(service, otherTenant, write, "{\"title\":\"foreign\"}").ConfigureAwait(false);

                UserMaster sameTenantPeer = new UserMaster(own.Owner.TenantId!, "ask-peer@example.invalid", "test-password") { IsAdmin = true };
                await testDb.Driver.Users.CreateAsync(sameTenantPeer).ConfigureAwait(false);
                AuthContext peer = AuthContext.Authenticated(own.Owner.TenantId!, sameTenantPeer.Id, true, true, "Session");

                List<InboxItem> ownItems = await AskItemsAsync(testDb.Driver, own.Owner).ConfigureAwait(false);
                AssertEqual(1, ownItems.Count, "the owner sees exactly their own pending proposal");
                AssertEqual(ownProposal.Id, ownItems[0].EntityId, "the item names the proposal");
                AssertEqual("ask_proposal", ownItems[0].EntityType);
                AssertEqual("/ask?thread=" + own.Thread.Id, ownItems[0].Href, "the item links to its conversation");
                AssertContains("Create mission \"own\"", ownItems[0].Title);

                AssertEqual(0, (await AskItemsAsync(testDb.Driver, peer).ConfigureAwait(false)).Count, "a peer administrator in the same tenant sees none of another user's proposals");
                List<InboxItem> foreignItems = await AskItemsAsync(testDb.Driver, otherTenant.Owner).ConfigureAwait(false);
                AssertEqual(1, foreignItems.Count);
                AssertEqual(foreignProposal.Id, foreignItems[0].EntityId, "another tenant's owner sees only their own proposal");

                AuthContext tenantAdminOnly = AuthContext.Authenticated(own.Owner.TenantId!, own.Owner.UserId!, false, true, "Session");
                AssertEqual(0, (await AskItemsAsync(testDb.Driver, tenantAdminOnly).ConfigureAwait(false)).Count, "a caller who is not a global administrator sees none");
                AuthContext captain = AuthContext.Authenticated(own.Owner.TenantId!, own.Owner.UserId!, true, true, "Bearer");
                captain.MissionId = "msn_example";
                AssertEqual(0, (await AskItemsAsync(testDb.Driver, captain).ConfigureAwait(false)).Count, "a mission credential sees none");
                AssertEqual(0, (await AskItemsAsync(testDb.Driver, ScopedCaller(own)).ConfigureAwait(false)).Count, "a saved-chat credential sees none");
                AssertEqual(0, (await AskItemsAsync(testDb.Driver, null).ConfigureAwait(false)).Count, "no caller sees none");
            }
        }

        private static async Task InboxOmitsResolvedAndExpiredProposalsAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                AskActionService service = CreateService(testDb.Driver);
                Func<JsonElement?, Task<object>> write = Register(service, "armada_create_mission", _ => Task.FromResult<object>(new { Success = true }));
                Fixture fixture = await CreateFixtureAsync(testDb.Driver).ConfigureAwait(false);
                AskActionProposal rejected = await ProposeAsync(service, fixture, write, "{\"title\":\"rejected\"}").ConfigureAwait(false);
                AskActionProposal approved = await ProposeAsync(service, fixture, write, "{\"title\":\"approved\"}").ConfigureAwait(false);
                AskActionProposal pending = await ProposeAsync(service, fixture, write, "{\"title\":\"pending\"}").ConfigureAwait(false);
                AskActionProposal expired = new AskActionProposal
                {
                    TenantId = fixture.Owner.TenantId!, UserId = fixture.Owner.UserId!, ThreadId = fixture.Thread.Id, RequestId = fixture.Turn.RequestId,
                    ToolName = "armada_create_mission", ArgumentsJson = "{\"title\":\"expired\"}", Fingerprint = new string('e', 64),
                    CreatedUtc = DateTime.UtcNow.AddMinutes(-31), ExpiresUtc = DateTime.UtcNow.AddMinutes(-1)
                };
                AssertNotNull(await testDb.Driver.AskActionProposals.TryCreateAsync(expired).ConfigureAwait(false));

                AssertEqual(3, (await AskItemsAsync(testDb.Driver, fixture.Owner).ConfigureAwait(false)).Count, "before a decision the three live proposals are listed and the expired one is not");
                AssertEqual(200, (await service.RejectAsync(fixture.Owner, fixture.Thread.Id, rejected.Id, rejected.Fingerprint).ConfigureAwait(false)).StatusCode);
                AssertEqual(200, (await service.ApproveAsync(fixture.Owner, fixture.Thread.Id, approved.Id, approved.Fingerprint).ConfigureAwait(false)).StatusCode);

                List<InboxItem> items = await AskItemsAsync(testDb.Driver, fixture.Owner).ConfigureAwait(false);
                AssertEqual(1, items.Count, "rejected, approved and expired proposals leave the inbox");
                AssertEqual(pending.Id, items[0].EntityId);
            }
        }

        private static async Task InboxItemCannotDecideTheProposalAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                AskActionService service = CreateService(testDb.Driver);
                int invoked = 0;
                Func<JsonElement?, Task<object>> write = Register(service, "armada_create_mission", _ => { Interlocked.Increment(ref invoked); return Task.FromResult<object>(new { Success = true }); });
                Fixture fixture = await CreateFixtureAsync(testDb.Driver).ConfigureAwait(false);
                AskActionProposal proposal = await ProposeAsync(service, fixture, write, "{\"title\":\"hold\"}").ConfigureAwait(false);

                for (int i = 0; i < 3; i++) AssertEqual(1, (await AskItemsAsync(testDb.Driver, fixture.Owner).ConfigureAwait(false)).Count);
                string wire = JsonSerializer.Serialize(await AskItemsAsync(testDb.Driver, fixture.Owner).ConfigureAwait(false));
                AssertFalse(wire.Contains(proposal.Fingerprint, StringComparison.Ordinal), "the item never carries the approval fingerprint");
                AssertFalse(wire.Contains(proposal.ArgumentsJson, StringComparison.Ordinal), "the item never carries the saved arguments");

                AskActionProposal? after = await testDb.Driver.AskActionProposals.ReadAsync(fixture.Owner.TenantId!, fixture.Owner.UserId!, fixture.Thread.Id, proposal.Id).ConfigureAwait(false);
                AssertEqual(AskProposalStateEnum.Pending, after!.State, "listing the inbox leaves the proposal pending");
                AssertEqual(0, invoked, "listing the inbox never runs the proposed tool");
                AssertEqual(409, (await service.ApproveAsync(fixture.Owner, fixture.Thread.Id, proposal.Id, "not-the-fingerprint").ConfigureAwait(false)).StatusCode,
                    "approval still needs the exact fingerprint from the conversation");
                AssertEqual(0, invoked);
            }
        }

        private static async Task ProposalSummaryNamesTheOwnersVesselAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                AskActionService service = CreateService(testDb.Driver);
                Func<JsonElement?, Task<object>> write = Register(service, "armada_create_mission", _ => Task.FromResult<object>(new { Success = true }));
                Fixture own = await CreateFixtureAsync(testDb.Driver).ConfigureAwait(false);
                Fixture other = await CreateFixtureAsync(testDb.Driver).ConfigureAwait(false);
                Vessel ownVessel = new Vessel("Harbor Alpha", "https://example.invalid/alpha.git") { TenantId = own.Owner.TenantId };
                Vessel foreignVessel = new Vessel("Foreign Secret Name", "https://example.invalid/foreign.git") { TenantId = other.Owner.TenantId };
                await testDb.Driver.Vessels.CreateAsync(ownVessel).ConfigureAwait(false);
                await testDb.Driver.Vessels.CreateAsync(foreignVessel).ConfigureAwait(false);

                string named = "{\"title\":\"named\",\"vesselId\":\"" + ownVessel.Id + "\"}";
                string foreign = "{\"title\":\"foreign\",\"vesselId\":\"" + foreignVessel.Id + "\"}";
                string unknown = "{\"title\":\"unknown\",\"vesselId\":\"vsl_missing\"}";
                AskActionProposal namedProposal = await ProposeAsync(service, own, write, named).ConfigureAwait(false);
                AskActionProposal foreignProposal = await ProposeAsync(service, own, write, foreign).ConfigureAwait(false);
                AskActionProposal unknownProposal = await ProposeAsync(service, own, write, unknown).ConfigureAwait(false);

                AssertEqual("Create mission \"named\" on vessel Harbor Alpha", namedProposal.Summary, "the summary shows the owner's vessel by name");
                AssertEqual("Create mission \"foreign\" on vessel " + foreignVessel.Id, foreignProposal.Summary, "another tenant's vessel falls back to its id and never leaks its name");
                AssertEqual("Create mission \"unknown\" on vessel vsl_missing", unknownProposal.Summary, "an unknown vessel falls back to its id");
                AssertContains(ownVessel.Id, namedProposal.ArgumentsJson);
                AssertFalse(namedProposal.ArgumentsJson.Contains("Harbor Alpha", StringComparison.Ordinal), "the saved arguments keep the vessel id, not its name");

                AskActionProposal stored = (await testDb.Driver.AskActionProposals.ReadAsync(own.Owner.TenantId!, own.Owner.UserId!, own.Thread.Id, namedProposal.Id).ConfigureAwait(false))!;
                AssertEqual(namedProposal.Fingerprint, stored.Fingerprint, "naming the vessel does not touch the fingerprint");
                AssertEqual(namedProposal.ArgumentsJson, stored.ArgumentsJson);

                AskProposalDecision decision = await service.RejectAsync(own.Owner, own.Thread.Id, namedProposal.Id, namedProposal.Fingerprint).ConfigureAwait(false);
                AssertEqual("Create mission \"named\" on vessel Harbor Alpha", decision.Proposal!.Summary, "a decision response carries the same summary");
                List<InboxItem> items = await AskItemsAsync(testDb.Driver, own.Owner).ConfigureAwait(false);
                AssertTrue(items.Any(item => item.Title == "Ask approval: Create mission \"foreign\" on vessel " + foreignVessel.Id), "the inbox title uses the same fallback");
                AssertFalse(JsonSerializer.Serialize(items).Contains("Foreign Secret Name", StringComparison.Ordinal), "no foreign vessel name reaches the inbox");
            }
        }
    }
}
