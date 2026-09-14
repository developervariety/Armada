namespace Armada.Core.Services.Interfaces
{
    /// <summary>
    /// Facts about the host running the admiral.
    /// </summary>
    public interface ISelfDeployHostEnvironment
    {
        /// <summary>
        /// Whether the admiral runs inside a container, where the container runtime owns the process lifecycle.
        /// </summary>
        bool IsContainer { get; }

        /// <summary>
        /// Directory holding the running server assembly.
        /// </summary>
        string CurrentServerDirectory { get; }

        /// <summary>
        /// Current process id.
        /// </summary>
        int CurrentProcessId { get; }
    }
}
