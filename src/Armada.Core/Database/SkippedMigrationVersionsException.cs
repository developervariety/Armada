namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// Raised at database startup when schema_migrations records a version above a known
    /// migration that was never applied. The runner applies only versions above the recorded
    /// maximum, so such a migration would otherwise never run and never be reported.
    /// Startup is refused before any migration, guard or prerequisite step runs.
    /// </summary>
    public class SkippedMigrationVersionsException : InvalidOperationException
    {
        #region Public-Members

        /// <summary>
        /// Database provider whose ledger was read.
        /// </summary>
        public DatabaseTypeEnum Provider { get; }

        /// <summary>
        /// Highest version recorded in schema_migrations.
        /// </summary>
        public int AppliedMaximum { get; }

        /// <summary>
        /// Known migration versions below <see cref="AppliedMaximum"/> that are absent from
        /// schema_migrations, in ascending order.
        /// </summary>
        public IReadOnlyList<int> MissingVersions { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="provider">Database provider whose ledger was read.</param>
        /// <param name="appliedMaximum">Highest recorded version.</param>
        /// <param name="missingVersions">Unapplied known versions below the maximum, ascending.</param>
        public SkippedMigrationVersionsException(DatabaseTypeEnum provider, int appliedMaximum, IReadOnlyList<int> missingVersions)
            : base(BuildMessage(provider, appliedMaximum, missingVersions))
        {
            Provider = provider;
            AppliedMaximum = appliedMaximum;
            MissingVersions = missingVersions ?? throw new ArgumentNullException(nameof(missingVersions));
        }

        #endregion

        #region Private-Methods

        private static string BuildMessage(DatabaseTypeEnum provider, int appliedMaximum, IReadOnlyList<int> missingVersions)
        {
            if (missingVersions == null) throw new ArgumentNullException(nameof(missingVersions));
            List<string> names = new List<string>();
            foreach (int version in missingVersions) names.Add("v" + version);
            return "skipped_migration_versions: " + provider + " schema_migrations records v" + appliedMaximum
                + " but not known migration(s) " + String.Join(", ", names)
                + ". Startup is refused because a migration numbered below an applied version would never run. "
                + "Renumber the unapplied migration above v" + appliedMaximum + " before it ships, "
                + "or restore the database from a backup taken before the higher version was applied.";
        }

        #endregion
    }
}
