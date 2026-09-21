namespace Armada.Server
{
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Models;

    /// <summary>Created voyage with the persisted start commit for each mission.</summary>
    public sealed class VoyageDispatchResponse : Voyage
    {
        /// <summary>Mission identifiers and start commits. Dependent stages have no separate start ref.</summary>
        public List<DispatchedMissionStartRef> MissionStartRefs { get; }

        /// <summary>Build an additive response that preserves the existing voyage fields.</summary>
        public VoyageDispatchResponse(Voyage voyage, List<Mission> missions)
        {
            Id = voyage.Id;
            TenantId = voyage.TenantId;
            UserId = voyage.UserId;
            Title = voyage.Title;
            Description = voyage.Description;
            Status = voyage.Status;
            CreatedUtc = voyage.CreatedUtc;
            CompletedUtc = voyage.CompletedUtc;
            LastUpdateUtc = voyage.LastUpdateUtc;
            AutoPush = voyage.AutoPush;
            AutoCreatePullRequests = voyage.AutoCreatePullRequests;
            AutoMergePullRequests = voyage.AutoMergePullRequests;
            LandingMode = voyage.LandingMode;
            SourcePlanningSessionId = voyage.SourcePlanningSessionId;
            SourcePlanningMessageId = voyage.SourcePlanningMessageId;
            CaptainOverridesJson = voyage.CaptainOverridesJson;
            SelectedPlaybooks = voyage.SelectedPlaybooks;
            MissionStartRefs = missions.Select(mission => new DispatchedMissionStartRef
            {
                MissionId = mission.Id,
                StartFromRef = mission.StartFromRef
            }).ToList();
        }
    }
}
