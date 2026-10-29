namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// One ecosystem found in a vessel's working directory by <see cref="Services.VesselEcosystemDetector"/>.
    /// </summary>
    public class DetectedEcosystem
    {
        #region Public-Members

        /// <summary>
        /// The ecosystem.
        /// </summary>
        public VesselEcosystemEnum Ecosystem { get; set; } = VesselEcosystemEnum.DotNet;

        /// <summary>
        /// The sub-tool (package manager or build driver) the markers select, for example npm, pnpm, yarn, bun, uv,
        /// poetry, pip, gradlew, gradle, mvnw, mvn, cmake, meson, make, dart or flutter. Never null.
        /// </summary>
        public string Tool
        {
            get => _Tool;
            set => _Tool = value ?? String.Empty;
        }

        /// <summary>
        /// Directory holding the markers, relative to the working directory, with forward slashes. "." is the
        /// working directory itself. Never null.
        /// </summary>
        public string RootPath
        {
            get => _RootPath;
            set => _RootPath = String.IsNullOrEmpty(value) ? "." : value;
        }

        /// <summary>
        /// Marker file (or directory) names found in <see cref="RootPath"/> that identified the ecosystem or
        /// select its tool, for example package.json and pnpm-lock.yaml. Never null.
        /// </summary>
        public List<string> Markers
        {
            get => _Markers;
            set => _Markers = value ?? new List<string>();
        }

        #endregion

        #region Private-Members

        private string _Tool = String.Empty;
        private string _RootPath = ".";
        private List<string> _Markers = new List<string>();

        #endregion

        #region Public-Methods

        /// <summary>
        /// Whether a marker with this name (case-insensitive) was found.
        /// </summary>
        /// <param name="name">Marker name.</param>
        /// <returns>True when present.</returns>
        public bool HasMarker(string name)
        {
            if (String.IsNullOrEmpty(name)) return false;
            foreach (string marker in _Markers)
            {
                if (String.Equals(marker, name, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        #endregion
    }
}
