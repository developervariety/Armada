namespace Armada.Core.Services
{
    /// <summary>
    /// Raised when a workspace save conflicts with an external file change.
    /// </summary>
    public class WorkspaceConflictException : Exception
    {
        /// <summary>
        /// Instantiate.
        /// </summary>
        public WorkspaceConflictException(string message) : base(message)
        {
        }
    }
}
