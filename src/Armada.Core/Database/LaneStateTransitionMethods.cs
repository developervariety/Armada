namespace Armada.Core.Database
{
    using System;
    using System.Data.Common;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Provider-neutral implementation of append-only lane state transitions.
    /// </summary>
    public sealed class LaneStateTransitionMethods : ILaneStateTransitionMethods
    {
        private const string _Columns = "id, lane_key, eligible_count, occupied, capacity, block_reason, eligible_source_families, is_checkpoint, valid_for_seconds, created_utc";
        private readonly Func<DbConnection> _ConnectionFactory;
        private readonly DatabaseTypeEnum _Provider;

        /// <summary>Instantiate.</summary>
        /// <param name="connectionFactory">Creates an unopened provider connection.</param>
        /// <param name="provider">Database provider.</param>
        public LaneStateTransitionMethods(Func<DbConnection> connectionFactory, DatabaseTypeEnum provider)
        {
            _ConnectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _Provider = provider;
        }

        /// <inheritdoc />
        public async Task<LaneStateTransition> CreateAsync(LaneStateTransition transition, CancellationToken token = default)
        {
            if (transition == null) throw new ArgumentNullException(nameof(transition));
            if (String.IsNullOrWhiteSpace(transition.LaneKey)) throw new ArgumentException("Lane key is required.", nameof(transition));
            if (transition.LaneKey.Length > LaneStateTransition.MaximumLaneKeyLength) throw new ArgumentException("Lane key exceeds its storage limit.", nameof(transition));
            if (transition.EligibleCount < 0 || transition.Occupied < 0 || transition.Capacity < 0 || transition.ValidForSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(transition), "Lane counts must be non-negative and the trust window positive.");
            if (transition.EligibleSourceFamilies.Length > LaneStateTransition.MaximumSourceFamiliesLength)
                transition.EligibleSourceFamilies = transition.EligibleSourceFamilies.Substring(0, LaneStateTransition.MaximumSourceFamiliesLength);
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "INSERT INTO lane_state_transitions (" + _Columns + ") VALUES (@id, @lane_key, @eligible_count, @occupied, @capacity, @block_reason, @eligible_source_families, @is_checkpoint, @valid_for_seconds, @created_utc);";
                    ProductionFactSql.Add(command, "@id", transition.Id);
                    ProductionFactSql.Add(command, "@lane_key", transition.LaneKey);
                    ProductionFactSql.Add(command, "@eligible_count", transition.EligibleCount);
                    ProductionFactSql.Add(command, "@occupied", transition.Occupied);
                    ProductionFactSql.Add(command, "@capacity", transition.Capacity);
                    ProductionFactSql.Add(command, "@block_reason", transition.BlockReason.ToString());
                    ProductionFactSql.Add(command, "@eligible_source_families", transition.EligibleSourceFamilies);
                    ProductionFactSql.AddBool(command, "@is_checkpoint", transition.Checkpoint, _Provider);
                    ProductionFactSql.Add(command, "@valid_for_seconds", transition.ValidForSeconds);
                    ProductionFactSql.AddUtc(command, "@created_utc", transition.CreatedUtc, _Provider);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
            return transition;
        }

        /// <inheritdoc />
        public async Task<ProductionFactPage<LaneStateTransition>> EnumerateAsync(ProductionFactQuery query, CancellationToken token = default)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            ProductionFactQuery unscoped = new ProductionFactQuery { FromUtc = query.FromUtc, ToUtc = query.ToUtc, Limit = query.Limit };
            ProductionFactPage<LaneStateTransition> page = new ProductionFactPage<LaneStateTransition>();
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    ProductionFactSql.BuildWindowSelect(command, _Provider, "lane_state_transitions", _Columns, unscoped);
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            if (page.Items.Count >= unscoped.Limit) { page.Truncated = true; break; }
                            page.Items.Add(FromReader(reader));
                        }
                    }
                }
            }
            return page;
        }

        private static LaneStateTransition FromReader(DbDataReader reader)
        {
            string block = Convert.ToString(reader["block_reason"], CultureInfo.InvariantCulture)!;
            if (!Enum.TryParse(block, false, out LaneBlockReasonEnum parsed))
                throw new InvalidOperationException("Stored lane transition has an unknown block reason: " + block);
            return new LaneStateTransition
            {
                Id = Convert.ToString(reader["id"], CultureInfo.InvariantCulture)!,
                LaneKey = Convert.ToString(reader["lane_key"], CultureInfo.InvariantCulture)!,
                EligibleCount = Convert.ToInt32(reader["eligible_count"], CultureInfo.InvariantCulture),
                Occupied = Convert.ToInt32(reader["occupied"], CultureInfo.InvariantCulture),
                Capacity = Convert.ToInt32(reader["capacity"], CultureInfo.InvariantCulture),
                BlockReason = parsed,
                EligibleSourceFamilies = ProductionFactSql.ReadText(reader["eligible_source_families"]) ?? String.Empty,
                Checkpoint = ProductionFactSql.ReadBool(reader["is_checkpoint"]),
                ValidForSeconds = Convert.ToInt32(reader["valid_for_seconds"], CultureInfo.InvariantCulture),
                CreatedUtc = ProductionFactSql.ReadUtc(reader["created_utc"])
            };
        }
    }
}
