namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// What a captain dock for one objective provides, as the preflight text half sees it: the sibling
    /// checkouts provisioned beside the dock, the tools that resolve on the captain PATH, and the evidence
    /// roots mounted for the target vessel. The dispatch preview builds it from the vessel declaration and the
    /// captain execution environment, so the model judges dock observability against what a dock actually
    /// holds. Names and relative paths only; it never carries a host path or a credential.
    /// </summary>
    public class PreflightDockContext
    {
        /// <summary>Operating system of the captain environment.</summary>
        public string OperatingSystem { get; set; } = String.Empty;

        /// <summary>True when captains run inside a container boundary.</summary>
        public bool IsContainer { get; set; } = false;

        /// <summary>
        /// Sibling checkouts provisioned beside the dock, as dock-relative paths (for example <c>../Shared</c>),
        /// each followed by the extraction artifact paths it provisions, when any.
        /// </summary>
        public List<string> SiblingCheckouts
        {
            get => _SiblingCheckouts;
            set => _SiblingCheckouts = value ?? new List<string>();
        }

        /// <summary>Tools that resolve on the captain PATH.</summary>
        public List<string> AvailableTools
        {
            get => _AvailableTools;
            set => _AvailableTools = value ?? new List<string>();
        }

        /// <summary>Tools the preview probed that do not resolve on the captain PATH.</summary>
        public List<string> MissingTools
        {
            get => _MissingTools;
            set => _MissingTools = value ?? new List<string>();
        }

        /// <summary>Evidence roots mounted for the target vessel, by directory name.</summary>
        public List<string> EvidenceRoots
        {
            get => _EvidenceRoots;
            set => _EvidenceRoots = value ?? new List<string>();
        }

        /// <summary>Evidence roots configured for the target vessel whose directory does not exist, by directory name.</summary>
        public List<string> MissingEvidenceRoots
        {
            get => _MissingEvidenceRoots;
            set => _MissingEvidenceRoots = value ?? new List<string>();
        }

        private List<string> _SiblingCheckouts = new List<string>();
        private List<string> _AvailableTools = new List<string>();
        private List<string> _MissingTools = new List<string>();
        private List<string> _EvidenceRoots = new List<string>();
        private List<string> _MissingEvidenceRoots = new List<string>();
    }
}
