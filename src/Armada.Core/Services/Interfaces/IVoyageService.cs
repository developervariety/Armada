namespace Armada.Core.Services.Interfaces
{
    using Armada.Core.Models;

    /// <summary>
    /// Service for voyage lifecycle management.
    /// </summary>
    public interface IVoyageService
    {
        /// <summary>
        /// Apply <see cref="VoyageCompletionRule"/> to every voyage that
        /// <see cref="VoyageCompletionRule.IsSweepCandidate"/> selects.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <param name="onVoyageComplete">Voyage completion hook, raised once for each voyage the rule ends.</param>
        /// <returns>Voyages the rule moved to Complete or Failed during this check.</returns>
        Task<List<Voyage>> CheckCompletionsAsync(CancellationToken token = default, Func<Voyage, Task>? onVoyageComplete = null);

        /// <summary>
        /// Get progress details for a specific voyage.
        /// </summary>
        /// <param name="voyageId">Voyage identifier.</param>
        /// <param name="tenantId">Optional tenant ID for tenant-scoped reads. Null for system/admin context.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Voyage progress or null if not found.</returns>
        Task<VoyageProgress?> GetProgressAsync(string voyageId, string? tenantId = null, CancellationToken token = default);
    }
}
