namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Context;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;

    public sealed partial class MissionServiceRequestedCaptainRoutingTests
    {
        private async Task VerifyHarborCapacityRefusalReclaimsBeforeRetryAsync()
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                ArmadaSettings settings = CreateSettings();
                settings.Harbor.Enabled = true;
                Captain captain = await CreateCaptainAsync(testDb.Driver, "harbor-capacity-retry", CaptainStateEnum.Idle,
                    CaptainTierEnum.Standard, "Worker").ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                captain.TenantId = Armada.Core.Constants.DefaultTenantId;
                captain.UserId = Armada.Core.Constants.DefaultUserId;
                vessel.TenantId = captain.TenantId;
                vessel.UserId = captain.UserId;
                await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);
                await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);
                string runnerId = "runner-harbor-capacity-retry";
                string grantId = "grant-harbor-capacity-retry";
                settings.Harbor.MissionRoutes.Add(new HarborMissionRoute
                {
                    CaptainId = captain.Id, RunnerId = runnerId,
                    CheckoutMode = HarborCheckoutModeEnum.HarborOwned, RepositoryGrantId = grantId
                });
                AssertTrue(await testDb.Driver.HarborRunnerEnrollments.TryEnrollAsync(new HarborRunnerEnrollment
                {
                    RunnerId = runnerId, TenantId = vessel.TenantId!, UserId = vessel.UserId!,
                    AuthMethod = "unit-test", Generation = 1, Active = true
                }, 0).ConfigureAwait(false), "the runner enrollment is active");

                HarborCheckoutService checkouts = new HarborCheckoutService(testDb.Driver, settings);
                RetryAfterPersistedDockChannel channel = new RetryAfterPersistedDockChannel(throwAfterFirstProvision: false);
                checkouts.SetChannel(channel);
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                StubGitService git = new StubGitService();
                HostAwareGitService hostGit = new HostAwareGitService(git, checkouts);
                DockService docks = new DockService(logging, testDb.Driver, settings, hostGit, checkouts);
                checkouts.SetDockLifecycle(docks);
                CaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
                int launchCount = 0;
                captains.OnLaunchAgent = (_, _, _) =>
                {
                    launchCount++;
                    if (launchCount == 1)
                        throw new HarborLaunchException(MissionAssignmentHold.HarborRunnerCapacity, runnerId: runnerId);
                    return Task.FromResult(64102);
                };
                MissionService missions = new MissionService(logging, testDb.Driver, settings, docks, captains, git: hostGit,
                    resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                missions.SetHarborCheckouts(checkouts);
                Mission mission = await CreateMissionAsync(testDb.Driver, vessel, null, null).ConfigureAwait(false);
                mission.TenantId = captain.TenantId;
                mission.UserId = captain.UserId;
                await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);

                AssertFalse(await missions.TryAssignAsync(mission, vessel).ConfigureAwait(false),
                    "late capacity refusal returns the mission to Pending");
                Mission waiting = (await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                AssertTrue(String.IsNullOrWhiteSpace(waiting.DockProvisionAttemptId),
                    "the mission releases its attempt ID only after the dock removal is confirmed");
                List<Dock> firstDocks = await testDb.Driver.Docks.EnumerateByVesselAsync(vessel.Id).ConfigureAwait(false);
                AssertEqual(1, firstDocks.Count, "the refused attempt has one durable dock row");
                AssertFalse(firstDocks[0].Active, "the first checkout is durably removed before retry");
                AssertTrue(HarborCheckoutRemovalIsTerminal(firstDocks[0]), "the dock has terminal removal proof");

                bool retryAssigned = await missions.TryAssignAsync(waiting, vessel).ConfigureAwait(false);
                Mission retryState = (await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                AssertTrue(retryAssigned,
                    "the mission provisions a fresh attempt after confirmed removal; state=" + retryState.AssignmentState
                    + ", hold=" + retryState.AssignmentHoldReason + ", failure=" + retryState.FailureReason
                    + ", provisionCount=" + channel.ProvisionCount + ", lastOperation=" + channel.LastOperation);
                Mission assigned = (await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                AssertFalse(String.IsNullOrWhiteSpace(assigned.DockProvisionAttemptId), "the retry owns a new attempt ID");
                AssertFalse(String.Equals(firstDocks[0].ProvisionAttemptId, assigned.DockProvisionAttemptId, StringComparison.Ordinal),
                    "the retry does not reuse the removed attempt ID");
                AssertEqual(2, channel.ProvisionCount, "the new attempt is provisioned once");
            }
        }

    }
}
