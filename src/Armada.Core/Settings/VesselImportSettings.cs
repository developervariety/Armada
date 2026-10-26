namespace Armada.Core.Settings
{
    using System;
    using System.Collections.Generic;

    /// <summary>Allow-listed roots from which a global administrator may import existing Git checkouts.</summary>
    public sealed class VesselImportSettings
    {
        #region Public-Members

        /// <summary>Configured filesystem roots. An empty list disables checkout discovery and import.</summary>
        public List<string> AllowedRoots
        {
            get => _AllowedRoots;
            set => _AllowedRoots = value ?? new List<string>();
        }

        #endregion

        #region Public-Methods

        /// <summary>Copy allowed roots into this live settings object.</summary>
        /// <param name="source">Settings to copy. Null leaves this object unchanged.</param>
        public void CopyFrom(VesselImportSettings? source)
        {
            if (source == null) return;
            _AllowedRoots = new List<string>(source.AllowedRoots);
        }

        #endregion

        #region Private-Members

        private List<string> _AllowedRoots = new List<string>();

        #endregion
    }
}
