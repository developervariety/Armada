namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;

    /// <summary>
    /// The one rule every provider driver uses to read its applied schema version.
    /// A driver applies only migrations above the recorded maximum, so a known migration
    /// below that maximum with no ledger row would never run. Such a ledger refuses startup
    /// with <see cref="SkippedMigrationVersionsException"/> instead of being read as current.
    /// Versions absent from the code's migration list (retired numbers) and ledger versions
    /// the code does not know are not gaps.
    /// </summary>
    internal static class AppliedMigrationLedger
    {
        #region Internal-Methods

        /// <summary>
        /// Read schema_migrations and return the highest applied version, or zero for an empty ledger.
        /// </summary>
        /// <param name="connection">Open connection holding the schema initialization lock where the provider has one.</param>
        /// <param name="migrations">The provider's declared migrations.</param>
        /// <param name="provider">Provider named in a refusal.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Highest applied version.</returns>
        /// <exception cref="SkippedMigrationVersionsException">A known version below the maximum is unapplied.</exception>
        internal static async Task<int> ReadCurrentVersionAsync(DbConnection connection, IReadOnlyList<SchemaMigration> migrations, DatabaseTypeEnum provider, CancellationToken token)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (migrations == null) throw new ArgumentNullException(nameof(migrations));

            List<int> applied = new List<int>();
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT version FROM schema_migrations;";
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                        applied.Add(Convert.ToInt32(reader.GetValue(0)));
                }
            }

            int maximum = 0;
            foreach (int version in applied) if (version > maximum) maximum = version;

            List<int> missing = FindSkippedVersions(applied, migrations);
            if (missing.Count > 0) throw new SkippedMigrationVersionsException(provider, maximum, missing);
            return maximum;
        }

        /// <summary>
        /// Known migration versions below the highest applied version that have no ledger row, ascending.
        /// </summary>
        /// <param name="appliedVersions">Versions recorded in schema_migrations.</param>
        /// <param name="migrations">The provider's declared migrations.</param>
        /// <returns>Skipped versions; empty when the ledger is contiguous over the known list.</returns>
        internal static List<int> FindSkippedVersions(IEnumerable<int> appliedVersions, IEnumerable<SchemaMigration> migrations)
        {
            if (appliedVersions == null) throw new ArgumentNullException(nameof(appliedVersions));
            if (migrations == null) throw new ArgumentNullException(nameof(migrations));

            HashSet<int> applied = new HashSet<int>(appliedVersions);
            int maximum = 0;
            foreach (int version in applied) if (version > maximum) maximum = version;

            SortedSet<int> missing = new SortedSet<int>();
            foreach (SchemaMigration migration in migrations)
            {
                if (migration.Version < maximum && !applied.Contains(migration.Version))
                    missing.Add(migration.Version);
            }
            return new List<int>(missing);
        }

        #endregion
    }
}
