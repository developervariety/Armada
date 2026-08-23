namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using SyslogLogging;

    /// <summary>
    /// Shared voyage dispatch orchestration used by MCP armada_dispatch and REST voyage creation.
    /// </summary>
    public sealed class VoyageDispatchService
    {
        #region Private-Members

        private readonly DatabaseDriver _Database;
        private readonly IAdmiralService _Admiral;
        private readonly LoggingModule? _Logging;
        private readonly ICodeIndexService? _CodeIndexService;
        private readonly ObjectiveService? _ObjectiveService;
        private readonly ArmadaSettings? _Settings;

        private const string _CodeContextDestPath = "_briefing/context-pack.md";
        private const string _CodeContextModeAuto = "auto";
        private const string _CodeContextModeOff = "off";
        private const string _CodeContextModeForce = "force";
        private const int _DefaultCodeContextTokenBudget = 5000;
        #endregion

        #region Private-Types

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="admiral">Admiral orchestration service.</param>
        /// <param name="logging">Optional logging module.</param>
        /// <param name="codeIndexService">Optional code-index service.</param>
        /// <param name="objectiveService">Optional objective service.</param>
        /// <param name="settings">Optional Armada settings.</param>
        public VoyageDispatchService(
            DatabaseDriver database,
            IAdmiralService admiral,
            LoggingModule? logging = null,
            ICodeIndexService? codeIndexService = null,
            ObjectiveService? objectiveService = null,
            ArmadaSettings? settings = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Admiral = admiral ?? throw new ArgumentNullException(nameof(admiral));
            _Logging = logging;
            _CodeIndexService = codeIndexService;
            _ObjectiveService = objectiveService;
            _Settings = settings;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Dispatch a voyage using the shared REST and MCP orchestration path.
        /// </summary>
        /// <param name="request">Dispatch request.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Dispatch result.</returns>
        /// <summary>
        /// Run every cheap, deterministic precondition a dispatch must satisfy and return the failing
        /// result, or null when the request is dispatchable. Callers that hand dispatch to a background
        /// job call this FIRST so a bad request still fails fast and specifically, instead of being
        /// accepted as a job the caller must poll only to discover the vesselId was a typo.
        /// <see cref="DispatchAsync"/> also calls it, so validation has exactly one implementation.
        /// </summary>
        /// <param name="request">Dispatch request to validate.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The failing result, or null when all preconditions pass.</returns>
        public async Task<VoyageDispatchResult?> ValidatePreconditionsAsync(SharedVoyageDispatchRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            VoyageDispatchResult? validation = ValidateRequest(request.Title, request.Missions);
            if (validation != null) return validation;

            string vesselId = request.VesselId;
            Vessel? dispatchVessel = await _Database.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);
            if (dispatchVessel == null) return VoyageDispatchResult.NotFound(new
            {
                Error = "Vessel not found: " + vesselId,
                Code = "vessel_not_found",
                Reason = "Vessel " + vesselId + " does not exist in this admiral.",
                Action = "Register the vessel via armada_add_vessel or verify the vesselId.",
                VesselId = vesselId
            });

            VoyageDispatchResult? objectiveValidation = await ValidateObjectiveAsync(
                NormalizeEmpty(request.ObjectiveId), request.ObjectiveAuthContext).ConfigureAwait(false);
            if (objectiveValidation != null) return objectiveValidation;

            if (IsCodeIndexEnabled() && ShouldEvaluateCodeIndexPrecondition(request))
            {
                object? blockedByIndex = await CodeIndexDispatchGuard.BuildVoyageDispatchBlockedResponseAsync(
                    _CodeIndexService,
                    vesselId,
                    "armada_dispatch",
                    LogCodeContextWarning,
                    token).ConfigureAwait(false);
                if (blockedByIndex != null) return VoyageDispatchResult.BadRequest(blockedByIndex);
            }
            else
            {
                LogCodeContextInfo(
                    "code index dispatch precondition skipped for vessel " + vesselId
                    + (IsCodeIndexEnabled()
                        ? " because every effective mission mode is off"
                        : " because code indexing is disabled"));
            }

            string? resolvedPipelineId = await ResolvePipelineIdAsync(request.PipelineId, request.Pipeline).ConfigureAwait(false);
            if (String.Equals(resolvedPipelineId, "__pipeline_not_found__", StringComparison.Ordinal))
            {
                return VoyageDispatchResult.BadRequest(new
                {
                    Error = "Pipeline not found: " + request.Pipeline,
                    Code = "pipeline_not_found",
                    Reason = "Pipeline named \"" + request.Pipeline + "\" does not exist in this admiral.",
                    Action = "Verify the pipeline name via armada_enumerate(entityType=\"pipelines\").",
                    Pipeline = request.Pipeline
                });
            }

            return null;
        }

        public async Task<VoyageDispatchResult> DispatchAsync(SharedVoyageDispatchRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            string title = request.Title;
            string description = request.Description ?? "";
            string vesselId = request.VesselId;
            List<MissionDescription> missions = request.Missions!;
            List<SelectedPlaybook> callerPlaybooks = request.SelectedPlaybooks ?? new List<SelectedPlaybook>();
            string? objectiveId = NormalizeEmpty(request.ObjectiveId);

            // Per-step elapsed logging: a prior regression let dispatch hang with no voyage and no log
            // line at all, making the stalled step invisible. These markers are cheap and pinpoint which
            // step is slow if a future stall recurs.
            Stopwatch dispatchWatch = Stopwatch.StartNew();
            LogDispatchInfo("dispatch start vessel " + vesselId + " missions=" + (missions?.Count ?? 0));

            VoyageDispatchResult? preconditions = await ValidatePreconditionsAsync(request, token).ConfigureAwait(false);
            if (preconditions != null) return preconditions;
            LogDispatchInfo("dispatch step preconditions_ok elapsedMs=" + dispatchWatch.ElapsedMilliseconds);

            Vessel? dispatchVessel = await _Database.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);
            if (dispatchVessel == null) return VoyageDispatchResult.NotFound(new
            {
                Error = "Vessel not found: " + vesselId,
                Code = "vessel_not_found",
                Reason = "Vessel " + vesselId + " does not exist in this admiral.",
                Action = "Register the vessel via armada_add_vessel or verify the vesselId.",
                VesselId = vesselId
            });

            List<SelectedPlaybook> mergedPlaybooks = PlaybookMerge.MergeWithVesselDefaults(dispatchVessel.GetDefaultPlaybooks(), callerPlaybooks);

            string? pipelineId = await ResolvePipelineIdAsync(request.PipelineId, request.Pipeline).ConfigureAwait(false);

            string? codeContextError = await PrepareDispatchCodeContextAsync(
                vesselId,
                request.CodeContextMode,
                request.CodeContextTokenBudget,
                request.CodeContextMaxResults,
                missions).ConfigureAwait(false);
            LogDispatchInfo("dispatch step code_context_prepared elapsedMs=" + dispatchWatch.ElapsedMilliseconds
                + " deterministic=true");
            if (codeContextError != null) return VoyageDispatchResult.BadRequest(new { Error = codeContextError });

            bool hasAliases = missions.Any(m =>
                !String.IsNullOrEmpty(m.Alias) || !String.IsNullOrEmpty(m.DependsOnMissionAlias));
            object dispatchResult;
            if (hasAliases)
            {
                dispatchResult = await DispatchWithAliasesAsync(
                    title,
                    description,
                    vesselId,
                    dispatchVessel,
                    missions,
                    mergedPlaybooks,
                    pipelineId,
                    request.Settings ?? _Settings).ConfigureAwait(false);
            }
            else
            {
                dispatchResult = await _Admiral.DispatchVoyageQueuedAsync(
                    title,
                    description,
                    vesselId,
                    missions,
                    pipelineId,
                    mergedPlaybooks,
                    token).ConfigureAwait(false);
            }

            if (dispatchResult is not Voyage voyage)
            {
                return VoyageDispatchResult.BadRequest(dispatchResult);
            }

            await LinkObjectiveToVoyageAsync(objectiveId, request.ObjectiveAuthContext, voyage).ConfigureAwait(false);

            // Persist per-persona captain overrides so assignment resolves the preferred captain and
            // fallback tier for every mission of a step, including fan-out missions created later. Both
            // the REST and MCP dispatch paths reach this one seam, so the overrides cannot be stored by
            // one caller and silently dropped by the other.
            if (request.CaptainAssignments != null && request.CaptainAssignments.Count > 0)
            {
                voyage.CaptainOverridesJson = MissionService.SerializeCaptainOverrides(request.CaptainAssignments);
                voyage = await _Database.Voyages.UpdateAsync(voyage).ConfigureAwait(false);
            }

            // Arm the voyage's own Checks here, in the same action as the dispatch, so the voyage
            // carries a standing record of which gates it wants. The records are intent markers:
            // they are executed once the voyage completes, and they do not stand in for the real
            // Build and UnitTest signal the Judge gate requires against the mission's own branch.
            await ArmVoyageChecksAsync(voyage, dispatchVessel, token).ConfigureAwait(false);

            LogDispatchInfo("dispatch complete voyage " + voyage.Id + " totalMs=" + dispatchWatch.ElapsedMilliseconds);
            return VoyageDispatchResult.Success(voyage);
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Attach Pending Build and UnitTest Checks to a freshly dispatched voyage.
        /// </summary>
        /// <remarks>
        /// The Checks are armed Pending, not executed: at dispatch there is no branch and no commit
        /// to measure. They are intent markers, executed after the voyage completes, and they carry
        /// no weight in the real-signal gate -- a Judge PASS still needs Checks that actually ran
        /// against the work. Arming costs nothing now; running a full suite at dispatch would
        /// instead load the host at the moment the first captain starts working.
        /// <para>
        /// Arming never fails a dispatch. A voyage that exists without its Checks can still be
        /// armed by hand, whereas refusing to dispatch over a Check record would turn a
        /// convenience into an outage.
        /// </para>
        /// </remarks>
        private async Task ArmVoyageChecksAsync(Voyage voyage, Vessel vessel, CancellationToken token)
        {
            try
            {
                VoyageCheckArmingSettings? arming = _Settings?.VoyageCheckArming;
                if (arming == null || !arming.Enabled) return;

                AuthContext auth = AuthContext.Authenticated(
                    voyage.TenantId ?? vessel.TenantId ?? "default",
                    voyage.UserId ?? "default",
                    true,
                    true,
                    "system");

                WorkflowProfileService profiles = new WorkflowProfileService(_Database, _Logging!);
                WorkflowProfile? profile = await profiles.ResolveForVesselAsync(auth, vessel, null, token).ConfigureAwait(false);

                IReadOnlyList<CheckRunTypeEnum> planned = VoyageCheckArmingPlan.Resolve(arming, profile, null);
                if (planned.Count == 0)
                {
                    LogDispatchInfo("dispatch step checks_armed voyage " + voyage.Id + " armed=0"
                        + (profile == null ? " reason=no_workflow_profile" : " reason=no_matching_commands"));
                    return;
                }

                foreach (CheckRunTypeEnum type in planned)
                {
                    CheckRun run = new CheckRun
                    {
                        TenantId = voyage.TenantId,
                        UserId = voyage.UserId,
                        VesselId = vessel.Id,
                        VoyageId = voyage.Id,
                        WorkflowProfileId = profile?.Id,
                        Type = type,
                        Source = CheckRunSourceEnum.Armada,
                        Status = CheckRunStatusEnum.Pending,
                        Label = type.ToString() + " (armed at dispatch)"
                    };

                    await _Database.CheckRuns.CreateAsync(run, token).ConfigureAwait(false);
                }

                LogDispatchInfo("dispatch step checks_armed voyage " + voyage.Id + " armed=" + planned.Count);
            }
            catch (Exception ex)
            {
                _Logging?.Warn("[VoyageDispatchService] could not arm Checks for voyage " + voyage.Id + ": " + ex.Message);
            }
        }

        private static VoyageDispatchResult? ValidateRequest(string title, List<MissionDescription>? missions)
        {
            if (String.IsNullOrWhiteSpace(title)) return VoyageDispatchResult.BadRequest(new
            {
                Error = "armada_dispatch requires a non-empty title.",
                Code = "missing_title",
                Reason = "The voyage title was null or whitespace.",
                Action = "Provide a non-empty title naming the voyage."
            });

            if (missions == null || missions.Count == 0) return VoyageDispatchResult.BadRequest(new
            {
                Error = "armada_dispatch requires a non-empty missions array; each mission needs a title and description.",
                Code = "missing_missions",
                Reason = "The missions array was null or empty.",
                Action = "Provide at least one mission, each with a title and a description."
            });

            for (int i = 0; i < missions.Count; i++)
            {
                MissionDescription mission = missions[i];
                int missionNumber = i + 1;

                if (String.IsNullOrWhiteSpace(mission.Title)) return VoyageDispatchResult.BadRequest(new
                {
                    Error = "armada_dispatch mission " + missionNumber + " is missing a title.",
                    Code = "missing_mission_title",
                    Reason = "Mission " + missionNumber + " had a null or whitespace title.",
                    Action = "Provide a non-empty title for mission " + missionNumber + "."
                });

                if (String.IsNullOrWhiteSpace(mission.Description)) return VoyageDispatchResult.BadRequest(new
                {
                    Error = "armada_dispatch mission " + missionNumber + " is missing a description.",
                    Code = "missing_mission_description",
                    Reason = "Mission " + missionNumber + " had a null or whitespace description.",
                    Action = "Provide a non-empty description for mission " + missionNumber + "."
                });

                // Reject an unrecognized mode rather than parsing it down to Implementation. A typo
                // such as "audits" would otherwise produce an implementing mission that is judged by
                // the commit gate, which is the exact failure mode modes exist to remove.
                if (!String.IsNullOrWhiteSpace(mission.Mode) && !Armada.Core.Enums.MissionModes.IsKnown(mission.Mode))
                    return VoyageDispatchResult.BadRequest(new
                    {
                        Error = "armada_dispatch mission " + missionNumber + " has an unknown mode: " + mission.Mode + ".",
                        Code = "invalid_mission_mode",
                        Reason = "Mission " + missionNumber + " requested mode '" + mission.Mode + "', which Armada does not recognize.",
                        Action = "Use Implementation, Audit, or Research, or omit mode to default to Implementation."
                    });
            }

            return null;
        }

        private static bool ShouldEvaluateCodeIndexPrecondition(SharedVoyageDispatchRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            string dispatchMode = String.IsNullOrWhiteSpace(request.CodeContextMode)
                ? _CodeContextModeAuto
                : request.CodeContextMode.Trim();

            foreach (MissionDescription mission in request.Missions)
            {
                string effectiveMode = String.IsNullOrWhiteSpace(mission.CodeContextMode)
                    ? dispatchMode
                    : mission.CodeContextMode.Trim();
                if (!String.Equals(effectiveMode, _CodeContextModeOff, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private bool IsCodeIndexEnabled()
        {
            return _Settings?.CodeIndex?.Enabled ?? true;
        }

        private async Task<VoyageDispatchResult?> ValidateObjectiveAsync(string? objectiveId, AuthContext? authContext)
        {
            if (String.IsNullOrEmpty(objectiveId)) return null;
            if (_ObjectiveService == null)
                return VoyageDispatchResult.BadRequest(new { Error = "Objective service unavailable; cannot link objectiveId " + objectiveId });

            AuthContext auth = authContext ?? McpToolHelpers.CreateDefaultTenantAdminContext();
            Objective? objective = await _ObjectiveService.ReadAsync(auth, objectiveId).ConfigureAwait(false);
            if (objective == null)
                return VoyageDispatchResult.NotFound(new { Error = "Objective not found: " + objectiveId });

            return null;
        }

        private async Task<string?> ResolvePipelineIdAsync(string? requestedPipelineId, string? requestedPipeline)
        {
            string? pipelineId = requestedPipelineId;
            if (String.IsNullOrEmpty(pipelineId) && !String.IsNullOrEmpty(requestedPipeline))
            {
                Pipeline? namedPipeline = await _Database.Pipelines.ReadByNameAsync(requestedPipeline).ConfigureAwait(false);
                if (namedPipeline != null) pipelineId = namedPipeline.Id;
                else return "__pipeline_not_found__";
            }

            return pipelineId;
        }

        private async Task<string?> PrepareDispatchCodeContextAsync(
            string vesselId,
            string? topLevelMode,
            int? tokenBudget,
            int? maxResults,
            List<MissionDescription> missions)
        {
            if (missions == null || missions.Count == 0) return null;

            string dispatchMode;
            if (!TryNormalizeCodeContextMode(topLevelMode, _CodeContextModeAuto, out dispatchMode))
                return "invalid codeContextMode: " + topLevelMode + ". Expected auto, off, or force.";

            if (!IsCodeIndexEnabled())
            {
                if (String.Equals(dispatchMode, _CodeContextModeForce, StringComparison.Ordinal)
                    || missions.Any(m => m != null
                        && String.Equals(m.CodeContextMode?.Trim(), _CodeContextModeForce, StringComparison.OrdinalIgnoreCase)))
                {
                    return "code context force requested but code indexing is disabled";
                }

                LogCodeContextInfo("code context preparation skipped because code indexing is disabled");
                return null;
            }

            bool requireContextPackWhenEnabled = _Settings?.CodeIndex?.RequireContextPackWhenEnabled ?? true;
            bool loggedUnavailable = false;
            for (int i = 0; i < missions.Count; i++)
            {
                MissionDescription mission = missions[i];
                if (mission == null) continue;

                string mode;
                if (!TryNormalizeCodeContextMode(mission.CodeContextMode, dispatchMode, out mode))
                    return "invalid codeContextMode for mission '" + mission.Title + "': " + mission.CodeContextMode + ". Expected auto, off, or force.";

                if (String.Equals(mode, _CodeContextModeOff, StringComparison.Ordinal))
                    continue;

                string query = BuildMissionCodeContextQuery(mission);
                if (String.IsNullOrWhiteSpace(query))
                {
                    if (String.Equals(mode, _CodeContextModeForce, StringComparison.Ordinal))
                        return "code context force requested for mission '" + mission.Title + "' but no query could be built";

                    if (requireContextPackWhenEnabled)
                        return "code context required for mission '" + mission.Title + "' but no query could be built (provide CodeContextQuery, title, or description)";

                    LogCodeContextWarning("skipping code context for mission '" + mission.Title + "' because no query could be built");
                    continue;
                }

                if (_CodeIndexService == null)
                {
                    if (String.Equals(mode, _CodeContextModeForce, StringComparison.Ordinal))
                        return "code context force requested but code index service is unavailable";

                    if (requireContextPackWhenEnabled)
                        return "code context required for mission '" + mission.Title + "' but the code index service is unavailable";

                    if (!loggedUnavailable)
                    {
                        LogCodeContextWarning("code index service is unavailable; dispatch will continue without auto code context");
                        loggedUnavailable = true;
                    }
                    continue;
                }

                ContextPackRequest contextRequest = new ContextPackRequest
                {
                    VesselId = vesselId,
                    Goal = query,
                    TokenBudget = tokenBudget ?? _DefaultCodeContextTokenBudget,
                    MaxResults = maxResults
                };

                try
                {
                    Stopwatch totalWatch = Stopwatch.StartNew();
                    LogCodeContextInfo(
                        "code context phase start for mission '" + mission.Title + "' vessel " + vesselId
                        + " mode=" + mode + " timeoutMs=" + ((int)GetCodeContextTimeout().TotalMilliseconds));
                    ContextPackResponse? cached = await RunCodeIndexCallWithTimeoutAsync(
                        "code context cache probe",
                        cancellationToken => _CodeIndexService.TryGetCachedContextPackAsync(contextRequest, cancellationToken))
                        .ConfigureAwait(false);

                    if (cached != null && cached.PrestagedFiles != null && cached.PrestagedFiles.Count > 0)
                    {
                        totalWatch.Stop();
                        LogCodeContextInfo(
                            "code context for mission '" + mission.Title + "': cache_hit"
                            + " totalMs=" + totalWatch.ElapsedMilliseconds
                            + " cacheKey=" + (cached.Metrics?.CacheKey ?? "unknown"));
                        MergeGeneratedPrestagedFiles(mission, cached.PrestagedFiles);
                        continue;
                    }

                    LogCodeContextInfo(
                        "code context for mission '" + mission.Title + "': cache_miss; warming baseline cache for vessel " + vesselId);
                    await RunCodeIndexCallWithTimeoutAsync(
                        "code context baseline warm",
                        async cancellationToken =>
                        {
                            await _CodeIndexService.WarmBaselineCacheAsync(vesselId, cancellationToken).ConfigureAwait(false);
                            return true;
                        }).ConfigureAwait(false);

                    cached = await RunCodeIndexCallWithTimeoutAsync(
                        "code context cache probe after warm",
                        cancellationToken => _CodeIndexService.TryGetCachedContextPackAsync(contextRequest, cancellationToken))
                        .ConfigureAwait(false);
                    if (cached != null && cached.PrestagedFiles != null && cached.PrestagedFiles.Count > 0)
                    {
                        totalWatch.Stop();
                        LogCodeContextInfo(
                            "code context for mission '" + mission.Title + "': cache_hit_after_warm"
                            + " totalMs=" + totalWatch.ElapsedMilliseconds
                            + " cacheKey=" + (cached.Metrics?.CacheKey ?? "unknown"));
                        MergeGeneratedPrestagedFiles(mission, cached.PrestagedFiles);
                        continue;
                    }

                    ContextPackResponse contextPack = await BuildContextPackWithTimeoutAsync(_CodeIndexService, contextRequest)
                        .ConfigureAwait(false);
                    totalWatch.Stop();
                    TimeSpan usedTimeout = GetCodeContextTimeout();
                    LogCodeContextInfo(
                        "code context for mission '" + mission.Title + "': cache_miss"
                        + " totalMs=" + totalWatch.ElapsedMilliseconds
                        + " searchMs=" + contextPack.Metrics?.SearchElapsedMs
                        + " summarizerMs=" + contextPack.Metrics?.SummarizerElapsedMs
                        + " timeoutMs=" + (int)usedTimeout.TotalMilliseconds);

                    if (contextPack.PrestagedFiles == null || contextPack.PrestagedFiles.Count == 0)
                    {
                        if (String.Equals(mode, _CodeContextModeForce, StringComparison.Ordinal) || requireContextPackWhenEnabled)
                            return "code context generation returned no prestaged files for mission '" + mission.Title + "'";

                        LogCodeContextWarning("code context generation returned no prestaged files for mission '" + mission.Title + "'");
                        continue;
                    }

                    MergeGeneratedPrestagedFiles(mission, contextPack.PrestagedFiles);
                }
                catch (TimeoutException ex)
                {
                    // A stalled index/embedding backend must never block dispatch. Force/require
                    // callers get a specific actionable error; auto callers degrade to no pack.
                    // Keep the established "code context generation failed" prefix -- it is an error
                    // contract callers match on -- and append the actionable timeout remedy.
                    if (String.Equals(mode, _CodeContextModeForce, StringComparison.Ordinal) || requireContextPackWhenEnabled)
                        return "code context generation failed for mission '" + mission.Title + "': " + ex.Message
                            + " (raise " + CodeContextTimeouts.TimeoutEnvVar + " or dispatch with codeContextMode=off)";

                    LogCodeContextWarning(
                        "code context phase end for mission '" + mission.Title + "': DEGRADED to no pack after timeout -- "
                        + ex.Message + "; dispatch continues");
                }
                catch (Exception ex)
                {
                    if (String.Equals(mode, _CodeContextModeForce, StringComparison.Ordinal) || requireContextPackWhenEnabled)
                        return "code context generation failed for mission '" + mission.Title + "': " + ex.Message;

                    LogCodeContextWarning("code context generation failed for mission '" + mission.Title + "': " + ex.Message);
                }
            }

            return null;
        }

        private static async Task<ContextPackResponse> BuildContextPackWithTimeoutAsync(
            ICodeIndexService codeIndexService,
            ContextPackRequest contextRequest)
        {
            TimeSpan timeout = GetCodeContextTimeout();
            CancellationTokenSource timeoutCts = new CancellationTokenSource();
            Task<ContextPackResponse> buildTask;

            try
            {
                buildTask = codeIndexService.BuildContextPackAsync(contextRequest, timeoutCts.Token);
            }
            catch
            {
                timeoutCts.Dispose();
                throw;
            }

            Task completed = await Task.WhenAny(buildTask, Task.Delay(timeout)).ConfigureAwait(false);
            if (completed != buildTask)
            {
                try { timeoutCts.Cancel(); }
                catch (ObjectDisposedException) { }

                _ = buildTask.ContinueWith(
                    task =>
                    {
                        _ = task.Exception;
                        timeoutCts.Dispose();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                throw new TimeoutException(
                    "code context generation exceeded " + timeout.TotalSeconds.ToString("F0") + " seconds");
            }

            try
            {
                return await buildTask.ConfigureAwait(false);
            }
            finally
            {
                timeoutCts.Dispose();
            }
        }

        private static TimeSpan GetCodeContextTimeout()
        {
            return CodeContextTimeouts.Resolve(CodeContextTimeouts.DefaultDispatchTimeoutMs);
        }

        /// <summary>
        /// Bound an arbitrary code-index call on the dispatch path with the shared code-context
        /// timeout. Only BuildContextPackAsync was previously time-boxed; the cache-probe, baseline
        /// warm, and index-status calls ran with no timeout and no token, so a stalled index or
        /// embedding backend blocked armada_dispatch indefinitely with no voyage and no log line.
        /// </summary>
        /// <typeparam name="T">Result type of the bounded operation.</typeparam>
        /// <param name="operationName">Short name used in the timeout message and logs.</param>
        /// <param name="operation">Factory receiving the cancellation token to pass through.</param>
        /// <returns>The operation result.</returns>
        /// <exception cref="TimeoutException">Thrown when the operation exceeds the resolved bound.</exception>
        private static async Task<T> RunCodeIndexCallWithTimeoutAsync<T>(
            string operationName,
            Func<CancellationToken, Task<T>> operation)
        {
            TimeSpan timeout = GetCodeContextTimeout();
            CancellationTokenSource timeoutCts = new CancellationTokenSource();
            Task<T> operationTask;

            try
            {
                operationTask = operation(timeoutCts.Token);
            }
            catch
            {
                timeoutCts.Dispose();
                throw;
            }

            Task completed = await Task.WhenAny(operationTask, Task.Delay(timeout)).ConfigureAwait(false);
            if (completed != operationTask)
            {
                try { timeoutCts.Cancel(); }
                catch (ObjectDisposedException) { }

                // Observe the abandoned task's exception so it does not surface as unobserved.
                _ = operationTask.ContinueWith(
                    task =>
                    {
                        _ = task.Exception;
                        timeoutCts.Dispose();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                throw new TimeoutException(
                    operationName + " exceeded " + timeout.TotalSeconds.ToString("F0") + " seconds");
            }

            try
            {
                return await operationTask.ConfigureAwait(false);
            }
            finally
            {
                timeoutCts.Dispose();
            }
        }

        private static bool TryNormalizeCodeContextMode(string? value, string fallback, out string normalized)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                normalized = fallback;
                return true;
            }

            string candidate = value.Trim().ToLowerInvariant();
            if (String.Equals(candidate, _CodeContextModeAuto, StringComparison.Ordinal)
                || String.Equals(candidate, _CodeContextModeOff, StringComparison.Ordinal)
                || String.Equals(candidate, _CodeContextModeForce, StringComparison.Ordinal))
            {
                normalized = candidate;
                return true;
            }

            normalized = fallback;
            return false;
        }

        private static string BuildMissionCodeContextQuery(MissionDescription mission)
        {
            if (!String.IsNullOrWhiteSpace(mission.CodeContextQuery))
                return mission.CodeContextQuery.Trim();

            string title = mission.Title ?? "";
            string description = mission.Description ?? "";
            if (String.IsNullOrWhiteSpace(description)) return title.Trim();
            if (String.IsNullOrWhiteSpace(title)) return description.Trim();
            return title.Trim() + "\n\n" + description.Trim();
        }

        private void MergeGeneratedPrestagedFiles(MissionDescription mission, List<PrestagedFile> generatedFiles)
        {
            if (generatedFiles == null || generatedFiles.Count == 0) return;

            List<PrestagedFile> merged = mission.PrestagedFiles ?? new List<PrestagedFile>();
            foreach (PrestagedFile generated in generatedFiles)
            {
                if (generated == null) continue;

                bool duplicateDest = false;
                foreach (PrestagedFile existing in merged)
                {
                    if (existing == null) continue;
                    if (String.Equals(existing.DestPath, generated.DestPath, StringComparison.Ordinal))
                    {
                        duplicateDest = true;
                        break;
                    }
                }

                if (duplicateDest)
                {
                    LogCodeContextWarning("skipping generated code context prestaged file because destPath already exists: " + generated.DestPath);
                    continue;
                }

                merged.Add(new PrestagedFile(generated.SourcePath ?? "", generated.DestPath ?? _CodeContextDestPath));
            }

            mission.PrestagedFiles = merged.Count > 0 ? merged : null;
        }

        private void MergeGeneratedPrestagedFiles(Mission mission, List<PrestagedFile> generatedFiles)
        {
            if (generatedFiles == null || generatedFiles.Count == 0) return;

            List<PrestagedFile> merged = mission.PrestagedFiles ?? new List<PrestagedFile>();
            foreach (PrestagedFile generated in generatedFiles)
            {
                if (generated == null) continue;

                bool duplicateDest = false;
                foreach (PrestagedFile existing in merged)
                {
                    if (existing == null) continue;
                    if (String.Equals(existing.DestPath, generated.DestPath, StringComparison.Ordinal))
                    {
                        duplicateDest = true;
                        break;
                    }
                }

                if (duplicateDest)
                {
                    LogCodeContextWarning("skipping generated code context prestaged file because destPath already exists: " + generated.DestPath);
                    continue;
                }

                merged.Add(new PrestagedFile(generated.SourcePath ?? "", generated.DestPath ?? _CodeContextDestPath));
            }

            mission.PrestagedFiles = merged.Count > 0 ? merged : null;
        }

        private void LogCodeContextWarning(string message)
        {
            if (_Logging == null) return;
            _Logging.Warn("[VoyageDispatchService] " + message);
        }

        private void LogCodeContextInfo(string message)
        {
            if (_Logging == null) return;
            _Logging.Info("[VoyageDispatchService] " + message);
        }

        private void LogDispatchInfo(string message)
        {
            if (_Logging == null) return;
            _Logging.Info("[VoyageDispatchService] " + message);
        }

        private static string? NormalizeEmpty(string? value)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            return value.Trim();
        }

        private async Task LinkObjectiveToVoyageAsync(string? objectiveId, AuthContext? authContext, Voyage voyage)
        {
            if (String.IsNullOrEmpty(objectiveId)) return;
            if (_ObjectiveService == null) return;

            AuthContext auth = authContext ?? McpToolHelpers.CreateDefaultTenantAdminContext();
            try
            {
                await _ObjectiveService.LinkVoyageAsync(auth, objectiveId, voyage.Id).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // This runs AFTER the voyage exists and its missions are dispatched. Letting a
                // bookkeeping failure escape here turned a live voyage into a generic internal error
                // (HTTP 500 / JSON-RPC -32603) with no voyage id returned to the caller, so the work
                // was running but unreachable. Record the voyage; report the link failure separately.
                _Logging?.Warn("[VoyageDispatchService] voyage " + voyage.Id + " dispatched but could not be linked to objective " +
                    objectiveId + ": " + ex.Message);
            }
        }

        private async Task<object> DispatchWithAliasesAsync(
            string title,
            string description,
            string vesselId,
            Vessel? vessel,
            List<MissionDescription> missions,
            List<SelectedPlaybook> selectedPlaybooks,
            string? pipelineId,
            ArmadaSettings? settings = null)
        {
            if (vessel == null)
                return new { Error = "Vessel not found: " + vesselId };

            IReadOnlyList<MissionDescription> sortedMissions;
            try
            {
                sortedMissions = MissionAliasResolver.ResolveAndOrder(missions);
            }
            catch (InvalidDataException ex)
            {
                return new { Error = ex.Message };
            }

            Pipeline? pipeline = await _Admiral.ResolvePipelineAsync(pipelineId, vessel).ConfigureAwait(false);
            bool isMultiStage = pipeline != null
                && !(pipeline.Stages.Count == 1 && pipeline.Stages[0].PersonaName == "Worker");

            Voyage voyage = new Voyage(title, description);
            voyage.TenantId = vessel.TenantId;
            voyage.UserId = vessel.UserId;
            voyage.Status = VoyageStatusEnum.Open;
            voyage = await _Database.Voyages.CreateAsync(voyage).ConfigureAwait(false);
            voyage.SelectedPlaybooks = ClonePlaybookSelectionsLocal(selectedPlaybooks);
            if (voyage.SelectedPlaybooks.Count > 0)
            {
                await _Database.Playbooks.SetVoyageSelectionsAsync(voyage.Id, voyage.SelectedPlaybooks).ConfigureAwait(false);
            }

            Dictionary<string, string> aliasToMsnId = new Dictionary<string, string>(StringComparer.Ordinal);
            bool anyAssigned = false;

            foreach (MissionDescription md in sortedMissions)
            {
                string? externalDep = null;
                if (!String.IsNullOrEmpty(md.DependsOnMissionAlias))
                    externalDep = aliasToMsnId[md.DependsOnMissionAlias];
                else if (!String.IsNullOrEmpty(md.DependsOnMissionId))
                    externalDep = md.DependsOnMissionId;

                List<SelectedPlaybook> mergedForMission = PlaybookMerge.MergeWithVesselDefaults(
                    voyage.SelectedPlaybooks,
                    md.SelectedPlaybooks ?? new List<SelectedPlaybook>());

                if (!isMultiStage)
                {
                    Mission mission = new Mission(md.Title, md.Description);
                    mission.TenantId = vessel.TenantId;
                    mission.UserId = vessel.UserId;
                    mission.VoyageId = voyage.Id;
                    mission.VesselId = vesselId;
                    mission.PrestagedFiles = ClonePrestagedFilesLocal(md.PrestagedFiles);
                    mission.PreferredModel = md.PreferredModel;
                    mission.CapabilityHint = md.CapabilityHint;
                    mission.Mode = Armada.Core.Enums.MissionModes.Parse(md.Mode);
                    mission.SelectedPlaybooks = ClonePlaybookSelectionsLocal(mergedForMission);
                    mission.DependsOnMissionId = externalDep;

                    mission = await _Admiral.DispatchMissionQueuedAsync(mission).ConfigureAwait(false);

                    if (mission.Status == MissionStatusEnum.Assigned || mission.Status == MissionStatusEnum.InProgress)
                        anyAssigned = true;

                    if (!String.IsNullOrEmpty(md.Alias))
                        aliasToMsnId[md.Alias] = mission.Id;
                    continue;
                }

                string baseTitle = md.Title.Length > 60 ? md.Title.Substring(0, 60).TrimEnd() + "..." : md.Title;
                string? previousOrderLastMissionId = null;
                string? lastStageMissionId = null;

                IOrderedEnumerable<IGrouping<int, PipelineStage>> stageGroups =
                    pipeline!.Stages.GroupBy(s => s.Order).OrderBy(g => g.Key);

                foreach (IGrouping<int, PipelineStage> stageGroup in stageGroups)
                {
                    string? groupDependencyId = previousOrderLastMissionId ?? externalDep;
                    string? lastMissionInGroup = null;

                    foreach (PipelineStage stage in stageGroup)
                    {
                        Mission stageMission = new Mission(
                            "[" + stage.PersonaName + "] " + baseTitle,
                            md.Description);
                        stageMission.TenantId = vessel.TenantId;
                        stageMission.UserId = vessel.UserId;
                        stageMission.VoyageId = voyage.Id;
                        stageMission.VesselId = vesselId;
                        stageMission.Persona = stage.PersonaName;
                        stageMission.DependsOnMissionId = groupDependencyId;
                        // Same-order stages are parallel siblings; StageOrder is the barrier key that
                        // makes a downstream stage wait for EVERY sibling in the group, not just the
                        // last one its dependency happens to name. Without it the alias dispatch path
                        // would let a Judge review a diff its parallel sibling reviewers had not
                        // finished contributing to.
                        stageMission.StageOrder = stage.Order;
                        stageMission.PreferredModel = PreferredModelTierSelector.EnforceHighTierForPersona(
                            stage.PreferredModel ?? md.PreferredModel,
                            stage.PersonaName,
                            settings?.ModelTier.SpecialistPersonas);
                        stageMission.CapabilityHint = md.CapabilityHint;
                        // Every stage of a read-only voyage stays read-only: a pipeline must not
                        // silently turn an audit into an implementing mission at stage 2.
                        stageMission.Mode = Armada.Core.Enums.MissionModes.Parse(md.Mode);
                        stageMission.SelectedPlaybooks = ClonePlaybookSelectionsLocal(mergedForMission);

                        // Each pipeline stage receives a new dock worktree. Preserve every
                        // prestaged entry on every stage so briefing and reference files are
                        // available to reviewers as well as the first Worker.
                        stageMission.PrestagedFiles = ClonePrestagedFilesLocal(md.PrestagedFiles);
                        bool isFirstChainMission = previousOrderLastMissionId == null && lastMissionInGroup == null;

                        if (isFirstChainMission)
                        {
                            stageMission = await _Admiral.DispatchMissionQueuedAsync(stageMission).ConfigureAwait(false);
                            if (stageMission.Status == MissionStatusEnum.Assigned || stageMission.Status == MissionStatusEnum.InProgress)
                                anyAssigned = true;
                        }
                        else
                        {
                            stageMission = await _Database.Missions.CreateAsync(stageMission).ConfigureAwait(false);

                            if (stageMission.SelectedPlaybooks != null
                                && stageMission.SelectedPlaybooks.Count > 0
                                && !String.IsNullOrEmpty(stageMission.TenantId))
                            {
                                LoggingModule effectiveLogging = _Logging ?? CreateSilentLogging();
                                IPlaybookService playbooks = new PlaybookService(_Database, effectiveLogging);
                                List<MissionPlaybookSnapshot> snapshots = await playbooks.CreateSnapshotsAsync(
                                    stageMission.TenantId,
                                    stageMission.SelectedPlaybooks).ConfigureAwait(false);
                                await _Database.Playbooks.SetMissionSnapshotsAsync(stageMission.Id, snapshots).ConfigureAwait(false);
                            }
                        }

                        lastMissionInGroup = stageMission.Id;
                        lastStageMissionId = stageMission.Id;
                    }

                    previousOrderLastMissionId = lastMissionInGroup;
                }

                if (!String.IsNullOrEmpty(md.Alias) && lastStageMissionId != null)
                    aliasToMsnId[md.Alias] = lastStageMissionId;
            }

            voyage.Status = anyAssigned ? VoyageStatusEnum.InProgress : VoyageStatusEnum.Open;
            voyage.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Voyages.UpdateAsync(voyage).ConfigureAwait(false);

            return voyage;
        }

        private static List<SelectedPlaybook> ClonePlaybookSelectionsLocal(List<SelectedPlaybook>? selections)
        {
            if (selections == null || selections.Count == 0) return new List<SelectedPlaybook>();
            List<SelectedPlaybook> copy = new List<SelectedPlaybook>(selections.Count);
            foreach (SelectedPlaybook s in selections)
            {
                copy.Add(new SelectedPlaybook { PlaybookId = s.PlaybookId, DeliveryMode = s.DeliveryMode });
            }
            return copy;
        }

        private static List<PrestagedFile>? ClonePrestagedFilesLocal(List<PrestagedFile>? entries)
        {
            if (entries == null || entries.Count == 0) return null;
            List<PrestagedFile> copy = new List<PrestagedFile>(entries.Count);
            foreach (PrestagedFile entry in entries)
            {
                if (entry == null) continue;
                copy.Add(new PrestagedFile(entry.SourcePath ?? "", entry.DestPath ?? "")
                {
                    Content = entry.Content,
                    ReadOnly = entry.ReadOnly
                });
            }
            return copy.Count > 0 ? copy : null;
        }

        private static LoggingModule CreateSilentLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        #endregion
    }
}
