namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Services;

    /// <summary>
    /// Provider-neutral implementation of the persisted dispatch hold.
    /// </summary>
    public sealed class DispatchHoldMethods : IDispatchHoldMethods
    {
        private const string _Table = "dispatch_holds";
        private const string _RowId = "dispatch-hold";
        private const string _VesselRowPrefix = "vessel:";
        private readonly Func<DbConnection> _ConnectionFactory;
        private readonly DatabaseTypeEnum _Provider;

        /// <summary>Instantiate.</summary>
        /// <param name="connectionFactory">Creates an unopened provider connection.</param>
        /// <param name="provider">Database provider.</param>
        public DispatchHoldMethods(Func<DbConnection> connectionFactory, DatabaseTypeEnum provider)
        {
            _ConnectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _Provider = provider;
        }

        /// <inheritdoc />
        public Task SaveAsync(DispatchHoldSnapshot hold, CancellationToken token = default)
        {
            if (hold == null) throw new ArgumentNullException(nameof(hold));
            return SaveRowAsync(_RowId, hold, token);
        }

        /// <inheritdoc />
        public Task SaveVesselAsync(DispatchHoldSnapshot hold, CancellationToken token = default)
        {
            if (hold == null) throw new ArgumentNullException(nameof(hold));
            if (String.IsNullOrWhiteSpace(hold.VesselId)) throw new ArgumentException("A vessel hold needs a vessel id.", nameof(hold));
            return SaveRowAsync(_VesselRowPrefix + hold.VesselId, hold, token);
        }

        private async Task SaveRowAsync(string rowId, DispatchHoldSnapshot hold, CancellationToken token)
        {
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                {
                    using (DbTransaction transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false))
                    {
                        using (DbCommand delete = connection.CreateCommand())
                        {
                            delete.Transaction = transaction;
                            delete.CommandText = "DELETE FROM " + _Table + " WHERE id = @id;";
                            ProductionFactSql.Add(delete, "@id", rowId);
                            await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                        }

                        using (DbCommand insert = connection.CreateCommand())
                        {
                            insert.Transaction = transaction;
                            insert.CommandText = "INSERT INTO " + _Table + " (id, reason, set_by, set_utc) VALUES (@id, @reason, @set_by, @set_utc);";
                            ProductionFactSql.Add(insert, "@id", rowId);
                            ProductionFactSql.Add(insert, "@reason", hold.Reason);
                            ProductionFactSql.Add(insert, "@set_by", hold.SetBy);
                            ProductionFactSql.AddUtc(insert, "@set_utc", hold.SetByUtc, _Provider);
                            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                        }

                        await transaction.CommitAsync(token).ConfigureAwait(false);
                    }
                }
            }
        }

        /// <inheritdoc />
        public Task ClearAsync(CancellationToken token = default)
        {
            return ClearRowAsync(_RowId, token);
        }

        /// <inheritdoc />
        public Task ClearVesselAsync(string vesselId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(vesselId)) throw new ArgumentNullException(nameof(vesselId));
            return ClearRowAsync(_VesselRowPrefix + vesselId, token);
        }

        private async Task ClearRowAsync(string rowId, CancellationToken token)
        {
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                {
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.CommandText = "DELETE FROM " + _Table + " WHERE id = @id;";
                        ProductionFactSql.Add(command, "@id", rowId);
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }
            }
        }

        /// <inheritdoc />
        public async Task<DispatchHoldSnapshot?> ReadAsync(CancellationToken token = default)
        {
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT reason, set_by, set_utc FROM " + _Table + " WHERE id = @id;";
                    ProductionFactSql.Add(command, "@id", _RowId);
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
                        return new DispatchHoldSnapshot
                        {
                            Reason = Convert.ToString(reader["reason"])!,
                            SetBy = ProductionFactSql.ReadText(reader["set_by"]),
                            SetByUtc = ProductionFactSql.ReadUtc(reader["set_utc"])
                        };
                    }
                }
            }
        }

        /// <inheritdoc />
        public async Task<List<DispatchHoldSnapshot>> ReadVesselsAsync(CancellationToken token = default)
        {
            List<DispatchHoldSnapshot> holds = new List<DispatchHoldSnapshot>();
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT id, reason, set_by, set_utc FROM " + _Table + " WHERE id LIKE @prefix;";
                    ProductionFactSql.Add(command, "@prefix", _VesselRowPrefix + "%");
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            string id = Convert.ToString(reader["id"])!;
                            if (!id.StartsWith(_VesselRowPrefix, StringComparison.Ordinal)) continue;
                            holds.Add(new DispatchHoldSnapshot
                            {
                                VesselId = id.Substring(_VesselRowPrefix.Length),
                                Reason = Convert.ToString(reader["reason"])!,
                                SetBy = ProductionFactSql.ReadText(reader["set_by"]),
                                SetByUtc = ProductionFactSql.ReadUtc(reader["set_utc"])
                            });
                        }
                    }
                }
            }

            return holds;
        }
    }
}
