namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Provider-neutral implementation of append-only mission attempt facts.
    /// </summary>
    public sealed class MissionAttemptFactMethods : IMissionAttemptFactMethods
    {
        private const string _Columns = "id, tenant_id, user_id, mission_id, voyage_id, vessel_id, root_mission_id, parent_mission_id, fact_type, is_rescue, reason_code, created_utc";
        private readonly Func<DbConnection> _ConnectionFactory;
        private readonly DatabaseTypeEnum _Provider;

        /// <summary>Instantiate.</summary>
        /// <param name="connectionFactory">Creates an unopened provider connection.</param>
        /// <param name="provider">Database provider.</param>
        public MissionAttemptFactMethods(Func<DbConnection> connectionFactory, DatabaseTypeEnum provider)
        {
            _ConnectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _Provider = provider;
        }

        /// <inheritdoc />
        public async Task<MissionAttemptFact> CreateAsync(MissionAttemptFact fact, CancellationToken token = default)
        {
            if (fact == null) throw new ArgumentNullException(nameof(fact));
            if (String.IsNullOrWhiteSpace(fact.MissionId)) throw new ArgumentException("Mission identifier is required.", nameof(fact));
            if (String.IsNullOrWhiteSpace(fact.RootMissionId)) throw new ArgumentException("Root mission identifier is required.", nameof(fact));
            fact.ReasonCode = ProductionFactSql.BoundCode(fact.ReasonCode, MissionAttemptFact.MaximumReasonCodeLength);
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "INSERT INTO mission_attempt_facts (" + _Columns + ") VALUES (@id, @tenant_id, @user_id, @mission_id, @voyage_id, @vessel_id, @root_mission_id, @parent_mission_id, @fact_type, @is_rescue, @reason_code, @created_utc);";
                    ProductionFactSql.Add(command, "@id", fact.Id);
                    ProductionFactSql.Add(command, "@tenant_id", fact.TenantId);
                    ProductionFactSql.Add(command, "@user_id", fact.UserId);
                    ProductionFactSql.Add(command, "@mission_id", fact.MissionId);
                    ProductionFactSql.Add(command, "@voyage_id", fact.VoyageId);
                    ProductionFactSql.Add(command, "@vessel_id", fact.VesselId);
                    ProductionFactSql.Add(command, "@root_mission_id", fact.RootMissionId);
                    ProductionFactSql.Add(command, "@parent_mission_id", fact.ParentMissionId);
                    ProductionFactSql.Add(command, "@fact_type", fact.FactType.ToString());
                    ProductionFactSql.AddBool(command, "@is_rescue", fact.IsRescue, _Provider);
                    ProductionFactSql.Add(command, "@reason_code", fact.ReasonCode);
                    ProductionFactSql.AddUtc(command, "@created_utc", fact.CreatedUtc, _Provider);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
            return fact;
        }

        /// <inheritdoc />
        public async Task<ProductionFactPage<MissionAttemptFact>> EnumerateAsync(ProductionFactQuery query, CancellationToken token = default)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            ProductionFactPage<MissionAttemptFact> page = new ProductionFactPage<MissionAttemptFact>();
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    ProductionFactSql.BuildWindowSelect(command, _Provider, "mission_attempt_facts", _Columns, query);
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            if (page.Items.Count >= query.Limit) { page.Truncated = true; break; }
                            page.Items.Add(FromReader(reader));
                        }
                    }
                }
            }
            return page;
        }

        private static MissionAttemptFact FromReader(DbDataReader reader)
        {
            string factType = Convert.ToString(reader["fact_type"])!;
            if (!Enum.TryParse(factType, false, out MissionAttemptFactTypeEnum parsed))
                throw new InvalidOperationException("Stored mission attempt fact has an unknown type: " + factType);
            return new MissionAttemptFact
            {
                Id = Convert.ToString(reader["id"])!,
                TenantId = ProductionFactSql.ReadText(reader["tenant_id"]),
                UserId = ProductionFactSql.ReadText(reader["user_id"]),
                MissionId = Convert.ToString(reader["mission_id"])!,
                VoyageId = ProductionFactSql.ReadText(reader["voyage_id"]),
                VesselId = ProductionFactSql.ReadText(reader["vessel_id"]),
                RootMissionId = Convert.ToString(reader["root_mission_id"])!,
                ParentMissionId = ProductionFactSql.ReadText(reader["parent_mission_id"]),
                FactType = parsed,
                IsRescue = ProductionFactSql.ReadBool(reader["is_rescue"]),
                ReasonCode = ProductionFactSql.ReadText(reader["reason_code"]),
                CreatedUtc = ProductionFactSql.ReadUtc(reader["created_utc"])
            };
        }
    }
}
