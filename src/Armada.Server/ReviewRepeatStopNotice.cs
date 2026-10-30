namespace Armada.Server
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>Records one repeat-review stop event per rejected Judge without holding the fleet.</summary>
    internal static class ReviewRepeatStopNotice
    {
        internal static async Task RecordAsync(DatabaseDriver database, Objective objective,
            ObjectiveDispatchPreviewIssue stop, CancellationToken token)
        {
            EnumerationResult<ArmadaEvent> prior = await database.Events.EnumerateAsync(new EnumerationQuery
            { EventType = ObjectiveReviewRepeatGuard.IssueCode, MissionId = stop.RelatedValue, PageSize = 1 }, token).ConfigureAwait(false);
            if (prior.Objects.Count > 0) return;
            Mission? judge = stop.RelatedValue == null ? null : await database.Missions.ReadAsync(stop.RelatedValue, token).ConfigureAwait(false);
            await database.Events.CreateAsync(new ArmadaEvent(ObjectiveReviewRepeatGuard.IssueCode, stop.Message)
            {
                TenantId = objective.TenantId, UserId = objective.UserId,
                EntityType = "objective", EntityId = objective.Id, MissionId = stop.RelatedValue,
                VoyageId = judge?.VoyageId
            }, token).ConfigureAwait(false);
        }
    }
}
