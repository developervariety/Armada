namespace Armada.Server.Routes
{
    using System.Diagnostics;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Text.Json;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;
    using Armada.Server;
    using Armada.Core;
    using ArmadaConstants = Armada.Core.Constants;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// REST API routes for status management.
    /// </summary>
    public class StatusRoutes
    {
        private readonly DatabaseDriver _database;
        private readonly ArmadaSettings _settings;
        private readonly IAdmiralService _admiral;
        private readonly Action _stopCallback;
        private readonly DateTime _startUtc;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly LoggingModule _logging;
        private readonly IBuildDriftService? _buildDrift;
        private readonly Func<RemoteTunnelStatus>? _getRemoteTunnelStatus;
        private readonly Func<Task>? _onRemoteControlSettingsChanged;
        private readonly Func<TypedCapacityEscalationAdapter?>? _getCapacityAdapter;

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="settings">Application settings.</param>
        /// <param name="admiral">Admiral coordination service.</param>
        /// <param name="stopCallback">Server shutdown callback.</param>
        /// <param name="startUtc">Server start timestamp.</param>
        /// <param name="jsonOptions">JSON serializer options.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="buildDrift">Optional build drift service.</param>
        /// <param name="getRemoteTunnelStatus">Optional callback that returns the current remote tunnel status.</param>
        /// <param name="onRemoteControlSettingsChanged">Optional callback invoked after remote-control settings are updated.</param>
        public StatusRoutes(
            DatabaseDriver database,
            ArmadaSettings settings,
            IAdmiralService admiral,
            Action stopCallback,
            DateTime startUtc,
            JsonSerializerOptions jsonOptions,
            LoggingModule logging,
            IBuildDriftService? buildDrift = null,
            Func<RemoteTunnelStatus>? getRemoteTunnelStatus = null,
            Func<Task>? onRemoteControlSettingsChanged = null,
            Func<TypedCapacityEscalationAdapter?>? getCapacityAdapter = null)
        {
            _database = database;
            _settings = settings;
            _admiral = admiral;
            _stopCallback = stopCallback;
            _startUtc = startUtc;
            _jsonOptions = jsonOptions;
            _logging = logging;
            _buildDrift = buildDrift;
            _getRemoteTunnelStatus = getRemoteTunnelStatus;
            _onRemoteControlSettingsChanged = onRemoteControlSettingsChanged;
            _getCapacityAdapter = getCapacityAdapter;
        }

        /// <summary>
        /// Register routes with the application.
        /// </summary>
        /// <param name="app">Webserver.</param>
        /// <param name="authenticate">Authentication middleware.</param>
        /// <param name="authz">Authorization service.</param>
        public void Register(
            Webserver app,
            Func<WatsonWebserver.Core.HttpContextBase, Task<AuthContext>> authenticate,
            IAuthorizationService authz)
        {
            string _Header = "[ArmadaServer] ";

            // Status
            app.Get("/api/v1/status", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                    return new ApiErrorResponse { Error = ctx.IsAuthenticated ? ApiResultEnum.BadRequest : ApiResultEnum.BadRequest, Message = ctx.IsAuthenticated ? "You do not have permission to perform this action" : "Authentication required" };
                }
                ArmadaStatus status = await _admiral.GetStatusAsync().ConfigureAwait(false);
                return status;
            },
            api => api
                .WithTag("Status")
                .WithSummary("Get Armada status")
                .WithDescription("Returns aggregate status including captain counts, mission breakdown, active voyages, and recent signals.")
                .WithResponse(200, OpenApiJson.For<ArmadaStatus>("Armada status dashboard"))
                .WithSecurity("ApiKey"));

            app.Get("/api/v1/status/health", async (ApiRequest req) =>
            {
                TimeSpan uptime = DateTime.UtcNow - _startUtc;
                BuildDriftReport? driftReport = null;
                if (_buildDrift != null)
                {
                    try
                    {
                        driftReport = await _buildDrift.GetReportAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        // health endpoint must not fail due to drift lookup
                    }
                }
                return new
                {
                    Status = "healthy",
                    Timestamp = DateTime.UtcNow,
                    StartUtc = _startUtc,
                    Uptime = uptime.ToString(@"d\.hh\:mm\:ss"),
                    Version = ArmadaConstants.ProductVersion,
                    Ports = new
                    {
                        Admiral = _settings.AdmiralPort,
                        Mcp = _settings.McpPort
                    },
                    RemoteTunnel = BuildRemoteTunnelStatus(),
                    RunningCommit = driftReport?.RunningCommit,
                    LandedCommit = driftReport?.LandedCommit,
                    BehindBy = driftReport?.BehindBy ?? 0,
                    DriftWarning = driftReport?.Warning
                };
            },
            api => api
                .WithTag("Status")
                .WithSummary("Health check")
                .WithDescription("Returns health status. Does not require authentication."));

            app.Get("/api/v1/doctor", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                    return new ApiErrorResponse { Error = ctx.IsAuthenticated ? ApiResultEnum.BadRequest : ApiResultEnum.BadRequest, Message = ctx.IsAuthenticated ? "You do not have permission to perform this action" : "Authentication required" };
                }
                List<object> results = new List<object>();

                // 1. Settings File
                try
                {
                    string settingsPath = ArmadaSettings.DefaultSettingsPath;
                    if (File.Exists(settingsPath))
                        results.Add(new { Name = "Settings", Status = "Pass", Message = "Settings loaded from " + settingsPath });
                    else
                        results.Add(new { Name = "Settings", Status = "Fail", Message = "Settings file not found at " + settingsPath });
                }
                catch (Exception ex)
                {
                    results.Add(new { Name = "Settings", Status = "Fail", Message = "Error checking settings: " + ex.Message });
                }

                // 2. Git Availability
                try
                {
                    ProcessStartInfo gitPsi = new ProcessStartInfo("git", "--version")
                    {
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (Process? gitProc = Process.Start(gitPsi))
                    {
                        if (gitProc != null)
                        {
                            string gitOutput = gitProc.StandardOutput.ReadToEnd().Trim();
                            gitProc.WaitForExit(5000);
                            results.Add(new { Name = "Git", Status = "Pass", Message = gitOutput });
                        }
                        else
                        {
                            results.Add(new { Name = "Git", Status = "Fail", Message = "Could not start git process" });
                        }
                    }
                }
                catch
                {
                    results.Add(new { Name = "Git", Status = "Fail", Message = "Git not found on PATH" });
                }

                // 3. Database
                try
                {
                    if (_settings.Database.Type == DatabaseTypeEnum.Sqlite)
                    {
                        string dbPath = _settings.DatabasePath;
                        if (File.Exists(dbPath))
                        {
                            FileInfo fi = new FileInfo(dbPath);
                            results.Add(new { Name = "Database", Status = "Pass", Message = $"SQLite database exists ({fi.Length / 1024} KB) at {dbPath}" });
                        }
                        else
                        {
                            results.Add(new { Name = "Database", Status = "Warn", Message = "SQLite database not found at " + dbPath });
                        }
                    }
                    else
                    {
                        DatabaseSettings db = _settings.Database;
                        results.Add(new { Name = "Database", Status = "Pass", Message = $"{db.Type} database configured at {db.Hostname}:{db.Port} / {db.DatabaseName}" });
                    }
                }
                catch (Exception ex)
                {
                    results.Add(new { Name = "Database", Status = "Fail", Message = "Error checking database: " + ex.Message });
                }

                // 4. Admiral Server (self-check — always passes if we reach here)
                results.Add(new { Name = "Admiral Server", Status = "Pass", Message = "Server is healthy" });

                // 5. Stalled Captains
                try
                {
                    List<Captain> stalledCaptains = ctx.IsAdmin
                        ? await _database.Captains.EnumerateByStateAsync(CaptainStateEnum.Stalled).ConfigureAwait(false)
                        : await _database.Captains.EnumerateByStateAsync(ctx.TenantId!, CaptainStateEnum.Stalled).ConfigureAwait(false);
                    int stalledCount = stalledCaptains.Count;
                    if (stalledCount == 0)
                        results.Add(new { Name = "Stalled Captains", Status = "Pass", Message = "No stalled captains" });
                    else
                        results.Add(new { Name = "Stalled Captains", Status = "Warn", Message = $"{stalledCount} captain(s) are stalled" });
                }
                catch (Exception ex)
                {
                    results.Add(new { Name = "Stalled Captains", Status = "Fail", Message = "Error checking captains: " + ex.Message });
                }

                // 6. Failed Missions -- use count-only query to avoid hydrating all failed rows
                try
                {
                    Dictionary<MissionStatusEnum, int> missionCounts = ctx.IsAdmin
                        ? await _database.Missions.CountByStatusAsync().ConfigureAwait(false)
                        : await _database.Missions.CountByStatusAsync(ctx.TenantId!).ConfigureAwait(false);
                    missionCounts.TryGetValue(MissionStatusEnum.Failed, out int failedCount);
                    if (failedCount == 0)
                        results.Add(new { Name = "Failed Missions", Status = "Pass", Message = "No failed missions" });
                    else
                        results.Add(new { Name = "Failed Missions", Status = "Warn", Message = $"{failedCount} mission(s) have failed" });
                }
                catch (Exception ex)
                {
                    results.Add(new { Name = "Failed Missions", Status = "Fail", Message = "Error checking missions: " + ex.Message });
                }

                // 7. Agent Runtimes
                string[] runtimeCommands = new string[] { "claude", "codex", "gemini", "cursor-agent", "mux" };
                string[] runtimeNames = new string[] { "Claude Code", "Codex", "Gemini CLI", "Cursor", "Mux" };
                bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                for (int i = 0; i < runtimeCommands.Length; i++)
                {
                    try
                    {
                        // Use 'where' on Windows, 'command -v' (POSIX standard) on Unix.
                        // 'which' is deprecated on Debian and missing on some distros.
                        ProcessStartInfo rtPsi;
                        if (isWindows)
                        {
                            rtPsi = new ProcessStartInfo("where");
                            rtPsi.ArgumentList.Add(runtimeCommands[i]);
                        }
                        else
                        {
                            rtPsi = new ProcessStartInfo("/bin/sh");
                            rtPsi.ArgumentList.Add("-c");
                            rtPsi.ArgumentList.Add("command -v " + runtimeCommands[i]);
                        }
                        rtPsi.RedirectStandardOutput = true;
                        rtPsi.RedirectStandardError = true;
                        rtPsi.UseShellExecute = false;
                        rtPsi.CreateNoWindow = true;
                        using (Process? rtProc = Process.Start(rtPsi))
                        {
                            if (rtProc != null)
                            {
                                string rtOutput = rtProc.StandardOutput.ReadToEnd().Trim();
                                rtProc.WaitForExit(5000);
                                if (rtProc.ExitCode == 0 && !string.IsNullOrEmpty(rtOutput))
                                {
                                    string path = rtOutput.Split('\n')[0].Trim();
                                    results.Add(new { Name = runtimeNames[i], Status = "Pass", Message = runtimeNames[i] + " found at " + path });
                                }
                                else
                                {
                                    results.Add(new { Name = runtimeNames[i], Status = "Warn", Message = runtimeNames[i] + " not found on PATH (optional)" });
                                }
                            }
                            else
                            {
                                results.Add(new { Name = runtimeNames[i], Status = "Warn", Message = runtimeNames[i] + " not found (optional)" });
                            }
                        }
                    }
                    catch
                    {
                        results.Add(new { Name = runtimeNames[i], Status = "Warn", Message = runtimeNames[i] + " not found (optional)" });
                    }
                }

                return results;
            },
            api => api
                .WithTag("Status")
                .WithSummary("Run system health diagnostics")
                .WithDescription("Runs 7 system health checks and returns results as a JSON array.")
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/server/stop", async (ApiRequest req) =>
            {
                if (_settings.RequireAuthForShutdown)
                {
                    AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                    if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                    {
                        req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                        return new ApiErrorResponse { Error = ctx.IsAuthenticated ? ApiResultEnum.BadRequest : ApiResultEnum.BadRequest, Message = ctx.IsAuthenticated ? "You do not have permission to perform this action" : "Authentication required" };
                    }
                }
                _logging.Info(_Header + "shutdown requested via API");
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500).ConfigureAwait(false);
                    _stopCallback();
                });
                return new { Status = "shutting_down" };
            },
            api => api
                .WithTag("Status")
                .WithSummary("Stop the Admiral server")
                .WithDescription("Initiates a graceful shutdown of the Admiral server.")
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/server/restart", async (ApiRequest req) =>
            {
                if (_settings.RequireAuthForShutdown)
                {
                    AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                    if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                    {
                        req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                        return new ApiErrorResponse { Error = ctx.IsAuthenticated ? ApiResultEnum.BadRequest : ApiResultEnum.BadRequest, Message = ctx.IsAuthenticated ? "You do not have permission to perform this action" : "Authentication required" };
                    }
                }
                _logging.Info(_Header + "restart requested via API");
                // Production runs under a container restart policy, so a graceful stop is a restart: the supervisor
                // relaunches the Admiral once the process exits. No child process is spawned here.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500).ConfigureAwait(false);
                    _stopCallback();
                });
                return new { Status = "restarting" };
            },
            api => api
                .WithTag("Status")
                .WithSummary("Restart the Admiral server")
                .WithDescription("Gracefully stops the Admiral server; the container or process supervisor restart policy relaunches it. Expect a brief period of downtime.")
                .WithSecurity("ApiKey"));

            app.Post<UsageRoutingPreviewRequest>("/api/v1/settings/usage-preview", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, "PUT", "/api/v1/settings"))
                {
                    req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "Administrator access required" };
                }
                UsageRoutingPreviewRequest body;
                try
                {
                    body = JsonSerializer.Deserialize<UsageRoutingPreviewRequest>(req.Http.Request.DataAsString, _jsonOptions) ?? throw new ArgumentException("Preview body is required.");
                    UsageRoutingService.Validate(body.UsageRouting ?? _settings.ModelTier.UsageRouting, AccountLoginPaths.AccountsRoot(_settings.DataDirectory));
                }
                catch (Exception ex) when (ex is ArgumentException || ex is JsonException)
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "Invalid usage policy or preview request." };
                }
                UsageRoutingSettings policy = body.UsageRouting ?? _settings.ModelTier.UsageRouting;
                // A draft has isolated collector and hysteresis state; preview cannot mutate live routing.
                UsageRoutingService usage = body.UsageRouting == null ? UsageRoutingService.For(_settings) : new UsageRoutingService();
                await usage.RefreshAsync(policy).ConfigureAwait(false);
                List<Captain> captains = await _database.Captains.EnumerateAsync().ConfigureAwait(false);
                try { CaptainAccountLaunch.ValidateCaptainBindings(policy, captains); }
                catch (ArgumentException ex)
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = ex.Message };
                }
                Mission mission = new Mission { Persona = body.Persona, Priority = body.Priority, PreferredModel = body.PreferredModel };
                if (!String.IsNullOrWhiteSpace(body.MissionTitle)) mission.Title = body.MissionTitle;
                if (!String.IsNullOrWhiteSpace(body.MissionText)) mission.Description = body.MissionText;
                List<Captain> idle = captains.Where(c => c.State == CaptainStateEnum.Idle && (!c.QuarantineUntilUtc.HasValue || c.QuarantineUntilUtc <= DateTime.UtcNow)).ToList();
                List<string> busy = captains.Where(c => c.State == CaptainStateEnum.Working).Select(c => c.Id).ToList();
                // Without mission text the capacity client is never called: the preview reports the Default list.
                bool askCapacity = !String.IsNullOrWhiteSpace(body.MissionText) || !String.IsNullOrWhiteSpace(body.MissionTitle);
                CapacityEscalationResolver capacity = new CapacityEscalationResolver(_getCapacityAdapter?.Invoke());
                CapacityWorkText workText = new CapacityWorkText { Title = body.MissionTitle ?? String.Empty, Description = body.MissionText ?? String.Empty };
                UsageRoutingDecision decision = await SmartRoutingSelector.SelectAsync(new SmartRoutingRequest
                {
                    Tiers = _settings.ModelTier,
                    Policy = policy,
                    Usage = usage,
                    Mission = mission,
                    Pool = idle,
                    BusyCaptainIds = busy,
                    NowUtc = DateTime.UtcNow,
                    Capacity = capacity,
                    WorkText = askCapacity ? _ => Task.FromResult(workText) : null,
                    UseCapacityCache = false
                }).ConfigureAwait(false);
                Captain? chosen = decision.Candidates.Count > 0 ? decision.Candidates[0] : null;
                return new
                {
                    decision.Reason,
                    SmartRoutingEnabled = policy.Enabled,
                    decision.HasPersonaRoutes,
                    decision.HasPersonaModels,
                    LegacyOrder = decision.LegacyOrder.Select(c => new { c.Id, c.Name, c.Model, c.Runtime }).ToList(),
                    UsageFilter = decision.Verdicts,
                    ModelGroups = decision.Groups,
                    Capacity = new { Choice = decision.Capacity, Source = decision.CapacitySource, Asked = askCapacity },
                    Candidates = decision.Candidates.Select(c => new { c.Id, c.Name, c.Model, c.Runtime }).ToList(),
                    Chosen = chosen == null ? null : new { chosen.Id, chosen.Name, chosen.Model, chosen.Runtime },
                    Accounts = policy.Accounts.Select(a => usage.GetStatus(a, null, DateTime.UtcNow)).ToList(),
                    Warnings = policy.Accounts.SelectMany(a => a.CaptainIds.Where(id => !captains.Any(c => c.Id == id)).Select(id => "Unknown captain: " + id)).ToList(),
                    Scope = "Idle captains only. Live assignment reservations, retry exclusions, requested captains, and vessel gates apply at dispatch."
                };
            }, api => api.WithTag("Settings").WithSummary("Preview Smart Routing for one persona").WithSecurity("ApiKey"));

            // Settings
            app.Get("/api/v1/settings", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                    return new ApiErrorResponse { Error = ctx.IsAuthenticated ? ApiResultEnum.BadRequest : ApiResultEnum.BadRequest, Message = ctx.IsAuthenticated ? "You do not have permission to perform this action" : "Authentication required" };
                }
                await UsageRoutingService.For(_settings).RefreshAsync(_settings.ModelTier.UsageRouting).ConfigureAwait(false);
                return BuildSettingsResponse();
            },
            api => api
                .WithTag("Settings")
                .WithSummary("Get server settings")
                .WithDescription("Returns current server settings including ports, agent configuration, and system paths.")
                .WithSecurity("ApiKey"));

            app.Put<SettingsUpdateRequest>("/api/v1/settings", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                    return new ApiErrorResponse { Error = ctx.IsAuthenticated ? ApiResultEnum.BadRequest : ApiResultEnum.BadRequest, Message = ctx.IsAuthenticated ? "You do not have permission to perform this action" : "Authentication required" };
                }
                SettingsUpdateRequest body = JsonSerializer.Deserialize<SettingsUpdateRequest>(req.Http.Request.DataAsString, _jsonOptions)
                    ?? throw new InvalidOperationException("Request body could not be deserialized as SettingsUpdateRequest.");

                if (body.ModelTier?.UsageRouting != null)
                {
                    List<Captain> boundCaptains = await _database.Captains.EnumerateAsync().ConfigureAwait(false);
                    try
                    {
                        UsageRoutingService.Validate(body.ModelTier.UsageRouting, AccountLoginPaths.AccountsRoot(_settings.DataDirectory));
                        CaptainAccountLaunch.ValidateCaptainBindings(body.ModelTier.UsageRouting, boundCaptains);
                    }
                    catch (ArgumentException ex)
                    {
                        req.Http.Response.StatusCode = 400;
                        return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = ex.Message };
                    }
                }

                if (body.AdmiralPort.HasValue)
                    _settings.AdmiralPort = body.AdmiralPort.Value;

                if (body.McpPort.HasValue)
                    _settings.McpPort = body.McpPort.Value;

                if (body.MaxCaptains.HasValue)
                    _settings.MaxCaptains = body.MaxCaptains.Value;

                if (body.MaxConcurrentCaptainWorkloads.HasValue)
                    _settings.MaxConcurrentCaptainWorkloads = body.MaxConcurrentCaptainWorkloads.Value;

                if (body.HeartbeatIntervalSeconds.HasValue)
                    _settings.HeartbeatIntervalSeconds = body.HeartbeatIntervalSeconds.Value;

                if (body.StallThresholdMinutes.HasValue)
                    _settings.StallThresholdMinutes = body.StallThresholdMinutes.Value;

                if (body.IdleCaptainTimeoutSeconds.HasValue)
                    _settings.IdleCaptainTimeoutSeconds = body.IdleCaptainTimeoutSeconds.Value;

                if (body.PlanningSessionInactivityTimeoutMinutes.HasValue)
                    _settings.PlanningSessionInactivityTimeoutMinutes = body.PlanningSessionInactivityTimeoutMinutes.Value;

                if (body.PlanningSessionAbandonmentTimeoutMinutes.HasValue)
                    _settings.PlanningSessionAbandonmentTimeoutMinutes = body.PlanningSessionAbandonmentTimeoutMinutes.Value;

                if (body.PlanningSessionRetentionDays.HasValue)
                    _settings.PlanningSessionRetentionDays = body.PlanningSessionRetentionDays.Value;

                if (body.AutoCreatePr.HasValue)
                    _settings.AutoCreatePullRequests = body.AutoCreatePr.Value;

                bool remoteControlChanged = body.RemoteControl != null;
                if (remoteControlChanged)
                    _settings.RemoteControl = body.RemoteControl!;

                // Applied in place. The resource-pressure gate is constructed with a
                // reference to the nested settings object, so replacing the reference
                // here would leave the running gate on the old limits until a restart.
                if (body.ResourcePressureAdmission != null)
                {
                    body.ResourcePressureAdmission.ApplyTo(_settings.ResourcePressureAdmission);
                    _logging.Info(_Header + "resource-pressure admission updated via API: enabled="
                        + _settings.ResourcePressureAdmission.Enabled
                        + " maxConcurrentBuilds=" + _settings.ResourcePressureAdmission.MaxConcurrentBuilds
                        + " minAvailableMemoryMb=" + _settings.ResourcePressureAdmission.MinAvailableMemoryMb
                        + " oomCooldownSeconds=" + _settings.ResourcePressureAdmission.OomCooldownSeconds);
                }

                if (body.ModelTier != null)
                {
                    body.ModelTier.ApplyTo(_settings.ModelTier);
                    _logging.Info(_Header + "model-tier routing updated via API: reservedHighTierSlots="
                        + _settings.ModelTier.ReservedHighTierSlots
                        + " specialistPersonas=" + _settings.ModelTier.SpecialistPersonas.Count
                        + " preferNonNativeFirst=" + _settings.ModelTier.PreferNonNativeFirst
                        + " withinTierStrategy=" + _settings.ModelTier.WithinTierStrategy);
                }

                if (body.VoyageDispatch != null)
                {
                    body.VoyageDispatch.ApplyTo(_settings.VoyageDispatch);
                    _logging.Info(_Header + "voyage-dispatch settings updated via API: rejectStagePersonaTitlePrefixes="
                        + _settings.VoyageDispatch.RejectStagePersonaTitlePrefixes
                        + " prefixCount=" + _settings.VoyageDispatch.StagePersonaTitlePrefixes.Count);
                }

                if (body.AdditionalPromptTemplates != null)
                    _settings.AdditionalPromptTemplates = body.AdditionalPromptTemplates;

                if (body.AdditionalPersonas != null)
                    _settings.AdditionalPersonas = body.AdditionalPersonas;

                if (body.AdditionalPipelines != null)
                    _settings.AdditionalPipelines = body.AdditionalPipelines;

                if (body.ModelProviders != null)
                    _settings.ModelProviders = body.ModelProviders;

                await _settings.SaveAsync().ConfigureAwait(false);

                if (remoteControlChanged && _onRemoteControlSettingsChanged != null)
                    await _onRemoteControlSettingsChanged().ConfigureAwait(false);

                _logging.Info(_Header + "settings updated via API");

                return BuildSettingsResponse();
            },
            api => api
                .WithTag("Settings")
                .WithSummary("Update server settings")
                .WithDescription("Accepts partial update of editable settings. Validates values and persists to settings file.")
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/settings/reload", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = ctx.IsAuthenticated ? "You do not have permission to perform this action" : "Authentication required" };
                }

                ArmadaSettings loaded = await ArmadaSettings.LoadAsync().ConfigureAwait(false);
                _settings.ApplyHotReloadableFrom(loaded);

                _logging.Info(_Header + "settings reloaded from file via API: maxConcurrentCaptainWorkloads="
                    + _settings.MaxConcurrentCaptainWorkloads
                    + " maxConcurrentBuilds=" + _settings.ResourcePressureAdmission.MaxConcurrentBuilds);

                return BuildSettingsResponse();
            },
            api => api
                .WithTag("Settings")
                .WithSummary("Reload settings from file")
                .WithDescription("Re-reads settings.json and applies the runtime-tunable values in place, without a restart. Ports, paths, database, API key, agent definitions and remote-control settings are not reloaded and still require a restart.")
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/server/reset", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!authz.IsAuthorized(ctx, req.Http.Request.Method.ToString(), req.Http.Request.Url.RawWithoutQuery))
                {
                    req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
                    return new ApiErrorResponse { Error = ctx.IsAuthenticated ? ApiResultEnum.BadRequest : ApiResultEnum.BadRequest, Message = ctx.IsAuthenticated ? "You do not have permission to perform this action" : "Authentication required" };
                }
                _logging.Warn(_Header + "factory reset requested via API");

                List<string> deleted = new List<string>();

                if (Directory.Exists(_settings.LogDirectory))
                {
                    Directory.Delete(_settings.LogDirectory, true);
                    deleted.Add("logs");
                }

                if (Directory.Exists(_settings.DocksDirectory))
                {
                    Directory.Delete(_settings.DocksDirectory, true);
                    deleted.Add("docks");
                }

                if (Directory.Exists(_settings.ReposDirectory))
                {
                    Directory.Delete(_settings.ReposDirectory, true);
                    deleted.Add("repos");
                }

                if (File.Exists(_settings.DatabasePath))
                {
                    File.Delete(_settings.DatabasePath);
                    deleted.Add("database");
                }

                _settings.InitializeDirectories();

                return new
                {
                    Status = "reset_complete",
                    Message = "Factory reset complete. Deleted: " + String.Join(", ", deleted) + ". Settings file preserved.",
                    Deleted = deleted
                };
            },
            api => api
                .WithTag("Settings")
                .WithSummary("Factory reset")
                .WithDescription("Deletes database, logs, docks, and repos directories. Preserves settings file.")
                .WithSecurity("ApiKey"));
        }

        private object BuildSettingsResponse()
        {
            return new
            {
                AdmiralPort = _settings.AdmiralPort,
                McpPort = _settings.McpPort,
                MaxCaptains = _settings.MaxCaptains,
                MaxConcurrentCaptainWorkloads = _settings.MaxConcurrentCaptainWorkloads,
                HeartbeatIntervalSeconds = _settings.HeartbeatIntervalSeconds,
                StallThresholdMinutes = _settings.StallThresholdMinutes,
                IdleCaptainTimeoutSeconds = _settings.IdleCaptainTimeoutSeconds,
                PlanningSessionInactivityTimeoutMinutes = _settings.PlanningSessionInactivityTimeoutMinutes,
                PlanningSessionAbandonmentTimeoutMinutes = _settings.PlanningSessionAbandonmentTimeoutMinutes,
                PlanningSessionRetentionDays = _settings.PlanningSessionRetentionDays,
                AutoCreatePr = _settings.AutoCreatePullRequests,
                DataDirectory = _settings.DataDirectory,
                DatabasePath = _settings.DatabasePath,
                LogDirectory = _settings.LogDirectory,
                DocksDirectory = _settings.DocksDirectory,
                ReposDirectory = _settings.ReposDirectory,
                RemoteControl = _settings.RemoteControl,
                ResourcePressureAdmission = _settings.ResourcePressureAdmission,
                ModelTier = _settings.ModelTier,
                ProviderUsage = _settings.ModelTier.UsageRouting.Accounts.Select(account => UsageRoutingService.For(_settings).GetStatus(account, null, DateTime.UtcNow)).ToList(),
                VoyageDispatch = _settings.VoyageDispatch,
                AdditionalPromptTemplates = _settings.AdditionalPromptTemplates,
                AdditionalPersonas = _settings.AdditionalPersonas,
                AdditionalPipelines = _settings.AdditionalPipelines,
                ModelProviders = _settings.ModelProviders,
                ModelProvidersHotReload = false,
                AdditionalAssetsHotReload = false
            };
        }

        private RemoteTunnelStatus BuildRemoteTunnelStatus()
        {
            return _getRemoteTunnelStatus?.Invoke() ?? new RemoteTunnelStatus
            {
                Enabled = _settings.RemoteControl.Enabled,
                State = _settings.RemoteControl.Enabled ? RemoteTunnelStateEnum.Disconnected : RemoteTunnelStateEnum.Disabled,
                TunnelUrl = _settings.RemoteControl.TunnelUrl
            };
        }
    }
}
