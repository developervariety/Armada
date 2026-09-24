namespace Armada.Core.Database
{
    using System;

    /// <summary>
    /// Raised when a stored row cannot be read into its model: a required column is missing from the result
    /// set, holds null, or holds a value that does not convert to the property's type. The message names the
    /// entity, the column and the provider, so an unreadable row is reported where it is, never read as a
    /// default value in place of the stored one.
    /// </summary>
    public sealed class StoredRowException : InvalidOperationException
    {
        /// <summary>
        /// Entity whose row could not be read.
        /// </summary>
        public string Entity { get; }

        /// <summary>
        /// Stored column that could not be read.
        /// </summary>
        public string Column { get; }

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="entity">Entity whose row could not be read.</param>
        /// <param name="column">Stored column that could not be read.</param>
        /// <param name="provider">Provider that returned the row.</param>
        /// <param name="problem">What is wrong with the column.</param>
        /// <param name="inner">Underlying conversion failure, if any.</param>
        public StoredRowException(string entity, string column, string provider, string problem, Exception? inner = null)
            : base("Stored " + entity + " row cannot be read on " + provider + ": column " + column + " " + problem + ".", inner)
        {
            Entity = entity;
            Column = column;
        }
    }
}
