namespace Armada.Core.Settings
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// A directory of read-only evidence that captains read beside their dock, such as an exported source tree the
    /// host mounts into the captain environment. A launch registers it as an extra workspace root, so built-in
    /// search and file tools reach it the way they reach a provisioned sibling checkout.
    /// </summary>
    public class CaptainEvidenceRoot
    {
        #region Public-Members

        /// <summary>
        /// Absolute directory as a captain on the Admiral resolves it. Trailing separators are ignored; a blank value
        /// declares nothing.
        /// </summary>
        public string Path
        {
            get => _Path;
            set => _Path = (value ?? String.Empty).Trim().TrimEnd('/', '\\');
        }

        /// <summary>
        /// Vessels whose missions receive the root. An empty list applies the root to every vessel.
        /// </summary>
        public List<string> VesselIds
        {
            get => _VesselIds;
            set => _VesselIds = value ?? new List<string>();
        }

        #endregion

        #region Private-Members

        private string _Path = String.Empty;
        private List<string> _VesselIds = new List<string>();

        #endregion

        #region Public-Methods

        /// <summary>
        /// True when a mission of the vessel receives this root.
        /// </summary>
        /// <param name="vesselId">The mission's vessel, or null.</param>
        /// <returns>True when the root applies.</returns>
        public bool AppliesTo(string? vesselId)
        {
            if (_Path.Length == 0) return false;
            if (_VesselIds.Count == 0) return true;
            if (String.IsNullOrWhiteSpace(vesselId)) return false;
            foreach (string id in _VesselIds)
            {
                if (id != null && String.Equals(id.Trim(), vesselId.Trim(), StringComparison.Ordinal)) return true;
            }
            return false;
        }

        #endregion
    }
}
