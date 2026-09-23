namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;

    /// <summary>
    /// The one definition of the captain name rule every create surface applies: a captain name is unique
    /// across the admiral, so a create whose name is already taken is refused as a conflict before anything
    /// is written. REST answers 409 Conflict; MCP and WebSocket return their error envelope with the same
    /// message.
    /// </summary>
    public static class CaptainNameRule
    {
        #region Public-Members

        /// <summary>
        /// Message returned when the requested captain name is already taken.
        /// </summary>
        public const string NameTakenMessage = "A captain with that name already exists.";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Return the refusal message when a captain named <paramref name="name"/> already exists, or null when
        /// the name is free. An empty name is not checked here.
        /// </summary>
        /// <param name="captains">Captain store.</param>
        /// <param name="name">Requested captain name.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The refusal message, or null when the create may proceed.</returns>
        public static async Task<string?> FindCreateConflictAsync(ICaptainMethods captains, string? name, CancellationToken token = default)
        {
            if (captains == null) throw new ArgumentNullException(nameof(captains));
            if (String.IsNullOrEmpty(name)) return null;
            Captain? existing = await captains.ReadByNameAsync(name, token).ConfigureAwait(false);
            return existing == null ? null : NameTakenMessage;
        }

        #endregion
    }
}
