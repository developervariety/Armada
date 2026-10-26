namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Server;
    using Test.Shared.Infrastructure;
    using static Test.Shared.Infrastructure.Asserts;

    public sealed partial class FleetActionServiceSuite
    {
        private static async Task RunSnapshotsOwnedInputsAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                Fixture fixture = await CreateFixtureAsync(testDb.Driver).ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(fixture, "Snapshot vessel").ConfigureAwait(false);
                FleetActionUpsertRequest definition = Definition();
                definition.PromptTemplate += " Preserve literal JSON {\"status\":\"ok\"}, code { return true; }, and {literal}.";
                FleetAction action = await fixture.Service.CreateAsync(fixture.Owner, definition).ConfigureAwait(false);
                FleetActionRun run = await fixture.Service.StartRunAsync(fixture.Owner, action.Id,
                    new FleetActionRunRequest { RequestId = "request_" + Guid.NewGuid().ToString("N"), VesselIds = new List<string> { vessel.Id } }).ConfigureAwait(false);
                AssertEqual(fixture.Tenant.Id, run.TenantId);
                AssertEqual(fixture.User.Id, run.UserId);
                AssertEqual(action.Id, run.ActionId);
                AssertStartsWith("request_", run.RequestId);
                AssertEqual(vessel.Id, run.Snapshot.Targets[0].VesselId);
                AssertEqual(vessel.Name, run.Snapshot.Targets[0].VesselNameSnapshot);
                AssertContains(vessel.Name, run.Snapshot.Targets[0].RenderedPrompt);
                AssertContains(vessel.Id, run.Snapshot.Targets[0].RenderedPrompt);
                AssertContains("{\"status\":\"ok\"}", run.Snapshot.Targets[0].RenderedPrompt, "JSON braces remain literal prompt text");
                AssertContains("{ return true; }", run.Snapshot.Targets[0].RenderedPrompt, "code braces remain literal prompt text");
                AssertContains("{literal}", run.Snapshot.Targets[0].RenderedPrompt, "only supported tokens are substituted");
                AssertEqual(FleetActionTargetStatusEnum.Pending, run.State.TargetStates[0].Status);
                vessel.Name = "Changed name";
                await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);
                FleetAction savedAction = (await fixture.Service.GetAsync(fixture.Owner, action.Id).ConfigureAwait(false))!;
                savedAction.PromptTemplate = "Changed after run";
                await fixture.Service.UpdateAsync(fixture.Owner, action.Id, new FleetActionUpsertRequest
                {
                    Name = savedAction.Name, Active = true, PromptTemplate = savedAction.PromptTemplate, Concurrency = savedAction.Concurrency
                }).ConfigureAwait(false);
                FleetActionRun stored = (await fixture.Service.GetRunAsync(fixture.Owner, run.Id).ConfigureAwait(false))!;
                AssertContains("Snapshot vessel", stored.Snapshot.Targets[0].RenderedPrompt);
                AssertEqual(false, stored.Snapshot.Targets[0].RenderedPrompt.Contains("Changed after run", StringComparison.Ordinal));
            }
        }

        private static async Task RequestReplayPrecedesMutableLookupAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                Fixture fixture = await CreateFixtureAsync(testDb.Driver).ConfigureAwait(false);
                Vessel first = await CreateVesselAsync(fixture, "First").ConfigureAwait(false);
                Vessel second = await CreateVesselAsync(fixture, "Second").ConfigureAwait(false);
                FleetAction action = await fixture.Service.CreateAsync(fixture.Owner, Definition()).ConfigureAwait(false);
                string requestId = "request_" + Guid.NewGuid().ToString("N");
                FleetActionRunRequest request = new FleetActionRunRequest { RequestId = requestId, VesselIds = new List<string> { first.Id } };
                FleetActionRun run = await fixture.Service.StartRunAsync(fixture.Owner, action.Id, request).ConfigureAwait(false);
                await AssertThrowsAsync<ArgumentException>(() => fixture.Service.StartRunAsync(fixture.Owner, action.Id,
                    new FleetActionRunRequest { RequestId = requestId, VesselIds = new List<string> { " " + first.Id } })).ConfigureAwait(false);
                await fixture.Service.UpdateAsync(fixture.Owner, action.Id, new FleetActionUpsertRequest
                {
                    Name = action.Name, Active = false, PromptTemplate = action.PromptTemplate, Concurrency = action.Concurrency
                }).ConfigureAwait(false);
                FleetActionRun replay = await fixture.Service.StartRunAsync(fixture.Owner, action.Id, request).ConfigureAwait(false);
                AssertEqual(run.Id, replay.Id, "same actor request returns the durable run before reading the now-inactive action");
                await AssertThrowsAsync<InvalidOperationException>(() =>
                    fixture.Service.StartRunAsync(fixture.Owner, action.Id,
                        new FleetActionRunRequest { RequestId = requestId, VesselIds = new List<string> { second.Id } })).ConfigureAwait(false);
            }
        }

        private static async Task OperatorRevocationAndOwnerScopeAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                Fixture fixture = await CreateFixtureAsync(testDb.Driver).ConfigureAwait(false);
                FleetAction action = await fixture.Service.CreateAsync(fixture.Owner, Definition()).ConfigureAwait(false);
                TenantMetadata otherTenant = await testDb.Driver.Tenants.CreateAsync(new TenantMetadata("Other owner")).ConfigureAwait(false);
                UserMaster otherUser = await testDb.Driver.Users.CreateAsync(new UserMaster(otherTenant.Id,
                    "other-" + Guid.NewGuid().ToString("N") + "@example.invalid", "test-password") { IsAdmin = true }).ConfigureAwait(false);
                AuthContext otherCaller = AuthContext.Authenticated(otherTenant.Id, otherUser.Id, true, false, "Session");
                AssertNull(await fixture.Service.GetAsync(otherCaller, action.Id).ConfigureAwait(false), "another operator cannot read an owned definition");
                UserMaster tenantAdmin = await testDb.Driver.Users.CreateAsync(new UserMaster(fixture.Tenant.Id,
                    "tenant-admin-" + Guid.NewGuid().ToString("N") + "@example.invalid", "test-password") { IsTenantAdmin = true }).ConfigureAwait(false);
                AuthContext tenantCaller = AuthContext.Authenticated(fixture.Tenant.Id, tenantAdmin.Id, false, true, "Session");
                await AssertThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ListAsync(tenantCaller)).ConfigureAwait(false);
                fixture.User.Active = false;
                await testDb.Driver.Users.UpdateAsync(fixture.User).ConfigureAwait(false);
                await AssertThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ListAsync(fixture.Owner)).ConfigureAwait(false);
            }
        }

        private static async Task PendingRunCancelIsDurableAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                Fixture fixture = await CreateFixtureAsync(testDb.Driver).ConfigureAwait(false);
                Vessel first = await CreateVesselAsync(fixture, "First").ConfigureAwait(false);
                Vessel second = await CreateVesselAsync(fixture, "Second").ConfigureAwait(false);
                FleetAction action = await fixture.Service.CreateAsync(fixture.Owner, Definition()).ConfigureAwait(false);
                FleetActionRun run = await fixture.Service.StartRunAsync(fixture.Owner, action.Id,
                    new FleetActionRunRequest { RequestId = "request_" + Guid.NewGuid().ToString("N"), VesselIds = new List<string> { first.Id, second.Id } }).ConfigureAwait(false);
                FleetActionRun cancelled = await fixture.Service.CancelRunAsync(fixture.Owner, run.Id).ConfigureAwait(false);
                AssertEqual(FleetActionRunStatusEnum.Cancelled, cancelled.State.Status);
                AssertEqual(2, cancelled.State.TargetStates.Count);
                AssertEqual(true, cancelled.State.TargetStates.TrueForAll(target => target.Status == FleetActionTargetStatusEnum.Cancelled));
            }
        }
    }
}
