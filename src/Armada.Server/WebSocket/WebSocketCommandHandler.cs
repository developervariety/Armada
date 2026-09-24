namespace Armada.Server.WebSocket
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Runtimes;

    /// <summary>
    /// Handles all WebSocket command actions, extracted from the ArmadaWebSocketHub command route.
    /// </summary>
    public class WebSocketCommandHandler
    {
        #region Private-Members

        private readonly IAdmiralService _Admiral;
        private readonly DatabaseBackupService? _Backups;
        private readonly DatabaseDriver _Database;
        private readonly IMergeQueueService _MergeQueue;
        private readonly ArmadaSettings? _Settings;
        private readonly IGitService? _Git;
        private readonly Action? _OnStop;
        private readonly JsonSerializerOptions _JsonOptions;
        private readonly Action<Mission> _BroadcastMissionChange;
        private readonly Action<Voyage> _BroadcastVoyageChange;
        private readonly MissionStatusTransitionService? _StatusTransitions;
        private readonly Dictionary<string, Func<WebSocketCommand, string, AuthContext, Task<object>>> _Commands;
        private CaptainAdministrationService? _CaptainAdministration;
        private Func<VoyageDispatchService>? _VoyageDispatchFactory;
        private MissionOperations? _Operations;
        private static readonly JsonSerializerOptions _FieldNameOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate the command handler.
        /// </summary>
        /// <param name="admiral">Admiral service for command handling.</param>
        /// <param name="database">Database driver for data access.</param>
        /// <param name="mergeQueue">Merge queue service.</param>
        /// <param name="settings">Optional Armada settings for log/diff paths.</param>
        /// <param name="git">Optional git service for diff generation.</param>
        /// <param name="onStop">Optional callback invoked when stop_server is requested.</param>
        /// <param name="jsonOptions">JSON serializer options.</param>
        /// <param name="broadcastMissionChange">Callback to broadcast a changed mission to the sessions that may read it.</param>
        /// <param name="broadcastVoyageChange">Callback to broadcast a changed voyage to the sessions that may read it.</param>
        /// <param name="statusTransitions">Shared operator status transition path; without it transitions are refused.</param>
        /// <param name="backups">Backup and restore service; without it backup and restore are refused.</param>
        public WebSocketCommandHandler(
            IAdmiralService admiral,
            DatabaseDriver database,
            IMergeQueueService mergeQueue,
            ArmadaSettings? settings,
            IGitService? git,
            Action? onStop,
            JsonSerializerOptions jsonOptions,
            Action<Mission> broadcastMissionChange,
            Action<Voyage> broadcastVoyageChange,
            MissionStatusTransitionService? statusTransitions = null,
            DatabaseBackupService? backups = null)
        {
            _StatusTransitions = statusTransitions;
            _Backups = backups;
            _Admiral = admiral;
            _Database = database;
            _MergeQueue = mergeQueue;
            _Settings = settings;
            _Git = git;
            _OnStop = onStop;
            _JsonOptions = jsonOptions;
            _BroadcastMissionChange = broadcastMissionChange;
            _BroadcastVoyageChange = broadcastVoyageChange;
            _Commands = new Dictionary<string, Func<WebSocketCommand, string, AuthContext, Task<object>>>(StringComparer.Ordinal)
            {
                { "status", StatusCommandAsync },
                { "stop_captain", StopCaptainCommandAsync },
                { "stop_all", StopAllCommandAsync },
                { "stop_server", StopServerCommandAsync },
                { "list_fleets", ListFleetsCommandAsync },
                { "get_fleet", GetFleetCommandAsync },
                { "create_fleet", CreateFleetCommandAsync },
                { "update_fleet", UpdateFleetCommandAsync },
                { "delete_fleet", DeleteFleetCommandAsync },
                { "list_vessels", ListVesselsCommandAsync },
                { "get_vessel", GetVesselCommandAsync },
                { "create_vessel", CreateVesselCommandAsync },
                { "update_vessel", UpdateVesselCommandAsync },
                { "update_vessel_context", UpdateVesselContextCommandAsync },
                { "delete_vessel", DeleteVesselCommandAsync },
                { "list_voyages", ListVoyagesCommandAsync },
                { "get_voyage", GetVoyageCommandAsync },
                { "create_voyage", CreateVoyageCommandAsync },
                { "cancel_voyage", CancelVoyageCommandAsync },
                { "purge_voyage", PurgeVoyageCommandAsync },
                { "list_missions", ListMissionsCommandAsync },
                { "list_missions_summary", ListMissionsSummaryCommandAsync },
                { "get_mission", GetMissionCommandAsync },
                { "create_mission", CreateMissionCommandAsync },
                { "update_mission", UpdateMissionCommandAsync },
                { "transition_mission_status", TransitionMissionStatusCommandAsync },
                { "cancel_mission", CancelMissionCommandAsync },
                { "purge_mission", PurgeMissionCommandAsync },
                { "restart_mission", RestartMissionCommandAsync },
                { "get_mission_diff", GetMissionDiffCommandAsync },
                { "get_mission_log", GetMissionLogCommandAsync },
                { "list_captains", ListCaptainsCommandAsync },
                { "get_captain", GetCaptainCommandAsync },
                { "create_captain", CreateCaptainCommandAsync },
                { "update_captain", UpdateCaptainCommandAsync },
                { "delete_captain", DeleteCaptainCommandAsync },
                { "get_captain_log", GetCaptainLogCommandAsync },
                { "list_signals", ListSignalsCommandAsync },
                { "send_signal", SendSignalCommandAsync },
                { "list_events", ListEventsCommandAsync },
                { "list_docks", ListDocksCommandAsync },
                { "list_merge_queue", ListMergeQueueCommandAsync },
                { "get_merge_entry", GetMergeEntryCommandAsync },
                { "enqueue_merge", EnqueueMergeCommandAsync },
                { "cancel_merge", CancelMergeCommandAsync },
                { "process_merge_queue", ProcessMergeQueueCommandAsync },
                { "enumerate", EnumerateCommandAsync },
                { "backup", BackupCommandAsync },
                { "restore", RestoreCommandAsync },
                { "get_persona", GetPersonaCommandAsync },
                { "create_persona", CreatePersonaCommandAsync },
                { "update_persona", UpdatePersonaCommandAsync },
                { "delete_persona", DeletePersonaCommandAsync },
                { "get_prompt_template", GetPromptTemplateCommandAsync },
                { "update_prompt_template", UpdatePromptTemplateCommandAsync },
                { "get_pipeline", GetPipelineCommandAsync },
                { "create_pipeline", CreatePipelineCommandAsync },
                { "update_pipeline", UpdatePipelineCommandAsync },
                { "delete_pipeline", DeletePipelineCommandAsync },
            };
        }

        #endregion

        #region Public-Members

        /// <summary>
        /// The names of the commands this handler dispatches. Every name has a rule in <see cref="WebSocketCommandRegistry"/>.
        /// </summary>
        public IReadOnlyCollection<string> CommandNames => _Commands.Keys;

        /// <summary>
        /// Shared captain stop, stop-all and deletion service. The server sets the instance REST and MCP use; when unset,
        /// one is built that recalls through the admiral and has no session coordinators, so it reports active
        /// planning and refinement sessions as failed stops and refuses to stop a Planning or Refining captain.
        /// </summary>
        public CaptainAdministrationService CaptainAdministration
        {
            get => _CaptainAdministration ??= new CaptainAdministrationService(_Database, (captainId, token) => _Admiral.RecallCaptainAsync(captainId, token));
            set => _CaptainAdministration = value ?? throw new ArgumentNullException(nameof(CaptainAdministration));
        }

        /// <summary>
        /// Shared mission and voyage operations (cancel, purge, restart). The server sets the instance REST and MCP
        /// use, which writes events and broadcasts; when unset, one is built that recalls through the admiral,
        /// broadcasts through this handler, writes no events, and removes docks only when settings and git are set.
        /// </summary>
        public MissionOperations Operations
        {
            get => _Operations ??= new MissionOperations(
                _Database,
                _Settings ?? new ArmadaSettings(),
                _Settings != null && _Git != null ? new DockService(new SyslogLogging.LoggingModule(), _Database, _Settings, _Git) : null,
                (captainId, token) => _Admiral.RecallCaptainAsync(captainId, token),
                new OperationNotifier(null, _BroadcastMissionChange, _BroadcastVoyageChange));
            set => _Operations = value ?? throw new ArgumentNullException(nameof(Operations));
        }

        /// <summary>
        /// Builds the shared voyage dispatch service <c>create_voyage</c> dispatches through. The server sets the
        /// factory REST and MCP use, carrying the code-index service, objective service, dispatch preview and
        /// staleness adapter. When unset, the service is built from this handler's database, admiral and settings
        /// alone: it still applies the same validation, captain overrides, stage skips and dispatch hold, but has
        /// no code index or objective service, so an objective-linked dispatch is refused by the service.
        /// </summary>
        public Func<VoyageDispatchService> VoyageDispatchFactory
        {
            get => _VoyageDispatchFactory ??= () => new VoyageDispatchService(_Database, _Admiral, null, null, null, _Settings);
            set => _VoyageDispatchFactory = value ?? throw new ArgumentNullException(nameof(VoyageDispatchFactory));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Handle a WebSocket command. The command's declared rule in <see cref="WebSocketCommandRegistry"/> is enforced
        /// first, for every caller, so an unknown command, a missing caller or a caller without the required role is
        /// refused before the command reads or writes anything.
        /// </summary>
        /// <param name="action">The action string from the command.</param>
        /// <param name="command">The deserialized WebSocket command.</param>
        /// <param name="rawBody">The raw JSON body string for data commands.</param>
        /// <param name="caller">The authenticated session caller.</param>
        /// <returns>The result object to serialize and send back to the client.</returns>
        public async Task<object> HandleCommandAsync(string action, WebSocketCommand command, string rawBody, AuthContext? caller = null)
        {
            string name = action ?? "";
            Func<WebSocketCommand, string, AuthContext, Task<object>>? run = null;
            WebSocketCommandRefusal? refusal = _Commands.TryGetValue(name, out run)
                ? WebSocketCommandRegistry.Authorize(name, caller)
                : WebSocketCommandRefusal.UnknownCommand(name);
            if (refusal != null || run == null)
                return (refusal ?? WebSocketCommandRefusal.UnknownCommand(name)).ToResult(name);
            return await run(command ?? new WebSocketCommand(), rawBody ?? "", caller!).ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Refusal for a record the caller may not read or that does not exist; the two read the same.
        /// </summary>
        private static object NotFound(string action, string message)
        {
            return new { type = "command.error", action = action, error = message, code = WebSocketCommandRefusal.NotFoundCode };
        }

        /// <summary>
        /// Refusal for a record the caller may read but not change.
        /// </summary>
        private static object Forbidden(string action, string message)
        {
            return new { type = "command.error", action = action, error = message, code = WebSocketCommandRefusal.ForbiddenCode };
        }

        /// <summary>
        /// Find a persona by name as the caller sees it, through the shared caller scope.
        /// </summary>
        private Task<Persona?> ReadVisiblePersonaAsync(AuthContext caller, string? name)
        {
            if (String.IsNullOrEmpty(name)) return Task.FromResult<Persona?>(null);
            return Armada.Core.Services.OwnedRecordScope.ReadByNameAsync(
                caller,
                name,
                (tenantId, personaName) => _Database.Personas.ReadByNameAsync(tenantId, personaName),
                () => _Database.Personas.EnumerateAsync(),
                record => record.Name);
        }

        /// <summary>
        /// Find a pipeline by name as the caller sees it, through the shared caller scope.
        /// </summary>
        private Task<Pipeline?> ReadVisiblePipelineAsync(AuthContext caller, string? name)
        {
            if (String.IsNullOrEmpty(name)) return Task.FromResult<Pipeline?>(null);
            return Armada.Core.Services.OwnedRecordScope.ReadByNameAsync(
                caller,
                name,
                (tenantId, pipelineName) => _Database.Pipelines.ReadByNameAsync(tenantId, pipelineName),
                () => _Database.Pipelines.EnumerateAsync(),
                record => record.Name);
        }

        /// <summary>
        /// The top-level field names of a command's data object, so an update can tell an omitted field from a
        /// field sent with its default value.
        /// </summary>
        private static HashSet<string> ReadDataFieldNames(string rawBody)
        {
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                WebSocketDataCommand<Dictionary<string, JsonElement>>? parsed =
                    JsonSerializer.Deserialize<WebSocketDataCommand<Dictionary<string, JsonElement>>>(rawBody, _FieldNameOptions);
                if (parsed?.Data != null)
                {
                    foreach (string name in parsed.Data.Keys) names.Add(name);
                }
            }
            catch (JsonException)
            {
                // A data member that is not an object carries no fields; the typed deserialization reports it.
            }
            return names;
        }

        /// <summary>
        /// Run the <c>status</c> command.
        /// </summary>
        private async Task<object> StatusCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            ArmadaStatus cmdStatus = await _Admiral.GetStatusAsync().ConfigureAwait(false);
            return new { type = "command.result", action = "status", data = (object)cmdStatus };
        }

        /// <summary>
        /// Run the <c>stop_captain</c> command.
        /// </summary>
        private async Task<object> StopCaptainCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            CaptainStopResult stopped = await CaptainAdministration.StopAsync(command.CaptainId ?? "", null).ConfigureAwait(false);
            if (stopped.Outcome != CaptainAdministrationOutcomeEnum.Completed)
                return new { type = "command.error", action = "stop_captain", error = stopped.Message, outcome = stopped.Outcome.ToString() };
            return new { type = "command.result", action = "stop_captain", data = (object)stopped };
        }

        /// <summary>
        /// Run the <c>stop_all</c> command.
        /// </summary>
        private async Task<object> StopAllCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            CaptainStopAllResult stopAll = await CaptainAdministration.StopAllAsync().ConfigureAwait(false);
            return new { type = "command.result", action = "stop_all", data = (object)stopAll };
        }

        /// <summary>
        /// Run the <c>stop_server</c> command.
        /// </summary>
        private Task<object> StopServerCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            if (_OnStop != null)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500).ConfigureAwait(false);
                    _OnStop();
                });
            }
            return Task.FromResult<object>(new { type = "command.result", action = "stop_server", data = (object)new { status = "shutting_down" } });
        }

        /// <summary>
        /// Run the <c>list_fleets</c> command.
        /// </summary>
        private async Task<object> ListFleetsCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            EnumerationQuery fleetQuery = command.Query ?? new EnumerationQuery();
            Stopwatch fleetSw = Stopwatch.StartNew();
            EnumerationResult<Fleet> fleetResult = await _Database.Fleets.EnumerateAsync(fleetQuery).ConfigureAwait(false);
            fleetResult.TotalMs = Math.Round(fleetSw.Elapsed.TotalMilliseconds, 2);
            return new { type = "command.result", action = "list_fleets", data = (object)fleetResult };
        }

        /// <summary>
        /// Run the <c>get_fleet</c> command.
        /// </summary>
        private async Task<object> GetFleetCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            string getFleetId = command.Id ?? "";
            Fleet? foundFleet = await _Database.Fleets.ReadAsync(getFleetId).ConfigureAwait(false);
            if (foundFleet == null)
                return new { type = "command.error", action = "get_fleet", error = "Fleet not found" };
            else
            {
                List<Vessel> fleetVessels = await _Database.Vessels.EnumerateByFleetAsync(getFleetId).ConfigureAwait(false);
                return new { type = "command.result", action = "get_fleet", data = (object)new { Fleet = foundFleet, Vessels = fleetVessels } };
            }
        }

        /// <summary>
        /// Run the <c>create_fleet</c> command.
        /// </summary>
        private async Task<object> CreateFleetCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            Fleet newFleet = JsonSerializer.Deserialize<WebSocketDataCommand<Fleet>>(rawBody, _JsonOptions)?.Data!;
            newFleet.TenantId = Armada.Core.Authorization.OwnershipPolicy.TenantOf(caller);
            newFleet.UserId = Armada.Core.Authorization.OwnershipPolicy.UserOf(caller);
            newFleet = await _Database.Fleets.CreateAsync(newFleet).ConfigureAwait(false);
            return new { type = "command.result", action = "create_fleet", data = (object)newFleet };
        }

        /// <summary>
        /// Run the <c>update_fleet</c> command. The body replaces client-editable fields only, by the rule REST applies.
        /// </summary>
        private async Task<object> UpdateFleetCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            string updFleetId = command.Id ?? "";
            Fleet? existFleet = await _Database.Fleets.ReadAsync(updFleetId).ConfigureAwait(false);
            if (existFleet == null)
                return NotFound("update_fleet", "Fleet not found");
            Fleet updFleet = JsonSerializer.Deserialize<WebSocketDataCommand<Fleet>>(rawBody, _JsonOptions)?.Data!;
            FleetUpdateMerge.KeepServerOwnedFields(existFleet, updFleet, ReadDataFieldNames(rawBody));
            updFleet = await _Database.Fleets.UpdateAsync(updFleet).ConfigureAwait(false);
            return new { type = "command.result", action = "update_fleet", data = (object)updFleet };
        }

        /// <summary>
        /// Run the <c>delete_fleet</c> command.
        /// </summary>
        private async Task<object> DeleteFleetCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            string delFleetId = command.Id ?? "";
            await _Database.Fleets.DeleteAsync(delFleetId).ConfigureAwait(false);
            return new { type = "command.result", action = "delete_fleet", data = (object)new { status = "deleted" } };
        }

        /// <summary>
        /// Run the <c>list_vessels</c> command.
        /// </summary>
        private async Task<object> ListVesselsCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            EnumerationQuery vesselQuery = command.Query ?? new EnumerationQuery();
            Stopwatch vesselSw = Stopwatch.StartNew();
            EnumerationResult<Vessel> vesselResult = await _Database.Vessels.EnumerateAsync(vesselQuery).ConfigureAwait(false);
            vesselResult.TotalMs = Math.Round(vesselSw.Elapsed.TotalMilliseconds, 2);
            return new { type = "command.result", action = "list_vessels", data = (object)vesselResult };
        }

        /// <summary>
        /// Run the <c>get_vessel</c> command.
        /// </summary>
        private async Task<object> GetVesselCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            string getVesselId = command.Id ?? "";
            Vessel? foundVessel = await _Database.Vessels.ReadAsync(getVesselId).ConfigureAwait(false);
            if (foundVessel == null)
                return new { type = "command.error", action = "get_vessel", error = "Vessel not found" };
            else
                return new { type = "command.result", action = "get_vessel", data = (object)foundVessel };
        }

        /// <summary>
        /// Run the <c>create_vessel</c> command.
        /// </summary>
        private async Task<object> CreateVesselCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            Vessel newVessel = JsonSerializer.Deserialize<WebSocketDataCommand<Vessel>>(rawBody, _JsonOptions)?.Data!;
            if (String.IsNullOrEmpty(newVessel.RepoUrl))
                return new { type = "command.error", action = "create_vessel", error = "repoUrl is required when creating a vessel" };
            string? createPathError = Armada.Core.Authorization.VesselPathPolicy.ValidateCreate(caller, newVessel, out bool createPathForbidden);
            if (createPathError != null)
                return createPathForbidden
                    ? Forbidden("create_vessel", createPathError)
                    : new { type = "command.error", action = "create_vessel", error = createPathError };
            newVessel.TenantId = Armada.Core.Authorization.OwnershipPolicy.TenantOf(caller);
            newVessel.UserId = Armada.Core.Authorization.OwnershipPolicy.UserOf(caller);
            newVessel.NormalizeGitHubTokenOverride();
            newVessel = await _Database.Vessels.CreateAsync(newVessel).ConfigureAwait(false);
            return new { type = "command.result", action = "create_vessel", data = (object)newVessel };
        }

        /// <summary>
        /// Run the <c>update_vessel</c> command. The body replaces client-editable fields only, by the rule REST applies.
        /// </summary>
        private async Task<object> UpdateVesselCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            string updVesselId = command.Id ?? "";
            Vessel? existVessel = await _Database.Vessels.ReadAsync(updVesselId).ConfigureAwait(false);
            if (existVessel == null)
                return NotFound("update_vessel", "Vessel not found");
            Vessel updVessel = JsonSerializer.Deserialize<WebSocketDataCommand<Vessel>>(rawBody, _JsonOptions)?.Data!;
            VesselUpdateMerge.KeepServerOwnedFields(existVessel, updVessel);
            string? updatePathError = Armada.Core.Authorization.VesselPathPolicy.ApplyUpdate(caller, existVessel, updVessel, out bool updatePathForbidden);
            if (updatePathError != null)
                return updatePathForbidden
                    ? Forbidden("update_vessel", updatePathError)
                    : new { type = "command.error", action = "update_vessel", error = updatePathError };
            updVessel = await _Database.Vessels.UpdateAsync(updVessel).ConfigureAwait(false);
            return new { type = "command.result", action = "update_vessel", data = (object)updVessel };
        }

        /// <summary>
        /// Run the <c>update_vessel_context</c> command.
        /// </summary>
        private async Task<object> UpdateVesselContextCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            string ctxVesselId = command.Id ?? "";
            Vessel? ctxVessel = await _Database.Vessels.ReadAsync(ctxVesselId).ConfigureAwait(false);
            if (ctxVessel == null)
                return new { type = "command.error", action = "update_vessel_context", error = "Vessel not found" };
            else
            {
                Vessel ctxPatch = JsonSerializer.Deserialize<WebSocketDataCommand<Vessel>>(rawBody, _JsonOptions)?.Data!;
                if (ctxPatch.ProjectContext != null)
                    ctxVessel.ProjectContext = ctxPatch.ProjectContext;
                if (ctxPatch.StyleGuide != null)
                    ctxVessel.StyleGuide = ctxPatch.StyleGuide;
                ctxVessel = await _Database.Vessels.UpdateAsync(ctxVessel).ConfigureAwait(false);
                return new { type = "command.result", action = "update_vessel_context", data = (object)ctxVessel };
            }
        }

        /// <summary>
        /// Run the <c>delete_vessel</c> command.
        /// </summary>
        private async Task<object> DeleteVesselCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            string delVesselId = command.Id ?? "";
            Vessel? delVessel = await _Database.Vessels.ReadAsync(delVesselId).ConfigureAwait(false);
            if (delVessel == null)
                return new { type = "command.error", action = "delete_vessel", error = "Vessel not found" };

            // Cancel active missions on this vessel
            try
            {
                List<Mission> delVesselMissions = await _Database.Missions.EnumerateByVesselAsync(delVesselId).ConfigureAwait(false);
                foreach (Mission dvm in delVesselMissions)
                {
                    if (dvm.Status == MissionStatusEnum.Pending || dvm.Status == MissionStatusEnum.Assigned || dvm.Status == MissionStatusEnum.InProgress)
                    {
                        dvm.Status = MissionStatusEnum.Cancelled;
                        dvm.CompletedUtc = DateTime.UtcNow;
                        dvm.LastUpdateUtc = DateTime.UtcNow;
                        await _Database.Missions.UpdateAsync(dvm).ConfigureAwait(false);
                    }
                }
            }
            catch { }

            // Clean up docks/worktrees for this vessel
            try
            {
                List<Dock> delVesselDocks = await _Database.Docks.EnumerateByVesselAsync(delVesselId).ConfigureAwait(false);
                foreach (Dock dvd in delVesselDocks)
                {
                    if (!String.IsNullOrEmpty(dvd.WorktreePath) && System.IO.Directory.Exists(dvd.WorktreePath))
                    {
                        try { System.IO.Directory.Delete(dvd.WorktreePath, true); }
                        catch { }
                    }
                    await _Database.Docks.DeleteAsync(dvd.Id).ConfigureAwait(false);
                }
            }
            catch { }

            // Clean up bare repo
            if (!String.IsNullOrEmpty(delVessel.LocalPath) && System.IO.Directory.Exists(delVessel.LocalPath))
            {
                try { System.IO.Directory.Delete(delVessel.LocalPath, true); }
                catch { }
            }

            await _Database.Vessels.DeleteAsync(delVesselId).ConfigureAwait(false);
            return new { type = "command.result", action = "delete_vessel", data = (object)new { status = "deleted" } };
        }

        /// <summary>
        /// Run the <c>list_voyages</c> command.
        /// </summary>
        private async Task<object> ListVoyagesCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            EnumerationQuery voyageQuery = command.Query ?? new EnumerationQuery();
            Stopwatch voyageSw = Stopwatch.StartNew();
            EnumerationResult<Voyage> voyageResult = await _Database.Voyages.EnumerateAsync(voyageQuery).ConfigureAwait(false);
            voyageResult.TotalMs = Math.Round(voyageSw.Elapsed.TotalMilliseconds, 2);
            return new { type = "command.result", action = "list_voyages", data = (object)voyageResult };
        }

        /// <summary>
        /// Run the <c>get_voyage</c> command.
        /// </summary>
        private async Task<object> GetVoyageCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            string getVoyageId = command.Id ?? "";
            Voyage? foundVoyage = await _Database.Voyages.ReadAsync(getVoyageId).ConfigureAwait(false);
            if (foundVoyage == null)
                return new { type = "command.error", action = "get_voyage", error = "Voyage not found" };
            else
            {
                EnumerationResult<Mission> voyageMissions = await _Database.Missions.EnumerateSummariesAsync(new EnumerationQuery
                {
                    VoyageId = getVoyageId,
                    PageSize = 1000
                }).ConfigureAwait(false);
                return new { type = "command.result", action = "get_voyage", data = (object)new { voyage = foundVoyage, missions = voyageMissions.Objects } };
            }
        }

        /// <summary>
        /// Run the <c>create_voyage</c> command. A payload without a vessel or missions creates a bare voyage
        /// owned by the caller. A payload with both is dispatched through the shared voyage dispatch service,
        /// the same path REST and MCP use, so captain overrides, pipeline selection, playbooks, objective
        /// linking, the code-index gate, code-context preparation, stage skips and the dispatch hold apply
        /// identically. A refusal returns <c>command.error</c> carrying the shared service's code and body.
        /// </summary>
        private async Task<object> CreateVoyageCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            WebSocketVoyageData voyageData = JsonSerializer.Deserialize<WebSocketDataCommand<WebSocketVoyageData>>(rawBody, _JsonOptions)?.Data ?? new WebSocketVoyageData();
            string voyTitle = voyageData.Title ?? "";
            string voyDesc = voyageData.Description ?? "";
            string voyVesselId = voyageData.VesselId ?? "";

            List<MissionDescription> missionDescs = voyageData.Missions ?? new List<MissionDescription>();

            if (String.IsNullOrEmpty(voyVesselId) || missionDescs.Count == 0)
            {
                if (!String.IsNullOrWhiteSpace(voyageData.ObjectiveId))
                {
                    return new
                    {
                        type = "command.error",
                        action = "create_voyage",
                        error = "objectiveId needs a vesselId and at least one mission; a bare voyage is not linked to an objective.",
                        code = "objective_requires_dispatch"
                    };
                }

                Voyage bareVoyage = new Voyage(voyTitle, voyDesc);
                bareVoyage.TenantId = Armada.Core.Authorization.OwnershipPolicy.TenantOf(caller);
                bareVoyage.UserId = Armada.Core.Authorization.OwnershipPolicy.UserOf(caller);
                bareVoyage = await _Database.Voyages.CreateAsync(bareVoyage).ConfigureAwait(false);
                return new { type = "command.result", action = "create_voyage", data = (object)bareVoyage };
            }

            SharedVoyageDispatchRequest dispatchRequest = new SharedVoyageDispatchRequest
            {
                Title = voyTitle,
                Description = voyDesc,
                VesselId = voyVesselId,
                Missions = missionDescs,
                CodeContextMode = voyageData.CodeContextMode,
                CodeContextTokenBudget = voyageData.CodeContextTokenBudget,
                CodeContextMaxResults = voyageData.CodeContextMaxResults,
                PipelineId = voyageData.PipelineId,
                Pipeline = voyageData.Pipeline,
                ObjectiveId = voyageData.ObjectiveId,
                ForcePreflight = voyageData.ForcePreflight,
                ObjectiveAuthContext = caller,
                SelectedPlaybooks = voyageData.SelectedPlaybooks ?? new List<SelectedPlaybook>(),
                Settings = _Settings,
                CaptainAssignments = voyageData.CaptainAssignments,
                SkipStages = voyageData.SkipStages,
                SkipStagesReason = voyageData.SkipStagesReason
            };

            VoyageDispatchResult result = await VoyageDispatchFactory().DispatchAsync(dispatchRequest).ConfigureAwait(false);
            if (result.Succeeded)
                return new { type = "command.result", action = "create_voyage", data = result.Value };

            WebSocketDispatchRefusal refusal = WebSocketDispatchRefusal.From(result);
            return new
            {
                type = "command.error",
                action = "create_voyage",
                error = refusal.Error,
                code = refusal.Code,
                status = result.StatusCode,
                detail = result.Value
            };
        }

        /// <summary>
        /// Run the <c>cancel_voyage</c> command.
        /// </summary>
        private async Task<object> CancelVoyageCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            string cvId = command.Id ?? "";
            Voyage? cvVoyage = await _Database.Voyages.ReadAsync(cvId).ConfigureAwait(false);
            if (cvVoyage == null)
                return new { type = "command.error", action = "cancel_voyage", error = "Voyage not found" };

            // The shared cancel stops the agent process of every running mission before it marks
            // the voyage and its missions Cancelled.
            VoyageCancellationResult cancellation = await VoyageCancellation.CancelAsync(
                _Database,
                cvVoyage,
                VoyageCancellation.OperatorCancelReason,
                _Admiral.RecallCaptainAsync).ConfigureAwait(false);

            // Command events follow the changed record's owner, like the same change made through REST.
            _BroadcastVoyageChange(cancellation.Voyage);
            foreach (Mission cvCm in cancellation.CancelledMissions)
            {
                _BroadcastMissionChange(cvCm);
            }
            return new { type = "command.result", action = "cancel_voyage", data = (object)new { Voyage = cancellation.Voyage, CancelledMissions = cancellation.CancelledMissions.Count } };
        }

        /// <summary>
        /// Run the <c>purge_voyage</c> command through the shared voyage purge REST and MCP use.
        /// </summary>
        private async Task<object> PurgeVoyageCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            string pvId = command.Id ?? "";
            Voyage? pvVoyage = await _Database.Voyages.ReadAsync(pvId).ConfigureAwait(false);
            if (pvVoyage == null)
                return NotFound("purge_voyage", "Voyage not found");
            WorkPurgeResult purge = await Operations.PurgeVoyageAsync(pvVoyage).ConfigureAwait(false);
            if (!purge.Succeeded)
                return new { type = "command.error", action = "purge_voyage", error = purge.Message, code = purge.Code };
            return new { type = "command.result", action = "purge_voyage", data = (object)new { status = "deleted", voyageId = pvId, missionsDeleted = purge.MissionsDeleted } };
        }

        /// <summary>
        /// Run the <c>list_missions</c> command.
        /// </summary>
        private async Task<object> ListMissionsCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            EnumerationQuery missionQuery = command.Query ?? new EnumerationQuery();
            Stopwatch missionSw = Stopwatch.StartNew();
            EnumerationResult<Mission> missionResult = await _Database.Missions.EnumerateSummariesAsync(missionQuery).ConfigureAwait(false);
            missionResult.TotalMs = Math.Round(missionSw.Elapsed.TotalMilliseconds, 2);
            return new { type = "command.result", action = "list_missions", data = (object)missionResult };
        }

        /// <summary>
        /// Run the <c>list_missions_summary</c> command.
        /// </summary>
        private async Task<object> ListMissionsSummaryCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            // Reads through the same caller-scoped query as REST, so a session receives exactly the
            // summaries REST returns to the same caller. There is no default caller to fall back to.
            if (caller == null || !caller.IsAuthenticated)
                return new { type = "command.error", action = "list_missions_summary", error = "list_missions_summary requires an authenticated caller" };
            EnumerationResult<MissionSummary> summaryResult = await MissionSummaryQuery.EnumerateForCallerAsync(
                _Database, caller, command.Query ?? new EnumerationQuery()).ConfigureAwait(false);
            return new { type = "command.result", action = "list_missions_summary", data = (object)summaryResult };
        }

        /// <summary>
        /// Run the <c>get_mission</c> command.
        /// </summary>
        private async Task<object> GetMissionCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            string getMissionId = command.Id ?? "";
            Mission? foundMission = await _Database.Missions.ReadSummaryAsync(getMissionId).ConfigureAwait(false);
            if (foundMission == null)
                return new { type = "command.error", action = "get_mission", error = "Mission not found" };
            else
                return new { type = "command.result", action = "get_mission", data = (object)foundMission };
        }

        /// <summary>
        /// Run the <c>create_mission</c> command.
        /// </summary>
        private async Task<object> CreateMissionCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            Mission newMission = JsonSerializer.Deserialize<WebSocketDataCommand<Mission>>(rawBody, _JsonOptions)?.Data!;
            newMission.TenantId = Armada.Core.Authorization.OwnershipPolicy.TenantOf(caller);
            newMission.UserId = Armada.Core.Authorization.OwnershipPolicy.UserOf(caller);
            string? unreachable = await MissionReferenceScope.FindUnreachableOnCreateAsync(_Database, caller, newMission).ConfigureAwait(false);
            if (unreachable != null) return NotFound("create_mission", unreachable);
            await MissionDefaultPlaybooks.MergeVesselDefaultsAsync(_Database, newMission).ConfigureAwait(false);
            try
            {
                newMission = await _Admiral.DispatchMissionAsync(newMission).ConfigureAwait(false);
            }
            catch (DispatchHoldActiveException held)
            {
                DispatchHoldRefusal refusal = DispatchHoldRefusal.From(held.Hold);
                return new
                {
                    type = "command.error",
                    action = "create_mission",
                    error = refusal.Error,
                    code = refusal.Code,
                    setBy = refusal.SetBy,
                    setByUtc = refusal.SetByUtc,
                    reason = refusal.Reason
                };
            }
            catch (FleetCapacityAdmissionException capacity)
            {
                return new
                {
                    type = "command.error",
                    action = "create_mission",
                    error = capacity.Message,
                    code = capacity.Code,
                    activeCount = capacity.ActiveCount,
                    limit = capacity.Limit,
                    candidateVesselId = capacity.CandidateVesselId,
                    laneMembers = capacity.LaneMembers
                };
            }
            if (newMission.Status == MissionStatusEnum.Pending)
            {
                return new { type = "command.result", action = "create_mission", data = (object)newMission, warning = "Mission created but could not be assigned to any captain. It will be retried on the next health check cycle." };
            }
            else
            {
                return new { type = "command.result", action = "create_mission", data = (object)newMission };
            }
        }

        /// <summary>
        /// Run the <c>update_mission</c> command. Only metadata fields are written, by the rule REST applies, so status
        /// changes go through <c>transition_mission_status</c> and its gates.
        /// </summary>
        private async Task<object> UpdateMissionCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            string updMissionId = command.Id ?? "";
            Mission? existMission = await _Database.Missions.ReadAsync(updMissionId).ConfigureAwait(false);
            if (existMission == null)
                return NotFound("update_mission", "Mission not found");
            Mission incoming = JsonSerializer.Deserialize<WebSocketDataCommand<Mission>>(rawBody, _JsonOptions)?.Data ?? new Mission();
            // The shared metadata update REST and MCP use: only named fields change, and a changed link must be visible.
            MissionMetadataPatch patch = MissionMetadataPatch.FromBody(incoming, ReadDataFieldNames(rawBody));
            MissionUpdateResult update = await Operations.UpdateMissionMetadataAsync(existMission, patch, caller).ConfigureAwait(false);
            if (update.LinkNotFound)
                return NotFound("update_mission", update.Message ?? "Mission not found");
            if (!update.Succeeded)
                return new { type = "command.error", action = "update_mission", error = update.Message, code = update.Code };
            return new { type = "command.result", action = "update_mission", data = (object)update.Mission };
        }

        /// <summary>
        /// Run the <c>transition_mission_status</c> command.
        /// </summary>
        private async Task<object> TransitionMissionStatusCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            string tmId = command.Id ?? "";
            string tmStatus = command.Status ?? "";
            Mission? tmMission = await _Database.Missions.ReadAsync(tmId).ConfigureAwait(false);
            if (tmMission == null)
            {
                return new { type = "command.error", action = "transition_mission_status", error = "Mission not found" };
            }
            else if (!Enum.TryParse<MissionStatusEnum>(tmStatus, true, out MissionStatusEnum tmNewStatus))
            {
                return new { type = "command.error", action = "transition_mission_status", error = "Invalid status: " + tmStatus };
            }
            else if (_StatusTransitions == null)
            {
                return new { type = "command.error", action = "transition_mission_status", error = MissionStatusTransitionService.UnavailableMessage };
            }
            else
            {
                // The shared operator transition path applies the same validation, manual
                // completion gates, landing, and handoff as the REST status route.
                MissionStatusTransitionResult tmResult = await _StatusTransitions.TransitionAsync(tmMission, tmNewStatus).ConfigureAwait(false);
                if (tmResult.Outcome == MissionStatusTransitionOutcomeEnum.Applied)
                    return new { type = "command.result", action = "transition_mission_status", data = (object)tmResult.Mission! };
                return new { type = "command.error", action = "transition_mission_status", error = tmResult.Message, reason = tmResult.Reason };
            }
        }

        /// <summary>
        /// Run the <c>cancel_mission</c> command through the shared mission cancel REST and MCP use.
        /// </summary>
        private async Task<object> CancelMissionCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            string cmId = command.Id ?? "";
            Mission? cmMission = await _Database.Missions.ReadAsync(cmId).ConfigureAwait(false);
            if (cmMission == null)
                return NotFound("cancel_mission", "Mission not found");
            MissionCancellationResult cancellation = await Operations.CancelMissionAsync(cmMission).ConfigureAwait(false);
            if (!cancellation.Succeeded)
                return new { type = "command.error", action = "cancel_mission", error = cancellation.Message, code = cancellation.Code };
            return new { type = "command.result", action = "cancel_mission", data = (object)cancellation.Mission };
        }

        /// <summary>
        /// Run the <c>purge_mission</c> command through the shared mission purge REST and MCP use.
        /// </summary>
        private async Task<object> PurgeMissionCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            string pmId = command.Id ?? "";
            Mission? pmMission = await _Database.Missions.ReadAsync(pmId).ConfigureAwait(false);
            if (pmMission == null)
                return NotFound("purge_mission", "Mission not found");
            WorkPurgeResult purge = await Operations.PurgeMissionAsync(pmMission).ConfigureAwait(false);
            if (!purge.Succeeded)
                return new { type = "command.error", action = "purge_mission", error = purge.Message, code = purge.Code };
            return new { type = "command.result", action = "purge_mission", data = (object)new { status = "deleted", missionId = pmId } };
        }

        /// <summary>
        /// Run the <c>restart_mission</c> command through the shared restart REST and MCP use: only a Failed or Cancelled
        /// mission is restarted, and a LandingFailed mission is refused with a pointer to retry-landing.
        /// </summary>
        private async Task<object> RestartMissionCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            string rmId = command.Id ?? "";
            Mission? rmMission = await _Database.Missions.ReadAsync(rmId).ConfigureAwait(false);
            if (rmMission == null)
                return NotFound("restart_mission", "Mission not found");

            MissionRestartData? rmData;
            try
            {
                rmData = JsonSerializer.Deserialize<WebSocketDataCommand<MissionRestartData>>(rawBody, _JsonOptions)?.Data;
            }
            catch (JsonException)
            {
                return new { type = "command.error", action = "restart_mission", error = "The command data could not be read as a restart request." };
            }

            MissionRestartResult restart;
            try
            {
                restart = await Operations.RestartMissionAsync(rmMission, rmData?.Title, rmData?.Description).ConfigureAwait(false);
            }
            catch (FleetCapacityAdmissionException capacity)
            {
                return new
                {
                    type = "command.error",
                    action = "restart_mission",
                    error = capacity.Message,
                    code = capacity.Code,
                    activeCount = capacity.ActiveCount,
                    limit = capacity.Limit,
                    candidateVesselId = capacity.CandidateVesselId,
                    laneMembers = capacity.LaneMembers
                };
            }

            if (!restart.Succeeded)
                return new { type = "command.error", action = "restart_mission", error = restart.Message, code = restart.Code };
            return new { type = "command.result", action = "restart_mission", data = (object)restart.Mission };
        }

        /// <summary>
        /// Run the <c>get_mission_diff</c> command through the shared diff reader REST and MCP use: the saved diff file,
        /// then the stored snapshot, then the live worktree.
        /// </summary>
        private async Task<object> GetMissionDiffCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            string mdId = command.Id ?? "";
            Mission? mdMission = await _Database.Missions.ReadAsync(mdId).ConfigureAwait(false);
            if (mdMission == null)
                return NotFound("get_mission_diff", "Mission not found");
            if (_Settings == null)
                return new { type = "command.error", action = "get_mission_diff", error = "Diff not available — settings not configured" };
            MissionDiffResult diff = await MissionDiffReader.ReadAsync(_Database, _Settings.LogDirectory, _Git, mdMission).ConfigureAwait(false);
            if (!diff.Available)
                return new { type = "command.error", action = "get_mission_diff", error = diff.Message };
            return new { type = "command.result", action = "get_mission_diff", data = (object)new { MissionId = mdId, Branch = diff.Branch, Diff = diff.Diff } };
        }

        /// <summary>
        /// Run the <c>get_mission_log</c> command through the shared log reader REST and MCP use, so the page is
        /// resolved, clamped and redacted alike.
        /// </summary>
        private async Task<object> GetMissionLogCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            string mlId = command.Id ?? "";
            Mission? mlMission = await _Database.Missions.ReadSummaryAsync(mlId).ConfigureAwait(false);
            if (mlMission == null)
                return new { type = "command.error", action = "get_mission_log", error = "Mission not found" };
            if (_Settings == null)
                return new { type = "command.error", action = "get_mission_log", error = "Logs not available — settings not configured" };
            MissionLogResponse mlPage = await SessionLogReader.ReadMissionLogAsync(
                _Settings.LogDirectory, mlMission.Id, command.Offset, command.Lines, 100).ConfigureAwait(false);
            return new { type = "command.result", action = "get_mission_log", data = (object)new { MissionId = mlPage.MissionId, Log = mlPage.Log, Lines = mlPage.Lines, TotalLines = mlPage.TotalLines } };
        }

        /// <summary>
        /// Run the <c>list_captains</c> command.
        /// </summary>
        private async Task<object> ListCaptainsCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            EnumerationQuery captainQuery = command.Query ?? new EnumerationQuery();
            Stopwatch captainSw = Stopwatch.StartNew();
            EnumerationResult<Captain> captainResult = await _Database.Captains.EnumerateAsync(captainQuery).ConfigureAwait(false);
            captainResult.TotalMs = Math.Round(captainSw.Elapsed.TotalMilliseconds, 2);
            return new { type = "command.result", action = "list_captains", data = (object)captainResult };
        }

        /// <summary>
        /// Run the <c>get_captain</c> command.
        /// </summary>
        private async Task<object> GetCaptainCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            string getCaptainId = command.Id ?? "";
            Captain? foundCaptain = await _Database.Captains.ReadAsync(getCaptainId).ConfigureAwait(false);
            if (foundCaptain == null)
                return new { type = "command.error", action = "get_captain", error = "Captain not found" };
            else
                return new { type = "command.result", action = "get_captain", data = (object)foundCaptain };
        }

        /// <summary>
        /// Run the <c>create_captain</c> command.
        /// </summary>
        private async Task<object> CreateCaptainCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            Captain? newCaptainInput = JsonSerializer.Deserialize<WebSocketDataCommand<Captain>>(rawBody, _JsonOptions)?.Data;
            if (newCaptainInput == null)
                return new { type = "command.error", action = "create_captain", error = "Captain data is required" };
            string? createOwnedFieldError = CaptainInputMapping.FindServerOwnedFieldViolation(
                JsonSerializer.Deserialize<WebSocketDataCommand<CaptainServerOwnedFields>>(rawBody, _JsonOptions)?.Data, null);
            if (createOwnedFieldError != null)
                return new { type = "command.error", action = "create_captain", error = createOwnedFieldError };
            // The shared captain create REST and MCP use: the name rule, runtime-option normalization and model validation.
            CaptainWriteResult created = await CaptainAdministration.CreateAsync(
                newCaptainInput,
                Armada.Core.Authorization.OwnershipPolicy.TenantOf(caller),
                Armada.Core.Authorization.OwnershipPolicy.UserOf(caller)).ConfigureAwait(false);
            if (!created.Succeeded)
                return new { type = "command.error", action = "create_captain", error = created.Message, code = created.Code };
            return new { type = "command.result", action = "create_captain", data = (object)created.Captain! };
        }

        /// <summary>
        /// Run the <c>update_captain</c> command.
        /// </summary>
        private async Task<object> UpdateCaptainCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            string updCptId = command.Id ?? "";
            Captain? existCpt = await _Database.Captains.ReadAsync(updCptId).ConfigureAwait(false);
            if (existCpt == null)
                return new { type = "command.error", action = "update_captain", error = "Captain not found" };
            else
            {
                Captain? updCptInput = JsonSerializer.Deserialize<WebSocketDataCommand<Captain>>(rawBody, _JsonOptions)?.Data;
                if (updCptInput == null)
                    return new { type = "command.error", action = "update_captain", error = "Captain data is required" };
                string? updateOwnedFieldError = CaptainInputMapping.FindServerOwnedFieldViolation(
                    JsonSerializer.Deserialize<WebSocketDataCommand<CaptainServerOwnedFields>>(rawBody, _JsonOptions)?.Data, existCpt);
                if (updateOwnedFieldError != null)
                    return new { type = "command.error", action = "update_captain", error = updateOwnedFieldError };
                // The shared captain update REST and MCP use: runtime-option normalization and model validation.
                CaptainWriteResult updated = await CaptainAdministration.UpdateAsync(existCpt, updCptInput).ConfigureAwait(false);
                if (!updated.Succeeded)
                    return new { type = "command.error", action = "update_captain", error = updated.Message, code = updated.Code };
                return new { type = "command.result", action = "update_captain", data = (object)updated.Captain! };
            }
        }

        /// <summary>
        /// Run the <c>delete_captain</c> command.
        /// </summary>
        private async Task<object> DeleteCaptainCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            CaptainDeletionResult deletion = await CaptainAdministration.DeleteAsync(command.Id ?? "", null).ConfigureAwait(false);
            if (deletion.Outcome != CaptainAdministrationOutcomeEnum.Completed)
                return new { type = "command.error", action = "delete_captain", error = deletion.Message };
            return new { type = "command.result", action = "delete_captain", data = (object)new { status = "deleted", dependentsRemoved = deletion.DependentsRemoved, dependentsSkipped = deletion.DependentsSkipped } };
        }

        /// <summary>
        /// Run the <c>get_captain_log</c> command through the shared log reader REST and MCP use, so the page is
        /// resolved, clamped and redacted alike.
        /// </summary>
        private async Task<object> GetCaptainLogCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            string clId = command.Id ?? "";
            Captain? clCaptain = await _Database.Captains.ReadAsync(clId).ConfigureAwait(false);
            if (clCaptain == null)
                return new { type = "command.error", action = "get_captain_log", error = "Captain not found" };
            if (_Settings == null)
                return new { type = "command.error", action = "get_captain_log", error = "Logs not available — settings not configured" };
            CaptainLogResponse clPage = await SessionLogReader.ReadCaptainLogAsync(
                _Settings.LogDirectory, clCaptain, command.Offset, command.Lines, 100).ConfigureAwait(false);
            return new { type = "command.result", action = "get_captain_log", data = (object)new { CaptainId = clPage.CaptainId, Log = clPage.Log, Lines = clPage.Lines, TotalLines = clPage.TotalLines } };
        }

        /// <summary>
        /// Run the <c>list_signals</c> command.
        /// </summary>
        private async Task<object> ListSignalsCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            EnumerationQuery signalQuery = command.Query ?? new EnumerationQuery();
            Stopwatch signalSw = Stopwatch.StartNew();
            EnumerationResult<Signal> signalResult = await _Database.Signals.EnumerateAsync(signalQuery).ConfigureAwait(false);
            signalResult.TotalMs = Math.Round(signalSw.Elapsed.TotalMilliseconds, 2);
            return new { type = "command.result", action = "list_signals", data = (object)signalResult };
        }

        /// <summary>
        /// Run the <c>send_signal</c> command.
        /// </summary>
        private async Task<object> SendSignalCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            Signal newSignal = JsonSerializer.Deserialize<WebSocketDataCommand<Signal>>(rawBody, _JsonOptions)?.Data!;
            newSignal.TenantId = Armada.Core.Authorization.OwnershipPolicy.TenantOf(caller);
            newSignal.UserId = Armada.Core.Authorization.OwnershipPolicy.UserOf(caller);
            newSignal = await _Database.Signals.CreateAsync(newSignal).ConfigureAwait(false);
            return new { type = "command.result", action = "send_signal", data = (object)newSignal };
        }

        /// <summary>
        /// Run the <c>list_events</c> command.
        /// </summary>
        private async Task<object> ListEventsCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            EnumerationQuery eventQuery = command.Query ?? new EnumerationQuery();
            Stopwatch eventSw = Stopwatch.StartNew();
            EnumerationResult<ArmadaEvent> eventResult = await _Database.Events.EnumerateAsync(eventQuery).ConfigureAwait(false);
            eventResult.TotalMs = Math.Round(eventSw.Elapsed.TotalMilliseconds, 2);
            return new { type = "command.result", action = "list_events", data = (object)eventResult };
        }

        /// <summary>
        /// Run the <c>list_docks</c> command.
        /// </summary>
        private async Task<object> ListDocksCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            EnumerationQuery dockQuery = command.Query ?? new EnumerationQuery();
            Stopwatch dockSw = Stopwatch.StartNew();
            EnumerationResult<Dock> dockResult = await _Database.Docks.EnumerateAsync(dockQuery).ConfigureAwait(false);
            dockResult.TotalMs = Math.Round(dockSw.Elapsed.TotalMilliseconds, 2);
            return new { type = "command.result", action = "list_docks", data = (object)dockResult };
        }

        /// <summary>
        /// Run the <c>list_merge_queue</c> command.
        /// </summary>
        private async Task<object> ListMergeQueueCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            EnumerationQuery mergeQuery = command.Query ?? new EnumerationQuery();
            Stopwatch mergeSw = Stopwatch.StartNew();
            List<MergeEntry> mergeAll = await _MergeQueue.ListAsync().ConfigureAwait(false);
            int mergeTotal = mergeAll.Count;
            List<MergeEntry> mergePage = mergeAll.Skip(mergeQuery.Offset).Take(mergeQuery.PageSize).ToList();
            EnumerationResult<MergeEntry> mergeResult = EnumerationResult<MergeEntry>.Create(mergeQuery, mergePage, mergeTotal);
            mergeResult.TotalMs = Math.Round(mergeSw.Elapsed.TotalMilliseconds, 2);
            return new { type = "command.result", action = "list_merge_queue", data = (object)mergeResult };
        }

        /// <summary>
        /// Run the <c>get_merge_entry</c> command.
        /// </summary>
        private async Task<object> GetMergeEntryCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            string meId = command.Id ?? "";
            MergeEntry? foundEntry = await _MergeQueue.GetAsync(meId).ConfigureAwait(false);
            if (foundEntry == null)
                return new { type = "command.error", action = "get_merge_entry", error = "Merge entry not found" };
            else
                return new { type = "command.result", action = "get_merge_entry", data = (object)foundEntry };
        }

        /// <summary>
        /// Run the <c>enqueue_merge</c> command.
        /// </summary>
        private async Task<object> EnqueueMergeCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            MergeEntry newEntry = JsonSerializer.Deserialize<WebSocketDataCommand<MergeEntry>>(rawBody, _JsonOptions)?.Data!;
            newEntry.TenantId = Armada.Core.Authorization.OwnershipPolicy.TenantOf(caller);
            newEntry.UserId = Armada.Core.Authorization.OwnershipPolicy.UserOf(caller);
            newEntry = await _MergeQueue.EnqueueAsync(newEntry).ConfigureAwait(false);
            return new { type = "command.result", action = "enqueue_merge", data = (object)newEntry };
        }

        /// <summary>
        /// Run the <c>cancel_merge</c> command.
        /// </summary>
        private async Task<object> CancelMergeCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            // The shared merge cancel REST and MCP use: an unknown entry and a finished entry are refused.
            string cmEntryId = command.Id ?? "";
            MergeEntryCancellationResult cancel = await new MergeEntryCancellation(_MergeQueue, Operations.Notifier)
                .CancelAsync(cmEntryId, null).ConfigureAwait(false);
            if (cancel.NotFound)
                return NotFound("cancel_merge", cancel.Message ?? "Merge entry not found");
            if (!cancel.Succeeded)
                return new { type = "command.error", action = "cancel_merge", error = cancel.Message, code = cancel.Code };
            return new { type = "command.result", action = "cancel_merge", data = (object)new { status = "cancelled", entry = cancel.Entry } };
        }

        /// <summary>
        /// Run the <c>process_merge_queue</c> command.
        /// </summary>
        private async Task<object> ProcessMergeQueueCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            await _MergeQueue.ProcessQueueAsync().ConfigureAwait(false);
            return new { type = "command.result", action = "process_merge_queue", data = (object)new { status = "processed" } };
        }

        /// <summary>
        /// Run the <c>enumerate</c> command.
        /// </summary>
        private async Task<object> EnumerateCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            string entityType = (command.EntityType ?? "").ToLowerInvariant();
            EnumerationQuery enumQuery = command.Query ?? new EnumerationQuery();
            Stopwatch enumSw = Stopwatch.StartNew();

            object? enumData = null;
            switch (entityType)
            {
                case "fleets":
                case "fleet":
                    EnumerationResult<Fleet> enumFleets = await _Database.Fleets.EnumerateAsync(enumQuery).ConfigureAwait(false);
                    enumFleets.TotalMs = Math.Round(enumSw.Elapsed.TotalMilliseconds, 2);
                    enumData = enumFleets;
                    break;
                case "vessels":
                case "vessel":
                    EnumerationResult<Vessel> enumVessels = await _Database.Vessels.EnumerateAsync(enumQuery).ConfigureAwait(false);
                    enumVessels.TotalMs = Math.Round(enumSw.Elapsed.TotalMilliseconds, 2);
                    enumData = enumVessels;
                    break;
                case "captains":
                case "captain":
                    EnumerationResult<Captain> enumCaptains = await _Database.Captains.EnumerateAsync(enumQuery).ConfigureAwait(false);
                    enumCaptains.TotalMs = Math.Round(enumSw.Elapsed.TotalMilliseconds, 2);
                    enumData = enumCaptains;
                    break;
                case "missions":
                case "mission":
                    EnumerationResult<Mission> enumMissions = await _Database.Missions.EnumerateSummariesAsync(enumQuery).ConfigureAwait(false);
                    enumMissions.TotalMs = Math.Round(enumSw.Elapsed.TotalMilliseconds, 2);
                    enumData = enumMissions;
                    break;
                case "voyages":
                case "voyage":
                    EnumerationResult<Voyage> enumVoyages = await _Database.Voyages.EnumerateAsync(enumQuery).ConfigureAwait(false);
                    enumVoyages.TotalMs = Math.Round(enumSw.Elapsed.TotalMilliseconds, 2);
                    enumData = enumVoyages;
                    break;
                case "docks":
                case "dock":
                    EnumerationResult<Dock> enumDocks = await _Database.Docks.EnumerateAsync(enumQuery).ConfigureAwait(false);
                    enumDocks.TotalMs = Math.Round(enumSw.Elapsed.TotalMilliseconds, 2);
                    enumData = enumDocks;
                    break;
                case "signals":
                case "signal":
                    EnumerationResult<Signal> enumSignals = await _Database.Signals.EnumerateAsync(enumQuery).ConfigureAwait(false);
                    enumSignals.TotalMs = Math.Round(enumSw.Elapsed.TotalMilliseconds, 2);
                    enumData = enumSignals;
                    break;
                case "events":
                case "event":
                    EnumerationResult<ArmadaEvent> enumEvents = await _Database.Events.EnumerateAsync(enumQuery).ConfigureAwait(false);
                    enumEvents.TotalMs = Math.Round(enumSw.Elapsed.TotalMilliseconds, 2);
                    enumData = enumEvents;
                    break;
                case "merge_queue":
                case "merge-queue":
                case "mergequeue":
                    List<MergeEntry> enumMqAll = await _MergeQueue.ListAsync().ConfigureAwait(false);
                    int enumMqTotal = enumMqAll.Count;
                    List<MergeEntry> enumMqPage = enumMqAll.Skip(enumQuery.Offset).Take(enumQuery.PageSize).ToList();
                    EnumerationResult<MergeEntry> enumMerge = EnumerationResult<MergeEntry>.Create(enumQuery, enumMqPage, enumMqTotal);
                    enumMerge.TotalMs = Math.Round(enumSw.Elapsed.TotalMilliseconds, 2);
                    enumData = enumMerge;
                    break;
            }

            if (enumData == null)
                return new { type = "command.error", action = "enumerate", error = "Unknown entity type: " + entityType + ". Valid types: fleets, vessels, captains, missions, voyages, docks, signals, events, merge_queue" };
            else
                return new { type = "command.result", action = "enumerate", data = enumData };
        }

        /// <summary>
        /// Run the <c>backup</c> command.
        /// </summary>
        private async Task<object> BackupCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            if (_Backups == null) return new { type = "command.error", action = "backup", error = "backup_unavailable" };
            try
            {
                DatabaseBackupResult backupData = await _Backups.BackupAsync(command.OutputPath).ConfigureAwait(false);
                return new { type = "command.result", action = "backup", data = (object)backupData };
            }
            catch (DatabaseBackupException ex)
            {
                return new { type = "command.error", action = "backup", error = ex.FailureReason };
            }
        }

        /// <summary>
        /// Run the <c>restore</c> command.
        /// </summary>
        private async Task<object> RestoreCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            string restoreFilePath = command.FilePath ?? "";
            if (String.IsNullOrEmpty(restoreFilePath))
            {
                return new { type = "command.error", action = "restore", error = "filePath is required" };
            }
            if (_Backups == null) return new { type = "command.error", action = "restore", error = "backup_unavailable" };
            try
            {
                DatabaseRestoreResult restoreData = await _Backups.RestoreAsync(restoreFilePath).ConfigureAwait(false);
                return new { type = "command.result", action = "restore", data = (object)restoreData };
            }
            catch (DatabaseBackupException ex)
            {
                return new { type = "command.error", action = "restore", error = ex.FailureReason };
            }
        }

        /// <summary>
        /// Run the <c>get_persona</c> command. The persona is found through the shared caller scope REST and MCP use.
        /// </summary>
        private async Task<object> GetPersonaCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            Persona? foundPersona = await ReadVisiblePersonaAsync(caller, command.Id).ConfigureAwait(false);
            if (foundPersona == null)
                return NotFound("get_persona", "Persona not found");
            return new { type = "command.result", action = "get_persona", data = (object)foundPersona };
        }

        /// <summary>
        /// Run the <c>create_persona</c> command. Ownership comes from the caller, a request cannot create a built-in
        /// persona, and a default captain passes the shared default captain rule.
        /// </summary>
        private async Task<object> CreatePersonaCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            Persona newPersona = JsonSerializer.Deserialize<WebSocketDataCommand<Persona>>(rawBody, _JsonOptions)?.Data!;
            string? createRetiredFieldError = JsonSerializer.Deserialize<WebSocketDataCommand<PersonaRoutingUpdate>>(rawBody, _JsonOptions)?.Data?.RetiredFieldError();
            if (createRetiredFieldError != null)
                return new { type = "command.error", action = "create_persona", error = createRetiredFieldError };
            newPersona.TenantId = Armada.Core.Authorization.OwnershipPolicy.TenantOf(caller);
            newPersona.UserId = Armada.Core.Authorization.OwnershipPolicy.UserOf(caller);
            newPersona.IsBuiltIn = false;
            string? defaultCaptainError = await Armada.Core.Services.PersonaDefaultCaptainRule.ApplyAsync(_Database, newPersona, newPersona.DefaultCaptainId).ConfigureAwait(false);
            if (defaultCaptainError != null)
                return new { type = "command.error", action = "create_persona", error = defaultCaptainError };
            newPersona = await _Database.Personas.CreateAsync(newPersona).ConfigureAwait(false);
            return new { type = "command.result", action = "create_persona", data = (object)newPersona };
        }

        /// <summary>
        /// Run the <c>update_persona</c> command. The persona is found through the shared caller scope and changed
        /// only when the shared ownership rule lets the caller edit it.
        /// </summary>
        private async Task<object> UpdatePersonaCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            Persona? existPersona = await ReadVisiblePersonaAsync(caller, command.Id).ConfigureAwait(false);
            if (existPersona == null)
                return NotFound("update_persona", "Persona not found");
            if (!Armada.Core.Authorization.OwnershipPolicy.CanEdit(caller, existPersona))
                return Forbidden("update_persona", "You may not change this persona");
            Persona patchPersona = JsonSerializer.Deserialize<WebSocketDataCommand<Persona>>(rawBody, _JsonOptions)?.Data!;
            if (patchPersona.Description != null) existPersona.Description = patchPersona.Description;
            if (patchPersona.PromptTemplateName != null) existPersona.PromptTemplateName = patchPersona.PromptTemplateName;
            PersonaRoutingUpdate? patchRouting = JsonSerializer.Deserialize<WebSocketDataCommand<PersonaRoutingUpdate>>(rawBody, _JsonOptions)?.Data;
            string? updateRetiredFieldError = patchRouting?.RetiredFieldError();
            if (updateRetiredFieldError != null)
                return new { type = "command.error", action = "update_persona", error = updateRetiredFieldError };
            if (patchRouting?.MinimumTierSupplied == true) existPersona.MinimumTier = patchRouting.MinimumTier;
            if (patchRouting != null && patchRouting.DefaultCaptainIdSupplied)
            {
                string? defaultCaptainError = await Armada.Core.Services.PersonaDefaultCaptainRule.ApplyAsync(_Database, existPersona, patchRouting.DefaultCaptainId).ConfigureAwait(false);
                if (defaultCaptainError != null)
                    return new { type = "command.error", action = "update_persona", error = defaultCaptainError };
            }
            existPersona = await _Database.Personas.UpdateAsync(existPersona).ConfigureAwait(false);
            return new { type = "command.result", action = "update_persona", data = (object)existPersona };
        }

        /// <summary>
        /// Run the <c>delete_persona</c> command. The persona is found through the shared caller scope and deleted
        /// only when the shared ownership rule lets the caller edit it.
        /// </summary>
        private async Task<object> DeletePersonaCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            string delPersonaName = command.Id ?? "";
            Persona? delPersona = await ReadVisiblePersonaAsync(caller, delPersonaName).ConfigureAwait(false);
            if (delPersona == null)
                return NotFound("delete_persona", "Persona not found");
            if (!Armada.Core.Authorization.OwnershipPolicy.CanEdit(caller, delPersona))
                return Forbidden("delete_persona", "You may not delete this persona");
            if (delPersona.IsBuiltIn)
                return new { type = "command.error", action = "delete_persona", error = "Cannot delete built-in persona" };
            await _Database.Personas.DeleteAsync(delPersona.Id).ConfigureAwait(false);
            return new { type = "command.result", action = "delete_persona", data = (object)new { Status = "deleted", Name = delPersonaName } };
        }

        /// <summary>
        /// Run the <c>get_prompt_template</c> command. The template is found through the shared caller scope REST and
        /// MCP use.
        /// </summary>
        private async Task<object> GetPromptTemplateCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            string getTemplateName = command.Id ?? "";
            PromptTemplate? foundTemplate = String.IsNullOrEmpty(getTemplateName)
                ? null
                : await Armada.Core.Services.OwnedRecordScope.ReadByNameAsync(
                    caller,
                    getTemplateName,
                    (tenantId, templateName) => _Database.PromptTemplates.ReadByNameAsync(tenantId, templateName),
                    () => _Database.PromptTemplates.EnumerateAsync(),
                    record => record.Name).ConfigureAwait(false);
            if (foundTemplate == null)
                return NotFound("get_prompt_template", "Prompt template not found");
            return new { type = "command.result", action = "get_prompt_template", data = (object)foundTemplate };
        }

        /// <summary>
        /// Run the <c>update_prompt_template</c> command.
        /// </summary>
        private async Task<object> UpdatePromptTemplateCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            string updTemplateName = command.Id ?? "";
            PromptTemplate? existTemplate = await _Database.PromptTemplates.ReadByNameAsync(updTemplateName).ConfigureAwait(false);
            if (existTemplate == null)
                return new { type = "command.error", action = "update_prompt_template", error = "Prompt template not found" };
            else
            {
                PromptTemplate patchTemplate = JsonSerializer.Deserialize<WebSocketDataCommand<PromptTemplate>>(rawBody, _JsonOptions)?.Data!;
                if (patchTemplate.Content != null) existTemplate.Content = patchTemplate.Content;
                if (patchTemplate.Description != null) existTemplate.Description = patchTemplate.Description;
                existTemplate = await _Database.PromptTemplates.UpdateAsync(existTemplate).ConfigureAwait(false);
                return new { type = "command.result", action = "update_prompt_template", data = (object)existTemplate };
            }
        }

        /// <summary>
        /// Run the <c>get_pipeline</c> command. The pipeline is found through the shared caller scope REST and MCP use.
        /// </summary>
        private async Task<object> GetPipelineCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            Pipeline? foundPipeline = await ReadVisiblePipelineAsync(caller, command.Id).ConfigureAwait(false);
            if (foundPipeline == null)
                return NotFound("get_pipeline", "Pipeline not found");
            return new { type = "command.result", action = "get_pipeline", data = (object)foundPipeline };
        }

        /// <summary>
        /// Run the <c>create_pipeline</c> command. Ownership comes from the caller, and a request cannot create a
        /// built-in pipeline.
        /// </summary>
        private async Task<object> CreatePipelineCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            Pipeline newPipeline = JsonSerializer.Deserialize<WebSocketDataCommand<Pipeline>>(rawBody, _JsonOptions)?.Data!;
            newPipeline.TenantId = Armada.Core.Authorization.OwnershipPolicy.TenantOf(caller);
            newPipeline.UserId = Armada.Core.Authorization.OwnershipPolicy.UserOf(caller);
            newPipeline.IsBuiltIn = false;
            newPipeline = await _Database.Pipelines.CreateAsync(newPipeline).ConfigureAwait(false);
            return new { type = "command.result", action = "create_pipeline", data = (object)newPipeline };
        }

        /// <summary>
        /// Run the <c>update_pipeline</c> command.
        /// </summary>
        private async Task<object> UpdatePipelineCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            Pipeline? existPipeline = await ReadVisiblePipelineAsync(caller, command.Id).ConfigureAwait(false);
            if (existPipeline == null)
                return NotFound("update_pipeline", "Pipeline not found");
            else if (!Armada.Core.Authorization.OwnershipPolicy.CanEdit(caller, existPipeline))
                return Forbidden("update_pipeline", "You may not change this pipeline");
            else
            {
                Pipeline patchPipeline = JsonSerializer.Deserialize<WebSocketDataCommand<Pipeline>>(rawBody, _JsonOptions)?.Data!;
                if (patchPipeline.Description != null) existPipeline.Description = patchPipeline.Description;
                if (patchPipeline.Stages != null && patchPipeline.Stages.Count > 0)
                {
                    existPipeline.Stages = patchPipeline.Stages;
                    foreach (PipelineStage stage in existPipeline.Stages)
                        stage.PipelineId = existPipeline.Id;
                }
                existPipeline = await _Database.Pipelines.UpdateAsync(existPipeline).ConfigureAwait(false);
                return new { type = "command.result", action = "update_pipeline", data = (object)existPipeline };
            }
        }

        /// <summary>
        /// Run the <c>delete_pipeline</c> command.
        /// </summary>
        private async Task<object> DeletePipelineCommandAsync(WebSocketCommand command, string rawBody, AuthContext caller)
        {
            string delPipelineName = command.Id ?? "";
            Pipeline? delPipeline = await ReadVisiblePipelineAsync(caller, delPipelineName).ConfigureAwait(false);
            if (delPipeline == null)
                return NotFound("delete_pipeline", "Pipeline not found");
            if (!Armada.Core.Authorization.OwnershipPolicy.CanEdit(caller, delPipeline))
                return Forbidden("delete_pipeline", "You may not delete this pipeline");
            if (delPipeline.IsBuiltIn)
                return new { type = "command.error", action = "delete_pipeline", error = "Cannot delete built-in pipeline" };
            await _Database.Pipelines.DeleteAsync(delPipeline.Id).ConfigureAwait(false);
            return new { type = "command.result", action = "delete_pipeline", data = (object)new { Status = "deleted", Name = delPipelineName } };
        }

        #endregion
    }
}
