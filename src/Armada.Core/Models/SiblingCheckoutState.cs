namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// What one declared sibling checkout holds when a captain starts: whether it was provisioned, its HEAD,
    /// whether it carries uncommitted changes, which required paths are absent, and how HEAD relates to the
    /// commit the objective pinned.
    /// </summary>
    public class SiblingCheckoutState
    {
        #region Public-Members

        /// <summary>Declared checkout path, relative to the dock.</summary>
        public string RelativePath { get; set; } = String.Empty;

        /// <summary>Absolute path of the sibling checkout.</summary>
        public string FullPath { get; set; } = String.Empty;

        /// <summary>True when the checkout directory exists and holds files.</summary>
        public bool Present { get; set; } = false;

        /// <summary>HEAD commit of the checkout, or null when it could not be read.</summary>
        public string? Head { get; set; } = null;

        /// <summary>True when clean, false when it carries uncommitted changes, null when unread.</summary>
        public bool? Clean { get; set; } = null;

        /// <summary>Paths the objective requires from this sibling that the checkout does not hold.</summary>
        public List<string> MissingPaths { get; set; } = new List<string>();

        /// <summary>Commit the objective pinned for this sibling, or null when it pins none.</summary>
        public string? PinnedCommit { get; set; } = null;

        /// <summary>True when the objective's sibling pin override provisioned <see cref="PinnedCommit"/> as this checkout.</summary>
        public bool PinProvisioned { get; set; } = false;

        /// <summary>True when the pin is an ancestor of HEAD, false when it is not, null when unknown.</summary>
        public bool? PinIsAncestor { get; set; } = null;

        /// <summary>Assigned paths changed between the pin and HEAD; null when the change was not read.</summary>
        public List<string>? ChangedSincePin { get; set; } = null;

        #endregion
    }
}
