namespace Armada.Test.Unit.Suites.Database
{
    using System.Data.Common;
    using System.Linq;
    using System.Text.RegularExpressions;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Test.Common;
    using Microsoft.Data.SqlClient;
    using Microsoft.Data.Sqlite;
    using MySqlConnector;
    using Npgsql;

    /// <summary>
    /// The shared method sets write the same scope conditions, in the same order and with the same parameter names,
    /// as each provider's own method set wrote before them. The expected text below is the text those method sets
    /// generated for a query with every filter set; a change here changes which rows a scoped read can see.
    /// </summary>
    public class StoredScopeSqlTests : TestSuite
    {
        public override string Name => "Stored Scope SQL";

        private static readonly DatabaseTypeEnum[] _Providers =
        {
            DatabaseTypeEnum.Sqlite, DatabaseTypeEnum.Postgresql, DatabaseTypeEnum.Mysql, DatabaseTypeEnum.SqlServer
        };

        protected override async Task RunTestsAsync()
        {
            await RunTest("CheckRun scope conditions keep their text, order and parameters on every provider", () =>
            {
                const string expected = "tenant_id = @tenant_id AND user_id = @user_id AND workflow_profile_id = @workflow_profile_id AND vessel_id = @vessel_id"
                    + " AND mission_id = @mission_id AND voyage_id = @voyage_id AND deployment_id = @deployment_id AND check_type = @check_type AND status = @status"
                    + " AND source = @source AND provider_name = @provider_name AND external_id = @external_id AND environment_name = @environment_name"
                    + " AND created_utc >= @from_utc AND created_utc <= @to_utc";
                CheckRunQuery everyFilter = new CheckRunQuery
                {
                    TenantId = "ten_x", UserId = "usr_x", WorkflowProfileId = "wfp_x", VesselId = "vsl_x", MissionId = "msn_x", VoyageId = "vyg_x",
                    DeploymentId = "dpl_x", Type = CheckRunTypeEnum.Build, Status = CheckRunStatusEnum.Passed, Source = CheckRunSourceEnum.Armada,
                    ProviderName = "provider", ExternalId = "external", EnvironmentName = "staging",
                    FromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), ToUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
                };
                AssertScope(p => expected, p => CheckRunMethods.Scope(CheckRunMethods.Table.Filter(), everyFilter), p => CheckRunMethods.Scope(CheckRunMethods.Table.Filter(), new CheckRunQuery()));
                AssertEqual("created_utc DESC, id DESC", CheckRunMethods.Order);
            });

            await RunTest("CheckRun insert and update name every column the writer binds", () =>
            {
                AssertTableMatchesWriter(CheckRunMethods.Table, new CheckRun());
            });

            await RunTest("Deployment scope conditions keep their text, order and parameters on every provider", () =>
            {
                string expected = "tenant_id = @tenant_id AND user_id = @user_id AND vessel_id = @vessel_id AND workflow_profile_id = @workflow_profile_id AND environment_id = @environment_id AND environment_name = @environment_name AND release_id = @release_id AND mission_id = @mission_id AND voyage_id = @voyage_id AND check_run_ids_json LIKE @check_run_like AND status = @status AND verification_status = @verification_status AND (LOWER(title) LIKE @search OR LOWER(COALESCE(source_ref, '')) LIKE @search OR LOWER(COALESCE(summary, '')) LIKE @search OR LOWER(COALESCE(notes, '')) LIKE @search OR LOWER(COALESCE(environment_name, '')) LIKE @search) AND created_utc >= @from_utc AND created_utc <= @to_utc";
                DeploymentQuery everyFilter = new DeploymentQuery
                {
                    TenantId = "ten_x", UserId = "usr_x", VesselId = "vsl_x", WorkflowProfileId = "wfp_x", EnvironmentId = "env_x", EnvironmentName = "staging",
                    ReleaseId = "rel_x", MissionId = "msn_x", VoyageId = "vyg_x", CheckRunId = "chk_x", Status = DeploymentStatusEnum.Succeeded,
                    VerificationStatus = DeploymentVerificationStatusEnum.Passed, Search = "Find", FromUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), ToUtc = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
                };
                AssertScope(p => expected, p => DeploymentMethods.Scope(DeploymentMethods.Table.Filter(), everyFilter, p), p => DeploymentMethods.Scope(DeploymentMethods.Table.Filter(), new DeploymentQuery(), p));
                AssertEqual("COALESCE(completed_utc, started_utc, last_update_utc) DESC, created_utc DESC", DeploymentMethods.Order);
                AssertTableMatchesWriter(DeploymentMethods.Table, new Deployment());
            });

