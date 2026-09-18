namespace Armada.Server.Routes
{
    using System.Text.Json;
    using WatsonWebserver;
    using WatsonWebserver.Core;
    using WatsonWebserver.Core.OpenApi;
    using Armada.Server;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Administrator routes for the typed-decision system: the effective and stored modes, per-decision modes and
    /// thresholds, and the provider key file. The key is accepted, written, and removed here and never returned.
    /// </summary>
    public class TypedDecisionRoutes
    {
        #region Private-Members

        private const string _Header = "[TypedDecisionRoutes] ";
        private readonly ArmadaSettings _Settings;
        private readonly TypedDecisionKeyStore _Keys;
        private readonly JsonSerializerOptions _JsonOptions;
        private readonly LoggingModule _Logging;
        private readonly Func<Task> _Save;
        private readonly SemaphoreSlim _Lock = new SemaphoreSlim(1, 1);

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create the routes.</summary>
        /// <param name="settings">Live settings.</param>
        /// <param name="keys">The key store.</param>
        /// <param name="jsonOptions">JSON options.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="save">Saves settings through the normal settings save path.</param>
        public TypedDecisionRoutes(ArmadaSettings settings, TypedDecisionKeyStore keys, JsonSerializerOptions jsonOptions, LoggingModule logging, Func<Task> save)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Keys = keys ?? throw new ArgumentNullException(nameof(keys));
            _JsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Save = save ?? throw new ArgumentNullException(nameof(save));
        }

        #endregion

        #region Public-Methods

        /// <summary>Only a caller with settings write permission (a global administrator) may use these routes.</summary>
        /// <param name="ctx">Authentication context.</param>
        /// <param name="authz">Authorization service.</param>
        /// <returns>True when permitted.</returns>
        public static bool IsPermitted(AuthContext ctx, IAuthorizationService authz)
        {
            if (ctx == null || authz == null) return false;
            return authz.IsAuthorized(ctx, "PUT", "/api/v1/settings");
        }

        /// <summary>Register the routes.</summary>
        /// <param name="app">Webserver.</param>
        /// <param name="authenticate">Authentication delegate.</param>
        /// <param name="authz">Authorization service.</param>
        public void Register(
            Webserver app,
            Func<WatsonWebserver.Core.HttpContextBase, Task<AuthContext>> authenticate,
            IAuthorizationService authz)
        {
            app.Get("/api/v1/typed-decisions", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!IsPermitted(ctx, authz)) return Refuse(req, ctx);
                return TypedDecisionStatusBuilder.Build(_Settings.TypedDecisions, _Keys);
            },
            api => api
                .WithTag("Settings")
                .WithSummary("Get typed-decision modes and key presence")
                .WithDescription("Returns the effective global mode (Off with reason typed_decisions_no_key when no key resolves), the stored global mode, whether a key is present and its source (env or file), and every decision's mode, threshold, and description. Never returns the key.")
                .WithResponse(200, OpenApiJson.For<TypedDecisionStatus>("Typed-decision status"))
                .WithSecurity("ApiKey"));

            app.Put<TypedDecisionsUpdateRequest>("/api/v1/typed-decisions", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!IsPermitted(ctx, authz)) return Refuse(req, ctx);
                TypedDecisionsUpdateRequest body;
                try
                {
                    body = JsonSerializer.Deserialize<TypedDecisionsUpdateRequest>(req.Http.Request.DataAsString, _JsonOptions)
                        ?? throw new ArgumentException("A request body is required.");
                    Validate(body);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is JsonException)
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = ex is JsonException ? "The request body is not valid JSON or names an unknown mode." : ex.Message };
                }

                await _Lock.WaitAsync().ConfigureAwait(false);
                try
                {
                    TypedDecisionSettings live = _Settings.TypedDecisions;
                    TypedDecisionModeEnum previousMode = live.Mode;
                    Dictionary<string, TypedDecisionRuleSettings> previousDecisions = Snapshot(live.Decisions);
                    if (body.Mode.HasValue) live.Mode = body.Mode.Value;
                    if (body.Decisions != null)
                    {
                        foreach (KeyValuePair<string, TypedDecisionRuleUpdate> pair in body.Decisions)
                        {
                            if (!live.Decisions.TryGetValue(pair.Key, out TypedDecisionRuleSettings? rule) || rule == null)
                            {
                                rule = new TypedDecisionRuleSettings();
                                live.Decisions[pair.Key] = rule;
                            }
                            if (pair.Value.Mode.HasValue) rule.Mode = pair.Value.Mode.Value;
                            if (pair.Value.GateThreshold.HasValue) rule.GateThreshold = pair.Value.GateThreshold.Value;
                        }
                    }
                    try
                    {
                        await _Save().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        live.Mode = previousMode;
                        live.Decisions = previousDecisions;
                        req.Http.Response.StatusCode = 500;
                        return new ApiErrorResponse { Error = ApiResultEnum.InternalError, Message = "typed_decisions_save_failed: settings could not be saved; nothing changed." };
                    }
                    _Logging.Info(_Header + "typed-decision modes updated via API: mode=" + live.Mode
                        + (body.Decisions != null ? " decisions=" + String.Join(",", body.Decisions.Keys) : String.Empty));
                }
                finally { _Lock.Release(); }
                return TypedDecisionStatusBuilder.Build(_Settings.TypedDecisions, _Keys);
            },
            api => api
                .WithTag("Settings")
                .WithSummary("Update typed-decision modes and thresholds")
                .WithDescription("Updates the global mode and any shipped decision's mode (Off, Shadow, Gate) or gateThreshold (0 to 1), then saves settings. Unknown decisions, modes, or thresholds are refused with 400.")
                .WithRequestBody(OpenApiJson.BodyFor<TypedDecisionsUpdateRequest>("Mode and threshold changes", true))
                .WithResponse(200, OpenApiJson.For<TypedDecisionStatus>("Typed-decision status"))
                .WithSecurity("ApiKey"));

            app.Post("/api/v1/typed-decisions/custom/install-seeds", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!IsPermitted(ctx, authz)) return Refuse(req, ctx);
                await _Lock.WaitAsync().ConfigureAwait(false);
                try
                {
                    TypedDecisionSettings live = _Settings.TypedDecisions;
                    Dictionary<string, CustomTypedDecisionSettings> previous = CloneCustom(live.Custom);
                    int added = 0;
                    foreach (KeyValuePair<string, CustomTypedDecisionSettings> seed in CustomDecisionSeeds.Build())
                    {
                        if (!live.Custom.ContainsKey(seed.Key)) { live.Custom[seed.Key] = seed.Value; added++; }
                    }
                    if (!await TrySaveAsync(live, previous).ConfigureAwait(false))
                    {
                        req.Http.Response.StatusCode = 500;
                        return new ApiErrorResponse { Error = ApiResultEnum.InternalError, Message = "typed_decisions_save_failed: nothing changed." };
                    }
                    _Logging.Info(_Header + "installed " + added + " seed custom decisions via API");
                }
                finally { _Lock.Release(); }
                return TypedDecisionStatusBuilder.Build(_Settings.TypedDecisions, _Keys);
            },
            api => api
                .WithTag("Settings")
                .WithSummary("Install the example custom decisions")
                .WithDescription("Adds the built-in example custom decisions (source_fidelity, safety_step_present, citation_resolves) that are not already present. They ship Off and unbound, so nothing runs until you turn them on. Existing decisions of the same name are left unchanged.")
                .WithResponse(200, OpenApiJson.For<TypedDecisionStatus>("Typed-decision status"))
                .WithSecurity("ApiKey"));

            app.Put("/api/v1/typed-decisions/custom/{name}", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!IsPermitted(ctx, authz)) return Refuse(req, ctx);
                string name = req.Parameters["name"] ?? String.Empty;
                CustomTypedDecisionSettings? body;
                try
                {
                    body = JsonSerializer.Deserialize<CustomTypedDecisionSettings>(req.Http.Request.DataAsString, _JsonOptions)
                        ?? throw new ArgumentException("A request body is required.");
                    ValidateCustom(name, body);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is JsonException)
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = ex is JsonException ? "The request body is not valid JSON." : ex.Message };
                }
                await _Lock.WaitAsync().ConfigureAwait(false);
                try
                {
                    TypedDecisionSettings live = _Settings.TypedDecisions;
                    Dictionary<string, CustomTypedDecisionSettings> previous = CloneCustom(live.Custom);
                    live.Custom[name] = body;
                    if (!await TrySaveAsync(live, previous).ConfigureAwait(false))
                    {
                        req.Http.Response.StatusCode = 500;
                        return new ApiErrorResponse { Error = ApiResultEnum.InternalError, Message = "typed_decisions_save_failed: nothing changed." };
                    }
                    _Logging.Info(_Header + "custom decision '" + name + "' saved via API");
                }
                finally { _Lock.Release(); }
                return TypedDecisionStatusBuilder.Build(_Settings.TypedDecisions, _Keys);
            },
            api => api
                .WithTag("Settings")
                .WithSummary("Create or replace a custom typed decision")
                .WithDescription("Upserts a user-defined custom decision by name. The body carries its mode, gateThreshold, description, surface, binding, stateFields, and questions. A name that collides with a shipped decision, an invalid mode/threshold/surface/binding, or a malformed question is refused with 400. Advisory only: a custom decision never lands, dispatches, or approves.")
                .WithResponse(200, OpenApiJson.For<TypedDecisionStatus>("Typed-decision status"))
                .WithSecurity("ApiKey"));

            app.Delete("/api/v1/typed-decisions/custom/{name}", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!IsPermitted(ctx, authz)) return Refuse(req, ctx);
                string name = req.Parameters["name"] ?? String.Empty;
                await _Lock.WaitAsync().ConfigureAwait(false);
                try
                {
                    TypedDecisionSettings live = _Settings.TypedDecisions;
                    if (!live.Custom.ContainsKey(name))
                    {
                        req.Http.Response.StatusCode = 404;
                        return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = "typed_decisions_custom_not_found: " + name };
                    }
                    Dictionary<string, CustomTypedDecisionSettings> previous = CloneCustom(live.Custom);
                    live.Custom.Remove(name);
                    if (!await TrySaveAsync(live, previous).ConfigureAwait(false))
                    {
                        req.Http.Response.StatusCode = 500;
                        return new ApiErrorResponse { Error = ApiResultEnum.InternalError, Message = "typed_decisions_save_failed: nothing changed." };
                    }
                    _Logging.Info(_Header + "custom decision '" + name + "' deleted via API");
                }
                finally { _Lock.Release(); }
                return TypedDecisionStatusBuilder.Build(_Settings.TypedDecisions, _Keys);
            },
            api => api
                .WithTag("Settings")
                .WithSummary("Delete a custom typed decision")
                .WithDescription("Removes a user-defined custom decision. Deleting one does not resurrect it from the seeds.")
                .WithResponse(200, OpenApiJson.For<TypedDecisionStatus>("Typed-decision status"))
                .WithSecurity("ApiKey"));

            app.Put<TypedDecisionKeyRequest>("/api/v1/typed-decisions/key", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!IsPermitted(ctx, authz)) return Refuse(req, ctx);
                TypedDecisionKeyRequest? body;
                try
                {
                    body = JsonSerializer.Deserialize<TypedDecisionKeyRequest>(req.Http.Request.DataAsString, _JsonOptions);
                }
                catch (JsonException)
                {
                    // The body holds a key, so the parser's message, which can quote it, is not passed on.
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "The request body is not valid JSON." };
                }
                try
                {
                    await _Keys.WriteKeyAsync(body?.ApiKey).ConfigureAwait(false);
                }
                catch (ArgumentException ex)
                {
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "typed_decisions_key_invalid: " + ex.Message };
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    req.Http.Response.StatusCode = 500;
                    return new ApiErrorResponse { Error = ApiResultEnum.InternalError, Message = "typed_decisions_key_write_failed: the key file could not be written." };
                }
                _Logging.Info(_Header + "typed-decision key file written via API");
                req.Http.Response.StatusCode = 204;
                return null;
            },
            api => api
                .WithTag("Settings")
                .WithSummary("Store the typed-decision provider key")
                .WithDescription("Writes the key to <data directory>/secrets/typesafe-api-key (folder 0700, file 0600). Takes effect without a restart. The key is never logged, recorded, stored in settings, or returned. The environment variable named by typedDecisions.apiKeyEnv still wins when set.")
                .WithRequestBody(OpenApiJson.BodyFor<TypedDecisionKeyRequest>("Provider key", true))
                .WithResponse(204, OpenApiResponseMetadata.NoContent())
                .WithSecurity("ApiKey"));

            app.Delete("/api/v1/typed-decisions/key", async (ApiRequest req) =>
            {
                AuthContext ctx = await authenticate(req.Http).ConfigureAwait(false);
                if (!IsPermitted(ctx, authz)) return Refuse(req, ctx);
                bool removed;
                try
                {
                    removed = _Keys.DeleteKeyFile();
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    req.Http.Response.StatusCode = 500;
                    return new ApiErrorResponse { Error = ApiResultEnum.InternalError, Message = "typed_decisions_key_delete_failed: the key file could not be removed." };
                }
                _Logging.Info(_Header + "typed-decision key file " + (removed ? "removed" : "was absent") + " via API");
                TypedDecisionStatus status = TypedDecisionStatusBuilder.Build(_Settings.TypedDecisions, _Keys);
                return new
                {
                    FileRemoved = removed,
                    EnvironmentSuppliesKey = _Keys.EnvironmentSuppliesKey(_Settings.TypedDecisions),
                    status.KeyPresent,
                    status.KeySource,
                    status.EffectiveMode,
                    status.EffectiveReason
                };
            },
            api => api
                .WithTag("Settings")
                .WithSummary("Remove the typed-decision key file")
                .WithDescription("Deletes the key file. When the environment variable still supplies a key, environmentSuppliesKey is true and the effective mode is unchanged.")
                .WithSecurity("ApiKey"));
        }

        #endregion

        #region Private-Methods

        private static void Validate(TypedDecisionsUpdateRequest body)
        {
            if (body.Mode.HasValue && !Enum.IsDefined(typeof(TypedDecisionModeEnum), body.Mode.Value))
                throw new ArgumentException("typed_decisions_mode_invalid: mode must be Off, Shadow, or Gate.");
            if (body.Decisions == null) return;
            foreach (KeyValuePair<string, TypedDecisionRuleUpdate> pair in body.Decisions)
            {
                if (String.IsNullOrWhiteSpace(pair.Key) || !TypedDecisionSettings.ShippedDecisionNames.Contains(pair.Key))
                    throw new ArgumentException("typed_decisions_unknown_decision: " + (pair.Key ?? String.Empty) + " is not a shipped decision.");
                if (pair.Value == null) throw new ArgumentException("typed_decisions_update_invalid: " + pair.Key + " has no changes.");
                if (pair.Value.Mode.HasValue && !Enum.IsDefined(typeof(TypedDecisionModeEnum), pair.Value.Mode.Value))
                    throw new ArgumentException("typed_decisions_mode_invalid: " + pair.Key + " mode must be Off, Shadow, or Gate.");
                if (pair.Value.GateThreshold.HasValue && (!Double.IsFinite(pair.Value.GateThreshold.Value) || pair.Value.GateThreshold.Value < 0 || pair.Value.GateThreshold.Value > 1))
                    throw new ArgumentException("typed_decisions_threshold_invalid: " + pair.Key + " gateThreshold must be between 0 and 1.");
            }
        }

        private async Task<bool> TrySaveAsync(TypedDecisionSettings live, Dictionary<string, CustomTypedDecisionSettings> previousCustom)
        {
            try
            {
                await _Save().ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                live.Custom = previousCustom;
                return false;
            }
        }

        private static Dictionary<string, CustomTypedDecisionSettings> CloneCustom(Dictionary<string, CustomTypedDecisionSettings> custom)
        {
            Dictionary<string, CustomTypedDecisionSettings> copy = new Dictionary<string, CustomTypedDecisionSettings>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, CustomTypedDecisionSettings> pair in custom)
                if (pair.Value != null) copy[pair.Key] = pair.Value.Clone();
            return copy;
        }

        /// <summary>
        /// Refuse a custom decision body the route must not store, with a coded
        /// <see cref="ArgumentException"/> the route returns as 400.
        /// </summary>
        /// <param name="name">The decision name.</param>
        /// <param name="body">The decision body.</param>
        internal static void ValidateCustom(string name, CustomTypedDecisionSettings body)
        {
            if (String.IsNullOrWhiteSpace(name))
                throw new ArgumentException("typed_decisions_custom_name_required: a decision name is required.");
            if (TypedDecisionSettings.ShippedDecisionNames.Contains(name))
                throw new ArgumentException("typed_decisions_custom_name_collision: " + name + " is a shipped decision; choose another name.");
            if (!Enum.IsDefined(typeof(TypedDecisionModeEnum), body.Mode))
                throw new ArgumentException("typed_decisions_mode_invalid: mode must be Off, Shadow, or Gate.");
            if (!Double.IsFinite(body.GateThreshold) || body.GateThreshold < 0 || body.GateThreshold > 1)
                throw new ArgumentException("typed_decisions_threshold_invalid: gateThreshold must be between 0 and 1.");
            if (!Enum.IsDefined(typeof(Armada.Core.Enums.CustomDecisionSurfaceEnum), body.Surface))
                throw new ArgumentException("typed_decisions_surface_invalid: surface must be CaptainTool or MissionDiff.");
            if (!Enum.IsDefined(typeof(Armada.Core.Enums.CustomDecisionSeamEnum), body.Binding))
                throw new ArgumentException("typed_decisions_binding_invalid: binding must be None or MissionDiffFlag.");
            // The only bound action wired today acts on the MissionDiff surface, so a CaptainTool
            // decision cannot claim it. This keeps the enum honest: every offered binding is enforced.
            if (body.Binding == Armada.Core.Enums.CustomDecisionSeamEnum.MissionDiffFlag
                && body.Surface != Armada.Core.Enums.CustomDecisionSurfaceEnum.MissionDiff)
                throw new ArgumentException("typed_decisions_binding_surface_mismatch: MissionDiffFlag requires the MissionDiff surface.");
            if (body.Vessels != null && body.Vessels.Count > 0
                && body.Surface != Armada.Core.Enums.CustomDecisionSurfaceEnum.MissionDiff)
                throw new ArgumentException("typed_decisions_custom_vessels_surface: a vessel scope applies only to the MissionDiff surface.");
            if (body.Questions == null || body.Questions.Count == 0)
                throw new ArgumentException("typed_decisions_custom_no_questions: a custom decision needs at least one question.");
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (CustomTypedQuestionSettings question in body.Questions)
            {
                if (question == null || String.IsNullOrWhiteSpace(question.Id))
                    throw new ArgumentException("typed_decisions_custom_question_id: every question needs a non-empty id.");
                if (!ids.Add(question.Id))
                    throw new ArgumentException("typed_decisions_custom_question_duplicate: question id '" + question.Id + "' is repeated.");
                if (String.IsNullOrWhiteSpace(question.Instructions))
                    throw new ArgumentException("typed_decisions_custom_question_instructions: question '" + question.Id + "' needs instructions.");
                string kind = (question.Type ?? "noul").Trim().ToLowerInvariant();
                if (kind != "choice" && kind != "score" && kind != "noul")
                    throw new ArgumentException("typed_decisions_custom_question_type: question '" + question.Id + "' type must be choice, score, or noul.");
                if (kind == "choice" && (question.Options == null || question.Options.Count < 2))
                    throw new ArgumentException("typed_decisions_custom_choice_options: choice question '" + question.Id + "' needs at least two options.");
                if (question.FlagOptions != null && question.FlagOptions.Count > 0)
                {
                    if (kind != "choice")
                        throw new ArgumentException("typed_decisions_custom_flag_options_kind: question '" + question.Id + "' names flagOptions, which only a choice question takes.");
                    foreach (string option in question.FlagOptions)
                        if (question.Options == null || !question.Options.ContainsKey(option ?? String.Empty))
                            throw new ArgumentException("typed_decisions_custom_flag_options_unknown: flag option '" + option + "' is not an option of question '" + question.Id + "'.");
                    if (question.FlagOptions.Count >= question.Options!.Count)
                        throw new ArgumentException("typed_decisions_custom_flag_options_all: question '" + question.Id + "' names every option as a finding, so it would flag every answer.");
                }
                if (kind == "score" && (question.Levels == null || question.Levels.Count < 2))
                    throw new ArgumentException("typed_decisions_custom_score_levels: score question '" + question.Id + "' needs at least two levels.");
            }
        }

        private static Dictionary<string, TypedDecisionRuleSettings> Snapshot(Dictionary<string, TypedDecisionRuleSettings> decisions)
        {
            Dictionary<string, TypedDecisionRuleSettings> copy = new Dictionary<string, TypedDecisionRuleSettings>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, TypedDecisionRuleSettings> pair in decisions)
                copy[pair.Key] = new TypedDecisionRuleSettings { Mode = pair.Value.Mode, GateThreshold = pair.Value.GateThreshold };
            return copy;
        }

        private static object Refuse(ApiRequest req, AuthContext ctx)
        {
            req.Http.Response.StatusCode = ctx.IsAuthenticated ? 403 : 401;
            return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = ctx.IsAuthenticated ? "Administrator access required" : "Authentication required" };
        }

        #endregion
    }
}
