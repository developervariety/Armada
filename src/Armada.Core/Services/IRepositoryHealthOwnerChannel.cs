namespace Armada.Core.Services
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// Reads repository health on the Harbor runner that owns a vessel checkout.
    /// An unavailable reply means the Admiral must not read a local path instead.
    /// </summary>
    public interface IRepositoryHealthOwnerChannel
    {
        /// <summary>Read facts from the owner host, or report that the owner cannot be asked.</summary>
        /// <param name="vessel">Vessel whose route selected this owner.</param>
        /// <param name="route">Configured owner route.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The owner reply.</returns>
        Task<RepositoryHealthOwnerReply> ReadAsync(Vessel vessel, HarborMissionRoute route, CancellationToken token);
    }

    /// <summary>Owner-host answer for one repository health read.</summary>
    public sealed class RepositoryHealthOwnerReply
    {
        /// <summary>The owner host did not return facts.</summary>
        public bool Unavailable { get; set; }

        /// <summary>Stable reason when <see cref="Unavailable"/> is true.</summary>
        public string? Reason { get; set; }

        /// <summary>Facts read on the owner host.</summary>
        public RepositoryHealthGitFacts? Facts { get; set; }
    }
}
