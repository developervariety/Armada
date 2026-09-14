namespace Armada.Runtimes.Tools
{
    using System.IO;

    /// <summary>Indicates that a tool path is outside the mission workspace or crosses a symlink/reparse point.</summary>
    public sealed class WorkspaceBoundaryException : IOException
    {
        /// <summary>Instantiate the boundary error with a safe message.</summary>
        public WorkspaceBoundaryException() : base("The requested path is outside the mission workspace.")
        {
        }
    }
}
