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
        private const int _DefaultCodeContextTokenBudget = 3000;
        private static readonly HashSet<string> _ImplementingPersonas = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Worker",
            "Architect",
            "PortingReferenceAnalyst"
        };

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
        public async Task<VoyageDispatchResult> DispatchAsync(SharedVoyageDispatchRequest request, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            string title = request.Title;
            string description = request.Description ?? "";
            string vesselId = request.VesselId;
            List<MissionDescription> missions = request.Missions;
            List<SelectedPlaybook> callerPlaybooks = request.SelectedPlaybooks ?? new List<SelectedPlaybook>();
            string? objectiveId = NormalizeEmpty(request.ObjectiveId);

            VoyageDispatchResult? validation = ValidateRequest(title, missions);
            if (validation != null) return validation;

            Vessel? dispatchVessel = await _Database.Vessels.ReadAsync(vesselId, token).ConfigureAwait(false);
            if (dispatchVessel == null) return VoyageDispatchResult.NotFound(new
            {
                Error = "Vessel not found: " + vesselId,
                Code = "vessel_not_found",
                Reason = "Vessel " + vesselId + " does not exist in this admiral.",
                Action = "Register the vessel via armada_add_vessel or verify the vesselId.",
                VesselId = vesselId
            });

            VoyageDispatchResult? objectiveValidation = await ValidateObjectiveAsync(objectiveId, request.ObjectiveAuthContext).ConfigureAwait(false);
            if (objectiveValidation != null) return objectiveValidation;

            List<SelectedPlaybook> mergedPlaybooks = PlaybookMerge.MergeWithVesselDefaults(dispatchVessel.GetDefaultPlaybooks(), callerPlaybooks);

            object? blockedByIndex = await CodeIndexDispatchGuard.BuildVoyageDispatchBlockedResponseAsync(
                _CodeIndexService,
                vesselId,
                "armada_dispatch").ConfigureAwait(false);
            if (blockedByIndex != null) return VoyageDispatchResult.BadRequest(blockedByIndex);

            string? pipelineId = await ResolvePipelineIdAsync(request.PipelineId, request.Pipeline).ConfigureAwait(false);
            if (String.Equals(pipelineId, "__pipeline_not_found__", StringComparison.Ordinal))
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

            string? codeContextError = await ApplyDispatchCodeContextAsync(
                vesselId,
                request.CodeContextMode,
                request.CodeContextTokenBudget,
                request.CodeContextMaxResults,
                missions).ConfigureAwait(false);
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
            return VoyageDispatchResult.Success(voyage);
        }

        #endregion

        #region Private-Methods

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
            }

            return null;
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

        private async Task<string?> ApplyDispatchCodeContextAsync(
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

                    LogCodeContextWarning("skipping code context for mission '" + mission.Title + "' because no query could be built");
                    continue;
                }

                if (_CodeIndexService == null)
                {
                    if (String.Equals(mode, _CodeContextModeForce, StringComparison.Ordinal))
                        return "code context force requested but code index service is unavailable";

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
                    ContextPackResponse? cached = await _CodeIndexService.TryGetCachedContextPackAsync(contextRequest).ConfigureAwait(false);

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

                    if (String.Equals(mode, _CodeContextModeAuto, StringComparison.Ordinal))
                    {
                        mission.CodeContextMode = mode;
                        mission.CodeContextQuery = query;
                        LogCodeContextInfo(
                            "code context for mission '" + mission.Title + "': auto_deferred"
                            + " vesselId=" + vesselId);
                        continue;
                    }

                    LogCodeContextInfo(
                        "code context for mission '" + mission.Title + "': cache_miss; warming baseline cache for vessel " + vesselId);
                    await _CodeIndexService.WarmBaselineCacheAsync(vesselId).ConfigureAwait(false);

                    cached = await _CodeIndexService.TryGetCachedContextPackAsync(contextRequest).ConfigureAwait(false);
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
                        if (String.Equals(mode, _CodeContextModeForce, StringComparison.Ordinal))
                            return "code context generation returned no prestaged files for mission '" + mission.Title + "'";

                        LogCodeContextWarning("code context generation returned no prestaged files for mission '" + mission.Title + "'");
                        continue;
                    }

                    MergeGeneratedPrestagedFiles(mission, contextPack.PrestagedFiles);
                }
                catch (Exception ex)
                {
                    if (String.Equals(mode, _CodeContextModeForce, StringComparison.Ordinal))
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
            await _ObjectiveService.LinkVoyageAsync(auth, objectiveId, voyage.Id).ConfigureAwait(false);
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
                        stageMission.PreferredModel = PreferredModelTierSelector.EnforceHighTierForPersona(
                            stage.PreferredModel ?? md.PreferredModel,
                            stage.PersonaName,
                            settings?.ModelTier.SpecialistPersonas);
                        stageMission.SelectedPlaybooks = ClonePlaybookSelectionsLocal(mergedForMission);

                        bool isFirstChainMission = previousOrderLastMissionId == null && lastMissionInGroup == null;
                        if (isFirstChainMission)
                        {
                            stageMission.PrestagedFiles = ClonePrestagedFilesLocal(md.PrestagedFiles);
                        }
                        else if (_ImplementingPersonas.Contains(stage.PersonaName ?? ""))
                        {
                            PrestagedFile? contextPackEntry = FindContextPackEntry(md.PrestagedFiles);
                            if (contextPackEntry != null)
                                stageMission.PrestagedFiles = new List<PrestagedFile> { new PrestagedFile(contextPackEntry.SourcePath ?? "", contextPackEntry.DestPath ?? _CodeContextDestPath) };
                        }

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

        private static PrestagedFile? FindContextPackEntry(List<PrestagedFile>? entries)
        {
            if (entries == null) return null;
            foreach (PrestagedFile entry in entries)
            {
                if (entry == null) continue;
                if (String.Equals(entry.DestPath, _CodeContextDestPath, StringComparison.OrdinalIgnoreCase))
                    return entry;
            }
            return null;
        }

        private static List<PrestagedFile>? ClonePrestagedFilesLocal(List<PrestagedFile>? entries)
        {
            if (entries == null || entries.Count == 0) return null;
            List<PrestagedFile> copy = new List<PrestagedFile>(entries.Count);
            foreach (PrestagedFile entry in entries)
            {
                if (entry == null) continue;
                copy.Add(new PrestagedFile(entry.SourcePath ?? "", entry.DestPath ?? ""));
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
