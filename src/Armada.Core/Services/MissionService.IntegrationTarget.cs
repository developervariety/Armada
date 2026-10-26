namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    public partial class MissionService
    {
        private async Task<bool?> TryIntegrationAncestryAsync(
            Mission mission, Vessel vessel, string ancestor, string target, string entityId, CancellationToken token)
        {
            try
            {
                return await _Git!.TryIsAncestorAsync(vessel.LocalPath!, ancestor, target, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "integration barrier ancestry proof failed for " + entityId + ": " + ex.Message);
                await HoldForAssignmentAsync(mission, IntegrationBarrierUnverifiable, entityId,
                    "A source commit ancestry check could not be completed. The integration mission will not start.", token).ConfigureAwait(false);
                return null;
            }
        }

        private async Task<string?> ResolveIntegrationTargetShaAsync(
            Mission mission, Vessel vessel, CancellationToken token)
        {
            if (_Git == null || String.IsNullOrWhiteSpace(vessel.LocalPath)
                || String.IsNullOrWhiteSpace(vessel.DefaultBranch))
            {
                await HoldForAssignmentAsync(mission, IntegrationBarrierUnverifiable, mission.VesselId,
                    "The configured landing target repository or branch is unavailable. The integration mission will not start.", token).ConfigureAwait(false);
                return null;
            }

            string targetRef = "refs/heads/" + vessel.DefaultBranch;
            try
            {
                await _Git.FetchAsync(vessel.LocalPath!, token).ConfigureAwait(false);
                string? targetSha = await _Git.GetRevisionCommitShaAsync(vessel.LocalPath!, targetRef, token).ConfigureAwait(false);
                if (!String.IsNullOrWhiteSpace(targetSha)) return targetSha;
                await HoldForAssignmentAsync(mission, IntegrationBarrierUnverifiable, targetRef,
                    "The configured landing target commit cannot be resolved. The integration mission will not start.", token).ConfigureAwait(false);
                return null;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "integration barrier could not resolve landing target " + targetRef + ": " + ex.Message);
                await HoldForAssignmentAsync(mission, IntegrationBarrierUnverifiable, targetRef,
                    "The configured landing target commit could not be read. The integration mission will not start.", token).ConfigureAwait(false);
                return null;
            }
        }
    }
}
