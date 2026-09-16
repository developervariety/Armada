namespace Armada.Test.Database
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>Provider-backed persistence of the Judge PASS operator-review hold on a mission.</summary>
    internal sealed class MissionOperatorHoldDatabaseTests
    {
        private readonly DatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly bool _NoCleanup;

        internal MissionOperatorHoldDatabaseTests(DatabaseDriver driver, DatabaseSettings settings, bool noCleanup)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _NoCleanup = noCleanup;
        }

        internal async Task VerifyAsync(CancellationToken token)
        {
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            Mission mission = await _Driver.Missions.CreateAsync(new Mission("Held judge " + suffix, "Review the work")
            {
                Persona = "Judge",
                Status = MissionStatusEnum.WorkProduced,
                HeldForOperatorReview = true,
                HeldForOperatorReviewReason = "thin PASS " + suffix
            }, token).ConfigureAwait(false);

            try
            {
                Mission created = await ReopenAndReadAsync(mission.Id, token).ConfigureAwait(false);
                DatabaseAssert.True(created.HeldForOperatorReview, "Create persists the hold");
                DatabaseAssert.Equal("thin PASS " + suffix, created.HeldForOperatorReviewReason, "Create persists the hold reason");

                created.Title = "Held judge renamed " + suffix;
                await _Driver.Missions.UpdateAsync(created, token).ConfigureAwait(false);
                Mission kept = await ReopenAndReadAsync(mission.Id, token).ConfigureAwait(false);
                DatabaseAssert.True(kept.HeldForOperatorReview, "An update that carries the hold keeps it");
                DatabaseAssert.Equal("thin PASS " + suffix, kept.HeldForOperatorReviewReason, "An update keeps the hold reason");

                kept.HeldForOperatorReview = false;
                await _Driver.Missions.UpdateAsync(kept, token).ConfigureAwait(false);
                Mission cleared = await ReopenAndReadAsync(mission.Id, token).ConfigureAwait(false);
                DatabaseAssert.True(!cleared.HeldForOperatorReview, "Update clears the hold");
                DatabaseAssert.True(cleared.HeldForOperatorReviewReason == null, "A cleared hold stores no reason");
            }
            finally
            {
                if (!_NoCleanup)
                    await _Driver.Missions.DeleteAsync(mission.Id, token).ConfigureAwait(false);
            }
        }

        private async Task<Mission> ReopenAndReadAsync(string missionId, CancellationToken token)
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
