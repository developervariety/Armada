namespace Armada.Server
{
    using System.Text.Json;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    /// <summary>
    /// Handles focused remote-control queries routed through the outbound tunnel.
    /// </summary>
    public class RemoteControlQueryService
    {
        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public RemoteControlQueryService(
            DatabaseDriver database,
            ArmadaSettings settings,
            IGitService git,
            Func<CancellationToken, Task<ArmadaStatus>> getStatusAsync,
            Func<RemoteTunnelStatus> getRemoteTunnelStatus,
            DateTime startUtc,
            Func<DateTime>? utcNow = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Git = git ?? throw new ArgumentNullException(nameof(git));
            _GetStatusAsync = getStatusAsync ?? throw new ArgumentNullException(nameof(getStatusAsync));
            _GetRemoteTunnelStatus = getRemoteTunnelStatus ?? throw new ArgumentNullException(nameof(getRemoteTunnelStatus));
            _StartUtc = startUtc;
            _UtcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Handle a focused tunnel query.
        /// </summary>
        public async Task<RemoteTunnelRequestResult> HandleAsync(RemoteTunnelEnvelope envelope, CancellationToken token)
        {
            string method = envelope.Method?.Trim().ToLowerInvariant() ?? String.Empty;
            RemoteTunnelQueryRequest request = DeserializeRequest(envelope);

            switch (method)
            {
                case "armada.instance.summary":
                    return await BuildInstanceSummaryAsync(token).ConfigureAwait(false);
                case "armada.status.snapshot":
                    return new RemoteTunnelRequestResult
                    {
                        StatusCode = 200,
                        Payload = await _GetStatusAsync(token).ConfigureAwait(false),
                        Message = "Armada status snapshot captured."
                    };
                case "armada.status.health":
                    return new RemoteTunnelRequestResult
                    {
                        StatusCode = 200,
                        Payload = BuildHealthPayload(),
                        Message = "Armada health snapshot captured."
                    };
                case "armada.settings.remotecontrol":
                    return new RemoteTunnelRequestResult
                    {
                        StatusCode = 200,
                        Payload = _Settings.RemoteControl,
                        Message = "Remote-control settings captured."
                    };
                case "armada.activity.recent":
                    return await GetRecentActivityAsync(request, token).ConfigureAwait(false);
                case "armada.missions.recent":
                    return await GetRecentMissionsAsync(request, token).ConfigureAwait(false);
                case "armada.voyages.recent":
                    return await GetRecentVoyagesAsync(request, token).ConfigureAwait(false);
                case "armada.captains.recent":
                    return await GetRecentCaptainsAsync(request, token).ConfigureAwait(false);
                case "armada.mission.detail":
                    return await GetMissionDetailAsync(request, token).ConfigureAwait(false);
                case "armada.mission.log":
                    return await GetMissionLogAsync(request, token).ConfigureAwait(false);
                case "armada.mission.diff":
                    return await GetMissionDiffAsync(request, token).ConfigureAwait(false);
                case "armada.voyage.detail":
                    return await GetVoyageDetailAsync(request, token).ConfigureAwait(false);
                case "armada.captain.detail":
                    return await GetCaptainDetailAsync(request, token).ConfigureAwait(false);
                case "armada.captain.log":
                    return await GetCaptainLogAsync(request, token).ConfigureAwait(false);
                default:
                    return new RemoteTunnelRequestResult
                    {
                        StatusCode = 404,
                        ErrorCode = "unsupported_method",
                        Message = "Unsupported tunnel method " + envelope.Method + "."
                    };
            }
        }

        #endregion

        #region Private-Methods

        private async Task<RemoteTunnelRequestResult> BuildInstanceSummaryAsync(CancellationToken token)
        {
            return new RemoteTunnelRequestResult
            {
                StatusCode = 200,
                Payload = new
                {
                    generatedUtc = _UtcNow(),
                    health = BuildHealthPayload(),
                    status = await _GetStatusAsync(token).ConfigureAwait(false),
                    recentActivity = (await BuildRecentActivityRowsAsync(12, token).ConfigureAwait(false)).ToList(),
                    recentMissions = (await BuildRecentMissionRowsAsync(8, token).ConfigureAwait(false)).ToList(),
                    recentVoyages = (await BuildRecentVoyageRowsAsync(6, token).ConfigureAwait(false)).ToList(),
                    recentCaptains = (await BuildRecentCaptainRowsAsync(8, token).ConfigureAwait(false)).ToList()
                },
                Message = "Remote instance summary captured."
            };
        }

        private object BuildHealthPayload()
        {
            DateTime nowUtc = _UtcNow();
            return new
            {
                Status = "healthy",
                StartUtc = _StartUtc,
                Timestamp = nowUtc,
                Uptime = (nowUtc - _StartUtc).ToString(@"d\.hh\:mm\:ss"),
                Version = Constants.ProductVersion,
                Ports = new
                {
                    Admiral = _Settings.AdmiralPort,
                    Mcp = _Settings.McpPort
                },
                RemoteTunnel = _GetRemoteTunnelStatus()
            };
        }

        private async Task<RemoteTunnelRequestResult> GetRecentActivityAsync(RemoteTunnelQueryRequest request, CancellationToken token)
        {
            int limit = Clamp(request.Limit, 20, 1, 100);
            return new RemoteTunnelRequestResult
            {
                StatusCode = 200,
                Payload = new
                {
                    limit = limit,
                    activity = (await BuildRecentActivityRowsAsync(limit, token).ConfigureAwait(false)).ToList()
                },
                Message = "Recent activity captured."
            };
        }

        private async Task<RemoteTunnelRequestResult> GetRecentMissionsAsync(RemoteTunnelQueryRequest request, CancellationToken token)
        {
            int limit = Clamp(request.Limit, 10, 1, 100);
            return new RemoteTunnelRequestResult
            {
                StatusCode = 200,
                Payload = new
                {
                    limit = limit,
                    missions = (await BuildRecentMissionRowsAsync(limit, token).ConfigureAwait(false)).ToList()
                },
                Message = "Recent missions captured."
            };
        }

        private async Task<RemoteTunnelRequestResult> GetRecentVoyagesAsync(RemoteTunnelQueryRequest request, CancellationToken token)
        {
            int limit = Clamp(request.Limit, 10, 1, 100);
            return new RemoteTunnelRequestResult
            {
                StatusCode = 200,
                Payload = new
                {
                    limit = limit,
                    voyages = (await BuildRecentVoyageRowsAsync(limit, token).ConfigureAwait(false)).ToList()
                },
                Message = "Recent voyages captured."
            };
        }

        private async Task<RemoteTunnelRequestResult> GetRecentCaptainsAsync(RemoteTunnelQueryRequest request, CancellationToken token)
        {
            int limit = Clamp(request.Limit, 10, 1, 100);
            return new RemoteTunnelRequestResult
            {
                StatusCode = 200,
                Payload = new
                {
                    limit = limit,
                    captains = (await BuildRecentCaptainRowsAsync(limit, token).ConfigureAwait(false)).ToList()
                },
                Message = "Recent captains captured."
            };
        }

        private async Task<RemoteTunnelRequestResult> GetMissionDetailAsync(RemoteTunnelQueryRequest request, CancellationToken token)
        {
            string missionId = request.MissionId?.Trim() ?? String.Empty;
            if (String.IsNullOrWhiteSpace(missionId))
            {
                return BadRequest("missing_mission_id", "MissionId is required.");
            }

            Mission? mission = await _Database.Missions.ReadSummaryAsync(missionId, token).ConfigureAwait(false);
            if (mission == null)
            {
                return NotFound("Mission not found.");
            }

            Captain? captain = !String.IsNullOrWhiteSpace(mission.CaptainId)
                ? await _Database.Captains.ReadAsync(mission.CaptainId, token).ConfigureAwait(false)
                : null;
            Voyage? voyage = !String.IsNullOrWhiteSpace(mission.VoyageId)
                ? await _Database.Voyages.ReadAsync(mission.VoyageId, token).ConfigureAwait(false)
                : null;
            Vessel? vessel = !String.IsNullOrWhiteSpace(mission.VesselId)
                ? await _Database.Vessels.ReadAsync(mission.VesselId, token).ConfigureAwait(false)
                : null;
            Dock? dock = await ResolveMissionDockAsync(mission, captain, token).ConfigureAwait(false);

            return new RemoteTunnelRequestResult
            {
                StatusCode = 200,
                Payload = new
                {
                    mission = mission,
                    captain = captain,
                    voyage = voyage,
                    vessel = vessel,
                    dock = dock
                },
                Message = "Mission detail captured."
            };
        }

        private async Task<RemoteTunnelRequestResult> GetVoyageDetailAsync(RemoteTunnelQueryRequest request, CancellationToken token)
        {
            string voyageId = request.VoyageId?.Trim() ?? String.Empty;
            if (String.IsNullOrWhiteSpace(voyageId))
            {
                return BadRequest("missing_voyage_id", "VoyageId is required.");
            }

            Voyage? voyage = await _Database.Voyages.ReadAsync(voyageId, token).ConfigureAwait(false);
            if (voyage == null)
            {
                return NotFound("Voyage not found.");
            }

            EnumerationResult<Mission> missions = await _Database.Missions.EnumerateSummariesAsync(new EnumerationQuery
            {
                VoyageId = voyageId,
                PageSize = 1000
            }, token).ConfigureAwait(false);

            return new RemoteTunnelRequestResult
            {
                StatusCode = 200,
                Payload = new
                {
                    voyage = voyage,
                    missions = missions.Objects.OrderByDescending(m => m.LastUpdateUtc).ThenByDescending(m => m.CreatedUtc).ToList()
                },
                Message = "Voyage detail captured."
            };
        }

        private async Task<RemoteTunnelRequestResult> GetCaptainDetailAsync(RemoteTunnelQueryRequest request, CancellationToken token)
        {
            string captainId = request.CaptainId?.Trim() ?? String.Empty;
            if (String.IsNullOrWhiteSpace(captainId))
            {
                return BadRequest("missing_captain_id", "CaptainId is required.");
            }

            Captain? captain = await _Database.Captains.ReadAsync(captainId, token).ConfigureAwait(false);
            if (captain == null)
            {
                return NotFound("Captain not found.");
            }

            Mission? currentMission = !String.IsNullOrWhiteSpace(captain.CurrentMissionId)
                ? await _Database.Missions.ReadSummaryAsync(captain.CurrentMissionId, token).ConfigureAwait(false)
                : null;
            Dock? currentDock = !String.IsNullOrWhiteSpace(captain.CurrentDockId)
                ? await _Database.Docks.ReadAsync(captain.CurrentDockId, token).ConfigureAwait(false)
                : null;
            EnumerationResult<Mission> missions = await _Database.Missions.EnumerateSummariesAsync(new EnumerationQuery
            {
                CaptainId = captainId,
                PageSize = 10
            }, token).ConfigureAwait(false);

            return new RemoteTunnelRequestResult
            {
                StatusCode = 200,
                Payload = new
                {
                    captain = captain,
                    currentMission = currentMission,
                    currentDock = currentDock,
                    recentMissions = missions.Objects
                        .OrderByDescending(m => m.LastUpdateUtc)
                        .ThenByDescending(m => m.CreatedUtc)
                        .Take(10)
                        .ToList()
                },
                Message = "Captain detail captured."
            };
        }

        private async Task<RemoteTunnelRequestResult> GetMissionLogAsync(RemoteTunnelQueryRequest request, CancellationToken token)
        {
            string missionId = request.MissionId?.Trim() ?? String.Empty;
            if (String.IsNullOrWhiteSpace(missionId))
            {
                return BadRequest("missing_mission_id", "MissionId is required.");
            }

            Mission? mission = await _Database.Missions.ReadSummaryAsync(missionId, token).ConfigureAwait(false);
            if (mission == null)
            {
                return NotFound("Mission not found.");
            }

            string logPath = Path.Combine(_Settings.LogDirectory, "missions", missionId + ".log");
            if (!File.Exists(logPath))
            {
                return new RemoteTunnelRequestResult
                {
                    StatusCode = 200,
                    Payload = new { missionId = missionId, log = "", lines = 0, totalLines = 0 },
                    Message = "Mission log captured."
                };
            }

            try
            {
                string[] allLines = await ReadLinesSharedAsync(logPath).ConfigureAwait(false);
                int offset = Math.Max(0, request.Offset);
                int lineCount = Clamp(request.Lines, 200, 1, 2000);
                string[] slice = allLines.Skip(offset).Take(lineCount).ToArray();

                return new RemoteTunnelRequestResult
                {
                    StatusCode = 200,
                    Payload = new
                    {
                        missionId = missionId,
                        log = String.Join("\n", slice),
                        lines = slice.Length,
                        totalLines = allLines.Length
                    },
                    Message = "Mission log captured."
                };
            }
            catch (IOException)
            {
                return new RemoteTunnelRequestResult
                {
                    StatusCode = 200,
                    Payload = new { missionId = missionId, log = "", lines = 0, totalLines = 0 },
                    Message = "Mission log captured."
                };
            }
        }

        private async Task<RemoteTunnelRequestResult> GetCaptainLogAsync(RemoteTunnelQueryRequest request, CancellationToken token)
        {
            string captainId = request.CaptainId?.Trim() ?? String.Empty;
            if (String.IsNullOrWhiteSpace(captainId))
            {
                return BadRequest("missing_captain_id", "CaptainId is required.");
            }

            Captain? captain = await _Database.Captains.ReadAsync(captainId, token).ConfigureAwait(false);
            if (captain == null)
            {
                return NotFound("Captain not found.");
            }

            string pointerPath = Path.Combine(_Settings.LogDirectory, "captains", captainId + ".current");
            string? logPath = null;

            if (File.Exists(pointerPath))
            {
                try
                {
                    string target = (await ReadFileSharedAsync(pointerPath).ConfigureAwait(false)).Trim();
                    if (File.Exists(target))
                    {
                        logPath = target;
                    }
                }
                catch (IOException)
                {
                }
            }

            if (String.IsNullOrWhiteSpace(logPath))
            {
                return new RemoteTunnelRequestResult
                {
                    StatusCode = 200,
                    Payload = new { captainId = captainId, log = "", lines = 0, totalLines = 0 },
                    Message = "Captain log captured."
                };
            }

            try
            {
                string[] allLines = await ReadLinesSharedAsync(logPath).ConfigureAwait(false);
                int offset = Math.Max(0, request.Offset);
                int lineCount = Clamp(request.Lines, 50, 1, 1000);
                string[] slice = allLines.Skip(offset).Take(lineCount).ToArray();

                return new RemoteTunnelRequestResult
                {
                    StatusCode = 200,
                    Payload = new
                    {
                        captainId = captainId,
                        log = String.Join("\n", slice),
                        lines = slice.Length,
                        totalLines = allLines.Length
                    },
                    Message = "Captain log captured."
                };
            }
            catch (IOException)
            {
                return new RemoteTunnelRequestResult
                {
                    StatusCode = 200,
                    Payload = new { captainId = captainId, log = "", lines = 0, totalLines = 0 },
                    Message = "Captain log captured."
                };
            }
        }

        private async Task<RemoteTunnelRequestResult> GetMissionDiffAsync(RemoteTunnelQueryRequest request, CancellationToken token)
        {
            string missionId = request.MissionId?.Trim() ?? String.Empty;
            if (String.IsNullOrWhiteSpace(missionId))
            {
                return BadRequest("missing_mission_id", "MissionId is required.");
            }

            Mission? mission = await _Database.Missions.ReadAsync(missionId, token).ConfigureAwait(false);
            if (mission == null)
            {
                return NotFound("Mission not found.");
            }

            if (!String.IsNullOrWhiteSpace(mission.DiffSnapshot))
            {
                return new RemoteTunnelRequestResult
                {
                    StatusCode = 200,
                    Payload = new
                    {
                        missionId = missionId,
                        branch = mission.BranchName ?? "",
                        diff = mission.DiffSnapshot,
                        source = "savedSnapshot"
                    },
                    Message = "Mission diff captured."
                };
            }

            Captain? captain = !String.IsNullOrWhiteSpace(mission.CaptainId)
                ? await _Database.Captains.ReadAsync(mission.CaptainId, token).ConfigureAwait(false)
                : null;
            Dock? dock = await ResolveMissionDockAsync(mission, captain, token).ConfigureAwait(false);

            if (dock == null || String.IsNullOrWhiteSpace(dock.WorktreePath) || !Directory.Exists(dock.WorktreePath))
            {
                return NotFound("No diff available because neither a saved diff snapshot nor a live worktree could be found.");
            }

            string baseBranch = "main";
            if (!String.IsNullOrWhiteSpace(mission.VesselId))
            {
                Vessel? vessel = await _Database.Vessels.ReadAsync(mission.VesselId, token).ConfigureAwait(false);
                if (vessel != null && !String.IsNullOrWhiteSpace(vessel.DefaultBranch))
                {
                    baseBranch = vessel.DefaultBranch;
                }
            }

            string diff = await _Git.DiffAsync(dock.WorktreePath, baseBranch, token).ConfigureAwait(false);
            return new RemoteTunnelRequestResult
            {
                StatusCode = 200,
                Payload = new
                {
                    missionId = missionId,
                    branch = dock.BranchName ?? mission.BranchName ?? "",
                    diff = diff,
                    source = "liveWorktree"
                },
                Message = "Mission diff captured."
            };
        }

        private async Task<IEnumerable<object>> BuildRecentActivityRowsAsync(int limit, CancellationToken token)
        {
            List<ArmadaEvent> events = await _Database.Events.EnumerateRecentAsync(limit, token).ConfigureAwait(false);
            return events.Select(evt => (object)new
            {
                id = evt.Id,
                eventType = evt.EventType,
                message = evt.Message,
                entityType = evt.EntityType,
                entityId = evt.EntityId,
                captainId = evt.CaptainId,
                missionId = evt.MissionId,
                vesselId = evt.VesselId,
                voyageId = evt.VoyageId,
                createdUtc = evt.CreatedUtc
            });
        }

        private async Task<IEnumerable<object>> BuildRecentMissionRowsAsync(int limit, CancellationToken token)
        {
            EnumerationResult<Mission> missions = await _Database.Missions.EnumerateSummariesAsync(new EnumerationQuery
            {
                PageSize = limit
            }, token).ConfigureAwait(false);
            return missions.Objects
                .OrderByDescending(m => m.LastUpdateUtc)
                .ThenByDescending(m => m.CreatedUtc)
                .Take(limit)
                .Select(mission => (object)new
                {
                    id = mission.Id,
                    title = mission.Title,
                    persona = mission.Persona,
                    status = mission.Status,
                    captainId = mission.CaptainId,
                    voyageId = mission.VoyageId,
                    branchName = mission.BranchName,
                    createdUtc = mission.CreatedUtc,
                    lastUpdateUtc = mission.LastUpdateUtc,
                    totalRuntimeMs = mission.TotalRuntimeMs,
                    failureReason = mission.FailureReason
                });
        }

        private async Task<IEnumerable<object>> BuildRecentVoyageRowsAsync(int limit, CancellationToken token)
        {
            List<Voyage> voyages = await _Database.Voyages.EnumerateAsync(token).ConfigureAwait(false);
            return voyages
                .OrderByDescending(v => v.LastUpdateUtc)
                .ThenByDescending(v => v.CreatedUtc)
                .Take(limit)
                .Select(voyage => (object)new
                {
                    id = voyage.Id,
                    title = voyage.Title,
                    status = voyage.Status,
                    createdUtc = voyage.CreatedUtc,
                    lastUpdateUtc = voyage.LastUpdateUtc,
                    completedUtc = voyage.CompletedUtc
                });
        }

        private async Task<IEnumerable<object>> BuildRecentCaptainRowsAsync(int limit, CancellationToken token)
        {
            List<Captain> captains = await _Database.Captains.EnumerateAsync(token).ConfigureAwait(false);
            return captains
                .OrderByDescending(c => c.LastUpdateUtc)
                .ThenByDescending(c => c.CreatedUtc)
                .Take(limit)
                .Select(captain => (object)new
                {
                    id = captain.Id,
                    name = captain.Name,
                    runtime = captain.Runtime,
                    model = captain.Model,
                    state = captain.State,
                    currentMissionId = captain.CurrentMissionId,
                    currentDockId = captain.CurrentDockId,
                    processId = captain.ProcessId,
                    lastHeartbeatUtc = captain.LastHeartbeatUtc,
                    lastUpdateUtc = captain.LastUpdateUtc
                });
        }

        private async Task<Dock?> ResolveMissionDockAsync(Mission mission, Captain? captain, CancellationToken token)
        {
            Dock? dock = null;
            if (!String.IsNullOrWhiteSpace(mission.DockId))
            {
                dock = await _Database.Docks.ReadAsync(mission.DockId, token).ConfigureAwait(false);
            }

            if (dock == null && captain != null && !String.IsNullOrWhiteSpace(captain.CurrentDockId))
            {
                dock = await _Database.Docks.ReadAsync(captain.CurrentDockId, token).ConfigureAwait(false);
            }

            if (dock == null && !String.IsNullOrWhiteSpace(mission.BranchName) && !String.IsNullOrWhiteSpace(mission.VesselId))
            {
                List<Dock> docks = await _Database.Docks.EnumerateByVesselAsync(mission.VesselId, token).ConfigureAwait(false);
                dock = docks.FirstOrDefault(d => d.Active && String.Equals(d.BranchName, mission.BranchName, StringComparison.OrdinalIgnoreCase));
            }

            return dock;
        }

        private static RemoteTunnelQueryRequest DeserializeRequest(RemoteTunnelEnvelope envelope)
        {
            if (!envelope.Payload.HasValue)
            {
                return new RemoteTunnelQueryRequest();
            }

            try
            {
                return envelope.Payload.Value.Deserialize<RemoteTunnelQueryRequest>(RemoteTunnelProtocol.JsonOptions)
                    ?? new RemoteTunnelQueryRequest();
            }
            catch (JsonException)
            {
                return new RemoteTunnelQueryRequest();
            }
        }

        private static int Clamp(int value, int defaultValue, int minimum, int maximum)
        {
            int effective = value <= 0 ? defaultValue : value;
            if (effective < minimum) effective = minimum;
            if (effective > maximum) effective = maximum;
            return effective;
        }

        private static RemoteTunnelRequestResult BadRequest(string errorCode, string message)
        {
            return new RemoteTunnelRequestResult
            {
                StatusCode = 400,
                ErrorCode = errorCode,
                Message = message
            };
        }

        private static RemoteTunnelRequestResult NotFound(string message)
        {
            return new RemoteTunnelRequestResult
            {
                StatusCode = 404,
                ErrorCode = "not_found",
                Message = message
            };
        }

        private static async Task<string[]> ReadLinesSharedAsync(string path)
        {
            using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new StreamReader(fs);
            List<string> lines = new List<string>();
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                lines.Add(line);
            }

            return lines.ToArray();
        }

        private static async Task<string> ReadFileSharedAsync(string path)
        {
            using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new StreamReader(fs);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        #endregion

        #region Private-Members

        private readonly DatabaseDriver _Database;
        private readonly ArmadaSettings _Settings;
        private readonly IGitService _Git;
        private readonly Func<CancellationToken, Task<ArmadaStatus>> _GetStatusAsync;
        private readonly Func<RemoteTunnelStatus> _GetRemoteTunnelStatus;
        private readonly DateTime _StartUtc;
        private readonly Func<DateTime> _UtcNow;

        #endregion
    }
}
