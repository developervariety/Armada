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
        private async Task VerifyHarborProvisionRetryAsync(bool makeOriginalCaptainBusy)
        {
            using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
            {
                ArmadaSettings settings = CreateSettings();
                settings.Harbor.Enabled = true;
                Captain original = await CreateCaptainAsync(testDb.Driver, "harbor-retry-original", CaptainStateEnum.Idle,
                    CaptainTierEnum.Standard, "Worker").ConfigureAwait(false);
                Captain other = await CreateCaptainAsync(testDb.Driver, "harbor-retry-other", CaptainStateEnum.Idle,
                    CaptainTierEnum.Standard).ConfigureAwait(false);
                Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                original.TenantId = Armada.Core.Constants.DefaultTenantId;
                original.UserId = Armada.Core.Constants.DefaultUserId;
                other.TenantId = original.TenantId;
                other.UserId = original.UserId;
                vessel.TenantId = original.TenantId;
                vessel.UserId = original.UserId;
                await testDb.Driver.Captains.UpdateAsync(original).ConfigureAwait(false);
                await testDb.Driver.Captains.UpdateAsync(other).ConfigureAwait(false);
                await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);
                string runnerId = "runner-harbor-retry";
                string grantId = "grant-harbor-retry";
                settings.Harbor.MissionRoutes.Add(new HarborMissionRoute
                {
                    CaptainId = original.Id, RunnerId = runnerId,
                    CheckoutMode = HarborCheckoutModeEnum.HarborOwned, RepositoryGrantId = grantId
                });
                AssertTrue(await testDb.Driver.HarborRunnerEnrollments.TryEnrollAsync(new HarborRunnerEnrollment
                {
                    RunnerId = runnerId, TenantId = vessel.TenantId!, UserId = vessel.UserId!,
                    AuthMethod = "unit-test", Generation = 1, Active = true
                }, 0).ConfigureAwait(false), "the original runner enrollment is active");

                HarborCheckoutService checkouts = new HarborCheckoutService(testDb.Driver, settings);
                RetryAfterPersistedDockChannel channel = new RetryAfterPersistedDockChannel();
                checkouts.SetChannel(channel);
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                StubGitService git = new StubGitService();
                HostAwareGitService hostGit = new HostAwareGitService(git, checkouts);
                DockService docks = new DockService(logging, testDb.Driver, settings, hostGit, checkouts);
                checkouts.SetDockLifecycle(docks);
                CaptainService captains = new CaptainService(logging, testDb.Driver, settings, git, docks);
                captains.OnLaunchAgent = (_, _, _) => Task.FromResult(64101);
                MissionService missions = new MissionService(logging, testDb.Driver, settings, docks, captains, git: hostGit,
                    resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                missions.SetHarborCheckouts(checkouts);
                Mission mission = await CreateMissionAsync(testDb.Driver, vessel, null, null).ConfigureAwait(false);
                mission.TenantId = original.TenantId;
                mission.UserId = original.UserId;
                await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);

                AssertFalse(await missions.TryAssignAsync(mission, vessel).ConfigureAwait(false),
                    "the first provisioning call fails after its dock intent is durable");
                Mission pending = (await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                Dock saved = (await testDb.Driver.Docks.ReadByProvisionAttemptAsync(pending.DockProvisionAttemptId!).ConfigureAwait(false))!;
                AssertEqual(original.Id, saved.CaptainId, "the durable intent records the first captain");
                AssertTrue(String.IsNullOrWhiteSpace(pending.CaptainId), "the failed first attempt has released its mission assignment");
                AssertEqual(saved.BranchName, pending.BranchName, "the failed provision keeps the durable branch identity");
                AssertEqual(saved.Id, pending.DockId, "the failed provision keeps the durable dock pointer");
                AssertEqual("harbor_provision_attempt_unavailable", pending.AssignmentHoldReason,
                    "a failed completion call leaves a named attempt hold");

                if (makeOriginalCaptainBusy)
                {
                    original.State = CaptainStateEnum.Working;
                    await testDb.Driver.Captains.UpdateAsync(original).ConfigureAwait(false);
                    AssertFalse(await missions.TryAssignAsync(pending, vessel).ConfigureAwait(false),
                        "a busy original captain keeps the mission on the existing attempt");
                    Mission held = (await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                    AssertEqual("harbor_provision_attempt_waiting_for_captain", held.AssignmentHoldReason,
                        "the unavailable original captain produces a named hold");
                    AssertEqual(1, channel.ProvisionCount, "the runner receives no request under the other captain");
                    AssertEqual(saved.Id, held.DockId, "the exact durable dock remains attached to the held mission");
                    return;
                }

                bool retryAssigned = await missions.TryAssignAsync(pending, vessel).ConfigureAwait(false);
                Mission retryState = (await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                AssertTrue(retryAssigned,
                    "the retry resumes the persisted dock attempt; state=" + retryState.AssignmentState
                    + ", hold=" + retryState.AssignmentHoldReason + ", failure=" + retryState.FailureReason
                    + ", provisionCount=" + channel.ProvisionCount + ", lastOperation=" + channel.LastOperation);
                Mission assigned = (await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!;
                AssertEqual(original.Id, assigned.CaptainId, "retry selection stays on the captain bound to the dock");
                AssertEqual(saved.BranchName, assigned.BranchName, "retry selection stays on the branch bound to the dock");
                AssertEqual(saved.Id, assigned.DockId, "retry assignment reuses the persisted dock row");
                AssertEqual(2, channel.ProvisionCount, "retry completes the original provision request without creating a second identity");
                AssertEqual(saved.HostCheckout!.CheckoutId, channel.LastBinding!.CheckoutId,
                    "the runner sees the original opaque checkout ID on retry");
                AssertFalse(String.Equals(other.Id, assigned.CaptainId, StringComparison.Ordinal), "the available alternate captain is not selected");
            }
        }

    }
}
