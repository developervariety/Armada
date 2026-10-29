namespace Armada.Core.Settings
{
    using Armada.Core.Enums;

    /// <summary>
    /// Settings for Ask Armada conversations.
    /// </summary>
    public sealed class AskSettings
    {
        #region Public-Members

        /// <summary>
        /// Native shell and file tool access of a CLI runtime launched for an Ask turn. The default is
        /// <see cref="NativeToolAccessEnum.ReadOnly"/>: no native shell execution and no file writes or edits, whatever the
        /// captain's mission launch defaults are. <see cref="NativeToolAccessEnum.Unrestricted"/> widens an Ask launch to the
        /// mission launch defaults. The value applies to every Ask turn on the server and never changes a mission launch.
        /// </summary>
        public NativeToolAccessEnum NativeToolAccess { get; set; } = NativeToolAccessEnum.ReadOnly;

        /// <summary>
        /// When true, a saved Ask turn on a Claude Code captain sends each native tool call that needs permission to the
        /// thread owner in the Dashboard instead of applying <see cref="NativeToolAccess"/>. The owner allows or denies the call, or
        /// a rule remembered for the thread decides it; a request nobody decides within <see cref="ToolApprovalTimeoutSeconds"/> is
        /// denied. Other runtimes and temporary chat keep <see cref="NativeToolAccess"/>, and a mission launch never uses this.
        /// The default is false.
        /// </summary>
        public bool ApproveNativeToolsInArmada { get; set; } = false;

        private int _ToolApprovalTimeoutSeconds = 120;

        /// <summary>
        /// Seconds a native tool permission request waits for the owner's decision before it is denied. Clamped to 10 through 600.
        /// The default is 120.
        /// </summary>
        public int ToolApprovalTimeoutSeconds
        {
            get => _ToolApprovalTimeoutSeconds;
            set => _ToolApprovalTimeoutSeconds = System.Math.Clamp(value, 10, 600);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Copy the values of another instance into this one, in place.
        /// </summary>
        /// <param name="source">Settings to copy. Null is ignored.</param>
        public void CopyFrom(AskSettings source)
        {
            if (source == null) return;
            NativeToolAccess = source.NativeToolAccess;
            ApproveNativeToolsInArmada = source.ApproveNativeToolsInArmada;
            ToolApprovalTimeoutSeconds = source.ToolApprovalTimeoutSeconds;
        }

        #endregion
    }
}
