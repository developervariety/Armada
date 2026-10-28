namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Authorization;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>Caller-scoped historical Research output paging.</summary>
    public sealed partial class ResearchReplayService
    {
        /// <summary>Read an immutable prior report through the same redaction and digest pager as current reports.</summary>
        /// <param name="caller">Current global operator or a mission credential in this replayed voyage.</param>
        /// <param name="replayId">Committed replay identity.</param>
        /// <param name="missionId">Original stage identity.</param>
        /// <param name="offset">Character offset.</param>
        /// <param name="length">Bounded page length.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A redacted digest-backed page of the historical attempt.</returns>
        public async Task<MissionOutputArtifactPage?> ReadOutputAsync(AuthContext caller, string replayId, string missionId,
            int offset = 0, int length = MissionOutputArtifact.DefaultPageLength, CancellationToken token = default)
        {
            AuthContext actor;
            Mission? anchor = null;
            if (!String.IsNullOrEmpty(caller.MissionId))
            {
                if (!String.IsNullOrEmpty(caller.AskThreadId) || !String.IsNullOrEmpty(caller.AskTurnId))
                    throw new UnauthorizedAccessException("Ask credentials cannot read Research attempt history.");
                anchor = await Mcp.McpMissionEvidenceScope.ReadMissionAsync(_Database, caller, caller.MissionId).ConfigureAwait(false);
                Mission? current = await Mcp.McpMissionEvidenceScope.ReadMissionAsync(_Database, caller, missionId).ConfigureAwait(false);
                if (anchor == null || current == null || String.IsNullOrEmpty(anchor.ResearchReplayId)
                    || current.ResearchReplayId != anchor.ResearchReplayId || String.IsNullOrEmpty(anchor.VoyageId)
                    || anchor.VoyageId != current.VoyageId)
                    throw new UnauthorizedAccessException("Prior attempt history is outside the current replayed voyage.");
                actor = Mcp.McpMissionEvidenceScope.OwnerScopeOf(caller);
            }
            else actor = await _Operators.RequireOperatorAsync(caller, token).ConfigureAwait(false);
            Mission? prior = await _Database.ResearchReplays.ReadPriorMissionAsync(OwnershipPolicy.TenantOf(actor),
                OwnershipPolicy.UserOf(actor), replayId, missionId, token).ConfigureAwait(false);
            if (prior == null || (anchor != null && prior.VoyageId != anchor.VoyageId)) return null;
            MissionOutputArtifactPage page = MissionOutputArtifact.Build(prior, offset, length);
            page.ArtifactRef = "research-replay:" + replayId + "/mission-output:" + missionId;
            page.PriorResearchAttempt = ResearchReplayStage.FromMission(prior);
            return page;
        }
    }
}
