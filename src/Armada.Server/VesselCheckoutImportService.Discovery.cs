namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Models;

    public sealed partial class VesselCheckoutImportService
    {
        private const int MaxCandidates = 200;

        /// <summary>Discover safe direct-child checkouts from the configured roots.</summary>
        /// <param name="caller">Authenticated caller.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Safe candidate metadata and bounded root errors.</returns>
        public async Task<VesselCheckoutDiscovery> DiscoverAsync(AuthContext caller, CancellationToken token = default)
        {
            await ValidateCallerAsync(caller, token).ConfigureAwait(false);
            List<string> configuredRoots = ReadAllowedRoots();
            VesselCheckoutDiscovery discovery = new VesselCheckoutDiscovery { Configured = configuredRoots.Count > 0 };
            if (!discovery.Configured) return discovery;

            List<string> existingPathIssues = new List<string>();
            Dictionary<string, string> existingPaths = await BuildExistingPathMapAsync(token, existingPathIssues).ConfigureAwait(false);
            if (existingPathIssues.Count > 0)
                AddDiscoveryError(discovery, "Some existing vessel paths could not be canonicalized for duplicate detection.");
            HashSet<string> visitedRoots = new HashSet<string>(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (string configuredRoot in configuredRoots)
            {
                token.ThrowIfCancellationRequested();
                string? rootError = VesselPathPolicy.ValidateCheckoutRoot(configuredRoot, out string canonicalRoot);
                if (rootError != null)
                {
                    AddDiscoveryError(discovery, rootError);
                    continue;
                }
                if (!visitedRoots.Add(canonicalRoot)) continue;
                discovery.Roots.Add(canonicalRoot);

                await ScanRootAsync(canonicalRoot, existingPaths, discovery, token).ConfigureAwait(false);
                if (discovery.Truncated) break;
            }
            return discovery;
        }
    }
}
