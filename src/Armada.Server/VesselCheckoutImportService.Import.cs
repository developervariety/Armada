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
        /// <summary>Register one validated existing checkout as a vessel.</summary>
        /// <param name="caller">Authenticated caller.</param>
        /// <param name="request">Checkout path and vessel fields.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The created vessel. Its LocalPath is null so vessel deletion keeps the external checkout.</returns>
        public async Task<Vessel> ImportAsync(AuthContext caller, VesselCheckoutImportRequest request, CancellationToken token = default)
        {
            await ValidateCallerAsync(caller, token).ConfigureAwait(false);
            if (request == null) throw new ArgumentNullException(nameof(request));
            string? nameError = VesselPathPolicy.ValidateName(request.Name);
            if (nameError != null) throw new ArgumentException(nameError, nameof(request));

            List<string> configuredRoots = ReadAllowedRoots();
            if (configuredRoots.Count == 0) throw new VesselCheckoutImportUnavailableException();
            string? matchedRoot = FindCheckoutRoot(configuredRoots, request.CheckoutPath, out string checkoutPath);
            if (matchedRoot == null) throw new ArgumentException("The checkout path is outside the configured roots or is invalid.", nameof(request));
            CheckoutMetadata? metadata = await ReadCheckoutMetadataAsync(checkoutPath, token).ConfigureAwait(false);
            if (metadata == null) throw new ArgumentException("The path is not the top level of a working-tree Git repository.", nameof(request));

            await _ImportGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await ValidateCallerAsync(caller, token).ConfigureAwait(false);
                configuredRoots = ReadAllowedRoots();
                if (configuredRoots.Count == 0) throw new VesselCheckoutImportUnavailableException();
                matchedRoot = FindCheckoutRoot(configuredRoots, request.CheckoutPath, out checkoutPath);
                if (matchedRoot == null) throw new ArgumentException("The checkout path is outside the configured roots or is invalid.", nameof(request));
                metadata = await ReadCheckoutMetadataAsync(checkoutPath, token).ConfigureAwait(false);
                if (metadata == null) throw new ArgumentException("The path is not the top level of a working-tree Git repository.", nameof(request));

                Dictionary<string, string> existingPaths = await BuildExistingPathMapAsync(token).ConfigureAwait(false);
                if (existingPaths.TryGetValue(checkoutPath, out string? existingVesselId))
                    throw new InvalidOperationException("The checkout is already registered by vessel " + existingVesselId + ".");
                string? fleetId = String.IsNullOrWhiteSpace(request.FleetId) ? null : request.FleetId.Trim();
                if (fleetId != null
                    && await CallerScopedRead.ReadFleetAsync(_Database, caller, fleetId, token).ConfigureAwait(false) == null)
                    throw new ArgumentException("The requested fleet is not available to this caller.", nameof(request));

                Vessel vessel = new Vessel(request.Name, checkoutPath)
                {
                    TenantId = caller.TenantId,
                    UserId = caller.UserId,
                    FleetId = fleetId,
                    RepoUrl = checkoutPath,
                    LocalPath = null,
                    WorkingDirectory = checkoutPath,
                    DefaultBranch = metadata.DefaultBranch
                };
                string? createError = VesselPathPolicy.ValidateCreate(caller, vessel, out bool forbidden);
                if (createError != null)
                {
                    if (forbidden) throw new UnauthorizedAccessException(createError);
                    throw new ArgumentException(createError, nameof(request));
                }
                return await _Database.Vessels.CreateAsync(vessel, token).ConfigureAwait(false);
            }
            finally
            {
                _ImportGate.Release();
            }
        }
    }
}
