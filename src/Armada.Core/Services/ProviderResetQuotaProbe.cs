namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Default quota probe that treats a captain's quota as recovered once the provider-published
    /// reset deadline (<see cref="Captain.QuarantineUntilUtc"/>) has elapsed. A captain quarantined
    /// without a published deadline is treated as recovered, since there is no provider signal to wait on.
    /// Runtimes that can query live quota state may supply a more aggressive probe to restore captains
    /// earlier than the published reset time.
    /// </summary>
    public sealed class ProviderResetQuotaProbe : ICaptainQuotaProbe
    {
        #region Public-Methods

        /// <inheritdoc />
        public Task<bool> HasRecoveredAsync(Captain captain, CancellationToken token = default)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));

            bool recovered = !captain.QuarantineUntilUtc.HasValue || captain.QuarantineUntilUtc.Value <= DateTime.UtcNow;
            return Task.FromResult(recovered);
        }

        #endregion
    }
}
