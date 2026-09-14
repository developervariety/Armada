namespace Armada.Core.Database
{
    using System;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Provider-neutral implementation of append-only preparation claim observations.
    /// </summary>
    public sealed class PreparationClaimObservationMethods : IPreparationClaimObservationMethods
    {
        private const string _Columns = "id, tenant_id, user_id, objective_id, claim_id, claim_kind, source_family, voyage_id, source_commit, target_commit, evidence_fingerprint, observation, created_utc";
        private readonly Func<DbConnection> _ConnectionFactory;
        private readonly DatabaseTypeEnum _Provider;

        /// <summary>Instantiate.</summary>
        /// <param name="connectionFactory">Creates an unopened provider connection.</param>
        /// <param name="provider">Database provider.</param>
        public PreparationClaimObservationMethods(Func<DbConnection> connectionFactory, DatabaseTypeEnum provider)
        {
            _ConnectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _Provider = provider;
        }

        /// <inheritdoc />
        public async Task<PreparationClaimObservation> CreateAsync(PreparationClaimObservation observation, CancellationToken token = default)
        {
            if (observation == null) throw new ArgumentNullException(nameof(observation));
            if (String.IsNullOrWhiteSpace(observation.ObjectiveId)) throw new ArgumentException("Objective identifier is required.", nameof(observation));
            if (String.IsNullOrWhiteSpace(observation.ClaimId)) throw new ArgumentException("Claim identifier is required.", nameof(observation));
            if (String.IsNullOrWhiteSpace(observation.EvidenceFingerprint)) throw new ArgumentException("Evidence fingerprint is required.", nameof(observation));
            observation.SourceFamily = ProductionFactSql.BoundCode(observation.SourceFamily, PreparationClaimObservation.MaximumSourceFamilyLength) ?? "unknown";
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "INSERT INTO preparation_claim_observations (" + _Columns + ") VALUES (@id, @tenant_id, @user_id, @objective_id, @claim_id, @claim_kind, @source_family, @voyage_id, @source_commit, @target_commit, @evidence_fingerprint, @observation, @created_utc);";
                    ProductionFactSql.Add(command, "@id", observation.Id);
                    ProductionFactSql.Add(command, "@tenant_id", observation.TenantId);
                    ProductionFactSql.Add(command, "@user_id", observation.UserId);
                    ProductionFactSql.Add(command, "@objective_id", observation.ObjectiveId);
                    ProductionFactSql.Add(command, "@claim_id", observation.ClaimId);
                    ProductionFactSql.Add(command, "@claim_kind", observation.ClaimKind.ToString());
                    ProductionFactSql.Add(command, "@source_family", observation.SourceFamily);
                    ProductionFactSql.Add(command, "@voyage_id", observation.VoyageId);
                    ProductionFactSql.Add(command, "@source_commit", observation.SourceCommit);
                    ProductionFactSql.Add(command, "@target_commit", observation.TargetCommit);
                    ProductionFactSql.Add(command, "@evidence_fingerprint", observation.EvidenceFingerprint);
                    ProductionFactSql.Add(command, "@observation", observation.Observation.ToString());
                    ProductionFactSql.AddUtc(command, "@created_utc", observation.CreatedUtc, _Provider);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
            return observation;
        }

        /// <inheritdoc />
        public async Task<ProductionFactPage<PreparationClaimObservation>> EnumerateAsync(ProductionFactQuery query, CancellationToken token = default)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            ProductionFactPage<PreparationClaimObservation> page = new ProductionFactPage<PreparationClaimObservation>();
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    ProductionFactSql.BuildWindowSelect(command, _Provider, "preparation_claim_observations", _Columns, query);
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

        private static PreparationClaimObservation FromReader(DbDataReader reader)
        {
            string kind = Convert.ToString(reader["claim_kind"])!;
            string observation = Convert.ToString(reader["observation"])!;
            if (!Enum.TryParse(kind, false, out ObjectivePreparationClaimKindEnum parsedKind))
                throw new InvalidOperationException("Stored claim observation has an unknown claim kind: " + kind);
            if (!Enum.TryParse(observation, false, out PreparationClaimObservationEnum parsedObservation))
                throw new InvalidOperationException("Stored claim observation has an unknown observation type: " + observation);
            return new PreparationClaimObservation
            {
                Id = Convert.ToString(reader["id"])!,
                TenantId = ProductionFactSql.ReadText(reader["tenant_id"]),
                UserId = ProductionFactSql.ReadText(reader["user_id"]),
                ObjectiveId = Convert.ToString(reader["objective_id"])!,
                ClaimId = Convert.ToString(reader["claim_id"])!,
                ClaimKind = parsedKind,
                SourceFamily = Convert.ToString(reader["source_family"])!,
                VoyageId = ProductionFactSql.ReadText(reader["voyage_id"]),
                SourceCommit = ProductionFactSql.ReadText(reader["source_commit"]),
                TargetCommit = ProductionFactSql.ReadText(reader["target_commit"]),
                EvidenceFingerprint = Convert.ToString(reader["evidence_fingerprint"])!,
                Observation = parsedObservation,
                CreatedUtc = ProductionFactSql.ReadUtc(reader["created_utc"])
            };
        }
    }
}
