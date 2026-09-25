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
    /// Startup refuses a schema whose timestamp column is stored in a form the provider's binder does not write, and
    /// names the column; once the column matches again, startup proceeds.
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
            // Each provider's default timestamp form for tenants, and a column type that does not hold it.
            string driftedType = _Settings.Type switch
            {
                DatabaseTypeEnum.Sqlite => "DATETIME",
                DatabaseTypeEnum.Postgresql => "TEXT",
                DatabaseTypeEnum.Mysql => "TEXT",
                DatabaseTypeEnum.SqlServer => "DATETIME2",
                _ => throw new NotSupportedException()
            };
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
