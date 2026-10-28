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
        }

        #endregion
    }
}
