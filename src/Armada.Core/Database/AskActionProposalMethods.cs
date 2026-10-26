namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Globalization;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Provider-neutral durable storage for owner-scoped Ask action proposals.</summary>
    public sealed partial class AskActionProposalMethods : IAskActionProposalMethods
    {
        private const string _Columns = "id, tenant_id, user_id, thread_id, request_id, tool_name, arguments_json, fingerprint, state, created_utc, expires_utc, decided_utc, completed_utc, outcome, work_references_json";
        private readonly Func<DbConnection> _ConnectionFactory;
        private readonly DatabaseTypeEnum _Provider;

        /// <summary>Create the proposal store.</summary>
        /// <param name="connectionFactory">Creates a closed database connection.</param>
        /// <param name="provider">Database provider.</param>
        public AskActionProposalMethods(Func<DbConnection> connectionFactory, DatabaseTypeEnum provider)
        {
            _ConnectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _Provider = provider;
        }

    }
}
