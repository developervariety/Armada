namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;

    /// <summary>Provider-backed persistence of the fleet-wide dispatch hold and vessel holds, which share one table.</summary>
    internal sealed class DispatchHoldDatabaseTests
    {
        private readonly DatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;

        internal DispatchHoldDatabaseTests(DatabaseDriver driver, DatabaseSettings settings)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        internal async Task VerifyAsync(CancellationToken token)
        {
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            string held = "vsl_held_" + suffix;
            string second = "vsl_second_" + suffix;
            DispatchHoldSnapshot? fleetBefore = await _Driver.DispatchHolds.ReadAsync(token).ConfigureAwait(false);

            try
            {
                await _Driver.DispatchHolds.SaveVesselAsync(Hold(held, "first reason " + suffix), token).ConfigureAwait(false);
                await _Driver.DispatchHolds.SaveVesselAsync(Hold(second, "second reason " + suffix), token).ConfigureAwait(false);
                await _Driver.DispatchHolds.SaveAsync(Hold(null, "fleet reason " + suffix), token).ConfigureAwait(false);

                (DispatchHoldSnapshot? fleet, List<DispatchHoldSnapshot> vessels) = await ReopenAndReadAsync(suffix, token).ConfigureAwait(false);
                DatabaseAssert.Equal("fleet reason " + suffix, fleet?.Reason, "The fleet hold reads back beside vessel holds");
                DatabaseAssert.Equal(2, vessels.Count, "Saving the fleet hold keeps both vessel holds");
                DatabaseAssert.Equal("first reason " + suffix, vessels.Single(v => v.VesselId == held).Reason, "A vessel hold reads back with its own reason");
                DatabaseAssert.True(vessels.All(v => v.SetBy == "database-test"), "A vessel hold reads back with its setter");

                await _Driver.DispatchHolds.SaveVesselAsync(Hold(held, "replaced reason " + suffix), token).ConfigureAwait(false);
                await _Driver.DispatchHolds.ClearAsync(token).ConfigureAwait(false);
                (fleet, vessels) = await ReopenAndReadAsync(suffix, token).ConfigureAwait(false);
                DatabaseAssert.True(fleet == null, "Clearing the fleet hold removes it");
                DatabaseAssert.Equal(2, vessels.Count, "Clearing the fleet hold keeps every vessel hold");
                DatabaseAssert.Equal("replaced reason " + suffix, vessels.Single(v => v.VesselId == held).Reason, "Saving a vessel hold again replaces its one row");

                await _Driver.DispatchHolds.ClearVesselAsync(second, token).ConfigureAwait(false);
                (fleet, vessels) = await ReopenAndReadAsync(suffix, token).ConfigureAwait(false);
                DatabaseAssert.Equal(1, vessels.Count, "Clearing one vessel hold keeps the other");
                DatabaseAssert.Equal(held, vessels[0].VesselId, "The remaining vessel hold is the one not cleared");
                DatabaseAssert.True(fleet == null, "Clearing a vessel hold engages no fleet hold");
            }
            finally
            {
                await _Driver.DispatchHolds.ClearVesselAsync(held, token).ConfigureAwait(false);
                await _Driver.DispatchHolds.ClearVesselAsync(second, token).ConfigureAwait(false);
                if (fleetBefore != null) await _Driver.DispatchHolds.SaveAsync(fleetBefore, token).ConfigureAwait(false);
                else await _Driver.DispatchHolds.ClearAsync(token).ConfigureAwait(false);
            }
        }

        private static DispatchHoldSnapshot Hold(string? vesselId, string reason)
        {
            return new DispatchHoldSnapshot { VesselId = vesselId, Reason = reason, SetBy = "database-test", SetByUtc = DateTime.UtcNow };
        }

        private async Task<(DispatchHoldSnapshot? Fleet, List<DispatchHoldSnapshot> Vessels)> ReopenAndReadAsync(string suffix, CancellationToken token)
        {
            using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                DispatchHoldSnapshot? fleet = await reopened.DispatchHolds.ReadAsync(token).ConfigureAwait(false);
                List<DispatchHoldSnapshot> vessels = (await reopened.DispatchHolds.ReadVesselsAsync(token).ConfigureAwait(false))
                    .Where(v => v.VesselId != null && v.VesselId.EndsWith(suffix, StringComparison.Ordinal))
                    .ToList();
                return (fleet, vessels);
            }
        }
    }
}
