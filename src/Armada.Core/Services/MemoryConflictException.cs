namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// A memory write was refused because it would overwrite another writer: the record changed since
    /// the caller read it, or the key it claims belongs to a different record.
    /// </summary>
    public class MemoryConflictException : InvalidOperationException
    {
        /// <summary>
        /// Conflict kind: "version" when the record moved under the caller, "key" when the key is taken.
        /// </summary>
        public string Kind { get; } = "version";

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="kind">Conflict kind.</param>
        /// <param name="message">Message.</param>
        public MemoryConflictException(string kind, string message) : base(message)
        {
            if (!String.IsNullOrWhiteSpace(kind)) Kind = kind;
        }
    }
}
