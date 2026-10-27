namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// The caller-scoped API over Evaluate (probe-only Quick model probes), shared by REST and MCP so both enforce the
    /// same rules. Evaluating a captain needs a global admin or a tenant admin of the captain's tenant; evaluating a model
    /// endpoint needs read access to it (the same rules as GET /api/v1/model-endpoints/{id}). Reading runs: a global admin
    /// sees every tenant; everyone else sees their own tenant. Captain sessions are refused. Nothing here changes routing
    /// or captain settings. Errors are typed exceptions: <see cref="KeyNotFoundException"/> (missing or outside the
    /// caller's scope), <see cref="ArgumentException"/> (invalid input, including depth Full) and
    /// <see cref="UnauthorizedAccessException"/> (not allowed).
    /// </summary>
    public class ModelProbeAccessService
    {
        #region Private-Members

        private string _Header = "[ModelProbeAccessService] ";
        private readonly DatabaseDriver _Database;
        private readonly ModelProbeService _Service;
        private readonly ModelEndpointService _Endpoints;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="service">Model probe service.</param>
        /// <param name="endpoints">Model endpoint service (visibility rules).</param>
        /// <param name="logging">Logging module.</param>
        /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
        public ModelProbeAccessService(DatabaseDriver database, ModelProbeService service, ModelEndpointService endpoints, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Service = service ?? throw new ArgumentNullException(nameof(service));
            _Endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Start a Quick Evaluate of a captain through its own runtime. Returns the queued run at once.
        /// </summary>
        /// <param name="caller">Caller.</param>
        /// <param name="captainId">Captain identifier (cpt_ prefix).</param>
        /// <param name="depth">Depth; only Quick is implemented.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The queued run.</returns>
        /// <exception cref="ArgumentNullException">Thrown when caller is null.</exception>
        /// <exception cref="ArgumentException">Thrown for depth Full or an unknown depth.</exception>
        /// <exception cref="KeyNotFoundException">Thrown when the captain is missing or in another tenant.</exception>
        /// <exception cref="UnauthorizedAccessException">Thrown for a captain session or a caller who is not an admin.</exception>
        public async Task<ModelProbeRun> EvaluateCaptainAsync(AuthContext caller, string? captainId, ModelProbeDepthEnum depth, CancellationToken token = default)
        {
            RequirePerson(caller);
            ValidateDepth(depth);
            if (String.IsNullOrWhiteSpace(captainId)) throw new KeyNotFoundException("Captain not found.");
            Captain? captain = await _Database.Captains.ReadAsync(captainId.Trim(), token).ConfigureAwait(false);
            if (captain == null || !CanSeeTenant(caller, captain.TenantId)) throw new KeyNotFoundException("Captain not found.");
            if (!caller.IsAdmin && !caller.IsTenantAdmin) throw new UnauthorizedAccessException("Only an admin can evaluate a captain.");

            ModelEndpoint? endpoint = null;
            if (!String.IsNullOrWhiteSpace(captain.ModelEndpointId))
                endpoint = await _Database.ModelEndpoints.ReadAsync(captain.ModelEndpointId!, token).ConfigureAwait(false);

            ModelProbeRun run = await _Service.StartCaptainAsync(captain, endpoint, depth, caller.TenantId, caller.UserId, token).ConfigureAwait(false);
            _Logging.Info(_Header + "Evaluate " + run.Id + " of captain " + captain.Id + " started by " + caller.UserId);
            return await SnapshotAsync(run, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Start a Quick Evaluate of a model endpoint (a transient API-endpoint captain that is never saved). Returns the
        /// queued run at once.
        /// </summary>
        /// <param name="caller">Caller.</param>
        /// <param name="modelEndpointId">Model endpoint identifier (mep_ prefix).</param>
        /// <param name="depth">Depth; only Quick is implemented.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The queued run.</returns>
        /// <exception cref="ArgumentNullException">Thrown when caller is null.</exception>
        /// <exception cref="ArgumentException">Thrown for depth Full or an unknown depth.</exception>
        /// <exception cref="KeyNotFoundException">Thrown when the endpoint is missing or not visible to the caller.</exception>
        /// <exception cref="UnauthorizedAccessException">Thrown for a captain session or a caller without a tenant.</exception>
        public async Task<ModelProbeRun> EvaluateEndpointAsync(AuthContext caller, string? modelEndpointId, ModelProbeDepthEnum depth, CancellationToken token = default)
        {
            RequirePerson(caller);
            ValidateDepth(depth);
            if (String.IsNullOrWhiteSpace(modelEndpointId)) throw new KeyNotFoundException("Model endpoint not found.");
            ModelEndpoint? endpoint = await _Endpoints.ReadAsync(caller, modelEndpointId.Trim(), token).ConfigureAwait(false);
            if (endpoint == null) throw new KeyNotFoundException("Model endpoint not found.");

            ModelProbeRun run = await _Service.StartEndpointAsync(endpoint, depth, caller.TenantId, caller.UserId, token).ConfigureAwait(false);
            _Logging.Info(_Header + "Evaluate " + run.Id + " of model endpoint " + endpoint.Id + " started by " + caller.UserId);
            return await SnapshotAsync(run, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Read one run (with its per-probe results) visible to the caller.
        /// </summary>
        /// <param name="caller">Caller.</param>
        /// <param name="runId">Run identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The run.</returns>
        /// <exception cref="ArgumentNullException">Thrown when caller is null.</exception>
        /// <exception cref="KeyNotFoundException">Thrown when the run is missing or in another tenant.</exception>
        /// <exception cref="UnauthorizedAccessException">Thrown for a captain session or a caller without a tenant.</exception>
        public async Task<ModelProbeRun> GetRunAsync(AuthContext caller, string? runId, CancellationToken token = default)
        {
            RequirePerson(caller);
            if (String.IsNullOrWhiteSpace(runId)) throw new KeyNotFoundException("Model probe run not found.");
            ModelProbeRun? run = await _Service.ReadAsync(runId.Trim(), token).ConfigureAwait(false);
            if (run == null || !CanSeeTenant(caller, run.TenantId)) throw new KeyNotFoundException("Model probe run not found.");
            return run;
        }

        /// <summary>
        /// One page of the runs visible to the caller, newest first, without their per-probe results. A caller other than
        /// a global admin is limited to their own tenant whatever TenantId the query names.
        /// </summary>
        /// <param name="caller">Caller.</param>
        /// <param name="query">Filters and paging, or null for the first page.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The page.</returns>
        /// <exception cref="ArgumentNullException">Thrown when caller is null.</exception>
        /// <exception cref="UnauthorizedAccessException">Thrown for a captain session or a caller without a tenant.</exception>
        public async Task<EnumerationResult<ModelProbeRun>> ListRunsAsync(AuthContext caller, ModelProbeRunQuery? query, CancellationToken token = default)
        {
            RequirePerson(caller);
            ModelProbeRunQuery source = query ?? new ModelProbeRunQuery();
            ModelProbeRunQuery effective = new ModelProbeRunQuery
            {
                TenantId = caller.IsAdmin ? Blank(source.TenantId) : caller.TenantId,
                CaptainId = Blank(source.CaptainId),
                ModelEndpointId = Blank(source.ModelEndpointId),
                Status = source.Status,
                FromUtc = source.FromUtc,
                PageNumber = source.PageNumber,
                PageSize = source.PageSize
            };
            return await _Database.ModelProbeRuns.EnumerateAsync(effective, token).ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        private static void RequirePerson(AuthContext caller)
        {
            if (caller == null) throw new ArgumentNullException(nameof(caller));
            if (!caller.IsAuthenticated) throw new UnauthorizedAccessException("Authentication required.");
            if (!String.IsNullOrEmpty(caller.MissionId) || !String.IsNullOrEmpty(caller.AskThreadId))
                throw new UnauthorizedAccessException("A captain session cannot start or read Evaluate runs.");
            if (!caller.IsAdmin && String.IsNullOrEmpty(caller.TenantId)) throw new UnauthorizedAccessException("A tenant is required.");
        }

        private async Task<ModelProbeRun> SnapshotAsync(ModelProbeRun started, CancellationToken token)
        {
            // The service keeps updating the instance it returned while the probes run in the background; answer with a
            // copy read back from the database so serializing the reply never races with that work.
            ModelProbeRun? copy = await _Service.ReadAsync(started.Id, token).ConfigureAwait(false);
            return copy ?? started;
        }

        private static void ValidateDepth(ModelProbeDepthEnum depth)
        {
            if (!Enum.IsDefined(typeof(ModelProbeDepthEnum), depth)) throw new ArgumentException("depth must be Quick or Full.", nameof(depth));
            if (depth == ModelProbeDepthEnum.Full) throw new ArgumentException(ModelProbeService.FullNotImplementedMessage, nameof(depth));
        }

        private static bool CanSeeTenant(AuthContext caller, string? tenantId)
        {
            if (caller.IsAdmin) return true;
            return !String.IsNullOrEmpty(tenantId) && String.Equals(caller.TenantId, tenantId, StringComparison.Ordinal);
        }

        private static string? Blank(string? value)
        {
            return String.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        #endregion
    }
}
