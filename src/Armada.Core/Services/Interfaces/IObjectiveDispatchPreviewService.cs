namespace Armada.Core.Services.Interfaces
{
    using Armada.Core.Models;

    /// <summary>
    /// Produces a read-only objective dispatch readiness result.
    /// </summary>
    public interface IObjectiveDispatchPreviewService
    {
        /// <summary>
        /// Preview the effective objective dispatch without creating fleet state.
        /// </summary>
        Task<ObjectiveDispatchPreview> PreviewAsync(
            AuthContext auth,
            Objective objective,
            string? requestedVesselId = null,
            string? requestedPipelineId = null,
            IReadOnlyList<CaptainAssignmentOverride>? captainAssignments = null,
            IReadOnlyList<MissionDescription>? missionDescriptions = null,
            CancellationToken token = default);
    }
}
