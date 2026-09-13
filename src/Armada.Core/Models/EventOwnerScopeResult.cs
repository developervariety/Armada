namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// Outcome of resolving an event's owner, with the record that supplied it or the reason it was not scoped.
    /// </summary>
    public sealed class EventOwnerScopeResult
    {
        #region Public-Members

        /// <summary>
        /// Outcome.
        /// </summary>
        public EventOwnerScopeOutcomeEnum Outcome { get; }

        /// <summary>
        /// Which reference supplied the owner (for example "mission"), or why no owner was applied.
        /// </summary>
        public string Detail { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="outcome">Outcome.</param>
        /// <param name="detail">Source reference or reason.</param>
        public EventOwnerScopeResult(EventOwnerScopeOutcomeEnum outcome, string detail)
        {
            Outcome = outcome;
            Detail = detail ?? throw new ArgumentNullException(nameof(detail));
        }

        #endregion
    }
}
