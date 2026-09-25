namespace Armada.Core.Database.Mysql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using MySqlConnector;
    using Armada.Core.Database;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// MySQL implementation of pipeline and pipeline stage database operations.
    /// </summary>
    public class PipelineMethods : IPipelineMethods
    {
        #region Private-Members

        private string _ConnectionString;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with a MySQL connection string.
        /// </summary>
        /// <param name="connectionString">MySQL connection string.</param>
        public PipelineMethods(string connectionString)
        {
            _ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Create a pipeline with its stages.
        /// </summary>
        /// <param name="pipeline">Pipeline to create.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Created pipeline.</returns>
        public async Task<Pipeline> CreateAsync(Pipeline pipeline, CancellationToken token = default)
        {
            if (pipeline == null) throw new ArgumentNullException(nameof(pipeline));
            pipeline.LastUpdateUtc = DateTime.UtcNow;

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                // The pipeline row and its stages change together or not at all.
                using (MySqlTransaction tx = await conn.BeginTransactionAsync(token).ConfigureAwait(false))
                {
                    using (MySqlCommand cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = @"INSERT INTO pipelines (id, tenant_id, user_id, ownership_scope, name, description, is_built_in, active, created_utc, last_update_utc)
                            VALUES (@id, @tenant_id, @user_id, @ownership_scope, @name, @description, @is_built_in, @active, @created_utc, @last_update_utc);";
                        PipelineColumns.Write(MysqlDatabaseDriver.StoredBinder.For(cmd, "pipelines"), pipeline);
                        await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }

                    for (int position = 0; position < pipeline.Stages.Count; position++)
                    {
                        PipelineStage stage = pipeline.Stages[position];
                        stage.PipelineId = pipeline.Id;
                        await InsertStageAsync(conn, tx, stage, position, token).ConfigureAwait(false);
                    }

                    await tx.CommitAsync(token).ConfigureAwait(false);
                }
            }

            return pipeline;
        }

        /// <summary>
        /// Read a pipeline by identifier, including its stages.
        /// </summary>
        /// <param name="id">Pipeline identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Pipeline or null if not found.</returns>
        public async Task<Pipeline?> ReadAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                Pipeline? pipeline = null;

                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM pipelines WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            pipeline = PipelineColumns.Read(reader, MysqlDatabaseDriver.StoredValues);
                    }
                }

                if (pipeline != null)
                    pipeline.Stages = await LoadStagesAsync(conn, pipeline.Id, token).ConfigureAwait(false);

                return pipeline;
            }
        }

        /// <summary>
        /// Read a pipeline by name, including its stages.
        /// </summary>
        /// <param name="name">Pipeline name.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Pipeline or null if not found.</returns>
        public async Task<Pipeline?> ReadByNameAsync(string name, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                Pipeline? pipeline = null;

                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM pipelines WHERE name = @name;";
                    StoredValueBinder.Value(cmd, "@name", name);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            pipeline = PipelineColumns.Read(reader, MysqlDatabaseDriver.StoredValues);
                    }
                }

                if (pipeline != null)
                    pipeline.Stages = await LoadStagesAsync(conn, pipeline.Id, token).ConfigureAwait(false);

                return pipeline;
            }
        }

        /// <summary>
        /// Read a pipeline by tenant and name, including its stages.
        /// </summary>
        /// <param name="tenantId">Tenant identifier.</param>
        /// <param name="name">Pipeline name.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Pipeline or null if not found.</returns>
        public async Task<Pipeline?> ReadByNameAsync(string tenantId, string name, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                Pipeline? pipeline = null;

                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM pipelines WHERE tenant_id = @tenant_id AND name = @name;";
                    StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                    StoredValueBinder.Value(cmd, "@name", name);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            pipeline = PipelineColumns.Read(reader, MysqlDatabaseDriver.StoredValues);
                    }
                }

                if (pipeline != null)
                    pipeline.Stages = await LoadStagesAsync(conn, pipeline.Id, token).ConfigureAwait(false);

                return pipeline;
            }
        }

        /// <summary>
        /// Update a pipeline and its stages.
        /// </summary>
        /// <param name="pipeline">Pipeline with updated values.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Updated pipeline.</returns>
        public async Task<Pipeline> UpdateAsync(Pipeline pipeline, CancellationToken token = default)
        {
            if (pipeline == null) throw new ArgumentNullException(nameof(pipeline));
            pipeline.LastUpdateUtc = DateTime.UtcNow;

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                // The pipeline row and its stages change together or not at all.
                using (MySqlTransaction tx = await conn.BeginTransactionAsync(token).ConfigureAwait(false))
                {
                    using (MySqlCommand cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = @"UPDATE pipelines SET
                            tenant_id = @tenant_id,
                            user_id = @user_id,
                            ownership_scope = @ownership_scope,
                            name = @name,
                            description = @description,
                            is_built_in = @is_built_in,
                            active = @active,
                            last_update_utc = @last_update_utc
                            WHERE id = @id;";
                        PipelineColumns.Write(MysqlDatabaseDriver.StoredBinder.For(cmd, "pipelines"), pipeline);
                        await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }

                    // Delete existing stages and reinsert
                    using (MySqlCommand cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "DELETE FROM pipeline_stages WHERE pipeline_id = @pipeline_id;";
                        StoredValueBinder.Value(cmd, "@pipeline_id", pipeline.Id);
                        await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }

                    for (int position = 0; position < pipeline.Stages.Count; position++)
                    {
                        PipelineStage stage = pipeline.Stages[position];
                        stage.PipelineId = pipeline.Id;
                        await InsertStageAsync(conn, tx, stage, position, token).ConfigureAwait(false);
                    }

                    await tx.CommitAsync(token).ConfigureAwait(false);
                }
            }

            return pipeline;
        }

        /// <summary>
        /// Delete a pipeline and its stages by identifier.
        /// </summary>
        /// <param name="id">Pipeline identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Task.</returns>
        public async Task DeleteAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                // The pipeline row and its stages change together or not at all.
                using (MySqlTransaction tx = await conn.BeginTransactionAsync(token).ConfigureAwait(false))
                {
                    // Delete stages first
                    using (MySqlCommand cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "DELETE FROM pipeline_stages WHERE pipeline_id = @pipeline_id;";
                        StoredValueBinder.Value(cmd, "@pipeline_id", id);
                        await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }

                    // Delete pipeline
                    using (MySqlCommand cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "DELETE FROM pipelines WHERE id = @id;";
                        StoredValueBinder.Value(cmd, "@id", id);
                        await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }

                    await tx.CommitAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Enumerate all pipelines, including their stages.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>List of all pipelines.</returns>
        public async Task<List<Pipeline>> EnumerateAsync(CancellationToken token = default)
        {
            List<Pipeline> results = new List<Pipeline>();

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM pipelines ORDER BY name;";
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(PipelineColumns.Read(reader, MysqlDatabaseDriver.StoredValues));
                    }
                }

                foreach (Pipeline pipeline in results)
                {
                    pipeline.Stages = await LoadStagesAsync(conn, pipeline.Id, token).ConfigureAwait(false);
                }
            }

            return results;
        }

        /// <summary>
        /// Enumerate pipelines with pagination and filtering, including their stages.
        /// </summary>
        /// <param name="query">Enumeration query parameters.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Paginated enumeration result.</returns>
        public async Task<EnumerationResult<Pipeline>> EnumerateAsync(EnumerationQuery query, CancellationToken token = default)
        {
            if (query == null) query = new EnumerationQuery();

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string>();
                List<MySqlParameter> parameters = new List<MySqlParameter>();

                if (query.CreatedAfter.HasValue)
                {
                    conditions.Add("created_utc > @created_after");
                    parameters.Add(MysqlDatabaseDriver.StoredBinder.Timestamp(new MySqlParameter(), "@created_after", "pipelines", "created_utc", query.CreatedAfter.Value));
                }
                if (query.CreatedBefore.HasValue)
                {
                    conditions.Add("created_utc < @created_before");
                    parameters.Add(MysqlDatabaseDriver.StoredBinder.Timestamp(new MySqlParameter(), "@created_before", "pipelines", "created_utc", query.CreatedBefore.Value));
                }

                string whereClause = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : "";
                string orderDirection = query.Order == EnumerationOrderEnum.CreatedAscending ? "ASC" : "DESC";

                // Count
                long totalCount = 0;
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM pipelines" + whereClause + ";";
                    foreach (MySqlParameter p in parameters) cmd.Parameters.Add(new MySqlParameter(p.ParameterName, p.Value));
                    totalCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                }

                // Query
                List<Pipeline> results = new List<Pipeline>();
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM pipelines" + whereClause +
                        " ORDER BY created_utc " + orderDirection +
                        " LIMIT " + query.PageSize + " OFFSET " + query.Offset + ";";
                    foreach (MySqlParameter p in parameters) cmd.Parameters.Add(new MySqlParameter(p.ParameterName, p.Value));
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(PipelineColumns.Read(reader, MysqlDatabaseDriver.StoredValues));
                    }
                }

                // Load stages for each pipeline
                foreach (Pipeline pipeline in results)
                {
                    pipeline.Stages = await LoadStagesAsync(conn, pipeline.Id, token).ConfigureAwait(false);
                }

                return EnumerationResult<Pipeline>.Create(query, results, totalCount);
            }
        }

        /// <summary>
        /// Check if a pipeline exists by identifier.
        /// </summary>
        /// <param name="id">Pipeline identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the pipeline exists.</returns>
        public async Task<bool> ExistsAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM pipelines WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    long count = Convert.ToInt64(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                    return count > 0;
                }
            }
        }

        /// <summary>
        /// Check if a pipeline exists by name.
        /// </summary>
        /// <param name="name">Pipeline name.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the pipeline exists.</returns>
        public async Task<bool> ExistsByNameAsync(string name, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM pipelines WHERE name = @name;";
                    StoredValueBinder.Value(cmd, "@name", name);
                    long count = Convert.ToInt64(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                    return count > 0;
                }
            }
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Insert a single pipeline stage row.
        /// </summary>
        /// <param name="conn">Open MySQL connection.</param>
        /// <param name="tx">Transaction the write belongs to.</param>
        /// <param name="stage">Pipeline stage to insert.</param>
        /// <param name="position">Index of the stage in the submitted list; it keeps same-order siblings in that order.</param>
        /// <param name="token">Cancellation token.</param>
        private static async Task InsertStageAsync(MySqlConnection conn, MySqlTransaction tx, PipelineStage stage, int position, CancellationToken token)
        {
            using (MySqlCommand cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO pipeline_stages (id, pipeline_id, stage_order, stage_position, persona_name, is_optional, description, preferred_model, requires_review, review_deny_action)
                        VALUES (@id, @pipeline_id, @stage_order, @stage_position, @persona_name, @is_optional, @description, @preferred_model, @requires_review, @review_deny_action);";
                PipelineColumns.WriteStage(MysqlDatabaseDriver.StoredBinder.For(cmd, "pipeline_stages"), stage);
                StoredValueBinder.Value(cmd, "@stage_position", position);
                await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Load all stages for a pipeline, ordered by stage_order and, within one order, by submitted position.
        /// </summary>
        /// <param name="conn">Open MySQL connection.</param>
        /// <param name="pipelineId">Pipeline identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>List of pipeline stages in stored order.</returns>
        private static async Task<List<PipelineStage>> LoadStagesAsync(MySqlConnection conn, string pipelineId, CancellationToken token)
        {
            List<PipelineStage> stages = new List<PipelineStage>();

            using (MySqlCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT * FROM pipeline_stages WHERE pipeline_id = @pipeline_id ORDER BY stage_order, stage_position, id;";
                StoredValueBinder.Value(cmd, "@pipeline_id", pipelineId);
                using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                        stages.Add(PipelineColumns.ReadStage(reader, MysqlDatabaseDriver.StoredValues));
                }
            }

            return stages;
        }

        private static DateTime ToDatabaseTimestamp(DateTime dt)
        {
            return MysqlDatabaseDriver.ToDatabaseTimestamp(dt);
        }

        #endregion
    }
}
