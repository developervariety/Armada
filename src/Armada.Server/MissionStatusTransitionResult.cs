namespace Armada.Server
{
    using System;
    using Armada.Core.Models;

    /// <summary>
    /// Result of an operator mission status transition, rendered by each entry point in its own format.
    /// </summary>
    public sealed class MissionStatusTransitionResult
    {
        /// <summary>
        /// What happened.
        /// </summary>
        public MissionStatusTransitionOutcomeEnum Outcome { get; }

        /// <summary>
        /// The mission after the request; the unchanged mission when refused or invalid.
        /// </summary>
        public Mission? Mission { get; }

        /// <summary>
        /// Named gate reason for a refusal, for example manual_completion_ancestry_unavailable.
        /// </summary>
        public string? Reason { get; }

        /// <summary>
        /// Human-readable message every entry point returns for a non-applied outcome.
        /// </summary>
        public string? Message { get; }

        private MissionStatusTransitionResult(MissionStatusTransitionOutcomeEnum outcome, Mission? mission, string? reason, string? message)
        {
            Outcome = outcome;
            Mission = mission;
            Reason = reason;
            Message = message;
        }

        /// <summary>
        /// The request was applied.
        /// </summary>
        public static MissionStatusTransitionResult Applied(Mission mission)
        {
            return new MissionStatusTransitionResult(
                MissionStatusTransitionOutcomeEnum.Applied,
                mission ?? throw new ArgumentNullException(nameof(mission)),
                null,
                null);
        }

        /// <summary>
        /// The target is not reachable from the current status.
        /// </summary>
        public static MissionStatusTransitionResult InvalidTransition(Mission mission, string message)
        {
            return new MissionStatusTransitionResult(MissionStatusTransitionOutcomeEnum.InvalidTransition, mission, null, message);
        }

        /// <summary>
        /// A manual completion gate refused the request.
        /// </summary>
        public static MissionStatusTransitionResult Refused(Mission mission, string reason)
        {
            if (String.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A refusal requires a named reason.", nameof(reason));
            return new MissionStatusTransitionResult(
                MissionStatusTransitionOutcomeEnum.Refused,
                mission,
                reason,
                "Manual completion blocked: " + reason);
        }

        /// <summary>
        /// The mission disappeared while the landing pipeline ran.
        /// </summary>
        public static MissionStatusTransitionResult MissionMissing()
        {
            return new MissionStatusTransitionResult(
                MissionStatusTransitionOutcomeEnum.MissionMissing,
                null,
                null,
                "Mission not found after landing");
        }
    }
}
