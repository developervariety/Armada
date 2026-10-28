namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// The catalog default command for one ecosystem and intent; see <see cref="Services.EcosystemCommandCatalog"/>.
    /// When <see cref="Supported"/> is false the commands are empty and <see cref="Notes"/> says why.
    /// </summary>
    public class EcosystemCommand
    {
        #region Public-Members

        /// <summary>
        /// The ecosystem, or null for a universal command (osv-scanner audit fallback, gitleaks secrets scan).
        /// </summary>
        public VesselEcosystemEnum? Ecosystem { get; set; } = null;

        /// <summary>
        /// The intent.
        /// </summary>
        public EcosystemCommandIntentEnum Intent { get; set; } = EcosystemCommandIntentEnum.Build;

        /// <summary>
        /// Whether the ecosystem has a command for the intent. False is an explicit NotSupported.
        /// </summary>
        public bool Supported { get; set; } = false;

        /// <summary>
        /// Command for Linux and macOS shells. Empty when not supported. Never null.
        /// </summary>
        public string PosixCommand
        {
            get => _PosixCommand;
            set => _PosixCommand = value ?? String.Empty;
        }

        /// <summary>
        /// Command for Windows (cmd.exe). Empty when not supported. Never null.
        /// </summary>
        public string WindowsCommand
        {
            get => _WindowsCommand;
            set => _WindowsCommand = value ?? String.Empty;
        }

        /// <summary>
        /// Executables that must be on PATH for <see cref="PosixCommand"/>. Never null.
        /// </summary>
        public List<string> RequiredTools
        {
            get => _RequiredTools;
            set => _RequiredTools = value ?? new List<string>();
        }

        /// <summary>
        /// Executables that must be on PATH for <see cref="WindowsCommand"/>. Never null.
        /// </summary>
        public List<string> WindowsRequiredTools
        {
            get => _WindowsRequiredTools;
            set => _WindowsRequiredTools = value ?? new List<string>();
        }

        /// <summary>
        /// Whether the command needs network access (dependency restore, advisory database, registry lookups).
        /// </summary>
        public bool RequiresNetwork { get; set; } = false;

        /// <summary>
        /// Identifier of the parser for this command's audit output (for example npm-audit, pip-audit,
        /// osv-scanner). Null for non-audit intents and unsupported commands.
        /// </summary>
        public string? AuditParserId { get; set; } = null;

        /// <summary>
        /// Short explanation, for example why the intent is not supported. Null when there is nothing to add.
        /// </summary>
        public string? Notes { get; set; } = null;

        #endregion

        #region Private-Members

        private string _PosixCommand = String.Empty;
        private string _WindowsCommand = String.Empty;
        private List<string> _RequiredTools = new List<string>();
        private List<string> _WindowsRequiredTools = new List<string>();

        #endregion

        #region Public-Methods

        /// <summary>
        /// The command for a platform.
        /// </summary>
        /// <param name="windows">True for Windows, false for POSIX.</param>
        /// <returns>Command; empty when not supported.</returns>
        public string GetCommand(bool windows)
        {
            return windows ? _WindowsCommand : _PosixCommand;
        }

        /// <summary>
        /// The required executables for a platform.
        /// </summary>
        /// <param name="windows">True for Windows, false for POSIX.</param>
        /// <returns>Executables.</returns>
        public List<string> GetRequiredTools(bool windows)
        {
            return windows ? _WindowsRequiredTools : _RequiredTools;
        }

        #endregion
    }
}
