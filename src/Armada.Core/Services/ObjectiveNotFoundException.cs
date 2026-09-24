namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// Raised by <see cref="ObjectiveService"/> when the objective a request names does not exist or the caller
    /// may not read it; the two read the same. A linked record that is missing is a validation failure and
    /// raises a plain <see cref="InvalidOperationException"/> instead, so a surface can tell "no such
    /// objective" (404) from "a field names a record that does not exist" (400).
    /// </summary>
    public class ObjectiveNotFoundException : InvalidOperationException
    {
        /// <summary>
        /// Instantiate.
        /// </summary>
        public ObjectiveNotFoundException()
            : base("Objective not found.")
        {
        }
    }
}
