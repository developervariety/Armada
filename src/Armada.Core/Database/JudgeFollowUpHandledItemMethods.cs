namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Globalization;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Provider-neutral implementation of the Judge follow-up handled-item store. Only the migration DDL
    /// differs per provider. One item holds at most one marker; a replacement deletes the old marker and
    /// stores the new one in one transaction.
    /// </summary>
    public sealed class JudgeFollowUpHandledItemMethods : IJudgeFollowUpHandledItemMethods
    {
        #region Private-Members

        private const string _Columns = "id, tenant_id, user_id, follow_up_id, item_number, kind, disposition, handled_by, handled_utc";
        private const int _IdBatchSize = 200;
        private readonly Func<DbConnection> _ConnectionFactory;
        private readonly DatabaseTypeEnum _Provider;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate.</summary>
        /// <param name="connectionFactory">Creates an unopened provider connection.</param>
        /// <param name="provider">Database provider.</param>
        public JudgeFollowUpHandledItemMethods(Func<DbConnection> connectionFactory, DatabaseTypeEnum provider)
        {
            _ConnectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _Provider = provider;
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<bool> CreateAsync(JudgeFollowUpHandledItem item, CancellationToken token = default)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (await ReadAsync(item.FollowUpId, item.ItemNumber, token).ConfigureAwait(false) != null) return false;
            try
            {
                using (DbConnection connection = _ConnectionFactory())
                {
                    await connection.OpenAsync(token).ConfigureAwait(false);
                    await InsertAsync(connection, null, item, token).ConfigureAwait(false);
                }
                return true;
            }
            catch (DbException)
            {
                // The unique item index refused a marker stored between the read and the insert.
                if (await ReadAsync(item.FollowUpId, item.ItemNumber, token).ConfigureAwait(false) != null) return false;
                throw;
            }
        }

        /// <inheritdoc />
        public async Task ReplaceAsync(JudgeFollowUpHandledItem item, CancellationToken token = default)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
                {
                    using (DbTransaction transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false))
                    {
                        using (DbCommand command = connection.CreateCommand())
                        {
                            command.Transaction = transaction;
                            command.CommandText = "DELETE FROM judge_follow_up_handled_items WHERE follow_up_id = @follow_up_id AND item_number = @item_number;";
                            ProductionFactSql.Add(command, "@follow_up_id", item.FollowUpId);
                            ProductionFactSql.Add(command, "@item_number", item.ItemNumber);
                            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                        }
                        await InsertAsync(connection, transaction, item, token).ConfigureAwait(false);
                        await transaction.CommitAsync(token).ConfigureAwait(false);
                    }
                }
            }
        }

        /// <inheritdoc />
        public async Task<JudgeFollowUpHandledItem?> ReadAsync(string followUpId, int itemNumber, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(followUpId)) return null;
            List<JudgeFollowUpHandledItem> rows = await SelectAsync(
                "follow_up_id = @follow_up_id AND item_number = @item_number",
                command =>
                {
                    ProductionFactSql.Add(command, "@follow_up_id", followUpId.Trim());
                    ProductionFactSql.Add(command, "@item_number", itemNumber);
                },
                token).ConfigureAwait(false);
            return rows.Count > 0 ? rows[0] : null;
        }

        /// <inheritdoc />
        public async Task<List<JudgeFollowUpHandledItem>> EnumerateForFollowUpsAsync(IReadOnlyCollection<string> followUpIds, CancellationToken token = default)
        {
            if (followUpIds == null) throw new ArgumentNullException(nameof(followUpIds));
            List<string> ids = followUpIds.Where(id => !String.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
            List<JudgeFollowUpHandledItem> results = new List<JudgeFollowUpHandledItem>();
            for (int start = 0; start < ids.Count; start += _IdBatchSize)
            {
                List<string> batch = ids.Skip(start).Take(_IdBatchSize).ToList();
                List<string> names = batch.Select((_, index) => "@id" + index.ToString(CultureInfo.InvariantCulture)).ToList();
                results.AddRange(await SelectAsync(
                    "follow_up_id IN (" + String.Join(", ", names) + ")",
                    command =>
                    {
                        for (int index = 0; index < batch.Count; index++) ProductionFactSql.Add(command, names[index], batch[index]);
                    },
                    token).ConfigureAwait(false));
            }
            return results
                .OrderBy(item => item.FollowUpId, StringComparer.Ordinal)
                .ThenBy(item => item.ItemNumber)
                .ToList();
        }

        #endregion

        #region Private-Methods

        private async Task InsertAsync(DbConnection connection, DbTransaction? transaction, JudgeFollowUpHandledItem item, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO judge_follow_up_handled_items (" + _Columns + ") VALUES (@id, @tenant_id, @user_id, @follow_up_id, @item_number, @kind, @disposition, @handled_by, @handled_utc);";
                ProductionFactSql.Add(command, "@id", item.Id);
                ProductionFactSql.Add(command, "@tenant_id", item.TenantId);
                ProductionFactSql.Add(command, "@user_id", item.UserId);
                ProductionFactSql.Add(command, "@follow_up_id", item.FollowUpId);
                ProductionFactSql.Add(command, "@item_number", item.ItemNumber);
                ProductionFactSql.Add(command, "@kind", item.Kind.ToString());
                ProductionFactSql.Add(command, "@disposition", Bound(item.Disposition, JudgeFollowUpHandledItem.MaximumDispositionLength));
                ProductionFactSql.Add(command, "@handled_by", Bound(item.HandledBy, JudgeFollowUpHandledItem.MaximumHandledByLength));
                ProductionFactSql.AddUtc(command, "@handled_utc", item.HandledUtc, _Provider);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }

        private async Task<List<JudgeFollowUpHandledItem>> SelectAsync(string where, Action<DbCommand> bind, CancellationToken token)
        {
            List<JudgeFollowUpHandledItem> rows = new List<JudgeFollowUpHandledItem>();
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT " + _Columns + " FROM judge_follow_up_handled_items WHERE " + where + ";";
                    bind(command);
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            rows.Add(FromReader(reader));
                    }
                }
            }
            return rows;
        }

        private static JudgeFollowUpHandledItem FromReader(DbDataReader reader)
        {
            string kind = Convert.ToString(reader["kind"], CultureInfo.InvariantCulture)!;
            if (!Enum.TryParse(kind, false, out JudgeFollowUpDispositionKindEnum parsedKind))
                throw new InvalidOperationException("Stored Judge follow-up handled item has an unknown kind: " + kind);

            return new JudgeFollowUpHandledItem
            {
                Id = Convert.ToString(reader["id"], CultureInfo.InvariantCulture)!,
                TenantId = ProductionFactSql.ReadText(reader["tenant_id"]),
                UserId = ProductionFactSql.ReadText(reader["user_id"]),
                FollowUpId = Convert.ToString(reader["follow_up_id"], CultureInfo.InvariantCulture)!,
                ItemNumber = Convert.ToInt32(reader["item_number"], CultureInfo.InvariantCulture),
                Kind = parsedKind,
                Disposition = ProductionFactSql.ReadText(reader["disposition"]) ?? String.Empty,
                HandledBy = ProductionFactSql.ReadText(reader["handled_by"]) ?? String.Empty,
                HandledUtc = ProductionFactSql.ReadUtc(reader["handled_utc"])
            };
        }

        private static string Bound(string? value, int maximumLength)
        {
            if (value == null) return String.Empty;
            string trimmed = value.Trim();
            return trimmed.Length > maximumLength ? trimmed.Substring(0, maximumLength) : trimmed;
        }

        #endregion
    }
}
