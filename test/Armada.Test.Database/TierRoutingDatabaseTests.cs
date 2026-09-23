namespace Armada.Test.Database
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>Provider-backed persistence of captain preference ranks and persona minimum tiers.</summary>
    internal sealed class TierRoutingDatabaseTests
    {
        private readonly DatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly bool _NoCleanup;

        internal TierRoutingDatabaseTests(DatabaseDriver driver, DatabaseSettings settings, bool noCleanup)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _NoCleanup = noCleanup;
        }

        internal async Task VerifyAsync(CancellationToken token)
        {
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            Captain captain = await _Driver.Captains.CreateAsync(new Captain("ranked-" + suffix)
            {
                Model = "example/model-" + suffix,
                Tier = CaptainTierEnum.Premium,
                PreferenceRank = 7
            }, token).ConfigureAwait(false);
            Persona persona = await _Driver.Personas.CreateAsync(new Persona("Routed" + suffix, "persona.worker") { MinimumTier = CaptainTierEnum.Standard }, token).ConfigureAwait(false);

            try
            {
                Captain createdCaptain = await ReopenAndReadCaptainAsync(captain.Id, token).ConfigureAwait(false);
                DatabaseAssert.Equal(7, createdCaptain.PreferenceRank, "Create persists the preference rank");
                DatabaseAssert.True(createdCaptain.Tier == CaptainTierEnum.Premium, "Create keeps the tier beside the rank");
                Persona createdPersona = await ReopenAndReadPersonaAsync(persona.Id, token).ConfigureAwait(false);
                DatabaseAssert.True(createdPersona.MinimumTier == CaptainTierEnum.Standard, "Create persists the minimum tier");

                createdCaptain.PreferenceRank = -3;
                await _Driver.Captains.UpdateAsync(createdCaptain, token).ConfigureAwait(false);
                DatabaseAssert.Equal(-3, (await ReopenAndReadCaptainAsync(captain.Id, token).ConfigureAwait(false)).PreferenceRank, "Update persists a changed rank");

                createdPersona.MinimumTier = CaptainTierEnum.Premium;
                await _Driver.Personas.UpdateAsync(createdPersona, token).ConfigureAwait(false);
                DatabaseAssert.True((await ReopenAndReadPersonaAsync(persona.Id, token).ConfigureAwait(false)).MinimumTier == CaptainTierEnum.Premium, "Update persists a changed minimum tier");

                Persona defaulted = await _Driver.Personas.CreateAsync(new Persona("Plain" + suffix, "persona.worker"), token).ConfigureAwait(false);
                try
                {
                    DatabaseAssert.True(!(await ReopenAndReadPersonaAsync(defaulted.Id, token).ConfigureAwait(false)).MinimumTier.HasValue, "A persona minimum is unset by default");
                }
                finally
                {
                    if (!_NoCleanup)
                        await _Driver.Personas.DeleteAsync(defaulted.Id, token).ConfigureAwait(false);
                }
            }
            finally
            {
                if (!_NoCleanup)
                {
                    await _Driver.Captains.DeleteAsync(captain.Id, token).ConfigureAwait(false);
                    await _Driver.Personas.DeleteAsync(persona.Id, token).ConfigureAwait(false);
                }
            }
        }

        private async Task<Captain> ReopenAndReadCaptainAsync(string captainId, CancellationToken token)
        {
            using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                Captain? captain = await reopened.Captains.ReadAsync(captainId, token).ConfigureAwait(false);
                DatabaseAssert.True(captain != null, "The captain reads back after reopening the database");
                return captain!;
            }
        }

        private async Task<Persona> ReopenAndReadPersonaAsync(string personaId, CancellationToken token)
        {
            using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                Persona? persona = await reopened.Personas.ReadAsync(personaId, token).ConfigureAwait(false);
                DatabaseAssert.True(persona != null, "The persona reads back after reopening the database");
                return persona!;
            }
        }
    }
}
