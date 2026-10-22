namespace Armada.Core.Services.Interfaces
{
    using Armada.Core.Models;

    /// <summary>
    /// Reads the live descendants of a captain's agent process. Reading only; it never signals a process.
    /// </summary>
    public interface ICaptainProcessTreeReader
    {
        /// <summary>
        /// Read the live descendants of a process.
        /// </summary>
        /// <param name="rootProcessId">The agent process.</param>
        /// <returns>The descendants, or the named reason they could not be read.</returns>
        CaptainProcessTreeRead ReadDescendants(int rootProcessId);
    }
}
