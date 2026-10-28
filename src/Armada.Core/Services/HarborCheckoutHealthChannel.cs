namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>Sends repository health reads through the owner-bound Harbor checkout service.</summary>
    public sealed class HarborCheckoutHealthChannel : IRepositoryHealthOwnerChannel
    {
        private readonly HarborCheckoutService _Checkouts;

        /// <summary>Create the channel.</summary>
        /// <param name="checkouts">Owner-bound checkout service.</param>
        public HarborCheckoutHealthChannel(HarborCheckoutService checkouts)
        {
            _Checkouts = checkouts ?? throw new ArgumentNullException(nameof(checkouts));
        }

        /// <inheritdoc />
        public Task<RepositoryHealthOwnerReply> ReadAsync(Vessel vessel, HarborMissionRoute route, CancellationToken token)
        {
            return _Checkouts.ReadRepositoryHealthAsync(vessel, route, token);
        }
    }
}
