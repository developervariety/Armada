namespace Armada.Test.Database
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>Provider-backed persistence of an agent process's start time next to its process identifier.</summary>
    internal sealed class ProcessLaunchIdentityDatabaseTests
    {
        private static readonly TimeSpan _Precision = TimeSpan.FromMilliseconds(1);

        private readonly DatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly bool _NoCleanup;

        internal ProcessLaunchIdentityDatabaseTests(DatabaseDriver driver, DatabaseSettings settings, bool noCleanup)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _NoCleanup = noCleanup;
        }

        internal async Task VerifyAsync(CancellationToken token)
        {
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            DateTime startedUtc = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234560);
            Captain captain = new Captain("launch-identity-" + suffix, AgentRuntimeEnum.ClaudeCode);
            captain.ProcessId = 424242;
            captain.ProcessStartedUtc = startedUtc;
            captain = await _Driver.Captains.CreateAsync(captain, token).ConfigureAwait(false);
            Mission mission = new Mission("Launch identity " + suffix, "Round-trip the process start time") { Status = MissionStatusEnum.InProgress };
            mission.ProcessId = 424242;
            mission.ProcessStartedUtc = startedUtc;
            mission = await _Driver.Missions.CreateAsync(mission, token).ConfigureAwait(false);

            try
            {
                Captain createdCaptain = await ReadCaptainAsync(captain.Id, token).ConfigureAwait(false);
                Mission createdMission = await ReadMissionAsync(mission.Id, token).ConfigureAwait(false);
                AssertStart(startedUtc, createdCaptain.ProcessStartedUtc, "Create persists the captain's process start time");
                AssertStart(startedUtc, createdMission.ProcessStartedUtc, "Create persists the mission's process start time");

                DateTime relaunchedUtc = startedUtc.AddMinutes(7);
                createdCaptain.ProcessId = 434343;
                DatabaseAssert.True(createdCaptain.ProcessStartedUtc == null, "A new process identifier drops the old start time");
                createdCaptain.ProcessStartedUtc = relaunchedUtc;
                await _Driver.Captains.UpdateAsync(createdCaptain, token).ConfigureAwait(false);
                createdMission.ProcessId = 434343;
                createdMission.ProcessStartedUtc = relaunchedUtc;
                await _Driver.Missions.UpdateAsync(createdMission, token).ConfigureAwait(false);
                AssertStart(relaunchedUtc, (await ReadCaptainAsync(captain.Id, token).ConfigureAwait(false)).ProcessStartedUtc, "Update stores the relaunched captain process's start time");
                AssertStart(relaunchedUtc, (await ReadMissionAsync(mission.Id, token).ConfigureAwait(false)).ProcessStartedUtc, "Update stores the relaunched mission process's start time");

                Captain legacy = await ReadCaptainAsync(captain.Id, token).ConfigureAwait(false);
                legacy.ProcessStartedUtc = null;
                await _Driver.Captains.UpdateAsync(legacy, token).ConfigureAwait(false);
                Captain legacyRead = await ReadCaptainAsync(captain.Id, token).ConfigureAwait(false);
                DatabaseAssert.Equal(434343, legacyRead.ProcessId ?? 0, "A process identifier without a start time is kept");
                DatabaseAssert.True(legacyRead.ProcessStartedUtc == null, "A process identifier without a start time reads back without one");
            }
            finally
            {
                if (!_NoCleanup)
                {
                    await _Driver.Missions.DeleteAsync(mission.Id, token).ConfigureAwait(false);
                    await _Driver.Captains.DeleteAsync(captain.Id, token).ConfigureAwait(false);
                }
            }
        }

        private static void AssertStart(DateTime expectedUtc, DateTime? actual, string message)
        {
            DatabaseAssert.True(actual.HasValue, message + " (no value read back)");
            TimeSpan difference = (actual!.Value.ToUniversalTime() - expectedUtc).Duration();
            if (actual.Value.Kind == DateTimeKind.Unspecified) difference = (DateTime.SpecifyKind(actual.Value, DateTimeKind.Utc) - expectedUtc).Duration();
            DatabaseAssert.True(difference <= _Precision, message + " (expected " + expectedUtc.ToString("O") + ", read " + actual.Value.ToString("O") + ")");
        }

        private async Task<Captain> ReadCaptainAsync(string captainId, CancellationToken token)
        {
            using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                Captain? captain = await reopened.Captains.ReadAsync(captainId, token).ConfigureAwait(false);
                DatabaseAssert.True(captain != null, "The captain reads back after reopening the database");
                return captain!;
            }
        }

        private async Task<Mission> ReadMissionAsync(string missionId, CancellationToken token)
        {
            using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                Mission? mission = await reopened.Missions.ReadAsync(missionId, token).ConfigureAwait(false);
                DatabaseAssert.True(mission != null, "The mission reads back after reopening the database");
                return mission!;
            }
        }
    }
}
