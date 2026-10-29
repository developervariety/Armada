namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// A battery pack as listed by the API, with its validation errors when it is malformed.
    /// </summary>
    public class BatteryPackInfo
    {
        #region Public-Members

        /// <summary>
        /// Pack identifier (the directory name when the manifest has none).
        /// </summary>
        public string Id
        {
            get => _Id;
            set => _Id = value ?? String.Empty;
        }

        /// <summary>
        /// Display name.
        /// </summary>
        public string? Name { get; set; } = null;

        /// <summary>
        /// Battery version.
        /// </summary>
        public int Version { get; set; } = 0;

        /// <summary>
        /// Manifest schema version.
        /// </summary>
        public int SchemaVersion { get; set; } = 0;

        /// <summary>
        /// Domain.
        /// </summary>
        public string? Domain { get; set; } = null;

        /// <summary>
        /// Description.
        /// </summary>
        public string? Description { get; set; } = null;

        /// <summary>
        /// Whether the manifest passed validation; only valid packs can run.
        /// </summary>
        public bool Valid { get; set; } = false;

        /// <summary>
        /// Validation errors.
        /// </summary>
        public List<string> Errors
        {
            get => _Errors;
            set => _Errors = value ?? new List<string>();
        }

        /// <summary>
        /// Toolchain names.
        /// </summary>
        public List<string> Toolchain
        {
            get => _Toolchain;
            set => _Toolchain = value ?? new List<string>();
        }

        /// <summary>
        /// Tasks in manifest order.
        /// </summary>
        public List<BatteryTaskInfo> Tasks
        {
            get => _Tasks;
            set => _Tasks = value ?? new List<BatteryTaskInfo>();
        }

        /// <summary>
        /// SHA-256 of the manifest bytes (hex), or null when it could not be read.
        /// </summary>
        public string? ManifestHash { get; set; } = null;

        #endregion

        #region Private-Members

        private string _Id = String.Empty;
        private List<string> _Errors = new List<string>();
        private List<string> _Toolchain = new List<string>();
        private List<BatteryTaskInfo> _Tasks = new List<BatteryTaskInfo>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public BatteryPackInfo()
        {
        }

        #endregion
    }
}
