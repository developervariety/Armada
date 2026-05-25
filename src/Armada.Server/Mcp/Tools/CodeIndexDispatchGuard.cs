namespace Armada.Server.Mcp.Tools
{
    using System;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    internal static class CodeIndexDispatchGuard
    {
        public static async Task<object?> BuildVoyageDispatchBlockedResponseAsync(
            ICodeIndexService? codeIndexService,
            string vesselId,
            string actionName)
        {
            if (codeIndexService == null || String.IsNullOrWhiteSpace(vesselId))
                return null;

            CodeIndexStatus status = await codeIndexService.GetStatusAsync(vesselId).ConfigureAwait(false);
            if (status.UpdateInProgress)
            {
                string vesselName = String.IsNullOrWhiteSpace(status.VesselName) ? vesselId : status.VesselName;
                string started = status.UpdateStartedUtc.HasValue ? status.UpdateStartedUtc.Value.ToString("o") : "unknown time";
                string reason =
                    "Voyage dispatch is blocked because Armada is currently refreshing the code index for vessel "
                    + vesselId + " (" + vesselName + ") since " + started
                    + ". Dispatch is delayed until indexing finishes so generated context packs and search results include the most recently landed code. Retry after codeIndex.updateInProgress is false.";

                return new
                {
                    Error = reason,
                    Code = "code_index_update_in_progress",
                    Reason = reason,
                    Action = actionName,
                    VesselId = vesselId,
                    VesselName = vesselName,
                    CodeIndex = status
                };
            }

            if (IsStale(status))
            {
                string vesselName = String.IsNullOrWhiteSpace(status.VesselName) ? vesselId : status.VesselName;
                string reason =
                    "Voyage dispatch is blocked because Armada's code index is stale for vessel "
                    + vesselId + " (" + vesselName + "). Indexed commit "
                    + (String.IsNullOrWhiteSpace(status.IndexedCommitSha) ? "unknown" : status.IndexedCommitSha)
                    + " does not match current commit "
                    + (String.IsNullOrWhiteSpace(status.CurrentCommitSha) ? "unknown" : status.CurrentCommitSha)
                    + ". Run armada_index_update and retry after codeIndex.freshness is Fresh.";

                return new
                {
                    Error = reason,
                    Code = "code_index_stale",
                    Reason = reason,
                    Action = actionName,
                    VesselId = vesselId,
                    VesselName = vesselName,
                    CodeIndex = status
                };
            }

            return null;
        }

        private static bool IsStale(CodeIndexStatus status)
        {
            if (String.Equals(status.Freshness, "Stale", StringComparison.OrdinalIgnoreCase))
                return true;

            return !String.IsNullOrWhiteSpace(status.IndexedCommitSha)
                && !String.IsNullOrWhiteSpace(status.CurrentCommitSha)
                && !String.Equals(status.IndexedCommitSha, status.CurrentCommitSha, StringComparison.OrdinalIgnoreCase);
        }
    }
}
