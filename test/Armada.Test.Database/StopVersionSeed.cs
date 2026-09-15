namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Globalization;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// Writes fixture rows while a migration scenario holds the schema below the newest version. Driver create
    /// methods write the newest row shape, which names columns that later migrations add, so every row here is
    /// written with SQL that names only the columns the caller lists. A caller lists only columns that exist at
    /// the version where its scenario stops.
    /// </summary>
    internal sealed class StopVersionSeed
    {
        private readonly DatabaseSettings _Settings;

        /// <summary>
        /// Instantiate for one scenario database.
        /// </summary>
        /// <param name="settings">Settings of the scenario database.</param>
        internal StopVersionSeed(DatabaseSettings settings)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// A boolean flag in the storage form of the selected provider.
        /// </summary>
        /// <param name="value">Flag value.</param>
        /// <returns>A boolean for PostgreSQL, otherwise an integer.</returns>
        internal object Flag(bool value)
        {
            // PostgreSQL stores these flags as booleans; the other providers as integers.
            return _Settings.Type == DatabaseTypeEnum.Postgresql ? (object)value : (value ? 1 : 0);
        }

        /// <summary>
        /// A fixed historical timestamp in the storage form of the selected provider.
        /// </summary>
        /// <returns>ISO 8601 text for SQLite and SQL Server, otherwise a UTC timestamp.</returns>
        internal object Timestamp()
        {
            DateTime timestamp = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
            // SQLite and SQL Server store these historical columns as text; the other providers as a timestamp.
            return _Settings.Type == DatabaseTypeEnum.Sqlite || _Settings.Type == DatabaseTypeEnum.SqlServer
                ? timestamp.ToString("o", CultureInfo.InvariantCulture)
                : timestamp;
        }

        /// <summary>
        /// A new column map that already holds the created and last-update timestamps.
        /// </summary>
        /// <returns>Column map for <see cref="InsertAsync"/>.</returns>
        internal Dictionary<string, object?> TimestampedRow()
        {
            Dictionary<string, object?> row = new Dictionary<string, object?>();
            row["created_utc"] = Timestamp();
            row["last_update_utc"] = Timestamp();
            return row;
        }

        /// <summary>
        /// Insert one row that names exactly the listed columns.
        /// </summary>
        /// <param name="table">Table name.</param>
        /// <param name="columns">Column names and values; a null value is written as NULL.</param>
        /// <param name="token">Cancellation token.</param>
        internal async Task InsertAsync(string table, Dictionary<string, object?> columns, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(table)) throw new ArgumentNullException(nameof(table));
            if (columns == null || columns.Count == 0) throw new ArgumentException("An insert names at least one column", nameof(columns));

            List<string> names = columns.Keys.ToList();
            string sql = "INSERT INTO " + table + " (" + String.Join(", ", names) + ") VALUES ("
                + String.Join(", ", names.Select(name => "@" + name)) + ");";
            Dictionary<string, object?> parameters = new Dictionary<string, object?>();
            foreach (string name in names) parameters["@" + name] = columns[name];
            await ExecuteAsync(sql, parameters, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Run one fixture statement that must change exactly one row.
        /// </summary>
        /// <param name="sql">Statement text.</param>
        /// <param name="parameters">Parameter names and values; a null value is written as NULL.</param>
        /// <param name="token">Cancellation token.</param>
        internal async Task ExecuteAsync(string sql, Dictionary<string, object?> parameters, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(sql)) throw new ArgumentNullException(nameof(sql));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));

            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    foreach (KeyValuePair<string, object?> entry in parameters)
                    {
                        DbParameter parameter = command.CreateParameter();
                        parameter.ParameterName = entry.Key;
                        parameter.Value = entry.Value ?? DBNull.Value;
                        command.Parameters.Add(parameter);
                    }
                    int changed = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    DatabaseAssert.True(changed == 1, "Fixture statement changes exactly one row: " + sql);
                }
            }
        }

        /// <summary>
        /// Insert an operator pipeline and its ordered stages in the default tenant.
        /// </summary>
        /// <param name="name">Pipeline name.</param>
        /// <param name="personas">Stage persona names in stage order.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The pipeline id.</returns>
        internal async Task<string> CreatePipelineAsync(string name, string[] personas, CancellationToken token)
        {
            if (personas == null) throw new ArgumentNullException(nameof(personas));

            string id = new Pipeline(name).Id;
            Dictionary<string, object?> pipeline = TimestampedRow();
            pipeline["id"] = id;
            pipeline["tenant_id"] = Constants.DefaultTenantId;
            pipeline["name"] = name;
            pipeline["is_built_in"] = Flag(false);
            pipeline["active"] = Flag(true);
            await InsertAsync("pipelines", pipeline, token).ConfigureAwait(false);

            for (int index = 0; index < personas.Length; index++)
            {
                Dictionary<string, object?> stage = new Dictionary<string, object?>();
                stage["id"] = new PipelineStage(index + 1, personas[index]).Id;
                stage["pipeline_id"] = id;
                stage["stage_order"] = index + 1;
                stage["persona_name"] = personas[index];
                stage["is_optional"] = Flag(false);
                await InsertAsync("pipeline_stages", stage, token).ConfigureAwait(false);
            }
            return id;
        }

        /// <summary>
        /// Insert a prompt template in the default tenant.
        /// </summary>
        /// <param name="name">Template name.</param>
        /// <param name="category">Template category.</param>
        /// <param name="builtIn">True for a built-in template.</param>
        /// <param name="token">Cancellation token.</param>
        internal async Task CreateTemplateAsync(string name, string category, bool builtIn, CancellationToken token)
        {
            string content = "template body for " + name;
            Dictionary<string, object?> template = TimestampedRow();
            template["id"] = new PromptTemplate(name, content).Id;
            template["tenant_id"] = Constants.DefaultTenantId;
            template["name"] = name;
            template["category"] = category;
            template["content"] = content;
            template["is_built_in"] = Flag(builtIn);
            template["active"] = Flag(true);
            await InsertAsync("prompt_templates", template, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Insert a persona in the default tenant.
        /// </summary>
        /// <param name="name">Persona name.</param>
        /// <param name="templateName">Prompt template name.</param>
        /// <param name="builtIn">True for a built-in persona.</param>
        /// <param name="defaultPlaybooks">Default playbook selections as JSON, or null to leave the column unnamed.</param>
        /// <param name="token">Cancellation token.</param>
        internal async Task CreatePersonaAsync(string name, string templateName, bool builtIn, string? defaultPlaybooks, CancellationToken token)
        {
            Dictionary<string, object?> persona = TimestampedRow();
            persona["id"] = new Persona(name, templateName).Id;
            persona["tenant_id"] = Constants.DefaultTenantId;
            persona["name"] = name;
            persona["prompt_template_name"] = templateName;
            persona["is_built_in"] = Flag(builtIn);
            if (defaultPlaybooks != null) persona["default_playbooks"] = defaultPlaybooks;
            persona["active"] = Flag(true);
            await InsertAsync("personas", persona, token).ConfigureAwait(false);
        }
    }
}
