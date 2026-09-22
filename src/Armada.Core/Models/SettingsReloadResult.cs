namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>Result of one settings reload from the bound settings file.</summary>
    public sealed class SettingsReloadResult
    {
        #region Public-Members

        /// <summary>What the reload did.</summary>
        public SettingsReloadOutcomeEnum Outcome { get; set; } = SettingsReloadOutcomeEnum.Invalid;

        /// <summary>Settings file the reload read.</summary>
        public string SettingsFilePath { get; set; } = String.Empty;

        /// <summary>Why the candidate was refused, or null when it was applied.</summary>
        public string? Reason { get; set; }

        /// <summary>True when the candidate was applied to the live settings.</summary>
        public bool Applied => Outcome == SettingsReloadOutcomeEnum.Applied;

        #endregion
    }
}
