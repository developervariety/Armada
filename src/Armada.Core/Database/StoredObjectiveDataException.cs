namespace Armada.Core.Database
{
    using System;

    /// <summary>
    /// Raised when a stored objective row holds a field value that cannot be read: malformed JSON in a
    /// list or document column, or a value that is not a member of the field's enum. The row is never
    /// read as an empty list or a default value, because the next update of that objective would write
    /// the empty or default value over the stored data.
    /// </summary>
    public sealed class StoredObjectiveDataException : InvalidOperationException
    {
        /// <summary>
        /// Id of the objective row that could not be read.
        /// </summary>
        public string ObjectiveId { get; }

        /// <summary>
        /// Stored column that holds the unreadable value.
        /// </summary>
        public string Field { get; }

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="objectiveId">Id of the objective row.</param>
        /// <param name="field">Stored column that holds the unreadable value.</param>
        /// <param name="problem">What is wrong with the value.</param>
        /// <param name="inner">Underlying parse failure, if any.</param>
        public StoredObjectiveDataException(string objectiveId, string field, string problem, Exception? inner = null)
            : base("Objective " + objectiveId + " cannot be read: stored field " + field + " " + problem
                + ". Repair the stored value; the objective is not read with an empty or default value in its place.", inner)
        {
            ObjectiveId = objectiveId;
            Field = field;
        }
    }
}
