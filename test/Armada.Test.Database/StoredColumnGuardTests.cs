namespace Armada.Test.Database
{
    using System;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Settings;

    /// <summary>
    /// Startup refuses a schema whose timestamp column, in a table the provider's schema statements create, is stored
    /// in a form the provider's binder does not write, and names the column; once the column matches again, startup
    /// proceeds. A table the schema statements do not create is ignored whatever its name and columns.
    /// </summary>
    internal sealed class StoredColumnGuardTests
    {
        private const string _Column = "stored_form_probe_utc";
        private readonly DatabaseSettings _Settings;

        internal StoredColumnGuardTests(DatabaseSettings settings)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        internal async Task VerifyAsync(CancellationToken token)
        {
            string driftedType = DriftedType();
            await ExecuteAsync("ALTER TABLE tenants ADD " + (_Settings.Type == DatabaseTypeEnum.SqlServer ? "" : "COLUMN ") + _Column + " " + driftedType + " NULL;", token).ConfigureAwait(false);
            string? refusal = null;
            try
            {
                using (DatabaseDriver drifted = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false)) { }
            }
            catch (InvalidOperationException ex)
            {
                refusal = ex.Message;
            }
            finally
            {
                await ExecuteAsync("ALTER TABLE tenants DROP COLUMN " + _Column + ";", token).ConfigureAwait(false);
            }

            DatabaseAssert.True(refusal != null, "Startup refuses a timestamp column stored in a form the binder does not write");
            DatabaseAssert.True(refusal!.Contains("Incompatible schema prerequisite", StringComparison.Ordinal) && refusal.Contains("tenants." + _Column, StringComparison.Ordinal),
                "The refusal is a schema prerequisite refusal naming the column: " + refusal);
            using (DatabaseDriver restored = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false)) { }
        }

        /// <summary>
        /// A table no schema statement creates, holding a timestamp column in a form the binder does not write, does
        /// not block startup.
        /// </summary>
        internal async Task VerifyUnknownTableIgnoredAsync(CancellationToken token)
        {
            string table = "captains_bak_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            await ExecuteAsync("CREATE TABLE " + table + " (id VARCHAR(64) NOT NULL, quarantine_until_utc " + DriftedType() + " NULL);", token).ConfigureAwait(false);
            try
            {
                using (DatabaseDriver reopened = await DatabaseDriverFactory.CreateAndInitializeAsync(_Settings, token).ConfigureAwait(false)) { }
            }
            finally
            {
                await ExecuteAsync("DROP TABLE " + table + ";", token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// A column type that does not hold each provider's default timestamp form.
        /// </summary>
        private string DriftedType()
        {
            return _Settings.Type switch
            {
                DatabaseTypeEnum.Sqlite => "DATETIME",
                DatabaseTypeEnum.Postgresql => "TEXT",
                DatabaseTypeEnum.Mysql => "TEXT",
                DatabaseTypeEnum.SqlServer => "DATETIME2",
                _ => throw new NotSupportedException()
            };
        }

        private async Task ExecuteAsync(string sql, CancellationToken token)
        {
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }
    }
}
