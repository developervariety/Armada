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

    /// <summary>Provider-neutral persistence for fleet action definitions.</summary>
    public sealed partial class FleetActionMethods : IFleetActionMethods
    {
        private const string _Columns = "id,tenant_id,user_id,name,active,kind,prompt_template,pipeline_id,concurrency,created_utc,last_update_utc";
        private readonly Func<DbConnection> _ConnectionFactory;
        private readonly DatabaseTypeEnum _Provider;

        /// <summary>Create the definition store.</summary>
        /// <param name="connectionFactory">Creates a closed provider connection.</param>
        /// <param name="provider">Database provider type.</param>
        public FleetActionMethods(Func<DbConnection> connectionFactory, DatabaseTypeEnum provider)
        {
            _ConnectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _Provider = provider;
        }

        /// <inheritdoc />
        public async Task<FleetAction> CreateAsync(FleetAction action, CancellationToken token = default)
        {
            FleetActionStorage.ValidateAction(action);
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "INSERT INTO fleet_actions (" + _Columns + ") VALUES (@id,@tenant_id,@user_id,@name,@active,@kind,@prompt_template,@pipeline_id,@concurrency,@created_utc,@last_update_utc);";
                    Bind(command, action, true); await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
            return action;
        }

        /// <inheritdoc />
        public async Task<bool> UpdateAsync(FleetAction action, CancellationToken token = default)
        {
            FleetActionStorage.ValidateAction(action);
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE fleet_actions SET name=@name,active=@active,kind=@kind,prompt_template=@prompt_template,pipeline_id=@pipeline_id,concurrency=@concurrency,last_update_utc=@last_update_utc WHERE id=@id AND tenant_id=@tenant_id AND user_id=@user_id;";
                    Bind(command, action, false); return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
                }
            }
        }

        private void Bind(DbCommand command, FleetAction action, bool includeCreated)
        {
            FleetActionStorage.Add(command, "@id", action.Id); FleetActionStorage.Add(command, "@tenant_id", action.TenantId); FleetActionStorage.Add(command, "@user_id", action.UserId); FleetActionStorage.Add(command, "@name", action.Name); FleetActionStorage.Add(command, "@active", _Provider == DatabaseTypeEnum.Sqlite ? (object)(action.Active ? 1 : 0) : action.Active); FleetActionStorage.Add(command, "@kind", action.Kind.ToString()); FleetActionStorage.Add(command, "@prompt_template", action.PromptTemplate); FleetActionStorage.Add(command, "@pipeline_id", action.PipelineId); FleetActionStorage.Add(command, "@concurrency", action.Concurrency); if (includeCreated) FleetActionStorage.AddTime(command, _Provider, "fleet_actions", "created_utc", action.CreatedUtc); FleetActionStorage.AddTime(command, _Provider, "fleet_actions", "last_update_utc", action.LastUpdateUtc);
        }
    }
}
