namespace Armada.Core.Database
{
    using System;

    /// <summary>
    /// Raised when a stored objective refinement session row holds a field value that cannot be read, such as a
    /// status that is not a member of its enum. The row is never read with a default value in its place, because
    /// an unknown status read as <c>Created</c> looks like a session that has not started and the next update of
    /// that session would write the default over the stored value.
    /// </summary>
    public sealed class StoredRefinementSessionDataException : InvalidOperationException
    {
        /// <summary>
        /// Id of the refinement session row that could not be read.
        /// </summary>
        public string SessionId { get; }

        /// <summary>
        /// Stored column that holds the unreadable value.
        /// </summary>
        public string Field { get; }

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="sessionId">Id of the refinement session row.</param>
        /// <param name="field">Stored column that holds the unreadable value.</param>
        /// <param name="problem">What is wrong with the value.</param>
        public StoredRefinementSessionDataException(string sessionId, string field, string problem)
            : base("Objective refinement session " + sessionId + " cannot be read: stored field " + field + " " + problem
                + ". Repair the stored value; the session is not read with a default value in its place.")
        {
            SessionId = sessionId;
            Field = field;
        }
    }
}
