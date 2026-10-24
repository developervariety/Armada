namespace Armada.Server.Harbor
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public sealed partial class DatabaseHarborWorkspaceBindingVerifier
    {
        private async Task<bool?> HasUnconfirmedHarborJobAsync(Dock dock, CancellationToken token)
        {
            try
            {
                System.Collections.Generic.List<HarborJobRecord> jobs = await _Database.HarborJobs.EnumerateAsync(new HarborJobQuery
                {
                    DockId = dock.Id, UnconfirmedOnly = true, Limit = 1
                }, token).ConfigureAwait(false);
                if (jobs.Count == 0)
                {
                    jobs = await _Database.HarborJobs.EnumerateAsync(new HarborJobQuery
                    {
                        TenantId = dock.TenantId, UnboundMissionOnly = true, UnconfirmedOnly = true, Limit = 1
                    }, token).ConfigureAwait(false);
                }
                return jobs.Count > 0;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool ValidOperationKey(string? operationKey)
        {
            if (String.IsNullOrWhiteSpace(operationKey) || operationKey.Length > 200) return false;
            foreach (char character in operationKey)
                if (Char.IsControl(character)) return false;
            return true;
        }
    }
}
