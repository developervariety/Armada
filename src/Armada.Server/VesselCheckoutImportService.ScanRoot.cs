namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Security;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Models;

    public sealed partial class VesselCheckoutImportService
    {
        private async Task ScanRootAsync(string canonicalRoot, Dictionary<string, string> existingPaths,
            VesselCheckoutDiscovery discovery, CancellationToken token)
        {
            try
            {
                foreach (string path in Directory.EnumerateDirectories(canonicalRoot))
                {
                    token.ThrowIfCancellationRequested();
                    string? pathError = VesselPathPolicy.ValidateCheckoutPath(canonicalRoot, path, out _, out string checkoutPath);
                    if (pathError != null) continue;
                    if (!Directory.Exists(Path.Combine(checkoutPath, ".git")))
                    {
                        string metadataFile = Path.Combine(checkoutPath, ".git");
                        if (!File.Exists(metadataFile)) continue;
                    }
                    CheckoutMetadata? metadata = await ReadCheckoutMetadataAsync(checkoutPath, token).ConfigureAwait(false);
                    if (metadata == null) continue;
                    if (discovery.Candidates.Count == MaxCandidates)
                    {
                        discovery.Truncated = true;
                        break;
                    }
                    string suggestedName = Path.GetFileName(checkoutPath) ?? String.Empty;
                    existingPaths.TryGetValue(checkoutPath, out string? existingVesselId);
                    discovery.Candidates.Add(new VesselCheckoutCandidate
                    {
                        CheckoutPath = checkoutPath,
                        SuggestedName = suggestedName,
                        OriginUrl = String.IsNullOrWhiteSpace(metadata.OriginUrl) ? null : metadata.OriginUrl,
                        DefaultBranch = metadata.DefaultBranch,
                        ExistingVesselId = existingVesselId
                    });
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
            {
                AddDiscoveryError(discovery, "A configured checkout root cannot be enumerated.");
            }
        }
    }
}
