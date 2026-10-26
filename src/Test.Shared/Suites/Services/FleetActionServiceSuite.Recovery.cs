namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Test.Shared.Infrastructure;
    using static Test.Shared.Infrastructure.Asserts;

    public sealed partial class FleetActionServiceSuite
    {
        private static async Task RunnerClaimIsBoundedAndUnknownIsFencedAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                Fixture fixture = await CreateFixtureAsync(testDb.Driver).ConfigureAwait(false);
                await CreateWorkerCaptainAsync(fixture).ConfigureAwait(false);
                Vessel first = await CreateVesselAsync(fixture, "Held dispatch").ConfigureAwait(false);
                Vessel second = await CreateVesselAsync(fixture, "Blocked dispatch").ConfigureAwait(false);
                FleetAction action = await fixture.Service.CreateAsync(fixture.Owner, new FleetActionUpsertRequest
                {
                    Name = "Bounded run", Active = true, PromptTemplate = "Inspect {vesselName}.", Concurrency = 1
                }).ConfigureAwait(false);
                FleetActionRun run = await fixture.Service.StartRunAsync(fixture.Owner, action.Id,
                    new FleetActionRunRequest { RequestId = "request_" + Guid.NewGuid().ToString("N"), VesselIds = new List<string> { first.Id, second.Id } }).ConfigureAwait(false);
                TestAdmiral admiral = (TestAdmiral)(object)fixture.Admiral;
                admiral.HoldNextDispatch(true);
                FleetActionMissionRunner firstRunner = CreateRunner(fixture);
                FleetActionMissionRunner secondRunner = CreateRunner(fixture);
                Task firstTick = firstRunner.TickAsync();
                try
                {
                    await admiral.WaitForDispatchAsync().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                    await secondRunner.TickAsync().ConfigureAwait(false);
                    FleetActionRun held = await fixture.Service.GetRunAsync(fixture.Owner, run.Id).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The held run disappeared.");
                    AssertEqual(1, admiral.DispatchCalls, "the second runner cannot claim beyond the one saved slot");
                    AssertEqual(FleetActionTargetStatusEnum.Dispatching, held.State.TargetStates[0].Status);
                    AssertEqual(FleetActionTargetStatusEnum.Pending, held.State.TargetStates[1].Status);
                }
                finally
                {
                    admiral.ReleaseDispatch();
                    await firstTick.ConfigureAwait(false);
                }
                FleetActionRun uncertain = await fixture.Service.GetRunAsync(fixture.Owner, run.Id).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The uncertain run disappeared.");
                AssertEqual(FleetActionTargetStatusEnum.Uncertain, uncertain.State.TargetStates[0].Status);
                AssertEqual(FleetActionTargetStatusEnum.Pending, uncertain.State.TargetStates[1].Status);
                await firstRunner.TickAsync().ConfigureAwait(false);
                AssertEqual(1, admiral.DispatchCalls, "an unknown dispatch is never repeated");
            }
        }

        private static async Task RecoveryFencesDispatchAndBlocksPendingAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                Fixture fixture = await CreateFixtureAsync(testDb.Driver).ConfigureAwait(false);
                Vessel first = await CreateVesselAsync(fixture, "Interrupted").ConfigureAwait(false);
                Vessel second = await CreateVesselAsync(fixture, "Still pending").ConfigureAwait(false);
                FleetAction action = await fixture.Service.CreateAsync(fixture.Owner, Definition()).ConfigureAwait(false);
                FleetActionRun run = await fixture.Service.StartRunAsync(fixture.Owner, action.Id,
                    new FleetActionRunRequest { RequestId = "request_" + Guid.NewGuid().ToString("N"), VesselIds = new List<string> { first.Id, second.Id } }).ConfigureAwait(false);
                FleetActionRunState state = new FleetActionRunState
                {
                    Status = FleetActionRunStatusEnum.Running, StartedUtc = DateTime.UtcNow,
                    TargetStates = new List<FleetActionRunTargetState>
                    {
                        new FleetActionRunTargetState
                        {
                            TargetId = run.State.TargetStates[0].TargetId, Status = FleetActionTargetStatusEnum.Dispatching,
                            DispatchStartedUtc = DateTime.UtcNow
                        },
                        new FleetActionRunTargetState { TargetId = run.State.TargetStates[1].TargetId }
                    }
                };
                FleetActionRun? claimed = await testDb.Driver.FleetActionRuns.TryCompareExchangeStateAsync(
                    run.TenantId, run.UserId, run.Id, run.Revision, state).ConfigureAwait(false);
                AssertNotNull(claimed, "the fixture stores one claimed target and one pending target");
                FleetActionMissionRunner runner = CreateRunner(fixture);
                await runner.RecoverAsync().ConfigureAwait(false);
                await runner.TickAsync().ConfigureAwait(false);
                FleetActionRun recovered = await fixture.Service.GetRunAsync(fixture.Owner, run.Id).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The recovered run disappeared.");
                AssertEqual(FleetActionRunStatusEnum.Uncertain, recovered.State.Status);
                AssertEqual(FleetActionTargetStatusEnum.Uncertain, recovered.State.TargetStates[0].Status);
                AssertEqual(FleetActionTargetStatusEnum.Pending, recovered.State.TargetStates[1].Status,
                    "an uncertain target blocks later targets in the same run");
            }
        }
    }
}
