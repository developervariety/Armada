namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// Raised by <see cref="ObjectiveService.LinkVoyageAsync"/> when the objective already has a
    /// nonterminal linked voyage and the new voyage is not a rescue. The dispatch that lost the
    /// race is refused so the objective keeps at most one active voyage; the exception names the
    /// winning voyage so the caller can report it instead of silently duplicating work.
    /// </summary>
    public class ObjectiveAlreadyDispatchedException : InvalidOperationException
    {
        /// <summary>
        /// The id of the already-linked nonterminal voyage that won the race.
        /// </summary>
        public string WinningVoyageId { get; }

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="objectiveId">The objective the voyage could not be linked to.</param>
        /// <param name="winningVoyageId">The existing nonterminal voyage linked to the objective.</param>
        public ObjectiveAlreadyDispatchedException(string objectiveId, string winningVoyageId)
            : base("Objective " + objectiveId + " already has a nonterminal linked voyage " + winningVoyageId + ".")
        {
            WinningVoyageId = winningVoyageId;
        }
    }
}