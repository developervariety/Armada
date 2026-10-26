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
        private static async Task VoyageOutcomesUseCurrentLandingFactsAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                Fixture fixture = await CreateFixtureAsync(testDb.Driver).ConfigureAwait(false);
                FleetAction action = await fixture.Service.CreateAsync(fixture.Owner, Definition()).ConfigureAwait(false);
                await AssertOutcomeAsync(fixture, action, false, false, FleetActionTargetStatusEnum.Completed).ConfigureAwait(false);
                await AssertOutcomeAsync(fixture, action, true, true, FleetActionTargetStatusEnum.WorkProduced).ConfigureAwait(false);
                await AssertOutcomeAsync(fixture, action, true, false, FleetActionTargetStatusEnum.Delivered).ConfigureAwait(false);
            }
        }

        private static async Task AssertOutcomeAsync(Fixture fixture, FleetAction action, bool sourceWork, bool staleLanding, FleetActionTargetStatusEnum expected)
        {
            Vessel vessel = await CreateVesselAsync(fixture, "Outcome vessel").ConfigureAwait(false);
            FleetActionRun run = await fixture.Service.StartRunAsync(fixture.Owner, action.Id,
                new FleetActionRunRequest { RequestId = "request_" + Guid.NewGuid().ToString("N"), VesselIds = new List<string> { vessel.Id } }).ConfigureAwait(false);
            Voyage voyage = await fixture.Database.Voyages.CreateAsync(new Voyage("Saved action outcome")
            {
                TenantId = fixture.Tenant.Id, UserId = fixture.User.Id, Status = VoyageStatusEnum.Complete
            }).ConfigureAwait(false);
            Mission mission = await fixture.Database.Missions.CreateAsync(new Mission
            {
                TenantId = fixture.Tenant.Id, UserId = fixture.User.Id, VesselId = vessel.Id, VoyageId = voyage.Id,
                Title = "Outcome mission", Status = MissionStatusEnum.Complete, CommitHash = sourceWork ? "commit_test" : null
            }).ConfigureAwait(false);
            if (sourceWork)
            {
                await CreateFactAsync(fixture, voyage, mission, MissionAttemptFactTypeEnum.AttemptStarted, DateTime.UtcNow.AddMinutes(-4)).ConfigureAwait(false);
                await CreateFactAsync(fixture, voyage, mission, MissionAttemptFactTypeEnum.Landed, DateTime.UtcNow.AddMinutes(-3)).ConfigureAwait(false);
                if (staleLanding)
                    await CreateFactAsync(fixture, voyage, mission, MissionAttemptFactTypeEnum.Retried, DateTime.UtcNow.AddMinutes(-2)).ConfigureAwait(false);
            }
            FleetActionRunState dispatchingState = new FleetActionRunState
            {
                Status = FleetActionRunStatusEnum.Running, StartedUtc = DateTime.UtcNow,
                TargetStates = new List<FleetActionRunTargetState>
                {
                    new FleetActionRunTargetState
                    {
                        TargetId = run.State.TargetStates[0].TargetId, Status = FleetActionTargetStatusEnum.Dispatching,
                        DispatchStartedUtc = DateTime.UtcNow
                    }
                }
            };
            FleetActionRun dispatching = await fixture.Database.FleetActionRuns.TryCompareExchangeStateAsync(
                run.TenantId, run.UserId, run.Id, run.Revision, dispatchingState).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The test could not claim its saved target.");
            FleetActionRunState runningState = new FleetActionRunState
            {
                Status = FleetActionRunStatusEnum.Running, StartedUtc = dispatching.State.StartedUtc,
                TargetStates = new List<FleetActionRunTargetState>
                {
                    new FleetActionRunTargetState
                    {
                        TargetId = dispatching.State.TargetStates[0].TargetId, Status = FleetActionTargetStatusEnum.Running,
                        DispatchStartedUtc = dispatching.State.TargetStates[0].DispatchStartedUtc, StartedUtc = DateTime.UtcNow,
                        VoyageId = voyage.Id
                    }
                }
            };
            FleetActionRun claimed = await fixture.Database.FleetActionRuns.TryCompareExchangeStateAsync(
                dispatching.TenantId, dispatching.UserId, dispatching.Id, dispatching.Revision, runningState).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The test could not attach the saved voyage.");
            FleetActionMissionRunner runner = CreateRunner(fixture);
            await runner.TickAsync().ConfigureAwait(false);
            FleetActionRun refreshed = await fixture.Service.GetRunAsync(fixture.Owner, claimed.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The outcome test run disappeared.");
            AssertEqual(expected, refreshed.State.TargetStates[0].Status);
            if (expected == FleetActionTargetStatusEnum.Completed)
                AssertContains("no source-producing", refreshed.State.TargetStates[0].Reason ?? String.Empty);
            if (expected == FleetActionTargetStatusEnum.WorkProduced)
                AssertContains("current-attempt landing is not confirmed", refreshed.State.TargetStates[0].Reason ?? String.Empty);
        }

        private static async Task CreateFactAsync(Fixture fixture, Voyage voyage, Mission mission, MissionAttemptFactTypeEnum type, DateTime createdUtc)
        {
            await fixture.Database.MissionAttemptFacts.CreateAsync(new MissionAttemptFact
            {
                TenantId = fixture.Tenant.Id, UserId = fixture.User.Id, VoyageId = voyage.Id, VesselId = mission.VesselId,
                MissionId = mission.Id, RootMissionId = mission.Id, FactType = type, CreatedUtc = createdUtc
            }).ConfigureAwait(false);
        }
    }
}
