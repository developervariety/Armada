namespace Armada.Server.Mcp.Tools
{
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
            if (!status.UpdateInProgress)
                return null;

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
    }
}
