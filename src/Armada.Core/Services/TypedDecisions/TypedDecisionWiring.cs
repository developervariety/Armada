namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The shipped decisions that no production decision point consults, each with the reason it is
    /// inert. A decision ships in a mode, so an operator reading a bare <c>Gate</c> takes the decision
    /// for enforced; one that consults nothing is a gate that executes nothing, which is more
    /// dangerous than one that fails. Every surface that reports a decision's mode reports this reason
    /// beside it.
    /// <para>
    /// This list is data, not an assumption: wiring a decision is the deletion of its one line here,
    /// and the wiring guard re-verifies the whole list against the decision points the assemblies
    /// actually expose, so a stale entry and a missing entry both fail the suite.
    /// </para>
    /// </summary>
    public static class TypedDecisionWiring
    {
        #region Public-Members

        /// <summary>
        /// Decision key to the reason no decision point consults it. Empty when every shipped decision
        /// is wired.
        /// </summary>
        public static IReadOnlyDictionary<string, string> UnwiredDecisions { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
        {
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// The reason a decision is not consulted by any decision point.
        /// </summary>
        /// <param name="decisionPoint">Decision key.</param>
        /// <returns>The reason, or null when the decision is wired or the key is unknown.</returns>
        public static string? UnwiredReason(string decisionPoint)
        {
            if (String.IsNullOrWhiteSpace(decisionPoint)) return null;
            return UnwiredDecisions.TryGetValue(decisionPoint, out string? reason) ? reason : null;
        }

        #endregion
    }
}
