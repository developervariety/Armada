namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// The command to run for an intent on a vessel, with where it came from; see
    /// <see cref="Services.EcosystemCommandResolver"/>.
    /// </summary>
    public class ResolvedEcosystemCommand
    {
        #region Public-Members

        /// <summary>
        /// The intent.
        /// </summary>
        public EcosystemCommandIntentEnum Intent { get; set; } = EcosystemCommandIntentEnum.Build;

        /// <summary>
        /// Where the command came from. <see cref="EcosystemCommandSourceEnum.NotSupported"/> means
        /// <see cref="Command"/> is empty.
        /// </summary>
        public EcosystemCommandSourceEnum Source { get; set; } = EcosystemCommandSourceEnum.NotSupported;

        /// <summary>
        /// The command for the requested platform. Empty when not supported. Never null.
        /// </summary>
        public string Command
        {
            get => _Command;
            set => _Command = value ?? String.Empty;
        }

        /// <summary>
        /// The ecosystem the command belongs to; null for a vessel override, a root Makefile or justfile target,
        /// a universal fallback, or not supported.
        /// </summary>
        public VesselEcosystemEnum? Ecosystem { get; set; } = null;

        /// <summary>
        /// Directory to run the command in, relative to the working directory, with forward slashes. Never null.
        /// </summary>
        public string RootPath
        {
            get => _RootPath;
            set => _RootPath = String.IsNullOrEmpty(value) ? "." : value;
        }

        /// <summary>
        /// Human-readable origin, for example "package.json script lint", "Makefile target test",
        /// "Vessel.TestCommand" or "Node default (pnpm)". Never null.
        /// </summary>
        public string Detail
        {
            get => _Detail;
            set => _Detail = value ?? String.Empty;
        }

        /// <summary>
        /// Executables the command needs on PATH. Empty for overrides and repo scripts, whose tools are unknown.
        /// Never null.
        /// </summary>
        public List<string> RequiredTools
        {
            get => _RequiredTools;
            set => _RequiredTools = value ?? new List<string>();
        }

        /// <summary>
        /// Whether the command needs network access; false when unknown (overrides and repo scripts).
        /// </summary>
        public bool RequiresNetwork { get; set; } = false;

        /// <summary>
        /// Parser identifier for audit output, from the catalog; null otherwise.
        /// </summary>
        public string? AuditParserId { get; set; } = null;

        #endregion

        #region Private-Members

        private string _Command = String.Empty;
        private string _RootPath = ".";
        private string _Detail = String.Empty;
        private List<string> _RequiredTools = new List<string>();

        #endregion
    }
}
