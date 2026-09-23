namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;
    using Armada.Core;
    using ArmadaConstants = Armada.Core.Constants;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server;
    using SyslogLogging;

    /// <summary>
    /// Registers MCP tools for captain operations (get, create, update, stop, delete, log).
    /// </summary>
    public static class McpCaptainTools
    {
        // MCP operator tools carry no per-caller identity, so quarantine requests run in the unscoped operator scope.
        private static readonly AuthContext _OperatorScope = AuthContext.Authenticated(
            ArmadaConstants.DefaultTenantId, ArmadaConstants.DefaultUserId, true, true, "Mcp");

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>
        /// Registers captain MCP tools with the server.
        /// </summary>
        /// <param name="register">Delegate to register each tool.</param>
        /// <param name="database">Database driver for captain data access.</param>
        /// <param name="admiral">Admiral service for captain orchestration.</param>
        /// <param name="settings">Armada settings, or null if unavailable.</param>
        /// <param name="onStopCaptain">Optional callback invoked when a captain is stopped.</param>
        /// <param name="agentLifecycle">Optional lifecycle handler used for model validation.</param>
        /// <param name="logging">Optional logging module for structured warning output.</param>
        /// <param name="captainQuarantine">Optional quarantine service; when supplied the bench and unbench tools are registered.</param>
        /// <param name="captainAdministration">Shared stop, stop-all and deletion service. When null, one is built that recalls through <paramref name="admiral"/>, stops processes through <paramref name="onStopCaptain"/>, and has no session coordinators, so it refuses to stop a Planning or Refining captain and reports active planning and refinement sessions as failed stops.</param>
        public static void Register(RegisterToolDelegate register, DatabaseDriver database, IAdmiralService admiral, ArmadaSettings? settings, Func<string, Task>? onStopCaptain = null, AgentLifecycleHandler? agentLifecycle = null, LoggingModule? logging = null, ICaptainQuarantineService? captainQuarantine = null, CaptainAdministrationService? captainAdministration = null)
        {
            CaptainAdministrationService administration = captainAdministration
                ?? new CaptainAdministrationService(database, (captainId, token) => admiral.RecallCaptainAsync(captainId, token), logging);
            if (captainAdministration == null && onStopCaptain != null)
                administration.StopProcess = captain => onStopCaptain(captain.Id);

            register(
                "armada_get_captain",
                "Get details of a specific captain (AI agent)",
                new
                {
                    type = "object",
                    properties = new
                    {
                        captainId = new { type = "string", description = "Captain ID (cpt_ prefix)" }
                    },
                    required = new[] { "captainId" }
                },
                async (args) =>
                {
                    CaptainIdArgs request = JsonSerializer.Deserialize<CaptainIdArgs>(args!.Value, _JsonOptions)!;
                    string captainId = request.CaptainId;
                    Captain? captain = await database.Captains.ReadAsync(captainId).ConfigureAwait(false);
                    if (captain == null) return (object)new { Error = "Captain not found" };
                    return (object)MaskCaptain(captain);
                });

            register(
                "armada_create_captain",
                "Register a new captain (AI agent)",
                new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string", description = "Captain display name" },
                        runtime = new { type = "string", description = "Agent runtime: ClaudeCode, Codex, Gemini, Cursor, OpenCode, Mux, or Custom" },
                        systemInstructions = new { type = "string", description = "System instructions for this captain -- injected into every mission prompt to specialize behavior" },
                        model = new { type = "string", description = "AI model identifier; null means runtime default" },
                        apiKey = new { type = "string", description = "Per-captain provider credential override for external-provider-served models (e.g. example-provider); wins over the provider's host-level environment variable" },
                        apiBaseUrl = new { type = "string", description = "Per-captain provider base URL override for external-provider-served models" },
                        allowedPersonas = new { type = "string", description = "JSON array of persona names this captain can fill, e.g. [\"Worker\",\"Judge\"]. Null means any persona." },
                        preferredPersona = new { type = "string", description = "Preferred persona for dispatch routing priority" },
                        tier = new { type = "string", description = "Capability tier: Economy, Standard, or Premium. Omit or send an empty string to classify the tier from the model name." },
                        preferenceRank = new { type = "integer", description = "Preference rank within the tier (-1000 to 1000); a higher rank is tried first. Default 0." },
                        muxConfigDirectory = new { type = "string", description = "Optional Mux config directory override" },
                        muxEndpoint = new { type = "string", description = "Named Mux endpoint for this captain" },
                        muxBaseUrl = new { type = "string", description = "Optional Mux base URL override" },
                        muxAdapterType = new { type = "string", description = "Optional Mux adapter type override" },
                        muxTemperature = new { type = "number", description = "Optional Mux temperature override" },
                        muxMaxTokens = new { type = "integer", description = "Optional Mux max tokens override" },
                        muxSystemPromptPath = new { type = "string", description = "Optional Mux system prompt file path" },
                        muxApprovalPolicy = new { type = "string", description = "Optional Mux approval policy override" },
                        reasoningEffort = new { type = "string", description = "Reasoning-effort / thinking-budget tier (low|medium|high). Null means runtime CLI default." },
                        defaultPlaybooks = DefaultPlaybooksSchema()
                    },
                    required = new[] { "name" }
                },
                async (args) =>
                {
                    CaptainCreateArgs request = JsonSerializer.Deserialize<CaptainCreateArgs>(args!.Value, _JsonOptions)!;
                    string? ownedFieldError = CaptainInputMapping.FindServerOwnedFieldViolation(
                        JsonSerializer.Deserialize<CaptainServerOwnedFields>(args.Value, _JsonOptions), null);
                    if (ownedFieldError != null) return CreateToolErrorResponse(ownedFieldError);
                    string? nameConflict = await CaptainNameRule.FindCreateConflictAsync(database.Captains, request.Name).ConfigureAwait(false);
                    if (nameConflict != null) return CreateToolErrorResponse(nameConflict);
                    Captain captain = new Captain();
                    captain.Name = request.Name;
                    if (!String.IsNullOrEmpty(request.Runtime) && Enum.TryParse<AgentRuntimeEnum>(request.Runtime, true, out AgentRuntimeEnum rt))
                        captain.Runtime = rt;
                    captain.SystemInstructions = request.SystemInstructions;
                    captain.Model = String.IsNullOrWhiteSpace(request.Model) ? null : request.Model;
                    captain.ApiKey = String.IsNullOrWhiteSpace(request.ApiKey) ? null : request.ApiKey;
                    captain.ApiBaseUrl = String.IsNullOrWhiteSpace(request.ApiBaseUrl) ? null : request.ApiBaseUrl;
                    captain.AllowedPersonas = request.AllowedPersonas;
                    captain.PreferredPersona = request.PreferredPersona;
                    string? createTierError = ApplyTierArguments(captain, request.Tier, request.PreferenceRank);
                    if (createTierError != null) return CreateToolErrorResponse(createTierError);
                    if (request.DefaultPlaybooks != null)
                        captain.DefaultPlaybooks = SerializeDefaultPlaybooks(request.DefaultPlaybooks);
                    string? reasoningValidationError = CaptainRuntimeOptions.ValidateReasoningEffort(captain.Runtime, request.ReasoningEffort);
                    if (reasoningValidationError != null) return CreateToolErrorResponse(reasoningValidationError);
                    ApplyCaptainOptions(captain, request);
                    captain = CaptainInputMapping.ForCreate(captain);
                    // The captain is owned by the authenticated caller, exactly as a REST create is.
                    AuthContext createCaller = McpCallerContext.Require();
                    captain.TenantId = Armada.Core.Authorization.OwnershipPolicy.TenantOf(createCaller);
                    captain.UserId = Armada.Core.Authorization.OwnershipPolicy.UserOf(createCaller);

                    if (agentLifecycle != null)
                    {
                        string? validationError = await agentLifecycle.ValidateCaptainModelAsync(captain).ConfigureAwait(false);
                        if (validationError != null)
                        {
                            bool isSoftFailure = ProviderQuotaLimitDetector.IsCreditAuthBenchSignal(validationError) ||
                                ProviderQuotaLimitDetector.IsQuotaLimitSignal(validationError);
                            if (!isSoftFailure)
                                return CreateToolErrorResponse(validationError);

                            logging?.Warn("[McpCaptainTools] model validation cannot be verified for new captain; creation persisted. Error: " + validationError);
                        }
                    }

                    captain = await database.Captains.CreateAsync(captain).ConfigureAwait(false);
                    return (object)MaskCaptain(captain);
                });

            register(
                "armada_update_captain",
                "Update a captain's name or runtime. Operational fields (state, process, mission) are preserved.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        captainId = new { type = "string", description = "Captain ID (cpt_ prefix)" },
                        name = new { type = "string", description = "New display name" },
                        runtime = new { type = "string", description = "New agent runtime: ClaudeCode, Codex, Gemini, Cursor, OpenCode, Mux, or Custom" },
                        systemInstructions = new { type = "string", description = "New system instructions for this captain" },
                        model = new { type = "string", description = "New AI model identifier; null means runtime default" },
                        apiKey = new { type = "string", emptyStringClears = true, description = "New per-captain provider credential override for external-provider-served models; empty string clears it" },
                        apiBaseUrl = new { type = "string", emptyStringClears = true, description = "New per-captain provider base URL override for external-provider-served models; empty string clears it" },
                        allowedPersonas = new { type = "string", description = "JSON array of persona names this captain can fill, e.g. [\"Worker\",\"Judge\"]. Null means any persona." },
                        preferredPersona = new { type = "string", description = "Preferred persona for dispatch routing priority" },
                        tier = new { type = "string", emptyStringClears = true, description = "Capability tier: Economy, Standard, or Premium. Empty string classifies the tier from the model name; null leaves it unchanged." },
                        preferenceRank = new { type = "integer", description = "Preference rank within the tier (-1000 to 1000); a higher rank is tried first. Null leaves it unchanged." },
                        muxConfigDirectory = new { type = "string", emptyStringClears = true, description = "Optional Mux config directory override; empty string clears it" },
                        muxEndpoint = new { type = "string", emptyStringClears = true, description = "Named Mux endpoint; empty string clears it" },
                        muxBaseUrl = new { type = "string", emptyStringClears = true, description = "Optional Mux base URL override; empty string clears it" },
                        muxAdapterType = new { type = "string", emptyStringClears = true, description = "Optional Mux adapter type override; empty string clears it" },
                        muxTemperature = new { type = "number", description = "Optional Mux temperature override" },
                        muxMaxTokens = new { type = "integer", description = "Optional Mux max tokens override" },
                        muxSystemPromptPath = new { type = "string", emptyStringClears = true, description = "Optional Mux system prompt file path; empty string clears it" },
                        muxApprovalPolicy = new { type = "string", emptyStringClears = true, description = "Optional Mux approval policy override; empty string clears it" },
                        reasoningEffort = new { type = "string", emptyStringClears = true, description = "Reasoning-effort / thinking-budget tier (low|medium|high). Empty string clears it; null leaves it unchanged." },
                        defaultPlaybooks = DefaultPlaybooksSchema()
                    },
                    required = new[] { "captainId" }
                },
                async (args) =>
                {
                    CaptainUpdateArgs request = JsonSerializer.Deserialize<CaptainUpdateArgs>(args!.Value, _JsonOptions)!;
                    string captainId = request.CaptainId;
                    Captain? stored = await database.Captains.ReadAsync(captainId).ConfigureAwait(false);
                    if (stored == null) return (object)new { Error = "Captain not found" };
                    string? ownedFieldError = CaptainInputMapping.FindServerOwnedFieldViolation(
                        JsonSerializer.Deserialize<CaptainServerOwnedFields>(args.Value, _JsonOptions), stored);
                    if (ownedFieldError != null) return CreateToolErrorResponse(ownedFieldError);
                    Captain captain = CaptainInputMapping.ConfigurationOf(stored);
                    Captain existingCaptain = CloneForOptionsBaseline(stored);
                    if (request.Name != null)
                        captain.Name = request.Name;
                    if (!String.IsNullOrEmpty(request.Runtime) && Enum.TryParse<AgentRuntimeEnum>(request.Runtime, true, out AgentRuntimeEnum rt))
                        captain.Runtime = rt;
                    if (request.SystemInstructions != null)
                        captain.SystemInstructions = request.SystemInstructions;
                    if (request.Model != null)
                        captain.Model = String.IsNullOrWhiteSpace(request.Model) ? null : request.Model;
                    if (request.ApiKey != null)
                        captain.ApiKey = String.IsNullOrWhiteSpace(request.ApiKey) ? null : request.ApiKey;
                    if (request.ApiBaseUrl != null)
                        captain.ApiBaseUrl = String.IsNullOrWhiteSpace(request.ApiBaseUrl) ? null : request.ApiBaseUrl;
                    if (request.AllowedPersonas != null)
                        captain.AllowedPersonas = request.AllowedPersonas;
                    if (request.PreferredPersona != null)
                        captain.PreferredPersona = request.PreferredPersona;
                    string? updateTierError = ApplyTierArguments(captain, request.Tier, request.PreferenceRank);
                    if (updateTierError != null) return CreateToolErrorResponse(updateTierError);
                    if (request.DefaultPlaybooks != null)
                        captain.DefaultPlaybooks = SerializeDefaultPlaybooks(request.DefaultPlaybooks);
                    if (request.ReasoningEffort != null)
                    {
                        string? validationError = CaptainRuntimeOptions.ValidateReasoningEffort(captain.Runtime, request.ReasoningEffort);
                        if (validationError != null) return CreateToolErrorResponse(validationError);
                    }
                    try
                    {
                        ApplyCaptainOptions(captain, request, existingCaptain);
                    }
                    catch (Exception ex)
                    {
                        return CreateToolErrorResponse(ex.Message);
                    }

                    if (agentLifecycle != null)
                    {
                        bool modelOrRuntimeChanged =
                            !String.Equals(captain.Model, existingCaptain.Model, StringComparison.OrdinalIgnoreCase) ||
                            captain.Runtime != existingCaptain.Runtime ||
                            !String.Equals(captain.ApiKey, existingCaptain.ApiKey, StringComparison.Ordinal) ||
                            !String.Equals(captain.ApiBaseUrl, existingCaptain.ApiBaseUrl, StringComparison.Ordinal);
                        if (modelOrRuntimeChanged)
                        {
                            string? validationError = await agentLifecycle.ValidateCaptainModelAsync(captain).ConfigureAwait(false);
                            if (validationError != null)
                            {
                                bool isSoftFailure = ProviderQuotaLimitDetector.IsCreditAuthBenchSignal(validationError) ||
                                    ProviderQuotaLimitDetector.IsQuotaLimitSignal(validationError);
                                if (isSoftFailure)
                                {
                                    logging?.Warn("[McpCaptainTools] model validation cannot be verified for captain " + captainId + "; edit persisted. Error: " + validationError);
                                    captain = await database.Captains.UpdateAsync(CaptainInputMapping.ForUpdate(stored, captain)).ConfigureAwait(false);
                                    return (object)new
                                    {
                                        Captain = MaskCaptain(captain),
                                        CannotVerifyNow = true,
                                        ValidationWarning = "Model validation cannot be verified: provider credit or authentication failure. Edit persisted; captain may be benched at dispatch."
                                    };
                                }

                                return CreateToolErrorResponse(validationError);
                            }
                        }
                    }

                    captain = await database.Captains.UpdateAsync(CaptainInputMapping.ForUpdate(stored, captain)).ConfigureAwait(false);
                    return (object)MaskCaptain(captain);
                });

            register(
                "armada_stop_captain",
                "Stop a specific captain. A Planning or Refining captain is stopped through its active planning or objective refinement session; any other captain has its process stopped and is recalled to Idle, failing its active mission.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        captainId = new { type = "string", description = "Captain ID (cpt_ prefix)" }
                    },
                    required = new[] { "captainId" }
                },
                async (args) =>
                {
                    CaptainIdArgs request = JsonSerializer.Deserialize<CaptainIdArgs>(args!.Value, _JsonOptions)!;
                    CaptainStopResult stopped = await administration.StopAsync(request.CaptainId, null).ConfigureAwait(false);
                    if (stopped.Outcome != CaptainAdministrationOutcomeEnum.Completed)
                        return (object)new { Error = stopped.Message, stopped.Outcome, stopped.CaptainId };
                    return (object)stopped;
                });

            if (captainQuarantine != null)
            {
                register(
                    "armada_bench_captain",
                    "Bench (quarantine) a captain so the dispatcher stops assigning it work. Records an operator reason and an expiry, honored live without a restart. Use for a provider that is out of balance or otherwise unusable.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            captainId = new { type = "string", description = "Captain ID (cpt_ prefix)" },
                            reason = new { type = "string", description = "Operator-visible reason the captain is being benched" },
                            untilUtc = new { type = "string", description = "Optional ISO-8601 UTC instant the bench expires; takes precedence over durationMinutes" },
                            durationMinutes = new { type = "integer", description = "Optional bench duration in minutes from now; used when untilUtc is omitted" }
                        },
                        required = new[] { "captainId", "reason" }
                    },
                    async (args) =>
                    {
                        CaptainBenchArgs request = JsonSerializer.Deserialize<CaptainBenchArgs>(args!.Value, _JsonOptions)!;
                        if (String.IsNullOrWhiteSpace(request.CaptainId))
                            return CreateToolErrorResponse("captainId is required.");
                        if (String.IsNullOrWhiteSpace(request.Reason))
                            return CreateToolErrorResponse("reason is required so the bench is auditable.");

                        // The service writes only while the captain is Idle (or already benched) and owns no
                        // mission, dock or process, so a captain claimed after this request keeps its work.
                        if (request.DurationMinutes.HasValue && request.DurationMinutes.Value <= 0)
                            return CreateToolErrorResponse("durationMinutes must be positive; omit it for a hold that lasts until released.");

                        DateTime? untilUtc = ResolveBenchExpiry(request);
                        CaptainQuarantineResult result = await captainQuarantine.QuarantineCaptainAsync(
                            _OperatorScope, request.CaptainId, request.Reason, untilUtc).ConfigureAwait(false);
                        if (result.Outcome == CaptainQuarantineOutcomeEnum.Busy)
                            return CreateToolErrorResponse(result.Message + " Use armada_stop_captain first.");
                        if (result.Outcome != CaptainQuarantineOutcomeEnum.Quarantined || result.Captain == null)
                            return CreateToolErrorResponse(result.Message);

                        Captain benched = result.Captain;
                        return (object)new
                        {
                            Status = "benched",
                            Outcome = result.Outcome.ToString(),
                            CaptainId = benched.Id,
                            benched.Name,
                            State = benched.State.ToString(),
                            BenchReason = benched.QuarantineReason,
                            BenchUntilUtc = benched.QuarantineUntilUtc
                        };
                    });

                register(
                    "armada_unbench_captain",
                    "Restore a benched (quarantined) captain to idle so the dispatcher can assign it work again.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            captainId = new { type = "string", description = "Captain ID (cpt_ prefix)" }
                        },
                        required = new[] { "captainId" }
                    },
                    async (args) =>
                    {
                        CaptainIdArgs request = JsonSerializer.Deserialize<CaptainIdArgs>(args!.Value, _JsonOptions)!;
                        if (String.IsNullOrWhiteSpace(request.CaptainId))
                            return CreateToolErrorResponse("captainId is required.");

                        CaptainQuarantineResult result = await captainQuarantine.ReleaseCaptainAsync(
                            _OperatorScope, request.CaptainId).ConfigureAwait(false);
                        if (result.Captain == null
                            || (result.Outcome != CaptainQuarantineOutcomeEnum.Released && result.Outcome != CaptainQuarantineOutcomeEnum.NotQuarantined))
                            return CreateToolErrorResponse(result.Message);

                        return (object)new
                        {
                            Status = result.Outcome == CaptainQuarantineOutcomeEnum.Released ? "restored" : "not_quarantined",
                            Outcome = result.Outcome.ToString(),
                            CaptainId = result.Captain.Id,
                            result.Captain.Name,
                            State = result.Captain.State.ToString()
                        };
                    });
            }

            register(
                "armada_stop_all",
                "Emergency stop of every working captain, active planning session and active objective refinement session. Returns status all_stopped, or stopped_with_failures with stopped and failed counts and each failure named.",
                new { type = "object", properties = new { } },
                async (args) =>
                {
                    CaptainStopAllResult stopAll = await administration.StopAllAsync().ConfigureAwait(false);
                    return (object)stopAll;
                });

            register(
                "armada_delete_captain",
                "Delete a captain and the events, planning sessions and objective refinement sessions that reference it. Refused while the captain is Working, Planning or Refining or owns an Assigned or InProgress mission; stop it first.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        captainId = new { type = "string", description = "Captain ID (cpt_ prefix)" }
                    },
                    required = new[] { "captainId" }
                },
                async (args) =>
                {
                    CaptainIdArgs request = JsonSerializer.Deserialize<CaptainIdArgs>(args!.Value, _JsonOptions)!;
                    CaptainDeletionResult deletion = await administration.DeleteAsync(request.CaptainId, null).ConfigureAwait(false);
                    if (deletion.Outcome != CaptainAdministrationOutcomeEnum.Completed)
                        return (object)new { Error = deletion.Message };
                    return (object)new { Status = "deleted", CaptainId = deletion.CaptainId, deletion.DependentsRemoved, deletion.DependentsSkipped };
                });

            register(
                "armada_delete_captains",
                "Permanently delete multiple captains by ID with the same rule and dependent cleanup as armada_delete_captain. Captains that are Working, Planning or Refining or own an Assigned or InProgress mission are skipped. Returns a summary of deleted and skipped entries. This cannot be undone.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        ids = new { type = "array", items = new { type = "string" }, description = "List of captain IDs to delete (cpt_ prefix)" }
                    },
                    required = new[] { "ids" }
                },
                async (args) =>
                {
                    DeleteMultipleArgs request = JsonSerializer.Deserialize<DeleteMultipleArgs>(args!.Value, _JsonOptions)!;
                    if (request.Ids == null || request.Ids.Count == 0)
                        return (object)new { Error = "ids is required and must not be empty" };

                    DeleteMultipleResult result = await administration.DeleteManyAsync(request.Ids, null).ConfigureAwait(false);
                    return (object)result;
                });

            // Captain log requires settings
            if (settings != null)
            {
                register(
                    "armada_get_captain_log",
                    "Get the current session log for a captain. Supports pagination.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            captainId = new { type = "string", description = "Captain ID (cpt_ prefix)" },
                            lines = new { type = "integer", description = "Number of lines to return (default 100)" },
                            offset = new { type = "integer", description = "Line offset to start from (default 0)" }
                        },
                        required = new[] { "captainId" }
                    },
                    async (args) =>
                    {
                        CaptainLogArgs request = JsonSerializer.Deserialize<CaptainLogArgs>(args!.Value, _JsonOptions)!;
                        string captainId = request.CaptainId;
                        Captain? captain = await database.Captains.ReadAsync(captainId).ConfigureAwait(false);
                        if (captain == null) return (object)new { Error = "Captain not found" };

                        string pointerPath = Path.Combine(settings.LogDirectory, "captains", captainId + ".current");
                        string? logPath = null;

                        if (File.Exists(pointerPath))
                        {
                            string target = (await McpToolHelpers.ReadTextFileSafeAsync(pointerPath).ConfigureAwait(false)).Trim();
                            if (File.Exists(target))
                                logPath = target;
                        }

                        if (logPath == null)
                            return (object)new { CaptainId = captainId, Log = "", Lines = 0, TotalLines = 0 };

                        string[] allLines = await McpToolHelpers.ReadLogFileSafeAsync(logPath).ConfigureAwait(false);
                        int totalLines = allLines.Length;

                        int offset = Math.Max(0, request.Offset ?? 0);
                        int lineCount = Math.Max(1, request.Lines ?? 100);

                        string[] slice = allLines.Skip(offset).Take(lineCount).ToArray();
                        string log = Armada.Core.Services.RuntimeLogFormatter.RedactSecrets(String.Join("\n", slice));
                        return (object)new { CaptainId = captainId, Log = log, Lines = slice.Length, TotalLines = totalLines };
                    });
            }
        }

        /// <summary>
        /// Resolves the requested bench expiry. An explicit UntilUtc wins; otherwise a positive
        /// DurationMinutes is applied from now. Null lets the quarantine service fall back to its
        /// configured default backoff.
        /// </summary>
        private static DateTime? ResolveBenchExpiry(CaptainBenchArgs request)
        {
            // The service normalizes the expiry (a value without a zone is UTC) and rejects one that is not in the future.
            if (request.UntilUtc.HasValue)
                return request.UntilUtc.Value;

            if (request.DurationMinutes.HasValue)
                return DateTime.UtcNow.AddMinutes(request.DurationMinutes.Value);

            return null;
        }

        private static string? ApplyTierArguments(Captain captain, string? tier, int? preferenceRank)
        {
            if (tier != null)
            {
                if (String.IsNullOrWhiteSpace(tier))
                {
                    captain.Tier = null;
                }
                else if (Enum.TryParse<CaptainTierEnum>(tier.Trim(), true, out CaptainTierEnum parsed) && Enum.IsDefined(parsed))
                {
                    captain.Tier = parsed;
                }
                else
                {
                    return "tier must be Economy, Standard, or Premium";
                }
            }
            if (preferenceRank.HasValue) captain.PreferenceRank = preferenceRank.Value;
            return null;
        }

        private static object CreateToolErrorResponse(string error)
        {
            return new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = error
                    }
                },
                isError = true
            };
        }

        /// <summary>
        /// Returns a copy of the captain with the per-captain provider credential masked, so the
        /// raw key never crosses the MCP operator surface. The last four characters are preserved so
        /// an operator can still tell which key a captain carries. The REST surface keeps the raw
        /// value because the dashboard edit form must prefill it.
        /// </summary>
        /// <param name="captain">Captain to mask.</param>
        /// <returns>Captain copy with a masked <see cref="Captain.ApiKey"/>.</returns>
        private static Captain MaskCaptain(Captain captain)
        {
            return new Captain
            {
                Id = captain.Id,
                TenantId = captain.TenantId,
                UserId = captain.UserId,
                Name = captain.Name,
                Runtime = captain.Runtime,
                Model = captain.Model,
                ModelEndpointId = captain.ModelEndpointId,
                ApiKey = MaskSecret(captain.ApiKey),
                ApiBaseUrl = captain.ApiBaseUrl,
                SystemInstructions = captain.SystemInstructions,
                AllowedPersonas = captain.AllowedPersonas,
                PreferredPersona = captain.PreferredPersona,
                RuntimeOptionsJson = captain.RuntimeOptionsJson,
                Tier = captain.Tier,
                PreferenceRank = captain.PreferenceRank,
                State = captain.State,
                CurrentMissionId = captain.CurrentMissionId,
                CurrentDockId = captain.CurrentDockId,
                ProcessId = captain.ProcessId,
                RecoveryAttempts = captain.RecoveryAttempts,
                LastHeartbeatUtc = captain.LastHeartbeatUtc,
                LastProcessAliveUtc = captain.LastProcessAliveUtc,
                QuarantineUntilUtc = captain.QuarantineUntilUtc,
                QuarantineReason = captain.QuarantineReason,
                DefaultPlaybooks = captain.DefaultPlaybooks,
                CreatedUtc = captain.CreatedUtc,
                LastUpdateUtc = captain.LastUpdateUtc
            };
        }

        /// <summary>
        /// Masks a credential string, keeping only a suffix for identification.
        /// </summary>
        /// <param name="secret">Credential string, or null.</param>
        /// <returns>Masked string, or null when the input is null or blank.</returns>
        private static string? MaskSecret(string? secret)
        {
            if (String.IsNullOrWhiteSpace(secret))
                return null;

            if (secret.Length <= 8)
                return "****";

            return "****" + secret.Substring(secret.Length - 4);
        }

        private static object DefaultPlaybooksSchema()
        {
            return new
            {
                type = "array",
                description = "Default playbooks merged for this captain. Pass an empty array to clear them.",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        playbookId = new { type = "string" },
                        deliveryMode = new { type = "string", description = "InlineFullContent, InstructionWithReference, or AttachIntoWorktree" }
                    },
                    required = new[] { "playbookId", "deliveryMode" }
                }
            };
        }

        private static string? SerializeDefaultPlaybooks(List<SelectedPlaybook> playbooks)
        {
            return playbooks.Count == 0 ? null : JsonSerializer.Serialize(playbooks, _JsonOptions);
        }

        /// <summary>
        /// Apply create-time reasoningEffort + Mux options into the captain row.
        /// Mux fields are honored only when the runtime is Mux; reasoningEffort applies
        /// to any runtime whose validator accepts it.
        /// </summary>
        private static void ApplyCaptainOptions(Captain captain, CaptainCreateArgs request)
        {
            CaptainOptions options = new CaptainOptions
            {
                ReasoningEffort = request.ReasoningEffort
            };

            if (captain.Runtime == AgentRuntimeEnum.Mux)
            {
                options.ConfigDirectory = request.MuxConfigDirectory;
                options.Endpoint = request.MuxEndpoint;
                options.BaseUrl = request.MuxBaseUrl;
                options.AdapterType = request.MuxAdapterType;
                options.Temperature = request.MuxTemperature;
                options.MaxTokens = request.MuxMaxTokens;
                options.SystemPromptPath = request.MuxSystemPromptPath;
                options.ApprovalPolicy = request.MuxApprovalPolicy;
            }

            captain.RuntimeOptionsJson = HasAnyOptions(options)
                ? CaptainRuntimeOptions.Serialize(options)
                : null;
        }

        /// <summary>
        /// Apply update-time reasoningEffort + Mux options. Preserves existing
        /// non-overwritten keys; null on a request field means "leave unchanged",
        /// empty-string means "clear".
        /// </summary>
        private static void ApplyCaptainOptions(Captain captain, CaptainUpdateArgs request, Captain? existingCaptain)
        {
            CaptainOptions options = GetExistingCaptainOptions(existingCaptain);

            // Reasoning effort: null leaves unchanged; empty string clears.
            if (request.ReasoningEffort != null)
            {
                options.ReasoningEffort = String.IsNullOrEmpty(request.ReasoningEffort)
                    ? null
                    : request.ReasoningEffort;
            }

            // Mux fields apply only when the captain's current runtime is Mux.
            // Switching away from Mux clears Mux fields but preserves reasoningEffort.
            if (captain.Runtime == AgentRuntimeEnum.Mux)
            {
                if (request.MuxConfigDirectory != null) options.ConfigDirectory = EmptyAsNull(request.MuxConfigDirectory);
                if (request.MuxEndpoint != null) options.Endpoint = EmptyAsNull(request.MuxEndpoint);
                if (request.MuxBaseUrl != null) options.BaseUrl = EmptyAsNull(request.MuxBaseUrl);
                if (request.MuxAdapterType != null) options.AdapterType = EmptyAsNull(request.MuxAdapterType);
                if (request.MuxTemperature.HasValue) options.Temperature = request.MuxTemperature;
                if (request.MuxMaxTokens.HasValue) options.MaxTokens = request.MuxMaxTokens;
                if (request.MuxSystemPromptPath != null) options.SystemPromptPath = EmptyAsNull(request.MuxSystemPromptPath);
                if (request.MuxApprovalPolicy != null) options.ApprovalPolicy = EmptyAsNull(request.MuxApprovalPolicy);
            }
            else
            {
                options.ConfigDirectory = null;
                options.Endpoint = null;
                options.BaseUrl = null;
                options.AdapterType = null;
                options.Temperature = null;
                options.MaxTokens = null;
                options.SystemPromptPath = null;
                options.ApprovalPolicy = null;
            }

            captain.RuntimeOptionsJson = HasAnyOptions(options)
                ? CaptainRuntimeOptions.Serialize(options)
                : null;
        }

        private static CaptainOptions GetExistingCaptainOptions(Captain? captain)
        {
            if (captain == null || String.IsNullOrWhiteSpace(captain.RuntimeOptionsJson))
                return new CaptainOptions();

            return CaptainRuntimeOptions.GetCaptainOptions(captain) ?? new CaptainOptions();
        }

        private static string? EmptyAsNull(string value)
        {
            return value.Length == 0 ? null : value;
        }

        private static bool HasAnyOptions(CaptainOptions options)
        {
            return options.ReasoningEffort != null
                || options.ConfigDirectory != null
                || options.Endpoint != null
                || options.BaseUrl != null
                || options.AdapterType != null
                || options.Temperature.HasValue
                || options.MaxTokens.HasValue
                || options.SystemPromptPath != null
                || options.ApprovalPolicy != null;
        }

        /// <summary>
        /// Snapshot the captain's existing RuntimeOptionsJson into a temporary captain
        /// instance so subsequent mutation of <paramref name="captain"/> doesn't
        /// race the options-merge step.
        /// </summary>
        private static Captain CloneForOptionsBaseline(Captain captain)
        {
            return new Captain
            {
                Id = captain.Id,
                Runtime = captain.Runtime,
                RuntimeOptionsJson = captain.RuntimeOptionsJson,
                Model = captain.Model,
                ApiKey = captain.ApiKey,
                ApiBaseUrl = captain.ApiBaseUrl
            };
        }
    }
}
