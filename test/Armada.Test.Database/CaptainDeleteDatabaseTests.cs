#nullable enable

namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// A captain delete clears the signal references to that captain only when the captain is actually removed.
    /// A delete that matches no row, or that a referencing row refuses, leaves every signal reference in place.
    /// </summary>
    internal sealed class CaptainDeleteDatabaseTests
    {
        private readonly DatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly bool _NoCleanup;
        private readonly List<string> _FromSignalIds = new List<string>();

        internal CaptainDeleteDatabaseTests(DatabaseDriver driver, DatabaseSettings settings, bool noCleanup)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _NoCleanup = noCleanup;
        }

        internal async Task VerifyAsync(CancellationToken token)
        {
            DatabaseFixture fixture = new DatabaseFixture(_Driver, _NoCleanup);
            try
            {
                TenantMetadata tenant = await fixture.CreateTenantAsync("captain-delete-tenant", token: token).ConfigureAwait(false);
                UserMaster user = await fixture.CreateUserAsync(tenant.Id, "captain-delete-user", token: token).ConfigureAwait(false);
                TenantMetadata otherTenant = await fixture.CreateTenantAsync("captain-delete-other", token: token).ConfigureAwait(false);
                UserMaster otherUser = await fixture.CreateUserAsync(otherTenant.Id, "captain-delete-other", token: token).ConfigureAwait(false);

                // A scoped delete that names another tenant or user matches no captain, so it must not touch
                // the signals that reference the captain.
                Captain scoped = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "captain-delete-scoped", token).ConfigureAwait(false);
                Signal scopedTo = await fixture.CreateSignalAsync(tenant.Id, user.Id, scoped.Id, token).ConfigureAwait(false);
                Signal scopedFrom = await CreateSignalFromAsync(tenant.Id, user.Id, scoped.Id, token).ConfigureAwait(false);
                await _Driver.Captains.DeleteAsync(otherTenant.Id, scoped.Id, token).ConfigureAwait(false);
                await _Driver.Captains.DeleteAsync(tenant.Id, otherUser.Id, scoped.Id, token).ConfigureAwait(false);
                DatabaseAssert.True(await _Driver.Captains.ReadAsync(scoped.Id, token).ConfigureAwait(false) != null, "Out-of-scope delete keeps the captain");
                await AssertSignalReferencesAsync(scopedTo.Id, scopedFrom.Id, scoped.Id, "out-of-scope delete", token).ConfigureAwait(false);

                // A delete the database refuses leaves the signal references as they were. Providers whose
                // schema lets the referencing row keep or null the captain id complete the delete instead,
                // and then the references must be cleared.
                Captain referenced = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "captain-delete-referenced", token).ConfigureAwait(false);
                Objective objective = await fixture.CreateObjectiveAsync(tenant.Id, user.Id, "captain-delete-objective", null, null, token).ConfigureAwait(false);
                await fixture.CreateObjectiveRefinementSessionAsync(tenant.Id, user.Id, objective.Id, referenced.Id, null,
                    ObjectiveRefinementSessionStatusEnum.Active, token).ConfigureAwait(false);
                Signal referencedTo = await fixture.CreateSignalAsync(tenant.Id, user.Id, referenced.Id, token).ConfigureAwait(false);
                Signal referencedFrom = await CreateSignalFromAsync(tenant.Id, user.Id, referenced.Id, token).ConfigureAwait(false);
                bool refused = false;
                try
                {
                    await _Driver.Captains.DeleteAsync(tenant.Id, referenced.Id, token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    refused = true;
                }

                bool captainRemains = await _Driver.Captains.ReadAsync(referenced.Id, token).ConfigureAwait(false) != null;
                DatabaseAssert.Equal(refused, captainRemains, "A captain remains exactly when its delete was refused");
                if (captainRemains)
                {
                    await AssertSignalReferencesAsync(referencedTo.Id, referencedFrom.Id, referenced.Id, "refused delete", token).ConfigureAwait(false);
                    await _Driver.Objectives.DeleteAsync(objective.Id, token).ConfigureAwait(false);
                }
                else
                {
                    await AssertSignalReferencesAsync(referencedTo.Id, referencedFrom.Id, null, "completed delete", token).ConfigureAwait(false);
                }

                // An ordinary delete clears both references on every provider.
                Captain plain = await fixture.CreateCaptainAsync(tenant.Id, user.Id, "captain-delete-plain", token).ConfigureAwait(false);
                Signal plainTo = await fixture.CreateSignalAsync(tenant.Id, user.Id, plain.Id, token).ConfigureAwait(false);
                Signal plainFrom = await CreateSignalFromAsync(tenant.Id, user.Id, plain.Id, token).ConfigureAwait(false);
                await _Driver.Captains.DeleteAsync(tenant.Id, user.Id, plain.Id, token).ConfigureAwait(false);
                DatabaseAssert.True(await _Driver.Captains.ReadAsync(plain.Id, token).ConfigureAwait(false) == null, "Captain delete removes the captain");
                await AssertSignalReferencesAsync(plainTo.Id, plainFrom.Id, null, "captain delete", token).ConfigureAwait(false);
            }
            finally
            {
                if (!_NoCleanup)
                {
                    foreach (string signalId in _FromSignalIds)
                        await _Driver.Signals.DeleteAsync(signalId, token).ConfigureAwait(false);
                }
                await fixture.CleanupAsync(token).ConfigureAwait(false);
            }
        }

        private async Task<Signal> CreateSignalFromAsync(string tenantId, string userId, string fromCaptainId, CancellationToken token)
        {
            Signal signal = new Signal(SignalTypeEnum.Nudge, "{\"message\":\"from captain\"}")
            {
                TenantId = tenantId,
                UserId = userId,
                FromCaptainId = fromCaptainId,
                Read = false
            };
            await _Driver.Signals.CreateAsync(signal, token).ConfigureAwait(false);
            _FromSignalIds.Add(signal.Id);
            return signal;
        }

        private async Task AssertSignalReferencesAsync(string toSignalId, string fromSignalId, string? expectedCaptainId, string label, CancellationToken token)
        {
            Signal to = DatabaseAssert.NotNull(await _Driver.Signals.ReadAsync(toSignalId, token).ConfigureAwait(false), "Signal to captain survives " + label);
            Signal from = DatabaseAssert.NotNull(await _Driver.Signals.ReadAsync(fromSignalId, token).ConfigureAwait(false), "Signal from captain survives " + label);
            DatabaseAssert.Equal(expectedCaptainId, to.ToCaptainId, "Signal.ToCaptainId after " + label);
            DatabaseAssert.Equal(expectedCaptainId, from.FromCaptainId, "Signal.FromCaptainId after " + label);
        }
    }
}
