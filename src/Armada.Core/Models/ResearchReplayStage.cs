namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>Non-secret status and chronology of one immutable prior Research duty.</summary>
    public sealed class ResearchReplayStage
    {
        /// <summary>Original mission identity, also retained by the current attempt.</summary>
        public string MissionId { get; set; } = String.Empty;
        /// <summary>Original independent role.</summary>
        public string? Persona { get; set; }
        /// <summary>Historical terminal status, never a verdict for the current attempt.</summary>
        public MissionStatusEnum Status { get; set; }
        /// <summary>Original creation time.</summary>
        public DateTime CreatedUtc { get; set; }
        /// <summary>Prior attempt's actual start time.</summary>
        public DateTime? StartedUtc { get; set; }
        /// <summary>Prior attempt's actual completion time.</summary>
        public DateTime? CompletedUtc { get; set; }
        /// <summary>Immutable source recorded for that prior duty.</summary>
        public string? CommitHash { get; set; }
        /// <summary>Earlier replay archive identity, or null for the original attempt.</summary>
        public string? PreviousReplayId { get; set; }
        /// <summary>Historical autonomous recovery counter, which replay does not reset.</summary>
        public int RecoveryAttempts { get; set; }

        /// <summary>Copy only safe history metadata; no prompt, environment or credential values.</summary>
        /// <param name="mission">Archived stage.</param>
        /// <returns>Safe immutable-attempt metadata.</returns>
        public static ResearchReplayStage FromMission(Mission mission) => new ResearchReplayStage
        {
            MissionId = mission.Id, Persona = mission.Persona, Status = mission.Status,
            CreatedUtc = mission.CreatedUtc, StartedUtc = mission.StartedUtc, CompletedUtc = mission.CompletedUtc,
            CommitHash = mission.CommitHash, PreviousReplayId = mission.ResearchReplayId, RecoveryAttempts = mission.RecoveryAttempts
        };
    }
}
