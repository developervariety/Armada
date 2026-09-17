namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// What the one-time tier record migration found and changed.
    /// </summary>
    public sealed class TierRecordMigrationResult
    {
        #region Public-Members

        /// <summary>Outcome: nothing to migrate.</summary>
        public const string OutcomeNothingToMigrate = "nothing_to_migrate";

        /// <summary>Outcome: the migration already ran; retired keys still present are ignored.</summary>
        public const string OutcomeAlreadyMigrated = "already_migrated";

        /// <summary>Outcome: the migration planned the changes without applying them.</summary>
        public const string OutcomePlanned = "planned";

        /// <summary>Outcome: the migration applied the changes and saved the settings.</summary>
        public const string OutcomeMigrated = "migrated";

        /// <summary>The outcome code.</summary>
        public string Outcome { get; set; } = OutcomeNothingToMigrate;

        /// <summary>Captain records whose tier or preference rank changes.</summary>
        public List<CaptainTierMigrationChange> Captains { get; set; } = new List<CaptainTierMigrationChange>();

        /// <summary>Persona records flagged as specialists.</summary>
        public List<PersonaSpecialistMigrationChange> Personas { get; set; } = new List<PersonaSpecialistMigrationChange>();

        /// <summary>Retired specialist persona names that match no persona record.</summary>
        public List<string> UnmatchedSpecialistPersonas { get; set; } = new List<string>();

        /// <summary>Path of the settings backup written before the retired keys were removed, when one was written.</summary>
        public string? SettingsBackupPath { get; set; } = null;

        #endregion
    }
}
