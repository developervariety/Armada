namespace Armada.Test.Database
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>Provider-backed persistence of the write-only per-vessel GitHub token override.</summary>
    internal sealed class VesselGitHubTokenOverrideDatabaseTests
    {
        private readonly DatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly bool _NoCleanup;

        internal VesselGitHubTokenOverrideDatabaseTests(DatabaseDriver driver, DatabaseSettings settings, bool noCleanup)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _NoCleanup = noCleanup;
        }

        internal async Task VerifyAsync(CancellationToken token)
        {
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 12);
            Vessel vessel = await _Driver.Vessels.CreateAsync(new Vessel
            {
                Name = "token-override-" + suffix,
                RepoUrl = "file:///tmp/token-override-" + suffix + ".git",
                GitHubTokenOverride = "example-override-created-" + suffix
            }, token).ConfigureAwait(false);

            try
            {
                Vessel created = await ReopenAndReadAsync(vessel.Id, token).ConfigureAwait(false);
                DatabaseAssert.Equal("example-override-created-" + suffix, created.GitHubTokenOverride, "Create persists the override");
                DatabaseAssert.True(created.HasGitHubTokenOverride, "A persisted override reads back as configured");

                // An update that carries the stored override forward (an omitted request field) keeps it.
                created.Name = "token-override-renamed-" + suffix;
                await _Driver.Vessels.UpdateAsync(created, token).ConfigureAwait(false);
                Vessel kept = await ReopenAndReadAsync(vessel.Id, token).ConfigureAwait(false);
                DatabaseAssert.Equal("token-override-renamed-" + suffix, kept.Name, "Update writes the other field");
                DatabaseAssert.Equal("example-override-created-" + suffix, kept.GitHubTokenOverride, "Update keeps the carried override");

                kept.GitHubTokenOverride = "example-override-replaced-" + suffix;
                await _Driver.Vessels.UpdateAsync(kept, token).ConfigureAwait(false);
                Vessel replaced = await ReopenAndReadAsync(vessel.Id, token).ConfigureAwait(false);
                DatabaseAssert.Equal("example-override-replaced-" + suffix, replaced.GitHubTokenOverride, "Update replaces the override");

                replaced.GitHubTokenOverride = null;
                await _Driver.Vessels.UpdateAsync(replaced, token).ConfigureAwait(false);
                Vessel cleared = await ReopenAndReadAsync(vessel.Id, token).ConfigureAwait(false);
                DatabaseAssert.True(cleared.GitHubTokenOverride == null, "Update clears the override");
                DatabaseAssert.True(!cleared.HasGitHubTokenOverride, "A cleared override reads back as not configured");
            }
            finally
            {
                if (!_NoCleanup)
                    await _Driver.Vessels.DeleteAsync(vessel.Id, token).ConfigureAwait(false);
            }
        }

        private async Task<Vessel> ReopenAndReadAsync(string vesselId, CancellationToken token)
        {
            using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false))
            {
                Vessel? vessel = await reopened.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);
                DatabaseAssert.True(vessel != null, "The vessel reads back after reopening the database");
                return vessel!;
            }
        }
    }
}
