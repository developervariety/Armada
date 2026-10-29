namespace Armada.Server
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using SyslogLogging;

    /// <summary>Reports one repeat-review stop per rejected Judge without holding the fleet.</summary>
    internal static class ReviewRepeatStopNotice
    {
        internal static async Task PostAsync(DatabaseDriver database, LoggingModule logging, Objective objective,
            ObjectiveDispatchPreviewIssue stop, string? participantKey, CancellationToken token)
        {
            EnumerationResult<ArmadaEvent> prior = await database.Events.EnumerateAsync(new EnumerationQuery
            { EventType = ObjectiveReviewRepeatGuard.IssueCode, MissionId = stop.RelatedValue, PageSize = 1 }, token).ConfigureAwait(false);
            if (prior.Objects.Count > 0) return;
            Mission? judge = stop.RelatedValue == null ? null : await database.Missions.ReadAsync(stop.RelatedValue, token).ConfigureAwait(false);
            if (participantKey == null && judge != null)
                participantKey = await VoyageDispatchOrigin.ReadAsync(database, judge, token).ConfigureAwait(false);
            CoordinationService coordination = new CoordinationService(logging, database);
            await coordination.PostMessageAsync(CoordinationService.DefaultRoomKey, CoordinationAuthorTypeEnum.System,
                authorId: null, authorName: "Review Repeat Stop", content: stop.Message,
                voyageId: judge?.VoyageId, missionId: stop.RelatedValue,
                vesselId: objective.VesselIds.Count == 1 ? objective.VesselIds[0] : null,
                incidentId: null, tenantId: objective.TenantId, toParticipantKey: participantKey, token: token).ConfigureAwait(false);
            await database.Events.CreateAsync(new ArmadaEvent(ObjectiveReviewRepeatGuard.IssueCode, stop.Message)
            {
                TenantId = objective.TenantId, UserId = objective.UserId,
                EntityType = "objective", EntityId = objective.Id, MissionId = stop.RelatedValue,
                VoyageId = judge?.VoyageId
            }, token).ConfigureAwait(false);
        }
    }
}
