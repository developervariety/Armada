namespace Armada.Core.Database.Postgresql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Npgsql;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// PostgreSQL implementation of pipeline and pipeline stage database operations.
    /// </summary>
    public class PipelineMethods : IPipelineMethods
    {
        #region Private-Members

#pragma warning disable CS0414
        private string _Header = "[PipelineMethods] ";
#pragma warning restore CS0414
        private PostgresqlDatabaseDriver _Driver;
        private DatabaseSettings _Settings;
        private LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="driver">PostgreSQL database driver.</param>
        /// <param name="settings">Database settings.</param>
        /// <param name="logging">Logging module.</param>
        public PipelineMethods(PostgresqlDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<Pipeline> CreateAsync(Pipeline pipeline, CancellationToken token = default)
        {
            if (pipeline == null) throw new ArgumentNullException(nameof(pipeline));
            pipeline.LastUpdateUtc = DateTime.UtcNow;

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                // The pipeline row and its stages change together or not at all.
                using (NpgsqlTransaction tx = await conn.BeginTransactionAsync(token).ConfigureAwait(false))
                {
                    using (NpgsqlCommand cmd = new NpgsqlCommand())
                    {
                        cmd.Connection = conn;
                        cmd.Transaction = tx;
                        cmd.CommandText = @"INSERT INTO pipelines (id, tenant_id, user_id, ownership_scope, name, description, is_built_in, active, created_utc, last_update_utc)
                            VALUES (@id, @tenant_id, @user_id, @ownership_scope, @name, @description, @is_built_in, @active, @created_utc, @last_update_utc);";
                        cmd.Parameters.AddWithValue("@id", pipeline.Id);
                        cmd.Parameters.AddWithValue("@tenant_id", (object?)pipeline.TenantId ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@user_id", (object?)pipeline.UserId ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@ownership_scope", pipeline.OwnershipScope.ToString());
                        cmd.Parameters.AddWithValue("@name", pipeline.Name);
                        cmd.Parameters.AddWithValue("@description", (object?)pipeline.Description ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@is_built_in", pipeline.IsBuiltIn);
                        cmd.Parameters.AddWithValue("@active", pipeline.Active);
                        cmd.Parameters.AddWithValue("@created_utc", pipeline.CreatedUtc);
                        cmd.Parameters.AddWithValue("@last_update_utc", pipeline.LastUpdateUtc);
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

        /// <inheritdoc />
        public async Task<Pipeline?> ReadAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                Pipeline? pipeline = null;

                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM pipelines WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            pipeline = PipelineColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues);
                    }
                }

                if (pipeline != null)
                    pipeline.Stages = await LoadStagesAsync(conn, pipeline.Id, token).ConfigureAwait(false);

                return pipeline;
            }
        }

        /// <inheritdoc />
        public async Task<Pipeline?> ReadByNameAsync(string name, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                Pipeline? pipeline = null;

                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM pipelines WHERE name = @name;";
                    cmd.Parameters.AddWithValue("@name", name);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            pipeline = PipelineColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues);
                    }
                }

                if (pipeline != null)
                    pipeline.Stages = await LoadStagesAsync(conn, pipeline.Id, token).ConfigureAwait(false);

                return pipeline;
            }
        }

        /// <inheritdoc />
        public async Task<Pipeline?> ReadByNameAsync(string tenantId, string name, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                Pipeline? pipeline = null;

                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM pipelines WHERE tenant_id = @tenant_id AND name = @name;";
                    cmd.Parameters.AddWithValue("@tenant_id", tenantId);
                    cmd.Parameters.AddWithValue("@name", name);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            pipeline = PipelineColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues);
                    }
                }

                if (pipeline != null)
                    pipeline.Stages = await LoadStagesAsync(conn, pipeline.Id, token).ConfigureAwait(false);

                return pipeline;
            }
        }

        /// <inheritdoc />
        public async Task<Pipeline> UpdateAsync(Pipeline pipeline, CancellationToken token = default)
        {
            if (pipeline == null) throw new ArgumentNullException(nameof(pipeline));
            pipeline.LastUpdateUtc = DateTime.UtcNow;

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                // The pipeline row and its stages change together or not at all.
                using (NpgsqlTransaction tx = await conn.BeginTransactionAsync(token).ConfigureAwait(false))
                {
                    using (NpgsqlCommand cmd = new NpgsqlCommand())
                    {
                        cmd.Connection = conn;
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
                        cmd.Parameters.AddWithValue("@id", pipeline.Id);
                        cmd.Parameters.AddWithValue("@tenant_id", (object?)pipeline.TenantId ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@user_id", (object?)pipeline.UserId ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@ownership_scope", pipeline.OwnershipScope.ToString());
                        cmd.Parameters.AddWithValue("@name", pipeline.Name);
                        cmd.Parameters.AddWithValue("@description", (object?)pipeline.Description ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@is_built_in", pipeline.IsBuiltIn);
                        cmd.Parameters.AddWithValue("@active", pipeline.Active);
                        cmd.Parameters.AddWithValue("@last_update_utc", pipeline.LastUpdateUtc);
                        await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }

                    // Delete existing stages and reinsert
                    using (NpgsqlCommand cmd = new NpgsqlCommand())
                    {
                        cmd.Connection = conn;
                        cmd.Transaction = tx;
                        cmd.CommandText = "DELETE FROM pipeline_stages WHERE pipeline_id = @pipeline_id;";
                        cmd.Parameters.AddWithValue("@pipeline_id", pipeline.Id);
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

        /// <inheritdoc />
        public async Task DeleteAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                // The pipeline row and its stages change together or not at all.
                using (NpgsqlTransaction tx = await conn.BeginTransactionAsync(token).ConfigureAwait(false))
                {
                    // Delete stages first
                    using (NpgsqlCommand cmd = new NpgsqlCommand())
                    {
                        cmd.Connection = conn;
                        cmd.Transaction = tx;
                        cmd.CommandText = "DELETE FROM pipeline_stages WHERE pipeline_id = @pipeline_id;";
                        cmd.Parameters.AddWithValue("@pipeline_id", id);
                        await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }

                    // Delete pipeline
                    using (NpgsqlCommand cmd = new NpgsqlCommand())
                    {
                        cmd.Connection = conn;
                        cmd.Transaction = tx;
                        cmd.CommandText = "DELETE FROM pipelines WHERE id = @id;";
                        cmd.Parameters.AddWithValue("@id", id);
                        await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }

                    await tx.CommitAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<List<Pipeline>> EnumerateAsync(CancellationToken token = default)
        {
            List<Pipeline> results = new List<Pipeline>();

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM pipelines ORDER BY name;";
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(PipelineColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
                    }
                }

                foreach (Pipeline pipeline in results)
                {
                    pipeline.Stages = await LoadStagesAsync(conn, pipeline.Id, token).ConfigureAwait(false);
                }
            }

            return results;
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<Pipeline>> EnumerateAsync(EnumerationQuery query, CancellationToken token = default)
        {
            if (query == null) query = new EnumerationQuery();

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string>();
                List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();

                if (query.CreatedAfter.HasValue)
                {
                    conditions.Add("created_utc > @created_after");
                    parameters.Add(new NpgsqlParameter("@created_after", query.CreatedAfter.Value));
                }
                if (query.CreatedBefore.HasValue)
                {
                    conditions.Add("created_utc < @created_before");
                    parameters.Add(new NpgsqlParameter("@created_before", query.CreatedBefore.Value));
                }

                string whereClause = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : "";
                string orderDirection = query.Order == EnumerationOrderEnum.CreatedAscending ? "ASC" : "DESC";

                // Count
                long totalCount = 0;
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT COUNT(*) FROM pipelines" + whereClause + ";";
                    foreach (NpgsqlParameter p in parameters) cmd.Parameters.Add(new NpgsqlParameter(p.ParameterName, p.Value));
                    totalCount = (long)(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false))!;
                }

                // Query
                List<Pipeline> results = new List<Pipeline>();
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM pipelines" + whereClause +
                        " ORDER BY created_utc " + orderDirection +
                        " LIMIT " + query.PageSize + " OFFSET " + query.Offset + ";";
                    foreach (NpgsqlParameter p in parameters) cmd.Parameters.Add(new NpgsqlParameter(p.ParameterName, p.Value));
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(PipelineColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
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

        /// <inheritdoc />
        public async Task<bool> ExistsAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT COUNT(*) FROM pipelines WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    long count = (long)(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false))!;
                    return count > 0;
                }
            }
        }

        /// <inheritdoc />
        public async Task<bool> ExistsByNameAsync(string name, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT COUNT(*) FROM pipelines WHERE name = @name;";
                    cmd.Parameters.AddWithValue("@name", name);
                    long count = (long)(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false))!;
                    return count > 0;
                }
            }
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Insert a single pipeline stage row.
        /// </summary>
        /// <param name="conn">Open PostgreSQL connection.</param>
        /// <param name="tx">Transaction the write belongs to.</param>
        /// <param name="stage">Pipeline stage to insert.</param>
        /// <param name="position">Index of the stage in the submitted list; it keeps same-order siblings in that order.</param>
        /// <param name="token">Cancellation token.</param>
        private static async Task InsertStageAsync(NpgsqlConnection conn, NpgsqlTransaction tx, PipelineStage stage, int position, CancellationToken token)
        {
            using (NpgsqlCommand cmd = new NpgsqlCommand())
            {
                cmd.Connection = conn;
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO pipeline_stages (id, pipeline_id, stage_order, stage_position, persona_name, is_optional, description, preferred_model, requires_review, review_deny_action)
                    VALUES (@id, @pipeline_id, @stage_order, @stage_position, @persona_name, @is_optional, @description, @preferred_model, @requires_review, @review_deny_action);";
                cmd.Parameters.AddWithValue("@id", stage.Id);
                cmd.Parameters.AddWithValue("@pipeline_id", (object?)stage.PipelineId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@stage_order", stage.Order);
                cmd.Parameters.AddWithValue("@stage_position", position);
                cmd.Parameters.AddWithValue("@persona_name", stage.PersonaName);
                cmd.Parameters.AddWithValue("@is_optional", stage.IsOptional);
                cmd.Parameters.AddWithValue("@description", (object?)stage.Description ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@preferred_model", (object?)stage.PreferredModel ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@requires_review", stage.RequiresReview);
                cmd.Parameters.AddWithValue("@review_deny_action", stage.ReviewDenyAction.ToString());
                await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Load all stages for a pipeline, ordered by stage_order and, within one order, by submitted position.
        /// </summary>
        /// <param name="conn">Open PostgreSQL connection.</param>
        /// <param name="pipelineId">Pipeline identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>List of pipeline stages in stored order.</returns>
        private static async Task<List<PipelineStage>> LoadStagesAsync(NpgsqlConnection conn, string pipelineId, CancellationToken token)
        {
            List<PipelineStage> stages = new List<PipelineStage>();

            using (NpgsqlCommand cmd = new NpgsqlCommand())
            {
                cmd.Connection = conn;
                cmd.CommandText = "SELECT * FROM pipeline_stages WHERE pipeline_id = @pipeline_id ORDER BY stage_order, stage_position, id;";
                cmd.Parameters.AddWithValue("@pipeline_id", pipelineId);
                using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                        stages.Add(PipelineColumns.ReadStage(reader, PostgresqlDatabaseDriver.StoredValues));
                }
            }

            return stages;
        }

        #endregion
    }
}
