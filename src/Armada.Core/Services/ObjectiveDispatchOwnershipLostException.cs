namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// Raised when a dispatch attempt no longer owns every objective admission lease it acquired.
    /// The attempt must not link objectives; its caller cancels the voyage it created.
    /// </summary>
    public class ObjectiveDispatchOwnershipLostException : InvalidOperationException
    {
        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="detail">Which lease was lost or why ownership could not be confirmed.</param>
        public ObjectiveDispatchOwnershipLostException(string detail)
            : base("Objective dispatch admission was lost before linking completed: " + detail)
        {
        }
    }
}
