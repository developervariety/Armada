namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// The answer to the Workspace's Open in Terminal (POST /api/v1/workspace/vessels/{vesselId}/open-terminal): whether a
    /// terminal window opened at the vessel's checkout and where, or why not, with the path so the caller can open one
    /// itself.
    /// </summary>
    public class WorkspaceOpenTerminalResult
    {
        #region Public-Members

        /// <summary>
        /// True when a terminal window opened.
        /// </summary>
        public bool Opened { get; set; } = false;

        /// <summary>
        /// The checkout's path on the host that holds it, or null when the vessel has none.
        /// </summary>
        public string? Path { get; set; } = null;

        /// <summary>
        /// Where the checkout is: a Harbor, the Admiral's computer, or nowhere.
        /// </summary>
        public WorkspaceTerminalLocationEnum Location { get; set; } = WorkspaceTerminalLocationEnum.None;

        /// <summary>
        /// The Harbor's display name, or the Admiral computer's name, or null.
        /// </summary>
        public string? HostName { get; set; } = null;

        /// <summary>
        /// Why no terminal opened, as one sentence; null when one did.
        /// </summary>
        public string? Message { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public WorkspaceOpenTerminalResult()
        {
        }

        #endregion
    }
}
