namespace Armada.Server.Mcp
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Database;
    using Armada.Core.Services;
    using Armada.Core.Settings;

    /// <summary>One durable mission budget for unified recall and its compatibility tools.</summary>
    public sealed class ContextRecallBudget
    {
        private readonly DatabaseDriver _Database;
        private readonly ArmadaSettings _Settings;

        /// <summary>Maximum shared calls, retaining the legacy code limit as a conservative cap.</summary>
        public int Maximum => _Settings.CodeIndex.Enabled
            ? Math.Min(_Settings.ContextRetrieval.MaxCallsPerMission, _Settings.CodeIndex.CaptainSearchMaxCallsPerMission)
            : _Settings.ContextRetrieval.MaxCallsPerMission;

        /// <summary>Create a budget over existing mission persistence.</summary>
        /// <param name="database">Configured database.</param>
        /// <param name="settings">Live recall limits.</param>
        public ContextRecallBudget(DatabaseDriver database, ArmadaSettings settings)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>Spend a call only after authorization. Operator calls without a mission have no mission counter.</summary>
        /// <param name="scope">Resolved read scope.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Committed count, zero for an operator call, or null when exhausted.</returns>
        public Task<int?> ConsumeAsync(ContextRecallScope scope, CancellationToken token = default)
        {
            if (scope.Mission == null) return Task.FromResult<int?>(0);
            return _Database.Missions.TryConsumeContextRecallAsync(OwnershipPolicy.TenantOfRecord(scope.Mission.TenantId),
                OwnershipPolicy.UserOfRecord(scope.Mission.UserId), scope.Mission.Id, Maximum, token);
        }
    }
}
