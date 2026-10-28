namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Data.Common;
    using System.Globalization;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;

    /// <summary>Provider-neutral persistence for derived context search vectors.</summary>
    public sealed class ContextEmbeddingMethods : IContextEmbeddingMethods
    {
        #region Private-Members

        private const int _ReadBatch = 200;
        private const int _MaxWriteAttempts = 5;
        private readonly Func<DbConnection> _ConnectionFactory;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create the vector store.</summary>
        /// <param name="connectionFactory">Creates a closed provider connection.</param>
        public ContextEmbeddingMethods(Func<DbConnection> connectionFactory)
        {
            _ConnectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<List<ContextEmbedding>> ReadAsync(string tenantId, string model, IReadOnlyCollection<string> sourceKeys, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrWhiteSpace(model)) throw new ArgumentNullException(nameof(model));
            List<ContextEmbedding> results = new List<ContextEmbedding>();
            if (sourceKeys == null || sourceKeys.Count == 0) return results;
            List<string> ids = sourceKeys.Where(key => !String.IsNullOrWhiteSpace(key)).Distinct(StringComparer.Ordinal)
                .Select(key => Key(tenantId, key, model)).ToList();
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                for (int offset = 0; offset < ids.Count; offset += _ReadBatch)
                {
                    List<string> batch = ids.Skip(offset).Take(_ReadBatch).ToList();
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.CommandText = "SELECT tenant_id, source_key, model, content_sha256, dimensions, vector FROM context_embeddings WHERE id IN ("
                            + String.Join(",", batch.Select((_, index) => "@id" + index.ToString(CultureInfo.InvariantCulture))) + ");";
                        for (int index = 0; index < batch.Count; index++)
                            StoredValueBinder.Add(command, "@id" + index.ToString(CultureInfo.InvariantCulture), DbType.String, batch[index]);
                        using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                        {
                            while (await reader.ReadAsync(token).ConfigureAwait(false))
                            {
                                float[] vector = Decode(reader.GetString(5));
                                if (vector.Length != Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture)) continue;
                                results.Add(new ContextEmbedding
                                {
                                    TenantId = reader.GetString(0), SourceKey = reader.GetString(1), Model = reader.GetString(2),
                                    ContentSha256 = reader.GetString(3), Vector = vector
                                });
                            }
                        }
                    }
                }
            }
            return results;
        }

        /// <inheritdoc />
        public async Task UpsertAsync(ContextEmbedding embedding, CancellationToken token = default)
        {
            if (embedding == null) throw new ArgumentNullException(nameof(embedding));
            if (embedding.Vector == null || embedding.Vector.Length == 0) throw new ArgumentException("An embedding needs at least one dimension.", nameof(embedding));
            if (String.IsNullOrWhiteSpace(embedding.ContentSha256)) throw new ArgumentException("An embedding needs its content hash.", nameof(embedding));
            string id = Key(embedding.TenantId, embedding.SourceKey, embedding.Model);
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await WriteAsync(id, embedding, token).ConfigureAwait(false);
                    return;
                }
                catch (DbException exception) when (IsDeadlock(exception) && attempt < _MaxWriteAttempts)
                {
                    // The provider rolled this writer back to break a lock cycle with a concurrent writer; write again.
                    await Task.Delay(Random.Shared.Next(5, 25) * attempt, token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<int> DeleteSourceAsync(string tenantId, string sourceKey, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrWhiteSpace(sourceKey)) throw new ArgumentNullException(nameof(sourceKey));
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? lease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "DELETE FROM context_embeddings WHERE tenant_id = @tenant_id AND source_key = @source_key;";
                    StoredValueBinder.Add(command, "@tenant_id", DbType.String, tenantId);
                    StoredValueBinder.Add(command, "@source_key", DbType.String, sourceKey);
                    return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        #endregion

        #region Private-Methods

        private static string Key(string tenantId, string sourceKey, string model)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(tenantId + "\n" + sourceKey + "\n" + model);
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }

        private async Task WriteAsync(string id, ContextEmbedding embedding, CancellationToken token)
        {
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? lease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                using (DbTransaction transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false))
                {
                    using (DbCommand delete = connection.CreateCommand())
                    {
                        delete.Transaction = transaction;
                        delete.CommandText = "DELETE FROM context_embeddings WHERE id = @id;";
                        StoredValueBinder.Add(delete, "@id", DbType.String, id);
                        await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                    using (DbCommand insert = connection.CreateCommand())
                    {
                        insert.Transaction = transaction;
                        insert.CommandText = "INSERT INTO context_embeddings (id, tenant_id, source_key, model, content_sha256, dimensions, vector) "
                            + "VALUES (@id, @tenant_id, @source_key, @model, @content_sha256, @dimensions, @vector);";
                        StoredValueBinder.Add(insert, "@id", DbType.String, id);
                        StoredValueBinder.Add(insert, "@tenant_id", DbType.String, embedding.TenantId);
                        StoredValueBinder.Add(insert, "@source_key", DbType.String, embedding.SourceKey);
                        StoredValueBinder.Add(insert, "@model", DbType.String, embedding.Model);
                        StoredValueBinder.Add(insert, "@content_sha256", DbType.String, embedding.ContentSha256);
                        StoredValueBinder.Add(insert, "@dimensions", DbType.Int32, embedding.Vector.Length);
                        StoredValueBinder.Add(insert, "@vector", DbType.String, Encode(embedding.Vector));
                        try
                        {
                            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                        }
                        catch (DbException exception) when (IsDuplicateKey(exception))
                        {
                            // A concurrent writer committed a vector for the same key after this delete ran.
                            // Its row stands; a stale one fails the content-hash check on the next read and is replaced.
                            return;
                        }
                    }
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                }
            }
        }

        private static bool IsDuplicateKey(DbException exception)
        {
            if (exception is Npgsql.PostgresException postgres) return postgres.SqlState == "23505";
            if (exception is MySqlConnector.MySqlException mysql) return mysql.Number == 1062;
            if (exception is Microsoft.Data.SqlClient.SqlException sqlServer) return sqlServer.Number == 2601 || sqlServer.Number == 2627;
            if (exception is Microsoft.Data.Sqlite.SqliteException sqlite) return sqlite.SqliteErrorCode == 19;
            return false;
        }

        private static bool IsDeadlock(DbException exception)
        {
            if (exception is Npgsql.PostgresException postgres) return postgres.SqlState == "40P01";
            if (exception is MySqlConnector.MySqlException mysql) return mysql.Number == 1213;
            if (exception is Microsoft.Data.SqlClient.SqlException sqlServer) return sqlServer.Number == 1205;
            return false;
        }

        private static string Encode(float[] vector)
        {
            byte[] bytes = new byte[vector.Length * sizeof(float)];
            for (int index = 0; index < vector.Length; index++)
                System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * sizeof(float)), vector[index]);
            return Convert.ToBase64String(bytes);
        }

        private static float[] Decode(string text)
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(text); }
            catch (FormatException) { return Array.Empty<float>(); }
            if (bytes.Length % sizeof(float) != 0) return Array.Empty<float>();
            float[] vector = new float[bytes.Length / sizeof(float)];
            for (int index = 0; index < vector.Length; index++)
                vector[index] = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(index * sizeof(float)));
            return vector;
        }

        #endregion
    }
}