            await RunTest("DeploymentEnvironment scope conditions keep their text, order and parameters on every provider", () =>
            {
                string expected = "tenant_id = @tenant_id AND user_id = @user_id AND vessel_id = @vessel_id AND kind = @kind AND is_default = @is_default_filter AND active = @active_filter AND (LOWER(name) LIKE @search OR LOWER(COALESCE(description, '')) LIKE @search OR LOWER(COALESCE(configuration_source, '')) LIKE @search OR LOWER(COALESCE(base_url, '')) LIKE @search OR LOWER(COALESCE(health_endpoint, '')) LIKE @search)";
                DeploymentEnvironmentQuery everyFilter = new DeploymentEnvironmentQuery
                {
                    TenantId = "ten_x", UserId = "usr_x", VesselId = "vsl_x", Kind = EnvironmentKindEnum.Staging, IsDefault = true, Active = false, Search = "Find"
                };
                AssertScope(p => expected, p => DeploymentEnvironmentMethods.Scope(DeploymentEnvironmentMethods.Table.Filter(), everyFilter, p), p => DeploymentEnvironmentMethods.Scope(DeploymentEnvironmentMethods.Table.Filter(), new DeploymentEnvironmentQuery(), p));
                AssertEqual("is_default DESC, active DESC, name ASC, created_utc DESC", DeploymentEnvironmentMethods.Order);
                AssertTableMatchesWriter(DeploymentEnvironmentMethods.Table, new DeploymentEnvironment());
            });

            await RunTest("First-row and paged statements keep each provider's syntax", () =>
            {
                foreach (DatabaseTypeEnum provider in _Providers)
                {
                    StoredDialect dialect = Dialect(provider);
                    bool sqlServer = provider == DatabaseTypeEnum.SqlServer;
                    AssertEqual(sqlServer ? "SELECT TOP 1 * FROM t WHERE id = @id;" : "SELECT * FROM t WHERE id = @id LIMIT 1;", dialect.SelectFirst("t", "id = @id"), provider + " first row");
                    AssertEqual(sqlServer
                        ? " ORDER BY created_utc DESC OFFSET @offset ROWS FETCH NEXT @page_size ROWS ONLY;"
                        : " ORDER BY created_utc DESC LIMIT @page_size OFFSET @offset;", dialect.OrderAndPage("created_utc DESC"), provider + " page");
                }
            });
        }

        /// <summary>
        /// On every provider the full query writes exactly the expected conditions and binds exactly their parameters,
        /// and the empty query writes no WHERE clause at all.
        /// </summary>
        private void AssertScope(Func<DatabaseTypeEnum, string> expectedFor, Func<DatabaseTypeEnum, StoredFilter> everyFilter, Func<DatabaseTypeEnum, StoredFilter> noFilter)
        {
            foreach (DatabaseTypeEnum provider in _Providers)
            {
                string expected = expectedFor(provider);
                List<string> expectedParameters = Regex.Matches(expected, "@[a-z_]+").Select(m => m.Value).Distinct().ToList();
                StoredFilter filter = everyFilter(provider);
                AssertEqual(expected, filter.Conjunction, provider + " conditions");
                using (DbCommand command = Command(provider))
                {
                    filter.Bind(command, StoredValueBinder.For(provider));
                    List<string> bound = command.Parameters.Cast<DbParameter>().Select(p => p.ParameterName).ToList();
                    AssertEqual(String.Join(", ", expectedParameters), String.Join(", ", bound), provider + " parameters");
                }

                AssertEqual(String.Empty, noFilter(provider).Where, provider + " empty query");
            }
        }

        /// <summary>
        /// The insert names every column the writer binds and nothing else, so no column is dropped or left unbound.
        /// </summary>
        private void AssertTableMatchesWriter<TModel>(StoredTable<TModel> table, TModel model) where TModel : class
        {
            foreach (DatabaseTypeEnum provider in _Providers)
            {
                using (DbCommand command = Command(provider))
                {
                    table.Write(StoredValueBinder.For(provider).For(command, table.Name), model);
                    List<string> bound = command.Parameters.Cast<DbParameter>().Select(p => p.ParameterName.TrimStart('@')).OrderBy(n => n, StringComparer.Ordinal).ToList();
                    List<string> columns = table.Columns.OrderBy(n => n, StringComparer.Ordinal).ToList();
                    AssertEqual(String.Join(", ", columns), String.Join(", ", bound), provider + " " + table.Name + " columns");
                }

                foreach (string column in table.Columns)
                {
                    AssertContains("@" + column, table.InsertSql, table.Name + " insert");
                }
            }
        }

        private static DbCommand Command(DatabaseTypeEnum provider)
        {
            return provider switch
            {
                DatabaseTypeEnum.Sqlite => new SqliteCommand(),
                DatabaseTypeEnum.Postgresql => new NpgsqlCommand(),
                DatabaseTypeEnum.Mysql => new MySqlCommand(),
                _ => new SqlCommand()
            };
        }

        private static StoredDialect Dialect(DatabaseTypeEnum provider)
        {
            StoredValueConverter values = provider switch
            {
                DatabaseTypeEnum.Sqlite => Armada.Core.Database.Sqlite.SqliteDatabaseDriver.StoredValues,
                DatabaseTypeEnum.Postgresql => Armada.Core.Database.Postgresql.PostgresqlDatabaseDriver.StoredValues,
                DatabaseTypeEnum.Mysql => Armada.Core.Database.Mysql.MysqlDatabaseDriver.StoredValues,
                _ => Armada.Core.Database.SqlServer.SqlServerDatabaseDriver.StoredValues
            };
            return new StoredDialect(provider, () => throw new InvalidOperationException("No connection in a text test."), values);
        }
    }
}
